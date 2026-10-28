using CrmAiPoc.Api.Crm.Models;

namespace CrmAiPoc.Api.Crm.Query;

public sealed class SalesOrderQueryExecutionResult
{
    public required SalesOrderQueryOperation Operation { get; init; }
    public required string RelativeRequestUri { get; init; }
    public required IReadOnlyList<string> AppliedFilterSummary { get; init; }
    public string? SortSummary { get; init; }
    public int Limit { get; init; }
    public IReadOnlyList<CrmSalesOrder> Items { get; init; } = Array.Empty<CrmSalesOrder>();

    /// <summary>Verified count from @odata.count when Operation=Count.</summary>
    public int? Count { get; init; }

    /// <summary>
    /// True when Dynamics returned the known OData count ceiling (5000), so the total may be incomplete.
    /// </summary>
    public bool CountMayBeCapped { get; init; }

    public string? Message { get; init; }
    public string ProviderName { get; init; } = "Dynamics365";
}
