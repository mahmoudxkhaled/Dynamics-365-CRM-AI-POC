using CrmAiPoc.Api.Ai;
using CrmAiPoc.Api.Configuration;
using CrmAiPoc.Api.Crm.Generic;
using CrmAiPoc.Api.Crm.Metadata;
using CrmAiPoc.Api.Crm.Query;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace CrmAiPoc.Tests;

/// <summary>
/// Stubbed-AI regression: backend must preserve AI business intent and only validate/normalize.
/// </summary>
public sealed class AiQueryPlanAuthorityTests
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
                ["contact"] = ["contacts", "primary contact", "primary contacts"],
                ["salesorder"] = ["sales order", "sales orders", "orders"],
                ["lead"] = ["leads", "prospects"]
            },
            RelationshipLabels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["te_account_salesorder_customeraccountid"] = "Customer Account",
                ["order_customer_accounts"] = "Potential Customer (Account)",
                ["account_primary_contact"] = "Primary Contact"
            }
        });
        var policy = new EntityApprovalPolicy(options);
        return (policy, StaticSeedCrmMetadata.CreateApprovedSeeds(policy).ToArray(), new CrmRelationshipLabeler(options));
    }

    private static CrmQueryPlan ParseStub(IDictionary<string, object?> args)
    {
        var parsed = CrmQueryPlanParser.ParseFromToolArguments(args);
        Assert.True(parsed.Success, parsed.Error);
        Assert.NotNull(parsed.Plan);
        return parsed.Plan!;
    }

    [Fact]
    public void TestA_Preserves_List_Operation_And_Constraints()
    {
        var stub = new Dictionary<string, object?>
        {
            ["entity"] = "salesorder",
            ["operation"] = "list",
            ["limit"] = 10,
            ["sortField"] = "createdon",
            ["sortDirection"] = "desc",
            ["relativeDateField"] = "createdon",
            ["relativeDateToken"] = "last_6_calendar_months"
        };

        var plan = ParseStub(stub);
        Assert.Equal(CrmQueryOperation.List, plan.Operation);
        Assert.Equal(10, plan.Limit);
        Assert.Equal("createdon", plan.Sort[0].Field);
        Assert.Equal(RelativeDateToken.Last6CalendarMonths, plan.RelativeDate!.Token);
        Assert.Empty(plan.Aggregates);
        Assert.Empty(plan.GroupBy);
    }

    [Fact]
    public void TestB_Preserves_Count_Operation()
    {
        var plan = ParseStub(new Dictionary<string, object?>
        {
            ["entity"] = "salesorder",
            ["operation"] = "count",
            ["relativeDateField"] = "createdon",
            ["relativeDateToken"] = "last_6_calendar_months"
        });
        Assert.Equal(CrmQueryOperation.Count, plan.Operation);
        Assert.Equal(RelativeDateToken.Last6CalendarMonths, plan.RelativeDate!.Token);
    }

    [Fact]
    public void TestC_Preserves_Aggregate_And_Grouping()
    {
        var plan = ParseStub(new Dictionary<string, object?>
        {
            ["entity"] = "salesorder",
            ["operation"] = "aggregate",
            ["aggregatesJson"] = """[{"function":"count","field":"salesorderid","alias":"record_count"}]""",
            ["groupByJson"] =
                """[{"field":"createdon","alias":"year","dateGrouping":"year"},{"field":"createdon","alias":"month","dateGrouping":"month"}]""",
            ["relativeDateField"] = "createdon",
            ["relativeDateToken"] = "last_6_calendar_months"
        });
        Assert.Equal(CrmQueryOperation.Aggregate, plan.Operation);
        Assert.Single(plan.Aggregates);
        Assert.Equal(2, plan.GroupBy.Count);
    }

    [Fact]
    public void TestD_Does_Not_Invent_Active_Filter_From_Keywords()
    {
        // Stub intentionally omits Active filter even though the user said "active".
        var plan = ParseStub(new Dictionary<string, object?>
        {
            ["entity"] = "salesorder",
            ["operation"] = "list",
            ["limit"] = 10
        });
        Assert.Empty(plan.Filters);
    }

    [Fact]
    public void TestE_Does_Not_Override_Ai_Root_Entity()
    {
        var (policy, entities, labeler) = Create();
        // AI selected salesorder as root for "Account names for latest Sales Orders".
        var plan = ParseStub(new Dictionary<string, object?>
        {
            ["entity"] = "salesorder",
            ["operation"] = "list",
            ["limit"] = 5,
            ["linksJson"] = """[{"entity":"account","relationship":"te_account_salesorder_customeraccountid","select":["name"]}]"""
        });

        Assert.Equal("salesorder", plan.Entity);
        var resolved = CrmRelationshipDiscovery.ResolveProposedLinks(
            plan,
            "Show Account names for the latest Sales Orders.",
            entities,
            policy,
            labeler);
        Assert.Equal("salesorder", resolved.Plan.Entity);
        Assert.Single(resolved.Plan.Links);
        Assert.Equal("account", resolved.Plan.Links[0].Entity);
    }

    [Fact]
    public void TestF_CorrectToolCall_No_Longer_Exists_As_Semantic_Rewriter()
    {
        // Ensure keyword helper cannot fabricate ops/filters anymore.
        Assert.Null(typeof(CrmQueryIntentHelper).GetMethod("CorrectToolCall"));
        Assert.Null(typeof(CrmQueryIntentHelper).GetMethod("LooksLikeAggregateQuestion"));
        Assert.Null(typeof(CrmQueryIntentHelper).GetMethod("LooksLikeCountQuestion"));
    }

    [Fact]
    public void TestG_Resolves_Ai_Proposed_Relationships_Without_Keyword_AutoJoin()
    {
        var (policy, entities, labeler) = Create();
        var bare = new CrmQueryPlan { Entity = "salesorder", Operation = CrmQueryOperation.List, Limit = 5 };
        var noInvent = CrmRelationshipDiscovery.ResolveProposedLinks(
            bare,
            "Show Sales Orders with their customer account and primary contact names.",
            entities,
            policy,
            labeler);
        Assert.Empty(noInvent.Plan.Links);

        var withLinks = ParseStub(new Dictionary<string, object?>
        {
            ["entity"] = "salesorder",
            ["operation"] = "list",
            ["limit"] = 5,
            ["linksJson"] =
                """[{"entity":"account","alias":"ca","select":["name"],"links":[{"entity":"contact","alias":"pc","select":["fullname"]}]}]"""
        });
        var resolved = CrmRelationshipDiscovery.ResolveProposedLinks(
            withLinks,
            "Show Sales Orders with their customer account and primary contact names.",
            entities,
            policy,
            labeler);
        Assert.True(string.IsNullOrWhiteSpace(resolved.ClarificationMessage), resolved.ClarificationMessage);
        Assert.Single(resolved.Plan.Links);
        Assert.False(string.IsNullOrWhiteSpace(resolved.Plan.Links[0].Relationship));
        Assert.Single(resolved.Plan.Links[0].Links);
        Assert.False(string.IsNullOrWhiteSpace(resolved.Plan.Links[0].Links[0].Relationship));
    }

    [Fact]
    public void TestH_Rejects_Unapproved_Entity_And_Bad_Related_Entity()
    {
        var fail = CrmQueryPlanParser.ParseFromToolArguments(new Dictionary<string, object?>
        {
            ["entity"] = "opportunity",
            ["operation"] = "list"
        });
        Assert.True(fail.Success); // parser allows any string; approval is ChatService/policy layer
        Assert.Equal("opportunity", fail.Plan!.Entity);

        var (policy, entities, labeler) = Create();
        Assert.False(policy.IsApproved("opportunity"));

        var badLink = ParseStub(new Dictionary<string, object?>
        {
            ["entity"] = "salesorder",
            ["operation"] = "list",
            ["linksJson"] = """[{"entity":"opportunity"}]"""
        });
        var resolved = CrmRelationshipDiscovery.ResolveProposedLinks(
            badLink, "show opportunities", entities, policy, labeler);
        Assert.Contains("not an approved", resolved.ClarificationMessage!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TestI_Complex_List_Stub_Preserved_With_Two_Hop_Links()
    {
        var (policy, entities, labeler) = Create();
        var plan = ParseStub(new Dictionary<string, object?>
        {
            ["entity"] = "salesorder",
            ["operation"] = "list",
            ["limit"] = 10,
            ["selectJson"] = """["name","ordernumber","createdon","totalamount"]""",
            ["filtersJson"] = """[{"field":"statecode","operator":"eq","value":"Active"}]""",
            ["relativeDateField"] = "createdon",
            ["relativeDateToken"] = "last_6_calendar_months",
            ["sortField"] = "createdon",
            ["sortDirection"] = "desc",
            ["clientSideSortJson"] = """[{"field":"totalamount","direction":"desc"}]""",
            ["linksJson"] =
                """[{"entity":"account","alias":"ca","relationship":"te_account_salesorder_customeraccountid","select":["name"],"links":[{"entity":"contact","alias":"pc","relationship":"account_primary_contact","select":["fullname"]}]}]"""
        });

        Assert.Equal(CrmQueryOperation.List, plan.Operation);
        Assert.Empty(plan.Aggregates);
        Assert.Single(plan.Filters);
        Assert.Equal(RelativeDateToken.Last6CalendarMonths, plan.RelativeDate!.Token);

        var resolved = CrmRelationshipDiscovery.ResolveProposedLinks(plan, "complex", entities, policy, labeler);
        Assert.Equal("te_account_salesorder_customeraccountid", resolved.Plan.Links[0].Relationship);
        Assert.Equal("account_primary_contact", resolved.Plan.Links[0].Links[0].Relationship);
    }

    [Fact]
    public void TestJ_English_Only_Messages_And_Prompt()
    {
        Assert.Equal("Please enter your CRM question in English.", EnglishInputPolicy.RequireEnglishMessage);
        var history = ChatService.BuildOutboundHistory("Show accounts");
        var system = Assert.IsType<string>(history[0].Text);
        Assert.Contains("English only", system, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Arabic", system, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("usually means Customer Account", system, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FunctionCallContent_Is_Not_Mutated_By_Removed_CorrectToolCall()
    {
        // Simulate post-AI path: arguments pass through parser unchanged (no CorrectToolCall).
        var original = new FunctionCallContent(
            "1",
            "query_crm",
            new Dictionary<string, object?>
            {
                ["entity"] = "salesorder",
                ["operation"] = "list",
                ["limit"] = 7
            });
        var plan = ParseStub(original.Arguments!);
        Assert.Equal(7, plan.Limit);
        Assert.Equal(CrmQueryOperation.List, plan.Operation);
    }
}
