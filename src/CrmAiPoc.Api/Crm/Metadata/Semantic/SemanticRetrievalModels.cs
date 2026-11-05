namespace CrmAiPoc.Api.Crm.Metadata.Semantic;

public sealed class IndexedMetadataVector
{
    public required MetadataSearchDocument Document { get; init; }
    public required float[] Embedding { get; init; }
}

public sealed class SemanticHit
{
    public required MetadataSearchDocument Document { get; init; }
    public required float Similarity { get; init; }
}

public sealed class SemanticMetadataRetrievalResult
{
    public required string Question { get; init; }
    public required string ContentHash { get; init; }
    public int IndexedDocumentCount { get; init; }
    public int RetrievedDocumentCount { get; init; }
    public int EstimatedPayloadChars { get; init; }
    public bool IndexRebuilt { get; init; }
    public IReadOnlyList<SemanticHit> EntityHits { get; init; } = Array.Empty<SemanticHit>();
    public IReadOnlyList<SemanticHit> AttributeHits { get; init; } = Array.Empty<SemanticHit>();
    public IReadOnlyList<SemanticHit> RelationshipHits { get; init; } = Array.Empty<SemanticHit>();
    public string? Error { get; init; }
}
