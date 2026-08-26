namespace WorkParcel.Core.Browser;

public sealed record BrowserCloseCandidate(string ItemKey, string Browser, string ConnectionId, string ExpectedUrl, string SessionTabId, string SessionWindowId, string CurrentUrl, string CurrentWindowId, bool Incognito);
public sealed record BrowserCloseDecision(string ItemKey, string Status, string Message)
{
    public bool MayClose => Status == "Close";
}

public static class BrowserClosePlanner
{
    public static BrowserCloseDecision Evaluate(BrowserCloseCandidate candidate, string activeBrowser, string activeConnectionId)
    {
        if (!string.Equals(candidate.Browser, activeBrowser, StringComparison.OrdinalIgnoreCase)) return new(candidate.ItemKey, "Stale", "The browser identity changed.");
        if (!string.Equals(candidate.ConnectionId, activeConnectionId, StringComparison.Ordinal)) return new(candidate.ItemKey, "Stale", "The browser connection identity changed.");
        if (candidate.Incognito) return new(candidate.ItemKey, "Excluded", "Private tabs are excluded from close operations.");
        if (!string.Equals(candidate.SessionWindowId, candidate.CurrentWindowId, StringComparison.Ordinal) || !string.Equals(candidate.ExpectedUrl, candidate.CurrentUrl, StringComparison.Ordinal)) return new(candidate.ItemKey, "Stale", "The current tab no longer matches the saved identity.");
        return new(candidate.ItemKey, "Close", "The current tab matches the browser, connection, window and URL identity.");
    }
}
