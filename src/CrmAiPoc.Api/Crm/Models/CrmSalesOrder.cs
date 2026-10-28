namespace CrmAiPoc.Api.Crm.Models;

/// <summary>
/// Minimal sales order projection. Field names are filled from verified schema discovery.
/// </summary>
public sealed class CrmSalesOrder
{
    public Guid SalesOrderId { get; init; }
    public string? Name { get; init; }
    public string? OrderNumber { get; init; }
    public Guid? CustomerAccountId { get; init; }
    public DateTimeOffset? CreatedOn { get; init; }
    public DateTimeOffset? ModifiedOn { get; init; }
    public decimal? TotalAmount { get; init; }
    public int? StatusCode { get; init; }
    public int? StateCode { get; init; }
    public string? StatusLabel { get; init; }
    public string? StateLabel { get; init; }
}
