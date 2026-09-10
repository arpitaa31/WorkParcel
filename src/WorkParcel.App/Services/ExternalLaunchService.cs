using System.Diagnostics;

namespace WorkParcel_App.Services;

public sealed record ExternalLaunchResult(bool Succeeded, string Target, string Message)
{
    public static ExternalLaunchResult Success(string target) => new(true, target, $"Opened {target}.");

    public static ExternalLaunchResult Failure(string target, string message) => new(false, target, message);
}

/// <summary>
/// Resolves files shipped with the running app and opens them through the
/// Windows shell. Keeping this outside the page makes missing packaged files
/// and shell failures observable in tests and in the UI.
/// </summary>
public static class InstalledResourcePathResolver
{
    public static string? FindFromAppBase(string relativePath) => Find(AppContext.BaseDirectory, relativePath);

    public static string? Find(string baseDirectory, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(baseDirectory) || string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath)) return null;

        for (var directory = new DirectoryInfo(baseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, relativePath);
            if (Directory.Exists(candidate) || File.Exists(candidate)) return candidate;
        }

        return null;
    }
}

public static class ExternalLaunchService
{
    public static ExternalLaunchResult TryOpenFolder(string? folderPath, Func<ProcessStartInfo, bool>? start = null)
    {
        if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath))
        {
            var target = string.IsNullOrWhiteSpace(folderPath) ? "the selected folder" : folderPath;
            return ExternalLaunchResult.Failure(target, "The selected folder does not exist or is unavailable.");
        }

        var info = new ProcessStartInfo
        {
            FileName = "explorer.exe",
            UseShellExecute = true
        };
        info.ArgumentList.Add(folderPath);
        return TryStart(folderPath, info, start);
    }


    private static ExternalLaunchResult TryStart(string target, ProcessStartInfo info, Func<ProcessStartInfo, bool>? start)
    {
        try
        {
            var started = (start ?? (processInfo => Process.Start(processInfo) is not null))(info);
            return started
                ? ExternalLaunchResult.Success(target)
                : ExternalLaunchResult.Failure(target, $"Windows could not open {target}.");
        }
        catch (Exception exception)
        {
            return ExternalLaunchResult.Failure(target, $"Windows could not open {target}: {exception.Message}");
        }
    }

}
