using System.Text.Json;
using WorkParcel.Core.Browser;

namespace WorkParcel.BrowserHost;

internal static class Program
{
    public static async Task Main(string[] args)
    {
        try
        {
            var origin = args.FirstOrDefault(argument => argument.StartsWith("chrome-extension://", StringComparison.OrdinalIgnoreCase));
            // Chrome and Edge pass the calling extension origin as the first
            // native-host argument (and on Windows also pass
            // --parent-window=<HWND>). The browser enforces the exact
            // allowed_origins list in the registered host manifest; validate
            // the origin again in the host when it is present. The no-origin
            // path is retained only for the local framing smoke test and is
            // enabled explicitly by that diagnostic process.
            var diagnosticNoOrigin = string.Equals(Environment.GetEnvironmentVariable("WORKPARCEL_BROWSER_HOST_SMOKE"), "1", StringComparison.Ordinal);
            if (origin is null && !diagnosticNoOrigin) { HostLog.Write("Native messaging origin missing."); return; }
            if (origin is not null && !AllowedOrigins.IsAllowed(origin)) { HostLog.Write("Native messaging origin rejected."); return; }
            await using var browserIn = Console.OpenStandardInput();
            await using var browserOut = Console.OpenStandardOutput();
            await using var appPipe = await PipeConnector.ConnectAsync();
            using var cancellation = new CancellationTokenSource();
            var browserToApp = PumpAsync(browserIn, appPipe, cancellation.Token, "browser-to-app");
            var appToBrowser = PumpAsync(appPipe, browserOut, cancellation.Token, "app-to-browser");
            await Task.WhenAny(browserToApp, appToBrowser);
            cancellation.Cancel();
            try { await Task.WhenAll(browserToApp, appToBrowser); } catch { }
        }
        catch (Exception exception) { HostLog.Write(exception); }
    }

    private static async Task PumpAsync(Stream input, Stream output, CancellationToken cancellationToken, string direction)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var body = await BrowserProtocol.ReadFrameAsync(input, cancellationToken);
                if (!BrowserProtocol.TryDeserializeForRelay(body, out var message, out var validationError)) { HostLog.Write($"Frame rejected: {validationError}"); continue; }
                var encoded = BrowserProtocol.Serialize(message!);
                await BrowserProtocol.WriteFrameAsync(output, encoded, cancellationToken);
            }
        }
        catch (Exception exception) when (exception is EndOfStreamException or IOException or InvalidDataException or OperationCanceledException)
        {
            if (!cancellationToken.IsCancellationRequested) HostLog.Write($"{direction} {exception.GetType().Name}: {exception.Message}");
            throw;
        }
    }
}

internal static class AllowedOrigins
{
    public static bool IsAllowed(string? origin)
    {
        if (string.IsNullOrWhiteSpace(origin)) return false;
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "allowed-origins.json");
            if (!File.Exists(path)) return false;
            var origins = JsonSerializer.Deserialize<string[]>(File.ReadAllText(path)) ?? Array.Empty<string>();
            var normalized = origin.TrimEnd('/');
            return origins.Any(item => string.Equals(item?.TrimEnd('/'), normalized, StringComparison.OrdinalIgnoreCase));
        }
        catch { return false; }
    }
}

internal static class PipeConnector
{
    public static async Task<Stream> ConnectAsync()
    {
        var pipe = new System.IO.Pipes.NamedPipeClientStream(".", BrowserProtocol.PipeName, System.IO.Pipes.PipeDirection.InOut, System.IO.Pipes.PipeOptions.Asynchronous);
        await pipe.ConnectAsync(2500); return pipe;
    }
}

internal static class HostLog
{
    public static void Write(string message)
    {
        try
        {
            var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WorkParcel", "BrowserHost"); Directory.CreateDirectory(directory); File.AppendAllText(Path.Combine(directory, "host.log"), $"{DateTime.UtcNow:O} {message}{Environment.NewLine}");
        }
        catch { }
    }

    public static void Write(Exception exception)
    {
        try
        {
            var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WorkParcel", "BrowserHost");
            Directory.CreateDirectory(directory); File.AppendAllText(Path.Combine(directory, "host.log"), $"{DateTime.UtcNow:O} {exception.GetType().Name} 0x{exception.HResult:X8}{Environment.NewLine}");
        }
        catch { }
    }
}
