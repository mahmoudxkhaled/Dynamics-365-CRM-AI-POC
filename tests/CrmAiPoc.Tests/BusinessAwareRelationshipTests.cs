using CrmAiPoc.Api.Configuration;
using CrmAiPoc.Api.Crm.Generic;
using CrmAiPoc.Api.Crm.Metadata;
using Microsoft.Extensions.Options;

namespace CrmAiPoc.Tests;

public sealed class BusinessAwareRelationshipTests
{
    private static (EntityApprovalPolicy Policy, IReadOnlyList<CrmEntityMetadata> Entities, CrmRelationshipLabeler Labeler)
        Create(IReadOnlyList<CrmRelationshipMetadata>? extraSalesOrderRels = null)
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
            },
            RelationshipLabels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["te_account_salesorder_customeraccountid"] = "Customer Account",
                ["order_customer_accounts"] = "Potential Customer (Account)",
                ["te_account_salesorder_branchaccountid"] = "Branch Account",
                ["account_primary_contact"] = "Primary Contact",
                ["contact_customer_accounts"] = "Account Contacts"
            }
        });
        var policy = new EntityApprovalPolicy(options);
        var entities = StaticSeedCrmMetadata.CreateApprovedSeeds(policy).ToArray();
        if (extraSalesOrderRels is { Count: > 0 })
        {
            entities = entities.Select(e =>
            {
                if (!e.LogicalName.Equals("salesorder", StringComparison.OrdinalIgnoreCase))
                {
                    return e;
                }

                return new CrmEntityMetadata
                {
                    LogicalName = e.LogicalName,
                    EntitySetName = e.EntitySetName,
                    DisplayName = e.DisplayName,
                    PrimaryIdAttribute = e.PrimaryIdAttribute,
                    PrimaryNameAttribute = e.PrimaryNameAttribute,
                    IsApprovedForAi = e.IsApprovedForAi,
                    Aliases = e.Aliases,
                    Attributes = e.Attributes,
                    Relationships = e.Relationships.Concat(extraSalesOrderRels).ToArray(),
                    Provenance = e.Provenance
                };
            }).ToArray();
        }

        return (policy, entities, new CrmRelationshipLabeler(options));
    }

    private static CrmQueryPlan AiPlanWithAccountLink(string? relationship = null) =>
        new()
        {
            Entity = "salesorder",
            Operation = CrmQueryOperation.List,
            Limit = 5,
            Links =
            [
                new CrmLinkSpec
                {
                    Entity = "account",
                    Relationship = relationship,
                    Select = ["name"]
                }
            ]
        };

    [Theory]
    [InlineData("Show Sales Orders with Account details")]
    [InlineData("show sales orders with account")]
    [InlineData("List Sales Orders including the Account name")]
    public void Ai_Link_Without_Schema_Resolves_CustomerAccount_For_Generic_Account(string message)
    {
        var (policy, entities, labeler) = Create();
        var attach = CrmRelationshipDiscovery.ResolveProposedLinks(
            AiPlanWithAccountLink(),
            message,
            entities,
            policy,
            labeler);

        Assert.True(string.IsNullOrWhiteSpace(attach.ClarificationMessage), attach.ClarificationMessage);
        Assert.Null(attach.Pending);
        Assert.Single(attach.Plan.Links);
        Assert.Equal("te_account_salesorder_customeraccountid", attach.Plan.Links[0].Relationship);
    }

    [Fact]
    public void Explicit_PotentialCustomer_Selects_PotentialCustomer_Relationship()
    {
        var (policy, entities, labeler) = Create();
        var attach = CrmRelationshipDiscovery.ResolveProposedLinks(
            AiPlanWithAccountLink(),
            "Show Sales Orders with the Potential Customer account",
            entities,
            policy,
            labeler);

        Assert.Null(attach.Pending);
        Assert.Single(attach.Plan.Links);
        Assert.Equal("order_customer_accounts", attach.Plan.Links[0].Relationship);
    }

    [Fact]
    public void Equally_Plausible_Account_Roles_Ask_Business_Friendly_Clarification()
    {
        var branch = new CrmRelationshipMetadata
        {
            SchemaName = "te_account_salesorder_branchaccountid",
            Kind = CrmRelationshipKind.ManyToOne,
            ReferencingEntity = "salesorder",
            ReferencedEntity = "account",
            ReferencingAttribute = "te_branchaccountid",
            ReferencedAttribute = "accountid",
            ReferencingAttributeDisplayName = "Branch Account",
            BusinessLabel = "Branch Account",
            IsApproved = true
        };
        var (policy, entities, labeler) = Create([branch]);

        var attach = CrmRelationshipDiscovery.ResolveProposedLinks(
            AiPlanWithAccountLink(),
            "Show Sales Orders with the Customer Account or the Branch Account",
            entities,
            policy,
            labeler);

        Assert.NotNull(attach.Pending);
        Assert.False(string.IsNullOrWhiteSpace(attach.ClarificationMessage));
        Assert.Contains("Which Account details", attach.ClarificationMessage!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Customer Account", attach.ClarificationMessage!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Branch Account", attach.ClarificationMessage!, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("te_account_salesorder", attach.ClarificationMessage!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TwoHop_Business_Query_Resolves_Ai_Proposed_Nested_Links()
    {
        var (policy, entities, labeler) = Create();
        const string message =
            "Show the latest 5 Sales Orders with Customer Account and Primary Contact names.";

        var resolution = CrmEntityResolver.Resolve(message, "salesorder", entities, policy);
        Assert.Equal(CrmEntityResolutionStatus.Resolved, resolution.Status);
        Assert.Equal("salesorder", resolution.LogicalName);

        var attach = CrmRelationshipDiscovery.ResolveProposedLinks(
            new CrmQueryPlan
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
                        Links =
                        [
                            new CrmLinkSpec { Entity = "contact", Alias = "pc", Select = ["fullname"] }
                        ]
                    }
                ]
            },
            message,
            entities,
            policy,
            labeler);

        Assert.Null(attach.Pending);
        Assert.True(string.IsNullOrWhiteSpace(attach.ClarificationMessage), attach.ClarificationMessage);
        Assert.Single(attach.Plan.Links);
        Assert.Equal("te_account_salesorder_customeraccountid", attach.Plan.Links[0].Relationship);
        Assert.Single(attach.Plan.Links[0].Links);
        Assert.Equal("account_primary_contact", attach.Plan.Links[0].Links[0].Relationship);
    }

    [Fact]
    public void Never_Invent_Links_When_Ai_Omits_Them()
    {
        var (policy, entities, labeler) = Create();
        var attach = CrmRelationshipDiscovery.ResolveProposedLinks(
            new CrmQueryPlan { Entity = "salesorder", Limit = 5 },
            "Show Sales Orders with Customer Account details",
            entities,
            policy,
            labeler);

        Assert.Null(attach.Pending);
        Assert.Empty(attach.Plan.Links);
    }
}
