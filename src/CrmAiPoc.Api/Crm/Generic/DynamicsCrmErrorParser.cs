using System.Text.Json;
using System.Text.RegularExpressions;

namespace CrmAiPoc.Api.Crm.Generic;

/// <summary>
/// Parses Dynamics Web API error payloads without logging customer records.
/// </summary>
public static partial class DynamicsCrmErrorParser
{
    public const string AggregateQueryRecordLimitCode = "8004E023";

    public sealed class ParsedError
    {
        public string? Code { get; init; }
        public string? Message { get; init; }
        public bool IsAggregateQueryRecordLimit { get; init; }
    }

    public static ParsedError Parse(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return new ParsedError();
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
            string? code = null;
            string? message = null;
            if (doc.RootElement.TryGetProperty("error", out var err))
            {
                code = err.TryGetProperty("code", out var c) ? c.GetString() : null;
                message = err.TryGetProperty("message", out var m) ? m.GetString() : null;
                if (err.TryGetProperty("innererror", out var inner) &&
                    inner.TryGetProperty("message", out var im) &&
                    string.IsNullOrWhiteSpace(message))
                {
                    message = im.GetString();
                }
            }

            return new ParsedError
            {
                Code = NormalizeCode(code),
                Message = Truncate(message, 240),
                IsAggregateQueryRecordLimit = IsAggregateLimit(code, message)
            };
        }
        catch (JsonException)
        {
            var truncated = Truncate(body, 240);
            return new ParsedError
            {
                Message = truncated,
                IsAggregateQueryRecordLimit = IsAggregateLimit(null, truncated)
            };
        }
    }

    public static bool IsAggregateLimit(string? code, string? message)
    {
        var normalized = NormalizeCode(code);
        if (string.Equals(normalized, AggregateQueryRecordLimitCode, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return !string.IsNullOrWhiteSpace(message) &&
               message.Contains("AggregateQueryRecordLimit", StringComparison.OrdinalIgnoreCase);
    }

    private static string? NormalizeCode(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return null;
        }

        var m = HexCodeRegex().Match(code);
        return m.Success ? m.Groups[1].Value.ToUpperInvariant() : code.Trim();
    }

    private static string? Truncate(string? value, int max)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        var cleaned = GuidRegex().Replace(value, "{guid}");
        return cleaned.Length <= max ? cleaned : cleaned[..max] + "…";
    }

    [GeneratedRegex(@"0x([0-9a-fA-F]{8})", RegexOptions.CultureInvariant)]
    private static partial Regex HexCodeRegex();

    [GeneratedRegex(
        @"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}",
        RegexOptions.CultureInvariant)]
    private static partial Regex GuidRegex();
}
