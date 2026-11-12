using System.Globalization;
using System.Text.RegularExpressions;

namespace CrmAiPoc.Api.Ai;

/// <summary>
/// Humanizes technical filter/sort diagnostic strings for logging and tests.
/// Successful CRM query results are rendered as structured tables in the UI —
/// this type no longer generates user-facing result summaries.
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

        var focus = parts[^1];
        if (focus.EndsWith("id", StringComparison.OrdinalIgnoreCase) && focus.Length > 2)
        {
            focus = focus[..^2];
        }

        focus = InsertKnownWordBreaks(focus);
        return string.IsNullOrWhiteSpace(focus) ? string.Empty : ToTitleWords(focus);
    }

    private static string InsertKnownWordBreaks(string value) =>
        Regex.Replace(
            value,
            @"(customer|billing|primary|parent|owning|created|modified)?(account|contact|order|lead|user|opportunity|customer)",
            m => string.Join(' ', new[] { m.Groups[1].Value, m.Groups[2].Value }.Where(x => x.Length > 0)),
            RegexOptions.IgnoreCase);

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
}
