namespace CrmAiPoc.Api.Crm.Models;

public sealed class CrmAccount
{
    public required Guid AccountId { get; init; }
    public required string Name { get; init; }
    public string? AccountNumber { get; init; }
    public string? Telephone1 { get; init; }
    public string? EmailAddress1 { get; init; }
    public string? City { get; init; }
    public string? Country { get; init; }
    public bool IsConfidential { get; init; }
    public string? Industry { get; init; }
}
