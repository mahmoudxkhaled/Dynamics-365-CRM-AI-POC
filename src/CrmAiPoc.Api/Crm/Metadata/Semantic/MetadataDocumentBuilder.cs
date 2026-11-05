using System.Security.Cryptography;
using System.Text;

namespace CrmAiPoc.Api.Crm.Metadata.Semantic;

/// <summary>
/// Builds searchable documents from authoritative approved CRM metadata (no invented descriptions).
/// </summary>
public static class MetadataDocumentBuilder
{
    public const string DocumentPrefix = "search_document:";
    public const string QueryPrefix = "search_query:";

    public static IReadOnlyList<MetadataSearchDocument> Build(
        IReadOnlyList<CrmEntityMetadata> approvedEntities)
    {
        var docs = new List<MetadataSearchDocument>();
        foreach (var entity in approvedEntities.OrderBy(e => e.LogicalName, StringComparer.OrdinalIgnoreCase))
        {
            docs.Add(BuildEntityDocument(entity));

            foreach (var attr in entity.Attributes
                         .Where(a => a.IsValidForRead)
                         .OrderBy(a => a.LogicalName, StringComparer.OrdinalIgnoreCase))
            {
                docs.Add(BuildAttributeDocument(entity, attr));
            }

            foreach (var rel in entity.Relationships
                         .Where(r => r.IsApproved)
                         .OrderBy(r => r.SchemaName, StringComparer.OrdinalIgnoreCase))
            {
                docs.Add(BuildRelationshipDocument(entity, rel));
            }
        }

        return docs;
    }

    public static string ComputeContentHash(IReadOnlyList<CrmEntityMetadata> approvedEntities)
    {
        var sb = new StringBuilder(capacity: 4096);
        foreach (var entity in approvedEntities.OrderBy(e => e.LogicalName, StringComparer.OrdinalIgnoreCase))
        {
            sb.Append(entity.LogicalName).Append('|')
                .Append(entity.DisplayName).Append('|')
                .Append(entity.EntitySetName).Append('|')
                .Append(entity.PrimaryIdAttribute).Append('|')
                .Append(entity.PrimaryNameAttribute).Append(';');

            foreach (var attr in entity.Attributes
                         .Where(a => a.IsValidForRead)
                         .OrderBy(a => a.LogicalName, StringComparer.OrdinalIgnoreCase))
            {
                sb.Append('A').Append(attr.LogicalName).Append(':')
                    .Append(attr.DisplayName).Append(':')
                    .Append(attr.Kind).Append(';');
            }

            foreach (var rel in entity.Relationships
                         .Where(r => r.IsApproved)
                         .OrderBy(r => r.SchemaName, StringComparer.OrdinalIgnoreCase))
            {
                sb.Append('R').Append(rel.SchemaName).Append(':')
                    .Append(rel.ReferencingEntity).Append(':')
                    .Append(rel.ReferencedEntity).Append(':')
                    .Append(rel.ReferencingAttribute).Append(':')
                    .Append(rel.ReferencedAttribute).Append(';');
            }
        }

        var bytes = Encoding.UTF8.GetBytes(sb.ToString());
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash);
    }

    public static string PrefixQuery(string userQuestion) =>
        QueryPrefix + " " + (userQuestion ?? string.Empty).Trim();

    private static MetadataSearchDocument BuildEntityDocument(CrmEntityMetadata entity)
    {
        var parts = new List<string>
        {
            "CRM entity",
            $"logicalName={entity.LogicalName}",
            $"entitySetName={entity.EntitySetName}",
            $"primaryIdAttribute={entity.PrimaryIdAttribute}",
            $"primaryNameAttribute={entity.PrimaryNameAttribute}"
        };
        if (!string.IsNullOrWhiteSpace(entity.DisplayName))
        {
            parts.Add($"displayName={entity.DisplayName}");
        }

        if (entity.Aliases.Count > 0)
        {
            parts.Add("aliases=" + string.Join(',', entity.Aliases));
        }

        var text = string.Join(". ", parts);
        return new MetadataSearchDocument
        {
            Id = $"entity:{entity.LogicalName}",
            Kind = MetadataSearchDocumentKind.Entity,
            EntityLogicalName = entity.LogicalName,
            DisplayName = entity.DisplayName,
            SearchText = text,
            EmbeddingInput = DocumentPrefix + " " + text
        };
    }

    private static MetadataSearchDocument BuildAttributeDocument(
        CrmEntityMetadata entity,
        CrmAttributeMetadata attr)
    {
        var parts = new List<string>
        {
            "CRM attribute",
            $"entity={entity.LogicalName}",
            $"logicalName={attr.LogicalName}",
            $"type={attr.Kind}"
        };
        if (!string.IsNullOrWhiteSpace(attr.DisplayName))
        {
            parts.Add($"displayName={attr.DisplayName}");
        }

        if (!string.IsNullOrWhiteSpace(attr.AttributeTypeName))
        {
            parts.Add($"attributeTypeName={attr.AttributeTypeName}");
        }

        parts.Add($"filterable={attr.IsFilterable}");
        parts.Add($"sortable={attr.IsSortable}");

        var text = string.Join(". ", parts);
        return new MetadataSearchDocument
        {
            Id = $"attribute:{entity.LogicalName}.{attr.LogicalName}",
            Kind = MetadataSearchDocumentKind.Attribute,
            EntityLogicalName = entity.LogicalName,
            AttributeLogicalName = attr.LogicalName,
            DisplayName = attr.DisplayName,
            AttributeType = attr.Kind.ToString(),
            SearchText = text,
            EmbeddingInput = DocumentPrefix + " " + text
        };
    }

    private static MetadataSearchDocument BuildRelationshipDocument(
        CrmEntityMetadata entity,
        CrmRelationshipMetadata rel)
    {
        var parts = new List<string>
        {
            "CRM relationship",
            $"schemaName={rel.SchemaName}",
            $"kind={rel.Kind}",
            $"referencingEntity={rel.ReferencingEntity}",
            $"referencedEntity={rel.ReferencedEntity}",
            $"ownerEntity={entity.LogicalName}"
        };
        if (!string.IsNullOrWhiteSpace(rel.ReferencingAttribute))
        {
            parts.Add($"referencingAttribute={rel.ReferencingAttribute}");
        }

        if (!string.IsNullOrWhiteSpace(rel.ReferencedAttribute))
        {
            parts.Add($"referencedAttribute={rel.ReferencedAttribute}");
        }

        if (!string.IsNullOrWhiteSpace(rel.ReferencingAttributeDisplayName))
        {
            parts.Add($"referencingAttributeDisplayName={rel.ReferencingAttributeDisplayName}");
        }

        if (!string.IsNullOrWhiteSpace(rel.BusinessLabel))
        {
            parts.Add($"businessLabel={rel.BusinessLabel}");
        }

        var text = string.Join(". ", parts);
        return new MetadataSearchDocument
        {
            Id = $"relationship:{rel.SchemaName}@{entity.LogicalName}",
            Kind = MetadataSearchDocumentKind.Relationship,
            EntityLogicalName = entity.LogicalName,
            RelationshipSchemaName = rel.SchemaName,
            DisplayName = rel.BusinessLabel ?? rel.ReferencingAttributeDisplayName,
            ReferencingEntity = rel.ReferencingEntity,
            ReferencedEntity = rel.ReferencedEntity,
            ReferencingAttribute = rel.ReferencingAttribute,
            ReferencedAttribute = rel.ReferencedAttribute,
            SearchText = text,
            EmbeddingInput = DocumentPrefix + " " + text
        };
    }
}
