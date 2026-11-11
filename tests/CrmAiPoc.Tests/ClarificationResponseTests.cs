using CrmAiPoc.Api.Ai;
using CrmAiPoc.Api.Configuration;
using CrmAiPoc.Api.Crm.Metadata;
using CrmAiPoc.Api.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CrmAiPoc.Tests;

public sealed class ClarificationResponseTests
{
    [Fact]
    public void ToolArgs_Parser_Accepts_Valid_Clarification()
    {
        var parsed = ClarifyCrmArgumentParser.ParseFromToolArguments(ValidArgs());

        Assert.True(parsed.Success);
        Assert.Equal(2, parsed.Suggestions.Count);
        Assert.Contains("Created On", parsed.Suggestions[0].Question);
    }

    [Fact]
    public void ToolArgs_Parser_Rejects_Partial_Field_Name_As_Question()
    {
        var parsed = ClarifyCrmArgumentParser.ParseFromToolArguments(new Dictionary<string, object?>
        {
            ["message"] = "Pick one",
            ["suggestions"] = """
                [
                  { "label": "A", "question": "Created On", "entityLogicalName": "salesorder", "attributeLogicalNames": ["createdon"] },
                  { "label": "B", "question": "Show the latest 5 Sales Orders by Modified On.", "entityLogicalName": "salesorder", "attributeLogicalNames": ["modifiedon"] }
                ]
                """
        });

        Assert.False(parsed.Success);
        Assert.Contains("complete standalone", parsed.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ToolArgs_Parser_Rejects_Wrong_Suggestion_Count()
    {
        var parsed = ClarifyCrmArgumentParser.ParseFromToolArguments(new Dictionary<string, object?>
        {
            ["message"] = "Pick one",
            ["suggestions"] = """
                [
                  {
                    "label": "Only one",
                    "question": "Show the latest 5 Sales Orders by Created On.",
                    "entityLogicalName": "salesorder",
                    "attributeLogicalNames": ["createdon"]
                  }
                ]
                """
        });

        Assert.False(parsed.Success);
        Assert.Contains("2–4", parsed.Error);
    }

    [Fact]
    public void Validator_Accepts_Catalog_Backed_Suggestions()
    {
        var (policy, catalog) = CreateCatalog();
        var parsed = ClarifyCrmArgumentParser.ParseFromToolArguments(ValidArgs());

        var validated = ClarificationSuggestionValidator.Validate(parsed, catalog, policy);
        Assert.True(validated.Success, validated.Error);
        Assert.Equal(2, validated.Suggestions.Count);
        Assert.All(validated.Suggestions, s =>
        {
            Assert.False(string.IsNullOrWhiteSpace(s.Label));
            Assert.Contains(' ', s.Question);
        });
    }

    [Fact]
    public void Validator_Rejects_Unauthorized_Or_Missing_Attribute()
    {
        var (policy, catalog) = CreateCatalog();
        var parsed = ClarifyCrmArgumentParser.ParseFromToolArguments(new Dictionary<string, object?>
        {
            ["message"] = "Which of these queries would you like to run?",
            ["suggestions"] = """
                [
                  {
                    "label": "By Fake Field",
                    "question": "Show the latest 5 Sales Orders by Fake Field.",
                    "entityLogicalName": "salesorder",
                    "attributeLogicalNames": ["not_a_real_field"]
                  },
                  {
                    "label": "By Created On",
                    "question": "Show the latest 5 Sales Orders by Created On.",
                    "entityLogicalName": "salesorder",
                    "attributeLogicalNames": ["createdon"]
                  }
                ]
                """
        });

        var validated = ClarificationSuggestionValidator.Validate(parsed, catalog, policy);
        Assert.False(validated.Success);
        Assert.Contains("not_a_real_field", validated.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(validated.Suggestions);
    }

    [Fact]
    public void Validator_Rejects_Entity_Not_In_Ai_Catalog()
    {
        var (policy, catalog) = CreateCatalog();
        var parsed = ClarifyCrmArgumentParser.ParseFromToolArguments(new Dictionary<string, object?>
        {
            ["message"] = "Which of these queries would you like to run?",
            ["suggestions"] = """
                [
                  {
                    "label": "Opportunities",
                    "question": "Show the latest 5 Opportunities by Created On.",
                    "entityLogicalName": "opportunity",
                    "attributeLogicalNames": ["createdon"]
                  },
                  {
                    "label": "By Created On",
                    "question": "Show the latest 5 Sales Orders by Created On.",
                    "entityLogicalName": "salesorder",
                    "attributeLogicalNames": ["createdon"]
                  }
                ]
                """
        });

        var validated = ClarificationSuggestionValidator.Validate(parsed, catalog, policy);
        Assert.False(validated.Success);
        Assert.Contains("opportunity", validated.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ChatResponseDto_Answer_Remains_Compatible_Without_Suggestions()
    {
        var dto = new ChatResponseDto
        {
            ConversationId = "c1",
            Reply = "Retrieved 5 salesorder record(s).",
            ResponseType = "answer",
            PrivacySafeToolExecution = true,
            OutboundAiContainedCrmRecords = false,
            SelectedTool = "query_crm"
        };

        Assert.Equal("answer", dto.ResponseType);
        Assert.Null(dto.Suggestions);
        Assert.Equal("query_crm", dto.SelectedTool);
    }

    [Fact]
    public void Old_Text_Clarification_Parser_Type_Is_Removed()
    {
        var type = Type.GetType("CrmAiPoc.Api.Ai.ClarificationResponseParser, CrmAiPoc.Api");
        Assert.Null(type);
    }

    private static Dictionary<string, object?> ValidArgs() =>
        new()
        {
            ["message"] = "Which of these queries would you like to run?",
            ["suggestions"] = """
                [
                  {
                    "label": "By Created On",
                    "question": "Show the latest 5 Sales Orders by Created On.",
                    "entityLogicalName": "salesorder",
                    "attributeLogicalNames": ["createdon"]
                  },
                  {
                    "label": "By Modified On",
                    "question": "Show the latest 5 Sales Orders by Modified On.",
                    "entityLogicalName": "salesorder",
                    "attributeLogicalNames": ["modifiedon"]
                  }
                ]
                """
        };

    private static (EntityApprovalPolicy Policy, CrmAiMetadataCatalogBuildResult Catalog) CreateCatalog()
    {
        var options = Options.Create(new CrmOptions
        {
            AllowSendApprovedMetadataCatalogToAi = true,
            ApprovedEntities = ["salesorder", "account"]
        });
        var policy = new EntityApprovalPolicy(options);
        var registry = new CrmMetadataRegistry();
        registry.Upsert(new CrmEntityMetadata
        {
            LogicalName = "salesorder",
            EntitySetName = "salesorders",
            DisplayName = "Sales Order",
            PrimaryIdAttribute = "salesorderid",
            PrimaryNameAttribute = "name",
            IsApprovedForAi = true,
            Attributes =
            [
                new CrmAttributeMetadata
                {
                    LogicalName = "salesorderid",
                    ODataPropertyName = "salesorderid",
                    Kind = CrmAttributeKind.Uniqueidentifier,
                    IsValidForRead = true
                },
                new CrmAttributeMetadata
                {
                    LogicalName = "name",
                    ODataPropertyName = "name",
                    Kind = CrmAttributeKind.String,
                    IsValidForRead = true
                },
                new CrmAttributeMetadata
                {
                    LogicalName = "createdon",
                    ODataPropertyName = "createdon",
                    DisplayName = "Created On",
                    Kind = CrmAttributeKind.DateTime,
                    IsValidForRead = true,
                    IsSortable = true
                },
                new CrmAttributeMetadata
                {
                    LogicalName = "modifiedon",
                    ODataPropertyName = "modifiedon",
                    DisplayName = "Modified On",
                    Kind = CrmAttributeKind.DateTime,
                    IsValidForRead = true,
                    IsSortable = true
                }
            ]
        });

        var catalogService = new CrmAiMetadataCatalogService(
            registry,
            policy,
            new CrmRelationshipLabeler(options),
            options,
            NullLogger<CrmAiMetadataCatalogService>.Instance);

        var retrieval = new CrmAiPoc.Api.Crm.Metadata.Semantic.SemanticMetadataRetrievalResult
        {
            Question = "latest sales orders",
            ContentHash = "test",
            EntityHits =
            [
                new CrmAiPoc.Api.Crm.Metadata.Semantic.SemanticHit
                {
                    Similarity = 0.9f,
                    Document = new CrmAiPoc.Api.Crm.Metadata.Semantic.MetadataSearchDocument
                    {
                        Id = "entity:salesorder",
                        Kind = CrmAiPoc.Api.Crm.Metadata.Semantic.MetadataSearchDocumentKind.Entity,
                        EntityLogicalName = "salesorder",
                        SearchText = "x",
                        EmbeddingInput = "x"
                    }
                }
            ],
            AttributeHits =
            [
                Hit("salesorder", "createdon"),
                Hit("salesorder", "modifiedon"),
                Hit("salesorder", "name"),
                Hit("salesorder", "salesorderid")
            ]
        };

        var built = catalogService.BuildCatalogFromSemanticRetrievalAsync(retrieval).GetAwaiter().GetResult();
        Assert.True(built.Success, built.Error);
        return (policy, built);
    }

    private static CrmAiPoc.Api.Crm.Metadata.Semantic.SemanticHit Hit(string entity, string attr) =>
        new()
        {
            Similarity = 0.8f,
            Document = new CrmAiPoc.Api.Crm.Metadata.Semantic.MetadataSearchDocument
            {
                Id = $"attribute:{entity}.{attr}",
                Kind = CrmAiPoc.Api.Crm.Metadata.Semantic.MetadataSearchDocumentKind.Attribute,
                EntityLogicalName = entity,
                AttributeLogicalName = attr,
                SearchText = "x",
                EmbeddingInput = "x"
            }
        };
}
