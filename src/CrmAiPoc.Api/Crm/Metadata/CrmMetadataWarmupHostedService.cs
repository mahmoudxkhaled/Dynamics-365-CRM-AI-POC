using CrmAiPoc.Api.Configuration;
using Microsoft.Extensions.Options;

namespace CrmAiPoc.Api.Crm.Metadata;

/// <summary>
/// Loads approved CRM entity metadata from Dynamics at application startup (background).
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
            var policy = _services.GetRequiredService<EntityApprovalPolicy>();
            var sw = System.Diagnostics.Stopwatch.StartNew();

            _logger.LogInformation(
                "Starting live CRM metadata warm-up for {Count} approved entities: [{Names}]",
                policy.ApprovedLogicalNames.Count,
                string.Join(", ", policy.ApprovedLogicalNames));

            await metadata.EnsureApprovedEntitiesLoadedAsync(stoppingToken);

            var approved = registry.GetApproved()
                .Where(e => e.Provenance == CrmMetadataProvenance.Live && e.Attributes.Count > 0)
                .ToArray();
            var missing = policy.ApprovedLogicalNames
                .Where(n => approved.All(e => !e.LogicalName.Equals(n, StringComparison.OrdinalIgnoreCase)))
                .ToArray();
            sw.Stop();

            _logger.LogInformation(
                "CRM metadata warm-up finished in {Ms}ms. LiveCached={LiveCount}/{ApprovedCount} [{LiveNames}]. Missing=[{Missing}]",
                sw.ElapsedMilliseconds,
                approved.Length,
                policy.ApprovedLogicalNames.Count,
                approved.Length == 0 ? "(none)" : string.Join(", ", approved.Select(e => e.LogicalName)),
                missing.Length == 0 ? "(none)" : string.Join(", ", missing));

            if (missing.Length > 0)
            {
                _logger.LogWarning(
                    "Warm-up did not cache live metadata for: [{Missing}]. Queries for those entities will fail until discovery succeeds.",
                    string.Join(", ", missing));
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            _logger.LogInformation("CRM metadata warm-up canceled (app shutting down).");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "CRM metadata warm-up failed; chat will attempt on-demand live discovery.");
        }
    }
}
