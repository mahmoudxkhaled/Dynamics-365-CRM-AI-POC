using CrmAiPoc.Api.Configuration;
using CrmAiPoc.Api.Crm.Metadata;
using CrmAiPoc.Api.Crm.Models;
using Microsoft.Extensions.Options;

namespace CrmAiPoc.Api.Security;

/// <summary>
/// Backend authorization for read-only CRM access.
/// Uses a server-defined mock identity — client role claims are ignored.
/// </summary>
public sealed class CrmAuthorizationService : ICrmAuthorizationService
{
    // Fixed denied account for "confidential / no access" demonstration.
    public static readonly Guid DeniedConfidentialAccountId =
        Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");

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
            DisplayName = crm.MockUserDisplayName,
            DenyConfidentialRecords = true,
            DeniedAccountIds = [DeniedConfidentialAccountId]
        };
    }

    public CrmUserContext CurrentUser => _user;

    public AuthorizationDecision AuthorizeRead(string entityLogicalName, string operation)
    {
        var allowedByLegacy = CrmEntityAllowlist.IsEntityAllowed(entityLogicalName);
        var allowedByPolicy = _approvalPolicy?.IsApproved(entityLogicalName) == true;
        if (!allowedByLegacy && !allowedByPolicy)
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

    public AuthorizationDecision AuthorizeAccountAccess(CrmAccount account, string operation) =>
        AuthorizeAccountId(account.AccountId, account.IsConfidential, operation);

    public AuthorizationDecision AuthorizeAccountId(Guid accountId, bool isConfidential, string operation)
    {
        if (_user.DeniedAccountIds.Contains(accountId) ||
            (isConfidential && _user.DenyConfidentialRecords))
        {
            var deny = AuthorizationDecision.Deny(
                "Access denied: you do not have permission to read this confidential CRM record.",
                operation,
                CrmEntityAllowlist.Account);
            LogDecision(deny);
            return deny;
        }

        return AuthorizeRead(CrmEntityAllowlist.Account, operation);
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
