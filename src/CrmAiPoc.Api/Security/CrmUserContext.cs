namespace CrmAiPoc.Api.Security;

/// <summary>
/// Server-established user context. Never trust client-supplied roles for authorization.
/// </summary>
public sealed class CrmUserContext
{
    public required string UserId { get; init; }
    public required string DisplayName { get; init; }

    /// <summary>Accounts the user may not read (mock permission demonstration).</summary>
    public HashSet<Guid> DeniedAccountIds { get; init; } = [];

    /// <summary>When true, confidential accounts are denied.</summary>
    public bool DenyConfidentialRecords { get; init; } = true;

    public bool CanReadEntity(string entityLogicalName) =>
        CrmEntityAllowlist.IsEntityAllowed(entityLogicalName);

    public bool CanReadField(string entityLogicalName, string fieldLogicalName) =>
        CrmEntityAllowlist.IsFieldAllowed(entityLogicalName, fieldLogicalName);
}
