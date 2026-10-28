namespace CrmAiPoc.Api.Security;

public sealed class AuthorizationDecision
{
    public required bool Allowed { get; init; }
    public required string Reason { get; init; }
    public string? Entity { get; init; }
    public string? Operation { get; init; }

    public static AuthorizationDecision Allow(string operation, string? entity = null) =>
        new()
        {
            Allowed = true,
            Reason = "Allowed by server-side policy.",
            Entity = entity,
            Operation = operation
        };

    public static AuthorizationDecision Deny(string reason, string operation, string? entity = null) =>
        new()
        {
            Allowed = false,
            Reason = reason,
            Entity = entity,
            Operation = operation
        };
}
