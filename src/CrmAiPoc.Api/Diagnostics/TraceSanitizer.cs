using System.Collections;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CrmAiPoc.Api.Diagnostics;

/// <summary>
/// Redacts secrets and customer-identifying values from diagnostic traces.
/// Never stores raw CRM row payloads.
/// </summary>
public static partial class TraceSanitizer
{
    private static readonly HashSet<string> SensitiveKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "password", "apiKey", "apikey", "authorization", "cookie", "token", "secret",
        "email", "emailaddress1", "telephone1", "phone", "fullname", "firstname", "lastname",
        "name", "subject", "relatedFromId", "accountid", "contactid", "salesorderid", "leadid",
        "customerid", "parentcustomerid", "te_customeraccountid"
    };

    public static string PreviewMessage(string? message, int maxLen = 120)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return string.Empty;
        }

        var trimmed = message.Trim();
        if (trimmed.Length > maxLen)
        {
            trimmed = trimmed[..maxLen] + "…";
        }

        return RedactInline(trimmed);
    }

    public static string RedactInline(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        text = GuidPattern().Replace(text, "[REDACTED_GUID]");
        text = EmailPattern().Replace(text, "[REDACTED_EMAIL]");
        text = PhonePattern().Replace(text, "[REDACTED_PHONE]");
        return text;
    }

    public static string RedactODataPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        var redacted = GuidPattern().Replace(path, "[REDACTED_GUID]");
        // Soften quoted filter string values (names, etc.) while keeping field/operators.
        redacted = QuotedFilterValue().Replace(redacted, "'[REDACTED]'");
        return redacted;
    }

    public static object? SanitizeObject(object? value, int depth = 0)
    {
        if (value is null || depth > 6)
        {
            return null;
        }

        if (value is string s)
        {
            return RedactInline(s);
        }

        if (value is JsonElement je)
        {
            return SanitizeJsonElement(je, depth);
        }

        if (value is IDictionary<string, object?> dict)
        {
            var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            foreach (var (k, v) in dict)
            {
                result[k] = IsSensitiveKey(k) ? RedactValue(v) : SanitizeObject(v, depth + 1);
            }

            return result;
        }

        if (value is IDictionary nonGeneric)
        {
            var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            foreach (DictionaryEntry entry in nonGeneric)
            {
                var k = Convert.ToString(entry.Key, CultureInfo.InvariantCulture) ?? "key";
                result[k] = IsSensitiveKey(k) ? RedactValue(entry.Value) : SanitizeObject(entry.Value, depth + 1);
            }

            return result;
        }

        if (value is IEnumerable enumerable and not string)
        {
            var list = new List<object?>();
            var i = 0;
            foreach (var item in enumerable)
            {
                if (i++ >= 30)
                {
                    list.Add("…truncated…");
                    break;
                }

                list.Add(SanitizeObject(item, depth + 1));
            }

            return list;
        }

        if (value.GetType().IsPrimitive || value is decimal or DateTime or DateTimeOffset or Guid)
        {
            if (value is Guid)
            {
                return "[REDACTED_GUID]";
            }

            return value;
        }

        // Anonymous / DTO: serialize then sanitize JSON tree.
        try
        {
            var json = JsonSerializer.SerializeToElement(value);
            return SanitizeJsonElement(json, depth);
        }
        catch
        {
            return RedactInline(Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty);
        }
    }

    public static Dictionary<string, object?> SanitizeToolArguments(IDictionary<string, object?>? arguments)
    {
        var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        if (arguments is null)
        {
            return result;
        }

        foreach (var (key, raw) in arguments)
        {
            object? value = raw;
            if (raw is JsonElement je)
            {
                value = je.ValueKind switch
                {
                    JsonValueKind.String => je.GetString(),
                    JsonValueKind.Number => je.TryGetInt64(out var l) ? l : je.GetDouble(),
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    JsonValueKind.Null => null,
                    _ => je.ToString()
                };
            }

            if (IsSensitiveKey(key))
            {
                result[key] = RedactValue(value);
                continue;
            }

            if (key.Equals("filtersJson", StringComparison.OrdinalIgnoreCase) && value is string filtersJson)
            {
                result[key] = RedactInline(filtersJson);
                continue;
            }

            result[key] = SanitizeObject(value);
        }

        return result;
    }

    public static string BuildSafeTraceText(ExecutionTraceSnapshot snap)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"ExecutionId: {snap.ExecutionId}");
        sb.AppendLine($"Status: {snap.Status}");
        sb.AppendLine($"TotalMs: {snap.TotalDurationMs}");
        sb.AppendLine($"AI: {snap.AiProvider} / {snap.AiModel}");
        sb.AppendLine($"Tool: {snap.SelectedTool}");
        sb.AppendLine($"PrivacySafe: {snap.PrivacySafe}; OutboundHadCrm: {snap.OutboundContainedCrmRecords}");
        sb.AppendLine("Steps:");
        foreach (var step in snap.Steps)
        {
            sb.AppendLine(
                $"  - [{step.Status}] {step.Name} ({step.DurationMs ?? 0}ms) {step.Summary}" +
                (string.IsNullOrWhiteSpace(step.Error) ? "" : $" ERROR={step.Error}"));
        }

        if (snap.CrmQuery is not null)
        {
            sb.AppendLine("CrmQuery:");
            sb.AppendLine(JsonSerializer.Serialize(snap.CrmQuery, new JsonSerializerOptions { WriteIndented = true }));
        }

        if (snap.OutboundAiPrompt is not null)
        {
            sb.AppendLine("OutboundAiPrompt:");
            sb.AppendLine(JsonSerializer.Serialize(snap.OutboundAiPrompt, new JsonSerializerOptions { WriteIndented = true }));
        }

        return sb.ToString();
    }

    private static object? SanitizeJsonElement(JsonElement je, int depth)
    {
        switch (je.ValueKind)
        {
            case JsonValueKind.Object:
                var obj = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                foreach (var prop in je.EnumerateObject())
                {
                    obj[prop.Name] = IsSensitiveKey(prop.Name)
                        ? "[REDACTED]"
                        : SanitizeJsonElement(prop.Value, depth + 1);
                }

                return obj;
            case JsonValueKind.Array:
                return je.EnumerateArray().Take(30).Select(x => SanitizeJsonElement(x, depth + 1)).ToArray();
            case JsonValueKind.String:
                return RedactInline(je.GetString() ?? "");
            case JsonValueKind.Number:
                return je.TryGetInt64(out var l) ? l : je.GetDouble();
            case JsonValueKind.True:
                return true;
            case JsonValueKind.False:
                return false;
            default:
                return null;
        }
    }

    private static bool IsSensitiveKey(string key) =>
        SensitiveKeys.Contains(key) ||
        key.EndsWith("Id", StringComparison.OrdinalIgnoreCase) ||
        key.Contains("password", StringComparison.OrdinalIgnoreCase) ||
        key.Contains("token", StringComparison.OrdinalIgnoreCase) ||
        key.Contains("secret", StringComparison.OrdinalIgnoreCase);

    private static object RedactValue(object? value) =>
        value is null ? null! : "[REDACTED]";

    [GeneratedRegex(@"\b[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\b")]
    private static partial Regex GuidPattern();

    [GeneratedRegex(@"[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}")]
    private static partial Regex EmailPattern();

    [GeneratedRegex(@"\b\d{7,15}\b")]
    private static partial Regex PhonePattern();

    [GeneratedRegex(@"'([^']*)'")]
    private static partial Regex QuotedFilterValue();
}
