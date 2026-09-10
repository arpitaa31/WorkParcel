using System.ComponentModel;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Runtime.CompilerServices;

namespace WorkParcel_App.Models;

public enum ParcelStatus
{
    Open,
    Packed,
    Archived
}

public enum ParcelHistoryEventType
{
    Created,
    Updated,
    Opened,
    Packed,
    Archived,
    Restored,
    ItemAdded,
    ItemsCaptured,
    ItemEdited,
    ItemRemoved,
    ItemRelinked,
    ChangedFileAccepted,
    CloseRequested
}

public enum ParcelItemType
{
    ApplicationWindow,
    Application,
    File,
    Folder,
    WebLink,
    Note
}

public sealed class Parcel : BindableBase
{
    private string _name = string.Empty;
    private string _description = string.Empty;
    private ParcelStatus _status = ParcelStatus.Packed;
    public Parcel() => Items.CollectionChanged += Items_CollectionChanged;

    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get => _name; set => Set(ref _name, value); }
    public string Description { get => _description; set => Set(ref _description, value); }
    public ParcelStatus Status { get => _status; set { if (Set(ref _status, value)) OnPropertyChanged(nameof(StatusText)); } }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? LastOpenedAt { get; set; }
    public DateTime? LastPackedAt { get; set; }
    public DateTime? ArchivedAt { get; set; }
    public ParcelStatus? PreviousStatus { get; set; }
    public ObservableCollection<ParcelItem> Items { get; } = new();
    public int ItemCount => Items.Count;
    public int AvailableItemCount => Items.Count(item => !item.IsMissing && !item.IsInaccessible);
    public int MissingItemCount => Items.Count(item => item.IsMissing);
    public string ItemSummary
    {
        get
        {
            if (Items.Count == 0) return "0 ITEMS";
            var bits = new List<string>();
            var apps = Items.Count(item => item.ItemType is ParcelItemType.Application or ParcelItemType.ApplicationWindow);
            AddCount(bits, apps, "APP");
            AddCount(bits, Items.Count(item => item.ItemType == ParcelItemType.WebLink), "WEB LINK");
            AddCount(bits, Items.Count(item => item.ItemType == ParcelItemType.File), "FILE");
            AddCount(bits, Items.Count(item => item.ItemType == ParcelItemType.Folder), "FOLDER");
            AddCount(bits, Items.Count(item => item.ItemType == ParcelItemType.Note), "NOTE");
            if (MissingItemCount > 0) bits.Add($"{MissingItemCount} MISSING");
            return string.Join(" · ", bits);
        }
    }
    public string StatusText => Status.ToString().ToUpperInvariant();
    public List<ParcelHistoryEntry> History { get; } = new();

    public void NotifyItemsChanged()
    {
        OnPropertyChanged(nameof(ItemCount));
        OnPropertyChanged(nameof(AvailableItemCount));
        OnPropertyChanged(nameof(MissingItemCount));
        OnPropertyChanged(nameof(ItemSummary));
    }

    private void Items_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => NotifyItemsChanged();
    private static void AddCount(List<string> values, int count, string name) { if (count > 0) values.Add($"{count} {name}{(count == 1 ? string.Empty : "S")}"); }
}

public sealed class ParcelItem : BindableBase
{
    private string _displayName = string.Empty;
    private string _value = string.Empty;
    private string? _secondaryDetail;
    private bool _isMissing;
    private bool _isInaccessible;
    private bool _hasChanged;

    public Guid Id { get; init; } = Guid.NewGuid();
    public Guid ParcelId { get; set; }
    public ParcelItemType ItemType { get; set; }
    public string DisplayName { get => _displayName; set => Set(ref _displayName, value); }
    public string Value { get => _value; set => Set(ref _value, value); }
    public string? NormalizedIdentity { get; set; }
    public string? SecondaryDetail { get => _secondaryDetail; set => Set(ref _secondaryDetail, value); }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? LastVerifiedAt { get; set; }
    public int SortOrder { get; set; }
    public bool IsMissing { get => _isMissing; set => Set(ref _isMissing, value); }
    public bool IsInaccessible { get => _isInaccessible; set => Set(ref _isInaccessible, value); }
    public bool HasChanged { get => _hasChanged; set => Set(ref _hasChanged, value); }
    public string? ExecutablePath { get; set; }
    public string? LaunchArguments { get; set; }
    public string? WorkingDirectory { get; set; }
    public string? WindowTitle { get; set; }
    public string? WindowClassName { get; set; }
    public string? ProcessName { get; set; }
    public string? ApplicationUserModelId { get; set; }
    public long? FileSize { get; set; }
    public DateTime? FileModifiedAt { get; set; }
    public string? Fingerprint { get; set; }
    public string? IconCacheKey { get; set; }
    public string? NoteContent { get; set; }
    public bool LaunchEnabled { get; set; } = true;
    public bool CloseSupported { get; set; }
    // Current window identity only. Never written to the database.
    public nint RuntimeWindowHandle { get; set; }
    public uint RuntimeProcessId { get; set; }

    public string TypeLabel => ItemType switch
    {
        ParcelItemType.ApplicationWindow => "APP WINDOW",
        ParcelItemType.WebLink => "WEB LINK",
        _ => ItemType.ToString().ToUpperInvariant()
    };
    public string AvailabilityLabel => IsMissing ? "MISSING" : IsInaccessible ? "INACCESSIBLE" : HasChanged ? "CHANGED SINCE ATTACHED" : LaunchEnabled || ItemType is ParcelItemType.Note ? "AVAILABLE" : "OPEN UNSUPPORTED";
}

public sealed class ParcelHistoryEntry
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public Guid ParcelId { get; init; }
    public ParcelHistoryEventType EventType { get; init; }
    public string Summary { get; init; } = string.Empty;
    public DateTime Timestamp { get; init; }
}

public sealed class WorkspaceSnapshot
{
    public List<Parcel> Parcels { get; } = new();
    public List<ParcelItem> Items { get; } = new();
    public List<ParcelHistoryEntry> History { get; } = new();
    public List<TodayTask> TodayTasks { get; } = new();
}

public sealed class TodayTask : BindableBase
{
    private string _text = string.Empty;
    private bool _isCompleted;

    public Guid Id { get; init; } = Guid.NewGuid();
    public string Text { get => _text; set => Set(ref _text, value); }
    public bool IsCompleted { get => _isCompleted; set => Set(ref _isCompleted, value); }
    public DateTime CreatedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public Guid? ParcelId { get; set; }
    public int SortOrder { get; set; }
}

public abstract class BindableBase : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }

    protected void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
