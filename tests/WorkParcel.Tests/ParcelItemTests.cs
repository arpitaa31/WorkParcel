using Microsoft.Data.Sqlite;
using System.Security.Cryptography;
using WorkParcel_App.Models;
using WorkParcel_App.Services;
using WorkParcel.Core.Browser;
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
    public async Task LatestPartThreeSchemaMigratesBrowserColumnsWithoutRecreatingParcelData()
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
        var browserDomain = check.CreateCommand(); browserDomain.CommandText = "SELECT BrowserDomain FROM ParcelItems WHERE Id='22222222-2222-2222-2222-222222222222';";
        Assert.Equal(DatabaseInitializer.CurrentSchemaVersion.ToString(), await version.ExecuteScalarAsync()); Assert.Equal("Part 3 parcel", await parcelName.ExecuteScalarAsync()); Assert.Equal("example.com", await browserDomain.ExecuteScalarAsync());
    }

    [Fact]
    public async Task EverySupportedItemTypePersistsAndCountsSurviveRestart()
    {
        using var temp = new TestFolder(); var store = await Store(temp.Path); var source = temp.File("brief.txt", "alpha"); var folder = temp.Folder("docs"); var app = temp.File("tiny.exe", "not really launched"); var factory = new ParcelItemFactory();
        var items = new List<ParcelItem>
        {
            await factory.FileAsync(Guid.Empty, source, 0), factory.Folder(Guid.Empty, folder, 1), factory.Application(Guid.Empty, app, 2),
            factory.WebLink(Guid.Empty, "https://example.com/a?q=1", "Example", null, 3), factory.Note(Guid.Empty, "Remember", "Do the careful thing", 4),
            new() { ParcelId=Guid.Empty, ItemType=ParcelItemType.ApplicationWindow, DisplayName="Disposable window", Value=app, NormalizedIdentity="window|disposable", ExecutablePath=app, WindowTitle="Disposable window", WindowClassName="TestWindow", ProcessName="tiny", CreatedAt=DateTime.Now, UpdatedAt=DateTime.Now, SortOrder=5, CloseSupported=true },
            new() { ParcelId=Guid.Empty, ItemType=ParcelItemType.BrowserTab, DisplayName="Docs tab", Value="https://example.com/docs", NormalizedIdentity="chrome|window-0|https://example.com/docs", BrowserFamily="chrome", BrowserDomain="example.com", BrowserWindowGroupId="window-0", BrowserTabIndex=0, BrowserCapturedAt=DateTime.Now, BrowserWindowLeft=120, BrowserWindowTop=80, BrowserWindowWidth=1400, BrowserWindowHeight=900, BrowserWindowState="normal", BrowserWindowFocused=true, BrowserWindowDpiX=144, BrowserWindowDpiY=144, CreatedAt=DateTime.Now, UpdatedAt=DateTime.Now, SortOrder=6 }
        };
        var parcel = await store.CreateWithItemsAsync("Real setup", "all types", items); Assert.Equal(7, parcel.ItemCount); Assert.Contains("2 APPS", parcel.ItemSummary);
        var restarted = await Store(temp.Path); var loaded = Assert.Single(restarted.Parcels); Assert.Equal(7, loaded.ItemCount); Assert.Equal(7, loaded.Items.Select(item => item.ItemType).Distinct().Count()); Assert.Equal("Do the careful thing", loaded.Items.Single(item => item.ItemType == ParcelItemType.Note).NoteContent); Assert.Equal("window-0", loaded.Items.Single(item => item.ItemType == ParcelItemType.BrowserTab).BrowserWindowGroupId); Assert.Equal("example.com", loaded.Items.Single(item => item.ItemType == ParcelItemType.BrowserTab).BrowserDomain); var loadedBrowser = loaded.Items.Single(item => item.ItemType == ParcelItemType.BrowserTab); Assert.Equal(120, loadedBrowser.BrowserWindowLeft); Assert.Equal(144, loadedBrowser.BrowserWindowDpiX); Assert.True(loadedBrowser.BrowserWindowFocused); Assert.Equal("TestWindow", loaded.Items.Single(item => item.ItemType == ParcelItemType.ApplicationWindow).WindowClassName);
    }

    [Fact]
    public async Task BrowserRuntimeIdsStayConnectionScopedAndDoNotPersist()
    {
        using var temp = new TestFolder(); var store = await Store(temp.Path); var now = DateTime.Now;
        var item = new ParcelItem
        {
            ParcelId = Guid.Empty, ItemType = ParcelItemType.BrowserTab, DisplayName = "Runtime tab", Value = "https://example.com/runtime",
            NormalizedIdentity = "chrome|window-0|https://example.com/runtime", BrowserFamily = "chrome", BrowserWindowGroupId = "window-0",
            BrowserSessionTabId = "temporary-tab-id", BrowserSessionWindowId = "temporary-window-id", BrowserConnectionId = "temporary-connection-id",
            CreatedAt = now, UpdatedAt = now, SortOrder = 0
        };
        var parcel = await store.CreateWithItemsAsync("Runtime identities", "", new[] { item });

        var restarted = await Store(temp.Path); var saved = Assert.Single(Assert.Single(restarted.Parcels).Items);
        Assert.Null(saved.BrowserSessionTabId); Assert.Null(saved.BrowserSessionWindowId); Assert.Null(saved.BrowserConnectionId);
        Assert.Equal("window-0", saved.BrowserWindowGroupId); Assert.Equal("https://example.com/runtime", saved.Value);
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
        await Assert.ThrowsAsync<DuplicateParcelItemException>(() => store.AddItemsAsync(parcel, new[] { factory.WebLink(parcel.Id, "HTTPS://EXAMPLE.COM/path", null, null, 4) }));
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
    public void WindowFilteringExcludesOwnProcessToolWindowsAndSystemNoise()
    {
        Assert.False(OpenWindowService.ShouldInclude(new WindowCandidate(1, 42, "WorkParcel", "WorkParcel.App", null, "WorkParcel", true, false), 42));
        Assert.False(OpenWindowService.ShouldInclude(new WindowCandidate(2, 7, "Search", "SearchHost", null, "Search", true, false), 42));
        Assert.False(OpenWindowService.ShouldInclude(new WindowCandidate(3, 9, "Tool", "tool", null, "Tool", true, true), 42));
        Assert.True(OpenWindowService.ShouldInclude(new WindowCandidate(5, 12, "Chrome", "chrome", @"C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe", "Google Chrome", true, false), 42));
        Assert.True(OpenWindowService.ShouldInclude(new WindowCandidate(6, 13, "Edge", "msedge", @"C:\\Program Files (x86)\\Microsoft\\Edge\\Application\\msedge.exe", "Microsoft Edge", true, false), 42));
        Assert.True(OpenWindowService.ShouldInclude(new WindowCandidate(4, 11, "notes.txt - Notepad", "notepad", @"C:\Windows\notepad.exe", "Notepad", true, false), 42));
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
        var keep = parcel.Items[1]; var link = factory.WebLink(parcel.Id, "https://example.com/new", null, null, 2); await store.ReplaceItemsAsync(parcel, new[] { keep, link }, "Parcel packed — 2 items saved");
        var restarted = await Store(temp.Path); var loaded = Assert.Single(restarted.Parcels); Assert.Equal(2, loaded.ItemCount); Assert.DoesNotContain(loaded.Items, item => item.Value == firstPath); Assert.Contains(loaded.Items, item => item.ItemType == ParcelItemType.WebLink);
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
    public void BrowserRulesExcludePrivateAndNonWebTabsAndUseWindowScopedIdentity()
    {
        var tab = new BrowserTabData { Browser = "chrome", Url = "https://example.com", Incognito = false };
        Assert.True(BrowserTabRules.IsAllowedForCapture(tab));
        Assert.False(BrowserTabRules.IsAllowedForCapture(tab with { Incognito = true }));
        Assert.False(BrowserTabRules.IsAllowedForCapture(tab with { Url = "chrome://settings" }));
        Assert.False(BrowserTabRules.IsAllowedForCapture(tab with { Url = "https://user:password@example.com/private" }));
        Assert.NotEqual(BrowserTabRules.Identity("chrome", "window-0", tab.Url), BrowserTabRules.Identity("chrome", "window-1", tab.Url));
    }

    [Fact]
    public void BrowserIdentityPreservesQueryFragmentPathAndProtocolAndNormalizesOnlyHost()
    {
        var first = BrowserTabRules.Identity("CHROME", "window-0", "HTTPS://Example.COM/Path%2FCase?q=One#Part");
        var second = BrowserTabRules.Identity("chrome", "window-0", "https://example.com/Path%2FCase?q=One#Part");
        Assert.Equal(first, second);
        Assert.NotEqual(first, BrowserTabRules.Identity("chrome", "window-0", "http://example.com/Path%2FCase?q=One#Part"));
        Assert.NotEqual(first, BrowserTabRules.Identity("chrome", "window-0", "https://example.com/Path%2FCase?q=Two#Part"));
        Assert.NotEqual(first, BrowserTabRules.Identity("chrome", "window-0", "https://example.com/Path%2FCase?q=One"));
        Assert.NotEqual(first, BrowserTabRules.Identity("chrome", "window-0", "https://example.com/path%2FCase?q=One#Part"));
        Assert.NotEqual(first, BrowserTabRules.Identity("chrome", "window-0", "https://example.com/Path%2FCase?b=Two&a=One#Part"));
        var saved = new ParcelItem { ItemType = ParcelItemType.BrowserTab, NormalizedIdentity = first };
        var caseChanged = new ParcelItem { ItemType = ParcelItemType.BrowserTab, NormalizedIdentity = BrowserTabRules.Identity("chrome", "window-0", "https://example.com/path%2FCase?q=One#Part") };
        Assert.False(ParcelItemIdentity.IsDuplicate(new[] { saved }, caseChanged));
        Assert.True(ParcelItemIdentity.IsDuplicate(new[] { saved }, new ParcelItem { ItemType = ParcelItemType.BrowserTab, NormalizedIdentity = first }));
    }

    [Fact]
    public void BrowserFaviconPolicyRejectsLocalAndOversizedReferences()
    {
        Assert.Equal("https://example.com/icon.png", BrowserTabRules.SafeFaviconUrl("https://example.com/icon.png"));
        Assert.Null(BrowserTabRules.SafeFaviconUrl("file:///C:/secret/icon.png"));
        Assert.Null(BrowserTabRules.SafeFaviconUrl("data:image/png;base64,AAAA"));
        Assert.Null(BrowserTabRules.SafeFaviconUrl("https://user:password@example.com/icon.png"));
        Assert.Null(BrowserTabRules.SafeFaviconUrl("https://example.com/" + new string('x', 2048)));
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
    public void BrowserGroupIdentityDoesNotPersistTemporarySessionGroupIds()
    {
        var first = BrowserTabRules.StableGroupIdentity("chrome", "window-0", "Research", "blue");
        var second = BrowserTabRules.StableGroupIdentity("chrome", "window-0", "Research", "blue");
        Assert.NotNull(first); Assert.Equal(first, second); Assert.NotEqual(first, BrowserTabRules.StableGroupIdentity("chrome", "window-1", "Research", "blue")); Assert.Null(BrowserTabRules.StableGroupIdentity("chrome", "window-0", null, null));
    }

    [Fact]
    public async Task BrowserLastOpenedTimestampPersistsWithoutAddingPerTabHistory()
    {
        using var temp = new TestFolder(); var store = await Store(temp.Path);
        var item = new ParcelItem { ItemType = ParcelItemType.BrowserTab, DisplayName = "Docs", Value = "https://example.com/docs", NormalizedIdentity = BrowserTabRules.Identity("chrome", "window-0", "https://example.com/docs"), BrowserFamily = "chrome", BrowserWindowGroupId = "window-0", LaunchEnabled = true };
        var parcel = await store.CreateWithItemsAsync("Browser", "", new[] { item });
        await store.MarkBrowserItemsOpenedAsync(parcel, parcel.Items);
        Assert.NotNull(item.BrowserLastOpenedAt);
        var restarted = await Store(temp.Path); var saved = Assert.Single(Assert.Single(restarted.Parcels).Items);
        Assert.NotNull(saved.BrowserLastOpenedAt);
        Assert.Single(restarted.History, entry => entry.EventType == ParcelHistoryEventType.ItemsCaptured);
    }

    [Fact]
    public void BrowserRestorePlannerPreservesOrderAndSkipsOnlySameWindowDuplicates()
    {
        var saved = new[]
        {
            new BrowserRestoreItem("b", "chrome", "window-0", "https://example.com/b", 2, false, false, null, null),
            new BrowserRestoreItem("a", "chrome", "window-0", "https://example.com/a", 1, true, true, "Research", "blue"),
            new BrowserRestoreItem("other", "chrome", "window-1", "https://example.com/a", 1, false, false, null, null),
            new BrowserRestoreItem("internal", "chrome", "window-0", "chrome://settings", 3, false, false, null, null)
        };
        var plan = BrowserRestorePlanner.Build(saved, new[] { new BrowserOpenIdentity("chrome", "window-0", "https://example.com/a") });
        Assert.Equal(new[] { "AlreadyOpen", "Open", "Unsupported", "Open" }, plan.Items.Select(item => item.Status));
        Assert.Equal(new[] { "a", "b", "internal", "other" }, plan.Items.Select(item => item.Item.ItemKey));
        Assert.Equal(2, plan.OpenableCount);
    }

    [Fact]
    public void BrowserRestorePlannerAllowsExplicitDuplicateCopies()
    {
        var item = new BrowserRestoreItem("copy", "edge", "window-0", "https://example.com", 0, false, false, null, null);
        var plan = BrowserRestorePlanner.Build(new[] { item }, new[] { new BrowserOpenIdentity("edge", "window-0", item.Url) }, allowDuplicates: true);
        Assert.Equal("Open", Assert.Single(plan.Items).Status);
    }

    [Fact]
    public void BrowserClosePlannerRejectsStaleCrossBrowserConnectionAndUrl()
    {
        var candidate = new BrowserCloseCandidate("item", "chrome", "connection-1", "https://example.com/a", "42", "window-1", "https://example.com/a", "window-1", false);
        Assert.True(BrowserClosePlanner.Evaluate(candidate, "chrome", "connection-1").MayClose);
        Assert.False(BrowserClosePlanner.Evaluate(candidate, "edge", "connection-1").MayClose);
        Assert.False(BrowserClosePlanner.Evaluate(candidate, "chrome", "connection-2").MayClose);
        Assert.False(BrowserClosePlanner.Evaluate(candidate with { CurrentUrl = "https://example.com/changed" }, "chrome", "connection-1").MayClose);
        Assert.Equal("Excluded", BrowserClosePlanner.Evaluate(candidate with { Incognito = true }, "chrome", "connection-1").Status);
    }

    [Fact]
    public void BrowserProtocolRejectsUnknownVersionAndOversizedMessages()
    {
        var message = BrowserProtocol.Create("ping", "test", "chrome", new { ok = true }); var bytes = BrowserProtocol.Serialize(message); Assert.True(BrowserProtocol.TryDeserialize(bytes, out var roundTrip, out _)); Assert.Equal("ping", roundTrip!.Type);
        var invalid = bytes.ToArray(); var json = System.Text.Encoding.UTF8.GetString(invalid).Replace("\"version\":1", "\"version\":99", StringComparison.Ordinal); Assert.False(BrowserProtocol.TryDeserialize(System.Text.Encoding.UTF8.GetBytes(json), out _, out var error)); Assert.Contains("version", error, StringComparison.OrdinalIgnoreCase);
        Assert.True(BrowserProtocol.TryDeserializeForRelay(System.Text.Encoding.UTF8.GetBytes(json), out var relayMessage, out _)); Assert.Equal(99, relayMessage!.Version);
        Assert.Throws<InvalidDataException>(() => BrowserProtocol.Serialize(message with { Payload = System.Text.Json.JsonDocument.Parse($"{{\"x\":\"{new string('x', BrowserProtocol.MaxMessageBytes)}\"}}").RootElement }));
    }

    [Fact]
    public void BrowserProtocolRejectsUnknownTypeAndInvalidBrowserIdentity()
    {
        var unknown = BrowserProtocol.Serialize(BrowserProtocol.Create("ping", "unknown", "chrome", new { }));
        var unknownJson = System.Text.Encoding.UTF8.GetString(unknown).Replace("\"ping\"", "\"execute\"", StringComparison.Ordinal);
        Assert.False(BrowserProtocol.TryDeserialize(System.Text.Encoding.UTF8.GetBytes(unknownJson), out _, out var unknownError)); Assert.Contains("unknown", unknownError, StringComparison.OrdinalIgnoreCase);
        var invalidBrowser = BrowserProtocol.Serialize(BrowserProtocol.Create("ping", "browser", "chrome", new { }));
        var invalidBrowserJson = System.Text.Encoding.UTF8.GetString(invalidBrowser).Replace("\"chrome\"", "\"firefox\"", StringComparison.Ordinal);
        Assert.False(BrowserProtocol.TryDeserialize(System.Text.Encoding.UTF8.GetBytes(invalidBrowserJson), out _, out var browserError)); Assert.Contains("browser", browserError, StringComparison.OrdinalIgnoreCase);
        var invalidConnection = BrowserProtocol.Serialize(BrowserProtocol.Create("ping", "connection", "chrome", new { }, new string('x', 121)));
        Assert.False(BrowserProtocol.TryDeserialize(invalidConnection, out _, out var connectionError)); Assert.Contains("connection", connectionError, StringComparison.OrdinalIgnoreCase);

        var missingFields = System.Text.Encoding.UTF8.GetBytes($"{{\"version\":1,\"requestId\":\"missing\",\"type\":\"ping\",\"browser\":\"chrome\",\"timestampUtc\":\"{DateTimeOffset.UtcNow:O}\"}}");
        Assert.False(BrowserProtocol.TryDeserialize(missingFields, out _, out var missingFieldsError)); Assert.Contains("payload", missingFieldsError, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task BrowserProtocolRejectsMalformedJsonAndIncompleteFrames()
    {
        Assert.False(BrowserProtocol.TryDeserialize(System.Text.Encoding.UTF8.GetBytes("{"), out _, out var malformedError));
        Assert.Contains("JSON", malformedError, StringComparison.OrdinalIgnoreCase);

        await using var incomplete = new MemoryStream(new byte[] { 5, 0, 0, 0, (byte)'{', (byte)'}' });
        await Assert.ThrowsAsync<EndOfStreamException>(() => BrowserProtocol.ReadFrameAsync(incomplete));

        await using var zeroLength = new MemoryStream(new byte[] { 0, 0, 0, 0 });
        await Assert.ThrowsAsync<InvalidDataException>(() => BrowserProtocol.ReadFrameAsync(zeroLength));
    }

    [Fact]
    public async Task BrowserProtocolFramingHandlesPartialReads()
    {
        var payload = BrowserProtocol.Serialize(BrowserProtocol.Create("ping", "partial", "edge", new { value = 1 })); await using var stream = new MemoryStream(); await BrowserProtocol.WriteFrameAsync(stream, payload); stream.Position = 0; var framed = await BrowserProtocol.ReadFrameAsync(new ChunkedReadStream(stream, 2)); Assert.Equal(payload, framed);
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

    private sealed class ChunkedReadStream(Stream inner, int chunk) : Stream
    {
        public override bool CanRead => true; public override bool CanSeek => inner.CanSeek; public override bool CanWrite => false; public override long Length => inner.Length; public override long Position { get => inner.Position; set => inner.Position = value; }
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, Math.Min(chunk, count)); public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => inner.ReadAsync(buffer[..Math.Min(chunk, buffer.Length)], cancellationToken); public override void Flush() { } public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask; public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin); public override void SetLength(long value) => throw new NotSupportedException(); public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
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
