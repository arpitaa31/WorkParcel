namespace WorkParcel_App.Services;

public sealed class AppLogger
{
    private readonly AppDataPaths _paths;
    private readonly object _gate = new();
    public AppLogger(AppDataPaths paths) => _paths = paths;

    public void Info(string message) => Write("INFO", message);
    public void Error(string message, Exception? exception = null) => Write("ERROR", exception is null ? message : $"{message}: {exception.GetType().Name}: {exception.Message}");

    public static void LogTechnicalError(Exception exception)
    {
        try { new AppLogger(new AppDataPaths()).Error("Unexpected UI operation failure", exception); }
        catch { /* Logging must never take down the app. */ }
    }

    private void Write(string level, string message)
    {
        try
        {
            _paths.EnsureDirectories();
            lock (_gate) File.AppendAllText(_paths.LogPath, $"{DateTime.UtcNow:O} [{level}] {message}{Environment.NewLine}");
        }
        catch { /* Logging must never take down the app. */ }
    }
}
