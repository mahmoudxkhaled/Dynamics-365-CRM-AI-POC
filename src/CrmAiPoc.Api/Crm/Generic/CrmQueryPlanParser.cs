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
        string? relatedFromId,
        string? aggregatesJson = null,
        string? groupByJson = null,
        string? linksJson = null,
        string? clientSideSortJson = null,
        string? dateRangeJson = null)
    {
        if (string.IsNullOrWhiteSpace(entity))
        {
            return CrmQueryPlanParseResult.Fail("entity is required (approved logical name).");
        }

        if (!TryParseOperation(operation, out var op, out var opError))
        {
            return CrmQueryPlanParseResult.Fail(opError!);
        }

        if (!TryParseAggregates(aggregatesJson, out var aggregates, out var aggError))
        {
            return CrmQueryPlanParseResult.Fail(aggError!);
        }

        if (!TryParseGroupBy(groupByJson, out var groupBy, out var groupError))
        {
            return CrmQueryPlanParseResult.Fail(groupError!);
        }

        // Technical normalization: month buckets without year collide across years (Jan 2024 vs Jan 2025).
        groupBy = EnsureYearContextForMonthGrouping(groupBy);

        if (!TryParseLinks(linksJson, out var links, out var linksError))
        {
            return CrmQueryPlanParseResult.Fail(linksError!);
        }

        if (op == CrmQueryOperation.Aggregate && aggregates.Count == 0)
        {
            return CrmQueryPlanParseResult.Fail("operation=aggregate requires aggregatesJson.");
        }

        if (op != CrmQueryOperation.Aggregate && (aggregates.Count > 0 || groupBy.Count > 0))
        {
            // Promote to aggregate when the model supplied aggregate args but forgot operation.
            op = CrmQueryOperation.Aggregate;
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

        if (!TryParseDateFilter(
                relativeDateField,
                relativeDateToken,
                dateRangeJson,
                out var relative,
                out var dateError))
        {
            return CrmQueryPlanParseResult.Fail(dateError!);
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

        if (!TryParseSortList(clientSideSortJson, CrmQueryLimits.MaxSortFields, out var clientSideSorts, out var clientSortError))
        {
            return CrmQueryPlanParseResult.Fail(clientSortError!);
        }

        if (op == CrmQueryOperation.Aggregate && links.Count > 0)
        {
            return CrmQueryPlanParseResult.Fail(
                "linksJson is not supported with operation=aggregate in this phase.");
        }

        var plan = new CrmQueryPlan
        {
            Entity = entity.Trim(),
            Operation = op,
            Select = select,
            Filters = filters,
            RelativeDate = relative,
            Sort = sorts,
            ClientSideSort = clientSideSorts,
            Limit = limit ?? CrmQueryLimits.DefaultLimit,
            RelatedFromEntity = string.IsNullOrWhiteSpace(relatedFromEntity) ? null : relatedFromEntity.Trim(),
            RelatedFromId = string.IsNullOrWhiteSpace(relatedFromId) ? null : relatedFromId.Trim(),
            Aggregates = aggregates,
            GroupBy = groupBy,
            Links = links
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
            GetArgString(arguments, "relatedFromId"),
            GetArgString(arguments, "aggregatesJson") ?? GetArgString(arguments, "aggregates"),
            GetArgString(arguments, "groupByJson") ?? GetArgString(arguments, "groupBy"),
            GetArgString(arguments, "linksJson") ?? GetArgString(arguments, "links"),
            GetArgString(arguments, "clientSideSortJson"),
            GetArgString(arguments, "dateRangeJson")
            ?? GetArgString(arguments, "dateRange")
            ?? GetArgObjectJson(arguments, "dateRange")
            ?? GetArgObjectJson(arguments, "dateRangeJson"));
    }

    /// <summary>
    /// When grouping by calendar month without year, prepend a year grouping on the same field
    /// so multi-year ranges cannot mix January 2024 with January 2025. Not a semantic rewrite.
    /// </summary>
    internal static IReadOnlyList<CrmGroupBySpec> EnsureYearContextForMonthGrouping(
        IReadOnlyList<CrmGroupBySpec> groupBy)
    {
        if (groupBy.Count == 0)
        {
            return groupBy;
        }

        var month = groupBy.FirstOrDefault(g => g.DateGrouping == CrmDateGrouping.Month);
        if (month is null || groupBy.Any(g => g.DateGrouping == CrmDateGrouping.Year))
        {
            return groupBy;
        }

        var yearAlias = "year";
        if (groupBy.Any(g => g.Alias.Equals(yearAlias, StringComparison.OrdinalIgnoreCase)))
        {
            yearAlias = "yr";
        }

        var year = new CrmGroupBySpec
        {
            Field = month.Field,
            Alias = yearAlias,
            DateGrouping = CrmDateGrouping.Year
        };

        return new[] { year }.Concat(groupBy).ToArray();
    }

    private static bool TryParseDateFilter(
        string? relativeDateField,
        string? relativeDateToken,
        string? dateRangeJson,
        out CrmRelativeDateFilter? relative,
        out string? error)
    {
        relative = null;
        error = null;

        var hasField = !string.IsNullOrWhiteSpace(relativeDateField);
        var hasToken = !string.IsNullOrWhiteSpace(relativeDateToken);
        var hasRange = !string.IsNullOrWhiteSpace(dateRangeJson);

        if (!hasField && !hasToken && !hasRange)
        {
            return true;
        }

        if (!hasField)
        {
            error = "relativeDateField is required when relativeDateToken or dateRangeJson is provided.";
            return false;
        }

        if (hasRange)
        {
            if (!TryParseDateRangeExpression(dateRangeJson!, out var expression, out error))
            {
                return false;
            }

            relative = new CrmRelativeDateFilter
            {
                Field = relativeDateField!.Trim(),
                Expression = expression
            };
            return true;
        }

        if (!hasToken)
        {
            error = "Provide dateRangeJson or relativeDateToken together with relativeDateField.";
            return false;
        }

        if (!RelativeDateResolver.TryParseToken(relativeDateToken, out var token))
        {
            error =
                $"Unsupported relativeDateToken '{relativeDateToken}'. Prefer dateRangeJson with a structured kind " +
                "(e.g. last_n_calendar_months with N, this_calendar_year, calendar_month_range, absolute).";
            return false;
        }

        relative = new CrmRelativeDateFilter { Field = relativeDateField!.Trim(), Token = token };
        return true;
    }

    private static bool TryParseDateRangeExpression(
        string json,
        out CrmDateRangeExpression expression,
        out string? error)
    {
        expression = null!;
        error = null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                error = "dateRangeJson/dateRange must be a JSON object.";
                return false;
            }

            if (!TryResolveDateRangeKindAndValues(root, out var kind, out var n, out var year, out var quarter,
                    out var monthStart, out var monthEnd, out var start, out var endExclusive, out error))
            {
                return false;
            }

            expression = new CrmDateRangeExpression
            {
                Kind = kind,
                N = n,
                Year = year,
                Quarter = quarter,
                MonthStart = monthStart,
                MonthEnd = monthEnd,
                AbsoluteStartUtc = start,
                AbsoluteEndExclusiveUtc = endExclusive
            };

            // Fail fast on invalid combinations using the configured clock/timezone.
            _ = DateRangeResolver.Resolve(
                expression,
                DateTimeOffset.UtcNow,
                BusinessTimeZone.Resolve());
            return true;
        }
        catch (JsonException)
        {
            error = "dateRangeJson is not valid JSON.";
            return false;
        }
        catch (ArgumentException ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// Canonical form requires <c>kind</c>. Also accepts narrowly equivalent unambiguous shorthands
    /// such as <c>{"calendar_year":2025}</c> → kind=calendar_year, year=2025. Does not invent intent.
    /// </summary>
    internal static bool TryResolveDateRangeKindAndValues(
        JsonElement root,
        out CrmDateRangeKind kind,
        out int? n,
        out int? year,
        out int? quarter,
        out int? monthStart,
        out int? monthEnd,
        out DateTimeOffset? start,
        out DateTimeOffset? endExclusive,
        out string? error)
    {
        kind = default;
        n = GetInt(root, "n") ?? GetInt(root, "N");
        year = GetInt(root, "year") ?? GetInt(root, "Year");
        quarter = GetInt(root, "quarter") ?? GetInt(root, "Quarter");
        monthStart = GetInt(root, "monthStart") ?? GetInt(root, "MonthStart");
        monthEnd = GetInt(root, "monthEnd") ?? GetInt(root, "MonthEnd");
        start = GetDateTimeOffset(
            root, "start", "Start", "absoluteStart", "AbsoluteStart", "absoluteStartUtc", "startUtc");
        endExclusive = GetDateTimeOffset(
            root, "endExclusive", "EndExclusive", "absoluteEndExclusive", "AbsoluteEndExclusive",
            "absoluteEndExclusiveUtc", "endExclusiveUtc", "end");
        error = null;

        var kindRaw = GetString(root, "kind") ?? GetString(root, "Kind");
        if (!string.IsNullOrWhiteSpace(kindRaw))
        {
            if (!DateRangeResolver.TryParseKind(kindRaw, out kind))
            {
                error =
                    $"Unsupported dateRangeJson kind '{kindRaw}'. " +
                    "Use absolute, last_n_days, last_n_weeks, last_n_calendar_months, last_n_rolling_months, " +
                    "last_n_years, this_calendar_month, this_calendar_quarter, this_calendar_year, this_week, " +
                    "previous_calendar_month, previous_calendar_quarter, previous_calendar_year, " +
                    "calendar_year, calendar_quarter, or calendar_month_range.";
                return false;
            }

            return true;
        }

        // --- Unambiguous shorthand normalization (no invented business intent) ---
        if (TryGetKindShorthandProperty(root, out var shorthandKind, out var shorthandValue))
        {
            kind = shorthandKind;
            switch (kind)
            {
                case CrmDateRangeKind.CalendarYear:
                case CrmDateRangeKind.LastNDays:
                case CrmDateRangeKind.LastNWeeks:
                case CrmDateRangeKind.LastNCalendarMonths:
                case CrmDateRangeKind.LastNRollingMonths:
                case CrmDateRangeKind.LastNYears:
                    if (shorthandValue is int iv)
                    {
                        if (kind == CrmDateRangeKind.CalendarYear)
                        {
                            year ??= iv;
                        }
                        else
                        {
                            n ??= iv;
                        }
                    }

                    break;
                case CrmDateRangeKind.CalendarQuarter:
                    if (shorthandValue is int q)
                    {
                        quarter ??= q;
                    }

                    break;
            }

            return true;
        }

        // { "year": 2025, "quarter": 2 }
        if (year is not null && quarter is not null && monthStart is null && monthEnd is null &&
            start is null && n is null)
        {
            kind = CrmDateRangeKind.CalendarQuarter;
            return true;
        }

        // { "year": 2025, "monthStart": 1, "monthEnd": 6 }
        if (year is not null && monthStart is not null && n is null && quarter is null && start is null)
        {
            kind = CrmDateRangeKind.CalendarMonthRange;
            monthEnd ??= monthStart;
            return true;
        }

        // { "year": 2025 } alone → calendar_year
        if (year is not null && quarter is null && monthStart is null && monthEnd is null &&
            n is null && start is null && endExclusive is null)
        {
            kind = CrmDateRangeKind.CalendarYear;
            return true;
        }

        // { "start": "...", "endExclusive": "..." }
        if (start is not null && endExclusive is not null && year is null && n is null)
        {
            kind = CrmDateRangeKind.Absolute;
            return true;
        }

        error =
            "dateRange requires property 'kind' (example: {\"kind\":\"calendar_year\",\"year\":2025}). " +
            "Supported unambiguous shorthand: {\"calendar_year\":2025}, {\"year\":2025}, " +
            "{\"last_n_calendar_months\":8}, {\"year\":2025,\"quarter\":2}, " +
            "{\"year\":2025,\"monthStart\":1,\"monthEnd\":6}, {\"start\":\"...\",\"endExclusive\":\"...\"}.";
        return false;
    }

    private static bool TryGetKindShorthandProperty(
        JsonElement root,
        out CrmDateRangeKind kind,
        out object? value)
    {
        kind = default;
        value = null;
        string? matchedKey = null;
        JsonElement matchedProp = default;

        foreach (var prop in root.EnumerateObject())
        {
            if (prop.NameEquals("kind") || prop.NameEquals("Kind") ||
                prop.NameEquals("year") || prop.NameEquals("Year") ||
                prop.NameEquals("n") || prop.NameEquals("N") ||
                prop.NameEquals("quarter") || prop.NameEquals("Quarter") ||
                prop.NameEquals("monthStart") || prop.NameEquals("MonthStart") ||
                prop.NameEquals("monthEnd") || prop.NameEquals("MonthEnd") ||
                prop.NameEquals("start") || prop.NameEquals("Start") ||
                prop.NameEquals("end") || prop.NameEquals("endExclusive") ||
                prop.NameEquals("EndExclusive"))
            {
                continue;
            }

            if (!DateRangeResolver.TryParseKind(prop.Name, out _))
            {
                continue;
            }

            // Ambiguous if multiple kind-named keys present.
            if (matchedKey is not null)
            {
                return false;
            }

            matchedKey = prop.Name;
            matchedProp = prop.Value;
        }

        if (matchedKey is null || !DateRangeResolver.TryParseKind(matchedKey, out kind))
        {
            return false;
        }

        value = matchedProp.ValueKind switch
        {
            JsonValueKind.Number when matchedProp.TryGetInt32(out var i) => i,
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String when int.TryParse(
                matchedProp.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var p) => p,
            _ => null
        };
        return true;
    }

    private static int? GetInt(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var p))
        {
            return null;
        }

        if (p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out var i))
        {
            return i;
        }

        if (p.ValueKind == JsonValueKind.String &&
            int.TryParse(p.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out i))
        {
            return i;
        }

        return null;
    }

    private static DateTimeOffset? GetDateTimeOffset(JsonElement el, params string[] names)
    {
        foreach (var name in names)
        {
            if (!el.TryGetProperty(name, out var p))
            {
                continue;
            }

            if (p.ValueKind == JsonValueKind.String &&
                DateTimeOffset.TryParse(
                    p.GetString(),
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind,
                    out var dto))
            {
                return dto.ToUniversalTime();
            }
        }

        return null;
    }

    private static bool TryParseSortList(
        string? json,
        int maxFields,
        out IReadOnlyList<CrmQuerySort> sorts,
        out string? error)
    {
        sorts = Array.Empty<CrmQuerySort>();
        error = null;
        if (string.IsNullOrWhiteSpace(json))
        {
            return true;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                error = "clientSideSortJson must be a JSON array.";
                return false;
            }

            var list = new List<CrmQuerySort>();
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                if (list.Count >= maxFields)
                {
                    error = $"A maximum of {maxFields} client-side sort fields is allowed.";
                    return false;
                }

                var field = GetString(el, "field") ?? GetString(el, "Field");
                if (string.IsNullOrWhiteSpace(field))
                {
                    error = "Each client-side sort requires field.";
                    return false;
                }

                var dirRaw = GetString(el, "direction") ?? GetString(el, "Direction") ?? "desc";
                list.Add(new CrmQuerySort
                {
                    Field = field.Trim(),
                    Direction = string.Equals(dirRaw, "asc", StringComparison.OrdinalIgnoreCase)
                        ? CrmSortDirection.Asc
                        : CrmSortDirection.Desc
                });
            }

            sorts = list;
            return true;
        }
        catch (JsonException)
        {
            error = "clientSideSortJson is not valid JSON.";
            return false;
        }
    }

    private static bool TryParseLinks(
        string? json,
        out IReadOnlyList<CrmLinkSpec> links,
        out string? error)
    {
        links = Array.Empty<CrmLinkSpec>();
        error = null;
        if (string.IsNullOrWhiteSpace(json))
        {
            return true;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                error = "linksJson must be a JSON array.";
                return false;
            }

            var list = new List<CrmLinkSpec>();
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                if (list.Count >= CrmQueryLimits.MaxLinkedEntities)
                {
                    error = $"A maximum of {CrmQueryLimits.MaxLinkedEntities} top-level links is allowed.";
                    return false;
                }

                if (!TryParseLinkElement(el, depth: 1, out var link, out error))
                {
                    return false;
                }

                list.Add(link!);
            }

            links = list;
            return true;
        }
        catch (JsonException)
        {
            error = "linksJson is not valid JSON.";
            return false;
        }
    }

    private static bool TryParseLinkElement(
        JsonElement el,
        int depth,
        out CrmLinkSpec? link,
        out string? error)
    {
        link = null;
        error = null;
        if (depth > CrmQueryLimits.MaxLinkDepth)
        {
            error = $"Link depth exceeds MaxLinkDepth={CrmQueryLimits.MaxLinkDepth}.";
            return false;
        }

        if (el.ValueKind != JsonValueKind.Object)
        {
            error = "Each link must be a JSON object.";
            return false;
        }

        var entity = GetString(el, "entity") ?? GetString(el, "Entity");
        if (string.IsNullOrWhiteSpace(entity))
        {
            error = "Each link requires entity.";
            return false;
        }

        var relationship = GetString(el, "relationship") ?? GetString(el, "Relationship");
        var purpose =
            GetString(el, "purpose") ??
            GetString(el, "Purpose") ??
            GetString(el, "relationshipPurpose") ??
            GetString(el, "RelationshipPurpose") ??
            GetString(el, "label") ??
            GetString(el, "Label") ??
            GetString(el, "displayName") ??
            GetString(el, "DisplayName");
        var alias = GetString(el, "alias") ?? GetString(el, "Alias");
        var joinRaw = GetString(el, "joinType") ?? GetString(el, "JoinType") ?? "outer";
        var joinType = joinRaw.Equals("inner", StringComparison.OrdinalIgnoreCase)
            ? CrmLinkJoinType.Inner
            : CrmLinkJoinType.Outer;

        var select = Array.Empty<string>();
        if (el.TryGetProperty("select", out var selectEl) || el.TryGetProperty("Select", out selectEl))
        {
            if (selectEl.ValueKind == JsonValueKind.Array)
            {
                select = selectEl.EnumerateArray()
                    .Select(e => e.GetString())
                    .Where(s => !string.IsNullOrWhiteSpace(s))
                    .Select(s => s!.Trim())
                    .Take(CrmQueryLimits.MaxLinkSelectFields)
                    .ToArray();
            }
            else if (selectEl.ValueKind == JsonValueKind.String)
            {
                select = ParseStringArray(selectEl.GetString()).ToArray();
            }
        }

        var filters = new List<CrmQueryFilter>();
        if (el.TryGetProperty("filters", out var filtersEl) || el.TryGetProperty("Filters", out filtersEl))
        {
            if (filtersEl.ValueKind != JsonValueKind.Array)
            {
                error = "link.filters must be a JSON array.";
                return false;
            }

            foreach (var filterEl in filtersEl.EnumerateArray())
            {
                if (filters.Count >= CrmQueryLimits.MaxFilters)
                {
                    error = $"A maximum of {CrmQueryLimits.MaxFilters} related filters is allowed per link.";
                    return false;
                }

                var field = GetString(filterEl, "field") ?? GetString(filterEl, "Field");
                var opRaw = GetString(filterEl, "operator") ?? GetString(filterEl, "op") ?? GetString(filterEl, "Operator");
                var value = GetString(filterEl, "value") ?? GetString(filterEl, "Value");
                if (string.IsNullOrWhiteSpace(field) || string.IsNullOrWhiteSpace(opRaw) || value is null)
                {
                    error = "Each related filter requires field, operator, and value.";
                    return false;
                }

                if (!TryParseOperator(opRaw, out var filterOp))
                {
                    error = $"Unsupported related filter operator '{opRaw}'.";
                    return false;
                }

                filters.Add(new CrmQueryFilter { Field = field.Trim(), Operator = filterOp, Value = value.Trim() });
            }
        }

        var children = new List<CrmLinkSpec>();
        if (el.TryGetProperty("links", out var linksEl) || el.TryGetProperty("Links", out linksEl))
        {
            if (linksEl.ValueKind != JsonValueKind.Array)
            {
                error = "link.links must be a JSON array.";
                return false;
            }

            foreach (var childEl in linksEl.EnumerateArray())
            {
                if (!TryParseLinkElement(childEl, depth + 1, out var child, out error))
                {
                    return false;
                }

                children.Add(child!);
            }
        }

        link = new CrmLinkSpec
        {
            Entity = entity.Trim(),
            Relationship = string.IsNullOrWhiteSpace(relationship) ? null : relationship.Trim(),
            RelationshipPurpose = string.IsNullOrWhiteSpace(purpose) ? null : purpose.Trim(),
            JoinType = joinType,
            Alias = string.IsNullOrWhiteSpace(alias) ? null : alias.Trim(),
            Select = select,
            Filters = filters,
            Links = children
        };
        return true;
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

        if (key is "aggregate" or "aggregates" or "report" or "groupby")
        {
            op = CrmQueryOperation.Aggregate;
            return true;
        }

        error = "operation must be 'list', 'count', or 'aggregate'.";
        return false;
    }

    private static bool TryParseAggregates(
        string? json,
        out IReadOnlyList<CrmAggregateSpec> aggregates,
        out string? error)
    {
        aggregates = Array.Empty<CrmAggregateSpec>();
        error = null;
        if (string.IsNullOrWhiteSpace(json))
        {
            return true;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                error = "aggregatesJson must be a JSON array.";
                return false;
            }

            var list = new List<CrmAggregateSpec>();
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                if (list.Count >= CrmQueryLimits.MaxAggregates)
                {
                    error = $"A maximum of {CrmQueryLimits.MaxAggregates} aggregates is allowed.";
                    return false;
                }

                var fnRaw = GetString(el, "function") ?? GetString(el, "aggregate") ?? GetString(el, "Function");
                var field = GetString(el, "field") ?? GetString(el, "Field");
                var alias = GetString(el, "alias") ?? GetString(el, "Alias") ?? field;
                if (string.IsNullOrWhiteSpace(fnRaw) || string.IsNullOrWhiteSpace(field))
                {
                    error = "Each aggregate requires function and field.";
                    return false;
                }

                if (!TryParseAggregateFunction(fnRaw, out var fn))
                {
                    error = $"Unsupported aggregate function '{fnRaw}'.";
                    return false;
                }

                list.Add(new CrmAggregateSpec
                {
                    Function = fn,
                    Field = field.Trim(),
                    Alias = string.IsNullOrWhiteSpace(alias) ? field.Trim() : alias.Trim()
                });
            }

            aggregates = list;
            return true;
        }
        catch (JsonException)
        {
            error = "aggregatesJson is not valid JSON.";
            return false;
        }
    }

    private static bool TryParseGroupBy(
        string? json,
        out IReadOnlyList<CrmGroupBySpec> groupBy,
        out string? error)
    {
        groupBy = Array.Empty<CrmGroupBySpec>();
        error = null;
        if (string.IsNullOrWhiteSpace(json))
        {
            return true;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                error = "groupByJson must be a JSON array.";
                return false;
            }

            var list = new List<CrmGroupBySpec>();
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                if (list.Count >= CrmQueryLimits.MaxGroupByFields)
                {
                    error = $"A maximum of {CrmQueryLimits.MaxGroupByFields} groupBy fields is allowed.";
                    return false;
                }

                var field = GetString(el, "field") ?? GetString(el, "Field");
                var alias = GetString(el, "alias") ?? GetString(el, "Alias") ?? field;
                var dateRaw = GetString(el, "dateGrouping") ?? GetString(el, "DateGrouping");
                if (string.IsNullOrWhiteSpace(field))
                {
                    error = "Each groupBy requires field.";
                    return false;
                }

                if (!TryParseDateGrouping(dateRaw, out var dateGrouping))
                {
                    error = $"Unsupported dateGrouping '{dateRaw}'.";
                    return false;
                }

                list.Add(new CrmGroupBySpec
                {
                    Field = field.Trim(),
                    Alias = string.IsNullOrWhiteSpace(alias) ? field.Trim() : alias.Trim(),
                    DateGrouping = dateGrouping
                });
            }

            groupBy = list;
            return true;
        }
        catch (JsonException)
        {
            error = "groupByJson is not valid JSON.";
            return false;
        }
    }

    private static bool TryParseAggregateFunction(string raw, out CrmAggregateFunction fn)
    {
        fn = default;
        var key = raw.Trim().ToLowerInvariant();
        return key switch
        {
            "count" or "countcolumn" or "countdistinct" => AssignFn(CrmAggregateFunction.Count, out fn),
            "sum" => AssignFn(CrmAggregateFunction.Sum, out fn),
            "avg" or "average" => AssignFn(CrmAggregateFunction.Avg, out fn),
            "min" => AssignFn(CrmAggregateFunction.Min, out fn),
            "max" => AssignFn(CrmAggregateFunction.Max, out fn),
            _ => false
        };
    }

    private static bool TryParseDateGrouping(string? raw, out CrmDateGrouping grouping)
    {
        grouping = CrmDateGrouping.None;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return true;
        }

        var key = raw.Trim().ToLowerInvariant();
        return key switch
        {
            "none" or "" => AssignDg(CrmDateGrouping.None, out grouping),
            "year" => AssignDg(CrmDateGrouping.Year, out grouping),
            "month" => AssignDg(CrmDateGrouping.Month, out grouping),
            "day" => AssignDg(CrmDateGrouping.Day, out grouping),
            "week" => AssignDg(CrmDateGrouping.Week, out grouping),
            "quarter" => AssignDg(CrmDateGrouping.Quarter, out grouping),
            _ => false
        };
    }

    private static bool AssignFn(CrmAggregateFunction value, out CrmAggregateFunction fn)
    {
        fn = value;
        return true;
    }

    private static bool AssignDg(CrmDateGrouping value, out CrmDateGrouping grouping)
    {
        grouping = value;
        return true;
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
                JsonValueKind.Null or JsonValueKind.Undefined => null,
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
            JsonElement je when je.ValueKind is JsonValueKind.Array or JsonValueKind.Object => je.GetRawText(),
            JsonElement je => je.ToString(),
            _ => Convert.ToString(raw, CultureInfo.InvariantCulture)
        };
    }

    /// <summary>
    /// Serializes a typed/object tool argument (e.g. dateRange) to JSON text for the string-based parser.
    /// </summary>
    private static string? GetArgObjectJson(IDictionary<string, object?> args, string name)
    {
        if (!args.TryGetValue(name, out var raw) || raw is null)
        {
            return null;
        }

        return raw switch
        {
            string s when !string.IsNullOrWhiteSpace(s) => s,
            JsonElement je when je.ValueKind == JsonValueKind.Object => je.GetRawText(),
            JsonElement je when je.ValueKind == JsonValueKind.String => je.GetString(),
            IDictionary<string, object?> dict =>
                JsonSerializer.Serialize(dict),
            _ when raw.GetType().IsClass => JsonSerializer.Serialize(raw),
            _ => null
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
