using System.Globalization;
using System.Text.RegularExpressions;
using CrmAiPoc.Api.Crm.Metadata;

namespace CrmAiPoc.Api.Crm.Generic;

/// <summary>
/// Formats CRM cell values for UI display based on metadata attribute kinds.
/// Does not invent labels when metadata/formatted values are missing.
/// </summary>
public static class CrmFieldDisplayFormatter
{
    private static readonly Regex GuidLike = new(
        @"^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static string Format(object? value, string? kind, string? key = null)
    {
        if (value is null)
        {
            return string.Empty;
        }

        if (value is string s && string.IsNullOrWhiteSpace(s))
        {
            return string.Empty;
        }

        var kindNorm = kind?.Trim() ?? string.Empty;
        if (Enum.TryParse<CrmAttributeKind>(kindNorm, ignoreCase: true, out var parsed))
        {
            return FormatByKind(value, parsed, key);
        }

        return FormatFallback(value, key);
    }

    public static string FormatByKind(object? value, CrmAttributeKind kind, string? key = null)
    {
        if (value is null)
        {
            return string.Empty;
        }

        return kind switch
        {
            CrmAttributeKind.Money => FormatMoney(value),
            CrmAttributeKind.Integer => FormatNumber(value, decimals: 0),
            CrmAttributeKind.Decimal => FormatNumber(value, decimals: 4),
            CrmAttributeKind.DateTime => FormatDate(value),
            CrmAttributeKind.Boolean => FormatBoolean(value),
            CrmAttributeKind.Lookup or CrmAttributeKind.Customer or CrmAttributeKind.Owner
                or CrmAttributeKind.Uniqueidentifier or CrmAttributeKind.Guid
                => FormatLookupOrGuid(value),
            CrmAttributeKind.Status or CrmAttributeKind.State or CrmAttributeKind.Picklist
                => FormatChoice(value),
            _ => FormatFallback(value, key)
        };
    }

    private static string FormatMoney(object value)
    {
        if (TryToDecimal(value, out var d))
        {
            return d.ToString("N2", CultureInfo.InvariantCulture);
        }

        return value.ToString() ?? string.Empty;
    }

    private static string FormatNumber(object value, int decimals)
    {
        if (TryToDecimal(value, out var d))
        {
            return decimals == 0
                ? Math.Truncate(d).ToString("0", CultureInfo.InvariantCulture)
                : d.ToString("0.####", CultureInfo.InvariantCulture);
        }

        return value.ToString() ?? string.Empty;
    }

    private static string FormatDate(object value)
    {
        if (value is DateTimeOffset dto)
        {
            return dto.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }

        if (value is DateTime dt)
        {
            return dt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }

        var raw = value.ToString();
        if (string.IsNullOrWhiteSpace(raw))
        {
            return string.Empty;
        }

        if (DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed))
        {
            return parsed.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }

        return raw;
    }

    private static string FormatBoolean(object value) => value switch
    {
        bool b => b ? "true" : "false",
        string s when bool.TryParse(s, out var b) => b ? "true" : "false",
        _ => value.ToString() ?? string.Empty
    };

    private static string FormatLookupOrGuid(object value)
    {
        var raw = value.ToString()?.Trim() ?? string.Empty;
        if (raw.Length == 0)
        {
            return string.Empty;
        }

        // Raw lookup GUIDs are not useful table labels without formatted annotations.
        if (GuidLike.IsMatch(raw) || (Guid.TryParse(raw, out _)))
        {
            return string.Empty;
        }

        return raw;
    }

    private static string FormatChoice(object value) =>
        value.ToString()?.Trim() ?? string.Empty;

    private static string FormatFallback(object value, string? key)
    {
        if (value is DateTimeOffset or DateTime ||
            (value is string ds && Regex.IsMatch(ds, @"^\d{4}-\d{2}-\d{2}T")))
        {
            return FormatDate(value);
        }

        if (value is IFormattable num &&
            key is not null &&
            Regex.IsMatch(key, "amount|price|revenue|total", RegexOptions.IgnoreCase))
        {
            return FormatMoney(value);
        }

        if (value is string gs && GuidLike.IsMatch(gs.Trim()))
        {
            return string.Empty;
        }

        return value.ToString() ?? string.Empty;
    }

    private static bool TryToDecimal(object value, out decimal d)
    {
        switch (value)
        {
            case decimal dec:
                d = dec;
                return true;
            case double dbl:
                d = (decimal)dbl;
                return true;
            case float fl:
                d = (decimal)fl;
                return true;
            case int i:
                d = i;
                return true;
            case long l:
                d = l;
                return true;
            case string s when decimal.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out d):
                return true;
            default:
                d = 0;
                return false;
        }
    }
}
