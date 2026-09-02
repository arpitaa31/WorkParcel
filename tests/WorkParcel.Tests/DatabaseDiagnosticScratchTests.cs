using Microsoft.Data.Sqlite;
using WorkParcel_App.Services;
using Xunit;

namespace WorkParcel.Tests;

public sealed class DatabaseDiagnosticScratchTests
{
    [Fact]
    public async Task InspectConfiguredDatabase()
    {
        var root = Environment.GetEnvironmentVariable("WORKPARCEL_DIAGNOSTIC_ROOT");
        if (string.IsNullOrWhiteSpace(root)) return;

        var paths = new AppDataPaths(root);
        Console.WriteLine($"ROOT={paths.RootDirectory}");
        Console.WriteLine($"DATABASE={paths.DatabasePath}");
        await using (var connection = new SqliteConnection($"Data Source={paths.DatabasePath};Mode=ReadOnly"))
        {
            await connection.OpenAsync();
            await using (var versionCommand = connection.CreateCommand())
            {
                versionCommand.CommandText = "SELECT Key, Value FROM SchemaInfo ORDER BY Key;";
                await using var versionReader = await versionCommand.ExecuteReaderAsync();
                while (await versionReader.ReadAsync()) Console.WriteLine($"SETTING {versionReader.GetString(0)}={versionReader.GetString(1)}");
            }
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT type, name, sql FROM sqlite_master WHERE sql IS NOT NULL ORDER BY type, name;";
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync()) Console.WriteLine($"SCHEMA {reader.GetString(0)} {reader.GetString(1)}: {reader.GetString(2)}");
        }

        try
        {
            await new DatabaseInitializer(new SqliteConnectionFactory(paths), new AppLogger(paths)).InitializeAsync();
            Console.WriteLine("INITIALIZATION SUCCEEDED");
        }
        catch (Exception exception)
        {
            for (var current = exception; current is not null; current = current.InnerException)
                Console.WriteLine($"EXCEPTION {current.GetType().FullName} HResult=0x{current.HResult:X8}: {current.Message}\n{current.StackTrace}");
        }
    }
}
