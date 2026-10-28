using System.ComponentModel;
using System.Text.Json;
using CrmAiPoc.Api.Configuration;
using CrmAiPoc.Api.Crm.Generic;
using CrmAiPoc.Api.Crm.Metadata;
using Microsoft.Extensions.Options;

namespace CrmAiPoc.Api.Ai;

/// <summary>
/// Approved read-only CRM tools exposed to the AI model via Microsoft.Extensions.AI.
/// Production chat exposes only <see cref="query_crm"/>; ChatService executes the validated plan locally.
/// </summary>
public sealed class CrmAiTools
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly CrmOptions _crmOptions;
    private readonly IGenericCrmRepository _genericCrm;
    private readonly ICrmMetadataService? _metadata;
    private readonly ILogger<CrmAiTools> _logger;

    /// <summary>Optional conversation session (set by ChatService when needed).</summary>
    public ConversationSession? Session { get; set; }

    public CrmAiTools(
        IGenericCrmRepository genericCrm,
        IOptions<CrmOptions> crmOptions,
        ILogger<CrmAiTools> logger,
        ICrmMetadataService? metadata = null)
    {
        _genericCrm = genericCrm;
        _crmOptions = crmOptions.Value;
        _metadata = metadata;
        _logger = logger;
    }

    [Description("THE only CRM read tool. Query ANY approved entity (account, contact, salesorder, lead, …). Supports list/count/filters/sort/dates/limit and one-hop relatedFromEntity+relatedFromId. Pass structured plan only — never OData/SQL/FetchXML.")]
    public async Task<string> query_crm(
        [Description("Approved entity logical name: account | contact | salesorder | lead (or configured aliases).")] string entity,
        [Description("list or count")] string operation = "list",
        [Description("Optional JSON array of field names to select.")] string? selectJson = null,
        [Description("JSON array of filters: [{field,operator,value}] max 3.")] string? filtersJson = null,
        [Description("Date field for relative range.")] string? relativeDateField = null,
        [Description("last_calendar_month | this_calendar_month | this_week | last_7_days | last_30_days")] string? relativeDateToken = null,
        [Description("JSON array of sorts: [{field,direction}] max 2.")] string? sortJson = null,
        [Description("Legacy single sort field.")] string? sortField = null,
        [Description("asc or desc")] string? sortDirection = "desc",
        [Description("Max rows 1-50 for list.")] int limit = 10,
        [Description("Parent entity for one-hop relationship filter.")] string? relatedFromEntity = null,
        [Description("Parent record GUID for one-hop relationship filter.")] string? relatedFromId = null,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation(
            "Selected AI function: query_crm | entity={Entity}, operation={Operation}, limit={Limit}",
            entity,
            operation,
            limit);

        if (_crmOptions.IsDynamicsProvider && _crmOptions.AllowSendLiveDataToAi)
        {
            return Fail("External transmission of live CRM data is not enabled for this POC.");
        }

        if (_metadata is not null)
        {
            await _metadata.EnsureApprovedEntitiesLoadedAsync(cancellationToken);
        }

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
            relatedFromId);
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
            note = result.Message,
            catalogHint = _metadata?.GetAiCatalogSummary()
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

    private string Fail(string message)
    {
        _logger.LogInformation("Function execution result: failure — {Message}", message);
        return JsonSerializer.Serialize(new { error = true, message }, JsonOptions);
    }
}
