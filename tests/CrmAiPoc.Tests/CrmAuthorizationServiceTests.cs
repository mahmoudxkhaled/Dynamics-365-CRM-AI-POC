using CrmAiPoc.Api.Configuration;
using CrmAiPoc.Api.Crm.Metadata;
using CrmAiPoc.Api.Security;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CrmAiPoc.Tests;

public class CrmAuthorizationServiceTests
{
    [Fact]
    public void AuthorizeRead_AllowsEntityInApprovedEntities()
    {
        var auth = CreateAuth(["account", "contact", "salesorder"]);

        Assert.True(auth.AuthorizeRead("account", "query_crm").Allowed);
        Assert.True(auth.AuthorizeRead("contact", "query_crm").Allowed);
        Assert.True(auth.AuthorizeRead("salesorder", "query_crm").Allowed);
    }

    [Fact]
    public void AuthorizeRead_DeniesEntityMissingFromApprovedEntities_EvenIfLegacyStaticListContainsIt()
    {
        // Legacy CrmEntityAllowlist still lists account/contact/salesorder, but config omits them.
        var auth = CreateAuth(["lead"]);

        Assert.False(auth.AuthorizeRead("account", "query_crm").Allowed);
        Assert.False(auth.AuthorizeRead("contact", "query_crm").Allowed);
        Assert.False(auth.AuthorizeRead("salesorder", "query_crm").Allowed);
        Assert.True(auth.AuthorizeRead("lead", "query_crm").Allowed);
    }

    [Fact]
    public void AuthorizeRead_DeniesSalesOrderWhenRemovedFromConfig()
    {
        var auth = CreateAuth(["account", "contact", "lead"]);

        var decision = auth.AuthorizeRead("salesorder", "query_crm");
        Assert.False(decision.Allowed);
        Assert.Contains("allowlist", decision.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AuthorizeRead_DeniesAccountWhenRemovedFromConfig()
    {
        var auth = CreateAuth(["contact", "salesorder"]);

        var decision = auth.AuthorizeRead("account", "query_crm");
        Assert.False(decision.Allowed);
        Assert.Contains("allowlist", decision.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AuthorizeRead_AllowsNewlyConfiguredEntityWithoutCSharpConstants()
    {
        var auth = CreateAuth(["opportunity"]);

        Assert.True(auth.AuthorizeRead("opportunity", "query_crm").Allowed);
        Assert.False(CrmEntityAllowlist.IsEntityAllowed("opportunity"));
    }

    [Fact]
    public void AuthorizeRead_DeniesWhenApprovalPolicyMissing()
    {
        var auth = new CrmAuthorizationService(
            Options.Create(new CrmOptions { ApprovedEntities = ["account"] }),
            NullLogger<CrmAuthorizationService>.Instance,
            approvalPolicy: null);

        Assert.False(auth.AuthorizeRead("account", "query_crm").Allowed);
        Assert.False(auth.AuthorizeRead("salesorder", "query_crm").Allowed);
    }

    [Fact]
    public void AuthorizeRead_DeniesWhenApprovedEntitiesEmpty()
    {
        var auth = CreateAuth([]);

        Assert.False(auth.AuthorizeRead("account", "query_crm").Allowed);
        Assert.False(auth.AuthorizeRead("contact", "query_crm").Allowed);
        Assert.False(auth.AuthorizeRead("salesorder", "query_crm").Allowed);
    }

    [Fact]
    public void AuthorizeRead_EntityNamesAreCaseInsensitive()
    {
        var auth = CreateAuth(["Account", "SalesOrder"]);

        Assert.True(auth.AuthorizeRead("account", "query_crm").Allowed);
        Assert.True(auth.AuthorizeRead("ACCOUNT", "query_crm").Allowed);
        Assert.True(auth.AuthorizeRead("salesorder", "query_crm").Allowed);
        Assert.True(auth.AuthorizeRead("SALESORDER", "query_crm").Allowed);
    }

    [Fact]
    public void AuthorizeRead_DeniesWriteShapedOperations()
    {
        var auth = CreateAuth(["account"]);

        Assert.False(auth.AuthorizeRead("account", "delete_accounts").Allowed);
        Assert.False(auth.AuthorizeRead("account", "create_account").Allowed);
        Assert.False(auth.AuthorizeRead("account", "update_account").Allowed);
    }

    [Fact]
    public void AuthorizeRead_AllowsConfiguredReadOperationPrefixes()
    {
        var auth = CreateAuth(["account"]);

        Assert.True(auth.AuthorizeRead("account", "query_crm").Allowed);
        Assert.True(auth.AuthorizeRead("account", "read_schema_metadata").Allowed);
        Assert.True(auth.AuthorizeRead("account", "search_accounts").Allowed);
        Assert.True(auth.AuthorizeRead("account", "get_account").Allowed);
        Assert.True(auth.AuthorizeRead("account", "count_accounts").Allowed);
    }

    [Fact]
    public void RelationshipApproval_StillRequiresBothEntitiesApproved()
    {
        var policy = CreatePolicy(["salesorder", "account"]);

        Assert.True(policy.IsRelationshipApproved(
            "order_customer_accounts", "salesorder", "account"));
        Assert.False(policy.IsRelationshipApproved(
            "order_customer_accounts", "salesorder", "contact"));
        Assert.False(policy.IsRelationshipApproved(
            "any_schema", "opportunity", "account"));
    }

    [Fact]
    public void ClientSuppliedRoleNames_AreNotUsed()
    {
        var auth = CreateAuth(["account"]);

        Assert.Equal("demo-user", auth.CurrentUser.UserId);
        Assert.DoesNotContain("admin", auth.CurrentUser.UserId, StringComparison.OrdinalIgnoreCase);
    }

    private static CrmAuthorizationService CreateAuth(IEnumerable<string> approvedEntities)
    {
        var options = Options.Create(new CrmOptions
        {
            ApprovedEntities = approvedEntities.ToList()
        });
        return new CrmAuthorizationService(
            options,
            NullLogger<CrmAuthorizationService>.Instance,
            CreatePolicy(approvedEntities));
    }

    private static EntityApprovalPolicy CreatePolicy(IEnumerable<string> approvedEntities) =>
        new(Options.Create(new CrmOptions
        {
            ApprovedEntities = approvedEntities.ToList()
        }));
}
