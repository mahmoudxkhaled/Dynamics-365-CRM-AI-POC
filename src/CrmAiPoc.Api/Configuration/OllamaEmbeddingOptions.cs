namespace CrmAiPoc.Api.Configuration;

/// <summary>Local Ollama embedding endpoint for semantic CRM metadata retrieval (SEM-02).</summary>
public sealed class OllamaEmbeddingOptions
{
    public const string SectionName = "OllamaEmbeddings";

    /// <summary>Ollama HTTP base URL (default http://localhost:11434).</summary>
    public string BaseUrl { get; set; } = "http://localhost:11434";

    /// <summary>Embedding model name (nomic-embed-text).</summary>
    public string EmbeddingModel { get; set; } = "nomic-embed-text";

    /// <summary>Expected embedding dimensionality for nomic-embed-text.</summary>
    public int ExpectedDimensions { get; set; } = 768;

    /// <summary>
    /// HTTP timeout for a single embedding batch request (seconds).
    /// Index builds issue multiple bounded batches; each batch uses this timeout.
    /// </summary>
    public int TimeoutSeconds { get; set; } = 120;
}
