using CrmAiPoc.Api.Configuration;
using Microsoft.Extensions.Options;

namespace CrmAiPoc.Api.Crm.Metadata;

/// <summary>
/// Loads approved CRM entity metadata at application startup (background).
/// Keeps chat responsive: catalog/query can use the registry cache once warm.
/// </summary>
public sealed class CrmMetadataWarmupHostedService : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly ILogger<CrmMetadataWarmupHostedService> _logger;

    public CrmMetadataWarmupHostedService(
        IServiceProvider services,
        ILogger<CrmMetadataWarmupHostedService> logger)
    {
        _services = services;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Let Kestrel start listening before CRM calls begin.
        await Task.Yield();

        var options = _services.GetRequiredService<IOptions<CrmOptions>>().Value;
        if (!options.WarmApprovedMetadataOnStartup)
        {
            _logger.LogInformation("CRM metadata startup warm-up is disabled (Crm:WarmApprovedMetadataOnStartup=false).");
            return;
        }

        try
        {
            var metadata = _services.GetRequiredService<ICrmMetadataService>();
            var registry = _services.GetRequiredService<CrmMetadataRegistry>();
            var sw = System.Diagnostics.Stopwatch.StartNew();

            await metadata.GetApprovedEntitiesAsync(stoppingToken);
            _logger.LogInformation(
                "CRM metadata seeds ready ({Count} entities). Starting live warm-up…",
                registry.GetApproved().Count);

            await metadata.EnsureApprovedEntitiesLoadedAsync(stoppingToken);

            var approved = registry.GetApproved();
            sw.Stop();

            _logger.LogInformation(
                "CRM metadata warm-up complete in {Ms}ms. Approved entities cached: {Count} [{Names}]",
                sw.ElapsedMilliseconds,
                approved.Count,
                string.Join(", ", approved.Select(e => e.LogicalName)));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            _logger.LogInformation("CRM metadata warm-up canceled (app shutting down).");
        }
        catch (Exception ex)
        {
            // Do not crash the host — chat can still use seeds/stubs / on-demand GetEntityAsync.
            _logger.LogWarning(ex, "CRM metadata warm-up failed; chat will load metadata on demand.");
        }
    }
}
