using System.ComponentModel;
using System.Text.Json.Serialization;

namespace CrmAiPoc.Api.Ai;

/// <summary>One clarification chip for the clarify_crm tool.</summary>
public sealed class ClarifyCrmSuggestionArg
{
    [Description("Short UI chip label (e.g. Customer Account).")]
    [JsonPropertyName("label")]
    public required string Label { get; set; }

    [Description(
        "Complete standalone English question the user can submit unchanged as a new /api/chat request.")]
    [JsonPropertyName("question")]
    public required string Question { get; set; }

    [Description("Approved entity logicalName from the APPROVED_CRM_METADATA_CATALOG.")]
    [JsonPropertyName("entityLogicalName")]
    public required string EntityLogicalName { get; set; }

    [Description(
        "Distinguishing attribute logical names from the catalog. " +
        "May be empty when the ambiguity is relationship-only.")]
    [JsonPropertyName("attributeLogicalNames")]
    public string[]? AttributeLogicalNames { get; set; }

    [Description(
        "Distinguishing relationship SchemaNames from the catalog. " +
        "May be empty when the ambiguity is attribute-only. Never invent SchemaNames.")]
    [JsonPropertyName("relationshipSchemaNames")]
    public string[]? RelationshipSchemaNames { get; set; }
}
