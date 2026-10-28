namespace CrmAiPoc.Api.Crm.Models;

public sealed class CrmContact
{
    public required Guid ContactId { get; init; }
    public required Guid AccountId { get; init; }
    public required string FullName { get; init; }
    public string? EmailAddress1 { get; init; }
    public string? Telephone1 { get; init; }
    public string? JobTitle { get; init; }
}
