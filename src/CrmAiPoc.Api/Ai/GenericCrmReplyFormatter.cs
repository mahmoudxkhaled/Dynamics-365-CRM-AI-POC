using System.Globalization;
using System.Text;
using CrmAiPoc.Api.Crm.Generic;

namespace CrmAiPoc.Api.Ai;

/// <summary>
/// Entity-independent local formatter for generic CRM query results (English).
/// </summary>
public static class GenericCrmReplyFormatter
{
    public static string Format(GenericCrmQueryResult result, string languageHint, Guid? crmUserId = null)
    {
        _ = languageHint;
        var sb = new StringBuilder();

        if (result.Operation == CrmQueryOperation.Count)
        {
            if (result.Count is null)
            {
                return result.Message ?? $"Could not count {result.Entity} records.";
            }

            if (result.CountMayBeCapped || result.CountLimitExceededAnnotation)
            {
                sb.AppendLine(
                    $"Matching {result.Entity} count is at least {result.Count} — not an exact total (Dynamics totalrecordcountlimitexceeded / count ceiling).");
            }
            else
            {
                sb.AppendLine($"Matching {result.Entity} count: {result.Count}.");
            }
        }
        else if (result.Operation == CrmQueryOperation.Aggregate)
        {
            sb.AppendLine(FormatAggregate(result));
        }
        else if (result.Items.Count == 0)
        {
            sb.AppendLine(
                $"No {result.Entity} records matched for the current authorized CRM identity.");
        }
        else
        {
            sb.AppendLine(
                $"Retrieved {result.Items.Count} {result.Entity} record(s) ({result.ProviderName}):");

            var i = 1;
            foreach (var row in result.Items)
            {
                sb.AppendLine($"{i}. {FormatRow(row, result)}");
                i++;
            }
        }

        if (result.AppliedFilterSummary.Count > 0)
        {
            sb.AppendLine("Applied filters:");
            foreach (var filter in result.AppliedFilterSummary)
            {
                sb.AppendLine($"- {filter}");
            }
        }

        if (!string.IsNullOrWhiteSpace(result.SortSummary) && result.Operation == CrmQueryOperation.List)
        {
            sb.AppendLine($"Sort: {result.SortSummary}");
        }

        if (crmUserId is Guid id)
        {
            sb.AppendLine($"CRM read identity (WhoAmI UserId): {id}");
        }

        sb.AppendLine(
            "Note: This reply was generated locally; CRM records were not sent to the AI model.");

        return sb.ToString().TrimEnd();
    }

    private static string FormatAggregate(GenericCrmQueryResult result)
    {
        if (result.Items.Count == 0)
        {
            return $"No aggregate results for {result.Entity}.";
        }

        var segments = new List<string>();
        decimal? countTotal = null;
        var allCounts = true;
        foreach (var row in result.Items)
        {
            var label = ResolveAggregateGroupLabel(row);
            var metric = ResolveAggregateMetric(row, out var numeric, out var isCountLike);
            if (string.IsNullOrWhiteSpace(metric))
            {
                continue;
            }

            if (isCountLike && numeric is not null)
            {
                countTotal = (countTotal ?? 0m) + numeric.Value;
            }
            else
            {
                allCounts = false;
            }

            segments.Add(string.IsNullOrWhiteSpace(label) ? metric : $"{metric} {label}");
        }

        if (segments.Count == 0)
        {
            return $"Returned {result.Items.Count} aggregate row(s) for {result.Entity}.";
        }

        var joined = JoinNatural(segments);
        if (allCounts && countTotal is decimal total)
        {
            var totalText = total.ToString("0.##", CultureInfo.InvariantCulture);
            return $"Found {totalText} {result.Entity}: {joined}.";
        }

        return $"{result.Entity} aggregate results: {joined}.";
    }

    private static string JoinNatural(IReadOnlyList<string> parts)
    {
        if (parts.Count == 1)
        {
            return parts[0];
        }

        if (parts.Count == 2)
        {
            return $"{parts[0]} and {parts[1]}";
        }

        var head = string.Join(", ", parts.Take(parts.Count - 1));
        return $"{head}, and {parts[^1]}";
    }

    private static string ResolveAggregateGroupLabel(IReadOnlyDictionary<string, object?> row)
    {
        if (TryGet(row, "year", out var year) && TryGet(row, "month", out var month) &&
            !string.IsNullOrWhiteSpace(year) && !string.IsNullOrWhiteSpace(month))
        {
            var m = month!.PadLeft(2, '0');
            return $"{year}-{m}";
        }

        foreach (var key in new[] { "statuscode_label", "statecode_label", "status_label", "statuscode", "statecode" })
        {
            if (TryGet(row, key, out var value) && !string.IsNullOrWhiteSpace(value))
            {
                return value!;
            }
        }

        return string.Empty;
    }

    private static string ResolveAggregateMetric(
        IReadOnlyDictionary<string, object?> row,
        out decimal? numeric,
        out bool isCountLike)
    {
        numeric = null;
        isCountLike = false;
        foreach (var key in new[]
                 {
                     "record_count", "ordercount", "count", "total_amount", "totalamount_sum", "sum", "avg", "min", "max"
                 })
        {
            if (!row.TryGetValue(key, out var raw) || raw is null)
            {
                continue;
            }

            isCountLike = key is "record_count" or "ordercount" or "count";
            numeric = ToDecimalOrNull(raw);
            if (TryGet(row, key, out var text) && !string.IsNullOrWhiteSpace(text))
            {
                return text!;
            }
        }

        foreach (var kv in row)
        {
            if (kv.Key.EndsWith("_label", StringComparison.OrdinalIgnoreCase) ||
                kv.Key.Equals("year", StringComparison.OrdinalIgnoreCase) ||
                kv.Key.Equals("month", StringComparison.OrdinalIgnoreCase) ||
                kv.Key.Equals("statuscode", StringComparison.OrdinalIgnoreCase) ||
                kv.Key.Equals("statecode", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (TryGet(row, kv.Key, out var value) && !string.IsNullOrWhiteSpace(value))
            {
                numeric = ToDecimalOrNull(kv.Value);
                return value!;
            }
        }

        return string.Empty;
    }

    private static decimal? ToDecimalOrNull(object? value) => value switch
    {
        decimal d => d,
        double db => (decimal)db,
        float f => (decimal)f,
        int i => i,
        long l => l,
        string s when decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out var p) => p,
        _ => null
    };

    private static string FormatRow(IReadOnlyDictionary<string, object?> row, GenericCrmQueryResult result)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(result.PrimaryNameAttribute) &&
            TryGet(row, result.PrimaryNameAttribute!, out var name) &&
            !string.IsNullOrWhiteSpace(name))
        {
            parts.Add(name!);
        }

        foreach (var key in new[] { "ordernumber", "accountnumber", "emailaddress1", "telephone1", "createdon", "totalamount", "jobtitle" })
        {
            if (TryGet(row, key, out var value) && !string.IsNullOrWhiteSpace(value))
            {
                parts.Add(FormatValue(key, value!));
            }
        }

        foreach (var key in new[] { "statecode_label", "statuscode_label", "statecode", "statuscode" })
        {
            if (TryGet(row, key, out var value) && !string.IsNullOrWhiteSpace(value))
            {
                parts.Add(value!);
                break;
            }
        }

        return parts.Count > 0 ? string.Join(" | ", parts) : "(record)";
    }

    private static bool TryGet(IReadOnlyDictionary<string, object?> row, string key, out string? value)
    {
        value = null;
        if (!row.TryGetValue(key, out var raw) || raw is null)
        {
            return false;
        }

        value = raw switch
        {
            DateTimeOffset dto => dto.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            DateTime dt => dt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            decimal d => d.ToString("N2", CultureInfo.InvariantCulture),
            double db => db.ToString("N2", CultureInfo.InvariantCulture),
            _ => Convert.ToString(raw, CultureInfo.InvariantCulture)
        };
        return !string.IsNullOrWhiteSpace(value);
    }

    private static string FormatValue(string key, string value) =>
        key.Equals("totalamount", StringComparison.OrdinalIgnoreCase) ? value : value;
}
