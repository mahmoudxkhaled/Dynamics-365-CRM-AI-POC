namespace CrmAiPoc.Api.Crm.Metadata;

/// <summary>
/// Builds a compact, approved-only CRM metadata catalog for outbound AI context.
/// Never includes CRM business records.
/// </summary>
public interface ICrmAiMetadataCatalogService
{
    /// <summary>
    /// Builds a catalog covering all approved entities (compact attribute set).
    /// When <c>Crm:AllowSendApprovedMetadataCatalogToAi</c> is false, returns Enabled=false.
    /// </summary>
    Task<CrmAiMetadataCatalogBuildResult> BuildApprovedCatalogAsync(
        CancellationToken cancellationToken = default);
}
