using System.Text.Json;
using System.Text.Json.Serialization;

namespace CrmAiPoc.Api.Ai;

/// <summary>
/// Compact, sanitized validation feedback returned to Groq as a query_crm tool result.
/// Never includes CRM records, credentials, stack traces, or unfiltered metadata catalogs.
/// </summary>
public sealed class QueryPlanValidationFeedback
{
    [JsonPropertyName("success")]
    public bool Success { get; init; }

    [JsonPropertyName("error")]
    public QueryPlanValidationError? Error { get; init; }

    [JsonPropertyName("instruction")]
    public string? Instruction { get; init; }

    public static QueryPlanValidationFeedback Fail(
        string code,
        string message,
        string? field = null,
        string? entity = null) =>
        new()
        {
            Success = false,
            Error = new QueryPlanValidationError
            {
                Code = code,
                Message = SanitizeMessage(message),
                Field = field,
                Entity = entity
            },
            Instruction =
                "Revise the query_crm FetchXML while preserving the user's original business request " +
                "(entity grain, reporting period, metrics, filters, ranking, and calculated metrics). " +
                "Do not substitute a different report. If you cannot safely correct the plan, " +
                "call clarify_crm or unsupported_crm_request."
        };

    public string ToToolResultJson() =>
        JsonSerializer.Serialize(this, JsonOptions);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = null
    };

    private static string SanitizeMessage(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return "The query plan failed validation.";
        }

        var trimmed = message.Trim();
        if (trimmed.Length > 400)
        {
            trimmed = trimmed[..400] + "…";
        }

        // Strip obvious exception/stack markers if a caller accidentally passes them.
        var newline = trimmed.IndexOfAny(['\r', '\n']);
        if (newline >= 0)
        {
            trimmed = trimmed[..newline].Trim();
        }

        return trimmed;
    }
}

public sealed class QueryPlanValidationError
{
    [JsonPropertyName("code")]
    public required string Code { get; init; }

    [JsonPropertyName("message")]
    public required string Message { get; init; }

    [JsonPropertyName("field")]
    public string? Field { get; init; }

    [JsonPropertyName("entity")]
    public string? Entity { get; init; }
}

/// <summary>
/// Classification of a planning validation failure for retry decisions.
/// Prefer typed stages over arbitrary exception-message matching.
/// </summary>
public enum QueryPlanFailureKind
{
    /// <summary>Deterministic planning/schema mistake the LLM may correct.</summary>
    RecoverablePlanning = 0,

    /// <summary>Authorization / approval-policy denial — never retry via AI.</summary>
    NonRetryableAuthorization = 1,

    /// <summary>CRM connectivity/metadata/service issues — never AI-retry.</summary>
    NonRetryableInfrastructure = 2,

    /// <summary>FetchXML/CRM execution failure after validation — never AI-retry.</summary>
    NonRetryableExecution = 3
}

public static class QueryPlanFailureClassifier
{
    public static (QueryPlanFailureKind Kind, string Code, string? Field) Classify(
        QueryPlanValidationStage stage,
        string message,
        string? entity = null,
        string? fieldHint = null)
    {
        _ = entity;
        var msg = message ?? string.Empty;

        switch (stage)
        {
            case QueryPlanValidationStage.Parse:
                if (ContainsAny(msg, "DTD", "unsafe XML", "well-formed", "Namespaced"))
                {
                    return (QueryPlanFailureKind.RecoverablePlanning, "INVALID_XML", fieldHint);
                }

                return (QueryPlanFailureKind.RecoverablePlanning, "MALFORMED_TOOL_ARGUMENTS", fieldHint);

            case QueryPlanValidationStage.EntityApproval:
                return (QueryPlanFailureKind.NonRetryableAuthorization, "ENTITY_NOT_APPROVED", fieldHint);

            case QueryPlanValidationStage.Authorization:
                return (QueryPlanFailureKind.NonRetryableAuthorization, "AUTHORIZATION_DENIED", fieldHint);

            case QueryPlanValidationStage.MetadataLoad:
                return (QueryPlanFailureKind.NonRetryableInfrastructure, "METADATA_UNAVAILABLE", fieldHint);

            case QueryPlanValidationStage.RelationshipResolution:
                if (ContainsAny(msg, "ambiguous", "more than one", "multiple relationships"))
                {
                    return (QueryPlanFailureKind.RecoverablePlanning, "AMBIGUOUS_RELATIONSHIP", fieldHint);
                }

                return (QueryPlanFailureKind.RecoverablePlanning, "INVALID_RELATIONSHIP", fieldHint);

            case QueryPlanValidationStage.FetchXmlBuild:
                if (ContainsAny(msg, "outside the approved CRM allowlist", "Only read operations are permitted"))
                {
                    return (QueryPlanFailureKind.NonRetryableAuthorization, "AUTHORIZATION_DENIED", fieldHint);
                }

                if (ContainsAny(msg, "could not be loaded from Dynamics", "metadata unavailable"))
                {
                    return (QueryPlanFailureKind.NonRetryableInfrastructure, "METADATA_UNAVAILABLE", fieldHint);
                }

                if (ContainsAny(msg, "share the same display name", "AGGREGATE_GROUP_IDENTITY",
                        "primaryIdAttribute", "without '", "Grouping '"))
                {
                    return (QueryPlanFailureKind.RecoverablePlanning, "AGGREGATE_GROUP_IDENTITY",
                        fieldHint ?? ExtractQuotedToken(msg));
                }

                if (ContainsAny(msg, "groupBy field", "dateGrouping requires"))
                {
                    return (QueryPlanFailureKind.RecoverablePlanning, "INVALID_GROUP_BY_FIELD",
                        fieldHint ?? ExtractQuotedToken(msg));
                }

                if (ContainsAny(msg, "Aggregate field", "Aggregate Sum", "Aggregate Avg", "Aggregate Min", "Aggregate Max",
                        "requires a numeric"))
                {
                    return (QueryPlanFailureKind.RecoverablePlanning, "INVALID_AGGREGATE_FIELD",
                        fieldHint ?? ExtractQuotedToken(msg));
                }

                if (ContainsAny(msg, "sort field", "sort alias", "Sort attribute", "Invalid sort", "is not sortable",
                        "<order alias", "must be sorted with"))
                {
                    return (QueryPlanFailureKind.RecoverablePlanning, "INVALID_SORT",
                        fieldHint ?? ExtractQuotedToken(msg));
                }

                if (ContainsAny(msg, "filter field", "Filter field", "operator", "is not filterable", "not supported for"))
                {
                    return (QueryPlanFailureKind.RecoverablePlanning, "INVALID_FILTER",
                        fieldHint ?? ExtractQuotedToken(msg));
                }

                if (ContainsAny(msg, "select field", "Select field", "not approved/readable"))
                {
                    // Same wording is used for unknown vs non-readable — do not disclose which.
                    return (QueryPlanFailureKind.RecoverablePlanning, "INVALID_FIELD",
                        fieldHint ?? ExtractQuotedToken(msg));
                }

                if (ContainsAny(msg, "link", "relationship", "SchemaName", "from=", "to="))
                {
                    return (QueryPlanFailureKind.RecoverablePlanning, "INVALID_RELATIONSHIP", fieldHint);
                }

                if (ContainsAny(msg, "alias"))
                {
                    return (QueryPlanFailureKind.RecoverablePlanning, "INVALID_ALIAS", fieldHint);
                }

                if (ContainsAny(msg, "too large", "exceed", "maximum", "depth"))
                {
                    return (QueryPlanFailureKind.RecoverablePlanning, "QUERY_TOO_LARGE", fieldHint);
                }

                if (ContainsAny(msg, "Unsupported FetchXML", "Unsupported element", "Unsupported attribute", "Paging beyond"))
                {
                    return (QueryPlanFailureKind.RecoverablePlanning, "UNSUPPORTED_FETCHXML", fieldHint);
                }

                return (QueryPlanFailureKind.RecoverablePlanning, "INVALID_QUERY_PLAN", fieldHint);

            default:
                return (QueryPlanFailureKind.RecoverablePlanning, "INVALID_QUERY_PLAN", fieldHint);
        }
    }

    public static bool IsRetryable(QueryPlanFailureKind kind) =>
        kind == QueryPlanFailureKind.RecoverablePlanning;

    private static bool ContainsAny(string message, params string[] fragments) =>
        fragments.Any(f => message.Contains(f, StringComparison.OrdinalIgnoreCase));

    private static string? ExtractQuotedToken(string message)
    {
        var start = message.IndexOf('\'');
        if (start < 0)
        {
            return null;
        }

        var end = message.IndexOf('\'', start + 1);
        if (end <= start + 1)
        {
            return null;
        }

        return message[(start + 1)..end];
    }
}

public enum QueryPlanValidationStage
{
    Parse,
    EntityApproval,
    Authorization,
    MetadataLoad,
    RelationshipResolution,
    FetchXmlBuild
}
