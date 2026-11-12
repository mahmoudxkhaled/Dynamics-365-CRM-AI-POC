using System.Text.RegularExpressions;
using CrmAiPoc.Api.Crm.Metadata;
using CrmAiPoc.Api.Models;

namespace CrmAiPoc.Api.Ai;

/// <summary>
/// Validates user-facing clarify_crm suggestions (structure + actionable-question quality).
/// Invalid suggestions are dropped; valid ones are preserved. Does not execute CRM queries.
/// </summary>
public static partial class ClarificationSuggestionValidator
{
    private const int MaxMessageLength = 500;
    private const int MaxLabelLength = 80;
    private const int MaxQuestionLength = 500;

    public sealed class ValidationResult
    {
        public bool Success { get; init; }
        public string? Error { get; init; }
        public IReadOnlyList<ClarificationSuggestionDto> Suggestions { get; init; } =
            Array.Empty<ClarificationSuggestionDto>();
        public int ReceivedCount { get; init; }
        public int AcceptedCount { get; init; }
        public int RejectedCount { get; init; }
        public IReadOnlyList<string> RejectionReasons { get; init; } = Array.Empty<string>();
    }

    public static ValidationResult Validate(
        ClarifyCrmArgumentParser.ParseResult parsed,
        IReadOnlyList<CrmEntityMetadata> approvedEntities,
        EntityApprovalPolicy approvalPolicy)
    {
        if (!parsed.Success || parsed.Suggestions.Count == 0)
        {
            return Fail(parsed.Error ?? "Clarification payload is invalid.");
        }

        if (string.IsNullOrWhiteSpace(parsed.Message))
        {
            return Fail("clarify_crm message is required.");
        }

        if (parsed.Message.Length > MaxMessageLength)
        {
            return Fail($"clarify_crm message must be at most {MaxMessageLength} characters.");
        }

        if (parsed.Suggestions.Count is < 2 or > 4)
        {
            return Fail($"Clarification must include 2–4 suggestions (received {parsed.Suggestions.Count}).");
        }

        var entitiesByName = approvedEntities
            .Where(e => approvalPolicy.IsApproved(e.LogicalName))
            .ToDictionary(e => e.LogicalName, StringComparer.OrdinalIgnoreCase);

        var allApprovedSchemas = approvedEntities
            .SelectMany(e => e.Relationships.Where(r => r.IsApproved).Select(r => r.SchemaName))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var accepted = new List<ClarificationSuggestionDto>();
        var rejectionReasons = new List<string>();
        var index = 0;

        foreach (var suggestion in parsed.Suggestions)
        {
            index++;
            var label = suggestion.Label?.Trim() ?? string.Empty;
            var question = suggestion.Question?.Trim() ?? string.Empty;

            if (string.IsNullOrWhiteSpace(label))
            {
                rejectionReasons.Add($"#{index}: empty label");
                continue;
            }

            if (label.Length > MaxLabelLength)
            {
                rejectionReasons.Add($"#{index}: label exceeds {MaxLabelLength} characters");
                continue;
            }

            if (string.IsNullOrWhiteSpace(question))
            {
                rejectionReasons.Add($"#{index}: empty question");
                continue;
            }

            if (question.Length > MaxQuestionLength)
            {
                rejectionReasons.Add($"#{index}: question exceeds {MaxQuestionLength} characters");
                continue;
            }

            if (question.Length < 12 || !question.Contains(' ', StringComparison.Ordinal))
            {
                rejectionReasons.Add($"#{index}: question is not a complete standalone sentence");
                continue;
            }

            if (TryDescribePlaceholderIssue(question, out var placeholderReason) ||
                TryDescribePlaceholderIssue(label, out placeholderReason))
            {
                rejectionReasons.Add($"#{index}: {placeholderReason}");
                continue;
            }

            if (TryDescribeTechnicalUserAskIssue(question, out var technicalReason) ||
                TryDescribeTechnicalUserAskIssue(label, out technicalReason))
            {
                rejectionReasons.Add($"#{index}: {technicalReason}");
                continue;
            }

            // Optional metadata refs: validate only when supplied. Empty arrays are fine.
            CrmEntityMetadata? entityMeta = null;
            if (!string.IsNullOrWhiteSpace(suggestion.EntityLogicalName))
            {
                if (!approvalPolicy.IsApproved(suggestion.EntityLogicalName))
                {
                    rejectionReasons.Add(
                        $"#{index}: unauthorized entity '{suggestion.EntityLogicalName}'");
                    continue;
                }

                if (!entitiesByName.TryGetValue(suggestion.EntityLogicalName, out entityMeta))
                {
                    rejectionReasons.Add(
                        $"#{index}: entity '{suggestion.EntityLogicalName}' not in live approved metadata");
                    continue;
                }
            }

            if (suggestion.AttributeLogicalNames.Count > 0)
            {
                if (entityMeta is null)
                {
                    rejectionReasons.Add(
                        $"#{index}: attributeLogicalNames require entityLogicalName");
                    continue;
                }

                var attrByName = entityMeta.Attributes
                    .Where(a => a.IsValidForRead)
                    .ToDictionary(a => a.LogicalName, StringComparer.OrdinalIgnoreCase);

                var missingAttr = suggestion.AttributeLogicalNames
                    .FirstOrDefault(attr => !attrByName.ContainsKey(attr));
                if (missingAttr is not null)
                {
                    rejectionReasons.Add(
                        $"#{index}: attribute '{suggestion.EntityLogicalName}.{missingAttr}' not in live metadata");
                    continue;
                }
            }

            if (suggestion.RelationshipSchemaNames.Count > 0)
            {
                var relBySchema = entityMeta?.Relationships
                    .Where(r => r.IsApproved)
                    .ToDictionary(r => r.SchemaName, StringComparer.OrdinalIgnoreCase);

                var missingRel = suggestion.RelationshipSchemaNames.FirstOrDefault(schema =>
                    !allApprovedSchemas.Contains(schema) &&
                    (relBySchema is null || !relBySchema.ContainsKey(schema)));
                if (missingRel is not null)
                {
                    rejectionReasons.Add(
                        $"#{index}: relationship '{missingRel}' not in live approved metadata");
                    continue;
                }
            }

            accepted.Add(new ClarificationSuggestionDto
            {
                Label = label,
                Question = question
            });
        }

        var received = parsed.Suggestions.Count;
        var rejected = rejectionReasons.Count;

        if (accepted.Count == 0)
        {
            return new ValidationResult
            {
                Success = false,
                Error = "No actionable clarification suggestions remained after validation.",
                ReceivedCount = received,
                AcceptedCount = 0,
                RejectedCount = rejected,
                RejectionReasons = rejectionReasons
            };
        }

        return new ValidationResult
        {
            Success = true,
            Suggestions = accepted,
            ReceivedCount = received,
            AcceptedCount = accepted.Count,
            RejectedCount = rejected,
            RejectionReasons = rejectionReasons
        };
    }

    /// <summary>
    /// True when a clarification prompt itself is a business-level question the user can answer in free text
    /// (used when all AI chips were rejected but custom reply should still be offered).
    /// </summary>
    public static bool IsAnswerableClarificationMessage(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return false;
        }

        var trimmed = message.Trim();
        if (trimmed.Length < 12 || trimmed.Length > MaxMessageLength)
        {
            return false;
        }

        if (TryDescribePlaceholderIssue(trimmed, out _) ||
            TryDescribeTechnicalUserAskIssue(trimmed, out _))
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// Detects fill-in-the-blank / incomplete clickable suggestion text (generic patterns).
    /// </summary>
    internal static bool TryDescribePlaceholderIssue(string text, out string reason)
    {
        reason = string.Empty;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        if (UnderscoreBlankPattern().IsMatch(text) ||
            EllipsisBlankPattern().IsMatch(text))
        {
            reason = "contains fill-in-the-blank placeholder characters";
            return true;
        }

        if (BracketPlaceholderPattern().IsMatch(text) ||
            AnglePlaceholderPattern().IsMatch(text))
        {
            reason = "contains bracketed placeholder text";
            return true;
        }

        if (FillInInstructionPattern().IsMatch(text))
        {
            reason = "asks the user to fill in a missing value";
            return true;
        }

        return false;
    }

    /// <summary>
    /// Rejects suggestions that ask ordinary users for CRM implementation details.
    /// </summary>
    internal static bool TryDescribeTechnicalUserAskIssue(string text, out string reason)
    {
        reason = string.Empty;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        if (OptionSetNumericAskPattern().IsMatch(text))
        {
            reason = "asks the user for an internal OptionSet numeric value";
            return true;
        }

        if (LogicalOrSchemaNameAskPattern().IsMatch(text))
        {
            reason = "asks the user for CRM logical/schema names";
            return true;
        }

        if (GuidAskPattern().IsMatch(text))
        {
            reason = "asks the user for a GUID or record id";
            return true;
        }

        return false;
    }

    private static ValidationResult Fail(string error) =>
        new()
        {
            Success = false,
            Error = error
        };

    [GeneratedRegex(@"_{3,}|\b_{2,}\b", RegexOptions.CultureInvariant)]
    private static partial Regex UnderscoreBlankPattern();

    [GeneratedRegex(@"\[\s*\.{3,}\s*\]|\(\s*\.{3,}\s*\)|\b\.{3,}\b\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex EllipsisBlankPattern();

    [GeneratedRegex(
        @"\[\s*(enter|fill|insert|type|provide|your|missing|value|number|code|here|blank|placeholder)[^\]\n]{0,40}\]",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BracketPlaceholderPattern();

    [GeneratedRegex(@"<\s*(enter|fill|value|number|code|placeholder)[^>\n]{0,40}>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AnglePlaceholderPattern();

    [GeneratedRegex(
        @"\b(please\s+fill(\s+in)?|fill\s+in|replace\s+with|enter\s+the\s+value|complete\s+the\s+blank|missing\s+value)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex FillInInstructionPattern();

    [GeneratedRegex(
        @"\b(numeric\s+value|option\s*set\s*(value|code|number)|optionset\s*(value|code)|picklist\s*(value|code))\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex OptionSetNumericAskPattern();

    [GeneratedRegex(
        @"\b((provide|enter|supply|tell me|what is)\s+(the\s+)?(attribute\s+)?logical\s+name|(provide|enter|supply)\s+(the\s+)?(relationship\s+)?schema\s*name|schemaName|logicalName)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LogicalOrSchemaNameAskPattern();

    [GeneratedRegex(
        @"\b((provide|enter|supply|paste)\s+(the\s+)?(record\s+)?(guid|id)\b|guid\s+of\b)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex GuidAskPattern();
}
