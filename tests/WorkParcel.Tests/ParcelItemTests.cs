using Microsoft.Data.Sqlite;
using System.Security.Cryptography;
using WorkParcel_App.Models;
using WorkParcel_App.Services;
using Xunit;

namespace WorkParcel.Tests;

public sealed class ParcelItemTests
{
    [Fact]
    public async Task VersionTwoDatabaseMigratesToItemSchemaWithoutLosingParcel()
    {
        using var temp = new TestFolder(); var paths = new AppDataPaths(temp.Path); paths.EnsureDirectories();
        await using (var connection = new SqliteConnection($"Data Source={paths.DatabasePath}"))
        {
            await connection.OpenAsync(); var command = connection.CreateCommand(); command.CommandText = @"
CREATE TABLE SchemaInfo(Key TEXT PRIMARY KEY, Value TEXT NOT NULL);
INSERT INTO SchemaInfo VALUES('SchemaVersion','2');
CREATE TABLE Parcels(Id TEXT PRIMARY KEY,Name TEXT NOT NULL,Description TEXT NOT NULL DEFAULT '',State TEXT NOT NULL,CreatedAt TEXT NOT NULL,UpdatedAt TEXT NOT NULL,LastOpenedAt TEXT NULL,LastPackedAt TEXT NULL,ArchivedAt TEXT NULL,PreviousState TEXT NULL);
CREATE TABLE ParcelHistory(Id TEXT PRIMARY KEY,ParcelId TEXT NOT NULL,EventType TEXT NOT NULL,Summary TEXT NOT NULL,Timestamp TEXT NOT NULL,FOREIGN KEY(ParcelId) REFERENCES Parcels(Id) ON DELETE CASCADE);
CREATE TABLE TodayTasks(Id TEXT PRIMARY KEY,Text TEXT NOT NULL,IsCompleted INTEGER NOT NULL,CreatedAt TEXT NOT NULL,CompletedAt TEXT NULL,ParcelId TEXT NULL,SortOrder INTEGER NOT NULL);
CREATE TABLE Settings(Key TEXT PRIMARY KEY,Value TEXT NOT NULL);
CREATE TABLE ParcelItems(Id TEXT PRIMARY KEY,ParcelId TEXT NOT NULL,Type INTEGER NOT NULL,Name TEXT NOT NULL,Location TEXT NOT NULL,NormalizedLocation TEXT NULL,Description TEXT NULL,CreatedAt TEXT NOT NULL,UpdatedAt TEXT NOT NULL,LastVerifiedAt TEXT NULL,AvailabilityState TEXT NULL,IsMissing INTEGER NOT NULL,Fingerprint TEXT NULL,FileSize INTEGER NULL,ModifiedAt TEXT NULL,WorkingDirectory TEXT NULL,ShellType TEXT NULL,RequiresApproval INTEGER NOT NULL,SortOrder INTEGER NOT NULL);
INSERT INTO Parcels VALUES('11111111-1111-1111-1111-111111111111','Old parcel','','Packed','2026-01-01T00:00:00.0000000Z','2026-01-01T00:00:00.0000000Z',NULL,NULL,NULL,NULL);
INSERT INTO ParcelItems VALUES('22222222-2222-2222-2222-222222222222','11111111-1111-1111-1111-111111111111',0,'old.txt','C:\old.txt','C:\OLD.TXT','legacy','2026-01-01T00:00:00.0000000Z','2026-01-01T00:00:00.0000000Z',NULL,'Unknown',1,NULL,12,NULL,NULL,NULL,0,0);"; await command.ExecuteNonQueryAsync();
        }
        var initializer = new DatabaseInitializer(new SqliteConnectionFactory(paths), new AppLogger(paths)); await initializer.InitializeAsync();
        await using var check = new SqliteConnection($"Data Source={paths.DatabasePath}"); await check.OpenAsync();
        var version = check.CreateCommand(); version.CommandText = "SELECT Value FROM SchemaInfo WHERE Key='SchemaVersion';";
        var parcelCount = check.CreateCommand(); parcelCount.CommandText = "SELECT COUNT(*) FROM Parcels;";
        var itemTable = check.CreateCommand(); itemTable.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='ParcelItems';";
        var migratedItems = check.CreateCommand(); migratedItems.CommandText = "SELECT ItemType || ':' || DisplayName FROM ParcelItems;";
        Assert.Equal(DatabaseInitializer.CurrentSchemaVersion.ToString(), (await version.ExecuteScalarAsync())?.ToString()); Assert.Equal(1L, await parcelCount.ExecuteScalarAsync()); Assert.Equal(1L, await itemTable.ExecuteScalarAsync()); Assert.Equal("File:old.txt", await migratedItems.ExecuteScalarAsync());
    }

    [Fact]
    public async Task LatestPartThreeSchemaMigratesOldBrowserRowsToWebLinksWithoutRecreatingParcelData()
    {
        using var temp = new TestFolder(); var paths = new AppDataPaths(temp.Path); paths.EnsureDirectories();
        await using (var connection = new SqliteConnection($"Data Source={paths.DatabasePath}"))
        {
            await connection.OpenAsync(); var command = connection.CreateCommand(); command.CommandText = @"
CREATE TABLE SchemaInfo(Key TEXT PRIMARY KEY, Value TEXT NOT NULL);
INSERT INTO SchemaInfo VALUES('SchemaVersion','3');
CREATE TABLE Parcels(Id TEXT PRIMARY KEY,Name TEXT NOT NULL,Description TEXT NOT NULL,State TEXT NOT NULL,CreatedAt TEXT NOT NULL,UpdatedAt TEXT NOT NULL,LastOpenedAt TEXT NULL,LastPackedAt TEXT NULL,ArchivedAt TEXT NULL,PreviousState TEXT NULL);
CREATE TABLE ParcelHistory(Id TEXT PRIMARY KEY,ParcelId TEXT NOT NULL,EventType TEXT NOT NULL,Summary TEXT NOT NULL,Timestamp TEXT NOT NULL,FOREIGN KEY(ParcelId) REFERENCES Parcels(Id) ON DELETE CASCADE);
CREATE TABLE TodayTasks(Id TEXT PRIMARY KEY,Text TEXT NOT NULL,IsCompleted INTEGER NOT NULL,CreatedAt TEXT NOT NULL,CompletedAt TEXT NULL,ParcelId TEXT NULL,SortOrder INTEGER NOT NULL);
CREATE TABLE Settings(Key TEXT PRIMARY KEY,Value TEXT NOT NULL);
CREATE TABLE ParcelItems(Id TEXT PRIMARY KEY,ParcelId TEXT NOT NULL,ItemType TEXT NOT NULL,DisplayName TEXT NOT NULL,Value TEXT NOT NULL,NormalizedIdentity TEXT NULL,SecondaryDetail TEXT NULL,CreatedAt TEXT NOT NULL,UpdatedAt TEXT NOT NULL,LastVerifiedAt TEXT NULL,SortOrder INTEGER NOT NULL,IsMissing INTEGER NOT NULL,IsInaccessible INTEGER NOT NULL,HasChanged INTEGER NOT NULL,ExecutablePath TEXT NULL,LaunchArguments TEXT NULL,WorkingDirectory TEXT NULL,WindowTitle TEXT NULL,ProcessName TEXT NULL,ApplicationUserModelId TEXT NULL,FileSize INTEGER NULL,FileModifiedAt TEXT NULL,Fingerprint TEXT NULL,IconCacheKey TEXT NULL,NoteContent TEXT NULL,LaunchEnabled INTEGER NOT NULL,CloseSupported INTEGER NOT NULL,FOREIGN KEY(ParcelId) REFERENCES Parcels(Id) ON DELETE CASCADE);
INSERT INTO Parcels VALUES('11111111-1111-1111-1111-111111111111','Part 3 parcel','','Packed','2026-01-01T00:00:00.0000000Z','2026-01-01T00:00:00.0000000Z',NULL,NULL,NULL,NULL);
INSERT INTO ParcelItems(Id,ParcelId,ItemType,DisplayName,Value,NormalizedIdentity,SecondaryDetail,CreatedAt,UpdatedAt,LastVerifiedAt,SortOrder,IsMissing,IsInaccessible,HasChanged,LaunchEnabled,CloseSupported) VALUES('22222222-2222-2222-2222-222222222222','11111111-1111-1111-1111-111111111111','BrowserTab','Docs','https://example.com/docs','chrome|window-0|https://example.com/docs','example.com','2026-01-01T00:00:00.0000000Z','2026-01-01T00:00:00.0000000Z',NULL,0,0,0,0,1,0);"; await command.ExecuteNonQueryAsync();
        }
        var initializer = new DatabaseInitializer(new SqliteConnectionFactory(paths), new AppLogger(paths)); await initializer.InitializeAsync();
        await using var check = new SqliteConnection($"Data Source={paths.DatabasePath}"); await check.OpenAsync();
        var version = check.CreateCommand(); version.CommandText = "SELECT Value FROM SchemaInfo WHERE Key='SchemaVersion';";
        var parcelName = check.CreateCommand(); parcelName.CommandText = "SELECT Name FROM Parcels WHERE Id='11111111-1111-1111-1111-111111111111';";
        var itemType = check.CreateCommand(); itemType.CommandText = "SELECT ItemType FROM ParcelItems WHERE Id='22222222-2222-2222-2222-222222222222';";
        var itemName = check.CreateCommand(); itemName.CommandText = "SELECT DisplayName FROM ParcelItems WHERE Id='22222222-2222-2222-2222-222222222222';";
        var itemUrl = check.CreateCommand(); itemUrl.CommandText = "SELECT Value FROM ParcelItems WHERE Id='22222222-2222-2222-2222-222222222222';";
        var columns = check.CreateCommand(); columns.CommandText = "SELECT name FROM pragma_table_info('ParcelItems') WHERE name LIKE 'Browser%';";
        Assert.Equal(DatabaseInitializer.CurrentSchemaVersion.ToString(), await version.ExecuteScalarAsync()); Assert.Equal("Part 3 parcel", await parcelName.ExecuteScalarAsync()); Assert.Equal("WebLink", await itemType.ExecuteScalarAsync()); Assert.Equal("Docs", await itemName.ExecuteScalarAsync()); Assert.Equal("https://example.com/docs", await itemUrl.ExecuteScalarAsync()); Assert.Null(await columns.ExecuteScalarAsync());
        var restarted = await Store(paths.RootDirectory);
        var loaded = Assert.Single(Assert.Single(restarted.Parcels).Items);
        Assert.Equal(ParcelItemType.WebLink, loaded.ItemType);
        Assert.Equal("https://example.com/docs", loaded.Value);
    }

    [Fact]
    public async Task EverySupportedItemTypePersistsAndCountsSurviveRestart()
    {
        using var temp = new TestFolder(); var store = await Store(temp.Path); var source = temp.File("brief.txt", "alpha"); var folder = temp.Folder("docs"); var app = temp.File("tiny.exe", "not really launched"); var factory = new ParcelItemFactory();
        var items = new List<ParcelItem>
        {
            await factory.FileAsync(Guid.Empty, source, 0), factory.Folder(Guid.Empty, folder, 1), factory.Application(Guid.Empty, app, 2),
            factory.WebLink(Guid.Empty, "https://example.com/a?q=1", "Example", null, 3), factory.Note(Guid.Empty, "Remember", "Do the careful thing", 4),
            new() { ParcelId=Guid.Empty, ItemType=ParcelItemType.ApplicationWindow, DisplayName="Disposable window", Value=app, NormalizedIdentity="window|disposable", ExecutablePath=app, WindowTitle="Disposable window", WindowClassName="TestWindow", ProcessName="tiny", CreatedAt=DateTime.Now, UpdatedAt=DateTime.Now, SortOrder=5, CloseSupported=true }
        };
        var parcel = await store.CreateWithItemsAsync("Real setup", "all types", items); Assert.Equal(6, parcel.ItemCount); Assert.Contains("2 APPS", parcel.ItemSummary);
        var restarted = await Store(temp.Path); var loaded = Assert.Single(restarted.Parcels); Assert.Equal(6, loaded.ItemCount); Assert.Equal(6, loaded.Items.Select(item => item.ItemType).Distinct().Count()); Assert.Equal("Do the careful thing", loaded.Items.Single(item => item.ItemType == ParcelItemType.Note).NoteContent); Assert.Equal("TestWindow", loaded.Items.Single(item => item.ItemType == ParcelItemType.ApplicationWindow).WindowClassName);
    }

    [Fact]
    public async Task DuplicatePathsAndUrlsAreBlockedInsideOneParcel()
    {
        using var temp = new TestFolder(); var store = await Store(temp.Path); var parcel = await store.CreateEmptyAsync("Duplicates"); var file = temp.File("same.txt", "one"); var folder = temp.Folder("same-folder"); var app = temp.File("same.exe", "x"); var factory = new ParcelItemFactory();
        await store.AddItemsAsync(parcel, new[] { await factory.FileAsync(parcel.Id, file, 0), factory.Folder(parcel.Id, folder, 1), factory.Application(parcel.Id, app, 2), factory.WebLink(parcel.Id, "https://example.com/path", null, null, 3) });
        var duplicateFile = await factory.FileAsync(parcel.Id, file.ToUpperInvariant(), 4);
        await Assert.ThrowsAsync<DuplicateParcelItemException>(() => store.AddItemsAsync(parcel, new[] { duplicateFile }));
        await Assert.ThrowsAsync<DuplicateParcelItemException>(() => store.AddItemsAsync(parcel, new[] { factory.Folder(parcel.Id, folder + "\\", 4) }));
        await Assert.ThrowsAsync<DuplicateParcelItemException>(() => store.AddItemsAsync(parcel, new[] { factory.Application(parcel.Id, app.ToUpperInvariant(), 4) }));
        var storedLink = parcel.Items.Single(item => item.ItemType == ParcelItemType.WebLink);
        var duplicateLink = factory.WebLink(parcel.Id, "HTTPS://EXAMPLE.COM/path", null, null, 4);
        Assert.Equal(storedLink.NormalizedIdentity, duplicateLink.NormalizedIdentity);
        await Assert.ThrowsAsync<DuplicateParcelItemException>(() => store.AddItemsAsync(parcel, new[] { duplicateLink }));
    }

    [Fact]
    public void WindowsPathsAndUncPathsCompareCaseInsensitivelyWithoutMergingShares()
    {
        Assert.True(WindowsPathIdentity.Same(@"C:\Work\Thing\", @"c:/work/thing"));
        Assert.True(WindowsPathIdentity.Same(@"\\server\share\Folder", @"\\SERVER\SHARE\folder\"));
        Assert.False(WindowsPathIdentity.Same(@"\\server\share-a\Folder", @"\\server\share-b\Folder"));
        Assert.Null(WindowsPathIdentity.Normalize("\0bad"));
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("data:text/plain,hi")]
    [InlineData("file:///C:/secret.txt")]
    [InlineData("shell:AppsFolder")]
    [InlineData("not a url")]
    public void DangerousOrNonWebUrlsAreRejected(string value) => Assert.False(WebLinkRules.TryNormalize(value, out _));

    [Fact]
    public void MultipleLinksReportInvalidLinesAndRemoveExactDuplicates()
    {
        var result = WebLinkRules.ParseMany("https://example.com\n\njavascript:bad\nHTTPS://EXAMPLE.COM/\nhttp://openai.com/a");
        Assert.Equal(2, result.Valid.Count); Assert.Single(result.Invalid); Assert.Equal("javascript:bad", result.Invalid[0]);
    }

    [Fact]
    public void UrlHostIsCaseInsensitiveButPathMeaningIsPreserved()
    {
        var factory = new ParcelItemFactory(); var upperHost = factory.WebLink(Guid.NewGuid(), "HTTPS://EXAMPLE.COM/Case", null, null, 0); var same = factory.WebLink(Guid.NewGuid(), "https://example.com/Case", null, null, 1); var differentPath = factory.WebLink(Guid.NewGuid(), "https://example.com/case", null, null, 2);
        Assert.True(ParcelItemIdentity.IsDuplicate(new[] { upperHost }, same)); Assert.False(ParcelItemIdentity.IsDuplicate(new[] { upperHost }, differentPath));
    }

    [Fact]
    public async Task FileVerificationDetectsChangedThenMissingFile()
    {
        using var temp = new TestFolder(); var path = temp.File("changing.txt", "first"); var item = await new ParcelItemFactory().FileAsync(Guid.NewGuid(), path, 0); var verifier = new ItemAvailabilityService();
        await File.WriteAllTextAsync(path, "second version"); await verifier.VerifyAsync(item); Assert.True(item.HasChanged); Assert.False(item.IsMissing);
        File.Delete(path); await verifier.VerifyAsync(item); Assert.True(item.IsMissing); Assert.False(item.HasChanged);
    }

    [Fact]
    public async Task RemovingItemAndDeletingParcelNeverDeletesExternalResourceAndRowsCascade()
    {
        using var temp = new TestFolder(); var path = temp.File("keep.txt", "keep me"); var store = await Store(temp.Path); var parcel = await store.CreateEmptyAsync("Safe records"); var item = await new ParcelItemFactory().FileAsync(parcel.Id, path, 0);
        await store.AddItemsAsync(parcel, new[] { item }); await store.RemoveItemAsync(parcel, item); Assert.True(File.Exists(path)); Assert.Empty(parcel.Items);
        await store.AddItemsAsync(parcel, new[] { await new ParcelItemFactory().FileAsync(parcel.Id, path, 0) }); await store.DeleteAsync(parcel); Assert.True(File.Exists(path));
        await using var db = new SqliteConnection($"Data Source={temp.Path}\\Data\\workparcel.db"); await db.OpenAsync(); var count = db.CreateCommand(); count.CommandText = "SELECT COUNT(*) FROM ParcelItems;"; Assert.Equal(0L, await count.ExecuteScalarAsync());
    }

    [Fact]
    public async Task ItemRemovalUsesStableIdUpdatesAllLiveStatesAndSurvivesRestart()
    {
        using var temp = new TestFolder();
        var path = temp.File("stable-id.txt", "keep the original");
        var store = await Store(temp.Path);
        var parcel = await store.CreateEmptyAsync("Stable ID removal");
        var item = await new ParcelItemFactory().FileAsync(parcel.Id, path, 0);
        await store.AddItemsAsync(parcel, new[] { item });

        var staleParcel = new Parcel { Id = parcel.Id, Name = parcel.Name, Description = parcel.Description, Status = parcel.Status, CreatedAt = parcel.CreatedAt, UpdatedAt = parcel.UpdatedAt };
        var staleItem = new ParcelItem { Id = item.Id, ParcelId = parcel.Id, ItemType = item.ItemType, DisplayName = item.DisplayName, Value = item.Value };
        staleParcel.Items.Add(staleItem);
        store.SetCurrent(staleParcel);

        var countNotifications = 0;
        var itemSummaryNotifications = 0;
        parcel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(Parcel.ItemCount)) countNotifications++;
            if (args.PropertyName == nameof(Parcel.ItemSummary)) itemSummaryNotifications++;
        };

        var result = await store.RemoveItemAsync(staleParcel, staleItem);

        Assert.True(result);
        Assert.Empty(parcel.Items);
        Assert.Empty(staleParcel.Items);
        Assert.Equal(0, parcel.ItemCount);
        Assert.True(countNotifications > 0);
        Assert.True(itemSummaryNotifications > 0);
        Assert.True(File.Exists(path));
        Assert.Single(parcel.History, entry => entry.EventType == ParcelHistoryEventType.ItemRemoved);
        Assert.Empty(ParcelItemQueries.Apply(parcel.Items, new ParcelItemQuery(null, ParcelItemGroup.Files, ParcelItemSort.Name)));

        await using (var db = new SqliteConnection($"Data Source={temp.Path}\\Data\\workparcel.db"))
        {
            await db.OpenAsync();
            var rowCount = db.CreateCommand(); rowCount.CommandText = "SELECT COUNT(*) FROM ParcelItems WHERE Id=$id;"; rowCount.Parameters.AddWithValue("$id", item.Id.ToString());
            var historyCount = db.CreateCommand(); historyCount.CommandText = "SELECT COUNT(*) FROM ParcelHistory WHERE ParcelId=$parcel AND EventType='ItemRemoved';"; historyCount.Parameters.AddWithValue("$parcel", parcel.Id.ToString());
            Assert.Equal(0L, await rowCount.ExecuteScalarAsync());
            Assert.Equal(1L, await historyCount.ExecuteScalarAsync());
        }

        var restarted = await Store(temp.Path);
        var loaded = Assert.Single(restarted.Parcels);
        Assert.Empty(loaded.Items);
        Assert.Single(loaded.History, entry => entry.EventType == ParcelHistoryEventType.ItemRemoved);
    }

    [Fact]
    public async Task RemovingFolderRecordLeavesExternalFolderAndStaysDeletedAfterRestart()
    {
        using var temp = new TestFolder();
        var folderPath = temp.Folder("keep-folder");
        var markerPath = temp.File("keep-folder-marker.txt", "original folder remains");
        File.Move(markerPath, Path.Combine(folderPath, "marker.txt"));
        var store = await Store(temp.Path);
        var parcel = await store.CreateEmptyAsync("Folder removal");
        var item = new ParcelItemFactory().Folder(parcel.Id, folderPath, 0);
        await store.AddItemsAsync(parcel, new[] { item });

        Assert.True(await store.RemoveItemAsync(parcel, new ParcelItem { Id = item.Id, ParcelId = parcel.Id, ItemType = ParcelItemType.Folder }));

        Assert.True(Directory.Exists(folderPath));
        Assert.Equal("original folder remains", await File.ReadAllTextAsync(Path.Combine(folderPath, "marker.txt")));
        await using (var db = new SqliteConnection($"Data Source={temp.Path}\\Data\\workparcel.db"))
        {
            await db.OpenAsync();
            var rowCount = db.CreateCommand(); rowCount.CommandText = "SELECT COUNT(*) FROM ParcelItems WHERE Id=$id;"; rowCount.Parameters.AddWithValue("$id", item.Id.ToString());
            Assert.Equal(0L, await rowCount.ExecuteScalarAsync());
        }

        var restarted = await Store(temp.Path);
        Assert.Empty(Assert.Single(restarted.Parcels).Items);
    }

    [Fact]
    public async Task RemovingApplicationRecordLeavesExternalApplicationAndStaysDeletedAfterRestart()
    {
        using var temp = new TestFolder();
        var applicationPath = temp.File("keep-application.exe", "not launched or deleted");
        var store = await Store(temp.Path);
        var parcel = await store.CreateEmptyAsync("Application removal");
        var item = new ParcelItemFactory().Application(parcel.Id, applicationPath, 0);
        await store.AddItemsAsync(parcel, new[] { item });

        Assert.True(await store.RemoveItemAsync(parcel, new ParcelItem { Id = item.Id, ParcelId = parcel.Id, ItemType = ParcelItemType.Application }));

        Assert.True(File.Exists(applicationPath));
        Assert.Equal("not launched or deleted", await File.ReadAllTextAsync(applicationPath));
        await using (var db = new SqliteConnection($"Data Source={temp.Path}\\Data\\workparcel.db"))
        {
            await db.OpenAsync();
            var rowCount = db.CreateCommand(); rowCount.CommandText = "SELECT COUNT(*) FROM ParcelItems WHERE Id=$id;"; rowCount.Parameters.AddWithValue("$id", item.Id.ToString());
            Assert.Equal(0L, await rowCount.ExecuteScalarAsync());
        }

        var restarted = await Store(temp.Path);
        Assert.Empty(Assert.Single(restarted.Parcels).Items);
    }

    [Fact]
    public async Task RemovingWebLinkRecordDoesNotOpenOrChangeTheUrlAndStaysDeletedAfterRestart()
    {
        using var temp = new TestFolder();
        var url = "https://example.com/workparcel-disposable-link";
        var store = await Store(temp.Path);
        var parcel = await store.CreateEmptyAsync("Web link removal");
        var item = new ParcelItemFactory().WebLink(parcel.Id, url, "Disposable link", "external reference", 0);
        await store.AddItemsAsync(parcel, new[] { item });

        Assert.True(await store.RemoveItemAsync(parcel, new ParcelItem { Id = item.Id, ParcelId = parcel.Id, ItemType = ParcelItemType.WebLink }));

        Assert.Equal(0, parcel.ItemCount);
        await using (var db = new SqliteConnection($"Data Source={temp.Path}\\Data\\workparcel.db"))
        {
            await db.OpenAsync();
            var rowCount = db.CreateCommand(); rowCount.CommandText = "SELECT COUNT(*) FROM ParcelItems WHERE Id=$id OR Value=$url;"; rowCount.Parameters.AddWithValue("$id", item.Id.ToString()); rowCount.Parameters.AddWithValue("$url", url);
            Assert.Equal(0L, await rowCount.ExecuteScalarAsync());
        }

        var restarted = await Store(temp.Path);
        Assert.Empty(Assert.Single(restarted.Parcels).Items);
    }


    [Fact]
    public async Task RepositoryReportsAffectedRowsAndFailedRemovalLeavesStateAndHistoryUnchanged()
    {
        using var temp = new TestFolder();
        var store = await Store(temp.Path);
        var parcel = await store.CreateEmptyAsync("Affected rows");
        var item = new ParcelItemFactory().Note(parcel.Id, "Retry me", "saved note", 0);
        await store.AddItemsAsync(parcel, new[] { item });

        await using (var db = new SqliteConnection($"Data Source={temp.Path}\\Data\\workparcel.db"))
        {
            await db.OpenAsync();
            var delete = db.CreateCommand(); delete.CommandText = "DELETE FROM ParcelItems WHERE Id=$id;"; delete.Parameters.AddWithValue("$id", item.Id.ToString());
            Assert.Equal(1, await delete.ExecuteNonQueryAsync());
        }

        var result = await store.RemoveItemAsync(parcel, item);

        Assert.False(result);
        Assert.Single(parcel.Items);
        Assert.Equal(1, parcel.ItemCount);
        Assert.DoesNotContain(parcel.History, entry => entry.EventType == ParcelHistoryEventType.ItemRemoved);
        var restarted = await Store(temp.Path);
        Assert.Empty(Assert.Single(restarted.Parcels).Items);
        Assert.DoesNotContain(restarted.History, entry => entry.EventType == ParcelHistoryEventType.ItemRemoved);
    }

    [Fact]
    public async Task RepositoryDeleteReturnsTrueOnlyAfterOneRowIsDeleted()
    {
        using var temp = new TestFolder();
        var paths = new AppDataPaths(temp.Path);
        var factory = new SqliteConnectionFactory(paths);
        await new DatabaseInitializer(factory, new AppLogger(paths)).InitializeAsync();
        var repository = new WorkspaceRepository(factory, paths);
        var now = DateTime.Now;
        var parcel = new Parcel { Name = "Repository result", Status = ParcelStatus.Packed, CreatedAt = now, UpdatedAt = now };
        await repository.InsertParcelWithHistoryAsync(parcel, new ParcelHistoryEntry { ParcelId = parcel.Id, EventType = ParcelHistoryEventType.Created, Summary = "Parcel created", Timestamp = now });
        var item = new ParcelItem { ParcelId = parcel.Id, ItemType = ParcelItemType.Note, DisplayName = "Record", Value = string.Empty, NoteContent = "saved", CreatedAt = now, UpdatedAt = now, SortOrder = 0, LaunchEnabled = false };
        await repository.InsertItemsWithHistoryAsync(new[] { item }, new ParcelHistoryEntry { ParcelId = parcel.Id, EventType = ParcelHistoryEventType.ItemAdded, Summary = "Note added", Timestamp = now.AddTicks(1) });

        Assert.True(await repository.DeleteItemWithHistoryAsync(item.Id, new ParcelHistoryEntry { ParcelId = parcel.Id, EventType = ParcelHistoryEventType.ItemRemoved, Summary = "Note removed", Timestamp = now.AddTicks(2) }));
        Assert.False(await repository.DeleteItemWithHistoryAsync(item.Id, new ParcelHistoryEntry { ParcelId = parcel.Id, EventType = ParcelHistoryEventType.ItemRemoved, Summary = "Should not be recorded", Timestamp = now.AddTicks(3) }));

        var snapshot = await repository.LoadSnapshotAsync();
        Assert.Empty(snapshot.Items);
        Assert.Single(snapshot.History, entry => entry.EventType == ParcelHistoryEventType.ItemRemoved);
    }

    [Fact]
    public async Task BulkRemovalRollsBackWhenAnyStableIdWasNotDeleted()
    {
        using var temp = new TestFolder();
        var store = await Store(temp.Path);
        var parcel = await store.CreateEmptyAsync("Atomic bulk removal");
        var first = new ParcelItemFactory().Note(parcel.Id, "First", "first", 0);
        var second = new ParcelItemFactory().Note(parcel.Id, "Second", "second", 1);
        await store.AddItemsAsync(parcel, new[] { first, second });

        var deleted = await store.RemoveItemsAsync(parcel, new[] { first, new ParcelItem { Id = Guid.NewGuid(), ParcelId = parcel.Id, ItemType = ParcelItemType.Note } });

        Assert.Equal(0, deleted);
        Assert.Equal(2, parcel.ItemCount);
        Assert.Contains(parcel.Items, item => item.Id == first.Id);
        Assert.Contains(parcel.Items, item => item.Id == second.Id);
        Assert.DoesNotContain(parcel.History, entry => entry.EventType == ParcelHistoryEventType.ItemRemoved);
        var restarted = await Store(temp.Path);
        Assert.Equal(2, Assert.Single(restarted.Parcels).ItemCount);
    }

    [Fact]
    public async Task RemovingLastItemUpdatesFilteredAndGroupedStateImmediately()
    {
        using var temp = new TestFolder();
        var store = await Store(temp.Path);
        var parcel = await store.CreateEmptyAsync("Views");
        var first = new ParcelItemFactory().Note(parcel.Id, "Same title", "first", 0);
        var second = new ParcelItemFactory().Note(parcel.Id, "Same title", "second", 1);
        await store.AddItemsAsync(parcel, new[] { first, second });

        var removed = await store.RemoveItemAsync(parcel, new ParcelItem { Id = first.Id, ParcelId = parcel.Id, ItemType = first.ItemType, DisplayName = first.DisplayName });

        Assert.True(removed);
        Assert.Single(parcel.Items);
        Assert.Equal(second.Id, parcel.Items[0].Id);
        Assert.Equal(1, parcel.ItemCount);
        Assert.Single(ParcelItemQueries.Apply(parcel.Items, new ParcelItemQuery("Same title", ParcelItemGroup.Notes, ParcelItemSort.Name)));
        Assert.Single(parcel.Items.GroupBy(item => item.ItemType));

        Assert.True(await store.RemoveItemAsync(parcel, new ParcelItem { Id = second.Id, ParcelId = parcel.Id, ItemType = second.ItemType }));
        Assert.Empty(parcel.Items);
        Assert.Equal(0, parcel.ItemCount);
        Assert.Empty(ParcelItemQueries.Apply(parcel.Items, new ParcelItemQuery("Same title", ParcelItemGroup.Notes, ParcelItemSort.Name)));
        Assert.Empty(parcel.Items.GroupBy(item => item.ItemType));
    }

    [Fact]
    public async Task ConcurrentRemovalAttemptsCreateOneOperationAndOneActivityEntry()
    {
        using var temp = new TestFolder();
        var store = await Store(temp.Path);
        var parcel = await store.CreateEmptyAsync("One operation");
        var item = new ParcelItemFactory().Note(parcel.Id, "Only once", "saved note", 0);
        await store.AddItemsAsync(parcel, new[] { item });

        var attempts = await Task.WhenAll(
            store.RemoveItemAsync(parcel, new ParcelItem { Id = item.Id, ParcelId = parcel.Id, ItemType = item.ItemType }),
            store.RemoveItemAsync(parcel, new ParcelItem { Id = item.Id, ParcelId = parcel.Id, ItemType = item.ItemType }));

        Assert.Equal(1, attempts.Count(result => result));
        Assert.Equal(1, attempts.Count(result => !result));
        Assert.Empty(parcel.Items);
        Assert.Single(parcel.History, entry => entry.EventType == ParcelHistoryEventType.ItemRemoved);

        await using var db = new SqliteConnection($"Data Source={temp.Path}\\Data\\workparcel.db");
        await db.OpenAsync();
        var historyCount = db.CreateCommand();
        historyCount.CommandText = "SELECT COUNT(*) FROM ParcelHistory WHERE ParcelId=$parcel AND EventType='ItemRemoved';";
        historyCount.Parameters.AddWithValue("$parcel", parcel.Id.ToString());
        Assert.Equal(1L, await historyCount.ExecuteScalarAsync());
    }

    [Fact]
    public void WindowFilteringExcludesOwnProcessToolWindowsAndSystemNoise()
    {
        Assert.False(OpenWindowService.ShouldInclude(new WindowCandidate(1, 42, "WorkParcel", "WorkParcel.App", null, true, false), 42));
        Assert.False(OpenWindowService.ShouldInclude(new WindowCandidate(2, 7, "Search", "SearchHost", null, true, false), 42));
        Assert.False(OpenWindowService.ShouldInclude(new WindowCandidate(3, 9, "Tool", "tool", null, true, true), 42));
        Assert.True(OpenWindowService.ShouldInclude(new WindowCandidate(5, 12, "Chrome", "chrome", @"C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe", true, false), 42));
        Assert.True(OpenWindowService.ShouldInclude(new WindowCandidate(6, 13, "Edge", "msedge", @"C:\\Program Files (x86)\\Microsoft\\Edge\\Application\\msedge.exe", true, false), 42));
        Assert.True(OpenWindowService.ShouldInclude(new WindowCandidate(4, 11, "notes.txt - Notepad", "notepad", @"C:\Windows\notepad.exe", true, false), 42));
    }

    [Fact]
    public void WindowIdentityMatchingRequiresOneUniqueExactIdentity()
    {
        var saved = new ParcelItem
        {
            ItemType = ParcelItemType.ApplicationWindow,
            DisplayName = "notes.txt - Notepad",
            WindowTitle = "notes.txt - Notepad",
            ProcessName = "notepad",
            ExecutablePath = @"C:\Windows\notepad.exe",
            WindowClassName = "Notepad"
        };
        var exact = new WindowCandidate(5, 11, " notes.txt - Notepad ", "notepad", @"c:\windows\NOTEPAD.EXE", true, false, "Notepad");
        var wrongTitle = new WindowCandidate(6, 12, "other.txt - Notepad", "notepad", @"C:\Windows\notepad.exe", true, false, "Notepad");
        Assert.Same(exact, Assert.Single(OpenWindowService.MatchSavedItemsByIdentity(new[] { saved }, new[] { exact, wrongTitle })).Live);

        var duplicate = new WindowCandidate(7, 13, "notes.txt - Notepad", "notepad", @"C:\Windows\notepad.exe", true, false, "Notepad");
        Assert.Null(Assert.Single(OpenWindowService.MatchSavedItemsByIdentity(new[] { saved }, new[] { exact, duplicate })).Live);
    }

    [Fact]
    public void OpeningPlanSkipsUnavailableItemsAndSuppressesDuplicateAppLaunches()
    {
        var first = new ParcelItem { ItemType = ParcelItemType.ApplicationWindow, DisplayName = "First", ExecutablePath = @"C:\Apps\editor.exe", Value = @"C:\Apps\editor.exe", LaunchEnabled = true };
        var second = new ParcelItem { ItemType = ParcelItemType.ApplicationWindow, DisplayName = "Second", ExecutablePath = @"c:\apps\EDITOR.exe", Value = @"c:\apps\EDITOR.exe", LaunchEnabled = true };
        var missing = new ParcelItem { ItemType = ParcelItemType.File, DisplayName = "Missing", Value = @"C:\gone.txt", LaunchEnabled = true, IsMissing = true };
        var link = new ParcelItem { ItemType = ParcelItemType.WebLink, DisplayName = "Docs", Value = "https://example.com", LaunchEnabled = true };
        var note = new ParcelItem { ItemType = ParcelItemType.Note, DisplayName = "Note", LaunchEnabled = false };
        var plan = new ItemLaunchService().BuildPlan(new[] { first, second, missing, link, note });
        Assert.Equal(3, plan.Count); Assert.Contains(first, plan); Assert.Contains(second, plan); Assert.Contains(link, plan);
    }

    [Fact]
    public void ItemSearchFilterSortAndResultAggregationAreDeterministic()
    {
        var now = DateTime.Now; var items = new[]
        {
            new ParcelItem { ItemType=ParcelItemType.File, DisplayName="Zeta notes.txt", SecondaryDetail="client", SortOrder=0, CreatedAt=now },
            new ParcelItem { ItemType=ParcelItemType.WebLink, DisplayName="Alpha docs", SecondaryDetail="reference", SortOrder=1, CreatedAt=now },
            new ParcelItem { ItemType=ParcelItemType.File, DisplayName="Beta.txt", SecondaryDetail="other", SortOrder=2, CreatedAt=now }
        };
        var files = ParcelItemQueries.Apply(items, new ParcelItemQuery("txt", ParcelItemGroup.Files, ParcelItemSort.Name));
        Assert.Equal(new[] { "Beta.txt", "Zeta notes.txt" }, files.Select(item => item.DisplayName));
        var summary = ItemLaunchService.Summarize(new[] { new ItemOpenResult(Guid.NewGuid(), ItemOpenStatus.Opened, "ok"), new ItemOpenResult(Guid.NewGuid(), ItemOpenStatus.Failed, "no"), new ItemOpenResult(Guid.NewGuid(), ItemOpenStatus.Opened, "ok") });
        Assert.Equal(2, summary[ItemOpenStatus.Opened]); Assert.Equal(1, summary[ItemOpenStatus.Failed]);
    }

    [Fact]
    public async Task MissingFolderAndApplicationAreDetectedAndRelinkKeepsStableIdentity()
    {
        using var temp = new TestFolder(); var folder = temp.Folder("old-folder"); var appPath = temp.File("old.exe", "x"); var factory = new ParcelItemFactory(); var folderItem = factory.Folder(Guid.NewGuid(), folder, 0); var appItem = factory.Application(Guid.NewGuid(), appPath, 1); Directory.Delete(folder); File.Delete(appPath); var verifier = new ItemAvailabilityService();
        await verifier.VerifyAsync(folderItem); await verifier.VerifyAsync(appItem); Assert.True(folderItem.IsMissing); Assert.True(appItem.IsMissing);
        var newFolder = temp.Folder("new-folder"); var replacement = factory.Folder(folderItem.ParcelId, newFolder, 0); var stableId = folderItem.Id; ParcelItemRelinker.Apply(folderItem, replacement);
        Assert.Equal(stableId, folderItem.Id); Assert.Equal(newFolder, folderItem.Value); Assert.False(folderItem.IsMissing);
    }

    [Fact]
    public async Task ItemInsertTransactionRollsBackWhenDatabaseIdentityConstraintFails()
    {
        using var temp = new TestFolder(); var paths = new AppDataPaths(temp.Path); var factory = new SqliteConnectionFactory(paths); await new DatabaseInitializer(factory, new AppLogger(paths)).InitializeAsync(); var repository = new WorkspaceRepository(factory, paths);
        var now = DateTime.Now; var parcel = new Parcel { Name="Atomic items", Status=ParcelStatus.Packed, CreatedAt=now, UpdatedAt=now }; var created = new ParcelHistoryEntry { ParcelId=parcel.Id, EventType=ParcelHistoryEventType.Created, Summary="Parcel created", Timestamp=now }; await repository.InsertParcelWithHistoryAsync(parcel, created);
        var first = new ParcelItem { ParcelId=parcel.Id, ItemType=ParcelItemType.File, DisplayName="one", Value="C:\\same.txt", NormalizedIdentity="C:\\SAME.TXT", CreatedAt=now, UpdatedAt=now };
        var second = new ParcelItem { ParcelId=parcel.Id, ItemType=ParcelItemType.File, DisplayName="two", Value="c:\\same.txt", NormalizedIdentity="C:\\SAME.TXT", CreatedAt=now, UpdatedAt=now };
        var history = new ParcelHistoryEntry { ParcelId=parcel.Id, EventType=ParcelHistoryEventType.ItemsCaptured, Summary="two items", Timestamp=now };
        await Assert.ThrowsAsync<SqliteException>(() => repository.InsertItemsWithHistoryAsync(new[] { first, second }, history)); var snapshot = await repository.LoadSnapshotAsync(); Assert.Empty(snapshot.Items); Assert.Single(snapshot.History);
    }

    [Fact]
    public async Task ReplacingPackSelectionPersistsAddsAndRemovalsAcrossRestart()
    {
        using var temp = new TestFolder(); var store = await Store(temp.Path); var factory = new ParcelItemFactory(); var firstPath = temp.File("first.txt", "1"); var secondPath = temp.File("second.txt", "2"); var parcel = await store.CreateWithItemsAsync("Pack", "", new[] { await factory.FileAsync(Guid.Empty, firstPath, 0), await factory.FileAsync(Guid.Empty, secondPath, 1) });
        var keep = parcel.Items[1]; var link = factory.WebLink(parcel.Id, "https://example.com/new", null, null, 2); await store.ReplaceItemsAsync(parcel, new[] { keep, link }, "Parcel packed - 2 items saved");
        var restarted = await Store(temp.Path); var loaded = Assert.Single(restarted.Parcels); Assert.Equal(2, loaded.ItemCount); Assert.DoesNotContain(loaded.Items, item => item.Value == firstPath); Assert.Contains(loaded.Items, item => item.ItemType == ParcelItemType.WebLink);
    }

    [Fact]
    public async Task ReplacingPackSelectionUpdatesEveryLiveParcelStateAfterCommit()
    {
        using var temp = new TestFolder();
        var store = await Store(temp.Path);
        var factory = new ParcelItemFactory();
        var firstPath = temp.File("first-live.txt", "1");
        var secondPath = temp.File("second-live.txt", "2");
        var parcel = await store.CreateWithItemsAsync("Shared pack state", "", new[] { await factory.FileAsync(Guid.Empty, firstPath, 0), await factory.FileAsync(Guid.Empty, secondPath, 1) });
        var stale = new Parcel { Id = parcel.Id, Name = parcel.Name, Description = parcel.Description, Status = ParcelStatus.Open, CreatedAt = parcel.CreatedAt, UpdatedAt = parcel.UpdatedAt };
        foreach (var item in parcel.Items) stale.Items.Add(new ParcelItem { Id = item.Id, ParcelId = parcel.Id, ItemType = item.ItemType, DisplayName = item.DisplayName, Value = item.Value, NormalizedIdentity = item.NormalizedIdentity, CreatedAt = item.CreatedAt, UpdatedAt = item.UpdatedAt, SortOrder = item.SortOrder });
        store.SetCurrent(stale);

        await store.ReplaceItemsAsync(parcel, new[] { parcel.Items[1] }, "Pack one selected item");

        Assert.Equal(ParcelStatus.Packed, parcel.Status);
        Assert.Equal(new[] { parcel.Items.Single().Id }, stale.Items.Select(item => item.Id));
        Assert.Null(store.CurrentParcel);
        var restarted = await Store(temp.Path);
        Assert.Equal(new[] { parcel.Items.Single().Id }, Assert.Single(restarted.Parcels).Items.Select(item => item.Id));
    }

    [Fact]
    public void GracefulCloseCandidatesRequireRuntimeWindowAndNeverIncludeOrdinaryItems()
    {
        var window = new ParcelItem { ItemType=ParcelItemType.ApplicationWindow, CloseSupported=true, RuntimeWindowHandle=(nint)123 };
        var savedAfterRestart = new ParcelItem { ItemType=ParcelItemType.ApplicationWindow, CloseSupported=true, RuntimeWindowHandle=nint.Zero };
        var file = new ParcelItem { ItemType=ParcelItemType.File, CloseSupported=true, RuntimeWindowHandle=(nint)123 };
        Assert.True(WindowCloseRequestService.IsCloseCandidate(window)); Assert.False(WindowCloseRequestService.IsCloseCandidate(savedAfterRestart)); Assert.False(WindowCloseRequestService.IsCloseCandidate(file));
    }

    [Fact]
    public async Task GracefulClosePlanOnlyPostsToValidatedSelectedWindowHandles()
    {
        var safe = new ParcelItem { Id = Guid.NewGuid(), ItemType = ParcelItemType.ApplicationWindow, CloseSupported = true, RuntimeWindowHandle = (nint)1 };
        var unsafeWindow = new ParcelItem { Id = Guid.NewGuid(), ItemType = ParcelItemType.ApplicationWindow, CloseSupported = true, RuntimeWindowHandle = (nint)2 };
        var transport = new FakeCloseTransport((nint)1);
        var service = new WindowCloseRequestService(transport, TimeSpan.FromMilliseconds(1));
        Assert.Same(safe, Assert.Single(service.BuildPlan(new[] { safe, unsafeWindow })));
        var results = await service.RequestCloseAsync(new[] { safe, unsafeWindow });
        Assert.Equal(CloseRequestStatus.Closed, Assert.Single(results, result => result.ItemId == safe.Id).Status);
        Assert.Equal(CloseRequestStatus.Unsupported, Assert.Single(results, result => result.ItemId == unsafeWindow.Id).Status);
        Assert.Equal(new[] { (nint)1 }, transport.PostedHandles);
    }

    [Fact]
    public async Task AlreadyOpenApplicationIsReportedWithoutLaunchingAgain()
    {
        var saved = new ParcelItem { ItemType=ParcelItemType.Application, DisplayName="Editor", ExecutablePath=@"C:\Apps\editor.exe", Value=@"C:\Apps\editor.exe", LaunchEnabled=true };
        var detected = new ParcelItem { ItemType=ParcelItemType.ApplicationWindow, DisplayName="Editor window", ExecutablePath=@"c:\apps\EDITOR.exe", Value=@"c:\apps\EDITOR.exe", LaunchEnabled=true };
        var result = Assert.Single(await new ItemLaunchService().OpenAsync(new[] { saved }, new[] { detected })); Assert.Equal(ItemOpenStatus.AlreadyOpen, result.Status);
    }

    [Fact]
    public async Task ItemEditAndNoteContentPersistWithOneMeaningfulHistoryEntry()
    {
        using var temp = new TestFolder(); var store = await Store(temp.Path); var parcel = await store.CreateWithItemsAsync("Notes", "", new[] { new ParcelItemFactory().Note(Guid.Empty, "Draft", "first version", 0) }); var note = Assert.Single(parcel.Items);
        note.DisplayName = "Revised"; note.NoteContent = "second version"; await store.UpdateItemAsync(parcel, note);
        var restarted = await Store(temp.Path); var loaded = Assert.Single(restarted.Parcels); var saved = Assert.Single(loaded.Items); Assert.Equal("Revised", saved.DisplayName); Assert.Equal("second version", saved.NoteContent); Assert.Single(loaded.History, entry => entry.EventType == ParcelHistoryEventType.ItemEdited);
    }

    [Fact]
    public async Task SmallFileFingerprintIsSha256AndConfigurableThresholdSkipsLargeFile()
    {
        using var temp = new TestFolder(); var path = temp.File("fingerprint.txt", "known content"); var bytes = await File.ReadAllBytesAsync(path); var expected = Convert.ToHexString(SHA256.HashData(bytes));
        var hashed = await new FileMetadataService().ReadAsync(path); var skipped = await new FileMetadataService(2).ReadAsync(path);
        Assert.Equal(expected, hashed.Fingerprint); Assert.Null(skipped.Fingerprint); Assert.Equal(bytes.LongLength, hashed.Size);
    }

    [Fact]
    public async Task FailedPackWriteRestoresParcelAndItemStateInMemory()
    {
        using var temp = new TestFolder(); var store = await Store(temp.Path); var file = temp.File("rollback.txt", "safe"); var parcel = await store.CreateWithItemsAsync("Rollback", "", new[] { await new ParcelItemFactory().FileAsync(Guid.Empty, file, 0) }); await store.SetStateAsync(parcel, ParcelStatus.Open); var item = Assert.Single(parcel.Items); var oldUpdated = parcel.UpdatedAt; var oldPacked = parcel.LastPackedAt; var oldItemUpdated = item.UpdatedAt; var oldSort = item.SortOrder; using var canceled = new CancellationTokenSource(); canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.ReplaceItemsAsync(parcel, new[] { item }, "should roll back", cancellationToken: canceled.Token));
        Assert.Equal(ParcelStatus.Open, parcel.Status); Assert.Equal(oldUpdated, parcel.UpdatedAt); Assert.Equal(oldPacked, parcel.LastPackedAt); Assert.Equal(oldItemUpdated, item.UpdatedAt); Assert.Equal(oldSort, item.SortOrder);
    }

    [Fact]
    public void IconCachePathStaysInsideTheWorkParcelRoot()
    {
        using var temp = new TestFolder();
        var paths = new AppDataPaths(temp.Path);
        paths.EnsureDirectories();

        Assert.Equal(Path.Combine(temp.Path, "Cache", "Icons"), paths.IconCacheDirectory);
        Assert.StartsWith(Path.GetFullPath(temp.Path) + Path.DirectorySeparatorChar, Path.GetFullPath(paths.IconCacheDirectory), StringComparison.OrdinalIgnoreCase);
        Assert.True(Directory.Exists(paths.IconCacheDirectory));
    }

    [Fact]
    public async Task NewParcelsStayOpenAndRetainDataWhenObsoleteTablesExist()
    {
        using var temp = new TestFolder();
        var store = await Store(temp.Path);
        var item = new ParcelItem { ItemType = ParcelItemType.Note, DisplayName = "Keep this", Value = "Keep this", NoteContent = "Keep this", LaunchEnabled = false };
        var parcel = await store.CreateWithItemsAsync("Open setup", "", new[] { item });
        Assert.Equal(ParcelStatus.Open, parcel.Status);

        var oldSnapshotTable = string.Concat("D", "esk", "L", "ayout", "S", "napshots");
        var oldWindowTable = string.Concat("D", "esk", "W", "indow", "L", "ayout", "s");
        await using (var db = new SqliteConnection($"Data Source={temp.Path}\\Data\\workparcel.db"))
        {
            await db.OpenAsync();
            var count = db.CreateCommand();
            count.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name IN ($snapshot, $window);";
            count.Parameters.AddWithValue("$snapshot", oldSnapshotTable);
            count.Parameters.AddWithValue("$window", oldWindowTable);
            Assert.Equal(0L, await count.ExecuteScalarAsync());

            var legacyColumns = new[]
            {
                (Name: string.Concat("Browser", "Window", "Left"), SqlType: "REAL"),
                (Name: string.Concat("Browser", "Window", "Top"), SqlType: "REAL"),
                (Name: string.Concat("Browser", "Window", "Width"), SqlType: "REAL"),
                (Name: string.Concat("Browser", "Window", "Height"), SqlType: "REAL"),
                (Name: string.Concat("Browser", "Window", "State"), SqlType: "TEXT"),
                (Name: string.Concat("Browser", "Window", "Focused"), SqlType: "INTEGER"),
                (Name: string.Concat("Browser", "Window", "DpiX"), SqlType: "REAL"),
                (Name: string.Concat("Browser", "Window", "DpiY"), SqlType: "REAL")
            };
            var legacyColumnSql = string.Join(" ", legacyColumns.Select(column => $"ALTER TABLE ParcelItems ADD COLUMN [{column.Name}] {column.SqlType};"));
            var addLegacy = db.CreateCommand();
            addLegacy.CommandText = $"CREATE TABLE [{oldSnapshotTable}](Id TEXT PRIMARY KEY); CREATE TABLE [{oldWindowTable}](Id TEXT PRIMARY KEY); {legacyColumnSql} INSERT INTO ParcelHistory(Id, ParcelId, EventType, Summary, Timestamp) VALUES($id, $parcel, $eventType, $summary, $timestamp);";
            addLegacy.Parameters.AddWithValue("$id", Guid.NewGuid().ToString());
            addLegacy.Parameters.AddWithValue("$parcel", parcel.Id.ToString());
            addLegacy.Parameters.AddWithValue("$eventType", "HistoricalEventV0");
            addLegacy.Parameters.AddWithValue("$summary", "Legacy history retained");
            addLegacy.Parameters.AddWithValue("$timestamp", DateTime.UtcNow.ToString("O"));
            await addLegacy.ExecuteNonQueryAsync();
        }

        var restarted = await Store(temp.Path);
        var loaded = Assert.Single(restarted.Parcels);
        Assert.Equal(ParcelStatus.Open, loaded.Status);
        Assert.Equal("Keep this", Assert.Single(loaded.Items).NoteContent);
        Assert.Contains(loaded.History, entry => entry.Summary == "Legacy history retained" && entry.EventType == ParcelHistoryEventType.Updated);
    }

    private static async Task<WorkspaceStore> Store(string root) { var store = new WorkspaceStore(new AppDataPaths(root)); await store.InitializeAsync(); return store; }

    private sealed class TestFolder : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "WorkParcelTests", Guid.NewGuid().ToString("N"));
        public TestFolder() => Directory.CreateDirectory(Path);
        public string File(string name, string content) { var path = System.IO.Path.Combine(Path, name); System.IO.File.WriteAllText(path, content); return path; }
        public string Folder(string name) { var path = System.IO.Path.Combine(Path, name); Directory.CreateDirectory(path); return path; }
        public void Dispose() { try { Directory.Delete(Path, true); } catch { } }
    }

    private sealed class FakeCloseTransport(nint safeHandle) : IWindowCloseTransport
    {
        private readonly HashSet<nint> _live = new() { (nint)1, (nint)2 };
        public List<nint> PostedHandles { get; } = new();
        public bool IsWindow(nint handle) => _live.Contains(handle);
        public bool IsSafeTarget(ParcelItem item) => item.RuntimeWindowHandle == safeHandle;
        public bool PostClose(nint handle) { PostedHandles.Add(handle); _live.Remove(handle); return true; }
    }
}
