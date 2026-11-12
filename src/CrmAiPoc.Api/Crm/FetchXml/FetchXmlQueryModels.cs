using CrmAiPoc.Api.Crm.Generic;
using CrmAiPoc.Api.Crm.Metadata;

namespace CrmAiPoc.Api.Crm.FetchXml;

public enum FetchXmlResultMode
{
    Complete = 0,
    Partial = 1
}

public sealed class FetchXmlToolRequest
{
    public required string FetchXml { get; init; }
    public FetchXmlResultMode ResultMode { get; init; } = FetchXmlResultMode.Complete;
    public string? Limitation { get; init; }
}

public sealed class FetchXmlToolParseResult
{
    public bool Success { get; init; }
    public FetchXmlToolRequest? Request { get; init; }
    public string? Error { get; init; }
    public string? ErrorCode { get; init; }

    public static FetchXmlToolParseResult Ok(FetchXmlToolRequest request) =>
        new() { Success = true, Request = request };

    public static FetchXmlToolParseResult Fail(string code, string error) =>
        new() { Success = false, ErrorCode = code, Error = error };
}

/// <summary>
/// Validated, normalized FetchXML ready for Dynamics Web API execution.
/// </summary>
public sealed class ValidatedFetchXmlQuery
{
    public required string FetchXml { get; init; }
    public required string EntityLogicalName { get; init; }
    public required string EntitySetName { get; init; }
    public required CrmEntityMetadata Entity { get; init; }
    public required string RelativeRequestUri { get; init; }
    public bool IsAggregate { get; init; }
    public bool IsCountOnly { get; init; }
    public int? Top { get; init; }
    public IReadOnlyList<CrmTableColumn> Columns { get; init; } = Array.Empty<CrmTableColumn>();
    public IReadOnlyList<string> ProjectedAliases { get; init; } = Array.Empty<string>();
    public FetchXmlResultMode ResultMode { get; init; }
    public string? Limitation { get; init; }
}

public sealed class FetchXmlValidationOutcome
{
    public bool Success { get; init; }
    public ValidatedFetchXmlQuery? Query { get; init; }
    public string? ErrorMessage { get; init; }
    public string? ErrorCode { get; init; }
    public string Stage { get; init; } = "FetchXmlBuild";
    public string? Field { get; init; }
    public string? Entity { get; init; }

    public static FetchXmlValidationOutcome Ok(ValidatedFetchXmlQuery query) =>
        new() { Success = true, Query = query };

    public static FetchXmlValidationOutcome Fail(
        string stage,
        string code,
        string message,
        string? field = null,
        string? entity = null) =>
        new()
        {
            Success = false,
            Stage = stage,
            ErrorCode = code,
            ErrorMessage = message,
            Field = field,
            Entity = entity
        };
}

public static class FetchXmlSafetyLimits
{
    public const int MaxXmlCharacters = 32_000;
    public const int MaxTop = CrmQueryLimits.MaxLimit;
    public const int DefaultTop = CrmQueryLimits.DefaultLimit;
    public const int MaxAggregatePageSize = CrmQueryLimits.MaxAggregateResultRows;
    public const int MaxLinkDepth = CrmQueryLimits.MaxLinkDepth;
    public const int MaxLinkedEntities = CrmQueryLimits.MaxLinkedEntities;
    public const int MaxRootAttributes = CrmQueryLimits.MaxSelectFields;
    public const int MaxLinkAttributes = CrmQueryLimits.MaxLinkSelectFields;
    public const int MaxOrders = CrmQueryLimits.MaxSortFields;
    public const int MaxAggregateAttributes = CrmQueryLimits.MaxAggregates;
    public const int MaxGroupByAttributes = CrmQueryLimits.MaxGroupByFields;
    public const int MaxLimitationLength = 400;
}
