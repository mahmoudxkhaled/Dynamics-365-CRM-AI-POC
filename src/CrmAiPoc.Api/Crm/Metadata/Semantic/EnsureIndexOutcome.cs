namespace CrmAiPoc.Api.Crm.Metadata.Semantic;

/// <summary>Result of ensuring the semantic metadata index is available.</summary>
public sealed class EnsureIndexOutcome
{
    public required bool Success { get; init; }
    public InMemorySemanticMetadataIndex.IndexSnapshot? Snapshot { get; init; }
    public bool Rebuilt { get; init; }
    public required SemanticIndexState State { get; init; }
    public string? FailureReason { get; init; }
    public bool SkippedDueToFailureCooldown { get; init; }
    public int DocumentCount { get; init; }
    public int BatchCount { get; init; }
    public int CompletedBatches { get; init; }
    public long DurationMs { get; init; }
    /// <summary>True when an in-memory ready snapshot was reused (no disk/Ollama work).</summary>
    public bool CacheHit { get; init; }
    /// <summary>True when vectors were loaded from the project-local disk cache (no Ollama rebuild).</summary>
    public bool LoadedFromDisk { get; init; }
}
