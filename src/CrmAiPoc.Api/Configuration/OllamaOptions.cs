namespace CrmAiPoc.Api.Configuration;

/// <summary>Local Ollama embedding endpoint (Phase 1 semantic metadata retrieval).</summary>
public sealed class OllamaOptions
{
    public const string SectionName = "Ollama";

    /// <summary>Ollama HTTP base URL (default http://localhost:11434).</summary>
    public string BaseUrl { get; set; } = "http://localhost:11434";

    /// <summary>Embedding model name (nomic-embed-text).</summary>
    public string EmbeddingModel { get; set; } = "nomic-embed-text";

    /// <summary>Expected embedding dimensionality for nomic-embed-text.</summary>
    public int ExpectedDimensions { get; set; } = 768;

    /// <summary>HTTP timeout for embedding calls (seconds).</summary>
    public int TimeoutSeconds { get; set; } = 60;
}
