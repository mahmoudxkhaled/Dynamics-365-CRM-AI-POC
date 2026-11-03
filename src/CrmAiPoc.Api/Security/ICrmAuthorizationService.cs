namespace CrmAiPoc.Api.Security;

public interface ICrmAuthorizationService
{
    CrmUserContext CurrentUser { get; }

    AuthorizationDecision AuthorizeRead(string entityLogicalName, string operation);
}
