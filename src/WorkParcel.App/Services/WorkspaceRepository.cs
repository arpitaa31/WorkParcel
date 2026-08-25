using Microsoft.Data.Sqlite;
using WorkParcel_App.Models;

namespace WorkParcel_App.Services;

public sealed class WorkspaceRepository
{
    private readonly SqliteConnectionFactory _factory;
    private readonly AppDataPaths _paths;

    public WorkspaceRepository(SqliteConnectionFactory factory, AppDataPaths paths)
    {
        _factory = factory;
        _paths = paths;
    }

    public async Task<WorkspaceSnapshot> LoadSnapshotAsync(CancellationToken cancellationToken = default)
    {
        var snapshot = new WorkspaceSnapshot();
        await using var connection = await _factory.OpenAsync(cancellationToken);
        var parcels = snapshot.Parcels.ToDictionary(parcel => parcel.Id);

        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT Id, Name, Description, State, CreatedAt, UpdatedAt, LastOpenedAt, LastPackedAt, ArchivedAt, PreviousState FROM Parcels ORDER BY UpdatedAt DESC, CreatedAt DESC;";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var parcel = ReadParcel(reader);
                snapshot.Parcels.Add(parcel);
                parcels[parcel.Id] = parcel;
            }
        }

        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT Id, ParcelId, EventType, Summary, Timestamp FROM ParcelHistory ORDER BY Timestamp DESC;";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var entry = ReadHistory(reader);
                snapshot.History.Add(entry);
                if (parcels.TryGetValue(entry.ParcelId, out var parcel)) parcel.History.Add(entry);
            }
        }

        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT Id, Text, IsCompleted, CreatedAt, CompletedAt, ParcelId, SortOrder FROM TodayTasks ORDER BY IsCompleted, SortOrder, CreatedAt;";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) snapshot.TodayTasks.Add(ReadTodayTask(reader));
        }

        return snapshot;
    }

    public async Task InsertParcelWithHistoryAsync(Parcel parcel, ParcelHistoryEntry history, CancellationToken cancellationToken = default)
    {
        await using var connection = await _factory.OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction();
        await InsertParcelAsync(connection, transaction, parcel, cancellationToken);
        await InsertHistoryAsync(connection, transaction, history, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task UpdateParcelWithHistoryAsync(Parcel parcel, ParcelHistoryEntry? history, CancellationToken cancellationToken = default)
    {
        await using var connection = await _factory.OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction();
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = @"UPDATE Parcels SET Name=$name, Description=$description, State=$state, UpdatedAt=$updated,
LastOpenedAt=$lastOpened, LastPackedAt=$lastPacked, ArchivedAt=$archived, PreviousState=$previous WHERE Id=$id;";
            AddParcelParameters(command, parcel);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        if (history is not null) await InsertHistoryAsync(connection, transaction, history, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task DeleteParcelAsync(Guid parcelId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _factory.OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction();
        await using (var unlink = connection.CreateCommand())
        {
            unlink.Transaction = transaction;
            unlink.CommandText = "UPDATE TodayTasks SET ParcelId = NULL WHERE ParcelId = $id;";
            unlink.Parameters.AddWithValue("$id", parcelId.ToString());
            await unlink.ExecuteNonQueryAsync(cancellationToken);
        }
        await using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM Parcels WHERE Id = $id;";
            delete.Parameters.AddWithValue("$id", parcelId.ToString());
            await delete.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task InsertTodayTaskAsync(TodayTask task, CancellationToken cancellationToken = default)
    {
        await using var connection = await _factory.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = @"INSERT INTO TodayTasks(Id, Text, IsCompleted, CreatedAt, CompletedAt, ParcelId, SortOrder)
VALUES($id, $text, $completed, $created, $completedAt, $parcelId, $sort);";
        AddTodayTaskParameters(command, task);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task UpdateTodayTaskAsync(TodayTask task, CancellationToken cancellationToken = default)
    {
        await using var connection = await _factory.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = @"UPDATE TodayTasks SET Text=$text, IsCompleted=$completed, CompletedAt=$completedAt, ParcelId=$parcelId, SortOrder=$sort WHERE Id=$id;";
        AddTodayTaskParameters(command, task);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task DeleteTodayTaskAsync(Guid taskId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _factory.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM TodayTasks WHERE Id=$id;";
        command.Parameters.AddWithValue("$id", taskId.ToString());
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task ClearCompletedTodayTasksAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _factory.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM TodayTasks WHERE IsCompleted = 1;";
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public long GetDatabaseSize() => File.Exists(_paths.DatabasePath) ? new FileInfo(_paths.DatabasePath).Length : 0;

    public async Task<(int Parcels, int TodayItems)> GetCountsAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _factory.OpenAsync(cancellationToken);
        return (await CountAsync(connection, "Parcels", cancellationToken), await CountAsync(connection, "TodayTasks", cancellationToken));
    }

    public async Task<string> BackupAsync(CancellationToken cancellationToken = default)
    {
        _paths.EnsureDirectories();
        var backupDirectory = Path.Combine(_paths.DataDirectory, "Backups");
        Directory.CreateDirectory(backupDirectory);
        var backupPath = Path.Combine(backupDirectory, $"workparcel-{DateTime.Now:yyyyMMdd-HHmmss}.db");
        var suffix = 1;
        while (File.Exists(backupPath)) backupPath = Path.Combine(backupDirectory, $"workparcel-{DateTime.Now:yyyyMMdd-HHmmss}-{suffix++}.db");
        await using var connection = await _factory.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "VACUUM INTO $path;";
        command.Parameters.AddWithValue("$path", backupPath);
        await command.ExecuteNonQueryAsync(cancellationToken);
        return backupPath;
    }

    public async Task<string?> GetSettingAsync(string key, CancellationToken cancellationToken = default)
    {
        await using var connection = await _factory.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Value FROM Settings WHERE Key=$key;";
        command.Parameters.AddWithValue("$key", key);
        return (await command.ExecuteScalarAsync(cancellationToken))?.ToString();
    }

    public async Task SetSettingAsync(string key, string value, CancellationToken cancellationToken = default)
    {
        await using var connection = await _factory.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO Settings(Key, Value) VALUES($key, $value) ON CONFLICT(Key) DO UPDATE SET Value=excluded.Value;";
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$value", value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertParcelAsync(SqliteConnection connection, SqliteTransaction transaction, Parcel parcel, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = @"INSERT INTO Parcels(Id,Name,Description,State,CreatedAt,UpdatedAt,LastOpenedAt,LastPackedAt,ArchivedAt,PreviousState)
VALUES($id,$name,$description,$state,$created,$updated,$lastOpened,$lastPacked,$archived,$previous);";
        AddParcelParameters(command, parcel, includeCreated: true);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertHistoryAsync(SqliteConnection connection, SqliteTransaction transaction, ParcelHistoryEntry entry, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO ParcelHistory(Id,ParcelId,EventType,Summary,Timestamp) VALUES($id,$parcel,$type,$summary,$timestamp);";
        command.Parameters.AddWithValue("$id", entry.Id.ToString());
        command.Parameters.AddWithValue("$parcel", entry.ParcelId.ToString());
        command.Parameters.AddWithValue("$type", entry.EventType.ToString());
        command.Parameters.AddWithValue("$summary", entry.Summary);
        command.Parameters.AddWithValue("$timestamp", DbValue.Time(entry.Timestamp));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void AddParcelParameters(SqliteCommand command, Parcel parcel, bool includeCreated = false)
    {
        command.Parameters.AddWithValue("$id", parcel.Id.ToString());
        command.Parameters.AddWithValue("$name", parcel.Name);
        command.Parameters.AddWithValue("$description", parcel.Description);
        command.Parameters.AddWithValue("$state", parcel.Status.ToString());
        if (includeCreated) command.Parameters.AddWithValue("$created", DbValue.Time(parcel.CreatedAt));
        command.Parameters.AddWithValue("$updated", DbValue.Time(parcel.UpdatedAt));
        command.Parameters.AddWithValue("$lastOpened", DbValue.Db(DbValue.Time(parcel.LastOpenedAt)));
        command.Parameters.AddWithValue("$lastPacked", DbValue.Db(DbValue.Time(parcel.LastPackedAt)));
        command.Parameters.AddWithValue("$archived", DbValue.Db(DbValue.Time(parcel.ArchivedAt)));
        command.Parameters.AddWithValue("$previous", DbValue.Db(parcel.PreviousStatus?.ToString()));
    }

    private static void AddTodayTaskParameters(SqliteCommand command, TodayTask task)
    {
        command.Parameters.AddWithValue("$id", task.Id.ToString());
        command.Parameters.AddWithValue("$text", task.Text);
        command.Parameters.AddWithValue("$completed", task.IsCompleted ? 1 : 0);
        command.Parameters.AddWithValue("$created", DbValue.Time(task.CreatedAt));
        command.Parameters.AddWithValue("$completedAt", DbValue.Db(DbValue.Time(task.CompletedAt)));
        command.Parameters.AddWithValue("$parcelId", DbValue.Db(task.ParcelId?.ToString()));
        command.Parameters.AddWithValue("$sort", task.SortOrder);
    }

    private static Parcel ReadParcel(SqliteDataReader reader) => new()
    {
        Id = Guid.Parse(reader["Id"].ToString()!),
        Name = reader["Name"].ToString()!,
        Description = reader["Description"].ToString()!,
        Status = Enum.Parse<ParcelStatus>(reader["State"].ToString()!, true),
        CreatedAt = DbValue.Time(reader["CreatedAt"]),
        UpdatedAt = DbValue.Time(reader["UpdatedAt"]),
        LastOpenedAt = DbValue.NullableTime(reader["LastOpenedAt"]),
        LastPackedAt = DbValue.NullableTime(reader["LastPackedAt"]),
        ArchivedAt = DbValue.NullableTime(reader["ArchivedAt"]),
        PreviousStatus = ParseStatus(reader["PreviousState"])
    };

    private static ParcelHistoryEntry ReadHistory(SqliteDataReader reader) => new()
    {
        Id = Guid.Parse(reader["Id"].ToString()!),
        ParcelId = Guid.Parse(reader["ParcelId"].ToString()!),
        EventType = Enum.Parse<ParcelHistoryEventType>(reader["EventType"].ToString()!, true),
        Summary = reader["Summary"].ToString()!,
        Timestamp = DbValue.Time(reader["Timestamp"])
    };

    private static TodayTask ReadTodayTask(SqliteDataReader reader) => new()
    {
        Id = Guid.Parse(reader["Id"].ToString()!),
        Text = reader["Text"].ToString()!,
        IsCompleted = Convert.ToInt32(reader["IsCompleted"]) == 1,
        CreatedAt = DbValue.Time(reader["CreatedAt"]),
        CompletedAt = DbValue.NullableTime(reader["CompletedAt"]),
        ParcelId = reader["ParcelId"] is DBNull ? null : Guid.Parse(reader["ParcelId"].ToString()!),
        SortOrder = Convert.ToInt32(reader["SortOrder"])
    };

    private static ParcelStatus? ParseStatus(object value) => value is DBNull || string.IsNullOrWhiteSpace(value.ToString()) ? null : Enum.TryParse<ParcelStatus>(value.ToString(), true, out var status) ? status : null;

    private static async Task<int> CountAsync(SqliteConnection connection, string table, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM [{table}];";
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
    }
}
