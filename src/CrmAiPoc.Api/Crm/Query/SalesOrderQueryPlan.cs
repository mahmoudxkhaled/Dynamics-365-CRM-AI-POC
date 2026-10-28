namespace CrmAiPoc.Api.Crm.Query;

/// <summary>
/// Structured, allowlisted Sales Order query plan produced by tool calling.
/// Never contains raw OData, SQL, or FetchXML.
/// </summary>
public sealed class SalesOrderQueryPlan
{
    public SalesOrderQueryOperation Operation { get; init; } = SalesOrderQueryOperation.List;

    /// <summary>AND-combined filters (max 3).</summary>
    public IReadOnlyList<SalesOrderQueryFilter> Filters { get; init; } = Array.Empty<SalesOrderQueryFilter>();

    /// <summary>Optional relative date range applied to an approved date field.</summary>
    public SalesOrderRelativeDateFilter? RelativeDate { get; init; }

    public string? SortField { get; init; }

    public SalesOrderSortDirection SortDirection { get; init; } = SalesOrderSortDirection.Desc;

    /// <summary>List page size. Ignored for count (count uses $top=1 with $count=true).</summary>
    public int Limit { get; init; } = SalesOrderQueryLimits.DefaultLimit;
}

public sealed class SalesOrderQueryFilter
{
    public required string Field { get; init; }
    public required SalesOrderFilterOperator Operator { get; init; }

    /// <summary>
    /// Raw value from the tool call: number, GUID, ISO date, status/state label or numeric code, or text for contains.
    /// </summary>
    public required string Value { get; init; }
}

public sealed class SalesOrderRelativeDateFilter
{
    public required string Field { get; init; }
    public required RelativeDateToken Token { get; init; }
}

public static class SalesOrderQueryLimits
{
    public const int MaxFilters = 3;
    public const int DefaultLimit = 10;
    public const int MaxLimit = 50;
    public const int MinLimit = 1;

    /// <summary>Dynamics On-Prem v9.1 commonly caps @odata.count at this value.</summary>
    public const int DynamicsODataCountCeiling = 5000;
}
