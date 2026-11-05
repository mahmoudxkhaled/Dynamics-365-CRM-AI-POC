using CrmAiPoc.Api.Crm.Metadata.Semantic;

namespace CrmAiPoc.Api.Crm.Metadata;

/// <summary>
/// Builds approved CRM metadata payloads for outbound AI context.
/// Never includes CRM business records.
/// </summary>
public interface ICrmAiMetadataCatalogService
{
    /// <summary>
    /// Builds the complete approved metadata catalog for all ApprovedEntities
    /// (readable attributes + approved relationships only).
    /// Used for diagnostics/tests — ChatService uses semantic selection instead.
    /// </summary>
    Task<CrmAiMetadataCatalogBuildResult> BuildApprovedCatalogAsync(
        string? userMessage = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Builds a compact AI catalog from semantic retrieval hits using authoritative
    /// CRM metadata definitions (not embedding search text).
    /// </summary>
    Task<CrmAiMetadataCatalogBuildResult> BuildCatalogFromSemanticRetrievalAsync(
        SemanticMetadataRetrievalResult retrieval,
        CancellationToken cancellationToken = default);
}
