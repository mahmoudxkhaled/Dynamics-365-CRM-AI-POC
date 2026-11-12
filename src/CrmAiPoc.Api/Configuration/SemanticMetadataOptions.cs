namespace CrmAiPoc.Api.Configuration;

/// <summary>Configurable hybrid lexical + semantic CRM metadata retrieval (SEM-02).</summary>
public sealed class SemanticMetadataOptions
{
    public const string SectionName = "SemanticMetadata";

    /// <summary>
    /// When true, <c>discover_crm_schema</c> merges Ollama semantic similarity with lexical ranking.
    /// When false, lexical-only behavior is preserved.
    /// </summary>
    public bool EnableHybridRetrieval { get; set; } = true;

    /// <summary>Weight for normalized lexical score in hybrid ranking (0–1 scale after normalization).</summary>
    public double LexicalWeight { get; set; } = 0.55;

    /// <summary>Weight for cosine semantic similarity in hybrid ranking.</summary>
    public double SemanticWeight { get; set; } = 0.45;

    /// <summary>
    /// Minimum cosine similarity for a semantic-only candidate to enter the hybrid pool
    /// when lexical score is below the accept threshold.
    /// </summary>
    public float MinSemanticSimilarity { get; set; } = 0.32f;

    /// <summary>Max attribute documents considered from the semantic index per concept (scoped).</summary>
    public int SemanticCandidatePoolSize { get; set; } = 40;

    /// <summary>Highest-ranked entity documents (legacy full-question retrieval helper).</summary>
    public int TopKEntities { get; set; } = 3;

    /// <summary>Per selected entity: top attribute documents by cosine similarity.</summary>
    public int TopKAttributesPerEntity { get; set; } = 20;

    public int TopKRelationships { get; set; } = 10;

    /// <summary>Minimum cosine similarity to include a hit in legacy full retrieval (0–1).</summary>
    public float MinSimilarity { get; set; }

    /// <summary>When true, Development diagnostic endpoint may be mapped.</summary>
    public bool EnableDiagnosticEndpoint { get; set; }

    /// <summary>
    /// When true, completed embedding vectors are saved to a local JSON file and reused
    /// across process restarts while the metadata content hash / model / dimensions match.
    /// </summary>
    public bool EnableDiskCache { get; set; } = true;

    /// <summary>
    /// Directory for the embedding cache file, relative to the ASP.NET Core ContentRootPath
    /// (or an absolute path). Default: Cache/SemanticIndex (project-local, not under wwwroot/bin).
    /// </summary>
    public string? CacheDirectory { get; set; } = Path.Combine("Cache", "SemanticIndex");

    /// <summary>
    /// Max documents per Ollama /api/embed request while building the index.
    /// Smaller batches avoid HttpClient timeouts on large approved metadata catalogs.
    /// </summary>
    public int EmbeddingBatchSize { get; set; } = 16;

    /// <summary>
    /// After an index build failure, skip rebuild attempts for this many seconds
    /// and fall back to lexical retrieval (avoids repeated 30s timeouts per chat).
    /// </summary>
    public int IndexFailureCooldownSeconds { get; set; } = 90;

    /// <summary>
    /// When true, warm the semantic index in the background after CRM metadata warm-up.
    /// </summary>
    public bool WarmIndexOnStartup { get; set; } = true;
}
