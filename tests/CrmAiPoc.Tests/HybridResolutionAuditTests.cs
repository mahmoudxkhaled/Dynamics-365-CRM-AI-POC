using CrmAiPoc.Api.Configuration;
using CrmAiPoc.Api.Crm.Generic;
using CrmAiPoc.Api.Crm.Metadata;
using Microsoft.Extensions.Options;

namespace CrmAiPoc.Tests;

/// <summary>
/// Audit-oriented verification of hybrid entity resolution and join FetchXML shape.
/// Documents current behavior including known intent gaps.
/// </summary>
public sealed class HybridResolutionAuditTests
{
    private static (EntityApprovalPolicy Policy, IReadOnlyList<CrmEntityMetadata> Entities, GenericCrmQueryBuilder Builder)
        Create()
    {
        var options = Options.Create(new CrmOptions
        {
            ApprovedEntities = ["account", "contact", "salesorder", "lead"],
            ApprovedRelationships = [],
            EntityAliases = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["account"] = ["accounts", "customers", "customer account", "customer accounts"],
                ["contact"] = ["contacts", "contact persons", "primary contact", "primary contacts"],
                ["salesorder"] = ["sales order", "sales orders", "orders"],
                ["lead"] = ["leads", "prospects"]
            }
        });
        var policy = new EntityApprovalPolicy(options);
        var entities = StaticSeedCrmMetadata.CreateApprovedSeeds(policy)
            .Concat([GenericApprovedEntityStubFactory.Create("lead", policy)])
            .ToArray();
        return (policy, entities, new GenericCrmQueryBuilder(options));
    }

    [Theory]
    [InlineData("show latest 5 sales orders", "salesorder")]
    [InlineData("count sales orders", "salesorder")]
    [InlineData("show contacts for this account", "contact")]
    [InlineData("list contacts", "contact")]
    public void Intent_Aligned_Cases_Resolve_Correct_Primary(string message, string expected)
    {
        var (policy, entities, _) = Create();
        var result = CrmEntityResolver.Resolve(message, null, entities, policy);
        Assert.Equal(CrmEntityResolutionStatus.Resolved, result.Status);
        Assert.Equal(expected, result.LogicalName);
    }

    [Theory]
    [InlineData("Show Account names for the latest sales orders", "salesorder")]
    [InlineData("count sales orders", "salesorder")]
    [InlineData("show contacts for this account", "contact")]
    [InlineData("latest sales orders with account name", "salesorder")]
    public void IntentAware_Primary_Uses_Result_Shape_Not_Leftmost(string message, string expected)
    {
        var (policy, entities, _) = Create();
        var result = CrmEntityResolver.Resolve(message, null, entities, policy);
        Assert.Equal(CrmEntityResolutionStatus.Resolved, result.Status);
        Assert.Equal(expected, result.LogicalName);
    }

    [Fact]
    public void Ai_Semantic_Fallback_Validates_Against_Allowlist()
    {
        var (policy, entities, _) = Create();
        var ok = CrmEntityResolver.Resolve("pipeline this week", "lead", entities, policy);
        Assert.Equal(CrmEntityResolutionStatus.Resolved, ok.Status);
        Assert.Equal(CrmEntityResolutionSource.AiCatalogProposal, ok.Source);

        var bad = CrmEntityResolver.Resolve("show opportunities", "opportunity", entities, policy);
        Assert.Equal(CrmEntityResolutionStatus.Unauthorized, bad.Status);
    }

    [Fact]
    public void FetchXml_SalesOrder_To_Account_LinkEntity_Direction()
    {
        var (_, entities, builder) = Create();
        var so = entities.First(e => e.LogicalName == "salesorder");
        CrmEntityMetadata? Get(string n) =>
            entities.FirstOrDefault(e => e.LogicalName.Equals(n, StringComparison.OrdinalIgnoreCase));

        var built = builder.Build(
            new CrmQueryPlan
            {
                Entity = "salesorder",
                Limit = 5,
                Sort = [new CrmQuerySort { Field = "createdon", Direction = CrmSortDirection.Desc }],
                Links =
                [
                    new CrmLinkSpec
                    {
                        Entity = "account",
                        Relationship = "te_account_salesorder_customeraccountid",
                        Alias = "ca",
                        Select = ["name"],
                        JoinType = CrmLinkJoinType.Outer
                    }
                ]
            },
            so,
            getEntity: Get);

        Assert.True(built.Success, built.Error);
        Assert.Contains("<link-entity", built.FetchXml);
        Assert.Contains("name=\"account\"", built.FetchXml);
        Assert.Contains("from=\"accountid\"", built.FetchXml);
        Assert.Contains("to=\"te_customeraccountid\"", built.FetchXml);
        Assert.Contains("link-type=\"outer\"", built.FetchXml);
        Assert.Contains(built.LinkColumns, c => c.Key == "ca.name");
    }

    [Fact]
    public void FetchXml_Account_To_Contact_OneToMany_Direction()
    {
        var (_, entities, builder) = Create();
        var account = entities.First(e => e.LogicalName == "account");
        CrmEntityMetadata? Get(string n) =>
            entities.FirstOrDefault(e => e.LogicalName.Equals(n, StringComparison.OrdinalIgnoreCase));

        var built = builder.Build(
            new CrmQueryPlan
            {
                Entity = "account",
                Limit = 5,
                Links =
                [
                    new CrmLinkSpec
                    {
                        Entity = "contact",
                        Relationship = "contact_customer_accounts",
                        Alias = "ct",
                        Select = ["fullname"]
                    }
                ]
            },
            account,
            getEntity: Get);

        Assert.True(built.Success, built.Error);
        Assert.Contains("name=\"contact\"", built.FetchXml);
        Assert.Contains("from=\"parentcustomerid\"", built.FetchXml);
        Assert.Contains("to=\"accountid\"", built.FetchXml);
    }

    [Fact]
    public void FetchXml_TwoHop_Nested_LinkEntity_Supported()
    {
        var (_, entities, builder) = Create();
        var so = entities.First(e => e.LogicalName == "salesorder");
        CrmEntityMetadata? Get(string n) =>
            entities.FirstOrDefault(e => e.LogicalName.Equals(n, StringComparison.OrdinalIgnoreCase));

        var built = builder.Build(
            new CrmQueryPlan
            {
                Entity = "salesorder",
                Limit = 5,
                Links =
                [
                    new CrmLinkSpec
                    {
                        Entity = "account",
                        Relationship = "te_account_salesorder_customeraccountid",
                        Alias = "ca",
                        Select = ["name"],
                        Links =
                        [
                            new CrmLinkSpec
                            {
                                Entity = "contact",
                                Relationship = "contact_customer_accounts",
                                Alias = "ct",
                                Select = ["fullname"]
                            }
                        ]
                    }
                ]
            },
            so,
            getEntity: Get);

        Assert.True(built.Success, built.Error);
        Assert.Contains("alias=\"ca\"", built.FetchXml);
        Assert.Contains("alias=\"ct\"", built.FetchXml);
        // Nested: contact link-entity appears inside the FetchXML document after account link.
        var caIdx = built.FetchXml!.IndexOf("alias=\"ca\"", StringComparison.Ordinal);
        var ctIdx = built.FetchXml.IndexOf("alias=\"ct\"", StringComparison.Ordinal);
        Assert.True(caIdx >= 0 && ctIdx > caIdx);
        Assert.Contains(built.LinkColumns, c => c.Key == "ca.name");
        Assert.Contains(built.LinkColumns, c => c.Key == "ct.fullname");
    }

    [Fact]
    public void Ambiguous_SO_Account_Roles_Clarification_Uses_Business_Labels()
    {
        var (policy, entities, _) = Create();
        var labeler = new CrmRelationshipLabeler(Options.Create(new CrmOptions()));
        var attach = CrmRelationshipDiscovery.ResolveProposedLinks(
            new CrmQueryPlan
            {
                Entity = "salesorder",
                Limit = 5,
                Links = [new CrmLinkSpec { Entity = "account" }]
            },
            "sales orders with Customer Account or Potential Customer",
            entities,
            policy,
            labeler);
        Assert.NotNull(attach.Pending);
        Assert.Contains("Customer Account", attach.ClarificationMessage!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Potential Customer", attach.ClarificationMessage!, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("te_account_salesorder_customeraccountid", attach.ClarificationMessage!);
        Assert.DoesNotContain("order_customer_accounts", attach.ClarificationMessage!);
    }

    [Fact]
    public void Optional_ApprovedRelationships_Empty_Allows_Discovered_Between_Approved()
    {
        var (policy, _, _) = Create();
        Assert.False(policy.HasRelationshipAllowlist);
        Assert.True(policy.IsRelationshipApproved("any_live_rel", "salesorder", "account"));
        Assert.False(policy.IsRelationshipApproved("any_live_rel", "salesorder", "opportunity"));
    }
}
