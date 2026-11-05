using System.Globalization;
using CrmAiPoc.Api.Crm.Metadata;
using CrmAiPoc.Api.Crm.Query;

namespace CrmAiPoc.Api.Crm.Generic;

/// <summary>
/// Post-processes aggregate rows: option labels, chronological sort, missing month fill.
/// </summary>
public static class AggregateResultNormalizer
{
    public static IReadOnlyList<IReadOnlyDictionary<string, object?>> Normalize(
        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows,
        CrmQueryPlan plan,
        CrmEntityMetadata entity,
        FetchXmlAggregateBuildResult built)
    {
        var list = rows.Select(r => new Dictionary<string, object?>(r, StringComparer.OrdinalIgnoreCase))
            .ToList();

        AttachOptionLabels(list, plan, entity);

        if (built.FillMissingMonthBuckets &&
            built.BusinessTimeZone is not null &&
            TryGetYearMonthAliases(plan, out var yearAlias, out var monthAlias))
        {
            list = FillMissingMonths(list, plan, built, yearAlias, monthAlias);
        }

        list = SortAggregateRows(list, plan);
        if (list.Count > CrmQueryLimits.MaxAggregateResultRows)
        {
            list = list.Take(CrmQueryLimits.MaxAggregateResultRows).ToList();
        }

        return list;
    }

    private static void AttachOptionLabels(
        List<Dictionary<string, object?>> rows,
        CrmQueryPlan plan,
        CrmEntityMetadata entity)
    {
        foreach (var group in plan.GroupBy.Where(g => g.DateGrouping == CrmDateGrouping.None))
        {
            if (!entity.TryGetAttribute(group.Field, out var attr) ||
                attr.Kind is not (CrmAttributeKind.Status or CrmAttributeKind.State or CrmAttributeKind.Picklist))
            {
                continue;
            }

            var alias = group.Alias;
            foreach (var row in rows)
            {
                if (row.ContainsKey(alias + "_label"))
                {
                    continue;
                }

                if (!row.TryGetValue(alias, out var raw) || raw is null)
                {
                    continue;
                }

                var code = raw switch
                {
                    int i => i,
                    long l => (int)l,
                    string s when int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var p) => p,
                    _ => (int?)null
                };

                if (code is int c && attr.OptionSetByValue.TryGetValue(c, out var label))
                {
                    row[alias + "_label"] = label;
                }
            }
        }
    }

    private static bool TryGetYearMonthAliases(CrmQueryPlan plan, out string yearAlias, out string monthAlias)
    {
        yearAlias = plan.GroupBy.FirstOrDefault(g => g.DateGrouping == CrmDateGrouping.Year)?.Alias ?? "";
        monthAlias = plan.GroupBy.FirstOrDefault(g => g.DateGrouping == CrmDateGrouping.Month)?.Alias ?? "";
        return !string.IsNullOrEmpty(yearAlias) && !string.IsNullOrEmpty(monthAlias);
    }

    private static List<Dictionary<string, object?>> FillMissingMonths(
        List<Dictionary<string, object?>> rows,
        CrmQueryPlan plan,
        FetchXmlAggregateBuildResult built,
        string yearAlias,
        string monthAlias)
    {
        var buckets = built.CalendarMonthBuckets
                      ?? RelativeDateResolver.EnumerateLast6CalendarMonths(built.UtcNow, built.BusinessTimeZone!);
        var countAlias = plan.Aggregates.FirstOrDefault(a => a.Function == CrmAggregateFunction.Count)?.Alias
                         ?? plan.Aggregates.FirstOrDefault()?.Alias;
        var indexed = new Dictionary<(int Y, int M), Dictionary<string, object?>>();
        foreach (var row in rows)
        {
            if (!TryGetInt(row, yearAlias, out var y) || !TryGetInt(row, monthAlias, out var m))
            {
                continue;
            }

            indexed[(y, m)] = row;
        }

        var filled = new List<Dictionary<string, object?>>(buckets.Count);
        foreach (var (year, month) in buckets)
        {
            if (indexed.TryGetValue((year, month), out var existing))
            {
                filled.Add(existing);
                continue;
            }

            var empty = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                [yearAlias] = year,
                [monthAlias] = month
            };
            if (!string.IsNullOrWhiteSpace(countAlias))
            {
                empty[countAlias!] = 0;
            }

            foreach (var agg in plan.Aggregates.Where(a => a.Function != CrmAggregateFunction.Count))
            {
                empty[agg.Alias] = 0m;
            }

            filled.Add(empty);
        }

        return filled;
    }

    private static List<Dictionary<string, object?>> SortAggregateRows(
        List<Dictionary<string, object?>> rows,
        CrmQueryPlan plan)
    {
        var yearAlias = plan.GroupBy.FirstOrDefault(g => g.DateGrouping == CrmDateGrouping.Year)?.Alias;
        var monthAlias = plan.GroupBy.FirstOrDefault(g => g.DateGrouping == CrmDateGrouping.Month)?.Alias;
        if (!string.IsNullOrEmpty(yearAlias) && !string.IsNullOrEmpty(monthAlias))
        {
            return rows
                .OrderBy(r => TryGetInt(r, yearAlias, out var y) ? y : 0)
                .ThenBy(r => TryGetInt(r, monthAlias, out var m) ? m : 0)
                .ToList();
        }

        var statusAlias = plan.GroupBy.FirstOrDefault(g =>
            g.Field.Equals("statuscode", StringComparison.OrdinalIgnoreCase) ||
            g.Field.Equals("statecode", StringComparison.OrdinalIgnoreCase))?.Alias;
        if (!string.IsNullOrEmpty(statusAlias))
        {
            return rows.OrderBy(r => TryGetInt(r, statusAlias, out var c) ? c : int.MaxValue).ToList();
        }

        return rows;
    }

    private static bool TryGetInt(IReadOnlyDictionary<string, object?> row, string key, out int value)
    {
        value = 0;
        if (!row.TryGetValue(key, out var raw) || raw is null)
        {
            return false;
        }

        switch (raw)
        {
            case int i:
                value = i;
                return true;
            case long l:
                value = (int)l;
                return true;
            case decimal d:
                value = (int)d;
                return true;
            case string s when int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var p):
                value = p;
                return true;
            default:
                return false;
        }
    }
}
