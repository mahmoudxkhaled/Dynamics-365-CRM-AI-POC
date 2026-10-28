namespace CrmAiPoc.Api.Crm.Metadata;

/// <summary>
/// Seed-based metadata service for Mock CRM / unit tests (no live Dynamics calls).
/// </summary>
public sealed class MockCrmMetadataService : ICrmMetadataService
{
    private readonly CrmMetadataRegistry _registry;
    private readonly EntityApprovalPolicy _policy;
    private bool _loaded;

    public MockCrmMetadataService(CrmMetadataRegistry registry, EntityApprovalPolicy policy)
    {
        _registry = registry;
        _policy = policy;
    }

    public string GetAiCatalogSummary() => _registry.BuildAiCatalogSummary();

    public Task EnsureApprovedEntitiesLoadedAsync(CancellationToken cancellationToken = default)
    {
        EnsureSeeded();
        return Task.CompletedTask;
    }

    public Task<CrmEntityMetadata?> GetEntityAsync(string logicalName, CancellationToken cancellationToken = default)
    {
        EnsureSeeded();
        return Task.FromResult(_registry.TryGet(logicalName, out var e) ? e : null);
    }

    public Task<IReadOnlyList<CrmEntityMetadata>> GetApprovedEntitiesAsync(CancellationToken cancellationToken = default)
    {
        EnsureSeeded();
        return Task.FromResult(_registry.GetApproved());
    }

    public Task RefreshAsync(string? logicalName = null, CancellationToken cancellationToken = default)
    {
        if (logicalName is null)
        {
            _registry.Clear();
            _loaded = false;
        }

        EnsureSeeded();
        return Task.CompletedTask;
    }

    private void EnsureSeeded()
    {
        if (_loaded && _registry.GetApproved().Count > 0)
        {
            return;
        }

        foreach (var entity in StaticSeedCrmMetadata.CreateApprovedSeeds(_policy))
        {
            _registry.Upsert(entity);
        }

        // Config-only approvals (e.g. lead) get a generic stub — no entity-specific builder code.
        foreach (var approved in _policy.ApprovedLogicalNames)
        {
            if (!_registry.TryGet(approved, out _))
            {
                _registry.Upsert(GenericApprovedEntityStubFactory.Create(approved, _policy));
            }
        }

        _loaded = true;
    }
}
