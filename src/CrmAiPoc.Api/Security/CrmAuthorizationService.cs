using CrmAiPoc.Api.Configuration;
using CrmAiPoc.Api.Crm.Metadata;
using Microsoft.Extensions.Options;

namespace CrmAiPoc.Api.Security;

/// <summary>
/// Backend authorization for read-only CRM access.
/// Uses a server-defined mock identity — client role claims are ignored.
/// </summary>
public sealed class CrmAuthorizationService : ICrmAuthorizationService
{
    private readonly CrmUserContext _user;
    private readonly EntityApprovalPolicy? _approvalPolicy;
    private readonly ILogger<CrmAuthorizationService> _logger;

    public CrmAuthorizationService(
        IOptions<CrmOptions> options,
        ILogger<CrmAuthorizationService> logger,
        EntityApprovalPolicy? approvalPolicy = null)
    {
        var crm = options.Value;
        _logger = logger;
        _approvalPolicy = approvalPolicy;
        _user = new CrmUserContext
        {
            UserId = crm.MockUserId,
            DisplayName = crm.MockUserDisplayName
        };
    }

    public CrmUserContext CurrentUser => _user;

    public AuthorizationDecision AuthorizeRead(string entityLogicalName, string operation)
    {
        // EntityApprovalPolicy (Crm:ApprovedEntities) is the sole entity allowlist.
        // Missing policy fails closed — legacy CrmEntityAllowlist must not grant access.
        if (_approvalPolicy is null || !_approvalPolicy.IsApproved(entityLogicalName))
        {
            var deny = AuthorizationDecision.Deny(
                $"Entity '{entityLogicalName}' is outside the approved CRM allowlist.",
                operation,
                entityLogicalName);
            LogDecision(deny);
            return deny;
        }

        if (!IsReadOperation(operation))
        {
            var deny = AuthorizationDecision.Deny(
                "Only read operations are permitted. Create, update, delete, and execute are blocked.",
                operation,
                entityLogicalName);
            LogDecision(deny);
            return deny;
        }

        var allow = AuthorizationDecision.Allow(operation, entityLogicalName);
        LogDecision(allow);
        return allow;
    }

    private static bool IsReadOperation(string operation) =>
        operation.StartsWith("read", StringComparison.OrdinalIgnoreCase) ||
        operation.StartsWith("search", StringComparison.OrdinalIgnoreCase) ||
        operation.StartsWith("get", StringComparison.OrdinalIgnoreCase) ||
        operation.StartsWith("count", StringComparison.OrdinalIgnoreCase) ||
        operation.StartsWith("query", StringComparison.OrdinalIgnoreCase);

    private void LogDecision(AuthorizationDecision decision)
    {
        _logger.LogInformation(
            "Authorization decision: Allowed={Allowed}, Operation={Operation}, Entity={Entity}, Reason={Reason}, UserId={UserId}",
            decision.Allowed,
            decision.Operation,
            decision.Entity,
            decision.Reason,
            _user.UserId);
    }
}
