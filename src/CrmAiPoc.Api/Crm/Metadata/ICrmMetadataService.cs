namespace CrmAiPoc.Api.Crm.Metadata;

public interface ICrmMetadataService
{
    Task EnsureApprovedEntitiesLoadedAsync(CancellationToken cancellationToken = default);

    Task<CrmEntityMetadata?> GetEntityAsync(string logicalName, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CrmEntityMetadata>> GetApprovedEntitiesAsync(CancellationToken cancellationToken = default);

    Task RefreshAsync(string? logicalName = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lazily loads OptionSet / Boolean choice labels and numeric values for a single attribute.
    /// Uses a type-specific Dynamics metadata cast. Does not load options for unrelated fields.
    /// </summary>
    Task<AttributeOptionsResult> EnsureAttributeOptionsAsync(
        string entityLogicalName,
        string attributeLogicalName,
        CancellationToken cancellationToken = default);

    string GetAiCatalogSummary();
}
