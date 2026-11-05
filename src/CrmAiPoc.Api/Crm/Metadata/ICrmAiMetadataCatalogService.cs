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
    /// </summary>
    Task<CrmAiMetadataCatalogBuildResult> BuildApprovedCatalogAsync(
        string? userMessage = null,
        CancellationToken cancellationToken = default);
}
