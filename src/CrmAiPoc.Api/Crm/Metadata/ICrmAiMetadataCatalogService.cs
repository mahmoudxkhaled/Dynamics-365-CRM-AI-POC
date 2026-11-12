namespace CrmAiPoc.Api.Crm.Metadata;

/// <summary>
/// Builds approved CRM metadata payloads for diagnostics/tests.
/// ChatService LLM-first planning does not send catalogs to the model.
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
