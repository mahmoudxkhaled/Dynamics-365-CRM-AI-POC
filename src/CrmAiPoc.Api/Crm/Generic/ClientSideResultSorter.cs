using System.Globalization;

namespace CrmAiPoc.Api.Crm.Generic;

/// <summary>
/// Re-sorts an already-fetched list page (after FetchXML order + top) for two-stage intents.
/// </summary>
public static class ClientSideResultSorter
{
    public static IReadOnlyList<IReadOnlyDictionary<string, object?>> Apply(
        IReadOnlyList<IReadOnlyDictionary<string, object?>> items,
        IReadOnlyList<CrmQuerySort> clientSideSort)
    {
        if (items.Count == 0 || clientSideSort.Count == 0)
        {
            return items;
        }

        IOrderedEnumerable<IReadOnlyDictionary<string, object?>>? ordered = null;
        for (var i = 0; i < clientSideSort.Count; i++)
        {
            var sort = clientSideSort[i];
            var desc = sort.Direction == CrmSortDirection.Desc;
            if (i == 0)
            {
                ordered = desc
                    ? items.OrderByDescending(r => ReadComparable(r, sort.Field))
                    : items.OrderBy(r => ReadComparable(r, sort.Field));
            }
            else
            {
                ordered = desc
                    ? ordered!.ThenByDescending(r => ReadComparable(r, sort.Field))
                    : ordered!.ThenBy(r => ReadComparable(r, sort.Field));
            }
        }

        return ordered!.ToList();
    }

    public static string AppendSortSummary(string? fetchSortSummary, IReadOnlyList<CrmQuerySort> clientSideSort)
    {
        if (clientSideSort.Count == 0)
        {
            return fetchSortSummary ?? string.Empty;
        }

        var client = string.Join(
            ", ",
            clientSideSort.Select(s =>
                $"{s.Field} {(s.Direction == CrmSortDirection.Asc ? "asc" : "desc")} (page)"));
        if (string.IsNullOrWhiteSpace(fetchSortSummary))
        {
            return client;
        }

        return $"{fetchSortSummary}; then {client}";
    }

    private static IComparable ReadComparable(IReadOnlyDictionary<string, object?> row, string field)
    {
        if (!row.TryGetValue(field, out var raw) || raw is null)
        {
            return string.Empty;
        }

        if (raw is string s)
        {
            return decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out var d)
                ? d
                : s;
        }

        return raw as IComparable
               ?? Convert.ToString(raw, CultureInfo.InvariantCulture)
               ?? string.Empty;
    }
}
