namespace WorkParcel_App.Services;

public sealed class AppDataPaths
{
    public AppDataPaths(string? root = null)
    {
        RootDirectory = root ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WorkParcel");
        DataDirectory = Path.Combine(RootDirectory, "Data");
        DatabasePath = Path.Combine(DataDirectory, "workparcel.db");
        LogDirectory = Path.Combine(RootDirectory, "Logs");
        LogPath = Path.Combine(LogDirectory, "workparcel.log");
    }

    public string RootDirectory { get; }
    public string DataDirectory { get; }
    public string DatabasePath { get; }
    public string LogDirectory { get; }
    public string LogPath { get; }

    public void EnsureDirectories()
    {
        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(LogDirectory);
    }
}
