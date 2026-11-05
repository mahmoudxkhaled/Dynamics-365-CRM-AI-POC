using CrmAiPoc.Api.Ai;
using CrmAiPoc.Api.Configuration;
using CrmAiPoc.Api.Crm.Generic;
using CrmAiPoc.Api.Crm.Metadata;
using Microsoft.Extensions.Options;

namespace CrmAiPoc.Tests;

public sealed class HybridEntityResolutionTests
{
    private static (EntityApprovalPolicy Policy, IReadOnlyList<CrmEntityMetadata> Entities) Create()
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
        return (policy, entities);
    }

    [Theory]
    [InlineData("Show me the latest 5 accounts", "account")]
    [InlineData("Show me the latest 5 sales orders", "salesorder")]
    [InlineData("count sales orders", "salesorder")]
    [InlineData("show contacts for this account", "contact")]
    [InlineData("Show me the latest 5 leads", "lead")]
    [InlineData("list contacts", "contact")]
    [InlineData("customers in Cairo", "account")]
    public void Alias_And_Metadata_Match_English(string message, string expected)
    {
        var (policy, entities) = Create();
        var result = CrmEntityResolver.Resolve(message, aiProposedEntity: null, entities, policy);
        Assert.Equal(CrmEntityResolutionStatus.Resolved, result.Status);
        Assert.Equal(expected, result.LogicalName);
        Assert.NotEqual(CrmEntityResolutionSource.AiCatalogProposal, result.Source);
    }

    [Fact]
    public void Ai_Catalog_Fallback_When_No_Phrase_Match()
    {
        var (policy, entities) = Create();
        var result = CrmEntityResolver.Resolve(
            "what is the pipeline looking like this week?",
            aiProposedEntity: "lead",
            entities,
            policy);

        Assert.Equal(CrmEntityResolutionStatus.Resolved, result.Status);
        Assert.Equal("lead", result.LogicalName);
        Assert.Equal(CrmEntityResolutionSource.AiCatalogProposal, result.Source);
    }

    [Fact]
    public void Unauthorized_Ai_Proposal_Rejected()
    {
        var (policy, entities) = Create();
        var result = CrmEntityResolver.Resolve(
            "show me opportunities",
            aiProposedEntity: "opportunity",
            entities,
            policy);

        Assert.Equal(CrmEntityResolutionStatus.Unauthorized, result.Status);
        Assert.Contains("not an approved", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Ambiguous_Peer_Choice_Asks_Clarification()
    {
        var (policy, entities) = Create();
        var result = CrmEntityResolver.Resolve(
            "Do you mean accounts or contacts?",
            aiProposedEntity: null,
            entities,
            policy);

        Assert.Equal(CrmEntityResolutionStatus.Ambiguous, result.Status);
        Assert.Contains("account", result.Candidates);
        Assert.Contains("contact", result.Candidates);
        Assert.Contains("Which entity", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Unresolved_Without_Ai_Proposal()
    {
        var (policy, entities) = Create();
        var result = CrmEntityResolver.Resolve(
            "how is the weather today?",
            aiProposedEntity: null,
            entities,
            policy);

        Assert.Equal(CrmEntityResolutionStatus.Unresolved, result.Status);
    }

    [Fact]
    public void IntentHelper_Uses_Hybrid_Resolver()
    {
        var (policy, _) = Create();
        Assert.Equal("salesorder", CrmQueryIntentHelper.DetectEntityLogicalName("count sales orders", policy));
        Assert.Equal("account", CrmQueryIntentHelper.DetectEntityLogicalName("list customers", policy));
        Assert.Null(CrmQueryIntentHelper.DetectEntityLogicalName("opportunities please", policy));
    }

    [Fact]
    public void Relationship_Allowlist_Optional_When_Empty()
    {
        var (policy, _) = Create();
        Assert.False(policy.HasRelationshipAllowlist);
        Assert.True(policy.IsRelationshipApproved(
            "te_account_salesorder_customeraccountid", "salesorder", "account"));
        Assert.True(policy.IsRelationshipApproved(
            "any_discovered_rel_between_approved", "contact", "account"));
        Assert.False(policy.IsRelationshipApproved(
            "te_account_salesorder_customeraccountid", "salesorder", "opportunity"));
    }

    [Fact]
    public void Relationship_Allowlist_Restricts_When_Configured()
    {
        var options = Options.Create(new CrmOptions
        {
            ApprovedEntities = ["account", "salesorder"],
            ApprovedRelationships = ["te_account_salesorder_customeraccountid"]
        });
        var policy = new EntityApprovalPolicy(options);
        Assert.True(policy.HasRelationshipAllowlist);
        Assert.True(policy.IsRelationshipApproved(
            "te_account_salesorder_customeraccountid", "salesorder", "account"));
        Assert.False(policy.IsRelationshipApproved(
            "order_customer_accounts", "salesorder", "account"));
    }

    [Fact]
    public void ResolveProposedLinks_Fills_Unique_SchemaName_On_Ai_Link()
    {
        var (policy, entities) = Create();
        var labeler = new CrmRelationshipLabeler(Options.Create(new CrmOptions()));
        var plan = new CrmQueryPlan
        {
            Entity = "contact",
            Operation = CrmQueryOperation.List,
            Limit = 5,
            Links = [new CrmLinkSpec { Entity = "account" }]
        };
        var attach = CrmRelationshipDiscovery.ResolveProposedLinks(
            plan,
            "show contacts with their account",
            entities,
            policy,
            labeler);

        Assert.Null(attach.ClarificationMessage);
        Assert.Single(attach.Plan.Links);
        Assert.Equal("account", attach.Plan.Links[0].Entity);
        Assert.Equal("contact_customer_accounts", attach.Plan.Links[0].Relationship);
    }

    [Fact]
    public void ResolveProposedLinks_CustomerAccount_Phrase_Selects_CustomerAccount_Relationship()
    {
        var (policy, entities) = Create();
        var labeler = new CrmRelationshipLabeler(Options.Create(new CrmOptions()));
        var plan = new CrmQueryPlan
        {
            Entity = "salesorder",
            Operation = CrmQueryOperation.List,
            Limit = 5,
            Links = [new CrmLinkSpec { Entity = "account" }]
        };
        var attach = CrmRelationshipDiscovery.ResolveProposedLinks(
            plan,
            "show sales orders with customer accounts",
            entities,
            policy,
            labeler);

        Assert.Null(attach.ClarificationMessage);
        Assert.Single(attach.Plan.Links);
        Assert.Equal("te_account_salesorder_customeraccountid", attach.Plan.Links[0].Relationship);
    }

    [Fact]
    public void ResolveProposedLinks_Does_Not_Invent_Links_From_Keywords_Alone()
    {
        var (policy, entities) = Create();
        var labeler = new CrmRelationshipLabeler(Options.Create(new CrmOptions()));
        var plan = new CrmQueryPlan { Entity = "salesorder", Operation = CrmQueryOperation.List, Limit = 5 };
        var attach = CrmRelationshipDiscovery.ResolveProposedLinks(
            plan,
            "Show Sales Orders with Account details",
            entities,
            policy,
            labeler);

        Assert.Null(attach.ClarificationMessage);
        Assert.Empty(attach.Plan.Links);
    }

    [Fact]
    public void Ambiguous_Account_Link_Fails_Without_Pending_Resume_Or_Option_Menu()
    {
        var (policy, entities) = Create();
        var labeler = new CrmRelationshipLabeler(Options.Create(new CrmOptions()));
        var plan = new CrmQueryPlan
        {
            Entity = "salesorder",
            Operation = CrmQueryOperation.List,
            Limit = 5,
            Links = [new CrmLinkSpec { Entity = "account" }]
        };
        var attach = CrmRelationshipDiscovery.ResolveProposedLinks(
            plan,
            "show sales orders with Customer Account or Potential Customer",
            entities,
            policy,
            labeler);

        Assert.False(string.IsNullOrWhiteSpace(attach.ClarificationMessage));
        Assert.Contains("Multiple approved relationships", attach.ClarificationMessage!, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("option number", attach.ClarificationMessage!, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Reply with", attach.ClarificationMessage!, StringComparison.OrdinalIgnoreCase);
        // Plan is not mutated into an executable join; caller must not resume it.
        Assert.DoesNotContain(attach.Plan.Links, l => !string.IsNullOrWhiteSpace(l.Relationship));
    }

    [Fact]
    public void DisplayName_Match_From_Metadata()
    {
        var (policy, entities) = Create();
        // Seed display name "Sales Order"
        var result = CrmEntityResolver.Resolve("list Sales Order records", null, entities, policy);
        Assert.Equal(CrmEntityResolutionStatus.Resolved, result.Status);
        Assert.Equal("salesorder", result.LogicalName);
    }
}
