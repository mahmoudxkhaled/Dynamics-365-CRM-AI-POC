using System.Globalization;

namespace CrmAiPoc.Api.Crm.Generic;

/// <summary>
/// Builds non-overlapping half-open [start, end) date partitions for Dynamics
/// AggregateQueryRecordLimit (0x8004E023) recovery. Never overlaps; never silently truncates.
/// </summary>
public static class AggregateDatePartitioner
{
    public const int MaxMonthPartitions = 120;
    public const int MaxDayPartitions = 62;

    public readonly record struct Partition(
        DateTimeOffset StartInclusiveUtc,
        DateTimeOffset EndExclusiveUtc,
        string Label);

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
        var list = new List<Partition>();
        for (var y = localMin.Year; y <= localMax.Year; y++)
        {
            var startLocal = new DateTime(y, 1, 1, 0, 0, 0, DateTimeKind.Unspecified);
            var endLocal = startLocal.AddYears(1);
            list.Add(ToPartition(startLocal, endLocal, businessTz, $"year {y}"));
        }

        return list;
    }

    /// <summary>
    /// Coarse half-open multi-year chunks covering [min,max] without gaps or overlaps.
    /// Used when a long CRM history would otherwise create dozens of single-year probes.
    /// </summary>
    public static IReadOnlyList<Partition> BuildMultiYearChunkPartitions(
        DateTimeOffset minUtc,
        DateTimeOffset maxUtc,
        TimeZoneInfo businessTz,
        int yearsPerChunk)
    {
        var years = BuildYearPartitions(minUtc, maxUtc, businessTz);
        if (years.Count == 0)
        {
            return years;
        }

        var chunkSize = Math.Clamp(yearsPerChunk, 1, 20);
        if (years.Count <= chunkSize)
        {
            return years;
        }

        var list = new List<Partition>();
        for (var i = 0; i < years.Count; i += chunkSize)
        {
            var take = Math.Min(chunkSize, years.Count - i);
            var first = years[i];
            var last = years[i + take - 1];
            var startYear = TimeZoneInfo.ConvertTime(first.StartInclusiveUtc, businessTz).Year;
            var endYearExclusive = TimeZoneInfo.ConvertTime(last.EndExclusiveUtc, businessTz).Year;
            list.Add(new Partition(
                first.StartInclusiveUtc,
                last.EndExclusiveUtc,
                take == 1
                    ? first.Label
                    : $"years {startYear}-{endYearExclusive - 1}"));
        }

        return list;
    }

    public static IReadOnlyList<Partition> BuildMonthPartitions(
        Partition yearPartition,
        TimeZoneInfo businessTz)
    {
        var localStart = TimeZoneInfo.ConvertTime(yearPartition.StartInclusiveUtc, businessTz);
        var localEnd = TimeZoneInfo.ConvertTime(yearPartition.EndExclusiveUtc, businessTz);
        var cursor = new DateTime(localStart.Year, localStart.Month, 1, 0, 0, 0, DateTimeKind.Unspecified);
        var end = new DateTime(localEnd.Year, localEnd.Month, 1, 0, 0, 0, DateTimeKind.Unspecified);

        var list = new List<Partition>();
        while (cursor < end && list.Count < MaxMonthPartitions)
        {
            var next = cursor.AddMonths(1);
            list.Add(ToPartition(cursor, next, businessTz, $"month {cursor:yyyy-MM}"));
            cursor = next;
        }

        return list;
    }

    public static IReadOnlyList<Partition> BuildDayPartitions(
        Partition monthPartition,
        TimeZoneInfo businessTz)
    {
        var localStart = TimeZoneInfo.ConvertTime(monthPartition.StartInclusiveUtc, businessTz);
        var localEnd = TimeZoneInfo.ConvertTime(monthPartition.EndExclusiveUtc, businessTz);
        var cursor = new DateTime(localStart.Year, localStart.Month, localStart.Day, 0, 0, 0, DateTimeKind.Unspecified);
        var end = new DateTime(localEnd.Year, localEnd.Month, localEnd.Day, 0, 0, 0, DateTimeKind.Unspecified);

        var list = new List<Partition>();
        while (cursor < end && list.Count < MaxDayPartitions)
        {
            var next = cursor.AddDays(1);
            list.Add(ToPartition(cursor, next, businessTz, $"day {cursor:yyyy-MM-dd}"));
            cursor = next;
        }

        return list;
    }

    /// <summary>
    /// Whether aggregate functions can be exactly merged across partitions.
    /// AVG requires companion count/sum statistics and is refused without them.
    /// </summary>
    public static bool CanMergeExactly(IReadOnlyList<CrmAggregateSpec> aggregates)
    {
        if (aggregates.Count == 0)
        {
            return false;
        }

        return aggregates.All(a =>
            a.Function is CrmAggregateFunction.Count
                or CrmAggregateFunction.Sum
                or CrmAggregateFunction.Min
                or CrmAggregateFunction.Max);
    }

    public static IReadOnlyList<IReadOnlyDictionary<string, object?>> MergePartitionRows(
        IReadOnlyList<IReadOnlyList<IReadOnlyDictionary<string, object?>>> partitions,
        IReadOnlyList<CrmAggregateSpec> aggregates,
        IReadOnlyList<string> groupAliases)
    {
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

                        CopyCompanion(row, target, alias + "_label");
                        CopyCompanion(row, target, alias + "_formatted");
                        CopyCompanion(row, target, alias + "@OData.Community.Display.V1.FormattedValue");
                    }

                    merged[key] = target;
                }

                foreach (var agg in aggregates)
                {
                    MergeAggregateValue(target, row, agg);
                }
            }
        }

        if (merged.Count == 0 && groupAliases.Count == 0)
        {
            var empty = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            foreach (var agg in aggregates)
            {
                empty[agg.Alias] = agg.Function is CrmAggregateFunction.Count ? 0L : 0m;
            }

            return [empty];
        }

        return merged.Values.ToArray();
    }

    private static void CopyCompanion(
        IReadOnlyDictionary<string, object?> source,
        IDictionary<string, object?> target,
        string key)
    {
        if (source.TryGetValue(key, out var value) && !target.ContainsKey(key))
        {
            target[key] = value;
        }
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

    internal static string BuildGroupKey(
        IReadOnlyDictionary<string, object?> row,
        IReadOnlyList<string> groupAliases)
    {
        if (groupAliases.Count == 0)
        {
            return "__total__";
        }

        // Prefer raw identity/value fields over formatted display companions.
        return string.Join('\u001f', groupAliases.Select(a =>
            row.TryGetValue(a, out var v) && v is not null
                ? Convert.ToString(v, CultureInfo.InvariantCulture) ?? string.Empty
                : string.Empty));
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
        float f => (decimal)f,
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
