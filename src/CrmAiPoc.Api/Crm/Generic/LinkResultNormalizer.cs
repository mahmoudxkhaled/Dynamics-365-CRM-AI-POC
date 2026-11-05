using CrmAiPoc.Api.Crm.Metadata;

namespace CrmAiPoc.Api.Crm.Generic;

/// <summary>
/// Normalizes FetchXML / mock join rows so linked attributes use stable <c>alias.field</c> keys
/// matching <see cref="CrmTableColumn.Key"/> values produced by the query builder.
/// </summary>
public static class LinkResultNormalizer
{
    public static IReadOnlyList<IReadOnlyDictionary<string, object?>> Normalize(
        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows,
        IReadOnlyList<CrmTableColumn> linkColumns)
    {
        if (rows.Count == 0 || linkColumns.Count == 0)
        {
            return rows;
        }

        var keys = linkColumns.Select(c => c.Key).Where(k => !string.IsNullOrWhiteSpace(k)).ToArray();
        var list = new List<IReadOnlyDictionary<string, object?>>(rows.Count);
        foreach (var row in rows)
        {
            var next = new Dictionary<string, object?>(row, StringComparer.OrdinalIgnoreCase);
            foreach (var key in keys)
            {
                if (next.ContainsKey(key))
                {
                    continue;
                }

                if (TryFindAliasedValue(row, key, out var value))
                {
                    next[key] = value;
                }
                else
                {
                    next[key] = null;
                }
            }

            list.Add(next);
        }

        return list;
    }

    public static IReadOnlyList<CrmTableColumn> MergeColumns(
        IReadOnlyList<CrmTableColumn> rootColumns,
        IReadOnlyList<CrmTableColumn> linkColumns)
    {
        if (linkColumns.Count == 0)
        {
            return rootColumns;
        }

        var merged = new List<CrmTableColumn>(rootColumns);
        var seen = new HashSet<string>(rootColumns.Select(c => c.Key), StringComparer.OrdinalIgnoreCase);
        foreach (var col in linkColumns)
        {
            if (seen.Add(col.Key))
            {
                merged.Add(col);
            }
        }

        return merged;
    }

    private static bool TryFindAliasedValue(
        IReadOnlyDictionary<string, object?> row,
        string aliasQualifiedKey,
        out object? value)
    {
        value = null;
        var dot = aliasQualifiedKey.IndexOf('.');
        if (dot <= 0 || dot >= aliasQualifiedKey.Length - 1)
        {
            return false;
        }

        var alias = aliasQualifiedKey[..dot];
        var field = aliasQualifiedKey[(dot + 1)..];

        // Dynamics Web API commonly returns "alias.field" or "alias_field".
        foreach (var candidate in new[]
                 {
                     aliasQualifiedKey,
                     $"{alias}_{field}",
                     $"{alias}{field}"
                 })
        {
            if (row.TryGetValue(candidate, out value))
            {
                return true;
            }

            var formatted = candidate + "_formatted";
            if (row.TryGetValue(formatted, out value) && value is not null)
            {
                return true;
            }
        }

        // Nested alias object: { "account": { "name": "..." } }
        if (row.TryGetValue(alias, out var nested) && nested is IReadOnlyDictionary<string, object?> nestedDict)
        {
            if (nestedDict.TryGetValue(field, out value))
            {
                return true;
            }
        }

        return false;
    }
}
