using CrmAiPoc.Api.Ai;
using CrmAiPoc.Api.Configuration;
using CrmAiPoc.Api.Crm.Generic;
using CrmAiPoc.Api.Crm.Metadata;
using CrmAiPoc.Api.Security;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CrmAiPoc.Tests;

public class Phase51UnifiedQueryCrmTests
{
    private static (IGenericCrmRepository Repo, CrmAiTools Tools, EntityApprovalPolicy Policy) CreateStack()
    {
        var options = Options.Create(new CrmOptions
        {
            ApprovedEntities = ["account", "contact", "salesorder", "lead"],
            BusinessTimeZoneId = "Africa/Cairo",
            EntityAliases = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["account"] = ["accounts", "customers", "customer account", "customer accounts"],
                ["contact"] = ["contacts", "contact persons", "primary contact", "primary contacts"],
                ["salesorder"] = ["sales order", "sales orders", "orders"],
                ["lead"] = ["leads", "prospects"]
            }
});
        var policy = new EntityApprovalPolicy(options);
        var registry = new CrmMetadataRegistry();
        var metadata = new MockCrmMetadataService(registry, policy);
        var builder = new GenericCrmQueryBuilder(options);
        var auth = new CrmAuthorizationService(options, NullLogger<CrmAuthorizationService>.Instance, policy);
        var repo = new MockGenericCrmRepository(metadata, policy, builder, new FetchXmlAggregateBuilder(), auth);
        var tools = new CrmAiTools(repo, NullLogger<CrmAiTools>.Instance);
        return (repo, tools, policy);
    }

    [Theory]
    [InlineData("Show me the latest 5 sales orders", "salesorder")]
    [InlineData("Show me the latest 5 accounts", "account")]
    [InlineData("Count active sales orders", "salesorder")]
    public void DetectEntity_Vocabulary_Still_Works(string message, string expectedEntity)
    {
        Assert.Equal(expectedEntity, CrmQueryIntentHelper.DetectEntityLogicalName(message));
    }

    [Theory]
    [InlineData("salesorder")]
    [InlineData("account")]
    [InlineData("contact")]
    public async Task Same_Generic_Engine_For_Core_Entities(string entity)
    {
        var (repo, _, _) = CreateStack();
        var result = await repo.QueryAsync(new CrmQueryPlan
        {
            Entity = entity,
            Operation = CrmQueryOperation.List,
            Sort = [new CrmQuerySort { Field = "createdon", Direction = CrmSortDirection.Desc }],
            Limit = 5
        });

        Assert.Equal(entity, result.Entity);
        Assert.False(string.IsNullOrWhiteSpace(result.RelativeRequestUri));
        Assert.Contains("fetchXml=", result.RelativeRequestUri);
        Assert.DoesNotContain("$select=", result.RelativeRequestUri);
        Assert.DoesNotContain("$top=", result.RelativeRequestUri);
        var fetch = Uri.UnescapeDataString(result.RelativeRequestUri.Split("fetchXml=", 2)[1]);
        Assert.Contains("top=\"5\"", fetch);
        Assert.True(result.Items.Count <= 5);
    }

    [Fact]
    public async Task Fourth_Entity_Lead_Enabled_By_Config_Only()
    {
        var (repo, tools, policy) = CreateStack();
        Assert.True(policy.IsApproved("lead"));

        var meta = await new MockCrmMetadataService(new CrmMetadataRegistry(), policy)
            .GetEntityAsync("lead");
        // Re-seed through repo path:
        var result = await repo.QueryAsync(new CrmQueryPlan
        {
            Entity = "lead",
            Operation = CrmQueryOperation.List,
            Limit = 5
        });

        Assert.Equal("lead", result.Entity);
        Assert.Contains("leads?fetchXml=", result.RelativeRequestUri);
        // Mock has no lead seed rows — empty is OK; engine + URI prove config-only enablement.
        Assert.True(result.Items.Count == 0 || result.Items.Count <= 5);

        var json = await tools.query_crm(entity: "lead", operation: "list", limit: 3);
        Assert.Contains("query_crm", json);
        Assert.DoesNotContain("\"error\":true", json.Replace(" ", ""));
        _ = meta;
    }

    [Fact]
    public async Task Highest_Value_SalesOrders_Sort()
    {
        var (repo, _, _) = CreateStack();
        var result = await repo.QueryAsync(new CrmQueryPlan
        {
            Entity = "salesorder",
            Sort = [new CrmQuerySort { Field = "totalamount", Direction = CrmSortDirection.Desc }],
            Limit = 5
        });
        Assert.Contains("totalamount", result.SortSummary);
        Assert.Contains("desc", result.SortSummary);
        Assert.Contains("fetchXml=", result.RelativeRequestUri);
    }

    [Fact]
    public async Task Contact_Created_Before_March_2026()
    {
        var (repo, _, _) = CreateStack();
        var result = await repo.QueryAsync(new CrmQueryPlan
        {
            Entity = "contact",
            Filters =
            [
                new CrmQueryFilter
                {
                    Field = "createdon",
                    Operator = CrmFilterOperator.Lt,
                    Value = "2026-03-01T00:00:00Z"
                }
            ],
            Limit = 10
        });
        Assert.Contains(result.AppliedFilterSummary, s => s.Contains("createdon", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("fetchXml=", result.RelativeRequestUri);
    }

    [Fact]
    public async Task Account_Created_After_January_2026()
    {
        var (repo, _, _) = CreateStack();
        var result = await repo.QueryAsync(new CrmQueryPlan
        {
            Entity = "account",
            Filters =
            [
                new CrmQueryFilter
                {
                    Field = "createdon",
                    Operator = CrmFilterOperator.Ge,
                    Value = "2026-02-01T00:00:00Z"
                }
            ],
            Limit = 10
        });
        Assert.Contains(result.AppliedFilterSummary, s => s.Contains("createdon", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("fetchXml=", result.RelativeRequestUri);
    }

    [Fact]
    public void Count_Capped_Message_Is_Not_Exact()
    {
        var text = GenericCrmReplyFormatter.Format(new GenericCrmQueryResult
        {
            Entity = "salesorder",
            Operation = CrmQueryOperation.Count,
            RelativeRequestUri = "salesorders?$count=true",
            Count = 5000,
            CountMayBeCapped = true,
            CountLimitExceededAnnotation = true,
            ProviderName = "Dynamics365"
        }, "en");

        Assert.Contains("at least 5000", text);
        Assert.Contains("not an exact total", text);
    }

    [Fact]
    public void Privacy_Outbound_Still_Blocks_Crm_Rows()
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, "system"),
            new(ChatRole.User, "Show me the latest 5 sales orders")
        };
        var audit = OutboundAiPayloadAuditor.Audit(messages);
        Assert.True(audit.SafeForLiveCrm);

        var dirty = OutboundAiPayloadAuditor.Audit(
        [
            new ChatMessage(ChatRole.Assistant, "ORD-1001 customer data")
        ]);
        Assert.False(dirty.SafeForLiveCrm);
    }
}
