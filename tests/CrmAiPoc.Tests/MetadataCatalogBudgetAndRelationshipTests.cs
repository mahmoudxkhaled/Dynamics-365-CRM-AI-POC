using CrmAiPoc.Api.Configuration;
using CrmAiPoc.Api.Crm.Generic;
using CrmAiPoc.Api.Crm.Metadata;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CrmAiPoc.Tests;

public sealed class MetadataCatalogBudgetAndRelationshipTests
{
    private static CrmAiMetadataCatalogService CreateCatalog(
        IEnumerable<string> approved,
        int maxChars,
        int maxAttrs = 16,
        int maxDetail = 12)
    {
        var options = new CrmOptions
        {
            AllowSendApprovedMetadataCatalogToAi = true,
            AiMetadataCatalogMaxChars = maxChars,
            AiMetadataCatalogMaxAttributesPerEntity = maxAttrs,
            AiMetadataCatalogMaxEntities = maxDetail,
            ApprovedEntities = approved.ToList(),
            EntityAliases = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["account"] = ["accounts", "customers", "customer account"],
                ["contact"] = ["contacts"],
                ["salesorder"] = ["sales order", "sales orders", "orders"],
                ["lead"] = ["leads"]
            },
            RelationshipLabels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["te_account_salesorder_customeraccountid"] = "Customer Account",
                ["order_customer_accounts"] = "Potential Customer (Account)"
            }
        };
        var opts = Options.Create(options);
        var policy = new EntityApprovalPolicy(opts);
        return new CrmAiMetadataCatalogService(
            new CrmMetadataRegistry(),
            policy,
            new CrmRelationshipLabeler(opts),
            opts,
            NullLogger<CrmAiMetadataCatalogService>.Instance);
    }

    [Fact]
    public async Task Catalog_Succeeds_With_Compact_Index_And_Detail()
    {
        var catalog = CreateCatalog(["account", "contact", "salesorder", "lead"], maxChars: 12000);
        var result = await catalog.BuildApprovedCatalogAsync(
            "Show the 10 highest-value Sales Orders created this year, including their customer account names.");

        Assert.True(result.Success, result.Error);
        Assert.Equal(4, result.IncludedEntityCount);
        Assert.Contains(result.Catalog.Index, e => e.LogicalName == "salesorder");
        Assert.Contains(result.Catalog.Detail, e => e.LogicalName == "salesorder");
        Assert.True(result.IncludedRelationshipCount > 0);
        Assert.Contains("\"index\"", result.FormattedBlock);
        Assert.Contains("\"detail\"", result.FormattedBlock);
        Assert.True(result.FinalCharacterCount <= result.ConfiguredBudget);
    }

    [Fact]
    public async Task Catalog_Scales_To_Many_Approved_Entities()
    {
        var approved = Enumerable.Range(1, 24).Select(i => $"customentity{i:00}").Append("salesorder").Append("account").ToList();
        var catalog = CreateCatalog(approved, maxChars: 12000, maxDetail: 8, maxAttrs: 8);
        var result = await catalog.BuildApprovedCatalogAsync("latest sales orders with customer account");

        Assert.True(result.Success, result.Error);
        Assert.Equal(approved.Count, result.IncludedEntityCount);
        Assert.True(result.Catalog.Detail.Count <= 8);
        Assert.True(result.TruncationApplied || result.Catalog.Detail.Count < approved.Count);
    }

    [Fact]
    public async Task Catalog_Budget_Overflow_Fails_Without_Empty_Success()
    {
        var catalog = CreateCatalog(["account", "contact", "salesorder", "lead"], maxChars: 200, maxAttrs: 20, maxDetail: 8);
        var result = await catalog.BuildApprovedCatalogAsync("sales orders with customer account");

        Assert.True(result.Enabled);
        Assert.False(result.Success);
        Assert.Contains("budget", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(string.Empty, result.FormattedBlock);
        Assert.True(result.TruncationApplied);
    }

    [Fact]
    public void Invented_SchemaName_Is_Resolved_Via_Business_Purpose()
    {
        var options = Options.Create(new CrmOptions
        {
            ApprovedEntities = ["account", "salesorder"],
            RelationshipLabels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["te_account_salesorder_customeraccountid"] = "Customer Account",
                ["order_customer_accounts"] = "Potential Customer (Account)"
            }
        });
        var policy = new EntityApprovalPolicy(options);
        var seeds = StaticSeedCrmMetadata.CreateApprovedSeeds(policy);
        var plan = new CrmQueryPlan
        {
            Entity = "salesorder",
            Operation = CrmQueryOperation.List,
            Limit = 10,
            Links =
            [
                new CrmLinkSpec
                {
                    Entity = "account",
                    Relationship = "salesorder_customerid", // invented — must not pass through
                    RelationshipPurpose = "Customer Account",
                    Select = ["name"]
                }
            ]
        };

        var result = CrmRelationshipDiscovery.ResolveProposedLinks(
            plan,
            "highest-value Sales Orders including their customer account names",
            seeds,
            policy,
            new CrmRelationshipLabeler(options));

        Assert.Null(result.Pending);
        Assert.True(string.IsNullOrWhiteSpace(result.ClarificationMessage), result.ClarificationMessage);
        Assert.Single(result.Plan.Links);
        Assert.Equal(
            "te_account_salesorder_customeraccountid",
            result.Plan.Links[0].Relationship,
            StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void Purpose_Only_Link_Resolves_Customer_Account()
    {
        var options = Options.Create(new CrmOptions
        {
            ApprovedEntities = ["account", "salesorder"],
            RelationshipLabels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["te_account_salesorder_customeraccountid"] = "Customer Account",
                ["order_customer_accounts"] = "Potential Customer (Account)"
            }
        });
        var policy = new EntityApprovalPolicy(options);
        var seeds = StaticSeedCrmMetadata.CreateApprovedSeeds(policy);
        var plan = new CrmQueryPlan
        {
            Entity = "salesorder",
            Links =
            [
                new CrmLinkSpec
                {
                    Entity = "account",
                    RelationshipPurpose = "Customer Account",
                    Select = ["name"]
                }
            ]
        };

        var result = CrmRelationshipDiscovery.ResolveProposedLinks(
            plan,
            "customer account names",
            seeds,
            policy,
            new CrmRelationshipLabeler(options));

        Assert.Equal(
            "te_account_salesorder_customeraccountid",
            result.Plan.Links[0].Relationship,
            StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void LinksJson_Parses_Purpose_Field()
    {
        var parsed = CrmQueryPlanParser.ParseFromToolArguments(new Dictionary<string, object?>
        {
            ["entity"] = "salesorder",
            ["operation"] = "list",
            ["limit"] = 10,
            ["linksJson"] =
                """[{"entity":"account","purpose":"Customer Account","select":["name"],"joinType":"outer"}]"""
        });

        Assert.True(parsed.Success, parsed.Error);
        Assert.Equal("Customer Account", parsed.Plan!.Links[0].RelationshipPurpose);
        Assert.Null(parsed.Plan.Links[0].Relationship);
    }
}
