namespace CrmAiPoc.Api.Crm.Metadata;

/// <summary>
/// Builds approved CRM metadata payloads for outbound AI context.
/// Never includes CRM business records.
/// </summary>
public interface ICrmAiMetadataCatalogService
{
    /// <summary>
    /// Stage 1: approved entity index only (logicalName / displayName / description when available).
    /// No attributes or relationships.
    /// </summary>
    Task<CrmAiEntitySelectionIndexBuildResult> BuildApprovedEntitySelectionIndexAsync(
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Builds the complete approved metadata catalog (all ApprovedEntities when not filtered).
    /// Optional <paramref name="userMessage"/> is unused for selection (Stage 1 owns entity choice).
    /// </summary>
    Task<CrmAiMetadataCatalogBuildResult> BuildApprovedCatalogAsync(
        string? userMessage = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Stage 2: complete metadata for the given approved entity logical names only.
    /// Full attributes/relationships — no shrinking, ranking, or Take caps.
    /// </summary>
    Task<CrmAiMetadataCatalogBuildResult> BuildApprovedCatalogForSelectedEntitiesAsync(
        IReadOnlyCollection<string> selectedLogicalNames,
        CancellationToken cancellationToken = default);
}
