namespace CrmAiPoc.Api.Crm.Metadata;

public interface ICrmMetadataService
{
    Task EnsureApprovedEntitiesLoadedAsync(CancellationToken cancellationToken = default);

    Task<CrmEntityMetadata?> GetEntityAsync(string logicalName, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CrmEntityMetadata>> GetApprovedEntitiesAsync(CancellationToken cancellationToken = default);

    Task RefreshAsync(string? logicalName = null, CancellationToken cancellationToken = default);

    string GetAiCatalogSummary();
}
