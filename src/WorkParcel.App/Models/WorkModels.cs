using System.ComponentModel;
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
    Restored
}

public sealed class Parcel : BindableBase
{
    private string _name = string.Empty;
    private string _description = string.Empty;
    private ParcelStatus _status = ParcelStatus.Packed;

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
    public int ItemCount => 0;
    public string ItemSummary => "0 ITEMS";
    public string StatusText => Status.ToString().ToUpperInvariant();
    public List<ParcelHistoryEntry> History { get; } = new();
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
