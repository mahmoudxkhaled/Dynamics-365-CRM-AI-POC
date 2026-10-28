using System.Globalization;
using System.Text.Json;

namespace CrmAiPoc.Api.Crm.Query;

/// <summary>
/// Parses AI tool arguments into a <see cref="SalesOrderQueryPlan"/> without trusting raw OData.
/// </summary>
public static class SalesOrderQueryPlanParser
{
    public static SalesOrderQueryPlanParseResult Parse(
        string? operation,
        string? filtersJson,
        string? relativeDateField,
        string? relativeDateToken,
        string? sortField,
        string? sortDirection,
        int? limit)
    {
        if (!TryParseOperation(operation, out var op, out var opError))
        {
            return SalesOrderQueryPlanParseResult.Fail(opError!);
        }

        var filters = new List<SalesOrderQueryFilter>();
        if (!string.IsNullOrWhiteSpace(filtersJson))
        {
            try
            {
                using var doc = JsonDocument.Parse(filtersJson);
                if (doc.RootElement.ValueKind != JsonValueKind.Array)
                {
                    return SalesOrderQueryPlanParseResult.Fail("filtersJson must be a JSON array.");
                }

                foreach (var el in doc.RootElement.EnumerateArray())
                {
                    if (filters.Count >= SalesOrderQueryLimits.MaxFilters)
                    {
                        return SalesOrderQueryPlanParseResult.Fail(
                            $"A maximum of {SalesOrderQueryLimits.MaxFilters} filters is allowed.");
                    }

                    var field = GetString(el, "field") ?? GetString(el, "Field");
                    var opRaw = GetString(el, "operator") ?? GetString(el, "op") ?? GetString(el, "Operator");
                    var value = GetString(el, "value") ?? GetString(el, "Value");
                    if (string.IsNullOrWhiteSpace(field) || string.IsNullOrWhiteSpace(opRaw) || value is null)
                    {
                        return SalesOrderQueryPlanParseResult.Fail(
                            "Each filter requires field, operator, and value.");
                    }

                    if (!TryParseOperator(opRaw, out var filterOp))
                    {
                        return SalesOrderQueryPlanParseResult.Fail($"Unsupported filter operator '{opRaw}'.");
                    }

                    filters.Add(new SalesOrderQueryFilter
                    {
                        Field = field.Trim(),
                        Operator = filterOp,
                        Value = value.Trim()
                    });
                }
            }
            catch (JsonException)
            {
                return SalesOrderQueryPlanParseResult.Fail("filtersJson is not valid JSON.");
            }
        }

        SalesOrderRelativeDateFilter? relative = null;
        if (!string.IsNullOrWhiteSpace(relativeDateField) || !string.IsNullOrWhiteSpace(relativeDateToken))
        {
            if (string.IsNullOrWhiteSpace(relativeDateField) || string.IsNullOrWhiteSpace(relativeDateToken))
            {
                return SalesOrderQueryPlanParseResult.Fail(
                    "relativeDateField and relativeDateToken must be provided together.");
            }

            if (!RelativeDateResolver.TryParseToken(relativeDateToken, out var token))
            {
                return SalesOrderQueryPlanParseResult.Fail(
                    $"Unsupported relativeDateToken '{relativeDateToken}'. Use last_calendar_month, this_calendar_month, this_week, last_7_days, or last_30_days.");
            }

            relative = new SalesOrderRelativeDateFilter
            {
                Field = relativeDateField.Trim(),
                Token = token
            };
        }

        var direction = SalesOrderSortDirection.Desc;
        if (!string.IsNullOrWhiteSpace(sortDirection))
        {
            var d = sortDirection.Trim();
            if (d.Equals("asc", StringComparison.OrdinalIgnoreCase))
            {
                direction = SalesOrderSortDirection.Asc;
            }
            else if (d.Equals("desc", StringComparison.OrdinalIgnoreCase))
            {
                direction = SalesOrderSortDirection.Desc;
            }
            else
            {
                return SalesOrderQueryPlanParseResult.Fail("sortDirection must be asc or desc.");
            }
        }

        var plan = new SalesOrderQueryPlan
        {
            Operation = op,
            Filters = filters,
            RelativeDate = relative,
            SortField = string.IsNullOrWhiteSpace(sortField) ? null : sortField.Trim(),
            SortDirection = direction,
            Limit = limit ?? SalesOrderQueryLimits.DefaultLimit
        };

        return SalesOrderQueryPlanParseResult.Ok(plan);
    }

    public static SalesOrderQueryPlanParseResult ParseFromToolArguments(IDictionary<string, object?>? arguments)
    {
        if (arguments is null)
        {
            return Parse("list", null, null, null, null, null, null);
        }

        return Parse(
            GetArgString(arguments, "operation"),
            GetArgString(arguments, "filtersJson") ?? GetArgString(arguments, "filters"),
            GetArgString(arguments, "relativeDateField"),
            GetArgString(arguments, "relativeDateToken"),
            GetArgString(arguments, "sortField"),
            GetArgString(arguments, "sortDirection"),
            GetArgInt(arguments, "limit"));
    }

    private static bool TryParseOperation(string? raw, out SalesOrderQueryOperation op, out string? error)
    {
        op = SalesOrderQueryOperation.List;
        error = null;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return true;
        }

        var key = raw.Trim().ToLowerInvariant();
        if (key is "list" or "search" or "query")
        {
            op = SalesOrderQueryOperation.List;
            return true;
        }

        if (key is "count")
        {
            op = SalesOrderQueryOperation.Count;
            return true;
        }

        error = "operation must be 'list' or 'count'.";
        return false;
    }

    private static bool TryParseOperator(string raw, out SalesOrderFilterOperator op)
    {
        op = default;
        var key = raw.Trim().ToLowerInvariant();
        return key switch
        {
            "eq" or "equals" or "=" => Assign(SalesOrderFilterOperator.Eq, out op),
            "ne" or "not_equals" or "!=" => Assign(SalesOrderFilterOperator.Ne, out op),
            "gt" or ">" => Assign(SalesOrderFilterOperator.Gt, out op),
            "ge" or ">=" => Assign(SalesOrderFilterOperator.Ge, out op),
            "lt" or "<" => Assign(SalesOrderFilterOperator.Lt, out op),
            "le" or "<=" => Assign(SalesOrderFilterOperator.Le, out op),
            "contains" or "contain" => Assign(SalesOrderFilterOperator.Contains, out op),
            _ => false
        };
    }

    private static bool Assign(SalesOrderFilterOperator value, out SalesOrderFilterOperator op)
    {
        op = value;
        return true;
    }

    private static string? GetString(JsonElement el, string name) =>
        el.TryGetProperty(name, out var p)
            ? p.ValueKind switch
            {
                JsonValueKind.String => p.GetString(),
                JsonValueKind.Number => p.GetRawText(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
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

public sealed class SalesOrderQueryPlanParseResult
{
    public bool Success { get; init; }
    public string? Error { get; init; }
    public SalesOrderQueryPlan? Plan { get; init; }

    public static SalesOrderQueryPlanParseResult Ok(SalesOrderQueryPlan plan) =>
        new() { Success = true, Plan = plan };

    public static SalesOrderQueryPlanParseResult Fail(string error) =>
        new() { Success = false, Error = error };
}
