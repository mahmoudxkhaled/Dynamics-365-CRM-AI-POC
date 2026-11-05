using CrmAiPoc.Api.Configuration;
using CrmAiPoc.Api.Crm.Generic;
using CrmAiPoc.Api.Crm.Metadata;
using Microsoft.Extensions.Options;

namespace CrmAiPoc.Tests;

public sealed class PrimaryEntityAndPrimaryContactTests
{
    private static (EntityApprovalPolicy Policy, IReadOnlyList<CrmEntityMetadata> Entities, CrmRelationshipLabeler Labeler)
        Create()
    {
        var options = Options.Create(new CrmOptions
        {
            ApprovedEntities = ["account", "contact", "salesorder", "lead"],
            EntityAliases = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["account"] = ["accounts", "customers", "customer account", "customer accounts"],
                ["contact"] = ["contacts", "contact persons", "primary contact", "primary contacts"],
                ["salesorder"] = ["sales order", "sales orders", "orders"],
                ["lead"] = ["leads", "prospects"]
            }
        });
        var policy = new EntityApprovalPolicy(options);
        var entities = StaticSeedCrmMetadata.CreateApprovedSeeds(policy).ToArray();
        return (policy, entities, new CrmRelationshipLabeler(options));
    }

    [Theory]
    [InlineData("Show the latest 5 Sales Orders with Customer Account and Primary Contact names.")]
    [InlineData("Show Account names for the latest 5 Sales Orders.")]
    public void Root_Is_SalesOrder_For_Projection_Questions(string message)
    {
        var (policy, entities, _) = Create();
        var result = CrmEntityResolver.Resolve(message, null, entities, policy);
        Assert.Equal(CrmEntityResolutionStatus.Resolved, result.Status);
        Assert.Equal("salesorder", result.LogicalName);
    }

    [Fact]
    public void Show_Accounts_With_Related_SalesOrders_Root_Is_Account()
    {
        var (policy, entities, _) = Create();
        var result = CrmEntityResolver.Resolve(
            "Show Accounts with their related Sales Orders.",
            null,
            entities,
            policy);
        Assert.Equal(CrmEntityResolutionStatus.Resolved, result.Status);
        Assert.Equal("account", result.LogicalName);
    }

    [Fact]
    public void Show_Accounts_Or_Contacts_Requires_Clarification()
    {
        var (policy, entities, _) = Create();
        var result = CrmEntityResolver.Resolve("Show Accounts or Contacts.", null, entities, policy);
        Assert.Equal(CrmEntityResolutionStatus.Ambiguous, result.Status);
    }

    [Fact]
    public void TwoHop_CustomerAccount_PrimaryContact_Attaches_Without_Primary_Entity_Clarification()
    {
        var (policy, entities, labeler) = Create();
        const string msg =
            "Show the latest 5 Sales Orders with Customer Account and Primary Contact names.";

        var resolution = CrmEntityResolver.Resolve(msg, null, entities, policy);
        Assert.Equal(CrmEntityResolutionStatus.Resolved, resolution.Status);
        Assert.Equal("salesorder", resolution.LogicalName);

        var plan = new CrmQueryPlan
        {
            Entity = "salesorder",
            Operation = CrmQueryOperation.List,
            Limit = 5,
            Sort = [new CrmQuerySort { Field = "createdon", Direction = CrmSortDirection.Desc }],
            Links =
            [
                new CrmLinkSpec
                {
                    Entity = "account",
                    Alias = "ca",
                    Select = ["name"],
                    Links = [new CrmLinkSpec { Entity = "contact", Alias = "pc", Select = ["fullname"] }]
                }
            ]
        };

        var attach = CrmRelationshipDiscovery.ResolveProposedLinks(
            plan, msg, entities, policy, labeler, "en");

        Assert.Null(attach.ClarificationMessage);
        Assert.Single(attach.Plan.Links);
        Assert.Equal("account", attach.Plan.Links[0].Entity);
        Assert.Equal("te_account_salesorder_customeraccountid", attach.Plan.Links[0].Relationship);
        Assert.Equal(CrmLinkJoinType.Outer, attach.Plan.Links[0].JoinType);
        Assert.Single(attach.Plan.Links[0].Links);
        Assert.Equal("contact", attach.Plan.Links[0].Links[0].Entity);
        Assert.Equal("account_primary_contact", attach.Plan.Links[0].Links[0].Relationship);
        Assert.NotNull(attach.Plan.Links[0].Links);
        Assert.DoesNotContain(
            attach.Plan.Links[0].Links,
            l => l.Relationship.Equals("contact_customer_accounts", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void FetchXml_TwoHop_PrimaryContact_Uses_primarycontactid_Not_parentcustomerid()
    {
        var (policy, entities, labeler) = Create();
        const string msg =
            "Show the latest 5 Sales Orders with Customer Account and Primary Contact names.";
        var plan = new CrmQueryPlan
        {
            Entity = "salesorder",
            Limit = 5,
            Sort = [new CrmQuerySort { Field = "createdon", Direction = CrmSortDirection.Desc }],
            Links =
            [
                new CrmLinkSpec
                {
                    Entity = "account",
                    Alias = "ca",
                    Select = ["name"],
                    Links = [new CrmLinkSpec { Entity = "contact", Alias = "pc", Select = ["fullname"] }]
                }
            ]
        };
        var attach = CrmRelationshipDiscovery.ResolveProposedLinks(
            plan, msg, entities, policy, labeler);
        Assert.Null(attach.ClarificationMessage);

        var so = entities.First(e => e.LogicalName == "salesorder");
        CrmEntityMetadata? Get(string n) =>
            entities.FirstOrDefault(e => e.LogicalName.Equals(n, StringComparison.OrdinalIgnoreCase));
        var built = new GenericCrmQueryBuilder(Options.Create(new CrmOptions
        {
            ApprovedEntities = ["account", "contact", "salesorder", "lead"]
        })).Build(attach.Plan, so, getEntity: Get);

        Assert.True(built.Success, built.Error);
        Assert.Contains("top=\"5\"", built.FetchXml);
        Assert.Contains("name=\"account\"", built.FetchXml);
        Assert.Contains("to=\"te_customeraccountid\"", built.FetchXml);
        Assert.Contains("name=\"contact\"", built.FetchXml);
        Assert.Contains("to=\"primarycontactid\"", built.FetchXml);
        Assert.DoesNotContain("to=\"parentcustomerid\"", built.FetchXml);
        Assert.Contains("link-type=\"outer\"", built.FetchXml);
        Assert.Contains(built.LinkColumns, c => c.Key is "ca.name" or "account.name");
        Assert.Contains(built.LinkColumns, c => c.Key.Contains("fullname", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void PrimaryContact_Is_Not_Confused_With_Account_Contacts_Collection()
    {
        var (policy, entities, labeler) = Create();
        var account = entities.First(e => e.LogicalName == "account");
        var contact = entities.First(e => e.LogicalName == "contact");
        var path = CrmRelationshipDiscovery.FindUniquePath(
            account,
            contact,
            "Primary Contact name",
            labeler);
        Assert.True(path.Success, path.Error);
        Assert.Equal("account_primary_contact", path.Relationship!.SchemaName);
        Assert.NotEqual("contact_customer_accounts", path.Relationship.SchemaName);
    }
}
