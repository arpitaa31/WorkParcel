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

        await LoadDeskLayoutsAsync(connection, parcels, cancellationToken);

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

    public async Task InsertParcelWithItemsAsync(Parcel parcel, IReadOnlyList<ParcelItem> items, ParcelHistoryEntry history, CancellationToken cancellationToken = default, DeskLayoutSnapshot? layout = null)
    {
        await using var connection = await _factory.OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction();
        await InsertParcelAsync(connection, transaction, parcel, cancellationToken);
        foreach (var item in items) await InsertItemAsync(connection, transaction, item, cancellationToken);
        if (layout is not null) await InsertDeskLayoutAsync(connection, transaction, layout, cancellationToken);
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
WorkingDirectory=$working, WindowTitle=$windowTitle, WindowClassName=$windowClassName, ProcessName=$process, ApplicationUserModelId=$aumid,
 FileSize=$fileSize, FileModifiedAt=$fileModified, Fingerprint=$fingerprint, IconCacheKey=$icon,
 NoteContent=$note, LaunchEnabled=$launchEnabled, CloseSupported=$closeSupported, BrowserFamily=$browserFamily, BrowserDomain=$browserDomain, BrowserWindowGroupId=$browserWindowGroupId, BrowserTabIndex=$browserTabIndex, BrowserPinned=$browserPinned, BrowserActive=$browserActive, BrowserTabGroupId=$browserTabGroupId, BrowserTabGroupTitle=$browserTabGroupTitle, BrowserTabGroupColor=$browserTabGroupColor, BrowserFaviconUrl=$browserFaviconUrl, BrowserCapturedAt=$browserCapturedAt, BrowserLastOpenedAt=$browserLastOpenedAt, BrowserWindowLeft=$browserWindowLeft, BrowserWindowTop=$browserWindowTop, BrowserWindowWidth=$browserWindowWidth, BrowserWindowHeight=$browserWindowHeight, BrowserWindowState=$browserWindowState, BrowserWindowFocused=$browserWindowFocused, BrowserWindowDpiX=$browserWindowDpiX, BrowserWindowDpiY=$browserWindowDpiY WHERE Id=$id;";
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

    public async Task<bool> DeleteItemWithHistoryAsync(Guid itemId, ParcelHistoryEntry history, CancellationToken cancellationToken = default)
    {
        await using var connection = await _factory.OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction();
        var deleted = 0;
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "DELETE FROM ParcelItems WHERE Id=$id AND ParcelId=$parcel;";
            command.Parameters.AddWithValue("$id", itemId.ToString()); command.Parameters.AddWithValue("$parcel", history.ParcelId.ToString());
            deleted = await command.ExecuteNonQueryAsync(cancellationToken);
        }

        // Do not write history or commit when SQLite did not delete the requested
        // row. The affected-row count is the repository's source of truth.
        if (deleted != 1) return false;

        await RemoveDeskWindowReferencesAsync(connection, transaction, history.ParcelId, itemId, cancellationToken);
        await InsertHistoryAsync(connection, transaction, history, cancellationToken);
        await TouchParcelAsync(connection, transaction, history.ParcelId, history.Timestamp, cancellationToken);
        // Once the delete and its history are prepared, finish the commit as a
        // unit. A cancellation request must not turn a committed delete into a
        // false failure at the UI boundary.
        await transaction.CommitAsync(CancellationToken.None);
        return true;
    }

    public async Task<int> DeleteItemsWithHistoryAsync(Guid parcelId, IReadOnlyList<Guid> itemIds, ParcelHistoryEntry history, CancellationToken cancellationToken = default)
    {
        var distinctIds = itemIds.Distinct().ToList();
        if (distinctIds.Count == 0) return 0;

        await using var connection = await _factory.OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction();
        var deleted = 0;
        foreach (var itemId in distinctIds)
        {
            await using var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = "DELETE FROM ParcelItems WHERE Id=$id AND ParcelId=$parcel;"; command.Parameters.AddWithValue("$id", itemId.ToString()); command.Parameters.AddWithValue("$parcel", parcelId.ToString());
            if (await command.ExecuteNonQueryAsync(cancellationToken) != 1) return 0;
            await RemoveDeskWindowReferencesAsync(connection, transaction, parcelId, itemId, cancellationToken);
            deleted++;
        }
        await InsertHistoryAsync(connection, transaction, history, cancellationToken);
        await TouchParcelAsync(connection, transaction, parcelId, history.Timestamp, cancellationToken);
        await transaction.CommitAsync(CancellationToken.None);
        return deleted;
    }

    public async Task ReplaceItemsWithHistoryAsync(Parcel parcel, IReadOnlyList<ParcelItem> items, ParcelHistoryEntry history, CancellationToken cancellationToken = default, DeskLayoutSnapshot? layout = null, bool clearDeskLayout = false)
    {
        await using var connection = await _factory.OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction();
        await using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction; delete.CommandText = "DELETE FROM ParcelItems WHERE ParcelId=$parcel;"; delete.Parameters.AddWithValue("$parcel", parcel.Id.ToString());
            await delete.ExecuteNonQueryAsync(cancellationToken);
        }
        foreach (var item in items) await InsertItemAsync(connection, transaction, item, cancellationToken);
        if (layout is not null || clearDeskLayout)
        {
            await DeleteCurrentDeskLayoutAsync(connection, transaction, parcel.Id, cancellationToken);
            if (layout is not null) await InsertDeskLayoutAsync(connection, transaction, layout, cancellationToken);
        }
        await using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction; update.CommandText = "UPDATE Parcels SET State=$state, UpdatedAt=$updated, LastPackedAt=$packed WHERE Id=$id;";
            update.Parameters.AddWithValue("$state", parcel.Status.ToString()); update.Parameters.AddWithValue("$updated", DbValue.Time(parcel.UpdatedAt)); update.Parameters.AddWithValue("$packed", DbValue.Db(DbValue.Time(parcel.LastPackedAt))); update.Parameters.AddWithValue("$id", parcel.Id.ToString());
            await update.ExecuteNonQueryAsync(cancellationToken);
        }
        await InsertHistoryAsync(connection, transaction, history, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task SaveDeskLayoutAsync(Guid parcelId, DeskLayoutSnapshot layout, ParcelHistoryEntry? history = null, CancellationToken cancellationToken = default)
    {
        await using var connection = await _factory.OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction();
        await DeleteCurrentDeskLayoutAsync(connection, transaction, parcelId, cancellationToken);
        await InsertDeskLayoutAsync(connection, transaction, layout, cancellationToken);
        if (history is not null)
        {
            await InsertHistoryAsync(connection, transaction, history, cancellationToken);
            await TouchParcelAsync(connection, transaction, parcelId, history.Timestamp, cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task DeleteDeskLayoutAsync(Guid parcelId, ParcelHistoryEntry? history = null, CancellationToken cancellationToken = default)
    {
        await using var connection = await _factory.OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction();
        await DeleteCurrentDeskLayoutAsync(connection, transaction, parcelId, cancellationToken);
        if (history is not null)
        {
            await InsertHistoryAsync(connection, transaction, history, cancellationToken);
            await TouchParcelAsync(connection, transaction, parcelId, history.Timestamp, cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task RemoveDeskWindowReferencesAsync(SqliteConnection connection, SqliteTransaction transaction, Guid parcelId, Guid itemId, CancellationToken cancellationToken)
    {
        await using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM DeskWindowLayouts WHERE ParcelId=$parcel AND ParcelItemId=$item;";
            delete.Parameters.AddWithValue("$parcel", parcelId.ToString());
            delete.Parameters.AddWithValue("$item", itemId.ToString());
            await delete.ExecuteNonQueryAsync(cancellationToken);
        }

        // Older Desk Memory rows may have no item identity. Keep those rows
        // while another stable-ID-linked window still anchors the layout, but
        // remove them once this deletion leaves the parcel with no linked
        // windows so they cannot resurrect an orphaned snapshot on restart.
        await using (var removeOrphans = connection.CreateCommand())
        {
            removeOrphans.Transaction = transaction;
            removeOrphans.CommandText = "DELETE FROM DeskWindowLayouts WHERE ParcelId=$parcel AND ParcelItemId IS NULL AND NOT EXISTS (SELECT 1 FROM DeskWindowLayouts WHERE ParcelId=$parcel AND ParcelItemId IS NOT NULL);";
            removeOrphans.Parameters.AddWithValue("$parcel", parcelId.ToString());
            await removeOrphans.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var removeEmpty = connection.CreateCommand();
        removeEmpty.Transaction = transaction;
        removeEmpty.CommandText = "DELETE FROM DeskLayoutSnapshots WHERE ParcelId=$parcel AND IsCurrent=1 AND NOT EXISTS (SELECT 1 FROM DeskWindowLayouts WHERE LayoutSnapshotId=DeskLayoutSnapshots.Id);";
        removeEmpty.Parameters.AddWithValue("$parcel", parcelId.ToString());
        await removeEmpty.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task InsertHistoryOnlyAsync(ParcelHistoryEntry history, CancellationToken cancellationToken = default)
    {
        await using var connection = await _factory.OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction();
        await InsertHistoryAsync(connection, transaction, history, cancellationToken);
        await TouchParcelAsync(connection, transaction, history.ParcelId, history.Timestamp, cancellationToken);
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
        command.CommandText = @"INSERT INTO ParcelItems(Id,ParcelId,ItemType,DisplayName,Value,NormalizedIdentity,SecondaryDetail,CreatedAt,UpdatedAt,LastVerifiedAt,SortOrder,IsMissing,IsInaccessible,HasChanged,ExecutablePath,LaunchArguments,WorkingDirectory,WindowTitle,WindowClassName,ProcessName,ApplicationUserModelId,FileSize,FileModifiedAt,Fingerprint,IconCacheKey,NoteContent,LaunchEnabled,CloseSupported,BrowserFamily,BrowserDomain,BrowserWindowGroupId,BrowserTabIndex,BrowserPinned,BrowserActive,BrowserTabGroupId,BrowserTabGroupTitle,BrowserTabGroupColor,BrowserFaviconUrl,BrowserCapturedAt,BrowserLastOpenedAt,BrowserWindowLeft,BrowserWindowTop,BrowserWindowWidth,BrowserWindowHeight,BrowserWindowState,BrowserWindowFocused,BrowserWindowDpiX,BrowserWindowDpiY)
 VALUES($id,$parcel,$type,$name,$value,$identity,$detail,$created,$updated,$verified,$sort,$missing,$inaccessible,$changed,$executable,$arguments,$working,$windowTitle,$windowClassName,$process,$aumid,$fileSize,$fileModified,$fingerprint,$icon,$note,$launchEnabled,$closeSupported,$browserFamily,$browserDomain,$browserWindowGroupId,$browserTabIndex,$browserPinned,$browserActive,$browserTabGroupId,$browserTabGroupTitle,$browserTabGroupColor,$browserFaviconUrl,$browserCapturedAt,$browserLastOpenedAt,$browserWindowLeft,$browserWindowTop,$browserWindowWidth,$browserWindowHeight,$browserWindowState,$browserWindowFocused,$browserWindowDpiX,$browserWindowDpiY);";
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
        command.Parameters.AddWithValue("$executable", DbValue.Db(item.ExecutablePath)); command.Parameters.AddWithValue("$arguments", DbValue.Db(item.LaunchArguments)); command.Parameters.AddWithValue("$working", DbValue.Db(item.WorkingDirectory)); command.Parameters.AddWithValue("$windowTitle", DbValue.Db(item.WindowTitle)); command.Parameters.AddWithValue("$windowClassName", DbValue.Db(item.WindowClassName)); command.Parameters.AddWithValue("$process", DbValue.Db(item.ProcessName)); command.Parameters.AddWithValue("$aumid", DbValue.Db(item.ApplicationUserModelId));
        command.Parameters.AddWithValue("$fileSize", DbValue.Db(item.FileSize)); command.Parameters.AddWithValue("$fileModified", DbValue.Db(DbValue.Time(item.FileModifiedAt))); command.Parameters.AddWithValue("$fingerprint", DbValue.Db(item.Fingerprint)); command.Parameters.AddWithValue("$icon", DbValue.Db(item.IconCacheKey)); command.Parameters.AddWithValue("$note", DbValue.Db(item.NoteContent));
        command.Parameters.AddWithValue("$launchEnabled", item.LaunchEnabled ? 1 : 0); command.Parameters.AddWithValue("$closeSupported", item.CloseSupported ? 1 : 0);
        command.Parameters.AddWithValue("$browserFamily", DbValue.Db(item.BrowserFamily)); command.Parameters.AddWithValue("$browserDomain", DbValue.Db(item.BrowserDomain)); command.Parameters.AddWithValue("$browserWindowGroupId", DbValue.Db(item.BrowserWindowGroupId)); command.Parameters.AddWithValue("$browserTabIndex", DbValue.Db(item.BrowserTabIndex)); command.Parameters.AddWithValue("$browserPinned", item.BrowserPinned ? 1 : 0); command.Parameters.AddWithValue("$browserActive", item.BrowserActive ? 1 : 0); command.Parameters.AddWithValue("$browserTabGroupId", DbValue.Db(item.BrowserTabGroupId)); command.Parameters.AddWithValue("$browserTabGroupTitle", DbValue.Db(item.BrowserTabGroupTitle)); command.Parameters.AddWithValue("$browserTabGroupColor", DbValue.Db(item.BrowserTabGroupColor)); command.Parameters.AddWithValue("$browserFaviconUrl", DbValue.Db(item.BrowserFaviconUrl)); command.Parameters.AddWithValue("$browserCapturedAt", DbValue.Db(DbValue.Time(item.BrowserCapturedAt))); command.Parameters.AddWithValue("$browserLastOpenedAt", DbValue.Db(DbValue.Time(item.BrowserLastOpenedAt))); command.Parameters.AddWithValue("$browserWindowLeft", DbValue.Db(item.BrowserWindowLeft)); command.Parameters.AddWithValue("$browserWindowTop", DbValue.Db(item.BrowserWindowTop)); command.Parameters.AddWithValue("$browserWindowWidth", DbValue.Db(item.BrowserWindowWidth)); command.Parameters.AddWithValue("$browserWindowHeight", DbValue.Db(item.BrowserWindowHeight)); command.Parameters.AddWithValue("$browserWindowState", DbValue.Db(item.BrowserWindowState)); command.Parameters.AddWithValue("$browserWindowFocused", item.BrowserWindowFocused ? 1 : 0); command.Parameters.AddWithValue("$browserWindowDpiX", DbValue.Db(item.BrowserWindowDpiX)); command.Parameters.AddWithValue("$browserWindowDpiY", DbValue.Db(item.BrowserWindowDpiY));
    }

    private static async Task LoadDeskLayoutsAsync(SqliteConnection connection, Dictionary<Guid, Parcel> parcels, CancellationToken cancellationToken)
    {
        var layouts = new Dictionary<Guid, DeskLayoutSnapshot>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT Id, ParcelId, Name, CreatedAt, UpdatedAt, IsCurrent, IsEnabled, TopologySignature FROM DeskLayoutSnapshots WHERE IsCurrent = 1 AND EXISTS (SELECT 1 FROM DeskWindowLayouts windowLayout INNER JOIN ParcelItems item ON item.Id = windowLayout.ParcelItemId AND item.ParcelId = windowLayout.ParcelId WHERE windowLayout.LayoutSnapshotId = DeskLayoutSnapshots.Id);";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var parcelId = Guid.Parse(reader["ParcelId"].ToString()!);
                var layout = new DeskLayoutSnapshot
                {
                    Id = Guid.Parse(reader["Id"].ToString()!),
                    ParcelId = parcelId,
                    Name = reader["Name"].ToString()!,
                    CreatedAt = DbValue.Time(reader["CreatedAt"]),
                    UpdatedAt = DbValue.Time(reader["UpdatedAt"]),
                    IsCurrent = Convert.ToInt32(reader["IsCurrent"]) == 1,
                    IsEnabled = Convert.ToInt32(reader["IsEnabled"]) == 1,
                    TopologySignature = reader["TopologySignature"].ToString() ?? string.Empty
                };
                layouts[layout.Id] = layout;
                if (parcels.TryGetValue(parcelId, out var parcel)) parcel.DeskLayout = layout;
            }
        }

        if (layouts.Count == 0) return;
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT * FROM DeskMonitors WHERE LayoutSnapshotId IN (SELECT Id FROM DeskLayoutSnapshots WHERE IsCurrent = 1) ORDER BY LayoutSnapshotId, CaptureOrder;";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var layoutId = Guid.Parse(reader["LayoutSnapshotId"].ToString()!);
                if (!layouts.TryGetValue(layoutId, out var layout)) continue;
                layout.Monitors.Add(new DeskMonitorLayout
                {
                    Id = Guid.Parse(reader["Id"].ToString()!),
                    LayoutSnapshotId = layoutId,
                    ParcelId = Guid.Parse(reader["ParcelId"].ToString()!),
                    DeviceIdentifier = reader["DeviceIdentifier"].ToString()!,
                    FriendlyName = reader["FriendlyName"].ToString()!,
                    IsPrimary = Convert.ToInt32(reader["IsPrimary"]) == 1,
                    Bounds = ReadRect(reader, "Bounds"),
                    WorkArea = ReadRect(reader, "Work"),
                    RelativeArrangement = reader["RelativeArrangement"].ToString() ?? string.Empty,
                    DpiX = Convert.ToInt32(reader["DpiX"]),
                    DpiY = Convert.ToInt32(reader["DpiY"]),
                    Orientation = Convert.ToInt32(reader["Orientation"]),
                    CaptureOrder = Convert.ToInt32(reader["CaptureOrder"]),
                    CreatedAt = DbValue.Time(reader["CreatedAt"])
                });
            }
        }

        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT * FROM DeskWindowLayouts WHERE ParcelItemId IS NOT NULL AND LayoutSnapshotId IN (SELECT Id FROM DeskLayoutSnapshots WHERE IsCurrent = 1) ORDER BY LayoutSnapshotId, ZOrderRank, CreatedAt;";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var layoutId = Guid.Parse(reader["LayoutSnapshotId"].ToString()!);
                if (!layouts.TryGetValue(layoutId, out var layout)) continue;
                layout.Windows.Add(new DeskWindowLayout
                {
                    Id = Guid.Parse(reader["Id"].ToString()!),
                    LayoutSnapshotId = layoutId,
                    ParcelId = Guid.Parse(reader["ParcelId"].ToString()!),
                    ParcelItemId = NullableGuid(reader["ParcelItemId"]),
                    ExecutableIdentity = reader["ExecutableIdentity"].ToString() ?? string.Empty,
                    ApplicationIdentifier = NullText(reader["ApplicationIdentifier"]),
                    ProcessName = reader["ProcessName"].ToString() ?? string.Empty,
                    CapturedTitle = reader["CapturedTitle"].ToString() ?? string.Empty,
                    NormalizedTitle = reader["NormalizedTitle"].ToString() ?? string.Empty,
                    WindowClassName = NullText(reader["WindowClassName"]),
                    SavedMonitorId = NullableGuid(reader["SavedMonitorId"]),
                    AbsoluteBounds = ReadRect(reader, "Absolute"),
                    NormalBounds = ReadRect(reader, "Normal"),
                    RelativeLeft = Convert.ToDouble(reader["RelativeLeft"], System.Globalization.CultureInfo.InvariantCulture),
                    RelativeTop = Convert.ToDouble(reader["RelativeTop"], System.Globalization.CultureInfo.InvariantCulture),
                    RelativeWidth = Convert.ToDouble(reader["RelativeWidth"], System.Globalization.CultureInfo.InvariantCulture),
                    RelativeHeight = Convert.ToDouble(reader["RelativeHeight"], System.Globalization.CultureInfo.InvariantCulture),
                    WindowState = ParseDeskWindowState(reader["WindowState"]),
                    ZOrderRank = Convert.ToInt32(reader["ZOrderRank"]),
                    SourceDpiX = Convert.ToInt32(reader["SourceDpiX"]),
                    SourceDpiY = Convert.ToInt32(reader["SourceDpiY"]),
                    MonitorDpiX = Convert.ToInt32(reader["MonitorDpiX"]),
                    MonitorDpiY = Convert.ToInt32(reader["MonitorDpiY"]),
                    IsTopmost = Convert.ToInt32(reader["IsTopmost"]) == 1,
                    CoordinatesArePhysicalPixels = Convert.ToInt32(reader["CoordinatesArePhysicalPixels"]) == 1,
                    IsEnabled = Convert.ToInt32(reader["IsEnabled"]) == 1,
                    IsSupported = Convert.ToInt32(reader["IsSupported"]) == 1,
                    MatchMetadata = NullText(reader["MatchMetadata"]),
                    LastMatchConfidence = ParseDeskMatchConfidence(reader["LastMatchConfidence"]),
                    CreatedAt = DbValue.Time(reader["CreatedAt"]),
                    UpdatedAt = DbValue.Time(reader["UpdatedAt"])
                });
            }
        }
    }

    private static async Task DeleteCurrentDeskLayoutAsync(SqliteConnection connection, SqliteTransaction transaction, Guid parcelId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM DeskLayoutSnapshots WHERE ParcelId=$parcel AND IsCurrent=1;";
        command.Parameters.AddWithValue("$parcel", parcelId.ToString());
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertDeskLayoutAsync(SqliteConnection connection, SqliteTransaction transaction, DeskLayoutSnapshot layout, CancellationToken cancellationToken)
    {
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = @"INSERT INTO DeskLayoutSnapshots(Id,ParcelId,Name,CreatedAt,UpdatedAt,IsCurrent,IsEnabled,TopologySignature)
VALUES($id,$parcel,$name,$created,$updated,$current,$enabled,$signature);";
            command.Parameters.AddWithValue("$id", layout.Id.ToString());
            command.Parameters.AddWithValue("$parcel", layout.ParcelId.ToString());
            command.Parameters.AddWithValue("$name", layout.Name);
            command.Parameters.AddWithValue("$created", DbValue.Time(layout.CreatedAt));
            command.Parameters.AddWithValue("$updated", DbValue.Time(layout.UpdatedAt));
            command.Parameters.AddWithValue("$current", layout.IsCurrent ? 1 : 0);
            command.Parameters.AddWithValue("$enabled", layout.IsEnabled ? 1 : 0);
            command.Parameters.AddWithValue("$signature", layout.TopologySignature);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        foreach (var monitor in layout.Monitors)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = @"INSERT INTO DeskMonitors(Id,LayoutSnapshotId,ParcelId,DeviceIdentifier,FriendlyName,IsPrimary,
BoundsLeft,BoundsTop,BoundsWidth,BoundsHeight,WorkLeft,WorkTop,WorkWidth,WorkHeight,RelativeArrangement,DpiX,DpiY,Orientation,CaptureOrder,CreatedAt)
VALUES($id,$layout,$parcel,$device,$friendly,$primary,$bl,$bt,$bw,$bh,$wl,$wt,$ww,$wh,$arrangement,$dx,$dy,$orientation,$order,$created);";
            command.Parameters.AddWithValue("$id", monitor.Id.ToString()); command.Parameters.AddWithValue("$layout", layout.Id.ToString()); command.Parameters.AddWithValue("$parcel", layout.ParcelId.ToString());
            command.Parameters.AddWithValue("$device", monitor.DeviceIdentifier); command.Parameters.AddWithValue("$friendly", monitor.FriendlyName); command.Parameters.AddWithValue("$primary", monitor.IsPrimary ? 1 : 0);
            AddRectParameters(command, "$b", monitor.Bounds); AddRectParameters(command, "$w", monitor.WorkArea);
            command.Parameters.AddWithValue("$arrangement", monitor.RelativeArrangement); command.Parameters.AddWithValue("$dx", monitor.DpiX); command.Parameters.AddWithValue("$dy", monitor.DpiY); command.Parameters.AddWithValue("$orientation", monitor.Orientation); command.Parameters.AddWithValue("$order", monitor.CaptureOrder); command.Parameters.AddWithValue("$created", DbValue.Time(monitor.CreatedAt));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        foreach (var window in layout.Windows)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = @"INSERT INTO DeskWindowLayouts(Id,LayoutSnapshotId,ParcelId,ParcelItemId,ExecutableIdentity,ApplicationIdentifier,ProcessName,CapturedTitle,NormalizedTitle,WindowClassName,SavedMonitorId,
AbsoluteLeft,AbsoluteTop,AbsoluteWidth,AbsoluteHeight,NormalLeft,NormalTop,NormalWidth,NormalHeight,RelativeLeft,RelativeTop,RelativeWidth,RelativeHeight,WindowState,ZOrderRank,SourceDpiX,SourceDpiY,MonitorDpiX,MonitorDpiY,IsTopmost,CoordinatesArePhysicalPixels,IsEnabled,IsSupported,MatchMetadata,LastMatchConfidence,CreatedAt,UpdatedAt)
VALUES($id,$layout,$parcel,$item,$executable,$app,$process,$title,$normalized,$class,$monitor,$abl,$abt,$abw,$abh,$nbl,$nbt,$nbw,$nbh,$rl,$rt,$rw,$rh,$state,$rank,$dx,$dy,$mdx,$mdy,$topmost,$physical,$enabled,$supported,$metadata,$confidence,$created,$updated);";
            command.Parameters.AddWithValue("$id", window.Id.ToString()); command.Parameters.AddWithValue("$layout", layout.Id.ToString()); command.Parameters.AddWithValue("$parcel", layout.ParcelId.ToString()); command.Parameters.AddWithValue("$item", DbValue.Db(window.ParcelItemId?.ToString()));
            command.Parameters.AddWithValue("$executable", window.ExecutableIdentity); command.Parameters.AddWithValue("$app", DbValue.Db(window.ApplicationIdentifier)); command.Parameters.AddWithValue("$process", window.ProcessName); command.Parameters.AddWithValue("$title", window.CapturedTitle); command.Parameters.AddWithValue("$normalized", window.NormalizedTitle); command.Parameters.AddWithValue("$class", DbValue.Db(window.WindowClassName)); command.Parameters.AddWithValue("$monitor", DbValue.Db(window.SavedMonitorId?.ToString()));
            AddRectParameters(command, "$ab", window.AbsoluteBounds); AddRectParameters(command, "$nb", window.NormalBounds);
            command.Parameters.AddWithValue("$rl", window.RelativeLeft); command.Parameters.AddWithValue("$rt", window.RelativeTop); command.Parameters.AddWithValue("$rw", window.RelativeWidth); command.Parameters.AddWithValue("$rh", window.RelativeHeight); command.Parameters.AddWithValue("$state", window.WindowState.ToString()); command.Parameters.AddWithValue("$rank", window.ZOrderRank); command.Parameters.AddWithValue("$dx", window.SourceDpiX); command.Parameters.AddWithValue("$dy", window.SourceDpiY); command.Parameters.AddWithValue("$mdx", window.MonitorDpiX); command.Parameters.AddWithValue("$mdy", window.MonitorDpiY); command.Parameters.AddWithValue("$topmost", window.IsTopmost ? 1 : 0); command.Parameters.AddWithValue("$physical", window.CoordinatesArePhysicalPixels ? 1 : 0); command.Parameters.AddWithValue("$enabled", window.IsEnabled ? 1 : 0); command.Parameters.AddWithValue("$supported", window.IsSupported ? 1 : 0); command.Parameters.AddWithValue("$metadata", DbValue.Db(window.MatchMetadata)); command.Parameters.AddWithValue("$confidence", window.LastMatchConfidence.ToString()); command.Parameters.AddWithValue("$created", DbValue.Time(window.CreatedAt)); command.Parameters.AddWithValue("$updated", DbValue.Time(window.UpdatedAt));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static void AddRectParameters(SqliteCommand command, string prefix, DeskRect rect)
    {
        command.Parameters.AddWithValue(prefix + "l", rect.Left); command.Parameters.AddWithValue(prefix + "t", rect.Top); command.Parameters.AddWithValue(prefix + "w", rect.Width); command.Parameters.AddWithValue(prefix + "h", rect.Height);
    }

    private static DeskRect ReadRect(SqliteDataReader reader, string prefix) => new(
        Convert.ToInt32(reader[prefix + "Left"]), Convert.ToInt32(reader[prefix + "Top"]), Convert.ToInt32(reader[prefix + "Width"]), Convert.ToInt32(reader[prefix + "Height"]));

    private static Guid? NullableGuid(object value) => value is DBNull || string.IsNullOrWhiteSpace(value.ToString()) ? null : Guid.Parse(value.ToString()!);
    private static DeskWindowState ParseDeskWindowState(object value) => Enum.TryParse<DeskWindowState>(value.ToString(), true, out var state) ? state : DeskWindowState.Unknown;
    private static DeskMatchConfidence ParseDeskMatchConfidence(object value) => Enum.TryParse<DeskMatchConfidence>(value.ToString(), true, out var confidence) ? confidence : DeskMatchConfidence.NoMatch;

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
        ExecutablePath = NullText(reader["ExecutablePath"]), LaunchArguments = NullText(reader["LaunchArguments"]), WorkingDirectory = NullText(reader["WorkingDirectory"]), WindowTitle = NullText(reader["WindowTitle"]), WindowClassName = NullText(reader["WindowClassName"]), ProcessName = NullText(reader["ProcessName"]), ApplicationUserModelId = NullText(reader["ApplicationUserModelId"]),
        FileSize = reader["FileSize"] is DBNull ? null : Convert.ToInt64(reader["FileSize"]), FileModifiedAt = DbValue.NullableTime(reader["FileModifiedAt"]), Fingerprint = NullText(reader["Fingerprint"]), IconCacheKey = NullText(reader["IconCacheKey"]), NoteContent = NullText(reader["NoteContent"]),
        LaunchEnabled = Convert.ToInt32(reader["LaunchEnabled"]) == 1, CloseSupported = Convert.ToInt32(reader["CloseSupported"]) == 1,
        BrowserFamily = NullText(reader["BrowserFamily"]), BrowserDomain = NullText(reader["BrowserDomain"]), BrowserWindowGroupId = NullText(reader["BrowserWindowGroupId"]), BrowserTabIndex = reader["BrowserTabIndex"] is DBNull ? null : Convert.ToInt32(reader["BrowserTabIndex"]), BrowserPinned = Convert.ToInt32(reader["BrowserPinned"]) == 1, BrowserActive = Convert.ToInt32(reader["BrowserActive"]) == 1, BrowserTabGroupId = NullText(reader["BrowserTabGroupId"]), BrowserTabGroupTitle = NullText(reader["BrowserTabGroupTitle"]), BrowserTabGroupColor = NullText(reader["BrowserTabGroupColor"]), BrowserFaviconUrl = NullText(reader["BrowserFaviconUrl"]), BrowserCapturedAt = DbValue.NullableTime(reader["BrowserCapturedAt"]), BrowserLastOpenedAt = DbValue.NullableTime(reader["BrowserLastOpenedAt"]), BrowserWindowLeft = NullableInt(reader["BrowserWindowLeft"]), BrowserWindowTop = NullableInt(reader["BrowserWindowTop"]), BrowserWindowWidth = NullableInt(reader["BrowserWindowWidth"]), BrowserWindowHeight = NullableInt(reader["BrowserWindowHeight"]), BrowserWindowState = NullText(reader["BrowserWindowState"]), BrowserWindowFocused = Convert.ToInt32(reader["BrowserWindowFocused"]) == 1, BrowserWindowDpiX = NullableInt(reader["BrowserWindowDpiX"]), BrowserWindowDpiY = NullableInt(reader["BrowserWindowDpiY"])
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
    private static int? NullableInt(object value) => value is DBNull || string.IsNullOrWhiteSpace(value.ToString()) ? null : Convert.ToInt32(value);
    private static string? NullText(object value) => value is DBNull ? null : value.ToString();

    private static async Task<int> CountAsync(SqliteConnection connection, string table, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM [{table}];";
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
    }
}
