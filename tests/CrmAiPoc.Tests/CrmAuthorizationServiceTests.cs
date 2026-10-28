using CrmAiPoc.Api.Configuration;
using CrmAiPoc.Api.Crm.Models;
using CrmAiPoc.Api.Security;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CrmAiPoc.Tests;

public class CrmAuthorizationServiceTests
{
    private readonly CrmAuthorizationService _auth = new(
        Options.Create(new CrmOptions()),
        NullLogger<CrmAuthorizationService>.Instance);

    [Fact]
    public void AuthorizeRead_AllowsAccountAndContact()
    {
        Assert.True(_auth.AuthorizeRead(CrmEntityAllowlist.Account, "search_accounts").Allowed);
        Assert.True(_auth.AuthorizeRead(CrmEntityAllowlist.Contact, "get_contacts_for_account").Allowed);
    }

    [Fact]
    public void AuthorizeRead_DeniesUnknownEntity()
    {
        var decision = _auth.AuthorizeRead("opportunity", "search_opportunities");
        Assert.False(decision.Allowed);
        Assert.Contains("allowlist", decision.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AuthorizeRead_DeniesWriteShapedOperations()
    {
        var decision = _auth.AuthorizeRead(CrmEntityAllowlist.Account, "delete_accounts");
        Assert.False(decision.Allowed);
    }

    [Fact]
    public void AuthorizeAccountAccess_DeniesConfidentialRecord()
    {
        var account = new CrmAccount
        {
            AccountId = CrmAuthorizationService.DeniedConfidentialAccountId,
            Name = "Confidential Strategic Partner",
            IsConfidential = true
        };

        var decision = _auth.AuthorizeAccountAccess(account, "search_accounts");
        Assert.False(decision.Allowed);
        Assert.Contains("permission", decision.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ClientSuppliedRoleNames_AreNotUsed()
    {
        // Authorization uses server mock identity only.
        Assert.Equal("demo-user", _auth.CurrentUser.UserId);
        Assert.DoesNotContain("admin", _auth.CurrentUser.UserId, StringComparison.OrdinalIgnoreCase);
    }
}
