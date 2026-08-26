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
    public const int CurrentSchemaVersion = 5;
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
                version = 2;
            }

            if (version < 3)
            {
                await MigrateToV3Async(connection, transaction, cancellationToken);
                await SetVersionAsync(connection, transaction, 3, cancellationToken);
            }

            if (version < 4)
            {
                await MigrateToV4Async(connection, transaction, cancellationToken);
                await SetVersionAsync(connection, transaction, 4, cancellationToken);
                version = 4;
            }

            if (version < 5)
            {
                await MigrateToV5Async(connection, transaction, cancellationToken);
                await SetVersionAsync(connection, transaction, 5, cancellationToken);
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

    private static async Task MigrateToV3Async(SqliteConnection connection, SqliteTransaction transaction, CancellationToken cancellationToken)
    {
        var existingColumns = await ReadColumnsAsync(connection, transaction, "ParcelItems", cancellationToken);
        var hasLegacyItems = existingColumns.Count > 0 && !existingColumns.Contains("ItemType", StringComparer.OrdinalIgnoreCase);
        if (hasLegacyItems) await ExecuteAsync(connection, transaction, "ALTER TABLE ParcelItems RENAME TO ParcelItems_LegacyV2;", cancellationToken);
        const string sql = @"
CREATE TABLE IF NOT EXISTS ParcelItems (
    Id TEXT PRIMARY KEY,
    ParcelId TEXT NOT NULL,
    ItemType TEXT NOT NULL CHECK(ItemType IN ('ApplicationWindow','Application','File','Folder','WebLink','Note')),
    DisplayName TEXT NOT NULL,
    Value TEXT NOT NULL DEFAULT '',
    NormalizedIdentity TEXT NULL,
    SecondaryDetail TEXT NULL,
    CreatedAt TEXT NOT NULL,
    UpdatedAt TEXT NOT NULL,
    LastVerifiedAt TEXT NULL,
    SortOrder INTEGER NOT NULL DEFAULT 0,
    IsMissing INTEGER NOT NULL DEFAULT 0 CHECK(IsMissing IN (0,1)),
    IsInaccessible INTEGER NOT NULL DEFAULT 0 CHECK(IsInaccessible IN (0,1)),
    HasChanged INTEGER NOT NULL DEFAULT 0 CHECK(HasChanged IN (0,1)),
    ExecutablePath TEXT NULL,
    LaunchArguments TEXT NULL,
    WorkingDirectory TEXT NULL,
    WindowTitle TEXT NULL,
    ProcessName TEXT NULL,
    ApplicationUserModelId TEXT NULL,
    FileSize INTEGER NULL,
    FileModifiedAt TEXT NULL,
    Fingerprint TEXT NULL,
    IconCacheKey TEXT NULL,
    NoteContent TEXT NULL,
    LaunchEnabled INTEGER NOT NULL DEFAULT 1 CHECK(LaunchEnabled IN (0,1)),
    CloseSupported INTEGER NOT NULL DEFAULT 0 CHECK(CloseSupported IN (0,1)),
    FOREIGN KEY(ParcelId) REFERENCES Parcels(Id) ON DELETE CASCADE
);
CREATE INDEX IF NOT EXISTS IX_ParcelItems_Parcel_Sort ON ParcelItems(ParcelId, SortOrder, CreatedAt);
CREATE INDEX IF NOT EXISTS IX_ParcelItems_Parcel_Type ON ParcelItems(ParcelId, ItemType);
CREATE UNIQUE INDEX IF NOT EXISTS UX_ParcelItems_Identity ON ParcelItems(ParcelId, ItemType, NormalizedIdentity) WHERE NormalizedIdentity IS NOT NULL;
";
        await ExecuteAsync(connection, transaction, sql, cancellationToken);
        if (!hasLegacyItems) return;

        const string import = @"
INSERT OR IGNORE INTO ParcelItems(Id,ParcelId,ItemType,DisplayName,Value,NormalizedIdentity,SecondaryDetail,CreatedAt,UpdatedAt,LastVerifiedAt,SortOrder,IsMissing,IsInaccessible,HasChanged,ExecutablePath,WorkingDirectory,FileSize,FileModifiedAt,Fingerprint,LaunchEnabled,CloseSupported)
SELECT Id, ParcelId,
       CASE
         WHEN lower(Location) LIKE 'http://%' OR lower(Location) LIKE 'https://%' THEN 'WebLink'
         WHEN Type = 0 THEN 'File'
         WHEN Type = 1 THEN 'Folder'
         WHEN Type = 2 THEN 'WebLink'
         WHEN Type = 3 THEN 'Application'
         ELSE 'Note'
       END,
       COALESCE(NULLIF(Name,''),'Imported item'), COALESCE(Location,''),
       CASE WHEN Type = 4 THEN NULL ELSE COALESCE(NULLIF(NormalizedLocation,''),NULLIF(Location,'')) END,
       NULLIF(Description,''), CreatedAt, UpdatedAt, NULLIF(LastVerifiedAt,''), SortOrder,
       COALESCE(IsMissing,0), CASE WHEN AvailabilityState = 'Inaccessible' THEN 1 ELSE 0 END,
       CASE WHEN AvailabilityState = 'Changed' THEN 1 ELSE 0 END,
       CASE WHEN Type = 3 THEN Location ELSE NULL END, NULLIF(WorkingDirectory,''), FileSize,
       NULLIF(ModifiedAt,''), NULLIF(Fingerprint,''), CASE WHEN Type = 4 THEN 0 ELSE 1 END, 0
FROM ParcelItems_LegacyV2;
DROP TABLE ParcelItems_LegacyV2;";
        await ExecuteAsync(connection, transaction, import, cancellationToken);
    }

    private static async Task MigrateToV4Async(SqliteConnection connection, SqliteTransaction transaction, CancellationToken cancellationToken)
    {
        await ExecuteAsync(connection, transaction, "DROP INDEX IF EXISTS IX_ParcelItems_Parcel_Sort; DROP INDEX IF EXISTS IX_ParcelItems_Parcel_Type; DROP INDEX IF EXISTS UX_ParcelItems_Identity; ALTER TABLE ParcelItems RENAME TO ParcelItems_V3;", cancellationToken);
        const string create = @"
CREATE TABLE ParcelItems (
    Id TEXT PRIMARY KEY,
    ParcelId TEXT NOT NULL,
    ItemType TEXT NOT NULL CHECK(ItemType IN ('ApplicationWindow','Application','File','Folder','WebLink','Note','BrowserTab')),
    DisplayName TEXT NOT NULL,
    Value TEXT NOT NULL DEFAULT '',
    NormalizedIdentity TEXT NULL,
    SecondaryDetail TEXT NULL,
    CreatedAt TEXT NOT NULL,
    UpdatedAt TEXT NOT NULL,
    LastVerifiedAt TEXT NULL,
    SortOrder INTEGER NOT NULL DEFAULT 0,
    IsMissing INTEGER NOT NULL DEFAULT 0 CHECK(IsMissing IN (0,1)),
    IsInaccessible INTEGER NOT NULL DEFAULT 0 CHECK(IsInaccessible IN (0,1)),
    HasChanged INTEGER NOT NULL DEFAULT 0 CHECK(HasChanged IN (0,1)),
    ExecutablePath TEXT NULL,
    LaunchArguments TEXT NULL,
    WorkingDirectory TEXT NULL,
    WindowTitle TEXT NULL,
    ProcessName TEXT NULL,
    ApplicationUserModelId TEXT NULL,
    FileSize INTEGER NULL,
    FileModifiedAt TEXT NULL,
    Fingerprint TEXT NULL,
    IconCacheKey TEXT NULL,
    NoteContent TEXT NULL,
    LaunchEnabled INTEGER NOT NULL DEFAULT 1 CHECK(LaunchEnabled IN (0,1)),
    CloseSupported INTEGER NOT NULL DEFAULT 0 CHECK(CloseSupported IN (0,1)),
    BrowserFamily TEXT NULL,
    BrowserWindowGroupId TEXT NULL,
    BrowserTabIndex INTEGER NULL,
    BrowserPinned INTEGER NOT NULL DEFAULT 0 CHECK(BrowserPinned IN (0,1)),
    BrowserActive INTEGER NOT NULL DEFAULT 0 CHECK(BrowserActive IN (0,1)),
    BrowserTabGroupId TEXT NULL,
    BrowserTabGroupTitle TEXT NULL,
    BrowserTabGroupColor TEXT NULL,
    BrowserFaviconUrl TEXT NULL,
    BrowserCapturedAt TEXT NULL,
    BrowserLastOpenedAt TEXT NULL,
    FOREIGN KEY(ParcelId) REFERENCES Parcels(Id) ON DELETE CASCADE
);
CREATE INDEX IX_ParcelItems_Parcel_Sort ON ParcelItems(ParcelId, SortOrder, CreatedAt);
CREATE INDEX IX_ParcelItems_Parcel_Type ON ParcelItems(ParcelId, ItemType);
CREATE UNIQUE INDEX UX_ParcelItems_Identity ON ParcelItems(ParcelId, ItemType, NormalizedIdentity) WHERE NormalizedIdentity IS NOT NULL;";
        await ExecuteAsync(connection, transaction, create, cancellationToken);
        const string copy = @"
INSERT INTO ParcelItems(Id,ParcelId,ItemType,DisplayName,Value,NormalizedIdentity,SecondaryDetail,CreatedAt,UpdatedAt,LastVerifiedAt,SortOrder,IsMissing,IsInaccessible,HasChanged,ExecutablePath,LaunchArguments,WorkingDirectory,WindowTitle,ProcessName,ApplicationUserModelId,FileSize,FileModifiedAt,Fingerprint,IconCacheKey,NoteContent,LaunchEnabled,CloseSupported)
SELECT Id,ParcelId,ItemType,DisplayName,Value,NormalizedIdentity,SecondaryDetail,CreatedAt,UpdatedAt,LastVerifiedAt,SortOrder,IsMissing,IsInaccessible,HasChanged,ExecutablePath,LaunchArguments,WorkingDirectory,WindowTitle,ProcessName,ApplicationUserModelId,FileSize,FileModifiedAt,Fingerprint,IconCacheKey,NoteContent,LaunchEnabled,CloseSupported FROM ParcelItems_V3;
DROP TABLE ParcelItems_V3;";
        await ExecuteAsync(connection, transaction, copy, cancellationToken);
    }

    private static async Task MigrateToV5Async(SqliteConnection connection, SqliteTransaction transaction, CancellationToken cancellationToken)
    {
        var columns = await ReadColumnsAsync(connection, transaction, "ParcelItems", cancellationToken);
        if (!columns.Contains("BrowserDomain", StringComparer.OrdinalIgnoreCase)) await ExecuteAsync(connection, transaction, "ALTER TABLE ParcelItems ADD COLUMN BrowserDomain TEXT NULL;", cancellationToken);
        await ExecuteAsync(connection, transaction, "UPDATE ParcelItems SET BrowserDomain = SecondaryDetail WHERE ItemType = 'BrowserTab' AND BrowserDomain IS NULL;", cancellationToken);
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
