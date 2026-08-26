namespace WorkParcel.Core.Browser;

public sealed record BrowserRestoreItem(string ItemKey, string Browser, string WindowGroup, string Url, int Position, bool Pinned, bool Active, string? GroupTitle, string? GroupColor);
public sealed record BrowserOpenIdentity(string Browser, string WindowGroup, string Url);
public sealed record BrowserRestorePlanItem(BrowserRestoreItem Item, string Status, string Message);
public sealed record BrowserRestorePlan(IReadOnlyList<BrowserRestorePlanItem> Items)
{
    public int OpenableCount => Items.Count(item => item.Status == "Open");
    public int SkippedCount => Items.Count(item => item.Status != "Open");
}

/// <summary>Pure restore planning used to keep duplicate and unsupported-tab policy testable without a real browser.</summary>
public static class BrowserRestorePlanner
{
    public static BrowserRestorePlan Build(IEnumerable<BrowserRestoreItem> savedItems, IEnumerable<BrowserOpenIdentity> alreadyOpen, bool allowDuplicates = false)
    {
        var open = new HashSet<string>(alreadyOpen.Select(item => BrowserTabRules.Identity(item.Browser, item.WindowGroup, item.Url)), StringComparer.Ordinal);
        var plan = new List<BrowserRestorePlanItem>();
        foreach (var item in savedItems.OrderBy(item => item.Browser, StringComparer.OrdinalIgnoreCase).ThenBy(item => item.WindowGroup, StringComparer.OrdinalIgnoreCase).ThenBy(item => item.Position).ThenBy(item => item.ItemKey, StringComparer.Ordinal))
        {
            if (!BrowserTabRules.TryGetRestorableUri(item.Url, out _)) { plan.Add(new(item, "Unsupported", "Only HTTP and HTTPS tabs can be restored.")); continue; }
            var identity = BrowserTabRules.Identity(item.Browser, item.WindowGroup, item.Url);
            if (!allowDuplicates && !open.Add(identity)) { plan.Add(new(item, "AlreadyOpen", "The same URL is already open in this saved browser window.")); continue; }
            plan.Add(new(item, "Open", "Ready to restore."));
        }
        return new(plan);
    }
}
