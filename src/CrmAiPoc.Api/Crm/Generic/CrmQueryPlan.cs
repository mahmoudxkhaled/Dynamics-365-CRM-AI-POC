using CrmAiPoc.Api.Crm.Metadata;
using CrmAiPoc.Api.Crm.Query;

namespace CrmAiPoc.Api.Crm.Generic;

public enum CrmQueryOperation
{
    List = 0,
    Count = 1,
    /// <summary>Server-side aggregation via FetchXML (COUNT/SUM/AVG/MIN/MAX + groupBy).</summary>
    Aggregate = 2
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

    /// <summary>
    /// Optional re-sort of the returned list page only (after FetchXML order + top).
    /// Used for two-stage requests: take latest N, then sort those N by amount.
    /// </summary>
    public IReadOnlyList<CrmQuerySort> ClientSideSort { get; init; } = Array.Empty<CrmQuerySort>();

    public int Limit { get; init; } = CrmQueryLimits.DefaultLimit;

    /// <summary>
    /// Optional one-hop relationship query: related entity linked via approved relationship.
    /// Example: entity=contact, relatedFromEntity=account, relatedFromId=guid.
    /// </summary>
    public string? RelatedFromEntity { get; init; }
    public string? RelatedFromId { get; init; }

    /// <summary>Aggregate functions for <see cref="CrmQueryOperation.Aggregate"/>.</summary>
    public IReadOnlyList<CrmAggregateSpec> Aggregates { get; init; } = Array.Empty<CrmAggregateSpec>();

    /// <summary>Group-by fields (optional dateGrouping) for aggregate queries.</summary>
    public IReadOnlyList<CrmGroupBySpec> GroupBy { get; init; } = Array.Empty<CrmGroupBySpec>();

    /// <summary>
    /// Metadata-validated related-entity links (FetchXML link-entity).
    /// Nested links enable bounded multi-hop joins (see <see cref="CrmQueryLimits.MaxLinkDepth"/>).
    /// </summary>
    public IReadOnlyList<CrmLinkSpec> Links { get; init; } = Array.Empty<CrmLinkSpec>();
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

/// <summary>
/// Date filter on an approved DateTime field. Prefer <see cref="Expression"/>;
/// <see cref="Token"/> remains for legacy fixed tokens.
/// </summary>
public sealed class CrmRelativeDateFilter
{
    public required string Field { get; init; }

    /// <summary>Legacy fixed token. Ignored when <see cref="Expression"/> is set.</summary>
    public RelativeDateToken? Token { get; init; }

    /// <summary>Structured date-range expression (absolute, last N months, this year, etc.).</summary>
    public CrmDateRangeExpression? Expression { get; init; }

    public CrmDateRangeExpression GetExpression()
    {
        if (Expression is not null)
        {
            return Expression;
        }

        if (Token is RelativeDateToken token)
        {
            return DateRangeResolver.FromLegacyToken(token);
        }

        throw new InvalidOperationException("Date filter requires Expression or Token.");
    }
}

public static class CrmQueryLimits
{
    public const int MaxFilters = 3;
    public const int MaxSortFields = 2;
    public const int MaxSelectFields = 20;
    public const int DefaultLimit = 10;
    public const int MaxLimit = 50;
    public const int MinLimit = 1;
    /// <summary>Legacy OData @$count ceiling formerly used for capped messaging.</summary>
    public const int DynamicsODataCountCeiling = 5000;

    /// <summary>
    /// Dynamics FetchXML aggregate evaluation limit (On-Prem / Online commonly 50,000).
    /// Counts at or above this threshold are treated as possibly capped / non-exact.
    /// </summary>
    public const int DynamicsFetchXmlAggregateCountCeiling = 50000;
    public const int MaxAggregates = 3;
    public const int MaxGroupByFields = 3;
    public const int MaxAggregateResultRows = 100;

    /// <summary>Maximum nesting depth for link-entity chains (root = 0).</summary>
    public const int MaxLinkDepth = 2;

    /// <summary>Maximum total linked entities across the plan (not counting the root).</summary>
    public const int MaxLinkedEntities = 3;

    /// <summary>Max attributes projected from each linked entity.</summary>
    public const int MaxLinkSelectFields = 5;
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

    /// <summary>Validated attribute keys used for projection / table columns (Web API JSON names).</summary>
    public IReadOnlyList<string> SelectFields { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Dynamic table columns derived from metadata + <see cref="SelectFields"/>.
    /// Preserved for zero-result list queries so the UI can still render headers.
    /// </summary>
    public IReadOnlyList<CrmTableColumn> Columns { get; init; } = Array.Empty<CrmTableColumn>();
}
