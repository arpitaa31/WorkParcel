using System.Security.Cryptography;
using System.Security;
using WorkParcel_App.Models;

namespace WorkParcel_App.Services;

public static class WindowsPathIdentity
{
    public static string? Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            var full = Path.GetFullPath(path.Trim().Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar));
            var root = Path.GetPathRoot(full) ?? string.Empty;
            while (full.Length > root.Length && full.EndsWith(Path.DirectorySeparatorChar)) full = full[..^1];
            return full;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException or SecurityException)
        {
            return null;
        }
    }

    public static bool Same(string? left, string? right)
    {
        var a = Normalize(left); var b = Normalize(right);
        return a is not null && b is not null && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    }
}

public sealed record ParsedLinks(IReadOnlyList<Uri> Valid, IReadOnlyList<string> Invalid);

public static class WebLinkRules
{
    public static bool TryNormalize(string? value, out Uri uri)
    {
        uri = null!;
        if (string.IsNullOrWhiteSpace(value) || !Uri.TryCreate(value.Trim(), UriKind.Absolute, out var parsed)) return false;
        if (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps) return false;
        if (string.IsNullOrWhiteSpace(parsed.Host)) return false;
        uri = parsed;
        return true;
    }

    public static ParsedLinks ParseMany(string? text)
    {
        var good = new List<Uri>(); var bad = new List<string>(); var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in (text ?? string.Empty).Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None))
        {
            var value = line.Trim(); if (value.Length == 0) continue;
            if (!TryNormalize(value, out var uri)) { bad.Add(value); continue; }
            if (seen.Add(uri.AbsoluteUri)) good.Add(uri);
        }
        return new ParsedLinks(good, bad);
    }
}

public sealed record FileAttachmentMetadata(long Size, DateTime ModifiedAt, string? Fingerprint);

public sealed class FileMetadataService
{
    public const long DefaultHashLimit = 32L * 1024L * 1024L;
    private readonly long _hashLimit;
    public FileMetadataService(long hashLimit = DefaultHashLimit) => _hashLimit = Math.Max(0, hashLimit);

    public async Task<FileAttachmentMetadata> ReadAsync(string path, CancellationToken cancellationToken = default)
    {
        var info = new FileInfo(path);
        if (!info.Exists) throw new FileNotFoundException("The selected file no longer exists.", path);
        string? fingerprint = null;
        if (info.Length <= _hashLimit)
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var hash = await SHA256.HashDataAsync(stream, cancellationToken);
            fingerprint = Convert.ToHexString(hash);
        }
        return new FileAttachmentMetadata(info.Length, info.LastWriteTime, fingerprint);
    }
}

public sealed class ParcelItemFactory
{
    private readonly FileMetadataService _files;
    public ParcelItemFactory(FileMetadataService? files = null) => _files = files ?? new FileMetadataService();

    public async Task<ParcelItem> FileAsync(Guid parcelId, string path, int sortOrder, CancellationToken cancellationToken = default)
    {
        var normalized = WindowsPathIdentity.Normalize(path) ?? throw new ArgumentException("That file path is not valid.", nameof(path));
        var metadata = await _files.ReadAsync(normalized, cancellationToken);
        var info = new FileInfo(normalized); var now = DateTime.Now;
        return new ParcelItem { ParcelId = parcelId, ItemType = ParcelItemType.File, DisplayName = info.Name, Value = normalized, NormalizedIdentity = normalized, SecondaryDetail = info.DirectoryName, CreatedAt = now, UpdatedAt = now, LastVerifiedAt = now, SortOrder = sortOrder, FileSize = metadata.Size, FileModifiedAt = metadata.ModifiedAt, Fingerprint = metadata.Fingerprint };
    }

    public ParcelItem Folder(Guid parcelId, string path, int sortOrder)
    {
        var normalized = WindowsPathIdentity.Normalize(path) ?? throw new ArgumentException("That folder path is not valid.", nameof(path));
        var info = new DirectoryInfo(normalized); if (!info.Exists) throw new DirectoryNotFoundException("The selected folder no longer exists.");
        var now = DateTime.Now;
        return new ParcelItem { ParcelId = parcelId, ItemType = ParcelItemType.Folder, DisplayName = string.IsNullOrWhiteSpace(info.Name) ? normalized : info.Name, Value = normalized, NormalizedIdentity = normalized, SecondaryDetail = info.Parent?.FullName, CreatedAt = now, UpdatedAt = now, LastVerifiedAt = now, SortOrder = sortOrder };
    }

    public ParcelItem Application(Guid parcelId, string path, int sortOrder)
    {
        var normalized = WindowsPathIdentity.Normalize(path) ?? throw new ArgumentException("That application path is not valid.", nameof(path));
        var extension = Path.GetExtension(normalized);
        if (!extension.Equals(".exe", StringComparison.OrdinalIgnoreCase) && !extension.Equals(".lnk", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Choose an .exe or supported .lnk application shortcut.", nameof(path));
        if (!File.Exists(normalized)) throw new FileNotFoundException("The selected application no longer exists.", normalized);
        var now = DateTime.Now; var name = Path.GetFileNameWithoutExtension(normalized); var shortcut = extension.Equals(".lnk", StringComparison.OrdinalIgnoreCase);
        if (!shortcut) try { name = System.Diagnostics.FileVersionInfo.GetVersionInfo(normalized).FileDescription ?? name; } catch (Exception exception) { AppLogger.LogTechnicalError(exception); }
        return new ParcelItem { ParcelId = parcelId, ItemType = ParcelItemType.Application, DisplayName = name, Value = normalized, NormalizedIdentity = normalized, ExecutablePath = normalized, WorkingDirectory = Path.GetDirectoryName(normalized), SecondaryDetail = shortcut ? "Windows shortcut; target is resolved by Windows" : null, CreatedAt = now, UpdatedAt = now, LastVerifiedAt = now, SortOrder = sortOrder };
    }

    public ParcelItem WebLink(Guid parcelId, string url, string? displayName, string? detail, int sortOrder)
    {
        if (!WebLinkRules.TryNormalize(url, out var uri)) throw new ArgumentException("Only valid HTTP and HTTPS links can be saved.", nameof(url));
        var now = DateTime.Now;
        return new ParcelItem { ParcelId = parcelId, ItemType = ParcelItemType.WebLink, DisplayName = string.IsNullOrWhiteSpace(displayName) ? uri.Host : displayName.Trim(), Value = uri.AbsoluteUri, NormalizedIdentity = uri.AbsoluteUri, SecondaryDetail = string.IsNullOrWhiteSpace(detail) ? uri.Host : detail.Trim(), CreatedAt = now, UpdatedAt = now, LastVerifiedAt = now, SortOrder = sortOrder };
    }

    public ParcelItem Note(Guid parcelId, string? title, string content, int sortOrder)
    {
        var clean = content?.Trim() ?? string.Empty; if (clean.Length == 0) throw new ArgumentException("A note cannot be empty.", nameof(content));
        if (clean.Length > 20000) throw new ArgumentException("Keep parcel notes under 20,000 characters.", nameof(content));
        var now = DateTime.Now;
        return new ParcelItem { ParcelId = parcelId, ItemType = ParcelItemType.Note, DisplayName = string.IsNullOrWhiteSpace(title) ? "Untitled note" : title.Trim(), Value = string.Empty, NoteContent = clean, CreatedAt = now, UpdatedAt = now, LastVerifiedAt = now, SortOrder = sortOrder, LaunchEnabled = false };
    }
}

public sealed class ItemAvailabilityService
{
    private readonly FileMetadataService _files;
    public ItemAvailabilityService(FileMetadataService? files = null) => _files = files ?? new FileMetadataService();

    public async Task VerifyAsync(ParcelItem item, CancellationToken cancellationToken = default)
    {
        item.IsMissing = false; item.IsInaccessible = false; item.HasChanged = false;
        try
        {
            if (item.ItemType == ParcelItemType.File)
            {
                if (!File.Exists(item.Value)) { item.IsMissing = true; return; }
                var info = new FileInfo(item.Value);
                if (item.FileSize == info.Length && item.FileModifiedAt?.ToUniversalTime() == info.LastWriteTime.ToUniversalTime()) return;
                var current = await _files.ReadAsync(item.Value, cancellationToken);
                item.HasChanged = item.FileSize != current.Size || item.FileModifiedAt?.ToUniversalTime() != current.ModifiedAt.ToUniversalTime() || item.Fingerprint is not null && current.Fingerprint is not null && !string.Equals(item.Fingerprint, current.Fingerprint, StringComparison.Ordinal);
            }
            else if (item.ItemType == ParcelItemType.Folder) item.IsMissing = !Directory.Exists(item.Value);
            else if (item.ItemType == ParcelItemType.Application) item.IsMissing = string.IsNullOrWhiteSpace(item.ExecutablePath) || !File.Exists(item.ExecutablePath);
            else if (item.ItemType == ParcelItemType.ApplicationWindow) { item.IsInaccessible = string.IsNullOrWhiteSpace(item.ExecutablePath); item.IsMissing = !item.IsInaccessible && !File.Exists(item.ExecutablePath); }
            else if (item.ItemType == ParcelItemType.WebLink) item.IsMissing = !WebLinkRules.TryNormalize(item.Value, out _);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or SecurityException)
        {
            item.IsInaccessible = true;
        }
        finally { item.LastVerifiedAt = DateTime.Now; }
    }
}

public static class ParcelItemIdentity
{
    public static bool IsDuplicate(IEnumerable<ParcelItem> existing, ParcelItem candidate, Guid? ignoreId = null) =>
        candidate.NormalizedIdentity is not null && existing.Any(item => item.Id != ignoreId && item.ItemType == candidate.ItemType && item.NormalizedIdentity is not null && string.Equals(item.NormalizedIdentity, candidate.NormalizedIdentity, candidate.ItemType is ParcelItemType.WebLink or ParcelItemType.BrowserTab ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase));
}

public static class ParcelItemRelinker
{
    public static void Apply(ParcelItem target, ParcelItem replacement)
    {
        if (target.ItemType != replacement.ItemType) throw new ArgumentException("A re-linked item must keep the same type.", nameof(replacement));
        target.DisplayName = replacement.DisplayName; target.Value = replacement.Value; target.NormalizedIdentity = replacement.NormalizedIdentity; target.SecondaryDetail = replacement.SecondaryDetail;
        target.ExecutablePath = replacement.ExecutablePath; target.WorkingDirectory = replacement.WorkingDirectory; target.FileSize = replacement.FileSize; target.FileModifiedAt = replacement.FileModifiedAt; target.Fingerprint = replacement.Fingerprint;
        target.IsMissing = false; target.IsInaccessible = false; target.HasChanged = false; target.LastVerifiedAt = DateTime.Now; target.LaunchEnabled = replacement.LaunchEnabled;
    }
}
