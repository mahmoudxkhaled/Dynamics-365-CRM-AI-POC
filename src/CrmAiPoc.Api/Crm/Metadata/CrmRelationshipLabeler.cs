using CrmAiPoc.Api.Configuration;
using Microsoft.Extensions.Options;

namespace CrmAiPoc.Api.Crm.Metadata;

/// <summary>
/// Builds business-friendly relationship descriptions for clarification UX.
/// Never invents SchemaNames — only labels known metadata.
/// </summary>
public sealed class CrmRelationshipLabeler
{
    private readonly Dictionary<string, string> _labels;

    public CrmRelationshipLabeler(IOptions<CrmOptions> options)
    {
        _labels = new Dictionary<string, string>(
            options.Value.RelationshipLabels ?? new Dictionary<string, string>(),
            StringComparer.OrdinalIgnoreCase);
    }

    public string Describe(CrmRelationshipMetadata rel, CrmEntityMetadata? referencingEntity = null)
    {
        if (!string.IsNullOrWhiteSpace(rel.BusinessLabel))
        {
            return rel.BusinessLabel!;
        }

        if (_labels.TryGetValue(rel.SchemaName, out var configured) && !string.IsNullOrWhiteSpace(configured))
        {
            return configured;
        }

        if (!string.IsNullOrWhiteSpace(rel.ReferencingAttributeDisplayName))
        {
            return $"{rel.ReferencingAttributeDisplayName} → {FriendlyEntity(rel.ReferencedEntity)}";
        }

        if (referencingEntity is not null &&
            !string.IsNullOrWhiteSpace(rel.ReferencingAttribute) &&
            referencingEntity.TryGetAttribute(rel.ReferencingAttribute!, out var attr) &&
            !string.IsNullOrWhiteSpace(attr.DisplayName))
        {
            return $"{attr.DisplayName} → {FriendlyEntity(rel.ReferencedEntity)}";
        }

        if (!string.IsNullOrWhiteSpace(rel.ReferencingAttribute))
        {
            return $"{Humanize(rel.ReferencingAttribute!)} → {FriendlyEntity(rel.ReferencedEntity)}";
        }

        return $"{FriendlyEntity(rel.ReferencingEntity)} → {FriendlyEntity(rel.ReferencedEntity)}";
    }

    private static string FriendlyEntity(string logical) => logical.ToLowerInvariant() switch
    {
        "salesorder" => "Sales Order",
        "account" => "Account",
        "contact" => "Contact",
        "lead" => "Lead",
        _ => logical
    };

    private static string Humanize(string logicalName)
    {
        var s = logicalName.Trim();
        if (s.StartsWith("te_", StringComparison.OrdinalIgnoreCase))
        {
            s = s[3..];
        }

        if (s.EndsWith("id", StringComparison.OrdinalIgnoreCase) && s.Length > 2)
        {
            s = s[..^2];
        }

        return string.Join(' ', s.Split('_', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => char.ToUpperInvariant(p[0]) + p[1..].ToLowerInvariant()));
    }
}
