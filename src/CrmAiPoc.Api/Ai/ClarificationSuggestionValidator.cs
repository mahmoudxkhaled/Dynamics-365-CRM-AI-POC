using CrmAiPoc.Api.Crm.Metadata;
using CrmAiPoc.Api.Models;

namespace CrmAiPoc.Api.Ai;

/// <summary>
/// Validates AI clarification suggestions against the metadata catalog sent to the model
/// (and approved entity policy). Does not invent substitute fields.
/// </summary>
public static class ClarificationSuggestionValidator
{
    public sealed class ValidationResult
    {
        public bool Success { get; init; }
        public string? Error { get; init; }
        public IReadOnlyList<ClarificationSuggestionDto> Suggestions { get; init; } =
            Array.Empty<ClarificationSuggestionDto>();
    }

    public static ValidationResult Validate(
        ClarifyCrmArgumentParser.ParseResult parsed,
        CrmAiMetadataCatalogBuildResult catalog,
        EntityApprovalPolicy approvalPolicy)
    {
        if (!parsed.Success || parsed.Suggestions.Count == 0)
        {
            return Fail(parsed.Error ?? "Clarification payload is invalid.");
        }

        if (catalog is not { Enabled: true, Success: true } ||
            catalog.Catalog.Detail.Count == 0)
        {
            return Fail("Clarification cannot be validated without an AI metadata catalog.");
        }

        var entitiesByName = catalog.Catalog.Detail
            .ToDictionary(e => e.LogicalName, StringComparer.OrdinalIgnoreCase);

        var accepted = new List<ClarificationSuggestionDto>();
        foreach (var suggestion in parsed.Suggestions)
        {
            if (string.IsNullOrWhiteSpace(suggestion.EntityLogicalName))
            {
                return Fail(
                    "Each clarification suggestion must declare entityLogicalName from the catalog.");
            }

            if (!approvalPolicy.IsApproved(suggestion.EntityLogicalName))
            {
                return Fail(
                    $"Clarification suggestion references unauthorized entity '{suggestion.EntityLogicalName}'.");
            }

            if (!entitiesByName.TryGetValue(suggestion.EntityLogicalName, out var entityDetail))
            {
                return Fail(
                    $"Clarification suggestion references entity '{suggestion.EntityLogicalName}' that was not in the AI catalog.");
            }

            var attrByName = entityDetail.Attributes
                .ToDictionary(a => a.LogicalName, StringComparer.OrdinalIgnoreCase);

            foreach (var attr in suggestion.AttributeLogicalNames)
            {
                if (!attrByName.ContainsKey(attr))
                {
                    return Fail(
                        $"Clarification suggestion references attribute '{suggestion.EntityLogicalName}.{attr}' that was not in the AI catalog.");
                }
            }

            var relBySchema = entityDetail.Relationships
                .Where(r => r.IsApproved)
                .ToDictionary(r => r.SchemaName, StringComparer.OrdinalIgnoreCase);

            // Relationships may also appear on peer entities in the same catalog.
            var allApprovedSchemas = catalog.Catalog.Detail
                .SelectMany(e => e.Relationships.Where(r => r.IsApproved).Select(r => r.SchemaName))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var schema in suggestion.RelationshipSchemaNames)
            {
                if (!allApprovedSchemas.Contains(schema) && !relBySchema.ContainsKey(schema))
                {
                    return Fail(
                        $"Clarification suggestion references relationship '{schema}' that was not in the AI catalog.");
                }
            }

            // Ambiguous date/field choices must declare the distinguishing attributes.
            if (suggestion.AttributeLogicalNames.Count == 0 &&
                suggestion.RelationshipSchemaNames.Count == 0)
            {
                return Fail(
                    "Each clarification suggestion must declare attributeLogicalNames and/or relationshipSchemaNames from the catalog.");
            }

            accepted.Add(new ClarificationSuggestionDto
            {
                Label = suggestion.Label,
                Question = suggestion.Question
            });
        }

        if (accepted.Count is < 2 or > 4)
        {
            return Fail("Clarification must retain 2–4 valid suggestions after validation.");
        }

        return new ValidationResult
        {
            Success = true,
            Suggestions = accepted
        };
    }

    private static ValidationResult Fail(string error) =>
        new()
        {
            Success = false,
            Error = error
        };
}
