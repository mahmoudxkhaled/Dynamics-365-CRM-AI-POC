using CrmAiPoc.Api.Crm.Models;

namespace CrmAiPoc.Api.Security;

public interface ICrmAuthorizationService
{
    CrmUserContext CurrentUser { get; }

    AuthorizationDecision AuthorizeRead(string entityLogicalName, string operation);

    AuthorizationDecision AuthorizeAccountAccess(CrmAccount account, string operation);

    AuthorizationDecision AuthorizeAccountId(Guid accountId, bool isConfidential, string operation);
}
