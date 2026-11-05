using System.Text.Json;
using System.Text.RegularExpressions;

namespace CrmAiPoc.Api.Ai;

/// <summary>
/// Parses Stage 1 AI entity-selection JSON. Does not infer entities from the user question.
/// </summary>
public static partial class AiEntitySelectionParser
{
    public sealed class Result
    {
        public bool Success { get; init; }
        public string? Error { get; init; }
        public IReadOnlyList<string> SelectedEntities { get; init; } = Array.Empty<string>();
        public string RawText { get; init; } = string.Empty;
    }

    public static Result Parse(string? rawText)
    {
        var text = (rawText ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            return new Result
            {
                Success = false,
                Error = "Stage 1 returned an empty response.",
                RawText = text
            };
        }

        var json = ExtractJsonObject(text);
        if (json is null)
        {
            return new Result
            {
                Success = false,
                Error = "Stage 1 response was not valid JSON with selectedEntities.",
                RawText = text
            };
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("selectedEntities", out var arr) ||
                arr.ValueKind != JsonValueKind.Array)
            {
                return new Result
                {
                    Success = false,
                    Error = "Stage 1 JSON must contain a selectedEntities array.",
                    RawText = text
                };
            }

            var names = new List<string>();
            foreach (var item in arr.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String)
                {
                    return new Result
                    {
                        Success = false,
                        Error = "Stage 1 selectedEntities must contain only strings.",
                        RawText = text
                    };
                }

                var name = item.GetString()?.Trim();
                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                if (!names.Contains(name, StringComparer.OrdinalIgnoreCase))
                {
                    names.Add(name);
                }
            }

            if (names.Count == 0)
            {
                return new Result
                {
                    Success = false,
                    Error = "Stage 1 selectedEntities was empty.",
                    RawText = text
                };
            }

            return new Result
            {
                Success = true,
                SelectedEntities = names,
                RawText = text
            };
        }
        catch (JsonException)
        {
            return new Result
            {
                Success = false,
                Error = "Stage 1 response JSON could not be parsed.",
                RawText = text
            };
        }
    }

    private static string? ExtractJsonObject(string text)
    {
        var fenced = FencePattern().Match(text);
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

    [GeneratedRegex(@"```(?:json)?\s*([\s\S]*?)```", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex FencePattern();
}
