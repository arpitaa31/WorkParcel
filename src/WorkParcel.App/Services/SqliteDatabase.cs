using Microsoft.Data.Sqlite;

namespace WorkParcel_App.Services;

public sealed class DatabaseStartupException : Exception
{
    public DatabaseStartupException(string message, Exception inner) : base(message, inner) { }
}

public sealed class SqliteConnectionFactory
{
    private readonly AppDataPaths _paths;
    public SqliteConnectionFactory(AppDataPaths paths) => _paths = paths;
    public string DatabasePath => _paths.DatabasePath;

    public async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken = default)
    {
        _paths.EnsureDirectories();
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _paths.DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        }.ToString());
        try
        {
            await connection.OpenAsync(cancellationToken);
            await using var foreignKeys = connection.CreateCommand();
            foreignKeys.CommandText = "PRAGMA foreign_keys = ON;";
            await foreignKeys.ExecuteNonQueryAsync(cancellationToken);
            await using var busyTimeout = connection.CreateCommand();
            busyTimeout.CommandText = "PRAGMA busy_timeout = 5000;";
            await busyTimeout.ExecuteNonQueryAsync(cancellationToken);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }
}

public sealed class DatabaseInitializer
{
    public const int CurrentSchemaVersion = 2;
    private readonly SqliteConnectionFactory _factory;
    private readonly AppLogger _logger;

    public DatabaseInitializer(SqliteConnectionFactory factory, AppLogger logger)
    {
        _factory = factory;
        _logger = logger;
    }

    public DateTime? LastSuccessfulInitializationUtc { get; private set; }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await using var connection = await _factory.OpenAsync(cancellationToken);
            await using var transaction = connection.BeginTransaction();
            await ExecuteAsync(connection, transaction, "CREATE TABLE IF NOT EXISTS SchemaInfo (Key TEXT PRIMARY KEY, Value TEXT NOT NULL);", cancellationToken);
            var version = await ReadVersionAsync(connection, transaction, cancellationToken);
            if (version > CurrentSchemaVersion)
                throw new InvalidOperationException($"Database schema {version} is newer than this app supports.");

            if (version < 1)
            {
                await CreateSchemaV1Async(connection, transaction, cancellationToken);
                await SetVersionAsync(connection, transaction, 1, cancellationToken);
                version = 1;
            }

            if (version < 2)
            {
                await MigrateToV2Async(connection, transaction, cancellationToken);
                await SetVersionAsync(connection, transaction, 2, cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
            LastSuccessfulInitializationUtc = DateTime.UtcNow;
            _logger.Info("Database initialized successfully.");
        }
        catch (Exception exception) when (exception is SqliteException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _logger.Error("Database initialization failed", exception);
            throw new DatabaseStartupException("WorkParcel could not open its local data. Your existing database was not deleted.", exception);
        }
    }

    private static async Task<int> ReadVersionAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT Value FROM SchemaInfo WHERE Key = 'SchemaVersion' LIMIT 1;";
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is null ? 0 : int.Parse(value.ToString()!);
    }

    private static Task SetVersionAsync(SqliteConnection connection, SqliteTransaction transaction, int version, CancellationToken cancellationToken) =>
        ExecuteAsync(connection, transaction, "INSERT INTO SchemaInfo(Key, Value) VALUES ('SchemaVersion', $version) ON CONFLICT(Key) DO UPDATE SET Value = excluded.Value;", cancellationToken, ("$version", version));

    private static async Task CreateSchemaV1Async(SqliteConnection connection, SqliteTransaction transaction, CancellationToken cancellationToken)
    {
        const string sql = @"
CREATE TABLE IF NOT EXISTS Parcels (
    Id TEXT PRIMARY KEY,
    Name TEXT NOT NULL,
    Description TEXT NOT NULL DEFAULT '',
    State TEXT NOT NULL CHECK(State IN ('Open', 'Packed', 'Archived')),
    CreatedAt TEXT NOT NULL,
    UpdatedAt TEXT NOT NULL,
    LastOpenedAt TEXT NULL,
    LastPackedAt TEXT NULL,
    ArchivedAt TEXT NULL,
    PreviousState TEXT NULL
);
CREATE INDEX IF NOT EXISTS IX_Parcels_State_UpdatedAt ON Parcels(State, UpdatedAt DESC);
CREATE TABLE IF NOT EXISTS ParcelHistory (
    Id TEXT PRIMARY KEY,
    ParcelId TEXT NOT NULL,
    EventType TEXT NOT NULL,
    Summary TEXT NOT NULL,
    Timestamp TEXT NOT NULL,
    FOREIGN KEY(ParcelId) REFERENCES Parcels(Id) ON DELETE CASCADE
);
CREATE INDEX IF NOT EXISTS IX_ParcelHistory_Parcel_Timestamp ON ParcelHistory(ParcelId, Timestamp DESC);
CREATE TABLE IF NOT EXISTS TodayTasks (
    Id TEXT PRIMARY KEY,
    Text TEXT NOT NULL,
    IsCompleted INTEGER NOT NULL DEFAULT 0 CHECK(IsCompleted IN (0, 1)),
    CreatedAt TEXT NOT NULL,
    CompletedAt TEXT NULL,
    ParcelId TEXT NULL,
    SortOrder INTEGER NOT NULL DEFAULT 0,
    FOREIGN KEY(ParcelId) REFERENCES Parcels(Id) ON DELETE SET NULL
);
CREATE INDEX IF NOT EXISTS IX_TodayTasks_SortOrder ON TodayTasks(IsCompleted, SortOrder, CreatedAt);
CREATE TABLE IF NOT EXISTS Settings (Key TEXT PRIMARY KEY, Value TEXT NOT NULL);
";
        await ExecuteAsync(connection, transaction, sql, cancellationToken);
    }

    private static async Task MigrateToV2Async(SqliteConnection connection, SqliteTransaction transaction, CancellationToken cancellationToken)
    {
        var columns = await ReadColumnsAsync(connection, transaction, "Parcels", cancellationToken);
        if (columns.Contains("Goal", StringComparer.OrdinalIgnoreCase))
        {
            await ExecuteAsync(connection, transaction, "ALTER TABLE Parcels RENAME TO Parcels_Legacy;", cancellationToken);
            await CreateSchemaV1Async(connection, transaction, cancellationToken);
            const string copy = @"
INSERT INTO Parcels(Id, Name, Description, State, CreatedAt, UpdatedAt, LastOpenedAt, LastPackedAt, ArchivedAt, PreviousState)
SELECT Id, Name, COALESCE(Description, ''),
       CASE Status WHEN 4 THEN 'Archived' WHEN 1 THEN 'Packed' ELSE 'Open' END,
       CreatedAt, UpdatedAt, NULLIF(LastOpenedAt, ''), NULL, ArchivedAt,
       CASE WHEN ArchivedFromStatus = 'PACKED' THEN 'Packed' WHEN ArchivedFromStatus = 'OPEN' THEN 'Open' ELSE NULL END
FROM Parcels_Legacy;";
            await ExecuteAsync(connection, transaction, copy, cancellationToken);
        }
        else
        {
            await CreateSchemaV1Async(connection, transaction, cancellationToken);
        }
    }

    private static async Task<HashSet<string>> ReadColumnsAsync(SqliteConnection connection, SqliteTransaction transaction, string table, CancellationToken cancellationToken)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"PRAGMA table_info([{table}]);";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) result.Add(reader["name"].ToString()!);
        return result;
    }

    internal static async Task ExecuteAsync(SqliteConnection connection, SqliteTransaction? transaction, string sql, CancellationToken cancellationToken, params (string Name, object? Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}

internal static class DbValue
{
    public static string Time(DateTime value) => value.ToUniversalTime().ToString("O");
    public static string? Time(DateTime? value) => value.HasValue ? Time(value.Value) : null;
    public static DateTime Time(object value) => DateTime.Parse(value.ToString()!, null, System.Globalization.DateTimeStyles.RoundtripKind).ToLocalTime();
    public static DateTime? NullableTime(object? value) => value is null or DBNull ? null : Time(value);
    public static object Db(object? value) => value ?? DBNull.Value;
}
