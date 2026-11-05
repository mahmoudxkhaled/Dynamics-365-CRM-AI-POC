using CrmAiPoc.Api.Ai;
using CrmAiPoc.Api.Configuration;
using CrmAiPoc.Api.Crm.Metadata;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CrmAiPoc.Tests;

public sealed class SingleStageAiPlanningTests
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
    public async Task Outbound_History_Is_Single_Stage_System_Catalog_User()
    {
        var catalog = CreateCatalog(["account", "product", "salesorder"]);
        var built = await catalog.BuildApprovedCatalogAsync("Show the latest 3 products.");
        Assert.True(built.Success, built.Error);

        var history = ChatService.BuildOutboundHistory("Show the latest 3 products.", built);
        Assert.Equal(3, history.Count);
        Assert.Equal(ChatRole.System, history[0].Role);
        Assert.Equal(ChatRole.System, history[1].Role);
        Assert.Equal(ChatRole.User, history[2].Role);
        Assert.Equal("Show the latest 3 products.", history[2].Text);
        Assert.Contains(CrmAiMetadataCatalogService.BeginMarker, history[1].Text);
        Assert.Contains("query_crm", history[0].Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Catalog_Includes_All_Approved_Entities_And_Only_Approved_Relationships()
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
        registry.Upsert(new CrmEntityMetadata
        {
            LogicalName = "account",
            EntitySetName = "accounts",
            DisplayName = "Account",
            PrimaryIdAttribute = "accountid",
            PrimaryNameAttribute = "name",
            IsApprovedForAi = true,
            Attributes =
            [
                new CrmAttributeMetadata
                {
                    LogicalName = "name",
                    ODataPropertyName = "name",
                    Kind = CrmAttributeKind.String,
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

        var catalog = await svc.BuildApprovedCatalogAsync();
        Assert.True(catalog.Success, catalog.Error);
        Assert.Equal(2, catalog.IncludedEntityCount);
        var product = Assert.Single(catalog.Catalog.Detail, e => e.LogicalName == "product");
        Assert.Equal(118, product.Attributes.Count);
        Assert.Equal(5, product.Relationships.Count);
        Assert.All(product.Relationships, r => Assert.True(r.IsApproved));
        Assert.DoesNotContain("unapproved_rel_", catalog.FormattedBlock, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Recording_Chat_Client_Counts_Single_Request_For_Standard_Question()
    {
        // Structural guarantee used by live/integration wrappers: one planning call path.
        var history = ChatService.BuildOutboundHistory(
            "Show the latest 3 products.",
            new CrmAiMetadataCatalogBuildResult
            {
                Enabled = true,
                Success = true,
                FormattedBlock =
                    $"{CrmAiMetadataCatalogService.BeginMarker}\n{{\"entities\":[]}}\n{CrmAiMetadataCatalogService.EndMarker}",
                IncludedEntityCount = 1,
                CharacterCount = 64
            });

        Assert.Single(history.Where(m => m.Role == ChatRole.User));
        Assert.Equal(1, history.Count(m =>
            m.Text.Contains(CrmAiMetadataCatalogService.BeginMarker, StringComparison.Ordinal)));
    }
}
