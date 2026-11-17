namespace ClassKendraAPI.Common;

/// <summary>One page of a long list (Paged&lt;T&gt; in models.ts). Total = rows matching the filters, on every page.</summary>
public record Paged<T>(List<T> Items, int Total, int Page, int PageSize);

public static class Paging
{
    public const int DefaultSize = 50;
    public const int MaxSize = 200;

    /// <summary>Page numbers start at 1. Missing or silly values fall back to page 1 / 50 rows; at most 200 rows.</summary>
    public static (int Page, int Size, int Skip) Clean(int? page, int? pageSize)
    {
        var size = Math.Clamp(pageSize ?? DefaultSize, 1, MaxSize);
        var p = Math.Max(1, page ?? 1);
        return (p, size, (p - 1) * size);
    }
}
