using CrmAiPoc.Api.Ai;
using CrmAiPoc.Api.Configuration;
using CrmAiPoc.Api.Crm.Generic;
using CrmAiPoc.Api.Crm.Metadata;
using CrmAiPoc.Api.Crm.Mock;
using CrmAiPoc.Api.Crm.Validation;
using CrmAiPoc.Api.Security;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CrmAiPoc.Tests;

public class Phase5GenericCrmEngineTests
{
    private static (EntityApprovalPolicy policy, CrmMetadataRegistry registry, ICrmMetadataService metadata, IGenericCrmRepository repo, CrmAiTools tools)
        CreateStack()
    {
        var options = Options.Create(new CrmOptions
        {
            AllowSendLiveDataToAi = false,
            ApprovedEntities = ["account", "contact", "salesorder"],
            BusinessTimeZoneId = "Africa/Cairo"
        });
        var policy = new EntityApprovalPolicy(options);
        var registry = new CrmMetadataRegistry();
        var metadata = new MockCrmMetadataService(registry, policy);
        var builder = new GenericCrmQueryBuilder(options);
        var auth = new CrmAuthorizationService(options, NullLogger<CrmAuthorizationService>.Instance, policy);
        var repo = new MockGenericCrmRepository(metadata, policy, builder, auth);
        var tools = new CrmAiTools(
            new MockCrmDataProvider(NullLogger<MockCrmDataProvider>.Instance, options),
            auth,
            new CrmArgumentValidator(options),
            options,
            NullLogger<CrmAiTools>.Instance,
            repo,
            metadata);
        return (policy, registry, metadata, repo, tools);
    }

    [Fact]
    public void ApprovalPolicy_ApprovesConfiguredEntities_Only()
    {
        var (policy, _, _, _, _) = CreateStack();
        Assert.True(policy.IsApproved("salesorder"));
        Assert.True(policy.IsApproved("account"));
        Assert.False(policy.IsApproved("opportunity"));
        Assert.True(policy.TryResolveEntityAlias("أوامر البيع", out var logical));
        Assert.Equal("salesorder", logical);
    }

    [Fact]
    public async Task Metadata_LoadsApprovedSeeds()
    {
        var (_, _, metadata, _, _) = CreateStack();
        var approved = await metadata.GetApprovedEntitiesAsync();
        Assert.Equal(3, approved.Count);
        Assert.Contains(approved, e => e.LogicalName == "account");
        Assert.Contains("salesorder", metadata.GetAiCatalogSummary());
    }

    [Fact]
    public void AttributeKindMapper_MapsDynamicsTypes()
    {
        Assert.Equal(CrmAttributeKind.Money, AttributeKindMapper.FromDynamicsType("Money"));
        Assert.Equal(CrmAttributeKind.Lookup, AttributeKindMapper.FromDynamicsType("Lookup"));
        Assert.Contains(CrmFilterOperator.Contains, AttributeKindMapper.DefaultOperators(CrmAttributeKind.String));
        Assert.Equal("_customerid_value", AttributeKindMapper.ToODataPropertyName("customerid", CrmAttributeKind.Customer));
    }

    [Fact]
    public async Task GenericBuilder_SalesOrderAmountFilter()
    {
        var (policy, _, metadata, _, _) = CreateStack();
        var entity = await metadata.GetEntityAsync("salesorder");
        Assert.NotNull(entity);
        var built = new GenericCrmQueryBuilder(Options.Create(new CrmOptions())).Build(
            new CrmQueryPlan
            {
                Entity = "salesorder",
                Filters =
                [
                    new CrmQueryFilter
                    {
                        Field = "totalamount",
                        Operator = CrmFilterOperator.Gt,
                        Value = "10000"
                    }
                ],
                Limit = 5
            },
            entity!);

        Assert.True(built.Success);
        Assert.Contains("salesorders?", built.RelativeRequestUri);
        Assert.Contains("totalamount gt 10000", built.RelativeRequestUri);
        Assert.DoesNotContain("opportunity", built.RelativeRequestUri ?? "");
        _ = policy;
    }

    [Fact]
    public void GenericBuilder_RejectsUnapprovedEntityMetadata()
    {
        var entity = new CrmEntityMetadata
        {
            LogicalName = "opportunity",
            EntitySetName = "opportunities",
            PrimaryIdAttribute = "opportunityid",
            PrimaryNameAttribute = "name",
            IsApprovedForAi = false
        };

        var built = new GenericCrmQueryBuilder().Build(new CrmQueryPlan { Entity = "opportunity" }, entity);
        Assert.False(built.Success);
    }

    [Fact]
    public void GenericBuilder_EscapesInjection()
    {
        var options = Options.Create(new CrmOptions());
        var policy = new EntityApprovalPolicy(options);
        var entity = StaticSeedCrmMetadata.CreateApprovedSeeds(policy).First(e => e.LogicalName == "account");
        var built = new GenericCrmQueryBuilder().Build(
            new CrmQueryPlan
            {
                Entity = "account",
                Filters =
                [
                    new CrmQueryFilter
                    {
                        Field = "name",
                        Operator = CrmFilterOperator.Contains,
                        Value = "A' or 1 eq 1"
                    }
                ]
            },
            entity);

        Assert.True(built.Success);
        Assert.Contains("contains(name,'A'' or 1 eq 1')", built.RelativeRequestUri);
    }

    [Fact]
    public async Task GenericRepository_QueriesThreeEntities_SameEngine()
    {
        var (_, _, _, repo, _) = CreateStack();

        var accounts = await repo.QueryAsync(new CrmQueryPlan
        {
            Entity = "account",
            Filters =
            [
                new CrmQueryFilter { Field = "name", Operator = CrmFilterOperator.Contains, Value = "Ahmed" }
            ]
        });
        Assert.Equal("account", accounts.Entity);
        Assert.True(accounts.Items.Count >= 1);

        var contacts = await repo.QueryAsync(new CrmQueryPlan { Entity = "contact", Limit = 5 });
        Assert.Equal("contact", contacts.Entity);
        Assert.True(contacts.Items.Count >= 1);

        var orders = await repo.QueryAsync(new CrmQueryPlan
        {
            Entity = "salesorder",
            Operation = CrmQueryOperation.Count
        });
        Assert.Equal("salesorder", orders.Entity);
        Assert.True(orders.Count >= 1);
    }

    [Fact]
    public async Task Relationship_ContactsForAccount()
    {
        var (_, _, _, repo, _) = CreateStack();
        var result = await repo.QueryAsync(new CrmQueryPlan
        {
            Entity = "contact",
            RelatedFromEntity = "account",
            RelatedFromId = MockCrmSeedData.AhmedTradingId.ToString()
        });

        Assert.True(result.Items.Count >= 1);
        Assert.Contains(result.AppliedFilterSummary, s => s.Contains("_parentcustomerid_value", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task UnsupportedRelationship_FailsExplicitly()
    {
        var (_, _, _, repo, _) = CreateStack();
        var result = await repo.QueryAsync(new CrmQueryPlan
        {
            Entity = "account",
            RelatedFromEntity = "salesorder",
            RelatedFromId = Guid.NewGuid().ToString()
        });

        Assert.NotNull(result.Message);
        Assert.Contains("relationship", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task QueryCrmTool_Works()
    {
        var (_, _, _, _, tools) = CreateStack();
        var json = await tools.query_crm(
            entity: "salesorder",
            operation: "list",
            filtersJson: """[{"field":"statecode","operator":"eq","value":"Active"}]""",
            limit: 5);
        Assert.DoesNotContain("\"error\":true", json.Replace(" ", ""));
        Assert.Contains("query_crm", json);
    }

    [Fact]
    public void OptionSet_ResolvesActive()
    {
        var options = Options.Create(new CrmOptions());
        var policy = new EntityApprovalPolicy(options);
        var so = StaticSeedCrmMetadata.CreateApprovedSeeds(policy).First(e => e.LogicalName == "salesorder");
        Assert.True(so.TryGetAttribute("statecode", out var state));
        Assert.Equal(0, state.OptionSetByLabel["Active"]);
    }

    [Fact]
    public void GenericFormatter_DoesNotInventData()
    {
        var text = GenericCrmReplyFormatter.Format(new GenericCrmQueryResult
        {
            Entity = "account",
            Operation = CrmQueryOperation.List,
            RelativeRequestUri = "accounts",
            Items =
            [
                new Dictionary<string, object?> { ["name"] = "Demo Co", ["accountid"] = Guid.NewGuid() }
            ],
            PrimaryNameAttribute = "name",
            ProviderName = "Mock"
        }, "en");

        Assert.Contains("Demo Co", text);
        Assert.Contains("were not sent to the AI model", text);
        Assert.DoesNotContain("invented", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReadOnly_RejectsWriteShapedOperationName()
    {
        var options = Options.Create(new CrmOptions());
        var auth = new CrmAuthorizationService(options, NullLogger<CrmAuthorizationService>.Instance, new EntityApprovalPolicy(options));
        var decision = auth.AuthorizeRead("account", "delete_account");
        Assert.False(decision.Allowed);
    }
}
