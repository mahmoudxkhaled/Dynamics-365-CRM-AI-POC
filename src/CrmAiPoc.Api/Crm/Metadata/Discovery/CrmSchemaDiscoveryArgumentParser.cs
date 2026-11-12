using System.Globalization;
using System.Text.Json;

namespace CrmAiPoc.Api.Crm.Metadata.Discovery;

public static class CrmSchemaDiscoveryArgumentParser
{
    public static (bool Success, CrmSchemaDiscoveryRequest? Request, string? Error) Parse(
        IDictionary<string, object?>? arguments)
    {
        if (arguments is null || arguments.Count == 0)
        {
            return (false, null, "discover_crm_schema requires entities and/or concepts and/or relationshipPairs.");
        }

        var entities = ReadStringList(arguments, "entities");
        var concepts = ReadStringList(arguments, "concepts");
        // Tool surface uses relationshipPairsJson; accept both for robustness.
        var pairs = ReadPairs(arguments, "relationshipPairs");
        if (pairs.Count == 0)
        {
            pairs = ReadPairs(arguments, "relationshipPairsJson");
        }

        if (entities.Count == 0 && concepts.Count == 0 && pairs.Count == 0)
        {
            return (false, null, "Provide at least one of entities, concepts, or relationshipPairs.");
        }

        if (entities.Count > CrmSchemaDiscoveryLimits.MaxEntities)
        {
            entities = entities.Take(CrmSchemaDiscoveryLimits.MaxEntities).ToList();
        }

        if (concepts.Count > CrmSchemaDiscoveryLimits.MaxConcepts)
        {
            concepts = concepts.Take(CrmSchemaDiscoveryLimits.MaxConcepts).ToList();
        }

        if (pairs.Count > CrmSchemaDiscoveryLimits.MaxRelationshipPairs)
        {
            pairs = pairs.Take(CrmSchemaDiscoveryLimits.MaxRelationshipPairs).ToList();
        }

        return (true, new CrmSchemaDiscoveryRequest
        {
            Entities = entities,
            Concepts = concepts,
            RelationshipPairs = pairs
        }, null);
    }

    private static List<string> ReadStringList(IDictionary<string, object?> arguments, string key)
    {
        var raw = GetRaw(arguments, key);
        if (raw is null)
        {
            return [];
        }

        if (raw is JsonElement je)
        {
            if (je.ValueKind == JsonValueKind.Array)
            {
                return je.EnumerateArray()
                    .Select(e => e.ValueKind == JsonValueKind.String ? e.GetString() : e.ToString())
                    .Where(s => !string.IsNullOrWhiteSpace(s))
                    .Select(s => s!.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }

            if (je.ValueKind == JsonValueKind.String)
            {
                return ParseJsonStringArray(je.GetString());
            }
        }

        if (raw is string s)
        {
            return ParseJsonStringArray(s);
        }

        if (raw is IEnumerable<object?> objs)
        {
            return objs
                .Select(o => Convert.ToString(o, CultureInfo.InvariantCulture))
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x!.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        return [];
    }

    private static List<(string Left, string Right)> ReadPairs(
        IDictionary<string, object?> arguments,
        string key)
    {
        var raw = GetRaw(arguments, key);
        if (raw is null)
        {
            return [];
        }

        if (raw is JsonElement je)
        {
            if (je.ValueKind == JsonValueKind.String)
            {
                return ParsePairsJson(je.GetString());
            }

            if (je.ValueKind == JsonValueKind.Array)
            {
                return ParsePairsElement(je);
            }
        }

        if (raw is string s)
        {
            return ParsePairsJson(s);
        }

        return [];
    }

    private static List<(string Left, string Right)> ParsePairsJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.ValueKind == JsonValueKind.Array
                ? ParsePairsElement(doc.RootElement)
                : [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static List<(string Left, string Right)> ParsePairsElement(JsonElement array)
    {
        var list = new List<(string, string)>();
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Array || item.GetArrayLength() < 2)
            {
                continue;
            }

            var left = item[0].ValueKind == JsonValueKind.String ? item[0].GetString() : item[0].ToString();
            var right = item[1].ValueKind == JsonValueKind.String ? item[1].GetString() : item[1].ToString();
            if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
            {
                continue;
            }

            list.Add((left!.Trim(), right!.Trim()));
        }

        return list;
    }

    private static List<string> ParseJsonStringArray(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        var trimmed = json.Trim();
        if (!trimmed.StartsWith('['))
        {
            return [trimmed];
        }

        try
        {
            using var doc = JsonDocument.Parse(trimmed);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            return doc.RootElement.EnumerateArray()
                .Select(e => e.ValueKind == JsonValueKind.String ? e.GetString() : e.ToString())
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Select(s => s!.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static object? GetRaw(IDictionary<string, object?> arguments, string key)
    {
        if (arguments.TryGetValue(key, out var raw))
        {
            return raw;
        }

        foreach (var kv in arguments)
        {
            if (kv.Key.Equals(key, StringComparison.OrdinalIgnoreCase))
            {
                return kv.Value;
            }
        }

        return null;
    }
}
