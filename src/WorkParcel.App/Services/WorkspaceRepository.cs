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
            command.CommandText = "SELECT * FROM ParcelItems ORDER BY ParcelId, SortOrder, CreatedAt;";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var item = ReadItem(reader); snapshot.Items.Add(item);
                if (parcels.TryGetValue(item.ParcelId, out var parcel)) parcel.Items.Add(item);
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

    public async Task InsertParcelWithItemsAsync(Parcel parcel, IReadOnlyList<ParcelItem> items, ParcelHistoryEntry history, CancellationToken cancellationToken = default)
    {
        await using var connection = await _factory.OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction();
        await InsertParcelAsync(connection, transaction, parcel, cancellationToken);
        foreach (var item in items) await InsertItemAsync(connection, transaction, item, cancellationToken);
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

    public async Task InsertItemsWithHistoryAsync(IReadOnlyList<ParcelItem> items, ParcelHistoryEntry history, CancellationToken cancellationToken = default)
    {
        await using var connection = await _factory.OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction();
        foreach (var item in items) await InsertItemAsync(connection, transaction, item, cancellationToken);
        await InsertHistoryAsync(connection, transaction, history, cancellationToken);
        await TouchParcelAsync(connection, transaction, history.ParcelId, history.Timestamp, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task UpdateItemWithHistoryAsync(ParcelItem item, ParcelHistoryEntry? history, CancellationToken cancellationToken = default)
    {
        await using var connection = await _factory.OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction();
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = @"UPDATE ParcelItems SET ItemType=$type, DisplayName=$name, Value=$value, NormalizedIdentity=$identity,
SecondaryDetail=$detail, UpdatedAt=$updated, LastVerifiedAt=$verified, SortOrder=$sort, IsMissing=$missing,
IsInaccessible=$inaccessible, HasChanged=$changed, ExecutablePath=$executable, LaunchArguments=$arguments,
WorkingDirectory=$working, WindowTitle=$windowTitle, ProcessName=$process, ApplicationUserModelId=$aumid,
FileSize=$fileSize, FileModifiedAt=$fileModified, Fingerprint=$fingerprint, IconCacheKey=$icon,
NoteContent=$note, LaunchEnabled=$launchEnabled, CloseSupported=$closeSupported, BrowserFamily=$browserFamily, BrowserDomain=$browserDomain, BrowserWindowGroupId=$browserWindowGroupId, BrowserTabIndex=$browserTabIndex, BrowserPinned=$browserPinned, BrowserActive=$browserActive, BrowserTabGroupId=$browserTabGroupId, BrowserTabGroupTitle=$browserTabGroupTitle, BrowserTabGroupColor=$browserTabGroupColor, BrowserFaviconUrl=$browserFaviconUrl, BrowserCapturedAt=$browserCapturedAt, BrowserLastOpenedAt=$browserLastOpenedAt WHERE Id=$id;";
            AddItemParameters(command, item);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        if (history is not null)
        {
            await InsertHistoryAsync(connection, transaction, history, cancellationToken);
            await TouchParcelAsync(connection, transaction, item.ParcelId, history.Timestamp, cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task UpdateItemStatesAsync(IEnumerable<ParcelItem> items, CancellationToken cancellationToken = default)
    {
        await using var connection = await _factory.OpenAsync(cancellationToken); await using var transaction = connection.BeginTransaction();
        foreach (var item in items)
        {
            await using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "UPDATE ParcelItems SET UpdatedAt=$updated,LastVerifiedAt=$verified,IsMissing=$missing,IsInaccessible=$inaccessible,HasChanged=$changed WHERE Id=$id;";
            command.Parameters.AddWithValue("$updated", DbValue.Time(item.UpdatedAt)); command.Parameters.AddWithValue("$verified", DbValue.Db(DbValue.Time(item.LastVerifiedAt))); command.Parameters.AddWithValue("$missing", item.IsMissing ? 1 : 0); command.Parameters.AddWithValue("$inaccessible", item.IsInaccessible ? 1 : 0); command.Parameters.AddWithValue("$changed", item.HasChanged ? 1 : 0); command.Parameters.AddWithValue("$id", item.Id.ToString()); await command.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task UpdateBrowserLastOpenedAsync(IEnumerable<ParcelItem> items, DateTime openedAt, CancellationToken cancellationToken = default)
    {
        var browserItems = items.Where(item => item.ItemType == ParcelItemType.BrowserTab).ToList();
        if (browserItems.Count == 0) return;
        await using var connection = await _factory.OpenAsync(cancellationToken); await using var transaction = connection.BeginTransaction();
        foreach (var item in browserItems)
        {
            await using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "UPDATE ParcelItems SET BrowserLastOpenedAt=$opened WHERE Id=$id AND ParcelId=$parcel AND ItemType='BrowserTab';";
            command.Parameters.AddWithValue("$opened", DbValue.Time(openedAt)); command.Parameters.AddWithValue("$id", item.Id.ToString()); command.Parameters.AddWithValue("$parcel", item.ParcelId.ToString());
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task DeleteItemWithHistoryAsync(Guid itemId, ParcelHistoryEntry history, CancellationToken cancellationToken = default)
    {
        await using var connection = await _factory.OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction();
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "DELETE FROM ParcelItems WHERE Id=$id AND ParcelId=$parcel;";
            command.Parameters.AddWithValue("$id", itemId.ToString()); command.Parameters.AddWithValue("$parcel", history.ParcelId.ToString());
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        await InsertHistoryAsync(connection, transaction, history, cancellationToken);
        await TouchParcelAsync(connection, transaction, history.ParcelId, history.Timestamp, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task DeleteItemsWithHistoryAsync(Guid parcelId, IReadOnlyList<Guid> itemIds, ParcelHistoryEntry history, CancellationToken cancellationToken = default)
    {
        if (itemIds.Count == 0) return;
        await using var connection = await _factory.OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction();
        foreach (var itemId in itemIds)
        {
            await using var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = "DELETE FROM ParcelItems WHERE Id=$id AND ParcelId=$parcel;"; command.Parameters.AddWithValue("$id", itemId.ToString()); command.Parameters.AddWithValue("$parcel", parcelId.ToString()); await command.ExecuteNonQueryAsync(cancellationToken);
        }
        await InsertHistoryAsync(connection, transaction, history, cancellationToken);
        await TouchParcelAsync(connection, transaction, parcelId, history.Timestamp, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task ReplaceItemsWithHistoryAsync(Parcel parcel, IReadOnlyList<ParcelItem> items, ParcelHistoryEntry history, CancellationToken cancellationToken = default)
    {
        await using var connection = await _factory.OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction();
        await using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction; delete.CommandText = "DELETE FROM ParcelItems WHERE ParcelId=$parcel;"; delete.Parameters.AddWithValue("$parcel", parcel.Id.ToString());
            await delete.ExecuteNonQueryAsync(cancellationToken);
        }
        foreach (var item in items) await InsertItemAsync(connection, transaction, item, cancellationToken);
        await using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction; update.CommandText = "UPDATE Parcels SET State=$state, UpdatedAt=$updated, LastPackedAt=$packed WHERE Id=$id;";
            update.Parameters.AddWithValue("$state", parcel.Status.ToString()); update.Parameters.AddWithValue("$updated", DbValue.Time(parcel.UpdatedAt)); update.Parameters.AddWithValue("$packed", DbValue.Db(DbValue.Time(parcel.LastPackedAt))); update.Parameters.AddWithValue("$id", parcel.Id.ToString());
            await update.ExecuteNonQueryAsync(cancellationToken);
        }
        await InsertHistoryAsync(connection, transaction, history, cancellationToken);
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

    private static async Task InsertItemAsync(SqliteConnection connection, SqliteTransaction transaction, ParcelItem item, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = @"INSERT INTO ParcelItems(Id,ParcelId,ItemType,DisplayName,Value,NormalizedIdentity,SecondaryDetail,CreatedAt,UpdatedAt,LastVerifiedAt,SortOrder,IsMissing,IsInaccessible,HasChanged,ExecutablePath,LaunchArguments,WorkingDirectory,WindowTitle,ProcessName,ApplicationUserModelId,FileSize,FileModifiedAt,Fingerprint,IconCacheKey,NoteContent,LaunchEnabled,CloseSupported,BrowserFamily,BrowserDomain,BrowserWindowGroupId,BrowserTabIndex,BrowserPinned,BrowserActive,BrowserTabGroupId,BrowserTabGroupTitle,BrowserTabGroupColor,BrowserFaviconUrl,BrowserCapturedAt,BrowserLastOpenedAt)
VALUES($id,$parcel,$type,$name,$value,$identity,$detail,$created,$updated,$verified,$sort,$missing,$inaccessible,$changed,$executable,$arguments,$working,$windowTitle,$process,$aumid,$fileSize,$fileModified,$fingerprint,$icon,$note,$launchEnabled,$closeSupported,$browserFamily,$browserDomain,$browserWindowGroupId,$browserTabIndex,$browserPinned,$browserActive,$browserTabGroupId,$browserTabGroupTitle,$browserTabGroupColor,$browserFaviconUrl,$browserCapturedAt,$browserLastOpenedAt);";
        AddItemParameters(command, item, true);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task TouchParcelAsync(SqliteConnection connection, SqliteTransaction transaction, Guid parcelId, DateTime timestamp, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = "UPDATE Parcels SET UpdatedAt=$updated WHERE Id=$id;";
        command.Parameters.AddWithValue("$updated", DbValue.Time(timestamp)); command.Parameters.AddWithValue("$id", parcelId.ToString()); await command.ExecuteNonQueryAsync(cancellationToken);
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

    private static void AddItemParameters(SqliteCommand command, ParcelItem item, bool includeCreated = false)
    {
        command.Parameters.AddWithValue("$id", item.Id.ToString()); command.Parameters.AddWithValue("$parcel", item.ParcelId.ToString());
        command.Parameters.AddWithValue("$type", item.ItemType.ToString()); command.Parameters.AddWithValue("$name", item.DisplayName); command.Parameters.AddWithValue("$value", item.Value);
        command.Parameters.AddWithValue("$identity", DbValue.Db(item.NormalizedIdentity)); command.Parameters.AddWithValue("$detail", DbValue.Db(item.SecondaryDetail));
        if (includeCreated) command.Parameters.AddWithValue("$created", DbValue.Time(item.CreatedAt));
        command.Parameters.AddWithValue("$updated", DbValue.Time(item.UpdatedAt)); command.Parameters.AddWithValue("$verified", DbValue.Db(DbValue.Time(item.LastVerifiedAt)));
        command.Parameters.AddWithValue("$sort", item.SortOrder); command.Parameters.AddWithValue("$missing", item.IsMissing ? 1 : 0); command.Parameters.AddWithValue("$inaccessible", item.IsInaccessible ? 1 : 0); command.Parameters.AddWithValue("$changed", item.HasChanged ? 1 : 0);
        command.Parameters.AddWithValue("$executable", DbValue.Db(item.ExecutablePath)); command.Parameters.AddWithValue("$arguments", DbValue.Db(item.LaunchArguments)); command.Parameters.AddWithValue("$working", DbValue.Db(item.WorkingDirectory)); command.Parameters.AddWithValue("$windowTitle", DbValue.Db(item.WindowTitle)); command.Parameters.AddWithValue("$process", DbValue.Db(item.ProcessName)); command.Parameters.AddWithValue("$aumid", DbValue.Db(item.ApplicationUserModelId));
        command.Parameters.AddWithValue("$fileSize", DbValue.Db(item.FileSize)); command.Parameters.AddWithValue("$fileModified", DbValue.Db(DbValue.Time(item.FileModifiedAt))); command.Parameters.AddWithValue("$fingerprint", DbValue.Db(item.Fingerprint)); command.Parameters.AddWithValue("$icon", DbValue.Db(item.IconCacheKey)); command.Parameters.AddWithValue("$note", DbValue.Db(item.NoteContent));
        command.Parameters.AddWithValue("$launchEnabled", item.LaunchEnabled ? 1 : 0); command.Parameters.AddWithValue("$closeSupported", item.CloseSupported ? 1 : 0);
        command.Parameters.AddWithValue("$browserFamily", DbValue.Db(item.BrowserFamily)); command.Parameters.AddWithValue("$browserDomain", DbValue.Db(item.BrowserDomain)); command.Parameters.AddWithValue("$browserWindowGroupId", DbValue.Db(item.BrowserWindowGroupId)); command.Parameters.AddWithValue("$browserTabIndex", DbValue.Db(item.BrowserTabIndex)); command.Parameters.AddWithValue("$browserPinned", item.BrowserPinned ? 1 : 0); command.Parameters.AddWithValue("$browserActive", item.BrowserActive ? 1 : 0); command.Parameters.AddWithValue("$browserTabGroupId", DbValue.Db(item.BrowserTabGroupId)); command.Parameters.AddWithValue("$browserTabGroupTitle", DbValue.Db(item.BrowserTabGroupTitle)); command.Parameters.AddWithValue("$browserTabGroupColor", DbValue.Db(item.BrowserTabGroupColor)); command.Parameters.AddWithValue("$browserFaviconUrl", DbValue.Db(item.BrowserFaviconUrl)); command.Parameters.AddWithValue("$browserCapturedAt", DbValue.Db(DbValue.Time(item.BrowserCapturedAt))); command.Parameters.AddWithValue("$browserLastOpenedAt", DbValue.Db(DbValue.Time(item.BrowserLastOpenedAt)));
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

    private static ParcelItem ReadItem(SqliteDataReader reader) => new()
    {
        Id = Guid.Parse(reader["Id"].ToString()!), ParcelId = Guid.Parse(reader["ParcelId"].ToString()!), ItemType = Enum.Parse<ParcelItemType>(reader["ItemType"].ToString()!, true),
        DisplayName = reader["DisplayName"].ToString()!, Value = reader["Value"].ToString()!, NormalizedIdentity = NullText(reader["NormalizedIdentity"]), SecondaryDetail = NullText(reader["SecondaryDetail"]),
        CreatedAt = DbValue.Time(reader["CreatedAt"]), UpdatedAt = DbValue.Time(reader["UpdatedAt"]), LastVerifiedAt = DbValue.NullableTime(reader["LastVerifiedAt"]), SortOrder = Convert.ToInt32(reader["SortOrder"]),
        IsMissing = Convert.ToInt32(reader["IsMissing"]) == 1, IsInaccessible = Convert.ToInt32(reader["IsInaccessible"]) == 1, HasChanged = Convert.ToInt32(reader["HasChanged"]) == 1,
        ExecutablePath = NullText(reader["ExecutablePath"]), LaunchArguments = NullText(reader["LaunchArguments"]), WorkingDirectory = NullText(reader["WorkingDirectory"]), WindowTitle = NullText(reader["WindowTitle"]), ProcessName = NullText(reader["ProcessName"]), ApplicationUserModelId = NullText(reader["ApplicationUserModelId"]),
        FileSize = reader["FileSize"] is DBNull ? null : Convert.ToInt64(reader["FileSize"]), FileModifiedAt = DbValue.NullableTime(reader["FileModifiedAt"]), Fingerprint = NullText(reader["Fingerprint"]), IconCacheKey = NullText(reader["IconCacheKey"]), NoteContent = NullText(reader["NoteContent"]),
        LaunchEnabled = Convert.ToInt32(reader["LaunchEnabled"]) == 1, CloseSupported = Convert.ToInt32(reader["CloseSupported"]) == 1,
        BrowserFamily = NullText(reader["BrowserFamily"]), BrowserDomain = NullText(reader["BrowserDomain"]), BrowserWindowGroupId = NullText(reader["BrowserWindowGroupId"]), BrowserTabIndex = reader["BrowserTabIndex"] is DBNull ? null : Convert.ToInt32(reader["BrowserTabIndex"]), BrowserPinned = Convert.ToInt32(reader["BrowserPinned"]) == 1, BrowserActive = Convert.ToInt32(reader["BrowserActive"]) == 1, BrowserTabGroupId = NullText(reader["BrowserTabGroupId"]), BrowserTabGroupTitle = NullText(reader["BrowserTabGroupTitle"]), BrowserTabGroupColor = NullText(reader["BrowserTabGroupColor"]), BrowserFaviconUrl = NullText(reader["BrowserFaviconUrl"]), BrowserCapturedAt = DbValue.NullableTime(reader["BrowserCapturedAt"]), BrowserLastOpenedAt = DbValue.NullableTime(reader["BrowserLastOpenedAt"])
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
    private static string? NullText(object value) => value is DBNull ? null : value.ToString();

    private static async Task<int> CountAsync(SqliteConnection connection, string table, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM [{table}];";
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
    }
}
