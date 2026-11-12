namespace CrmAiPoc.Api.Crm.Metadata.Semantic;

/// <summary>
/// Searchable CRM metadata document. Text is embedded; Id references authoritative cache entries.
/// </summary>
public sealed class MetadataSearchDocument
{
    public required string Id { get; init; }
    public required MetadataSearchDocumentKind Kind { get; init; }
    public required string EntityLogicalName { get; init; }
    public string? AttributeLogicalName { get; init; }
    public string? RelationshipSchemaName { get; init; }
    public string? DisplayName { get; init; }
    public string? Description { get; init; }
    public string? AttributeType { get; init; }
    public string? ReferencingEntity { get; init; }
    public string? ReferencedEntity { get; init; }
    public string? ReferencingAttribute { get; init; }
    public string? ReferencedAttribute { get; init; }

    /// <summary>Plain text used for embedding (without Nomic prefix).</summary>
    public required string SearchText { get; init; }

    /// <summary>Nomic-prefixed text actually sent to the embedder (search_document: …).</summary>
    public required string EmbeddingInput { get; init; }
}
