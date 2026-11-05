using System.Text.Json;
using System.Text.RegularExpressions;

namespace CrmAiPoc.Api.Ai;

/// <summary>Parses structured clarification JSON from a Groq text response (no tool call).</summary>
public static partial class ClarificationResponseParser
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public sealed class ParseResult
    {
        public bool IsClarificationAttempt { get; init; }
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

    public static ParseResult TryParse(string? modelText)
    {
        if (string.IsNullOrWhiteSpace(modelText))
        {
            return new ParseResult { IsClarificationAttempt = false, Success = false };
        }

        var json = ExtractJsonObject(modelText.Trim());
        if (json is null)
        {
            return new ParseResult { IsClarificationAttempt = false, Success = false };
        }

        ClarificationPayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<ClarificationPayload>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return new ParseResult
            {
                IsClarificationAttempt = LooksLikeClarification(json),
                Success = false,
                Error = "Clarification JSON could not be parsed."
            };
        }

        if (payload is null ||
            !string.Equals(payload.Type, "clarification", StringComparison.OrdinalIgnoreCase))
        {
            return new ParseResult
            {
                IsClarificationAttempt = LooksLikeClarification(json),
                Success = false,
                Error = LooksLikeClarification(json)
                    ? "Clarification payload is missing type=clarification."
                    : null
            };
        }

        var message = (payload.Message ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(message))
        {
            return new ParseResult
            {
                IsClarificationAttempt = true,
                Success = false,
                Error = "Clarification message is required."
            };
        }

        var raw = payload.Suggestions ?? [];
        if (raw.Count is < 2 or > 4)
        {
            return new ParseResult
            {
                IsClarificationAttempt = true,
                Success = false,
                Error = $"Clarification must include 2–4 suggestions (received {raw.Count})."
            };
        }

        var suggestions = new List<RawSuggestion>(raw.Count);
        foreach (var item in raw)
        {
            var label = (item.Label ?? string.Empty).Trim();
            var question = (item.Question ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(question))
            {
                return new ParseResult
                {
                    IsClarificationAttempt = true,
                    Success = false,
                    Error = "Each clarification suggestion must include a complete question."
                };
            }

            if (question.Length < 12 || !question.Contains(' ', StringComparison.Ordinal))
            {
                return new ParseResult
                {
                    IsClarificationAttempt = true,
                    Success = false,
                    Error = "Clarification suggestions must be complete standalone questions."
                };
            }

            if (string.IsNullOrWhiteSpace(label))
            {
                label = TruncateLabel(question);
            }

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
            IsClarificationAttempt = true,
            Success = true,
            Message = message,
            Suggestions = suggestions
        };
    }

    private static string? ExtractJsonObject(string text)
    {
        var fenced = MarkdownJsonFence().Match(text);
        if (fenced.Success)
        {
            text = fenced.Groups[1].Value.Trim();
        }

        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        if (start < 0 || end <= start)
        {
            return null;
        }

        return text[start..(end + 1)];
    }

    private static bool LooksLikeClarification(string json) =>
        json.Contains("\"clarification\"", StringComparison.OrdinalIgnoreCase) ||
        json.Contains("\"suggestions\"", StringComparison.OrdinalIgnoreCase);

    private static string TruncateLabel(string question)
    {
        var trimmed = question.Trim();
        return trimmed.Length <= 48 ? trimmed : trimmed[..45] + "…";
    }

    [GeneratedRegex(@"```(?:json)?\s*([\s\S]*?)```", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex MarkdownJsonFence();

    private sealed class ClarificationPayload
    {
        public string? Type { get; set; }
        public string? Message { get; set; }
        public List<ClarificationSuggestionPayload>? Suggestions { get; set; }
    }

    private sealed class ClarificationSuggestionPayload
    {
        public string? Label { get; set; }
        public string? Question { get; set; }
        public string? EntityLogicalName { get; set; }
        public List<string>? AttributeLogicalNames { get; set; }
        public List<string>? RelationshipSchemaNames { get; set; }
    }
}
