using System.Globalization;
using System.Text.Json;

namespace CrmAiPoc.Api.Ai;

/// <summary>Parses structured clarify_crm tool arguments (not free-text assistant JSON).</summary>
public static class ClarifyCrmArgumentParser
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public sealed class ParseResult
    {
        public bool Success { get; init; }
        public string? Error { get; init; }
        public string Message { get; init; } = string.Empty;
        public IReadOnlyList<RawSuggestion> Suggestions { get; init; } = Array.Empty<RawSuggestion>();
    }

    public sealed class RawSuggestion
    {
        public string Label { get; init; } = string.Empty;
        public string Question { get; init; } = string.Empty;
        public string? EntityLogicalName { get; init; }
        public IReadOnlyList<string> AttributeLogicalNames { get; init; } = Array.Empty<string>();
        public IReadOnlyList<string> RelationshipSchemaNames { get; init; } = Array.Empty<string>();
    }

    public static ParseResult ParseFromToolArguments(IDictionary<string, object?>? arguments)
    {
        if (arguments is null || arguments.Count == 0)
        {
            return Fail("clarify_crm requires message and suggestions.");
        }

        var message = GetArgString(arguments, "message")?.Trim();
        if (string.IsNullOrWhiteSpace(message))
        {
            return Fail("clarify_crm message is required.");
        }

        if (!TryReadSuggestions(arguments, out var rawSuggestions, out var suggestionsError))
        {
            return Fail(suggestionsError ?? "clarify_crm suggestions are invalid.");
        }

        if (rawSuggestions.Count is < 2 or > 4)
        {
            return Fail($"Clarification must include 2–4 suggestions (received {rawSuggestions.Count}).");
        }

        // Keep incomplete suggestions intact — ClarificationSuggestionValidator drops invalid chips.
        var suggestions = new List<RawSuggestion>(rawSuggestions.Count);
        foreach (var item in rawSuggestions)
        {
            var label = (item.Label ?? string.Empty).Trim();
            var question = (item.Question ?? string.Empty).Trim();

            suggestions.Add(new RawSuggestion
            {
                Label = label,
                Question = question,
                EntityLogicalName = string.IsNullOrWhiteSpace(item.EntityLogicalName)
                    ? null
                    : item.EntityLogicalName.Trim(),
                AttributeLogicalNames = (item.AttributeLogicalNames ?? [])
                    .Where(a => !string.IsNullOrWhiteSpace(a))
                    .Select(a => a.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray(),
                RelationshipSchemaNames = (item.RelationshipSchemaNames ?? [])
                    .Where(a => !string.IsNullOrWhiteSpace(a))
                    .Select(a => a.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray()
            });
        }

        return new ParseResult
        {
            Success = true,
            Message = message,
            Suggestions = suggestions
        };
    }

    private static bool TryReadSuggestions(
        IDictionary<string, object?> arguments,
        out List<SuggestionPayload> suggestions,
        out string? error)
    {
        suggestions = [];
        error = null;

        if (!arguments.TryGetValue("suggestions", out var raw) || raw is null)
        {
            // Allow suggestionsJson alias if a model emits a JSON string.
            if (!arguments.TryGetValue("suggestionsJson", out raw) || raw is null)
            {
                error = "clarify_crm suggestions are required.";
                return false;
            }
        }

        try
        {
            switch (raw)
            {
                case string s when !string.IsNullOrWhiteSpace(s):
                    suggestions = JsonSerializer.Deserialize<List<SuggestionPayload>>(s, JsonOptions) ?? [];
                    return true;

                case JsonElement je when je.ValueKind == JsonValueKind.Array:
                    suggestions = JsonSerializer.Deserialize<List<SuggestionPayload>>(je.GetRawText(), JsonOptions) ?? [];
                    return true;

                case JsonElement je when je.ValueKind == JsonValueKind.String:
                    suggestions = JsonSerializer.Deserialize<List<SuggestionPayload>>(je.GetString() ?? "[]", JsonOptions) ?? [];
                    return true;

                case IEnumerable<ClarifyCrmSuggestionArg> typed:
                    suggestions = typed.Select(t => new SuggestionPayload
                    {
                        Label = t.Label,
                        Question = t.Question,
                        EntityLogicalName = t.EntityLogicalName,
                        AttributeLogicalNames = t.AttributeLogicalNames?.ToList(),
                        RelationshipSchemaNames = t.RelationshipSchemaNames?.ToList()
                    }).ToList();
                    return true;

                case IEnumerable<object> objs:
                    var json = JsonSerializer.Serialize(objs);
                    suggestions = JsonSerializer.Deserialize<List<SuggestionPayload>>(json, JsonOptions) ?? [];
                    return true;

                default:
                    var fallback = JsonSerializer.Serialize(raw);
                    suggestions = JsonSerializer.Deserialize<List<SuggestionPayload>>(fallback, JsonOptions) ?? [];
                    return true;
            }
        }
        catch (JsonException)
        {
            error = "clarify_crm suggestions are not valid JSON.";
            return false;
        }
    }

    private static string? GetArgString(IDictionary<string, object?> args, string name)
    {
        if (!args.TryGetValue(name, out var raw) || raw is null)
        {
            return null;
        }

        return raw switch
        {
            string s => s,
            JsonElement je when je.ValueKind == JsonValueKind.String => je.GetString(),
            JsonElement je when je.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined => null,
            JsonElement je => je.ToString(),
            _ => Convert.ToString(raw, CultureInfo.InvariantCulture)
        };
    }

    private static ParseResult Fail(string error) =>
        new()
        {
            Success = false,
            Error = error
        };

    private sealed class SuggestionPayload
    {
        public string? Label { get; set; }
        public string? Question { get; set; }
        public string? EntityLogicalName { get; set; }
        public List<string>? AttributeLogicalNames { get; set; }
        public List<string>? RelationshipSchemaNames { get; set; }
    }
}
