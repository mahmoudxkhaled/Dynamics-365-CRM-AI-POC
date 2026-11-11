using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using CrmAiPoc.Api.Crm.Generic;

namespace CrmAiPoc.Api.Ai;

/// <summary>
/// Approved read-only CRM tools exposed to the AI model via Microsoft.Extensions.AI.
/// Production chat exposes <see cref="query_crm"/> and <see cref="clarify_crm"/>.
/// ChatService routes FunctionCallContent manually and never returns CRM rows to the model.
/// </summary>
public sealed class CrmAiTools
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private static readonly JsonSerializerOptions DateRangeSerializeOptions = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly IGenericCrmRepository _genericCrm;
    private readonly ILogger<CrmAiTools> _logger;

    public CrmAiTools(
        IGenericCrmRepository genericCrm,
        ILogger<CrmAiTools> logger)
    {
        _genericCrm = genericCrm;
        _logger = logger;
    }

    [Description(
        "CRM read tool. Query approved entities from the catalog when the question is clear. " +
        "Supports list/count/aggregate with filters/sort/dates/limit, aggregatesJson+groupByJson, " +
        "relatedFromEntity+relatedFromId GUID filters, and linksJson for related-entity joins " +
        "(max depth 2 / max 3 linked entities). Pass a complete structured plan — never OData/SQL/FetchXML. " +
        "Include every filter, date range, field, sort, and relationship the user requested. " +
        "If the CRM question is genuinely ambiguous, call clarify_crm instead of guessing.")]
    public async Task<string> query_crm(
        [Description("Approved entity logical name from the catalog (or configured alias).")] string entity,
        [Description("list | count | aggregate — choose from the user's requested result shape.")] string operation = "list",
        [Description("Optional JSON array of field names to select (list only).")] string? selectJson = null,
        [Description(
            "JSON array of filters: [{field,operator,value}] max 3. " +
            "DateTime fields accept ISO values with operators eq|ne|gt|ge|lt|le.")] string? filtersJson = null,
        [Description("Approved DateTime field for a structured date range (e.g. createdon, modifiedon).")]
        string? relativeDateField = null,
        [Description(
            "Legacy fixed tokens only: last_calendar_month | this_calendar_month | this_week | " +
            "last_7_days | last_30_days | last_6_calendar_months. Prefer dateRange for arbitrary periods.")]
        string? relativeDateToken = null,
        [Description(
            "Preferred structured date range object used with relativeDateField. " +
            "ALWAYS set kind. Canonical example for full year 2025: " +
            "{\"kind\":\"calendar_year\",\"year\":2025}. " +
            "Kinds: absolute (start,endExclusive ISO), last_n_days|last_n_weeks|last_n_calendar_months|" +
            "last_n_rolling_months|last_n_years (n), this_calendar_month|this_calendar_quarter|" +
            "this_calendar_year (YTD)|this_week, previous_calendar_month|previous_calendar_quarter|" +
            "previous_calendar_year, calendar_year (year), calendar_quarter (year,quarter), " +
            "calendar_month_range (year,monthStart,monthEnd). Half-open [start,endExclusive). " +
            "Backend resolves relative kinds with the business timezone. " +
            "If the user asks for an ambiguous period (e.g. 'six months within 2025' without which months), ask clarification instead of guessing.")]
        QueryCrmDateRangeArg? dateRange = null,
        [Description(
            "Legacy string form of dateRange (JSON object text). Prefer the typed dateRange argument. " +
            "Same shape: {\"kind\":\"calendar_year\",\"year\":2025}.")]
        string? dateRangeJson = null,
        [Description("JSON array of sorts: [{field,direction}] max 2.")] string? sortJson = null,
        [Description("Sort field logical name from approved metadata.")] string? sortField = null,
        [Description("asc or desc.")] string? sortDirection = "desc",
        [Description(
            "Optional max rows for list (1-50). Omit or null to use the backend default. " +
            "Not required for count/aggregate.")]
        int? limit = null,
        [Description("Parent entity for one-hop relationship filter.")] string? relatedFromEntity = null,
        [Description("Parent record GUID for one-hop relationship filter.")] string? relatedFromId = null,
        [Description("JSON array of aggregates for reports: [{function:count|sum|avg|min|max, field, alias}] max 3. Required when operation=aggregate.")] string? aggregatesJson = null,
        [Description("JSON array of groupBy for reports: [{field, alias, dateGrouping?:year|month|day|week|quarter}] max 3. For monthly series across a year, include dateGrouping month (backend adds year context when needed).")] string? groupByJson = null,
        [Description(
            "JSON array of related-entity links: " +
            "[{entity, purpose?:businessLabel, relationship?:exactCatalogSchemaName, joinType?:outer|inner, alias?, select?:[fields], filters?:[{field,operator,value}], links?:[nested]}]. " +
            "Prefer purpose from catalog rels[].lbl (e.g. Customer Account). " +
            "When more than one approved relationship exists to the related entity, you MUST set relationship to the exact catalog SchemaName (or a purpose that uniquely identifies one). " +
            "If still ambiguous, call clarify_crm instead of guessing. " +
            "Never invent SchemaNames. Nested links max depth 2.")]
        string? linksJson = null,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation(
            "Selected AI function: query_crm | entity={Entity}, operation={Operation}, limit={Limit}",
            entity,
            operation,
            limit);

        var resolvedDateRangeJson = ResolveDateRangeJson(dateRange, dateRangeJson);

        var parsed = CrmQueryPlanParser.Parse(
            entity,
            operation,
            selectJson,
            filtersJson,
            relativeDateField,
            relativeDateToken,
            sortJson,
            sortField,
            sortDirection,
            limit,
            relatedFromEntity,
            relatedFromId,
            aggregatesJson,
            groupByJson,
            linksJson,
            dateRangeJson: resolvedDateRangeJson);
        if (!parsed.Success || parsed.Plan is null)
        {
            return Fail(parsed.Error ?? "Invalid query_crm arguments.");
        }

        var result = await _genericCrm.QueryAsync(parsed.Plan, cancellationToken);
        if (!string.IsNullOrWhiteSpace(result.Message) &&
            string.IsNullOrWhiteSpace(result.RelativeRequestUri) &&
            result.Items.Count == 0 &&
            result.Count is null)
        {
            return Fail(result.Message);
        }

        var payload = new
        {
            function = "query_crm",
            provider = result.ProviderName,
            entity = result.Entity,
            operation = result.Operation.ToString().ToLowerInvariant(),
            appliedFilters = result.AppliedFilterSummary,
            sort = result.SortSummary,
            limit = result.Limit,
            count = result.Count,
            countMayBeCapped = result.CountMayBeCapped,
            totalReturned = result.Items.Count,
            records = result.Items,
            note = result.Message
        };

        var json = JsonSerializer.Serialize(payload, JsonOptions);
        _logger.LogInformation(
            "Function execution result: query_crm entity={Entity} operation={Operation} rows={Rows} count={Count}",
            result.Entity,
            result.Operation,
            result.Items.Count,
            result.Count);
        return json;
    }

    [Description(
        "Request user clarification when a CRM question is genuinely ambiguous across catalog " +
        "relationships, attributes, or interpretations. Do not use for clear latest/newest/most-recent " +
        "list requests or other ordinary conventions. Provide 2–4 complete standalone suggestion questions. " +
        "entityLogicalName, attributeLogicalNames, and relationshipSchemaNames must come from the catalog. " +
        "Never invent SchemaNames. Do not call query_crm in the same turn.")]
    public Task<string> clarify_crm(
        [Description("Short user-facing message explaining what needs clarification.")]
        string message,
        [Description(
            "Exactly 2–4 clarification options. Each question must be a complete standalone English " +
            "question the user can submit unchanged.")]
        ClarifyCrmSuggestionArg[] suggestions,
        CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        // ChatService routes FunctionCallContent manually; this body is schema/discovery only.
        _logger.LogInformation(
            "Selected AI function: clarify_crm | suggestionCount={SuggestionCount}",
            suggestions?.Length ?? 0);
        return Task.FromResult(JsonSerializer.Serialize(
            new
            {
                function = "clarify_crm",
                message,
                suggestionCount = suggestions?.Length ?? 0
            },
            JsonOptions));
    }

    /// <summary>
    /// Prefer typed <paramref name="dateRange"/>; fall back to legacy JSON string.
    /// </summary>
    internal static string? ResolveDateRangeJson(QueryCrmDateRangeArg? dateRange, string? dateRangeJson)
    {
        if (dateRange is not null &&
            (dateRange.Kind is not null ||
             dateRange.N is not null ||
             dateRange.Year is not null ||
             dateRange.Quarter is not null ||
             dateRange.MonthStart is not null ||
             dateRange.MonthEnd is not null ||
             dateRange.Start is not null ||
             dateRange.EndExclusive is not null))
        {
            return JsonSerializer.Serialize(dateRange, DateRangeSerializeOptions);
        }

        return string.IsNullOrWhiteSpace(dateRangeJson) ? null : dateRangeJson;
    }

    private static string Fail(string message) =>
        JsonSerializer.Serialize(new { error = true, message }, JsonOptions);
}
