using CrmAiPoc.Api.Crm.Metadata;

namespace CrmAiPoc.Api.Crm.Generic;

/// <summary>
/// Builds validated CRM Web API request URIs for business queries using FetchXML
/// (<c>entityset?fetchXml=...</c>). List and count only — aggregates use
/// <see cref="FetchXmlAggregateBuilder"/>.
/// </summary>
public interface IGenericCrmQueryBuilder
{
    GenericCrmQueryBuildResult Build(
        CrmQueryPlan plan,
        CrmEntityMetadata entity,
        CrmEntityMetadata? relatedFrom = null,
        Func<string, CrmEntityMetadata?>? getEntity = null);
}

public sealed class GenericCrmQueryBuildResult
{
    public bool Success { get; init; }
    public string? Error { get; init; }
    /// <summary>Web API relative path: {entitySet}?fetchXml={urlencoded}.</summary>
    public string? RelativeRequestUri { get; init; }
    /// <summary>Raw validated FetchXML (never taken from the AI).</summary>
    public string? FetchXml { get; init; }
    public CrmQueryOperation Operation { get; init; }
    public IReadOnlyList<string> AppliedFilterSummary { get; init; } = Array.Empty<string>();
    public string? SortSummary { get; init; }
    public int Limit { get; init; }
    /// <summary>Validated attribute keys used for projection / table columns (OData JSON names).</summary>
    public IReadOnlyList<string> SelectFields { get; init; } = Array.Empty<string>();

    /// <summary>Dynamic columns for linked-entity attributes (alias-qualified).</summary>
    public IReadOnlyList<CrmTableColumn> LinkColumns { get; init; } = Array.Empty<CrmTableColumn>();

    public static GenericCrmQueryBuildResult Ok(
        string uri,
        string fetchXml,
        CrmQueryOperation operation,
        IReadOnlyList<string> summaries,
        string? sortSummary,
        int limit,
        IReadOnlyList<string> select,
        IReadOnlyList<CrmTableColumn>? linkColumns = null) =>
        new()
        {
            Success = true,
            RelativeRequestUri = uri,
            FetchXml = fetchXml,
            Operation = operation,
            AppliedFilterSummary = summaries,
            SortSummary = sortSummary,
            Limit = limit,
            SelectFields = select,
            LinkColumns = linkColumns ?? Array.Empty<CrmTableColumn>()
        };

    public static GenericCrmQueryBuildResult Fail(string error) =>
        new() { Success = false, Error = error };
}
