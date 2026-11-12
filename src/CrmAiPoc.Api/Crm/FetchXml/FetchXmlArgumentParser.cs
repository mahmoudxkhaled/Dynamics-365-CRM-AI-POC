using System.Globalization;
using System.Text.Json;

namespace CrmAiPoc.Api.Crm.FetchXml;

public static class FetchXmlArgumentParser
{
    public static FetchXmlToolParseResult ParseFromToolArguments(IDictionary<string, object?>? arguments)
    {
        if (arguments is null || arguments.Count == 0)
        {
            return FetchXmlToolParseResult.Fail("MALFORMED_TOOL_ARGUMENTS", "query_crm requires fetchXml.");
        }

        var fetchXml = GetString(arguments, "fetchXml") ?? GetString(arguments, "fetchxml");
        if (string.IsNullOrWhiteSpace(fetchXml))
        {
            return FetchXmlToolParseResult.Fail("MALFORMED_TOOL_ARGUMENTS", "fetchXml is required.");
        }

        fetchXml = fetchXml.Trim();
        if (fetchXml.Length > FetchXmlSafetyLimits.MaxXmlCharacters)
        {
            return FetchXmlToolParseResult.Fail(
                "QUERY_TOO_LARGE",
                $"fetchXml exceeds the maximum length of {FetchXmlSafetyLimits.MaxXmlCharacters} characters.");
        }

        var modeRaw = GetString(arguments, "resultMode") ?? GetString(arguments, "result_mode") ?? "complete";
        if (!TryParseMode(modeRaw, out var mode))
        {
            return FetchXmlToolParseResult.Fail(
                "MALFORMED_TOOL_ARGUMENTS",
                "resultMode must be 'complete' or 'partial'.");
        }

        var limitation = GetString(arguments, "limitation");
        if (mode == FetchXmlResultMode.Partial)
        {
            if (string.IsNullOrWhiteSpace(limitation) || limitation.Trim().Length < 12)
            {
                return FetchXmlToolParseResult.Fail(
                    "MALFORMED_TOOL_ARGUMENTS",
                    "resultMode=partial requires a clear limitation describing what could not be completed.");
            }

            limitation = limitation.Trim();
            if (limitation.Length > FetchXmlSafetyLimits.MaxLimitationLength)
            {
                limitation = limitation[..FetchXmlSafetyLimits.MaxLimitationLength] + "…";
            }

            if (LooksLikeClaimedExecution(limitation))
            {
                return FetchXmlToolParseResult.Fail(
                    "MALFORMED_TOOL_ARGUMENTS",
                    "limitation must describe missing functionality only; do not claim CRM data was already retrieved.");
            }
        }
        else if (!string.IsNullOrWhiteSpace(limitation))
        {
            return FetchXmlToolParseResult.Fail(
                "MALFORMED_TOOL_ARGUMENTS",
                "limitation must be null/omitted when resultMode=complete.");
        }
        else
        {
            limitation = null;
        }

        // Reject legacy query-plan arguments so the model cannot mix contracts.
        if (HasLegacyPlanArgs(arguments))
        {
            return FetchXmlToolParseResult.Fail(
                "MALFORMED_TOOL_ARGUMENTS",
                "Legacy query-plan arguments are no longer accepted. Pass fetchXml, resultMode, and optional limitation only.");
        }

        return FetchXmlToolParseResult.Ok(new FetchXmlToolRequest
        {
            FetchXml = fetchXml,
            ResultMode = mode,
            Limitation = limitation
        });
    }

    private static bool HasLegacyPlanArgs(IDictionary<string, object?> arguments)
    {
        foreach (var key in arguments.Keys)
        {
            if (key.Equals("entity", StringComparison.OrdinalIgnoreCase) ||
                key.Equals("operation", StringComparison.OrdinalIgnoreCase) ||
                key.Equals("aggregatesJson", StringComparison.OrdinalIgnoreCase) ||
                key.Equals("groupByJson", StringComparison.OrdinalIgnoreCase) ||
                key.Equals("filtersJson", StringComparison.OrdinalIgnoreCase) ||
                key.Equals("selectJson", StringComparison.OrdinalIgnoreCase) ||
                key.Equals("linksJson", StringComparison.OrdinalIgnoreCase) ||
                key.Equals("calculatedMetricsJson", StringComparison.OrdinalIgnoreCase) ||
                key.Equals("dateRangeJson", StringComparison.OrdinalIgnoreCase) ||
                key.Equals("dateRange", StringComparison.OrdinalIgnoreCase))
            {
                var value = arguments[key];
                if (value is not null && !string.IsNullOrWhiteSpace(Convert.ToString(value, CultureInfo.InvariantCulture)))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool LooksLikeClaimedExecution(string limitation)
    {
        return limitation.Contains("already retrieved", StringComparison.OrdinalIgnoreCase) ||
               limitation.Contains("already been retrieved", StringComparison.OrdinalIgnoreCase) ||
               limitation.Contains("data has been fetched", StringComparison.OrdinalIgnoreCase) ||
               limitation.Contains("records were returned", StringComparison.OrdinalIgnoreCase) ||
               limitation.Contains("query succeeded", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryParseMode(string raw, out FetchXmlResultMode mode)
    {
        mode = FetchXmlResultMode.Complete;
        if (raw.Equals("complete", StringComparison.OrdinalIgnoreCase))
        {
            mode = FetchXmlResultMode.Complete;
            return true;
        }

        if (raw.Equals("partial", StringComparison.OrdinalIgnoreCase))
        {
            mode = FetchXmlResultMode.Partial;
            return true;
        }

        return false;
    }

    private static string? GetString(IDictionary<string, object?> arguments, string key)
    {
        if (!arguments.TryGetValue(key, out var raw) || raw is null)
        {
            // Case-insensitive fallback
            foreach (var kv in arguments)
            {
                if (kv.Key.Equals(key, StringComparison.OrdinalIgnoreCase))
                {
                    raw = kv.Value;
                    break;
                }
            }
        }

        if (raw is null)
        {
            return null;
        }

        if (raw is JsonElement je)
        {
            return je.ValueKind switch
            {
                JsonValueKind.String => je.GetString(),
                JsonValueKind.Null => null,
                _ => je.ToString()
            };
        }

        return Convert.ToString(raw, CultureInfo.InvariantCulture);
    }
}
