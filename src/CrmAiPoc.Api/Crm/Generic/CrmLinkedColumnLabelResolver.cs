using System.Text.RegularExpressions;
using CrmAiPoc.Api.Crm.Metadata;

namespace CrmAiPoc.Api.Crm.Generic;

/// <summary>
/// Builds human-readable labels for linked-entity table columns without duplicating
/// entity names already present in CRM attribute DisplayNames.
/// Technical column keys remain alias-qualified and are not affected.
/// </summary>
public static class CrmLinkedColumnLabelResolver
{
    /// <summary>
    /// Resolves a display label for a linked attribute column.
    /// </summary>
    /// <param name="attributeDisplayName">CRM attribute DisplayName when available.</param>
    /// <param name="attributeLogicalName">Attribute logical name fallback.</param>
    /// <param name="entityDisplayName">Linked entity DisplayName when available.</param>
    /// <param name="entityLogicalName">Linked entity logical name fallback.</param>
    /// <param name="relationshipDisplayName">
    /// Optional relationship/business-role label (e.g. Customer Account) used when the same
    /// entity is joined more than once under different relationships.
    /// </param>
    /// <param name="disambiguateByRelationship">
    /// True when multiple links target the same entity logical name in this result set.
    /// </param>
    public static string Resolve(
        string? attributeDisplayName,
        string attributeLogicalName,
        string? entityDisplayName,
        string entityLogicalName,
        string? relationshipDisplayName = null,
        bool disambiguateByRelationship = false)
    {
        var attrLabel = FirstNonEmpty(attributeDisplayName, attributeLogicalName);
        var entityLabel = FirstNonEmpty(entityDisplayName, entityLogicalName);

        string baseLabel;
        if (StartsWithLabel(attrLabel, entityLabel) || ContainsLabel(attrLabel, entityLabel))
        {
            // CRM already named the field with the entity (e.g. "Account Name (English)"
            // or "Customer Account Name") — do not prefix again.
            baseLabel = attrLabel;
        }
        else
        {
            // Generic attribute labels need entity context (e.g. "Name" → "Account Name").
            baseLabel = string.IsNullOrWhiteSpace(entityLabel)
                ? attrLabel
                : $"{entityLabel} {attrLabel}".Trim();
        }

        if (!disambiguateByRelationship || string.IsNullOrWhiteSpace(relationshipDisplayName))
        {
            return baseLabel;
        }

        var role = relationshipDisplayName.Trim();
        if (ContainsLabel(baseLabel, role) || StartsWithLabel(baseLabel, role))
        {
            return baseLabel;
        }

        // Distinguish same-entity joins by relationship role without inventing field names.
        // Example: "Customer Account — Account Name (English)"
        return $"{role} — {attrLabel}";
    }

    /// <summary>
    /// Picks a business-facing relationship label from metadata when available.
    /// </summary>
    public static string? ResolveRelationshipDisplayName(CrmRelationshipMetadata? relationship)
    {
        if (relationship is null)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(relationship.BusinessLabel))
        {
            return relationship.BusinessLabel.Trim();
        }

        if (!string.IsNullOrWhiteSpace(relationship.ReferencingAttributeDisplayName))
        {
            return relationship.ReferencingAttributeDisplayName.Trim();
        }

        return null;
    }

    public static bool StartsWithLabel(string text, string label)
    {
        if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(label))
        {
            return false;
        }

        var t = text.Trim();
        var l = label.Trim();
        if (t.Length < l.Length)
        {
            return false;
        }

        if (!t.StartsWith(l, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // Require a boundary so "Account" does not match "Accountability".
        if (t.Length == l.Length)
        {
            return true;
        }

        return !char.IsLetterOrDigit(t[l.Length]);
    }

    public static bool ContainsLabel(string text, string label)
    {
        if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(label))
        {
            return false;
        }

        var pattern = $@"(?i)(?<![\p{{L}}\p{{N}}]){Regex.Escape(label.Trim())}(?![\p{{L}}\p{{N}}])";
        return Regex.IsMatch(text.Trim(), pattern);
    }

    private static string FirstNonEmpty(string? preferred, string fallback)
    {
        if (!string.IsNullOrWhiteSpace(preferred))
        {
            return preferred.Trim();
        }

        return (fallback ?? string.Empty).Trim();
    }
}
