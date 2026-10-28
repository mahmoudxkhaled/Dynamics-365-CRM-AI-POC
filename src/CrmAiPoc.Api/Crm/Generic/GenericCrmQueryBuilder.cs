using System.Globalization;
using System.Text;
using CrmAiPoc.Api.Configuration;
using CrmAiPoc.Api.Crm.Metadata;
using CrmAiPoc.Api.Crm.Query;
using Microsoft.Extensions.Options;

namespace CrmAiPoc.Api.Crm.Generic;

public sealed class GenericCrmQueryBuilder : IGenericCrmQueryBuilder
{
    private readonly TimeZoneInfo _businessTz;
    private readonly Func<DateTimeOffset> _utcNow;

    public GenericCrmQueryBuilder(IOptions<CrmOptions>? crmOptions = null, Func<DateTimeOffset>? utcNow = null)
    {
        _businessTz = BusinessTimeZone.Resolve(crmOptions?.Value.BusinessTimeZoneId);
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    }

    public GenericCrmODataBuildResult Build(CrmQueryPlan plan, CrmEntityMetadata entity, CrmEntityMetadata? relatedFrom = null)
    {
        if (plan is null || entity is null)
        {
            return GenericCrmODataBuildResult.Fail("Query plan and entity metadata are required.");
        }

        if (!entity.IsApprovedForAi)
        {
            return GenericCrmODataBuildResult.Fail($"Entity '{entity.LogicalName}' is not approved for AI queries.");
        }

        if (plan.Filters.Count > CrmQueryLimits.MaxFilters)
        {
            return GenericCrmODataBuildResult.Fail($"A maximum of {CrmQueryLimits.MaxFilters} filters is allowed.");
        }

        if (plan.Sort.Count > CrmQueryLimits.MaxSortFields)
        {
            return GenericCrmODataBuildResult.Fail($"A maximum of {CrmQueryLimits.MaxSortFields} sort fields is allowed.");
        }

        var filterClauses = new List<string>();
        var summaries = new List<string>();

        if (!string.IsNullOrWhiteSpace(plan.RelatedFromEntity) || !string.IsNullOrWhiteSpace(plan.RelatedFromId))
        {
            if (string.IsNullOrWhiteSpace(plan.RelatedFromEntity) || string.IsNullOrWhiteSpace(plan.RelatedFromId))
            {
                return GenericCrmODataBuildResult.Fail("relatedFromEntity and relatedFromId must be provided together.");
            }

            var hop = CrmRelationshipResolver.ResolveOneHop(entity, plan.RelatedFromEntity, plan.RelatedFromId, relatedFrom);
            if (!hop.Success)
            {
                return GenericCrmODataBuildResult.Fail(hop.Error!);
            }

            filterClauses.Add($"{hop.FilterField} eq {hop.FilterValue}");
            summaries.Add(hop.Summary!);
        }

        if (plan.RelativeDate is not null)
        {
            var relative = BuildRelativeDate(entity, plan.RelativeDate);
            if (!relative.Success)
            {
                return GenericCrmODataBuildResult.Fail(relative.Error!);
            }

            filterClauses.AddRange(relative.Clauses!);
            summaries.Add(relative.Summary!);
        }

        foreach (var filter in plan.Filters)
        {
            var built = BuildFilter(entity, filter);
            if (!built.Success)
            {
                return GenericCrmODataBuildResult.Fail(built.Error!);
            }

            filterClauses.Add(built.Clause!);
            summaries.Add(built.Summary!);
        }

        var select = ResolveSelect(entity, plan.Select);
        string? orderby = null;
        string? sortSummary = null;
        if (plan.Operation == CrmQueryOperation.List)
        {
            var sort = BuildSort(entity, plan.Sort);
            if (!sort.Success)
            {
                return GenericCrmODataBuildResult.Fail(sort.Error!);
            }

            orderby = sort.OrderBy;
            sortSummary = sort.Summary;
        }

        var limit = ClampLimit(plan.Limit);
        var sb = new StringBuilder();
        sb.Append(entity.EntitySetName);
        sb.Append("?$select=");
        sb.Append(string.Join(",", select));

        if (filterClauses.Count > 0)
        {
            sb.Append("&$filter=");
            sb.Append(string.Join(" and ", filterClauses));
        }

        if (plan.Operation == CrmQueryOperation.Count)
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

        return GenericCrmODataBuildResult.Ok(sb.ToString(), plan.Operation, summaries, sortSummary, limit, select);
    }

    private static int ClampLimit(int limit) =>
        limit <= 0 ? CrmQueryLimits.DefaultLimit : Math.Clamp(limit, CrmQueryLimits.MinLimit, CrmQueryLimits.MaxLimit);

    private static readonly string[] PreferredSelectFields =
    [
        "createdon", "modifiedon", "statuscode", "statecode",
        "totalamount", "ordernumber", "accountnumber",
        "emailaddress1", "telephone1", "firstname", "lastname", "jobtitle",
        "address1_city", "address1_country", "fullname", "name", "subject",
        "customerid", "te_customeraccountid", "parentcustomerid"
    ];

    private static IReadOnlyList<string> ResolveSelect(CrmEntityMetadata entity, IReadOnlyList<string> requested)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        TryAddSelect(set, entity, entity.PrimaryIdAttribute);
        TryAddSelect(set, entity, entity.PrimaryNameAttribute);

        if (requested.Count > 0)
        {
            foreach (var name in requested)
            {
                if (set.Count >= CrmQueryLimits.MaxSelectFields)
                {
                    break;
                }

                TryAddSelect(set, entity, name);
            }

            return set.ToArray();
        }

        // Preferred safe columns first — never dump arbitrary first-N from full EntityDefinitions.
        foreach (var preferred in PreferredSelectFields)
        {
            if (set.Count >= CrmQueryLimits.MaxSelectFields)
            {
                break;
            }

            TryAddSelect(set, entity, preferred, requireSafeDefault: true);
        }

        // Only top up when preferred matched very few columns (e.g. stub metadata).
        if (set.Count < 6)
        {
            foreach (var attr in entity.Attributes
                         .Where(a => IsSafeDefaultSelectField(a, entity))
                         .OrderBy(a => a.LogicalName, StringComparer.OrdinalIgnoreCase))
            {
                if (set.Count >= CrmQueryLimits.MaxSelectFields)
                {
                    break;
                }

                set.Add(attr.ODataPropertyName);
            }
        }

        return set.ToArray();
    }

    private static void TryAddSelect(
        HashSet<string> set,
        CrmEntityMetadata entity,
        string? logicalOrOData,
        bool requireSafeDefault = false)
    {
        if (string.IsNullOrWhiteSpace(logicalOrOData))
        {
            return;
        }

        if (!entity.TryGetAttribute(logicalOrOData, out var attr) || !attr.IsValidForRead)
        {
            // Primary id/name may not yet be in Attributes for stubs — still allow raw logical names.
            if (!requireSafeDefault &&
                (logicalOrOData.Equals(entity.PrimaryIdAttribute, StringComparison.OrdinalIgnoreCase) ||
                 logicalOrOData.Equals(entity.PrimaryNameAttribute, StringComparison.OrdinalIgnoreCase)))
            {
                set.Add(logicalOrOData);
            }

            return;
        }

        if (requireSafeDefault && !IsSafeDefaultSelectField(attr, entity))
        {
            return;
        }

        set.Add(attr.ODataPropertyName);
    }

    private static bool IsSafeDefaultSelectField(CrmAttributeMetadata attr, CrmEntityMetadata entity)
    {
        if (!attr.IsValidForRead)
        {
            return false;
        }

        if (attr.Kind is CrmAttributeKind.Memo or CrmAttributeKind.Unknown)
        {
            return false;
        }

        var n = attr.LogicalName;
        if (n.StartsWith("entityimage", StringComparison.OrdinalIgnoreCase) ||
            n.StartsWith("traversedpath", StringComparison.OrdinalIgnoreCase) ||
            n.Contains("yominame", StringComparison.OrdinalIgnoreCase) ||
            n.EndsWith("idname", StringComparison.OrdinalIgnoreCase) ||
            n.EndsWith("_base", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // Allow primary name fields; skip other *name formatted/virtual strings.
        if (n.EndsWith("name", StringComparison.OrdinalIgnoreCase) &&
            !n.Equals(entity.PrimaryNameAttribute, StringComparison.OrdinalIgnoreCase) &&
            n is not ("name" or "fullname" or "firstname" or "lastname" or "subject"))
        {
            return false;
        }

        return attr.Kind is CrmAttributeKind.String or CrmAttributeKind.Integer or CrmAttributeKind.Decimal
            or CrmAttributeKind.Money or CrmAttributeKind.Boolean or CrmAttributeKind.DateTime
            or CrmAttributeKind.Picklist or CrmAttributeKind.Status or CrmAttributeKind.State
            or CrmAttributeKind.Uniqueidentifier or CrmAttributeKind.Lookup or CrmAttributeKind.Customer
            or CrmAttributeKind.Owner;
    }

    private FilterBuild BuildRelativeDate(CrmEntityMetadata entity, CrmRelativeDateFilter relative)
    {
        if (!entity.TryGetAttribute(relative.Field, out var field) || field.Kind != CrmAttributeKind.DateTime)
        {
            return FilterBuild.Fail($"Relative date field '{relative.Field}' is not an approved date field on {entity.LogicalName}.");
        }

        var (start, end, description) = RelativeDateResolver.Resolve(relative.Token, _utcNow(), _businessTz);
        return FilterBuild.OkClauses(
            [
                $"{field.ODataPropertyName} ge {FormatDateLiteral(start)}",
                $"{field.ODataPropertyName} lt {FormatDateLiteral(end)}"
            ],
            $"{field.LogicalName} in {description} [{start:u} .. {end:u})");
    }

    private static FilterBuild BuildFilter(CrmEntityMetadata entity, CrmQueryFilter filter)
    {
        if (string.IsNullOrWhiteSpace(filter.Field) || string.IsNullOrWhiteSpace(filter.Value))
        {
            return FilterBuild.Fail("Each filter requires field and value.");
        }

        if (!entity.TryGetAttribute(filter.Field, out var field) || !field.IsFilterable)
        {
            return FilterBuild.Fail($"Field '{filter.Field}' is not allowlisted/filterable on '{entity.LogicalName}'.");
        }

        if (!field.AllowedOperators.Contains(filter.Operator))
        {
            return FilterBuild.Fail($"Operator '{filter.Operator}' is not allowed for '{field.LogicalName}'.");
        }

        if (filter.Operator == CrmFilterOperator.Contains && !field.IsSearchable)
        {
            return FilterBuild.Fail($"contains is only allowed on searchable text fields, not '{field.LogicalName}'.");
        }

        return field.Kind switch
        {
            CrmAttributeKind.String or CrmAttributeKind.Memo => BuildString(field, filter),
            CrmAttributeKind.Money or CrmAttributeKind.Decimal or CrmAttributeKind.Integer => BuildNumber(field, filter),
            CrmAttributeKind.DateTime => BuildDate(field, filter),
            CrmAttributeKind.Boolean => BuildBool(field, filter),
            CrmAttributeKind.Picklist or CrmAttributeKind.Status or CrmAttributeKind.State => BuildOption(field, filter),
            CrmAttributeKind.Lookup or CrmAttributeKind.Customer or CrmAttributeKind.Owner
                or CrmAttributeKind.Guid or CrmAttributeKind.Uniqueidentifier => BuildGuid(field, filter),
            _ => FilterBuild.Fail($"Unsupported field type for '{field.LogicalName}'.")
        };
    }

    private static FilterBuild BuildString(CrmAttributeMetadata field, CrmQueryFilter filter)
    {
        var value = filter.Value.Trim();
        if (value.Length > 200 || ContainsInjectionAttempt(value))
        {
            return FilterBuild.Fail("Text filter value is invalid or disallowed.");
        }

        var escaped = EscapeODataString(value);
        if (filter.Operator == CrmFilterOperator.Contains)
        {
            return FilterBuild.Ok($"contains({field.ODataPropertyName},'{escaped}')", $"{field.LogicalName} contains '{value}'");
        }

        var op = Op(filter.Operator);
        return FilterBuild.Ok($"{field.ODataPropertyName} {op} '{escaped}'", $"{field.LogicalName} {op} '{value}'");
    }

    private static FilterBuild BuildNumber(CrmAttributeMetadata field, CrmQueryFilter filter)
    {
        if (!decimal.TryParse(filter.Value.Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out var number))
        {
            return FilterBuild.Fail($"Value for '{field.LogicalName}' must be a number.");
        }

        var lit = field.Kind == CrmAttributeKind.Integer
            ? ((long)number).ToString(CultureInfo.InvariantCulture)
            : number.ToString(CultureInfo.InvariantCulture);
        var op = Op(filter.Operator);
        return FilterBuild.Ok($"{field.ODataPropertyName} {op} {lit}", $"{field.LogicalName} {op} {lit}");
    }

    private static FilterBuild BuildDate(CrmAttributeMetadata field, CrmQueryFilter filter)
    {
        if (!DateTimeOffset.TryParse(filter.Value.Trim(), CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dto))
        {
            return FilterBuild.Fail($"Value for '{field.LogicalName}' must be an ISO date/time or use relativeDate.");
        }

        var op = Op(filter.Operator);
        var lit = FormatDateLiteral(dto);
        return FilterBuild.Ok($"{field.ODataPropertyName} {op} {lit}", $"{field.LogicalName} {op} {lit}");
    }

    private static FilterBuild BuildBool(CrmAttributeMetadata field, CrmQueryFilter filter)
    {
        if (!bool.TryParse(filter.Value.Trim(), out var b))
        {
            return FilterBuild.Fail($"Value for '{field.LogicalName}' must be true or false.");
        }

        var op = Op(filter.Operator);
        var lit = b ? "true" : "false";
        return FilterBuild.Ok($"{field.ODataPropertyName} {op} {lit}", $"{field.LogicalName} {op} {lit}");
    }

    private static FilterBuild BuildOption(CrmAttributeMetadata field, CrmQueryFilter filter)
    {
        int code;
        if (int.TryParse(filter.Value.Trim(), out code) && field.OptionSetByValue.ContainsKey(code))
        {
            // ok
        }
        else if (!field.OptionSetByLabel.TryGetValue(filter.Value.Trim(), out code))
        {
            return FilterBuild.Fail($"Unknown option value '{filter.Value}' for '{field.LogicalName}'.");
        }

        var label = field.OptionSetByValue.TryGetValue(code, out var l) ? l : code.ToString(CultureInfo.InvariantCulture);
        var op = Op(filter.Operator);
        return FilterBuild.Ok(
            $"{field.ODataPropertyName} {op} {code.ToString(CultureInfo.InvariantCulture)}",
            $"{field.LogicalName} {op} {code} ({label})");
    }

    private static FilterBuild BuildGuid(CrmAttributeMetadata field, CrmQueryFilter filter)
    {
        if (!Guid.TryParse(filter.Value.Trim(), out var id) || id == Guid.Empty)
        {
            return FilterBuild.Fail($"Lookup/GUID filter on '{field.LogicalName}' requires a non-empty GUID.");
        }

        var op = Op(filter.Operator);
        return FilterBuild.Ok($"{field.ODataPropertyName} {op} {id:D}", $"{field.LogicalName} {op} {id:D}");
    }

    private static SortBuild BuildSort(CrmEntityMetadata entity, IReadOnlyList<CrmQuerySort> sorts)
    {
        if (sorts.Count == 0)
        {
            if (entity.TryGetAttribute("createdon", out var created) && created.IsSortable)
            {
                return SortBuild.Ok($"{created.ODataPropertyName} desc", "createdon desc (default)");
            }

            if (entity.TryGetAttribute(entity.PrimaryNameAttribute, out var name) && name.IsSortable)
            {
                return SortBuild.Ok($"{name.ODataPropertyName} asc", $"{name.LogicalName} asc (default)");
            }

            return SortBuild.Ok(null, null);
        }

        var parts = new List<string>();
        var summaries = new List<string>();
        foreach (var sort in sorts)
        {
            if (!entity.TryGetAttribute(sort.Field, out var field) || !field.IsSortable)
            {
                return SortBuild.Fail($"Sort field '{sort.Field}' is not approved/sortable on '{entity.LogicalName}'.");
            }

            var dir = sort.Direction == CrmSortDirection.Asc ? "asc" : "desc";
            parts.Add($"{field.ODataPropertyName} {dir}");
            summaries.Add($"{field.LogicalName} {dir}");
        }

        return SortBuild.Ok(string.Join(",", parts), string.Join(", ", summaries));
    }

    private static string Op(CrmFilterOperator op) => op switch
    {
        CrmFilterOperator.Eq => "eq",
        CrmFilterOperator.Ne => "ne",
        CrmFilterOperator.Gt => "gt",
        CrmFilterOperator.Ge => "ge",
        CrmFilterOperator.Lt => "lt",
        CrmFilterOperator.Le => "le",
        _ => throw new InvalidOperationException($"Operator {op} is not a binary OData operator.")
    };

    private static string EscapeODataString(string value) =>
        value.Replace("'", "''", StringComparison.Ordinal);

    private static string FormatDateLiteral(DateTimeOffset value) =>
        value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    private static bool ContainsInjectionAttempt(string value)
    {
        ReadOnlySpan<string> blocked =
        [
            "$filter", "$select", "$orderby", "$expand", " fetch", "<fetch", "fetchxml", ";", "--", "*/", "/*"
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

        public static SortBuild Ok(string? orderBy, string? summary) =>
            new() { Success = true, OrderBy = orderBy, Summary = summary };

        public static SortBuild Fail(string error) =>
            new() { Success = false, Error = error };
    }
}
