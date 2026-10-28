using System.Globalization;
using System.Text.Json;
using CrmAiPoc.Api.Crm.Metadata;
using CrmAiPoc.Api.Crm.Query;

namespace CrmAiPoc.Api.Crm.Generic;

public static class CrmQueryPlanParser
{
    public static CrmQueryPlanParseResult Parse(
        string? entity,
        string? operation,
        string? selectJson,
        string? filtersJson,
        string? relativeDateField,
        string? relativeDateToken,
        string? sortJson,
        string? sortField,
        string? sortDirection,
        int? limit,
        string? relatedFromEntity,
        string? relatedFromId)
    {
        if (string.IsNullOrWhiteSpace(entity))
        {
            return CrmQueryPlanParseResult.Fail("entity is required (approved logical name).");
        }

        if (!TryParseOperation(operation, out var op, out var opError))
        {
            return CrmQueryPlanParseResult.Fail(opError!);
        }

        var select = ParseStringArray(selectJson);
        var filters = new List<CrmQueryFilter>();
        if (!string.IsNullOrWhiteSpace(filtersJson))
        {
            try
            {
                using var doc = JsonDocument.Parse(filtersJson);
                if (doc.RootElement.ValueKind != JsonValueKind.Array)
                {
                    return CrmQueryPlanParseResult.Fail("filtersJson must be a JSON array.");
                }

                foreach (var el in doc.RootElement.EnumerateArray())
                {
                    if (filters.Count >= CrmQueryLimits.MaxFilters)
                    {
                        return CrmQueryPlanParseResult.Fail($"A maximum of {CrmQueryLimits.MaxFilters} filters is allowed.");
                    }

                    var field = GetString(el, "field") ?? GetString(el, "Field");
                    var opRaw = GetString(el, "operator") ?? GetString(el, "op") ?? GetString(el, "Operator");
                    var value = GetString(el, "value") ?? GetString(el, "Value");
                    if (string.IsNullOrWhiteSpace(field) || string.IsNullOrWhiteSpace(opRaw) || value is null)
                    {
                        return CrmQueryPlanParseResult.Fail("Each filter requires field, operator, and value.");
                    }

                    if (!TryParseOperator(opRaw, out var filterOp))
                    {
                        return CrmQueryPlanParseResult.Fail($"Unsupported filter operator '{opRaw}'.");
                    }

                    filters.Add(new CrmQueryFilter { Field = field.Trim(), Operator = filterOp, Value = value.Trim() });
                }
            }
            catch (JsonException)
            {
                return CrmQueryPlanParseResult.Fail("filtersJson is not valid JSON.");
            }
        }

        CrmRelativeDateFilter? relative = null;
        if (!string.IsNullOrWhiteSpace(relativeDateField) || !string.IsNullOrWhiteSpace(relativeDateToken))
        {
            if (string.IsNullOrWhiteSpace(relativeDateField) || string.IsNullOrWhiteSpace(relativeDateToken))
            {
                return CrmQueryPlanParseResult.Fail("relativeDateField and relativeDateToken must be provided together.");
            }

            if (!RelativeDateResolver.TryParseToken(relativeDateToken, out var token))
            {
                return CrmQueryPlanParseResult.Fail($"Unsupported relativeDateToken '{relativeDateToken}'.");
            }

            relative = new CrmRelativeDateFilter { Field = relativeDateField.Trim(), Token = token };
        }

        var sorts = new List<CrmQuerySort>();
        if (!string.IsNullOrWhiteSpace(sortJson))
        {
            try
            {
                using var doc = JsonDocument.Parse(sortJson);
                if (doc.RootElement.ValueKind != JsonValueKind.Array)
                {
                    return CrmQueryPlanParseResult.Fail("sortJson must be a JSON array.");
                }

                foreach (var el in doc.RootElement.EnumerateArray())
                {
                    if (sorts.Count >= CrmQueryLimits.MaxSortFields)
                    {
                        break;
                    }

                    var field = GetString(el, "field") ?? GetString(el, "Field");
                    var dirRaw = GetString(el, "direction") ?? GetString(el, "Direction") ?? "desc";
                    if (string.IsNullOrWhiteSpace(field))
                    {
                        continue;
                    }

                    sorts.Add(new CrmQuerySort
                    {
                        Field = field.Trim(),
                        Direction = dirRaw.Equals("asc", StringComparison.OrdinalIgnoreCase)
                            ? CrmSortDirection.Asc
                            : CrmSortDirection.Desc
                    });
                }
            }
            catch (JsonException)
            {
                return CrmQueryPlanParseResult.Fail("sortJson is not valid JSON.");
            }
        }
        else if (!string.IsNullOrWhiteSpace(sortField))
        {
            var direction = string.Equals(sortDirection, "asc", StringComparison.OrdinalIgnoreCase)
                ? CrmSortDirection.Asc
                : CrmSortDirection.Desc;
            sorts.Add(new CrmQuerySort { Field = sortField.Trim(), Direction = direction });
        }

        var plan = new CrmQueryPlan
        {
            Entity = entity.Trim(),
            Operation = op,
            Select = select,
            Filters = filters,
            RelativeDate = relative,
            Sort = sorts,
            Limit = limit ?? CrmQueryLimits.DefaultLimit,
            RelatedFromEntity = string.IsNullOrWhiteSpace(relatedFromEntity) ? null : relatedFromEntity.Trim(),
            RelatedFromId = string.IsNullOrWhiteSpace(relatedFromId) ? null : relatedFromId.Trim()
        };

        return CrmQueryPlanParseResult.Ok(plan);
    }

    public static CrmQueryPlanParseResult ParseFromToolArguments(IDictionary<string, object?>? arguments)
    {
        if (arguments is null)
        {
            return CrmQueryPlanParseResult.Fail("entity is required.");
        }

        return Parse(
            GetArgString(arguments, "entity"),
            GetArgString(arguments, "operation"),
            GetArgString(arguments, "selectJson") ?? GetArgString(arguments, "select"),
            GetArgString(arguments, "filtersJson") ?? GetArgString(arguments, "filters"),
            GetArgString(arguments, "relativeDateField"),
            GetArgString(arguments, "relativeDateToken"),
            GetArgString(arguments, "sortJson"),
            GetArgString(arguments, "sortField"),
            GetArgString(arguments, "sortDirection"),
            GetArgInt(arguments, "limit"),
            GetArgString(arguments, "relatedFromEntity"),
            GetArgString(arguments, "relatedFromId"));
    }

    private static bool TryParseOperation(string? raw, out CrmQueryOperation op, out string? error)
    {
        op = CrmQueryOperation.List;
        error = null;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return true;
        }

        var key = raw.Trim().ToLowerInvariant();
        if (key is "list" or "search" or "query")
        {
            op = CrmQueryOperation.List;
            return true;
        }

        if (key is "count")
        {
            op = CrmQueryOperation.Count;
            return true;
        }

        error = "operation must be 'list' or 'count'.";
        return false;
    }

    private static bool TryParseOperator(string raw, out CrmFilterOperator op)
    {
        op = default;
        var key = raw.Trim().ToLowerInvariant();
        return key switch
        {
            "eq" or "equals" or "=" => Assign(CrmFilterOperator.Eq, out op),
            "ne" or "!=" => Assign(CrmFilterOperator.Ne, out op),
            "gt" or ">" => Assign(CrmFilterOperator.Gt, out op),
            "ge" or ">=" => Assign(CrmFilterOperator.Ge, out op),
            "lt" or "<" => Assign(CrmFilterOperator.Lt, out op),
            "le" or "<=" => Assign(CrmFilterOperator.Le, out op),
            "contains" or "contain" => Assign(CrmFilterOperator.Contains, out op),
            _ => false
        };
    }

    private static bool Assign(CrmFilterOperator value, out CrmFilterOperator op)
    {
        op = value;
        return true;
    }

    private static IReadOnlyList<string> ParseStringArray(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return Array.Empty<string>();
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                return Array.Empty<string>();
            }

            return doc.RootElement.EnumerateArray()
                .Select(e => e.GetString())
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Select(s => s!.Trim())
                .Take(CrmQueryLimits.MaxSelectFields)
                .ToArray();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private static string? GetString(JsonElement el, string name) =>
        el.TryGetProperty(name, out var p)
            ? p.ValueKind switch
            {
                JsonValueKind.String => p.GetString(),
                JsonValueKind.Number => p.GetRawText(),
                _ => p.ToString()
            }
            : null;

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
            JsonElement je when je.ValueKind == JsonValueKind.Array => je.GetRawText(),
            JsonElement je => je.ToString(),
            _ => Convert.ToString(raw, CultureInfo.InvariantCulture)
        };
    }

    private static int? GetArgInt(IDictionary<string, object?> args, string name)
    {
        if (!args.TryGetValue(name, out var raw) || raw is null)
        {
            return null;
        }

        return raw switch
        {
            int i => i,
            long l => (int)l,
            double d => (int)d,
            JsonElement je when je.TryGetInt32(out var ji) => ji,
            string s when int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => null
        };
    }
}

public sealed class CrmQueryPlanParseResult
{
    public bool Success { get; init; }
    public string? Error { get; init; }
    public CrmQueryPlan? Plan { get; init; }

    public static CrmQueryPlanParseResult Ok(CrmQueryPlan plan) =>
        new() { Success = true, Plan = plan };

    public static CrmQueryPlanParseResult Fail(string error) =>
        new() { Success = false, Error = error };
}
