using WorkParcel_App.Models;

namespace WorkParcel_App.Services;

public enum ParcelItemGroup { All, AppsAndWindows, Links, Files, Folders, Notes }
public enum ParcelItemSort { DateAdded, Name, Type }
public sealed record ParcelItemQuery(string? Search = null, ParcelItemGroup Group = ParcelItemGroup.All, ParcelItemSort Sort = ParcelItemSort.DateAdded);

public static class ParcelItemQueries
{
    public static IReadOnlyList<ParcelItem> Apply(IEnumerable<ParcelItem> source, ParcelItemQuery query)
    {
        IEnumerable<ParcelItem> items = source;
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var text = query.Search.Trim(); items = items.Where(item => item.DisplayName.Contains(text, StringComparison.OrdinalIgnoreCase) || item.Value.Contains(text, StringComparison.OrdinalIgnoreCase) || (item.SecondaryDetail?.Contains(text, StringComparison.OrdinalIgnoreCase) ?? false) || item.TypeLabel.Contains(text, StringComparison.OrdinalIgnoreCase));
        }
        items = query.Group switch
        {
            ParcelItemGroup.AppsAndWindows => items.Where(item => item.ItemType is ParcelItemType.Application or ParcelItemType.ApplicationWindow),
            ParcelItemGroup.Links => items.Where(item => item.ItemType == ParcelItemType.WebLink),
            ParcelItemGroup.Files => items.Where(item => item.ItemType == ParcelItemType.File),
            ParcelItemGroup.Folders => items.Where(item => item.ItemType == ParcelItemType.Folder),
            ParcelItemGroup.Notes => items.Where(item => item.ItemType == ParcelItemType.Note),
            _ => items
        };
        return query.Sort switch
        {
            ParcelItemSort.Name => items.OrderBy(item => item.DisplayName, StringComparer.OrdinalIgnoreCase).ToList(),
            ParcelItemSort.Type => items.OrderBy(item => item.ItemType).ThenBy(item => item.DisplayName, StringComparer.OrdinalIgnoreCase).ToList(),
            _ => items.OrderBy(item => item.SortOrder).ThenBy(item => item.CreatedAt).ToList()
        };
    }
}
