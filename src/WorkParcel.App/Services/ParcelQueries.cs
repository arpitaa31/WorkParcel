using WorkParcel_App.Models;

namespace WorkParcel_App.Services;

public enum ParcelSort { RecentlyUsed, RecentlyUpdated, Name, DateCreated }

public sealed record ParcelQueryOptions(string? Search = null, ParcelStatus? Status = null, ParcelSort Sort = ParcelSort.RecentlyUsed, bool IncludeArchived = false);

public static class ParcelQueries
{
    public static IReadOnlyList<Parcel> Apply(IEnumerable<Parcel> parcels, ParcelQueryOptions? options = null)
    {
        options ??= new ParcelQueryOptions();
        IEnumerable<Parcel> query = parcels;
        if (!options.IncludeArchived) query = query.Where(p => p.Status != ParcelStatus.Archived);
        if (options.Status is ParcelStatus status) query = query.Where(p => p.Status == status);
        var term = options.Search?.Trim();
        if (!string.IsNullOrWhiteSpace(term)) query = query.Where(p => p.Name.Contains(term, StringComparison.OrdinalIgnoreCase) || p.Description.Contains(term, StringComparison.OrdinalIgnoreCase));
        var ordered = options.Sort switch
        {
            ParcelSort.Name => query.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase),
            ParcelSort.RecentlyUpdated => query.OrderByDescending(p => p.UpdatedAt),
            ParcelSort.DateCreated => query.OrderByDescending(p => p.CreatedAt),
            _ => query.OrderByDescending(p => p.LastOpenedAt ?? DateTime.MinValue).ThenByDescending(p => p.UpdatedAt)
        };
        return ordered.ThenBy(p => p.Id).ToList();
    }
}
