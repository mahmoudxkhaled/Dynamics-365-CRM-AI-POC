namespace CrmAiPoc.Api.Security;

/// <summary>
/// Server-established user context. Never trust client-supplied roles for authorization.
/// </summary>
public sealed class CrmUserContext
{
    public required string UserId { get; init; }
    public required string DisplayName { get; init; }
}
