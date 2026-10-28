using CrmAiPoc.Api.Crm.Metadata;
using CrmAiPoc.Api.Crm.Query;

namespace CrmAiPoc.Api.Crm.Generic;

public enum CrmQueryOperation
{
    List = 0,
    Count = 1
}

public enum CrmSortDirection
{
    Asc,
    Desc
}

/// <summary>
/// Structured, non-executable CRM query plan. Never contains OData/SQL/FetchXML.
/// </summary>
public sealed class CrmQueryPlan
{
    public required string Entity { get; init; }
    public CrmQueryOperation Operation { get; init; } = CrmQueryOperation.List;
    public IReadOnlyList<string> Select { get; init; } = Array.Empty<string>();
    public IReadOnlyList<CrmQueryFilter> Filters { get; init; } = Array.Empty<CrmQueryFilter>();
    public CrmRelativeDateFilter? RelativeDate { get; init; }
    public IReadOnlyList<CrmQuerySort> Sort { get; init; } = Array.Empty<CrmQuerySort>();
    public int Limit { get; init; } = CrmQueryLimits.DefaultLimit;

    /// <summary>
    /// Optional one-hop relationship query: related entity linked via approved relationship.
    /// Example: entity=contact, relatedFromEntity=account, relatedFromId=guid.
    /// </summary>
    public string? RelatedFromEntity { get; init; }
    public string? RelatedFromId { get; init; }
}

public sealed class CrmQueryFilter
{
    public required string Field { get; init; }
    public required CrmFilterOperator Operator { get; init; }
    public required string Value { get; init; }
}

public sealed class CrmQuerySort
{
    public required string Field { get; init; }
    public CrmSortDirection Direction { get; init; } = CrmSortDirection.Desc;
}

public sealed class CrmRelativeDateFilter
{
    public required string Field { get; init; }
    public required RelativeDateToken Token { get; init; }
}

public static class CrmQueryLimits
{
    public const int MaxFilters = 3;
    public const int MaxSortFields = 2;
    public const int MaxSelectFields = 20;
    public const int DefaultLimit = 10;
    public const int MaxLimit = 50;
    public const int MinLimit = 1;
    public const int DynamicsODataCountCeiling = 5000;
}

public sealed class GenericCrmQueryResult
{
    public required string Entity { get; init; }
    public required CrmQueryOperation Operation { get; init; }
    public required string RelativeRequestUri { get; init; }
    public IReadOnlyList<string> AppliedFilterSummary { get; init; } = Array.Empty<string>();
    public string? SortSummary { get; init; }
    public int Limit { get; init; }
    public IReadOnlyList<IReadOnlyDictionary<string, object?>> Items { get; init; } =
        Array.Empty<IReadOnlyDictionary<string, object?>>();
    public int? Count { get; init; }

    /// <summary>True when Dynamics reports the count ceiling was hit (exact total unknown).</summary>
    public bool CountMayBeCapped { get; init; }

    /// <summary>
    /// True when CRM returned @Microsoft.Dynamics.CRM.totalrecordcountlimitexceeded = true.
    /// </summary>
    public bool CountLimitExceededAnnotation { get; init; }

    public string? Message { get; init; }
    public string ProviderName { get; init; } = "Dynamics365";
    public string? PrimaryNameAttribute { get; init; }
    public string? PrimaryIdAttribute { get; init; }

    /// <summary>Validated $select OData property names used for the query.</summary>
    public IReadOnlyList<string> SelectFields { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Dynamic table columns derived from metadata + <see cref="SelectFields"/>.
    /// Preserved for zero-result list queries so the UI can still render headers.
    /// </summary>
    public IReadOnlyList<CrmTableColumn> Columns { get; init; } = Array.Empty<CrmTableColumn>();
}
