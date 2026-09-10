using System.Security.Cryptography;
using System.Text;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage;
using Windows.Storage.FileProperties;
using WorkParcel_App.Models;

namespace WorkParcel_App.Services;

internal sealed class ItemIconCacheService
{
    private readonly AppDataPaths _paths;
    private static readonly SemaphoreSlim Gate = new(3, 3);
    public ItemIconCacheService(AppDataPaths? paths = null) => _paths = paths ?? WorkspaceStore.Current.Paths;

    public async Task TrySetAsync(ParcelItem item, Image image, CancellationToken cancellationToken = default)
    {
        if (item.ItemType is ParcelItemType.Note or ParcelItemType.WebLink) return;
        var sourcePath = item.ItemType is ParcelItemType.Application or ParcelItemType.ApplicationWindow ? item.ExecutablePath : item.Value;
        if (string.IsNullOrWhiteSpace(sourcePath)) return;
        var key = item.IconCacheKey ?? MakeKey(item, sourcePath); item.IconCacheKey = key; _paths.EnsureDirectories(); var cachePath = Path.Combine(_paths.IconCacheDirectory, key + ".thumb");
        try
        {
            await Gate.WaitAsync(cancellationToken);
            try
            {
                if (!File.Exists(cachePath)) await ExtractAsync(item.ItemType, sourcePath, cachePath, cancellationToken);
                if (!File.Exists(cachePath)) return;
                var bitmap = new BitmapImage { DecodePixelHeight = 28, DecodePixelWidth = 28 };
                await using var stream = File.OpenRead(cachePath); await bitmap.SetSourceAsync(stream.AsRandomAccessStream()); image.Source = bitmap; image.Opacity = 1;
            }
            finally { Gate.Release(); }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or COMException)
        {
            AppLogger.LogTechnicalError(exception);
        }
    }

    private static async Task ExtractAsync(ParcelItemType type, string sourcePath, string cachePath, CancellationToken cancellationToken)
    {
        StorageItemThumbnail? thumbnail = null;
        try
        {
            if (type == ParcelItemType.Folder) thumbnail = await (await StorageFolder.GetFolderFromPathAsync(sourcePath)).GetThumbnailAsync(ThumbnailMode.SingleItem, 32, ThumbnailOptions.UseCurrentScale);
            else thumbnail = await (await StorageFile.GetFileFromPathAsync(sourcePath)).GetThumbnailAsync(ThumbnailMode.SingleItem, 32, ThumbnailOptions.UseCurrentScale);
            if (thumbnail is null || thumbnail.Size == 0) return;
            await using var input = thumbnail.AsStreamForRead(); await using var output = new FileStream(cachePath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true); await input.CopyToAsync(output, cancellationToken);
        }
        finally { thumbnail?.Dispose(); }
    }

    private static string MakeKey(ParcelItem item, string sourcePath)
    {
        var identity = item.ItemType == ParcelItemType.File ? Path.GetExtension(sourcePath) : item.NormalizedIdentity ?? sourcePath;
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity.ToUpperInvariant())))[..24];
    }
}
