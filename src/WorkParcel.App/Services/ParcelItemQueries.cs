using WorkParcel_App.Models;

namespace WorkParcel_App.Services;

public enum ParcelItemGroup { All, AppsAndWindows, Links, Files, Folders, Notes, BrowserTabs, ChromeTabs, EdgeTabs }
public enum ParcelItemSort { DateAdded, Name, Type, TabOrder }
public sealed record ParcelItemQuery(string? Search = null, ParcelItemGroup Group = ParcelItemGroup.All, ParcelItemSort Sort = ParcelItemSort.DateAdded);

public static class ParcelItemQueries
{
    public static IReadOnlyList<ParcelItem> Apply(IEnumerable<ParcelItem> source, ParcelItemQuery query)
    {
        IEnumerable<ParcelItem> items = source;
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var text = query.Search.Trim(); items = items.Where(item => item.DisplayName.Contains(text, StringComparison.OrdinalIgnoreCase) || (item.SecondaryDetail?.Contains(text, StringComparison.OrdinalIgnoreCase) ?? false) || (item.BrowserDomain?.Contains(text, StringComparison.OrdinalIgnoreCase) ?? false) || (item.BrowserWindowGroupId?.Contains(text, StringComparison.OrdinalIgnoreCase) ?? false) || item.TypeLabel.Contains(text, StringComparison.OrdinalIgnoreCase));
        }
        items = query.Group switch
        {
            ParcelItemGroup.AppsAndWindows => items.Where(item => item.ItemType is ParcelItemType.Application or ParcelItemType.ApplicationWindow),
            ParcelItemGroup.Links => items.Where(item => item.ItemType == ParcelItemType.WebLink),
            ParcelItemGroup.Files => items.Where(item => item.ItemType == ParcelItemType.File),
            ParcelItemGroup.Folders => items.Where(item => item.ItemType == ParcelItemType.Folder),
            ParcelItemGroup.Notes => items.Where(item => item.ItemType == ParcelItemType.Note),
            ParcelItemGroup.BrowserTabs => items.Where(item => item.ItemType == ParcelItemType.BrowserTab),
            ParcelItemGroup.ChromeTabs => items.Where(item => item.ItemType == ParcelItemType.BrowserTab && string.Equals(item.BrowserFamily, "chrome", StringComparison.OrdinalIgnoreCase)),
            ParcelItemGroup.EdgeTabs => items.Where(item => item.ItemType == ParcelItemType.BrowserTab && string.Equals(item.BrowserFamily, "edge", StringComparison.OrdinalIgnoreCase)),
            _ => items
        };
        return query.Sort switch
        {
            ParcelItemSort.Name => items.OrderBy(item => item.DisplayName, StringComparer.OrdinalIgnoreCase).ToList(),
            ParcelItemSort.Type => items.OrderBy(item => item.ItemType).ThenBy(item => item.DisplayName, StringComparer.OrdinalIgnoreCase).ToList(),
            ParcelItemSort.TabOrder => items.OrderBy(item => item.BrowserFamily).ThenBy(item => item.BrowserWindowGroupId).ThenBy(item => item.BrowserTabIndex ?? int.MaxValue).ThenBy(item => item.DisplayName, StringComparer.OrdinalIgnoreCase).ToList(),
            _ => items.OrderBy(item => item.SortOrder).ThenBy(item => item.CreatedAt).ToList()
        };
    }
}
