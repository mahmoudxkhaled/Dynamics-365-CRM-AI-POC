namespace CrmAiPoc.Api.Crm.Metadata;

/// <summary>
/// Builds a compact, approved-only CRM metadata catalog for outbound AI context.
/// Never includes CRM business records.
/// </summary>
public interface ICrmAiMetadataCatalogService
{
    /// <summary>
    /// Builds a bounded catalog of approved entities. When enabled, failure means the chat
    /// path must not call the AI planner (empty/incomplete catalogs are not treated as success).
    /// Optional <paramref name="userMessage"/> expands attributes/relationships for relevant entities.
    /// </summary>
    Task<CrmAiMetadataCatalogBuildResult> BuildApprovedCatalogAsync(
        string? userMessage = null,
        CancellationToken cancellationToken = default);
}
