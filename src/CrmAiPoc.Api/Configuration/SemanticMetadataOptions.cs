namespace CrmAiPoc.Api.Configuration;

/// <summary>Configurable retrieval limits for Phase 1 semantic CRM metadata search.</summary>
public sealed class SemanticMetadataOptions
{
    public const string SectionName = "SemanticMetadata";

    /// <summary>Highest-ranked entity documents to select before attribute/relationship scoping.</summary>
    public int TopKEntities { get; set; } = 3;

    /// <summary>
    /// Per selected entity: top attribute documents by cosine similarity.
    /// Structural primary / relationship endpoint attributes may be added beyond this count.
    /// </summary>
    public int TopKAttributesPerEntity { get; set; } = 20;

    public int TopKRelationships { get; set; } = 10;

    /// <summary>Minimum cosine similarity to include a hit (0–1). Default 0 = no floor.</summary>
    public float MinSimilarity { get; set; }

    /// <summary>When true, Development diagnostic endpoint is mapped.</summary>
    public bool EnableDiagnosticEndpoint { get; set; } = true;
}
