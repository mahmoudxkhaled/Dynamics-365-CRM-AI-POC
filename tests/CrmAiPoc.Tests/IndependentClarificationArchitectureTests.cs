using System.Reflection;
using CrmAiPoc.Api.Ai;
using CrmAiPoc.Api.Configuration;
using CrmAiPoc.Api.Crm.Generic;
using CrmAiPoc.Api.Crm.Metadata;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace CrmAiPoc.Tests;

/// <summary>
/// Guards the independent-question architecture: Groq owns clarification;
/// backend never resumes pending relationship plans.
/// </summary>
public sealed class IndependentClarificationArchitectureTests
{
    private static (EntityApprovalPolicy Policy, IReadOnlyList<CrmEntityMetadata> Entities, CrmRelationshipLabeler Labeler) Create()
    {
        var options = Options.Create(new CrmOptions
        {
            ApprovedEntities = ["account", "contact", "salesorder"],
            ApprovedRelationships = [],
            EntityAliases = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["account"] = ["accounts", "customers", "customer account", "customer accounts"],
                ["salesorder"] = ["sales order", "sales orders", "orders"]
            }
        });
        var policy = new EntityApprovalPolicy(options);
        var entities = StaticSeedCrmMetadata.CreateApprovedSeeds(policy);
        var labeler = new CrmRelationshipLabeler(options);
        return (policy, entities, labeler);
    }

    [Fact]
    public void ConversationSession_Has_No_Pending_Relationship_State()
    {
        var sessionType = typeof(ConversationSession);
        Assert.Null(sessionType.GetProperty("PendingRelationship"));
        Assert.Null(sessionType.GetMethod("SetPendingRelationship"));
        Assert.Null(sessionType.GetMethod("ClearPendingRelationship"));
        Assert.Null(typeof(CrmRelationshipDiscovery).Assembly.GetType(
            "CrmAiPoc.Api.Ai.PendingRelationshipClarification"));
    }

    [Fact]
    public void Ambiguous_Proposed_Link_Fails_Without_Option_Menu()
    {
        var (policy, entities, labeler) = Create();

        var attach = CrmRelationshipDiscovery.ResolveProposedLinks(
            new CrmQueryPlan
            {
                Entity = "salesorder",
                Limit = 5,
                Links = [new CrmLinkSpec { Entity = "account" }]
            },
            "Show Sales Orders with Customer Account or Potential Customer",
            entities,
            policy,
            labeler);

        Assert.False(string.IsNullOrWhiteSpace(attach.ClarificationMessage));
        Assert.Contains("Multiple approved relationships", attach.ClarificationMessage!, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("option number", attach.ClarificationMessage!, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Reply with", attach.ClarificationMessage!, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(attach.ClarificationMessage!, "1)", StringComparison.Ordinal);
    }

    [Fact]
    public void Explicit_SchemaName_Is_Preserved()
    {
        var (policy, entities, labeler) = Create();
        var so = entities.First(e => e.LogicalName == "salesorder");
        var schema = so.Relationships
            .First(r => r.IsApproved &&
                        r.ReferencedEntity.Equals("account", StringComparison.OrdinalIgnoreCase))
            .SchemaName;

        var attach = CrmRelationshipDiscovery.ResolveProposedLinks(
            new CrmQueryPlan
            {
                Entity = "salesorder",
                Limit = 5,
                Links =
                [
                    new CrmLinkSpec
                    {
                        Entity = "account",
                        Relationship = schema,
                        Select = ["name"]
                    }
                ]
            },
            "Show Sales Orders with related Account",
            entities,
            policy,
            labeler);

        Assert.Null(attach.ClarificationMessage);
        Assert.Single(attach.Plan.Links);
        Assert.Equal(schema, attach.Plan.Links[0].Relationship);
    }

    [Fact]
    public void Invented_SchemaName_With_Ambiguous_Wording_Is_Rejected()
    {
        var (policy, entities, labeler) = Create();

        var attach = CrmRelationshipDiscovery.ResolveProposedLinks(
            new CrmQueryPlan
            {
                Entity = "salesorder",
                Limit = 5,
                Links =
                [
                    new CrmLinkSpec
                    {
                        Entity = "account",
                        Relationship = "invented_relationship_schema"
                    }
                ]
            },
            "Show Sales Orders with Customer Account or Potential Customer",
            entities,
            policy,
            labeler);

        Assert.False(string.IsNullOrWhiteSpace(attach.ClarificationMessage));
        Assert.Contains("Multiple approved relationships", attach.ClarificationMessage!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildOutboundHistory_Sends_Only_Current_Question()
    {
        var messages = ChatService.BuildOutboundHistory(
            "Show the latest 5 Sales Orders by Created On.",
            catalog: null);

        Assert.Equal(2, messages.Count);
        Assert.Equal(ChatRole.System, messages[0].Role);
        Assert.Equal(ChatRole.User, messages[1].Role);
        Assert.Equal("Show the latest 5 Sales Orders by Created On.", messages[1].Text);
        Assert.DoesNotContain(messages, m => m.Role == ChatRole.Assistant);
    }

    [Fact]
    public void Clarification_Suggestions_Remain_Complete_Standalone_Questions()
    {
        var parsed = ClarificationResponseParser.TryParse(
            """
            {
              "type": "clarification",
              "message": "Which relationship should I use?",
              "suggestions": [
                {
                  "label": "Customer Account",
                  "question": "Show Sales Orders with their Customer Account details.",
                  "entityLogicalName": "salesorder",
                  "relationshipSchemaNames": ["order_customer_accounts"]
                },
                {
                  "label": "Branch Account",
                  "question": "Show Sales Orders with their Branch Account details.",
                  "entityLogicalName": "salesorder",
                  "relationshipSchemaNames": ["te_account_salesorder_branchaccountid"]
                }
              ]
            }
            """);

        Assert.True(parsed.Success);
        Assert.All(parsed.Suggestions, s =>
        {
            Assert.Contains(' ', s.Question);
            Assert.True(s.Question.Length >= 12);
        });
    }

    [Fact]
    public void Discovery_No_Longer_Exposes_Resume_APIs()
    {
        var type = typeof(CrmRelationshipDiscovery);
        Assert.Null(type.GetMethod("AttachSelectedRelationship", BindingFlags.Public | BindingFlags.Static));
        Assert.Null(type.GetMethod("TryMatchClarificationSelection", BindingFlags.Public | BindingFlags.Static));
        Assert.Null(typeof(CrmRelationshipDiscovery.AutoAttachResult).GetProperty("Pending"));
    }
}
