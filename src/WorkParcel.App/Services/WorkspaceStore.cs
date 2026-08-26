using System.Collections.ObjectModel;
using WorkParcel_App.Models;

namespace WorkParcel_App.Services;

public sealed class DuplicateParcelItemException : InvalidOperationException
{
    public DuplicateParcelItemException(string message) : base(message) { }
}

public sealed class WorkspaceStore : BindableBase
{
    private static readonly Lazy<WorkspaceStore> _current = new(() => new WorkspaceStore());
    private readonly DatabaseInitializer _initializer;
    private readonly WorkspaceRepository _repository;
    private readonly ItemAvailabilityService _availability = new();
    private readonly SemaphoreSlim _initializationGate = new(1, 1);
    private bool _initialized;
    private string _theme = "Dark";
    private Parcel? _currentParcel;
    private int _parcelCount;
    private int _todayItemCount;

    public static WorkspaceStore Current => _current.Value;

    public WorkspaceStore(AppDataPaths? paths = null)
    {
        Paths = paths ?? new AppDataPaths();
        var logger = new AppLogger(Paths);
        var factory = new SqliteConnectionFactory(Paths);
        _initializer = new DatabaseInitializer(factory, logger);
        _repository = new WorkspaceRepository(factory, Paths);
    }

    public AppDataPaths Paths { get; }
    public ObservableCollection<Parcel> Parcels { get; } = new();
    public ObservableCollection<Parcel> Archived { get; } = new();
    public ObservableCollection<TodayTask> TodayTasks { get; } = new();
    public IReadOnlyList<ParcelHistoryEntry> History => Parcels.SelectMany(p => p.History).Concat(Archived.SelectMany(p => p.History)).ToList();
    public Parcel? CurrentParcel { get => _currentParcel; private set => Set(ref _currentParcel, value); }
    public string Theme { get => _theme; set => Set(ref _theme, value); }
    public int ParcelCount { get => _parcelCount; private set => Set(ref _parcelCount, value); }
    public int TodayItemCount { get => _todayItemCount; private set => Set(ref _todayItemCount, value); }
    public long DatabaseSize => _repository.GetDatabaseSize();
    public DateTime? LastSuccessfulInitializationUtc => _initializer.LastSuccessfulInitializationUtc;
    public string DatabaseStatus => _initialized ? "READY" : "NOT INITIALIZED";

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await _initializationGate.WaitAsync(cancellationToken);
        try
        {
            if (_initialized) return;
            await _initializer.InitializeAsync(cancellationToken);
            var snapshot = await _repository.LoadSnapshotAsync(cancellationToken);
            ReplaceSnapshot(snapshot);
            var savedTheme = await _repository.GetSettingAsync("Theme", cancellationToken);
            if (savedTheme is "Dark" or "Light" or "System") _theme = savedTheme;
            _initialized = true;
            OnPropertyChanged(nameof(DatabaseStatus));
        }
        finally { _initializationGate.Release(); }
    }

    private void ReplaceSnapshot(WorkspaceSnapshot snapshot)
    {
        Parcels.Clear();
        Archived.Clear();
        TodayTasks.Clear();
        foreach (var parcel in snapshot.Parcels.Where(p => p.Status == ParcelStatus.Archived)) Archived.Add(parcel);
        foreach (var parcel in snapshot.Parcels.Where(p => p.Status != ParcelStatus.Archived)) Parcels.Add(parcel);
        foreach (var task in snapshot.TodayTasks) TodayTasks.Add(task);
        ParcelCount = snapshot.Parcels.Count;
        TodayItemCount = TodayTasks.Count;
    }

    public async Task<Parcel> CreateEmptyAsync(string name, string description = "", CancellationToken cancellationToken = default)
    {
        var cleanName = ValidateName(name);
        var cleanDescription = ValidateDescription(description);
        var now = DateTime.Now;
        var parcel = new Parcel { Name = cleanName, Description = cleanDescription, Status = ParcelStatus.Packed, CreatedAt = now, UpdatedAt = now, LastPackedAt = now };
        var history = NewHistory(parcel, ParcelHistoryEventType.Created, "Parcel created");
        await _repository.InsertParcelWithHistoryAsync(parcel, history, cancellationToken);
        parcel.History.Add(history);
        Parcels.Insert(0, parcel);
        ParcelCount++;
        return parcel;
    }

    public async Task<Parcel> CreateWithItemsAsync(string name, string description, IReadOnlyList<ParcelItem> items, CancellationToken cancellationToken = default)
    {
        var cleanName = ValidateName(name); var cleanDescription = ValidateDescription(description); var now = DateTime.Now;
        var parcel = new Parcel { Name = cleanName, Description = cleanDescription, Status = ParcelStatus.Packed, CreatedAt = now, UpdatedAt = now, LastPackedAt = now };
        var prepared = PrepareItems(parcel, items, Array.Empty<ParcelItem>());
        var history = NewHistory(parcel, ParcelHistoryEventType.ItemsCaptured, $"Parcel packed — {prepared.Count} item{(prepared.Count == 1 ? string.Empty : "s")} saved");
        await _repository.InsertParcelWithItemsAsync(parcel, prepared, history, cancellationToken);
        foreach (var item in prepared) parcel.Items.Add(item); parcel.History.Add(history); Parcels.Insert(0, parcel); ParcelCount++;
        return parcel;
    }

    public async Task AddItemsAsync(Parcel parcel, IReadOnlyList<ParcelItem> items, CancellationToken cancellationToken = default)
    {
        if (items.Count == 0) return;
        var prepared = PrepareItems(parcel, items, parcel.Items); var now = DateTime.Now;
        var history = NewHistory(parcel, prepared.Count == 1 ? ParcelHistoryEventType.ItemAdded : ParcelHistoryEventType.ItemsCaptured, prepared.Count == 1 ? $"{prepared[0].TypeLabel} added" : $"{prepared.Count} items added");
        await _repository.InsertItemsWithHistoryAsync(prepared, history, cancellationToken);
        parcel.UpdatedAt = now; foreach (var item in prepared) parcel.Items.Add(item); parcel.History.Insert(0, history);
    }

    public async Task UpdateItemAsync(Parcel parcel, ParcelItem item, ParcelHistoryEventType eventType = ParcelHistoryEventType.ItemEdited, string? summary = null, CancellationToken cancellationToken = default)
    {
        if (ParcelItemIdentity.IsDuplicate(parcel.Items, item, item.Id)) throw new DuplicateParcelItemException("That item is already saved in this parcel.");
        item.DisplayName = ValidateItemName(item.DisplayName); item.UpdatedAt = DateTime.Now; parcel.UpdatedAt = item.UpdatedAt;
        var history = NewHistory(parcel, eventType, summary ?? $"{item.TypeLabel} edited");
        await _repository.UpdateItemWithHistoryAsync(item, history, cancellationToken); parcel.History.Insert(0, history); parcel.NotifyItemsChanged();
    }

    public async Task RemoveItemAsync(Parcel parcel, ParcelItem item, CancellationToken cancellationToken = default)
    {
        if (!parcel.Items.Contains(item)) return;
        var history = NewHistory(parcel, ParcelHistoryEventType.ItemRemoved, $"{item.TypeLabel} removed — external resource unchanged");
        await _repository.DeleteItemWithHistoryAsync(item.Id, history, cancellationToken);
        parcel.UpdatedAt = history.Timestamp; parcel.Items.Remove(item); parcel.History.Insert(0, history);
    }

    public async Task RemoveItemsAsync(Parcel parcel, IReadOnlyList<ParcelItem> items, CancellationToken cancellationToken = default)
    {
        var selected = items.Where(item => parcel.Items.Contains(item)).Distinct().ToList(); if (selected.Count == 0) return;
        var history = NewHistory(parcel, ParcelHistoryEventType.ItemRemoved, $"{selected.Count} item{(selected.Count == 1 ? string.Empty : "s")} removed - external resources unchanged");
        await _repository.DeleteItemsWithHistoryAsync(parcel.Id, selected.Select(item => item.Id).ToList(), history, cancellationToken);
        parcel.UpdatedAt = history.Timestamp; foreach (var item in selected) parcel.Items.Remove(item); parcel.History.Insert(0, history);
    }

    public async Task ReplaceItemsAsync(Parcel parcel, IReadOnlyList<ParcelItem> selected, string summary, ParcelHistoryEventType eventType = ParcelHistoryEventType.Packed, CancellationToken cancellationToken = default)
    {
        var itemState = selected.Select(item => (Item: item, item.ParcelId, item.DisplayName, item.CreatedAt, item.UpdatedAt, item.SortOrder)).ToList(); var prepared = PrepareItems(parcel, selected, Array.Empty<ParcelItem>()); var now = DateTime.Now; var oldStatus = parcel.Status; var oldUpdated = parcel.UpdatedAt; var oldPacked = parcel.LastPackedAt;
        parcel.Status = ParcelStatus.Packed; parcel.UpdatedAt = now; parcel.LastPackedAt = now;
        var history = NewHistory(parcel, eventType, summary);
        try { await _repository.ReplaceItemsWithHistoryAsync(parcel, prepared, history, cancellationToken); }
        catch { parcel.Status = oldStatus; parcel.UpdatedAt = oldUpdated; parcel.LastPackedAt = oldPacked; foreach (var old in itemState) { old.Item.ParcelId = old.ParcelId; old.Item.DisplayName = old.DisplayName; old.Item.CreatedAt = old.CreatedAt; old.Item.UpdatedAt = old.UpdatedAt; old.Item.SortOrder = old.SortOrder; } throw; }
        parcel.Items.Clear(); foreach (var item in prepared) parcel.Items.Add(item); parcel.History.Insert(0, history); if (CurrentParcel?.Id == parcel.Id) CurrentParcel = null;
    }

    public async Task VerifyItemsAsync(Parcel parcel, CancellationToken cancellationToken = default)
    {
        foreach (var item in parcel.Items)
        {
            cancellationToken.ThrowIfCancellationRequested(); await _availability.VerifyAsync(item, cancellationToken); item.UpdatedAt = DateTime.Now;
        }
        await _repository.UpdateItemStatesAsync(parcel.Items, cancellationToken);
        parcel.NotifyItemsChanged();
    }

    public async Task MarkBrowserItemsOpenedAsync(Parcel parcel, IEnumerable<ParcelItem> items, CancellationToken cancellationToken = default)
    {
        var selected = items.Where(item => item.ItemType == ParcelItemType.BrowserTab && parcel.Items.Contains(item)).Distinct().ToList();
        if (selected.Count == 0) return;
        var openedAt = DateTime.Now;
        var previous = selected.Select(item => (Item: item, Value: item.BrowserLastOpenedAt)).ToList();
        foreach (var item in selected) item.BrowserLastOpenedAt = openedAt;
        try { await _repository.UpdateBrowserLastOpenedAsync(selected, openedAt, cancellationToken); }
        catch { foreach (var entry in previous) entry.Item.BrowserLastOpenedAt = entry.Value; throw; }
    }

    public async Task UpdateParcelAsync(Parcel parcel, string originalName, string originalDescription, CancellationToken cancellationToken = default)
    {
        var cleanName = ValidateName(parcel.Name);
        var cleanDescription = ValidateDescription(parcel.Description);
        parcel.Name = cleanName;
        parcel.Description = cleanDescription;
        parcel.UpdatedAt = DateTime.Now;
        var changedName = !string.Equals(originalName.Trim(), cleanName, StringComparison.Ordinal);
        var changedDescription = !string.Equals(originalDescription.Trim(), cleanDescription, StringComparison.Ordinal);
        ParcelHistoryEntry? history = changedName || changedDescription ? NewHistory(parcel, ParcelHistoryEventType.Updated, BuildUpdateSummary(changedName, changedDescription)) : null;
        await _repository.UpdateParcelWithHistoryAsync(parcel, history, cancellationToken);
        if (history is not null) parcel.History.Insert(0, history);
    }

    public async Task SetStateAsync(Parcel parcel, ParcelStatus state, string? summary = null, CancellationToken cancellationToken = default)
    {
        if (state is ParcelStatus.Archived) throw new ArgumentException("Use ArchiveAsync for archived parcels.", nameof(state));
        if (parcel.Status == state) return;
        var now = DateTime.Now; var oldStatus = parcel.Status; var oldUpdated = parcel.UpdatedAt; var oldOpened = parcel.LastOpenedAt; var oldPacked = parcel.LastPackedAt;
        parcel.Status = state;
        parcel.UpdatedAt = now;
        if (state == ParcelStatus.Open) parcel.LastOpenedAt = now;
        if (state == ParcelStatus.Packed) parcel.LastPackedAt = now;
        var history = NewHistory(parcel, state == ParcelStatus.Open ? ParcelHistoryEventType.Opened : ParcelHistoryEventType.Packed,
            summary ?? (state == ParcelStatus.Open ? $"Parcel opened — {parcel.ItemCount} items" : $"Parcel packed — {parcel.ItemCount} items"));
        try { await _repository.UpdateParcelWithHistoryAsync(parcel, history, cancellationToken); }
        catch { parcel.Status = oldStatus; parcel.UpdatedAt = oldUpdated; parcel.LastOpenedAt = oldOpened; parcel.LastPackedAt = oldPacked; throw; }
        parcel.History.Insert(0, history);
        CurrentParcel = state == ParcelStatus.Open ? parcel : null;
    }

    public async Task ArchiveAsync(Parcel parcel, CancellationToken cancellationToken = default)
    {
        if (parcel.Status == ParcelStatus.Archived) return;
        var oldStatus = parcel.Status; var oldPrevious = parcel.PreviousStatus; var oldArchived = parcel.ArchivedAt; var oldUpdated = parcel.UpdatedAt;
        parcel.PreviousStatus = parcel.Status is ParcelStatus.Open or ParcelStatus.Packed ? parcel.Status : ParcelStatus.Packed;
        parcel.Status = ParcelStatus.Archived;
        parcel.ArchivedAt = DateTime.Now;
        parcel.UpdatedAt = DateTime.Now;
        var history = NewHistory(parcel, ParcelHistoryEventType.Archived, "Parcel moved to archive");
        try { await _repository.UpdateParcelWithHistoryAsync(parcel, history, cancellationToken); }
        catch { parcel.Status = oldStatus; parcel.PreviousStatus = oldPrevious; parcel.ArchivedAt = oldArchived; parcel.UpdatedAt = oldUpdated; throw; }
        parcel.History.Insert(0, history);
        Parcels.Remove(parcel);
        if (!Archived.Contains(parcel)) Archived.Insert(0, parcel);
        if (CurrentParcel?.Id == parcel.Id) CurrentParcel = null;
    }

    public async Task RestoreAsync(Parcel parcel, CancellationToken cancellationToken = default)
    {
        if (parcel.Status != ParcelStatus.Archived) return;
        var oldStatus = parcel.Status; var oldPrevious = parcel.PreviousStatus; var oldArchived = parcel.ArchivedAt; var oldUpdated = parcel.UpdatedAt;
        parcel.Status = parcel.PreviousStatus is ParcelStatus.Open or ParcelStatus.Packed ? parcel.PreviousStatus.Value : ParcelStatus.Packed;
        parcel.PreviousStatus = null;
        parcel.ArchivedAt = null;
        parcel.UpdatedAt = DateTime.Now;
        var history = NewHistory(parcel, ParcelHistoryEventType.Restored, "Parcel restored from archive");
        try { await _repository.UpdateParcelWithHistoryAsync(parcel, history, cancellationToken); }
        catch { parcel.Status = oldStatus; parcel.PreviousStatus = oldPrevious; parcel.ArchivedAt = oldArchived; parcel.UpdatedAt = oldUpdated; throw; }
        parcel.History.Insert(0, history);
        Archived.Remove(parcel);
        if (!Parcels.Contains(parcel)) Parcels.Insert(0, parcel);
    }

    public async Task DeleteAsync(Parcel parcel, CancellationToken cancellationToken = default)
    {
        await _repository.DeleteParcelAsync(parcel.Id, cancellationToken);
        Parcels.Remove(parcel);
        Archived.Remove(parcel);
        if (CurrentParcel?.Id == parcel.Id) CurrentParcel = null;
        ParcelCount = Math.Max(0, ParcelCount - 1);
        foreach (var task in TodayTasks.Where(task => task.ParcelId == parcel.Id)) task.ParcelId = null;
    }

    public async Task AddTodayTaskAsync(string text, Guid? parcelId = null, CancellationToken cancellationToken = default)
    {
        var clean = ValidateTask(text);
        var task = new TodayTask { Text = clean, ParcelId = parcelId, CreatedAt = DateTime.Now, SortOrder = TodayTasks.Count };
        await _repository.InsertTodayTaskAsync(task, cancellationToken);
        TodayTasks.Add(task);
        TodayItemCount++;
    }

    public async Task UpdateTodayTaskAsync(TodayTask task, string text, bool completed, Guid? parcelId, CancellationToken cancellationToken = default)
    {
        task.Text = ValidateTask(text);
        task.IsCompleted = completed;
        task.CompletedAt = completed ? task.CompletedAt ?? DateTime.Now : null;
        task.ParcelId = parcelId;
        await _repository.UpdateTodayTaskAsync(task, cancellationToken);
    }

    public async Task ToggleTodayTaskAsync(TodayTask task, bool completed, CancellationToken cancellationToken = default) =>
        await UpdateTodayTaskAsync(task, task.Text, completed, task.ParcelId, cancellationToken);

    public async Task DeleteTodayTaskAsync(TodayTask task, CancellationToken cancellationToken = default)
    {
        await _repository.DeleteTodayTaskAsync(task.Id, cancellationToken);
        TodayTasks.Remove(task);
        TodayItemCount = Math.Max(0, TodayItemCount - 1);
    }

    public async Task ClearCompletedTodayTasksAsync(CancellationToken cancellationToken = default)
    {
        await _repository.ClearCompletedTodayTasksAsync(cancellationToken);
        foreach (var task in TodayTasks.Where(task => task.IsCompleted).ToList()) TodayTasks.Remove(task);
        TodayItemCount = TodayTasks.Count;
    }

    public Task<string> BackupAsync(CancellationToken cancellationToken = default) => _repository.BackupAsync(cancellationToken);
    public Task<(int Parcels, int TodayItems)> GetCountsAsync(CancellationToken cancellationToken = default) => _repository.GetCountsAsync(cancellationToken);
    public Task SetThemeAsync(string theme, CancellationToken cancellationToken = default) { Theme = theme; return _repository.SetSettingAsync("Theme", theme, cancellationToken); }
    public void SetCurrent(Parcel? parcel) => CurrentParcel = parcel;

    private static ParcelHistoryEntry NewHistory(Parcel parcel, ParcelHistoryEventType type, string summary) => new()
    {
        ParcelId = parcel.Id,
        EventType = type,
        Summary = summary,
        Timestamp = DateTime.Now
    };

    private static string BuildUpdateSummary(bool nameChanged, bool descriptionChanged) => nameChanged && descriptionChanged ? "Parcel name and description updated" : nameChanged ? "Parcel renamed" : "Parcel description updated";
    private static List<ParcelItem> PrepareItems(Parcel parcel, IReadOnlyList<ParcelItem> items, IEnumerable<ParcelItem> existing)
    {
        var result = new List<ParcelItem>(); var all = existing.ToList(); var now = DateTime.Now; var sort = all.Count;
        foreach (var item in items)
        {
            item.ParcelId = parcel.Id; item.DisplayName = ValidateItemName(item.DisplayName); if (item.CreatedAt == default) item.CreatedAt = now; item.UpdatedAt = now; item.SortOrder = sort++;
            if (ParcelItemIdentity.IsDuplicate(all.Concat(result), item)) throw new DuplicateParcelItemException($"{item.DisplayName} is already saved in this parcel.");
            result.Add(item);
        }
        return result;
    }
    private static string ValidateName(string name) { var clean = name?.Trim() ?? string.Empty; if (clean.Length == 0) throw new ArgumentException("A parcel name is required.", nameof(name)); if (clean.Length > 80) throw new ArgumentException("Parcel names must be 80 characters or fewer.", nameof(name)); return clean; }
    private static string ValidateItemName(string name) { var clean = name?.Trim() ?? string.Empty; if (clean.Length == 0) throw new ArgumentException("An item name is required.", nameof(name)); if (clean.Length > 160) throw new ArgumentException("Item names must be 160 characters or fewer.", nameof(name)); return clean; }
    private static string ValidateDescription(string description) { var clean = description?.Trim() ?? string.Empty; if (clean.Length > 240) throw new ArgumentException("Descriptions must be 240 characters or fewer.", nameof(description)); return clean; }
    private static string ValidateTask(string text) { var clean = text?.Trim() ?? string.Empty; if (clean.Length == 0) throw new ArgumentException("A task is required.", nameof(text)); if (clean.Length > 200) throw new ArgumentException("Tasks must be 200 characters or fewer.", nameof(text)); return clean; }
}
