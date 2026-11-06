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

    /// <summary>
    /// When true, completed embedding vectors are saved to a local JSON file and reused
    /// across process restarts while the metadata content hash / model / dimensions match.
    /// </summary>
    public bool EnableDiskCache { get; set; } = true;

    /// <summary>
    /// Directory for the embedding cache file, relative to the API content root
    /// (or an absolute path). Default: App_Data/semantic-metadata-index.
    /// </summary>
    public string? CacheDirectory { get; set; } = Path.Combine("App_Data", "semantic-metadata-index");
}
