using CrmAiPoc.Api.Ai;
using CrmAiPoc.Api.Configuration;
using CrmAiPoc.Api.Crm.Metadata;
using CrmAiPoc.Api.Models;
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
    public void ToolArgs_Parser_Accepts_Clarification_Without_Metadata_References()
    {
        var parsed = ClarifyCrmArgumentParser.ParseFromToolArguments(UserFacingOnlyArgs());

        Assert.True(parsed.Success, parsed.Error);
        Assert.Equal(2, parsed.Suggestions.Count);
        Assert.All(parsed.Suggestions, s =>
        {
            Assert.Null(s.EntityLogicalName);
            Assert.Empty(s.AttributeLogicalNames);
            Assert.Empty(s.RelationshipSchemaNames);
        });
    }

    [Fact]
    public void Validator_Drops_Partial_Field_Name_As_Question()
    {
        var (policy, entities) = CreateApprovedEntities();
        var parsed = ClarifyCrmArgumentParser.ParseFromToolArguments(new Dictionary<string, object?>
        {
            ["message"] = "Pick one",
            ["suggestions"] = """
                [
                  { "label": "A", "question": "Created On" },
                  { "label": "B", "question": "Show the latest 5 Sales Orders by Modified On." }
                ]
                """
        });

        Assert.True(parsed.Success, parsed.Error);
        var validated = ClarificationSuggestionValidator.Validate(parsed, entities, policy);
        Assert.True(validated.Success, validated.Error);
        Assert.Equal(1, validated.AcceptedCount);
        Assert.Equal(1, validated.RejectedCount);
        Assert.Contains("Modified On", validated.Suggestions[0].Question, StringComparison.OrdinalIgnoreCase);
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
                    "question": "Show the latest 5 Sales Orders by Created On."
                  }
                ]
                """
        });

        Assert.False(parsed.Success);
        Assert.Contains("2–4", parsed.Error);
    }

    [Fact]
    public void Validator_Accepts_User_Facing_Suggestions_Without_Metadata()
    {
        var (policy, entities) = CreateApprovedEntities();
        var parsed = ClarifyCrmArgumentParser.ParseFromToolArguments(UserFacingOnlyArgs());

        var validated = ClarificationSuggestionValidator.Validate(parsed, entities, policy);
        Assert.True(validated.Success, validated.Error);
        Assert.Equal(2, validated.Suggestions.Count);
        Assert.Equal("Account", validated.Suggestions[0].Label);
        Assert.Contains("Account customer names", validated.Suggestions[0].Question);
    }

    [Fact]
    public void Validator_Accepts_Empty_Optional_Metadata_Arrays()
    {
        var (policy, entities) = CreateApprovedEntities();
        var parsed = ClarifyCrmArgumentParser.ParseFromToolArguments(new Dictionary<string, object?>
        {
            ["message"] = "Which customer type should I use?",
            ["suggestions"] = """
                [
                  {
                    "label": "Account",
                    "question": "Show the latest 5 sales orders with Account customer names.",
                    "entityLogicalName": "salesorder",
                    "attributeLogicalNames": [],
                    "relationshipSchemaNames": []
                  },
                  {
                    "label": "Contact",
                    "question": "Show the latest 5 sales orders with Contact customer names.",
                    "attributeLogicalNames": [],
                    "relationshipSchemaNames": []
                  }
                ]
                """
        });

        var validated = ClarificationSuggestionValidator.Validate(parsed, entities, policy);
        Assert.True(validated.Success, validated.Error);
        Assert.Equal(2, validated.Suggestions.Count);
    }

    [Fact]
    public void Validator_Accepts_Valid_Optional_Metadata_References()
    {
        var (policy, entities) = CreateApprovedEntities();
        var parsed = ClarifyCrmArgumentParser.ParseFromToolArguments(ValidArgs());

        var validated = ClarificationSuggestionValidator.Validate(parsed, entities, policy);
        Assert.True(validated.Success, validated.Error);
        Assert.Equal(2, validated.Suggestions.Count);
        Assert.All(validated.Suggestions, s =>
        {
            Assert.False(string.IsNullOrWhiteSpace(s.Label));
            Assert.Contains(' ', s.Question);
        });
    }

    [Fact]
    public void Validator_Drops_Unauthorized_Or_Missing_Attribute_But_Keeps_Valid_Sibling()
    {
        var (policy, entities) = CreateApprovedEntities();
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

        var validated = ClarificationSuggestionValidator.Validate(parsed, entities, policy);
        Assert.True(validated.Success, validated.Error);
        Assert.Equal(1, validated.AcceptedCount);
        Assert.Equal(1, validated.RejectedCount);
        Assert.Single(validated.Suggestions);
        Assert.Contains("Created On", validated.Suggestions[0].Question, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(validated.RejectionReasons, r => r.Contains("not_a_real_field", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Validator_Drops_Entity_Not_Approved_But_Keeps_Valid_Sibling()
    {
        var (policy, entities) = CreateApprovedEntities();
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

        var validated = ClarificationSuggestionValidator.Validate(parsed, entities, policy);
        Assert.True(validated.Success, validated.Error);
        Assert.Single(validated.Suggestions);
        Assert.Contains(validated.RejectionReasons, r => r.Contains("opportunity", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Validator_Drops_Invalid_Relationship_But_Keeps_Valid_Sibling()
    {
        var (policy, entities) = CreateApprovedEntities();
        var parsed = ClarifyCrmArgumentParser.ParseFromToolArguments(new Dictionary<string, object?>
        {
            ["message"] = "Which relationship?",
            ["suggestions"] = """
                [
                  {
                    "label": "Fake link",
                    "question": "Show the latest 5 sales orders with fake customer link.",
                    "relationshipSchemaNames": ["not_a_real_relationship"]
                  },
                  {
                    "label": "By name",
                    "question": "Show the latest 5 sales orders with order name only."
                  }
                ]
                """
        });

        var validated = ClarificationSuggestionValidator.Validate(parsed, entities, policy);
        Assert.True(validated.Success, validated.Error);
        Assert.Single(validated.Suggestions);
        Assert.Contains("order name only", validated.Suggestions[0].Question, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validator_Rejects_Underscore_Placeholder_Suggestions()
    {
        var (policy, entities) = CreateApprovedEntities();
        var parsed = ClarifyCrmArgumentParser.ParseFromToolArguments(new Dictionary<string, object?>
        {
            ["message"] = "Which Preferred Language value should I use?",
            ["suggestions"] = """
                [
                  {
                    "label": "English code",
                    "question": "The numeric value for English in the Preferred Communication Language option set is ___ (please fill in)."
                  },
                  {
                    "label": "Arabic code",
                    "question": "The numeric value for Arabic in the Preferred Communication Language option set is ___."
                  }
                ]
                """
        });

        var validated = ClarificationSuggestionValidator.Validate(parsed, entities, policy);
        Assert.False(validated.Success);
        Assert.Equal(0, validated.AcceptedCount);
        Assert.Equal(2, validated.RejectedCount);
        Assert.Empty(validated.Suggestions);
        Assert.All(validated.RejectionReasons, r =>
            Assert.True(
                r.Contains("placeholder", StringComparison.OrdinalIgnoreCase) ||
                r.Contains("OptionSet", StringComparison.OrdinalIgnoreCase) ||
                r.Contains("fill", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public void Validator_Rejects_Bracket_Enter_Value_Placeholders()
    {
        var (policy, entities) = CreateApprovedEntities();
        var parsed = ClarifyCrmArgumentParser.ParseFromToolArguments(new Dictionary<string, object?>
        {
            ["message"] = "Provide the missing filter value.",
            ["suggestions"] = """
                [
                  {
                    "label": "City filter",
                    "question": "Show accounts in city [enter value]."
                  },
                  {
                    "label": "Cairo",
                    "question": "Show accounts in Cairo."
                  }
                ]
                """
        });

        var validated = ClarificationSuggestionValidator.Validate(parsed, entities, policy);
        Assert.True(validated.Success, validated.Error);
        Assert.Equal(1, validated.AcceptedCount);
        Assert.Equal(1, validated.RejectedCount);
        Assert.Equal("Show accounts in Cairo.", validated.Suggestions[0].Question);
    }

    [Fact]
    public void Validator_Rejects_Empty_Label_Or_Question()
    {
        var (policy, entities) = CreateApprovedEntities();
        var parsed = ClarifyCrmArgumentParser.ParseFromToolArguments(new Dictionary<string, object?>
        {
            ["message"] = "Which sort?",
            ["suggestions"] = """
                [
                  { "label": " ", "question": "Show the latest 5 sales orders by Created On." },
                  { "label": "By name", "question": "   " },
                  { "label": "By Created On", "question": "Show the latest 5 sales orders by Created On." },
                  { "label": "By Modified On", "question": "Show the latest 5 sales orders by Modified On." }
                ]
                """
        });

        var validated = ClarificationSuggestionValidator.Validate(parsed, entities, policy);
        Assert.True(validated.Success, validated.Error);
        Assert.Equal(2, validated.AcceptedCount);
        Assert.Equal(2, validated.RejectedCount);
    }

    [Fact]
    public void Validator_Rejects_Asking_For_Logical_Or_Schema_Names()
    {
        var (policy, entities) = CreateApprovedEntities();
        var parsed = ClarifyCrmArgumentParser.ParseFromToolArguments(new Dictionary<string, object?>
        {
            ["message"] = "Which technical detail should I use?",
            ["suggestions"] = """
                [
                  {
                    "label": "Logical name",
                    "question": "Please provide the attribute logical name for the language field."
                  },
                  {
                    "label": "Schema name",
                    "question": "Please enter the relationship schema name to join accounts."
                  }
                ]
                """
        });

        var validated = ClarificationSuggestionValidator.Validate(parsed, entities, policy);
        Assert.False(validated.Success);
        Assert.Empty(validated.Suggestions);
        Assert.All(validated.RejectionReasons, r =>
            Assert.Contains("logical/schema", r, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Validator_Keeps_Normal_Business_Level_Clarifications()
    {
        var (policy, entities) = CreateApprovedEntities();
        var parsed = ClarifyCrmArgumentParser.ParseFromToolArguments(UserFacingOnlyArgs());
        var validated = ClarificationSuggestionValidator.Validate(parsed, entities, policy);
        Assert.True(validated.Success, validated.Error);
        Assert.Equal(2, validated.AcceptedCount);
        Assert.Equal(0, validated.RejectedCount);
    }

    [Fact]
    public void Answerable_Message_Helper_Accepts_Business_Questions()
    {
        Assert.True(ClarificationSuggestionValidator.IsAnswerableClarificationMessage(
            "Do you want Account or Contact customer names?"));
        Assert.False(ClarificationSuggestionValidator.IsAnswerableClarificationMessage("___"));
        Assert.False(ClarificationSuggestionValidator.IsAnswerableClarificationMessage(
            "Please provide the attribute logical name for language."));
        Assert.False(ClarificationSuggestionValidator.IsAnswerableClarificationMessage("short"));
    }

    [Fact]
    public void All_Invalid_Suggestions_With_Answerable_Message_Are_Custom_Reply_Candidates()
    {
        var (policy, entities) = CreateApprovedEntities();
        var parsed = ClarifyCrmArgumentParser.ParseFromToolArguments(new Dictionary<string, object?>
        {
            ["message"] = "Which Preferred Language should I use for this contact filter?",
            ["suggestions"] = """
                [
                  {
                    "label": "English code",
                    "question": "The numeric value for English in the Preferred Communication Language option set is ___ (please fill in)."
                  },
                  {
                    "label": "Arabic code",
                    "question": "The numeric value for Arabic in the Preferred Communication Language option set is ___."
                  }
                ]
                """
        });

        var validated = ClarificationSuggestionValidator.Validate(parsed, entities, policy);
        Assert.False(validated.Success);
        Assert.Equal(0, validated.AcceptedCount);
        Assert.True(ClarificationSuggestionValidator.IsAnswerableClarificationMessage(parsed.Message));
    }

    [Fact]
    public void Custom_Only_Clarification_History_Stores_Message_Without_Invalid_Chips()
    {
        var message = "Which Preferred Language should I use for this contact filter?";
        var historyText = ChatService.FormatClarificationHistoryText(
            message,
            Array.Empty<ClarificationSuggestionDto>());

        Assert.Equal(message, historyText);
        Assert.DoesNotContain("___", historyText, StringComparison.Ordinal);
        Assert.True(AssistantHistoryPrivacyFilter.IsAllowed("clarification", historyText, structuredData: null));
    }

    [Fact]
    public void Validated_Clarification_History_Uses_Accepted_Suggestions_Only()
    {
        var (policy, entities) = CreateApprovedEntities();
        var parsed = ClarifyCrmArgumentParser.ParseFromToolArguments(new Dictionary<string, object?>
        {
            ["message"] = "Which customer type should I use?",
            ["suggestions"] = """
                [
                  {
                    "label": "Fill later",
                    "question": "Show orders for customer ___."
                  },
                  {
                    "label": "Account",
                    "question": "Show the latest 5 sales orders with Account customer names."
                  }
                ]
                """
        });

        var validated = ClarificationSuggestionValidator.Validate(parsed, entities, policy);
        Assert.True(validated.Success, validated.Error);
        Assert.Single(validated.Suggestions);

        var historyText = ChatService.FormatClarificationHistoryText(parsed.Message!, validated.Suggestions);
        Assert.Contains("Account customer names", historyText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("___", historyText, StringComparison.Ordinal);
        Assert.True(AssistantHistoryPrivacyFilter.IsAllowed("clarification", historyText, structuredData: null));
    }

    [Fact]
    public void Validator_Rejects_Empty_Message()
    {
        var (policy, entities) = CreateApprovedEntities();
        var parsed = ClarifyCrmArgumentParser.ParseFromToolArguments(new Dictionary<string, object?>
        {
            ["message"] = "   ",
            ["suggestions"] = """
                [
                  { "label": "Account", "question": "Show the latest 5 sales orders with Account customer names." },
                  { "label": "Contact", "question": "Show the latest 5 sales orders with Contact customer names." }
                ]
                """
        });

        Assert.False(parsed.Success);
        var validated = ClarificationSuggestionValidator.Validate(parsed, entities, policy);
        Assert.False(validated.Success);
    }

    [Fact]
    public void Validator_Accepts_Without_Requiring_Live_Metadata_Catalog()
    {
        var (policy, _) = CreateApprovedEntities();
        var parsed = ClarifyCrmArgumentParser.ParseFromToolArguments(UserFacingOnlyArgs());

        // User-facing-only clarifications must succeed even if approved entity metadata is empty.
        var validated = ClarificationSuggestionValidator.Validate(
            parsed,
            Array.Empty<CrmEntityMetadata>(),
            policy);
        Assert.True(validated.Success, validated.Error);
        Assert.Equal(2, validated.Suggestions.Count);
    }

    [Fact]
    public void ChatResponseDto_Clarification_Carries_Suggestions_Without_Crm_Data()
    {
        var dto = new ChatResponseDto
        {
            ConversationId = "c1",
            Reply = "Do you want the customer name from an Account or Contact?",
            ResponseType = "clarification",
            PrivacySafeToolExecution = true,
            OutboundAiContainedCrmRecords = false,
            SelectedTool = "clarify_crm",
            Suggestions =
            [
                new ClarificationSuggestionDto
                {
                    Label = "Account",
                    Question = "Show the latest 5 sales orders with Account customer names."
                },
                new ClarificationSuggestionDto
                {
                    Label = "Contact",
                    Question = "Show the latest 5 sales orders with Contact customer names."
                }
            ]
        };

        Assert.Equal("clarification", dto.ResponseType);
        Assert.Equal("clarify_crm", dto.SelectedTool);
        Assert.Equal(2, dto.Suggestions!.Count);
        Assert.Null(dto.StructuredData);
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

    private static Dictionary<string, object?> UserFacingOnlyArgs() =>
        new()
        {
            ["message"] = "Do you want the customer name from an Account or Contact?",
            ["suggestions"] = """
                [
                  {
                    "label": "Account",
                    "question": "Show the latest 5 sales orders with Account customer names."
                  },
                  {
                    "label": "Contact",
                    "question": "Show the latest 5 sales orders with Contact customer names."
                  }
                ]
                """
        };

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

    private static (EntityApprovalPolicy Policy, IReadOnlyList<CrmEntityMetadata> Entities) CreateApprovedEntities()
    {
        var options = Options.Create(new CrmOptions
        {
            AllowSendApprovedMetadataCatalogToAi = false,
            ApprovedEntities = ["salesorder", "account"]
        });
        var policy = new EntityApprovalPolicy(options);
        var salesorder = new CrmEntityMetadata
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
        };

        return (policy, [salesorder]);
    }
}
