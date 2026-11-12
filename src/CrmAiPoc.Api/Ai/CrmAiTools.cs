using System.ComponentModel;
using System.Text.Json;
using CrmAiPoc.Api.Crm.FetchXml;

namespace CrmAiPoc.Api.Ai;

/// <summary>
/// Approved read-only CRM tools exposed to the AI model via Microsoft.Extensions.AI.
/// Production chat exposes <see cref="query_crm"/>, <see cref="discover_crm_schema"/>,
/// <see cref="clarify_crm"/>, and <see cref="unsupported_crm_request"/>.
/// ChatService routes FunctionCallContent manually and never returns CRM rows to the model.
/// </summary>
public sealed class CrmAiTools
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly ILogger<CrmAiTools> _logger;

    public CrmAiTools(ILogger<CrmAiTools> logger)
    {
        _logger = logger;
    }

    [Description(
        "CRM read tool. Generate complete Dynamics 365 On-Premises v9.1 FetchXML for the user's request. " +
        "Use immediately for simple single-entity queries with standard fields you can name confidently. " +
        "For multi-entity joins, custom fields, or unverified relationships, call discover_crm_schema first and use its candidates. " +
        "Pass fetchXml (required), resultMode=complete|partial, and limitation when partial. " +
        "Use complete when FetchXML can answer; partial when only supporting data can be retrieved " +
        "(limitation describes only the missing capability — never claim data was already retrieved). " +
        "Never invent logical names or relationship SchemaNames. Never emit SQL/OData/C#. " +
        "If no useful authorized query can be produced, call unsupported_crm_request. " +
        "If business intent is genuinely ambiguous, call clarify_crm.")]
    public Task<string> query_crm(
        [Description("Complete Dynamics FetchXML document starting with <fetch>...</fetch>.")]
        string fetchXml,
        [Description("complete when the FetchXML answers the request; partial when only supporting data can be retrieved.")]
        string resultMode = "complete",
        [Description(
            "Required when resultMode=partial. Describe only the missing analysis/capability. " +
            "Do not claim CRM execution already happened.")]
        string? limitation = null,
        CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        // ChatService routes FunctionCallContent manually; this body is schema/discovery only.
        _logger.LogInformation(
            "Selected AI function: query_crm | resultMode={ResultMode} fetchLength={FetchLength}",
            resultMode,
            fetchXml?.Length ?? 0);

        var parsed = FetchXmlArgumentParser.ParseFromToolArguments(new Dictionary<string, object?>
        {
            ["fetchXml"] = fetchXml,
            ["resultMode"] = resultMode,
            ["limitation"] = limitation
        });

        if (!parsed.Success)
        {
            return Task.FromResult(JsonSerializer.Serialize(
                new { error = true, message = parsed.Error, code = parsed.ErrorCode },
                JsonOptions));
        }

        return Task.FromResult(JsonSerializer.Serialize(
            new
            {
                function = "query_crm",
                resultMode = parsed.Request!.ResultMode.ToString().ToLowerInvariant(),
                limitation = parsed.Request.Limitation,
                fetchLength = parsed.Request.FetchXml.Length
            },
            JsonOptions));
    }

    [Description(
        "On-demand CRM schema discovery. Call BEFORE query_crm when the request needs verified organization-specific " +
        "attribute logical names, types, or relationship SchemaNames/join fields — especially multi-entity joins, " +
        "custom fields, ambiguous business labels, or unverified date/money/grouping fields. " +
        "Skip for simple single-entity queries that use only standard Dynamics names you already know. " +
        "Pass entities you will query/join, business concepts (not invented schema names), and relationshipPairsJson " +
        "for every join you need in one call. Use returned candidates for FetchXML: schemaName, businessLabel, and " +
        "referencing/referenced entity+attribute map to link-entity from/to (from=linked entity attribute, to=parent). " +
        "When several relationships share the same entity pair, pick by businessLabel matching the user's concept — " +
        "distinct labels are not equivalent. If still ambiguous, call clarify_crm — do not guess. " +
        "For Picklist/State/Status/Boolean candidates, use returned options (value+label) and put the numeric value " +
        "in FetchXML filters — never the display label. Never invent schema identifiers or OptionSet codes. " +
        "At most a few discovery calls per request.")]
    public Task<string> discover_crm_schema(
        [Description(
            "Approved CRM entity logical names to scope the search. Include every entity you plan to query or join " +
            "(e.g. salesorder, account, contact).")]
        string[]? entities = null,
        [Description(
            "Business concepts to map to CRM attributes (e.g. 'order total value', 'customer account name', " +
            "'billing contact full name'). Do not invent logical names here.")]
        string[]? concepts = null,
        [Description(
            "JSON array of entity pairs for relationship discovery for every planned join, e.g. " +
            "[[\"salesorder\",\"account\"],[\"salesorder\",\"contact\"]]. " +
            "Returned candidates include schemaName, businessLabel, and referencing/referenced attributes for from/to.")]
        string? relationshipPairsJson = null,
        CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        _logger.LogInformation(
            "Selected AI function: discover_crm_schema | entities={Entities} concepts={Concepts}",
            entities?.Length ?? 0,
            concepts?.Length ?? 0);

        // ChatService executes discovery against live metadata; this body is schema/discovery only.
        return Task.FromResult(JsonSerializer.Serialize(
            new
            {
                function = "discover_crm_schema",
                entityCount = entities?.Length ?? 0,
                conceptCount = concepts?.Length ?? 0,
                hasRelationshipPairs = !string.IsNullOrWhiteSpace(relationshipPairsJson)
            },
            JsonOptions));
    }

    [Description(
        "Request user clarification when a CRM question is genuinely ambiguous across relationships, " +
        "attributes, or interpretations. Do not use for engine capability gaps — use unsupported_crm_request. " +
        "Provide 2–4 complete standalone suggestion questions with label and question. " +
        "Each suggestion must be directly executable as the next user message with no blanks, placeholders, " +
        "or requests for CRM logical names, SchemaNames, GUIDs, or OptionSet numeric values. " +
        "Ask only business-level choices the user can click; the UI also offers a custom free-text option. " +
        "Do not claim unsupported solely because a suggestion was rejected. " +
        "Use discover_crm_schema for missing technical metadata. Do not call query_crm in the same turn.")]
    public Task<string> clarify_crm(
        [Description("Short user-facing message explaining what needs clarification.")]
        string message,
        [Description(
            "Exactly 2–4 clarification options. Each needs a short label and a complete standalone " +
            "question the user can submit unchanged (no ___, [enter value], or fill-in blanks).")]
        ClarifyCrmSuggestionArg[] suggestions,
        CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
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

    [Description(
        "Report that the user's CRM request is understood but no useful, authorized, safe CRM query " +
        "can reasonably be produced. Do not substitute a different report. Provide a factual English reason " +
        "for backend diagnostics; the application shows a fixed version message to the user.")]
    public Task<string> unsupported_crm_request(
        [Description(
            "Factual English reason the request cannot be fulfilled (logged for diagnostics; not shown verbatim in the UI).")]
        string message,
        CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        _logger.LogInformation("Selected AI function: unsupported_crm_request");
        return Task.FromResult(JsonSerializer.Serialize(
            new
            {
                function = "unsupported_crm_request",
                message
            },
            JsonOptions));
    }
}
