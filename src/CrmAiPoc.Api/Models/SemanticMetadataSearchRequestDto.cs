namespace CrmAiPoc.Api.Models;

/// <summary>Development-only diagnostic request for Phase 1 semantic metadata retrieval.</summary>
public sealed class SemanticMetadataSearchRequestDto
{
    public string Question { get; set; } = string.Empty;
}
