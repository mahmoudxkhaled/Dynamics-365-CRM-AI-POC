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
    public void Parser_Accepts_Valid_Clarification_Json()
    {
        var parsed = ClarificationResponseParser.TryParse(
            """
            {
              "type": "clarification",
              "message": "Which of these queries would you like to run?",
              "suggestions": [
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
            }
            """);

        Assert.True(parsed.IsClarificationAttempt);
        Assert.True(parsed.Success);
        Assert.Equal(2, parsed.Suggestions.Count);
        Assert.Contains("Created On", parsed.Suggestions[0].Question);
    }

    [Fact]
    public void Parser_Rejects_Partial_Field_Name_As_Question()
    {
        var parsed = ClarificationResponseParser.TryParse(
            """
            {
              "type": "clarification",
              "message": "Pick one",
              "suggestions": [
                { "label": "A", "question": "Created On", "entityLogicalName": "salesorder", "attributeLogicalNames": ["createdon"] },
                { "label": "B", "question": "Show the latest 5 Sales Orders by Modified On.", "entityLogicalName": "salesorder", "attributeLogicalNames": ["modifiedon"] }
              ]
            }
            """);

        Assert.True(parsed.IsClarificationAttempt);
        Assert.False(parsed.Success);
        Assert.Contains("complete standalone", parsed.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parser_Rejects_Wrong_Suggestion_Count()
    {
        var parsed = ClarificationResponseParser.TryParse(
            """
            {
              "type": "clarification",
              "message": "Pick one",
              "suggestions": [
                {
                  "label": "Only one",
                  "question": "Show the latest 5 Sales Orders by Created On.",
                  "entityLogicalName": "salesorder",
                  "attributeLogicalNames": ["createdon"]
                }
              ]
            }
            """);

        Assert.True(parsed.IsClarificationAttempt);
        Assert.False(parsed.Success);
    }

    [Fact]
    public void Validator_Accepts_Catalog_Backed_Suggestions()
    {
        var (policy, catalog) = CreateCatalog();
        var parsed = ClarificationResponseParser.TryParse(
            """
            {
              "type": "clarification",
              "message": "Which of these queries would you like to run?",
              "suggestions": [
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
            }
            """);

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
        var parsed = ClarificationResponseParser.TryParse(
            """
            {
              "type": "clarification",
              "message": "Which of these queries would you like to run?",
              "suggestions": [
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
            }
            """);

        var validated = ClarificationSuggestionValidator.Validate(parsed, catalog, policy);
        Assert.False(validated.Success);
        Assert.Contains("not_a_real_field", validated.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(validated.Suggestions);
    }

    [Fact]
    public void Validator_Rejects_Entity_Not_In_Ai_Catalog()
    {
        var (policy, catalog) = CreateCatalog();
        var parsed = ClarificationResponseParser.TryParse(
            """
            {
              "type": "clarification",
              "message": "Which of these queries would you like to run?",
              "suggestions": [
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
            }
            """);

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
    public void Plain_Text_Is_Not_Treated_As_Clarification()
    {
        var parsed = ClarificationResponseParser.TryParse(
            "I can only answer read-only CRM questions about approved entities.");
        Assert.False(parsed.IsClarificationAttempt);
        Assert.False(parsed.Success);
    }

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
                new()
                {
                    LogicalName = "salesorderid",
                    ODataPropertyName = "salesorderid",
                    Kind = CrmAttributeKind.Uniqueidentifier,
                    IsValidForRead = true
                },
                new()
                {
                    LogicalName = "name",
                    ODataPropertyName = "name",
                    DisplayName = "Name",
                    Kind = CrmAttributeKind.String,
                    IsValidForRead = true
                },
                new()
                {
                    LogicalName = "createdon",
                    ODataPropertyName = "createdon",
                    DisplayName = "Created On",
                    Kind = CrmAttributeKind.DateTime,
                    IsValidForRead = true,
                    IsSortable = true
                },
                new()
                {
                    LogicalName = "modifiedon",
                    ODataPropertyName = "modifiedon",
                    DisplayName = "Modified On",
                    Kind = CrmAttributeKind.DateTime,
                    IsValidForRead = true,
                    IsSortable = true
                },
                new()
                {
                    LogicalName = "totalamount",
                    ODataPropertyName = "totalamount",
                    DisplayName = "Total Amount",
                    Kind = CrmAttributeKind.Money,
                    IsValidForRead = true,
                    IsSortable = true
                }
            ]
        });

        var service = new CrmAiMetadataCatalogService(
            registry,
            policy,
            new CrmRelationshipLabeler(options),
            options,
            NullLogger<CrmAiMetadataCatalogService>.Instance);

        var catalog = service.BuildApprovedCatalogAsync().GetAwaiter().GetResult();
        Assert.True(catalog.Success, catalog.Error);
        return (policy, catalog);
    }
}
