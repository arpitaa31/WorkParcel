using System.Net;

namespace WorkParcel.Core.Browser;

/// <summary>Shared safety rules for browser-tab capture and restore.</summary>
public static class BrowserTabRules
{
    public static bool IsPrivate(bool incognito) => incognito;

    public static bool TryGetRestorableUri(string? value, out Uri uri)
    {
        uri = null!;
        if (string.IsNullOrWhiteSpace(value) || !Uri.TryCreate(value.Trim(), UriKind.Absolute, out var parsed)) return false;
        if ((parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps) || string.IsNullOrWhiteSpace(parsed.Host) || !string.IsNullOrWhiteSpace(parsed.UserInfo)) return false;
        uri = parsed;
        return true;
    }

    public static string Identity(string browser, string windowGroupId, string url)
    {
        var normalizedUrl = TryGetRestorableUri(url, out var parsed) ? parsed.AbsoluteUri : url.Trim();
        return $"{browser.Trim().ToLowerInvariant()}|{windowGroupId.Trim()}|{normalizedUrl}";
    }

    public static string Domain(string? value)
    {
        if (!TryGetRestorableUri(value, out var uri)) return string.Empty;
        return uri.Host.Trim().ToLowerInvariant();
    }

    public static string? SafeFaviconUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 2048 || !Uri.TryCreate(value, UriKind.Absolute, out var uri)) return null;
        return (string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) || string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)) && string.IsNullOrWhiteSpace(uri.UserInfo) ? uri.AbsoluteUri : null;
    }

    public static string? StableGroupIdentity(string browser, string windowGroupId, string? title, string? color)
    {
        if (string.IsNullOrWhiteSpace(title) && string.IsNullOrWhiteSpace(color)) return null;
        var key = $"{browser.Trim().ToLowerInvariant()}|{windowGroupId.Trim()}|{title?.Trim() ?? string.Empty}|{color?.Trim().ToLowerInvariant() ?? string.Empty}";
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(key)))[..24];
    }

    public static bool IsAllowedForCapture(BrowserTabData tab, bool excludePrivate = true) =>
        (!excludePrivate || !IsPrivate(tab.Incognito)) && TryGetRestorableUri(tab.Url, out _);
}
