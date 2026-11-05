using System.Globalization;
using CrmAiPoc.Api.Crm.Query;

namespace CrmAiPoc.Api.Crm.Generic;

/// <summary>
/// Builds non-overlapping half-open [start, end) createdon partitions for Dynamics
/// AggregateQueryRecordLimit (0x8004E023) recovery. Never overlaps; never silently truncates.
/// </summary>
public static class AggregateDatePartitioner
{
    public const int MaxMonthPartitions = 120;

    public readonly record struct Partition(
        DateTimeOffset StartInclusiveUtc,
        DateTimeOffset EndExclusiveUtc,
        string Label);

    /// <summary>
    /// Calendar-year partitions covering [minUtc, maxUtc] in the business timezone.
    /// </summary>
    public static IReadOnlyList<Partition> BuildYearPartitions(
        DateTimeOffset minUtc,
        DateTimeOffset maxUtc,
        TimeZoneInfo businessTz)
    {
        if (maxUtc < minUtc)
        {
            (minUtc, maxUtc) = (maxUtc, minUtc);
        }

        var localMin = TimeZoneInfo.ConvertTime(minUtc, businessTz);
        var localMax = TimeZoneInfo.ConvertTime(maxUtc, businessTz);
        var yearStart = new DateTime(localMin.Year, 1, 1, 0, 0, 0, DateTimeKind.Unspecified);
        var lastYear = localMax.Year;
        var list = new List<Partition>();
        for (var y = yearStart.Year; y <= lastYear; y++)
        {
            var startLocal = new DateTime(y, 1, 1, 0, 0, 0, DateTimeKind.Unspecified);
            var endLocal = startLocal.AddYears(1);
            list.Add(ToPartition(startLocal, endLocal, businessTz, $"year {y}"));
        }

        return list;
    }

    /// <summary>
    /// Calendar-month partitions covering a single year partition window.
    /// </summary>
    public static IReadOnlyList<Partition> BuildMonthPartitions(
        Partition yearPartition,
        TimeZoneInfo businessTz)
    {
        var localStart = TimeZoneInfo.ConvertTime(yearPartition.StartInclusiveUtc, businessTz);
        var localEnd = TimeZoneInfo.ConvertTime(yearPartition.EndExclusiveUtc, businessTz);
        var cursor = new DateTime(localStart.Year, localStart.Month, 1, 0, 0, 0, DateTimeKind.Unspecified);
        var end = new DateTime(localEnd.Year, localEnd.Month, 1, 0, 0, 0, DateTimeKind.Unspecified);
        if (localEnd > end)
        {
            // end is exclusive; if end is Jan 1 next year, months run through Dec.
        }

        var list = new List<Partition>();
        while (cursor < end && list.Count < MaxMonthPartitions)
        {
            var next = cursor.AddMonths(1);
            list.Add(ToPartition(cursor, next, businessTz, $"month {cursor:yyyy-MM}"));
            cursor = next;
        }

        return list;
    }

    /// <summary>
    /// Whether the plan's aggregates can be exactly merged across partitions.
    /// AVG cannot be merged without per-partition counts of the averaged field.
    /// </summary>
    public static bool CanMergeExactly(CrmQueryPlan plan)
    {
        if (plan.Aggregates.Count == 0)
        {
            return false;
        }

        return plan.Aggregates.All(a =>
            a.Function is CrmAggregateFunction.Count
                or CrmAggregateFunction.Sum
                or CrmAggregateFunction.Min
                or CrmAggregateFunction.Max);
    }

    public static IReadOnlyList<IReadOnlyDictionary<string, object?>> MergePartitionRows(
        IReadOnlyList<IReadOnlyList<IReadOnlyDictionary<string, object?>>> partitions,
        CrmQueryPlan plan)
    {
        var groupAliases = plan.GroupBy.Select(g => g.Alias).ToArray();
        var merged = new Dictionary<string, Dictionary<string, object?>>(StringComparer.Ordinal);

        foreach (var rows in partitions)
        {
            foreach (var row in rows)
            {
                var key = BuildGroupKey(row, groupAliases);
                if (!merged.TryGetValue(key, out var target))
                {
                    target = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                    foreach (var alias in groupAliases)
                    {
                        if (row.TryGetValue(alias, out var g))
                        {
                            target[alias] = g;
                        }

                        var labelKey = alias + "_label";
                        if (row.TryGetValue(labelKey, out var label))
                        {
                            target[labelKey] = label;
                        }
                    }

                    merged[key] = target;
                }

                foreach (var agg in plan.Aggregates)
                {
                    MergeAggregateValue(target, row, agg);
                }
            }
        }

        // Preserve empty group-less aggregate (single total row) when all partitions empty.
        if (merged.Count == 0 && groupAliases.Length == 0)
        {
            var empty = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            foreach (var agg in plan.Aggregates)
            {
                empty[agg.Alias] = agg.Function is CrmAggregateFunction.Count ? 0 : 0m;
            }

            return [empty];
        }

        return merged.Values.ToArray();
    }

    private static void MergeAggregateValue(
        Dictionary<string, object?> target,
        IReadOnlyDictionary<string, object?> source,
        CrmAggregateSpec agg)
    {
        source.TryGetValue(agg.Alias, out var incomingRaw);
        target.TryGetValue(agg.Alias, out var existingRaw);

        switch (agg.Function)
        {
            case CrmAggregateFunction.Count:
                target[agg.Alias] = ToInt64(existingRaw) + ToInt64(incomingRaw);
                break;
            case CrmAggregateFunction.Sum:
                target[agg.Alias] = ToDecimal(existingRaw) + ToDecimal(incomingRaw);
                break;
            case CrmAggregateFunction.Min:
                target[agg.Alias] = existingRaw is null ? incomingRaw : Min(existingRaw, incomingRaw);
                break;
            case CrmAggregateFunction.Max:
                target[agg.Alias] = existingRaw is null ? incomingRaw : Max(existingRaw, incomingRaw);
                break;
            default:
                throw new InvalidOperationException($"Aggregate '{agg.Function}' cannot be partition-merged.");
        }
    }

    private static string BuildGroupKey(
        IReadOnlyDictionary<string, object?> row,
        IReadOnlyList<string> groupAliases)
    {
        if (groupAliases.Count == 0)
        {
            return "__total__";
        }

        return string.Join('\u001f', groupAliases.Select(a =>
            row.TryGetValue(a, out var v) && v is not null
                ? Convert.ToString(v, CultureInfo.InvariantCulture) ?? ""
                : ""));
    }

    private static Partition ToPartition(
        DateTime startLocal,
        DateTime endLocal,
        TimeZoneInfo tz,
        string label)
    {
        var startUtc = TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(startLocal, DateTimeKind.Unspecified), tz);
        var endUtc = TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(endLocal, DateTimeKind.Unspecified), tz);
        return new Partition(
            new DateTimeOffset(startUtc, TimeSpan.Zero),
            new DateTimeOffset(endUtc, TimeSpan.Zero),
            label);
    }

    private static long ToInt64(object? value) => value switch
    {
        null => 0,
        int i => i,
        long l => l,
        decimal d => (long)d,
        double db => (long)db,
        string s when long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var p) => p,
        _ => 0
    };

    private static decimal ToDecimal(object? value) => value switch
    {
        null => 0m,
        decimal d => d,
        int i => i,
        long l => l,
        double db => (decimal)db,
        string s when decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out var p) => p,
        _ => 0m
    };

    private static object? Min(object? a, object? b)
    {
        if (a is null) return b;
        if (b is null) return a;
        return ToDecimal(a) <= ToDecimal(b) ? a : b;
    }

    private static object? Max(object? a, object? b)
    {
        if (a is null) return b;
        if (b is null) return a;
        return ToDecimal(a) >= ToDecimal(b) ? a : b;
    }
}
