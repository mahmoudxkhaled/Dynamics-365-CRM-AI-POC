using System.Globalization;
using System.Text;
using Microsoft.Extensions.Options;
using CrmAiPoc.Api.Configuration;

namespace CrmAiPoc.Api.Crm.Query;

/// <summary>
/// Validates a structured Sales Order query plan and builds a safe relative OData URI.
/// Field names, operators, and literals are never taken from unvalidated user/AI text.
/// </summary>
public sealed class SalesOrderODataQueryBuilder
{
    private readonly TimeZoneInfo _businessTz;
    private readonly Func<DateTimeOffset> _utcNow;

    public SalesOrderODataQueryBuilder(IOptions<CrmOptions>? crmOptions = null, Func<DateTimeOffset>? utcNow = null)
    {
        _businessTz = BusinessTimeZone.Resolve(crmOptions?.Value.BusinessTimeZoneId);
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    }

    public SalesOrderODataBuildResult Build(SalesOrderQueryPlan plan)
    {
        if (plan is null)
        {
            return SalesOrderODataBuildResult.Fail("Query plan is required.");
        }

        if (plan.Filters.Count > SalesOrderQueryLimits.MaxFilters)
        {
            return SalesOrderODataBuildResult.Fail(
                $"A maximum of {SalesOrderQueryLimits.MaxFilters} filters is allowed.");
        }

        var filterClauses = new List<string>();
        var summaries = new List<string>();

        if (plan.RelativeDate is not null)
        {
            var relative = BuildRelativeDate(plan.RelativeDate);
            if (!relative.Success)
            {
                return SalesOrderODataBuildResult.Fail(relative.Error!);
            }

            filterClauses.AddRange(relative.Clauses!);
            summaries.Add(relative.Summary!);
        }

        foreach (var filter in plan.Filters)
        {
            var built = BuildFilter(filter);
            if (!built.Success)
            {
                return SalesOrderODataBuildResult.Fail(built.Error!);
            }

            filterClauses.Add(built.Clause!);
            summaries.Add(built.Summary!);
        }

        string? orderby = null;
        string? sortSummary = null;
        if (plan.Operation == SalesOrderQueryOperation.List)
        {
            var sort = BuildSort(plan.SortField, plan.SortDirection);
            if (!sort.Success)
            {
                return SalesOrderODataBuildResult.Fail(sort.Error!);
            }

            orderby = sort.OrderBy;
            sortSummary = sort.Summary;
        }

        var limit = ClampLimit(plan.Limit);
        var select = string.Join(",", SalesOrderFieldCatalog.SelectFields);
        var sb = new StringBuilder();
        sb.Append(SalesOrderFieldCatalog.EntitySetName);
        sb.Append("?$select=");
        sb.Append(select);

        if (filterClauses.Count > 0)
        {
            sb.Append("&$filter=");
            sb.Append(string.Join(" and ", filterClauses));
        }

        if (plan.Operation == SalesOrderQueryOperation.Count)
        {
            sb.Append("&$count=true&$top=1");
        }
        else
        {
            if (!string.IsNullOrWhiteSpace(orderby))
            {
                sb.Append("&$orderby=");
                sb.Append(orderby);
            }

            sb.Append("&$top=");
            sb.Append(limit.ToString(CultureInfo.InvariantCulture));
        }

        return SalesOrderODataBuildResult.Ok(
            sb.ToString(),
            plan.Operation,
            summaries,
            sortSummary,
            limit);
    }

    private static int ClampLimit(int limit)
    {
        if (limit <= 0)
        {
            return SalesOrderQueryLimits.DefaultLimit;
        }

        return Math.Clamp(limit, SalesOrderQueryLimits.MinLimit, SalesOrderQueryLimits.MaxLimit);
    }

    private FilterBuild BuildRelativeDate(SalesOrderRelativeDateFilter relative)
    {
        if (!SalesOrderFieldCatalog.TryResolveField(relative.Field, out var field) ||
            field.FieldType != SalesOrderFieldType.DateTime)
        {
            return FilterBuild.Fail(
                $"Relative date field '{relative.Field}' is not an approved date field (createdon/modifiedon).");
        }

        var (start, end, description) = RelativeDateResolver.Resolve(relative.Token, _utcNow(), _businessTz);
        var startLit = FormatDateLiteral(start);
        var endLit = FormatDateLiteral(end);
        var clauses = new[]
        {
            $"{field.LogicalName} ge {startLit}",
            $"{field.LogicalName} lt {endLit}"
        };

        return FilterBuild.OkClauses(
            clauses,
            $"{field.LogicalName} in {description} [{start:u} .. {end:u})");
    }

    private static FilterBuild BuildFilter(SalesOrderQueryFilter filter)
    {
        if (filter is null || string.IsNullOrWhiteSpace(filter.Field))
        {
            return FilterBuild.Fail("Each filter requires a field name.");
        }

        if (string.IsNullOrWhiteSpace(filter.Value))
        {
            return FilterBuild.Fail($"Filter on '{filter.Field}' requires a value.");
        }

        if (!SalesOrderFieldCatalog.TryResolveField(filter.Field, out var field))
        {
            return FilterBuild.Fail($"Field '{filter.Field}' is not allowlisted for salesorder queries.");
        }

        if (!field.Filterable || !SalesOrderFieldCatalog.IsOperatorAllowed(field, filter.Operator))
        {
            return FilterBuild.Fail(
                $"Operator '{filter.Operator}' is not allowed for field '{field.LogicalName}'.");
        }

        if (filter.Operator == SalesOrderFilterOperator.Contains && !field.Searchable)
        {
            return FilterBuild.Fail($"contains is only allowed on searchable text fields, not '{field.LogicalName}'.");
        }

        return field.FieldType switch
        {
            SalesOrderFieldType.String => BuildStringFilter(field, filter),
            SalesOrderFieldType.Money => BuildMoneyFilter(field, filter),
            SalesOrderFieldType.DateTime => BuildAbsoluteDateFilter(field, filter),
            SalesOrderFieldType.Status => BuildOptionFilter(field, filter, isStatus: true),
            SalesOrderFieldType.State => BuildOptionFilter(field, filter, isStatus: false),
            SalesOrderFieldType.Guid or SalesOrderFieldType.LookupGuid => BuildGuidFilter(field, filter),
            _ => FilterBuild.Fail($"Unsupported field type for '{field.LogicalName}'.")
        };
    }

    private static FilterBuild BuildStringFilter(SalesOrderFieldDefinition field, SalesOrderQueryFilter filter)
    {
        var value = filter.Value.Trim();
        if (value.Length > 200)
        {
            return FilterBuild.Fail("Text filter values cannot exceed 200 characters.");
        }

        if (ContainsInjectionAttempt(value))
        {
            return FilterBuild.Fail("Text filter value contains disallowed content.");
        }

        var escaped = EscapeODataString(value);
        if (filter.Operator == SalesOrderFilterOperator.Contains)
        {
            return FilterBuild.Ok(
                $"contains({field.LogicalName},'{escaped}')",
                $"{field.LogicalName} contains '{value}'");
        }

        var op = OperatorToken(filter.Operator);
        return FilterBuild.Ok(
            $"{field.LogicalName} {op} '{escaped}'",
            $"{field.LogicalName} {op} '{value}'");
    }

    private static FilterBuild BuildMoneyFilter(SalesOrderFieldDefinition field, SalesOrderQueryFilter filter)
    {
        if (!decimal.TryParse(filter.Value.Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out var amount))
        {
            return FilterBuild.Fail($"Value for '{field.LogicalName}' must be a number.");
        }

        var lit = amount.ToString(CultureInfo.InvariantCulture);
        var op = OperatorToken(filter.Operator);
        return FilterBuild.Ok($"{field.LogicalName} {op} {lit}", $"{field.LogicalName} {op} {lit}");
    }

    private static FilterBuild BuildAbsoluteDateFilter(SalesOrderFieldDefinition field, SalesOrderQueryFilter filter)
    {
        if (!DateTimeOffset.TryParse(
                filter.Value.Trim(),
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var dto))
        {
            return FilterBuild.Fail(
                $"Value for '{field.LogicalName}' must be an ISO date/time or use relativeDate tokens.");
        }

        var lit = FormatDateLiteral(dto);
        var op = OperatorToken(filter.Operator);
        return FilterBuild.Ok($"{field.LogicalName} {op} {lit}", $"{field.LogicalName} {op} {lit}");
    }

    private static FilterBuild BuildOptionFilter(
        SalesOrderFieldDefinition field,
        SalesOrderQueryFilter filter,
        bool isStatus)
    {
        var resolved = isStatus
            ? SalesOrderFieldCatalog.TryResolveStatusCode(filter.Value, out var code)
            : SalesOrderFieldCatalog.TryResolveStateCode(filter.Value, out code);

        if (!resolved)
        {
            return FilterBuild.Fail(
                $"Unknown {(isStatus ? "statuscode" : "statecode")} value '{filter.Value}'. Use a verified label or numeric code.");
        }

        var label = isStatus
            ? SalesOrderFieldCatalog.GetStatusLabel(code)
            : SalesOrderFieldCatalog.GetStateLabel(code);
        var op = OperatorToken(filter.Operator);
        return FilterBuild.Ok(
            $"{field.LogicalName} {op} {code.ToString(CultureInfo.InvariantCulture)}",
            $"{field.LogicalName} {op} {code} ({label})");
    }

    private static FilterBuild BuildGuidFilter(SalesOrderFieldDefinition field, SalesOrderQueryFilter filter)
    {
        if (!Guid.TryParse(filter.Value.Trim(), out var id) || id == Guid.Empty)
        {
            return FilterBuild.Fail(
                $"Lookup/GUID filter on '{field.LogicalName}' requires a non-empty GUID. Customer name search is not supported.");
        }

        var op = OperatorToken(filter.Operator);
        var lit = id.ToString("D");
        return FilterBuild.Ok($"{field.LogicalName} {op} {lit}", $"{field.LogicalName} {op} {lit}");
    }

    private static SortBuild BuildSort(string? sortField, SalesOrderSortDirection direction)
    {
        if (string.IsNullOrWhiteSpace(sortField))
        {
            // Deterministic default for list queries.
            return SortBuild.Ok("createdon desc", "createdon desc (default)");
        }

        if (!SalesOrderFieldCatalog.TryResolveField(sortField, out var field) || !field.Sortable)
        {
            return SortBuild.Fail(
                $"Sort field '{sortField}' is not approved. Allowed: {string.Join(", ", SalesOrderFieldCatalog.SortableFields)}.");
        }

        var dir = direction == SalesOrderSortDirection.Asc ? "asc" : "desc";
        return SortBuild.Ok($"{field.LogicalName} {dir}", $"{field.LogicalName} {dir}");
    }

    private static string OperatorToken(SalesOrderFilterOperator op) => op switch
    {
        SalesOrderFilterOperator.Eq => "eq",
        SalesOrderFilterOperator.Ne => "ne",
        SalesOrderFilterOperator.Gt => "gt",
        SalesOrderFilterOperator.Ge => "ge",
        SalesOrderFilterOperator.Lt => "lt",
        SalesOrderFilterOperator.Le => "le",
        _ => throw new InvalidOperationException($"Operator {op} cannot be used as a binary OData operator.")
    };

    private static string EscapeODataString(string value) =>
        value.Replace("'", "''", StringComparison.Ordinal);

    private static string FormatDateLiteral(DateTimeOffset value) =>
        value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    private static bool ContainsInjectionAttempt(string value)
    {
        ReadOnlySpan<string> blocked =
        [
            "$filter",
            "$select",
            "$orderby",
            "$expand",
            " fetch",
            "<fetch",
            "fetchxml",
            ";",
            "--",
            "*/",
            "/*"
        ];

        foreach (var token in blocked)
        {
            if (value.Contains(token, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private sealed class FilterBuild
    {
        public bool Success { get; init; }
        public string? Error { get; init; }
        public string? Clause { get; init; }
        public IReadOnlyList<string>? Clauses { get; init; }
        public string? Summary { get; init; }

        public static FilterBuild Ok(string clause, string summary) =>
            new() { Success = true, Clause = clause, Summary = summary };

        public static FilterBuild OkClauses(IReadOnlyList<string> clauses, string summary) =>
            new() { Success = true, Clauses = clauses, Summary = summary };

        public static FilterBuild Fail(string error) =>
            new() { Success = false, Error = error };
    }

    private sealed class SortBuild
    {
        public bool Success { get; init; }
        public string? Error { get; init; }
        public string? OrderBy { get; init; }
        public string? Summary { get; init; }

        public static SortBuild Ok(string orderBy, string summary) =>
            new() { Success = true, OrderBy = orderBy, Summary = summary };

        public static SortBuild Fail(string error) =>
            new() { Success = false, Error = error };
    }
}

public sealed class SalesOrderODataBuildResult
{
    public bool Success { get; init; }
    public string? Error { get; init; }
    public string? RelativeRequestUri { get; init; }
    public SalesOrderQueryOperation Operation { get; init; }
    public IReadOnlyList<string> AppliedFilterSummary { get; init; } = Array.Empty<string>();
    public string? SortSummary { get; init; }
    public int Limit { get; init; }

    public static SalesOrderODataBuildResult Ok(
        string uri,
        SalesOrderQueryOperation operation,
        IReadOnlyList<string> summaries,
        string? sortSummary,
        int limit) =>
        new()
        {
            Success = true,
            RelativeRequestUri = uri,
            Operation = operation,
            AppliedFilterSummary = summaries,
            SortSummary = sortSummary,
            Limit = limit
        };

    public static SalesOrderODataBuildResult Fail(string error) =>
        new() { Success = false, Error = error };
}
