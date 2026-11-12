using System.ComponentModel;
using System.Text.Json.Serialization;

namespace CrmAiPoc.Api.Ai;

/// <summary>One clarification chip for the clarify_crm tool.</summary>
public sealed class ClarifyCrmSuggestionArg
{
    [Description("Short UI chip label.")]
    [JsonPropertyName("label")]
    public required string Label { get; set; }

    [Description(
        "Complete standalone question the user can submit unchanged as a new /api/chat request. " +
        "No blanks, placeholders, or requests for CRM technical identifiers.")]
    [JsonPropertyName("question")]
    public required string Question { get; set; }

    [Description(
        "Optional Dynamics entity logical name when the suggestion targets a specific entity. " +
        "Omit for ordinary user-facing clarification choices.")]
    [JsonPropertyName("entityLogicalName")]
    public string? EntityLogicalName { get; set; }

    [Description(
        "Optional distinguishing attribute logical names. Omit or leave empty when not needed. " +
        "When supplied, the backend validates them against live CRM metadata.")]
    [JsonPropertyName("attributeLogicalNames")]
    public string[]? AttributeLogicalNames { get; set; }

    [Description(
        "Optional distinguishing relationship SchemaNames. Omit or leave empty when not needed. " +
        "When supplied, the backend validates them against live CRM metadata. Never invent SchemaNames.")]
    [JsonPropertyName("relationshipSchemaNames")]
    public string[]? RelationshipSchemaNames { get; set; }
}
