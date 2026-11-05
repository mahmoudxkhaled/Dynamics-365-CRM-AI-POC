using CrmAiPoc.Api.Ai;
using CrmAiPoc.Api.Configuration;
using CrmAiPoc.Api.Crm.Metadata;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CrmAiPoc.Tests;

public sealed class TwoStageAiPlanningTests
{
    private static CrmAiMetadataCatalogService CreateCatalog(IEnumerable<string> approved)
    {
        var options = new CrmOptions
        {
            AllowSendApprovedMetadataCatalogToAi = true,
            ApprovedEntities = approved.ToList()
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
    public async Task Stage1_Index_Contains_Only_LogicalName_And_DisplayName_No_Attributes()
    {
        var catalog = CreateCatalog(["account", "opportunity", "invoice", "product", "campaign", "salesorder"]);
        var index = await catalog.BuildApprovedEntitySelectionIndexAsync();

        Assert.True(index.Success, index.Error);
        Assert.Equal(6, index.ApprovedEntityCount);
        Assert.Contains(CrmAiMetadataCatalogService.EntityIndexBeginMarker, index.FormattedBlock);
        Assert.Contains("poc-stage1-entity-index", index.FormattedBlock);
        Assert.Contains("\"logicalName\":\"opportunity\"", index.FormattedBlock);
        Assert.DoesNotContain("estimatedvalue", index.FormattedBlock);
        Assert.DoesNotContain("\"attributes\"", index.FormattedBlock);
        Assert.DoesNotContain("\"relationships\"", index.FormattedBlock);
        Assert.All(index.Entities, e => Assert.False(string.IsNullOrWhiteSpace(e.LogicalName)));
    }

    [Fact]
    public async Task Stage2_Catalog_Includes_Only_Selected_Entities_With_Full_Attributes()
    {
        var options = Options.Create(new CrmOptions
        {
            AllowSendApprovedMetadataCatalogToAi = true,
            ApprovedEntities = ["opportunity", "invoice", "product", "account"]
        });
        var policy = new EntityApprovalPolicy(options);
        var registry = new CrmMetadataRegistry();
        registry.Upsert(new CrmEntityMetadata
        {
            LogicalName = "opportunity",
            EntitySetName = "opportunities",
            DisplayName = "Opportunity",
            PrimaryIdAttribute = "opportunityid",
            PrimaryNameAttribute = "name",
            IsApprovedForAi = true,
            Attributes =
            [
                new CrmAttributeMetadata
                {
                    LogicalName = "estimatedvalue",
                    ODataPropertyName = "estimatedvalue",
                    DisplayName = "Est. Revenue",
                    Kind = CrmAttributeKind.Money,
                    IsValidForRead = true,
                    IsFilterable = true,
                    IsSortable = true
                },
                new CrmAttributeMetadata
                {
                    LogicalName = "name",
                    ODataPropertyName = "name",
                    DisplayName = "Topic",
                    Kind = CrmAttributeKind.String,
                    IsValidForRead = true
                }
            ]
        });
        registry.Upsert(new CrmEntityMetadata
        {
            LogicalName = "invoice",
            EntitySetName = "invoices",
            DisplayName = "Invoice",
            PrimaryIdAttribute = "invoiceid",
            PrimaryNameAttribute = "name",
            IsApprovedForAi = true,
            Attributes =
            [
                new CrmAttributeMetadata
                {
                    LogicalName = "totalamount",
                    ODataPropertyName = "totalamount",
                    Kind = CrmAttributeKind.Money,
                    IsValidForRead = true
                }
            ]
        });

        var svc = new CrmAiMetadataCatalogService(
            registry,
            policy,
            new CrmRelationshipLabeler(options),
            options,
            NullLogger<CrmAiMetadataCatalogService>.Instance);

        var stage2 = await svc.BuildApprovedCatalogForSelectedEntitiesAsync(["opportunity"]);
        Assert.True(stage2.Success, stage2.Error);
        Assert.Equal(1, stage2.IncludedEntityCount);
        Assert.Contains("estimatedvalue", stage2.FormattedBlock);
        Assert.DoesNotContain("\"logicalName\":\"invoice\"", stage2.FormattedBlock);
        Assert.False(stage2.TruncationApplied);
    }

    [Fact]
    public async Task Stage2_Sends_Only_Approved_Relationships_Keeps_All_Readable_Attributes()
    {
        var options = Options.Create(new CrmOptions
        {
            AllowSendApprovedMetadataCatalogToAi = true,
            ApprovedEntities = ["product", "account"]
        });
        var policy = new EntityApprovalPolicy(options);
        var registry = new CrmMetadataRegistry();

        var attrs = Enumerable.Range(1, 118)
            .Select(i => new CrmAttributeMetadata
            {
                LogicalName = i == 1 ? "price" : $"attr{i:000}",
                ODataPropertyName = i == 1 ? "price" : $"attr{i:000}",
                Kind = i == 1 ? CrmAttributeKind.Money : CrmAttributeKind.String,
                IsValidForRead = true
            })
            .ToArray();

        var rels = new List<CrmRelationshipMetadata>();
        for (var i = 0; i < 71; i++)
        {
            rels.Add(new CrmRelationshipMetadata
            {
                SchemaName = $"unapproved_rel_{i}",
                Kind = CrmRelationshipKind.ManyToOne,
                ReferencingEntity = "product",
                ReferencedEntity = "systemuser",
                ReferencingAttribute = "ownerid",
                ReferencedAttribute = "systemuserid",
                IsApproved = false
            });
        }

        for (var i = 0; i < 5; i++)
        {
            rels.Add(new CrmRelationshipMetadata
            {
                SchemaName = $"approved_rel_{i}",
                Kind = CrmRelationshipKind.ManyToOne,
                ReferencingEntity = "product",
                ReferencedEntity = "account",
                ReferencingAttribute = "accountid",
                ReferencedAttribute = "accountid",
                IsApproved = true
            });
        }

        registry.Upsert(new CrmEntityMetadata
        {
            LogicalName = "product",
            EntitySetName = "products",
            DisplayName = "Product",
            PrimaryIdAttribute = "productid",
            PrimaryNameAttribute = "name",
            IsApprovedForAi = true,
            Attributes = attrs,
            Relationships = rels
        });

        var svc = new CrmAiMetadataCatalogService(
            registry,
            policy,
            new CrmRelationshipLabeler(options),
            options,
            NullLogger<CrmAiMetadataCatalogService>.Instance);

        var stage2 = await svc.BuildApprovedCatalogForSelectedEntitiesAsync(["product"]);
        Assert.True(stage2.Success, stage2.Error);
        var product = Assert.Single(stage2.Catalog.Detail);
        Assert.Equal(118, product.Attributes.Count);
        Assert.Equal(5, product.Relationships.Count);
        Assert.All(product.Relationships, r => Assert.True(r.IsApproved));
        Assert.DoesNotContain("unapproved_rel_", stage2.FormattedBlock, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("approved_rel_0", stage2.FormattedBlock);
        Assert.Contains("\"logicalName\":\"price\"", stage2.FormattedBlock);
        Assert.Equal(5, stage2.IncludedRelationshipCount);
    }

    [Theory]
    [InlineData("""{"selectedEntities":["opportunity"]}""", new[] { "opportunity" })]
    [InlineData("""{"selectedEntities":["salesorder","account"]}""", new[] { "salesorder", "account" })]
    [InlineData("```json\n{\"selectedEntities\":[\"campaign\"]}\n```", new[] { "campaign" })]
    public void Stage1_Parser_Accepts_Valid_Json(string raw, string[] expected)
    {
        var parsed = AiEntitySelectionParser.Parse(raw);
        Assert.True(parsed.Success, parsed.Error);
        Assert.Equal(expected, parsed.SelectedEntities);
    }

    [Fact]
    public void Stage1_Parser_Rejects_Empty_Selection()
    {
        var parsed = AiEntitySelectionParser.Parse("""{"selectedEntities":[]}""");
        Assert.False(parsed.Success);
    }

    [Fact]
    public void Stage1_History_Does_Not_Include_Query_Planning_Catalog()
    {
        var index = new CrmAiEntitySelectionIndexBuildResult
        {
            Enabled = true,
            Success = true,
            FormattedBlock =
                $"{CrmAiMetadataCatalogService.EntityIndexBeginMarker}\n{{\"entities\":[]}}\n{CrmAiMetadataCatalogService.EntityIndexEndMarker}",
            ApprovedEntityCount = 0
        };
        var history = ChatService.BuildStage1History("Show opportunities", index);
        Assert.Equal(3, history.Count);
        Assert.DoesNotContain(history, m =>
            m.Text.Contains(CrmAiMetadataCatalogService.BeginMarker, StringComparison.Ordinal));
    }
}
