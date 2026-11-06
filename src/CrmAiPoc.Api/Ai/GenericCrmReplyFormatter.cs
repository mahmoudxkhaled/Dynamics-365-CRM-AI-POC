using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using CrmAiPoc.Api.Crm.Generic;

namespace CrmAiPoc.Api.Ai;

/// <summary>
/// Entity-independent local formatter for generic CRM query results (English).
/// Produces user-readable wording — not technical schema / FetchXML join strings.
/// </summary>
public static class GenericCrmReplyFormatter
{
    private static readonly Regex LinkSummaryRegex = new(
        @"^(?<parent>[A-Za-z0-9_]+)-\[(?<rel>[^/\]]+)/(?<join>[A-Za-z]+)\]->(?<target>[A-Za-z0-9_]+)\s+as\s+(?<alias>[A-Za-z0-9_]+)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex RelativeDateRegex = new(
        @"^(?<field>[A-Za-z0-9_]+)\s+in\s+(?<desc>.+?)\s+\[",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex SimpleFilterRegex = new(
        @"^(?<field>[A-Za-z0-9_]+)\s+(?<op>eq|ne|gt|ge|lt|le|contains|Equals|NotEquals|GreaterThan|GreaterOrEqual|LessThan|LessOrEqual|Contains)\s+(?<value>.+)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static string Format(GenericCrmQueryResult result, string languageHint, Guid? crmUserId = null)
    {
        _ = languageHint;
        _ = crmUserId; // keep CRM identity out of user-facing text
        var sb = new StringBuilder();
        var entityLabel = HumanizeEntity(result.Entity);
        var entityLabelPlural = Pluralize(entityLabel);

        if (result.Operation == CrmQueryOperation.Count)
        {
            if (result.Count is null)
            {
                return result.Message ?? $"Could not count {entityLabelPlural.ToLowerInvariant()}.";
            }

            if (result.CountMayBeCapped || result.CountLimitExceededAnnotation)
            {
                sb.AppendLine(
                    $"At least {result.Count} matching {entityLabelPlural.ToLowerInvariant()} were found (exact total may be higher).");
            }
            else
            {
                sb.AppendLine($"Matching {entityLabelPlural.ToLowerInvariant()}: {result.Count}.");
            }
        }
        else if (result.Operation == CrmQueryOperation.Aggregate)
        {
            sb.AppendLine(FormatAggregate(result));
        }
        else if (result.Items.Count == 0)
        {
            sb.AppendLine($"No {entityLabelPlural.ToLowerInvariant()} matched your question.");
        }
        else
        {
            sb.AppendLine($"Here are {result.Items.Count} {entityLabelPlural.ToLowerInvariant()}:");

            var i = 1;
            foreach (var row in result.Items)
            {
                sb.AppendLine($"{i}. {FormatRow(row, result)}");
                i++;
            }
        }

        var readableFilters = result.AppliedFilterSummary
            .Select(HumanizeFilterSummary)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (readableFilters.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Applied filters:");
            foreach (var filter in readableFilters)
            {
                sb.AppendLine($"- {filter}");
            }
        }

        if (!string.IsNullOrWhiteSpace(result.SortSummary) && result.Operation == CrmQueryOperation.List)
        {
            sb.AppendLine($"Sorted by: {HumanizeSortSummary(result.SortSummary)}");
        }

        sb.AppendLine();
        sb.AppendLine("Your CRM data stayed private. Records were not sent to the AI model.");

        return sb.ToString().TrimEnd();
    }

    internal static string HumanizeFilterSummary(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return string.Empty;
        }

        var text = raw.Trim();

        if (text.StartsWith("expands-to-many:", StringComparison.OrdinalIgnoreCase))
        {
            return "Related records may appear more than once in this list.";
        }

        if (text.Contains("Year partition", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("month partition", StringComparison.OrdinalIgnoreCase))
        {
            return "Results were grouped by year and month.";
        }

        var link = LinkSummaryRegex.Match(text);
        if (link.Success)
        {
            var target = HumanizeEntity(link.Groups["target"].Value);
            var relationship = HumanizeRelationshipName(link.Groups["rel"].Value);
            var join = link.Groups["join"].Value;
            var requires = join.Equals("Inner", StringComparison.OrdinalIgnoreCase);
            var prefix = requires ? "Requires related" : "Includes related";
            return string.IsNullOrWhiteSpace(relationship)
                ? $"{prefix} {target}"
                : $"{prefix} {target} ({relationship})";
        }

        var relative = RelativeDateRegex.Match(text);
        if (relative.Success)
        {
            var field = HumanizeField(relative.Groups["field"].Value);
            var desc = relative.Groups["desc"].Value.Trim();
            return $"{field}: {desc}";
        }

        var filter = SimpleFilterRegex.Match(text);
        if (filter.Success)
        {
            var field = HumanizeField(filter.Groups["field"].Value);
            var op = HumanizeOperator(filter.Groups["op"].Value);
            var value = filter.Groups["value"].Value.Trim().Trim('\'', '"');
            return $"{field} {op} {value}";
        }

        return HumanizeLooseTechnicalText(text);
    }

    internal static string HumanizeSortSummary(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return string.Empty;
        }

        var parts = raw.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var readable = parts.Select(HumanizeSingleSortPart).Where(p => !string.IsNullOrWhiteSpace(p));
        return string.Join("; ", readable);
    }

    private static string HumanizeSingleSortPart(string part)
    {
        var isDefault = part.Contains("(default)", StringComparison.OrdinalIgnoreCase);
        var isPage = part.Contains("(page)", StringComparison.OrdinalIgnoreCase);
        var cleaned = part
            .Replace("(default)", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("(page)", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Trim();

        var tokens = cleaned.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length == 0)
        {
            return string.Empty;
        }

        var field = HumanizeField(tokens[0]);
        var direction = tokens.Length > 1 ? tokens[1] : "asc";
        var order = direction.Equals("desc", StringComparison.OrdinalIgnoreCase)
            ? PreferNewestOrHighest(tokens[0])
            : PreferOldestOrLowest(tokens[0]);

        var suffix = isDefault ? " (default)" : isPage ? " (this page)" : string.Empty;
        return $"{field}, {order}{suffix}";
    }

    private static string PreferNewestOrHighest(string field) =>
        field.Contains("amount", StringComparison.OrdinalIgnoreCase) ||
        field.Contains("total", StringComparison.OrdinalIgnoreCase) ||
        field.Contains("price", StringComparison.OrdinalIgnoreCase)
            ? "highest first"
            : "newest first";

    private static string PreferOldestOrLowest(string field) =>
        field.Contains("amount", StringComparison.OrdinalIgnoreCase) ||
        field.Contains("total", StringComparison.OrdinalIgnoreCase) ||
        field.Contains("price", StringComparison.OrdinalIgnoreCase)
            ? "lowest first"
            : "oldest first";

    private static string HumanizeOperator(string op) => op.ToLowerInvariant() switch
    {
        "eq" or "equals" => "equals",
        "ne" or "notequals" => "does not equal",
        "gt" or "greaterthan" => "is greater than",
        "ge" or "greaterorequal" => "is at least",
        "lt" or "lessthan" => "is less than",
        "le" or "lessorequal" => "is at most",
        "contains" => "contains",
        _ => op
    };

    private static string HumanizeEntity(string logical) => logical.Trim().ToLowerInvariant() switch
    {
        "salesorder" or "salesorders" => "Sales Order",
        "account" or "accounts" => "Account",
        "contact" or "contacts" => "Contact",
        "lead" or "leads" => "Lead",
        "opportunity" or "opportunities" => "Opportunity",
        "systemuser" => "User",
        _ => ToTitleWords(logical)
    };

    private static string Pluralize(string singular) =>
        singular.EndsWith("s", StringComparison.OrdinalIgnoreCase)
            ? singular
            : singular + "s";

    private static string HumanizeField(string field)
    {
        var key = field.Trim();
        if (key.EndsWith("_label", StringComparison.OrdinalIgnoreCase))
        {
            key = key[..^6];
        }

        return key.ToLowerInvariant() switch
        {
            "totalamount" => "Total Amount",
            "createdon" => "Created On",
            "modifiedon" => "Modified On",
            "ordernumber" => "Order Number",
            "accountnumber" => "Account Number",
            "emailaddress1" => "Email",
            "telephone1" => "Phone",
            "fullname" or "name" => "Name",
            "jobtitle" => "Job Title",
            "statecode" => "Status",
            "statuscode" => "Status Reason",
            "address1_city" => "City",
            "address1_country" => "Country",
            _ => ToTitleWords(key)
        };
    }

    private static string HumanizeRelationshipName(string schema)
    {
        var s = schema.Trim();
        if (s.StartsWith("te_", StringComparison.OrdinalIgnoreCase))
        {
            s = s[3..];
        }

        var parts = s.Split('_', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
        {
            return string.Empty;
        }

        // Prefer the last segment (e.g. customeraccountid / billingcontactid).
        var focus = parts[^1];
        if (focus.EndsWith("id", StringComparison.OrdinalIgnoreCase) && focus.Length > 2)
        {
            focus = focus[..^2];
        }

        focus = InsertKnownWordBreaks(focus);
        return string.IsNullOrWhiteSpace(focus) ? string.Empty : ToTitleWords(focus);
    }

    private static string InsertKnownWordBreaks(string value)
    {
        // customeraccount → customer account, billingcontact → billing contact
        return Regex.Replace(
            value,
            @"(customer|billing|primary|parent|owning|created|modified)?(account|contact|order|lead|user|opportunity|customer)",
            m => string.Join(' ', new[] { m.Groups[1].Value, m.Groups[2].Value }.Where(x => x.Length > 0)),
            RegexOptions.IgnoreCase);
    }

    private static string HumanizeLooseTechnicalText(string text)
    {
        var t = text;
        t = Regex.Replace(t, @"\bsalesorder\b", "Sales Order", RegexOptions.IgnoreCase);
        t = Regex.Replace(t, @"\baccount\b", "Account", RegexOptions.IgnoreCase);
        t = Regex.Replace(t, @"\bcontact\b", "Contact", RegexOptions.IgnoreCase);
        t = Regex.Replace(t, @"\blead\b", "Lead", RegexOptions.IgnoreCase);
        t = Regex.Replace(t, @"\btotalamount\b", "Total Amount", RegexOptions.IgnoreCase);
        t = Regex.Replace(t, @"\bcreatedon\b", "Created On", RegexOptions.IgnoreCase);
        return t;
    }

    private static string ToTitleWords(string value)
    {
        var spaced = Regex.Replace(value.Replace('_', ' ').Trim(), @"([a-z])([A-Z])", "$1 $2");
        spaced = Regex.Replace(spaced, @"\s+", " ");
        return CultureInfo.InvariantCulture.TextInfo.ToTitleCase(spaced.ToLowerInvariant());
    }

    private static string FormatAggregate(GenericCrmQueryResult result)
    {
        var entityLabel = HumanizeEntity(result.Entity);
        if (result.Items.Count == 0)
        {
            return $"No grouped results for {Pluralize(entityLabel).ToLowerInvariant()}.";
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
            return $"Returned {result.Items.Count} grouped row(s) for {Pluralize(entityLabel).ToLowerInvariant()}.";
        }

        var joined = JoinNatural(segments);
        if (allCounts && countTotal is decimal total)
        {
            var totalText = total.ToString("0.##", CultureInfo.InvariantCulture);
            return $"Found {totalText} {Pluralize(entityLabel).ToLowerInvariant()}: {joined}.";
        }

        return $"{entityLabel} summary: {joined}.";
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
                parts.Add(value!);
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

        return parts.Count > 0 ? string.Join(" · ", parts) : "(record)";
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
}
