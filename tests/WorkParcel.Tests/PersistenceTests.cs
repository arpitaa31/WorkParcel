using Microsoft.Data.Sqlite;
using Xunit;
using WorkParcel_App.Models;
using WorkParcel_App.Services;

namespace WorkParcel.Tests;

public sealed class PersistenceTests
{
    [Fact]
    public async Task EmptyDatabaseInitializesRepeatedlyWithoutSeeding()
    {
        using var temp = new TempDirectory();
        var first = await CreateStoreAsync(temp.Path);
        var second = await CreateStoreAsync(temp.Path);
        Assert.Empty(first.Parcels);
        Assert.Empty(second.Parcels);
        await using var connection = new SqliteConnection($"Data Source={temp.Path}\\Data\\workparcel.db");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand(); command.CommandText = "SELECT Value FROM SchemaInfo WHERE Key='SchemaVersion';";
        Assert.Equal(DatabaseInitializer.CurrentSchemaVersion.ToString(), (await command.ExecuteScalarAsync())?.ToString());
        await using var columnsCommand = connection.CreateCommand();
        columnsCommand.CommandText = "PRAGMA table_info(ParcelItems);";
        var columns = new List<string>();
        await using var reader = await columnsCommand.ExecuteReaderAsync();
        while (await reader.ReadAsync()) columns.Add(reader["name"].ToString()!);
        Assert.DoesNotContain(columns, column => column.StartsWith("Browser", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ParcelCreationAndEditSurviveRestart()
    {
        using var temp = new TempDirectory(); var store = await CreateStoreAsync(temp.Path);
        var parcel = await store.CreateEmptyAsync("  Research setup  ", "  Useful context  ");
        Assert.Equal(ParcelStatus.Packed, parcel.Status); Assert.Equal("Research setup", parcel.Name); Assert.Equal("Useful context", parcel.Description); Assert.Equal(0, parcel.ItemCount);
        var originalName = parcel.Name; var originalDescription = parcel.Description; parcel.Name = "Updated setup"; parcel.Description = "Updated context"; await store.UpdateParcelAsync(parcel, originalName, originalDescription);
        var restarted = await CreateStoreAsync(temp.Path); var loaded = restarted.Parcels.Single(p => p.Id == parcel.Id);
        Assert.Equal("Updated setup", loaded.Name); Assert.Equal("Updated context", loaded.Description); Assert.Contains(loaded.History, e => e.EventType == ParcelHistoryEventType.Updated);
    }

    [Fact]
    public async Task OpenPackedArchiveRestoreAndDeletePersistSafely()
    {
        using var temp = new TempDirectory(); var store = await CreateStoreAsync(temp.Path); var parcel = await store.CreateEmptyAsync("Workflow");
        await store.SetStateAsync(parcel, ParcelStatus.Open); Assert.Equal(ParcelStatus.Open, parcel.Status); Assert.NotNull(parcel.LastOpenedAt);
        await store.SetStateAsync(parcel, ParcelStatus.Packed); Assert.Equal(ParcelStatus.Packed, parcel.Status); Assert.NotNull(parcel.LastPackedAt);
        await store.ArchiveAsync(parcel); Assert.Single(store.Archived); Assert.Empty(store.Parcels);
        var restarted = await CreateStoreAsync(temp.Path); var archived = Assert.Single(restarted.Archived); await restarted.RestoreAsync(archived); Assert.Equal(ParcelStatus.Packed, archived.Status); Assert.Single(restarted.Parcels);
        var external = Path.Combine(temp.Path, "external.txt"); await File.WriteAllTextAsync(external, "keep"); await restarted.DeleteAsync(archived); Assert.DoesNotContain(restarted.Parcels, p => p.Id == archived.Id); Assert.Equal("keep", await File.ReadAllTextAsync(external));
        var after = await CreateStoreAsync(temp.Path); Assert.Empty(after.Parcels); Assert.Empty(after.Archived);
    }

    [Fact]
    public async Task SearchFilterAndSortUseRealParcelData()
    {
        using var temp = new TempDirectory(); var store = await CreateStoreAsync(temp.Path); var alpha = await store.CreateEmptyAsync("Alpha", "client handoff"); await store.CreateEmptyAsync("Beta", "internal notes"); await store.SetStateAsync(alpha, ParcelStatus.Open);
        Assert.Single(ParcelQueries.Apply(store.Parcels, new ParcelQueryOptions("HAND", null, ParcelSort.Name)));
        Assert.Single(ParcelQueries.Apply(store.Parcels, new ParcelQueryOptions(null, ParcelStatus.Packed, ParcelSort.RecentlyUpdated)));
        Assert.Equal("Alpha", ParcelQueries.Apply(store.Parcels, new ParcelQueryOptions(null, null, ParcelSort.Name)).First().Name);
    }

    [Fact]
    public async Task TodayItemsCreateCompleteEditDeleteAndUnlinkSafely()
    {
        using var temp = new TempDirectory(); var store = await CreateStoreAsync(temp.Path); var parcel = await store.CreateEmptyAsync("Linked parcel");
        await store.AddTodayTaskAsync("  Review setup  ", parcel.Id); var task = Assert.Single(store.TodayTasks); Assert.Equal("Review setup", task.Text); Assert.Equal(parcel.Id, task.ParcelId);
        await store.ToggleTodayTaskAsync(task, true); Assert.NotNull(task.CompletedAt); var oldText = task.Text; await store.UpdateTodayTaskAsync(task, "Review updated", true, parcel.Id); Assert.NotEqual(oldText, task.Text);
        var restarted = await CreateStoreAsync(temp.Path); var restartedTask = Assert.Single(restarted.TodayTasks); Assert.Equal("Review updated", restartedTask.Text); var restartedParcel = Assert.Single(restarted.Parcels); await restarted.DeleteAsync(restartedParcel); Assert.Null(restartedTask.ParcelId); await restarted.UpdateTodayTaskAsync(restartedTask, restartedTask.Text, false, null); await restarted.DeleteTodayTaskAsync(restartedTask); Assert.Empty(restarted.TodayTasks);
    }

    [Fact]
    public async Task HistoryRecordsOnlyMeaningfulParcelEvents()
    {
        using var temp = new TempDirectory(); var store = await CreateStoreAsync(temp.Path); var parcel = await store.CreateEmptyAsync("History");
        var originalName = parcel.Name; parcel.Name = "Renamed"; await store.UpdateParcelAsync(parcel, originalName, parcel.Description); await store.SetStateAsync(parcel, ParcelStatus.Open); await store.SetStateAsync(parcel, ParcelStatus.Packed);
        var restarted = await CreateStoreAsync(temp.Path); var loaded = restarted.Parcels.Single(); Assert.Collection(loaded.History.OrderBy(h => h.Timestamp), history => Assert.Equal(ParcelHistoryEventType.Created, history.EventType), history => Assert.Equal(ParcelHistoryEventType.Updated, history.EventType), history => Assert.Equal(ParcelHistoryEventType.Opened, history.EventType), history => Assert.Equal(ParcelHistoryEventType.Packed, history.EventType));
    }

    [Fact]
    public async Task ParcelAndHistoryTransactionRollsBackOnDuplicateRecord()
    {
        using var temp = new TempDirectory(); var paths = new AppDataPaths(temp.Path); var factory = new SqliteConnectionFactory(paths); var initializer = new DatabaseInitializer(factory, new AppLogger(paths)); await initializer.InitializeAsync(); var repository = new WorkspaceRepository(factory, paths);
        var now = DateTime.Now; var parcel = new Parcel { Name = "Atomic", CreatedAt = now, UpdatedAt = now, LastPackedAt = now, Status = ParcelStatus.Packed }; var created = new ParcelHistoryEntry { ParcelId = parcel.Id, EventType = ParcelHistoryEventType.Created, Summary = "Parcel created", Timestamp = now }; await repository.InsertParcelWithHistoryAsync(parcel, created);
        await Assert.ThrowsAsync<SqliteException>(() => repository.InsertParcelWithHistoryAsync(parcel, new ParcelHistoryEntry { ParcelId = parcel.Id, EventType = ParcelHistoryEventType.Created, Summary = "Should roll back", Timestamp = now }));
        var snapshot = await repository.LoadSnapshotAsync(); Assert.Single(snapshot.Parcels); Assert.Single(snapshot.History); Assert.Equal("Parcel created", snapshot.History[0].Summary);
    }

    private static async Task<WorkspaceStore> CreateStoreAsync(string root) { var store = new WorkspaceStore(new AppDataPaths(root)); await store.InitializeAsync(); return store; }

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "WorkParcelTests", Guid.NewGuid().ToString("N"));
        public TempDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() { try { Directory.Delete(Path, true); } catch { } }
    }
}
