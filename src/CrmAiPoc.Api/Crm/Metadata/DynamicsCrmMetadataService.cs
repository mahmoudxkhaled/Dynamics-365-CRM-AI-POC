using System.Text.Json;
using CrmAiPoc.Api.Configuration;
using CrmAiPoc.Api.Crm.Dynamics;
using Microsoft.Extensions.Options;

namespace CrmAiPoc.Api.Crm.Metadata;

/// <summary>
/// Discovers approved entity metadata from Dynamics v9.1 EntityDefinitions (cached).
/// Does not load full org schema per chat message.
/// </summary>
public sealed class DynamicsCrmMetadataService : ICrmMetadataService
{
    private readonly DynamicsCrmHttp _http;
    private readonly CrmMetadataRegistry _registry;
    private readonly EntityApprovalPolicy _policy;
    private readonly CrmOptions _options;
    private readonly ILogger<DynamicsCrmMetadataService> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateTimeOffset _lastFullLoadUtc = DateTimeOffset.MinValue;
    private bool _liveDiscoveryCompleted;
    private int _backgroundRefreshRunning;

    public DynamicsCrmMetadataService(
        DynamicsCrmHttp http,
        CrmMetadataRegistry registry,
        EntityApprovalPolicy policy,
        IOptions<CrmOptions> options,
        ILogger<DynamicsCrmMetadataService> logger)
    {
        _http = http;
        _registry = registry;
        _policy = policy;
        _options = options.Value;
        _logger = logger;
    }

    public string GetAiCatalogSummary() => _registry.BuildAiCatalogSummary();

    public async Task EnsureApprovedEntitiesLoadedAsync(CancellationToken cancellationToken = default)
    {
        EnsureSeedsPresent();

        var cacheTtl = TimeSpan.FromSeconds(Math.Max(60, _options.MetadataCacheSeconds));
        var fresh = _liveDiscoveryCompleted &&
                    _registry.GetApproved().Count >= _policy.ApprovedLogicalNames.Count &&
                    DateTimeOffset.UtcNow - _lastFullLoadUtc < cacheTtl;

        if (fresh)
        {
            return;
        }

        // Stale-while-revalidate: keep serving cache/seeds and refresh in background when enabled.
        if (_options.MetadataStaleWhileRevalidate &&
            _liveDiscoveryCompleted &&
            _registry.GetApproved().Count > 0 &&
            Interlocked.CompareExchange(ref _backgroundRefreshRunning, 1, 0) == 0)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await RefreshAsync(null, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Background metadata refresh failed; continuing with stale cache.");
                }
                finally
                {
                    Interlocked.Exchange(ref _backgroundRefreshRunning, 0);
                }
            }, CancellationToken.None);
            return;
        }

        await RefreshAsync(null, cancellationToken);
    }

    public async Task<CrmEntityMetadata?> GetEntityAsync(string logicalName, CancellationToken cancellationToken = default)
    {
        EnsureSeedsPresent();

        if (_registry.TryGet(logicalName, out var cached))
        {
            return cached;
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(8));
        try
        {
            await RefreshAsync(logicalName, cts.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("Metadata refresh timed out for {Entity}; continuing without live schema.", logicalName);
            UpsertFallbackMetadata(logicalName);
        }

        return _registry.TryGet(logicalName, out var entity) ? entity : null;
    }

    public Task<IReadOnlyList<CrmEntityMetadata>> GetApprovedEntitiesAsync(CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        EnsureSeedsPresent();
        return Task.FromResult(_registry.GetApproved());
    }

    public async Task RefreshAsync(string? logicalName = null, CancellationToken cancellationToken = default)
    {
        EnsureSeedsPresent();

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!_options.EnableLiveIntegration || string.IsNullOrWhiteSpace(_options.BaseUrl))
            {
                _liveDiscoveryCompleted = true;
                _lastFullLoadUtc = DateTimeOffset.UtcNow;
                return;
            }

            try
            {
                _http.EnsureLiveConfigured();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Metadata discovery skipped because live CRM is not configured.");
                _liveDiscoveryCompleted = true;
                _lastFullLoadUtc = DateTimeOffset.UtcNow;
                return;
            }

            var targets = string.IsNullOrWhiteSpace(logicalName)
                ? _policy.ApprovedLogicalNames.ToArray()
                : [_policy.IsApproved(logicalName) ? logicalName.Trim() : logicalName.Trim()];

            foreach (var name in targets)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    using var entityCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    entityCts.CancelAfter(TimeSpan.FromSeconds(20));
                    var meta = await DiscoverEntityAsync(name, entityCts.Token);
                    if (meta is not null)
                    {
                        _registry.Upsert(meta);
                    }
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    _logger.LogWarning("Metadata discovery timed out for {Entity}; keeping seed/stub.", name);
                    UpsertFallbackMetadata(name);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Metadata discovery failed for {Entity}; using seed/stub if available.", name);
                    UpsertFallbackMetadata(name);
                }

                if (!_registry.TryGet(name, out _) && _policy.IsApproved(name))
                {
                    UpsertFallbackMetadata(name);
                }
            }

            if (string.IsNullOrWhiteSpace(logicalName))
            {
                _liveDiscoveryCompleted = true;
                _lastFullLoadUtc = DateTimeOffset.UtcNow;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Ensures approved entities exist in the registry immediately (no CRM HTTP).
    /// Does not mark live discovery complete — warm-up still overlays Dynamics metadata.
    /// </summary>
    private void EnsureSeedsPresent()
    {
        if (_registry.GetApproved().Count >= _policy.ApprovedLogicalNames.Count &&
            _policy.ApprovedLogicalNames.All(n => _registry.TryGet(n, out _)))
        {
            return;
        }

        foreach (var entity in StaticSeedCrmMetadata.CreateApprovedSeeds(_policy))
        {
            if (!_registry.TryGet(entity.LogicalName, out _))
            {
                _registry.Upsert(entity);
            }
        }

        foreach (var approved in _policy.ApprovedLogicalNames)
        {
            if (!_registry.TryGet(approved, out _))
            {
                UpsertFallbackMetadata(approved);
            }
        }
    }

    private void UpsertFallbackMetadata(string logicalName)
    {
        var seed = StaticSeedCrmMetadata.CreateApprovedSeeds(_policy)
            .FirstOrDefault(e => e.LogicalName.Equals(logicalName, StringComparison.OrdinalIgnoreCase));
        if (seed is not null)
        {
            _registry.Upsert(seed);
            return;
        }

        if (_policy.IsApproved(logicalName))
        {
            // Config-only entity: no entity-specific C# — generic stub until live discovery succeeds.
            _registry.Upsert(GenericApprovedEntityStubFactory.Create(logicalName, _policy));
        }
    }

    private async Task<CrmEntityMetadata?> DiscoverEntityAsync(string logicalName, CancellationToken cancellationToken)
    {
        var entityPath =
            $"EntityDefinitions(LogicalName='{Escape(logicalName)}')?$select=LogicalName,EntitySetName,PrimaryIdAttribute,PrimaryNameAttribute,DisplayName";
        var entityHttp = await _http.GetAsync(entityPath, cancellationToken);
        if (!entityHttp.IsSuccess)
        {
            _logger.LogWarning("EntityDefinitions failed for {Entity}: {Status}", logicalName, entityHttp.StatusCode);
            return null;
        }

        using var entityDoc = JsonDocument.Parse(entityHttp.Body);
        var root = entityDoc.RootElement;
        var entitySet = GetString(root, "EntitySetName");
        var primaryId = GetString(root, "PrimaryIdAttribute");
        var primaryName = GetString(root, "PrimaryNameAttribute");
        var display = GetLocalizedLabel(root, "DisplayName") ?? logicalName;
        if (string.IsNullOrWhiteSpace(entitySet) || string.IsNullOrWhiteSpace(primaryId) || string.IsNullOrWhiteSpace(primaryName))
        {
            return null;
        }

        var attrsPath =
            $"EntityDefinitions(LogicalName='{Escape(logicalName)}')/Attributes" +
            "?$select=LogicalName,AttributeType,SchemaName,DisplayName,IsValidForRead,IsCustomAttribute,IsValidForGrid" +
            "&$filter=IsValidForRead eq true";
        var attrsHttp = await _http.GetAsync(attrsPath, cancellationToken);
        var attributes = attrsHttp.IsSuccess ? ParseAttributes(attrsHttp.Body) : [];

        await EnrichOptionSetsAsync(logicalName, attributes, cancellationToken);

        var seedRelationships = _registry.TryGet(logicalName, out var existing)
            ? existing.Relationships
            : Array.Empty<CrmRelationshipMetadata>();

        IReadOnlyList<CrmRelationshipMetadata> relationships;
        string? discoveryError = null;
        var liveRelationshipsOk = false;
        try
        {
            relationships = await DiscoverRelationshipsAsync(logicalName, seedRelationships, cancellationToken);
            liveRelationshipsOk = true;
        }
        catch (Exception ex)
        {
            discoveryError = "Relationship discovery failed; using seed fallback.";
            _logger.LogWarning(ex, "Relationship discovery failed for {Entity}; keeping seed relationships.", logicalName);
            relationships = MergeRelationships(seedRelationships, Array.Empty<CrmRelationshipMetadata>(), liveSucceeded: false);
        }

        relationships = EnrichRelationshipLabels(relationships, attributes);

        var approved = _policy.IsApproved(logicalName);
        return new CrmEntityMetadata
        {
            LogicalName = logicalName,
            EntitySetName = entitySet,
            DisplayName = display,
            PrimaryIdAttribute = primaryId,
            PrimaryNameAttribute = primaryName,
            IsApprovedForAi = approved,
            Aliases = _policy.GetAliases(logicalName),
            Attributes = attributes,
            Relationships = relationships,
            DiscoveredAtUtc = DateTimeOffset.UtcNow,
            Provenance = CrmMetadataProvenance.Live,
            IsLiveVerified = liveRelationshipsOk,
            DiscoveryError = discoveryError
        };
    }

    /// <summary>
    /// Discovers ManyToOne / OneToMany / ManyToMany metadata for an approved entity.
    /// Many-to-many is retained for catalog visibility but never approved for joins in this phase.
    /// </summary>
    private async Task<IReadOnlyList<CrmRelationshipMetadata>> DiscoverRelationshipsAsync(
        string logicalName,
        IReadOnlyList<CrmRelationshipMetadata> seedRelationships,
        CancellationToken cancellationToken)
    {
        // Unfiltered relationship collections on Dynamics On-Prem are huge and can hang chat.
        // Bound discovery tightly and keep seeds as the safe fallback.
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(8));

        try
        {
            var discovered = new List<CrmRelationshipMetadata>();
            var approvedFilter = BuildApprovedEntityODataFilter();

            var manyToOnePath =
                $"EntityDefinitions(LogicalName='{Escape(logicalName)}')/ManyToOneRelationships" +
                "?$select=SchemaName,ReferencingEntity,ReferencedEntity,ReferencingAttribute,ReferencedAttribute," +
                "ReferencingEntityNavigationPropertyName,ReferencedEntityNavigationPropertyName" +
                approvedFilter;
            var oneToManyPath =
                $"EntityDefinitions(LogicalName='{Escape(logicalName)}')/OneToManyRelationships" +
                "?$select=SchemaName,ReferencingEntity,ReferencedEntity,ReferencingAttribute,ReferencedAttribute," +
                "ReferencingEntityNavigationPropertyName,ReferencedEntityNavigationPropertyName" +
                approvedFilter;
            // Skip ManyToMany live GET — unsupported for joins and often the largest payload.

            var m2o = await _http.GetAsync(manyToOnePath, cts.Token);
            if (m2o.IsSuccess)
            {
                discovered.AddRange(ParseLookupRelationships(m2o.Body, CrmRelationshipKind.ManyToOne));
            }

            var o2m = await _http.GetAsync(oneToManyPath, cts.Token);
            if (o2m.IsSuccess)
            {
                discovered.AddRange(ParseLookupRelationships(o2m.Body, CrmRelationshipKind.OneToMany));
            }

            return MergeRelationships(seedRelationships, discovered, liveSucceeded: true);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(
                "Relationship discovery timed out for {Entity}; using seed relationships only.",
                logicalName);
            return MergeRelationships(seedRelationships, Array.Empty<CrmRelationshipMetadata>(), liveSucceeded: false);
        }
    }

    /// <summary>
    /// Limits relationship GETs to rows touching at least one approved entity (On-Prem safe filter).
    /// </summary>
    private string BuildApprovedEntityODataFilter()
    {
        var names = _policy.ApprovedLogicalNames
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Select(n => n.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(12)
            .ToArray();
        if (names.Length == 0)
        {
            return string.Empty;
        }

        var parts = names.SelectMany(n =>
        {
            var lit = Escape(n);
            return new[]
            {
                $"ReferencingEntity eq '{lit}'",
                $"ReferencedEntity eq '{lit}'"
            };
        });
        return "&$filter=" + Uri.EscapeDataString(string.Join(" or ", parts));
    }

    private IReadOnlyList<CrmRelationshipMetadata> MergeRelationships(
        IReadOnlyList<CrmRelationshipMetadata> seeds,
        IReadOnlyList<CrmRelationshipMetadata> discovered,
        bool liveSucceeded)
    {
        var bySchema = new Dictionary<string, CrmRelationshipMetadata>(StringComparer.OrdinalIgnoreCase);
        var liveSchemas = new HashSet<string>(
            discovered.Select(d => d.SchemaName),
            StringComparer.OrdinalIgnoreCase);

        CrmRelationshipMetadata Materialize(CrmRelationshipMetadata rel, bool fromLive)
        {
            var policyOk = rel.Kind != CrmRelationshipKind.ManyToMany &&
                           _policy.IsRelationshipApproved(rel.SchemaName, rel.ReferencingEntity, rel.ReferencedEntity);

            // When live discovery succeeded, only live-present relationships are authoritative/joinable.
            // Seed-only leftovers remain visible but not approved (unless live failed → SeedFallback).
            var approved = liveSucceeded
                ? fromLive && policyOk
                : policyOk && (rel.IsApproved || fromLive);

            string? configuredLabel = null;
            if (_options.RelationshipLabels is not null &&
                _options.RelationshipLabels.TryGetValue(rel.SchemaName, out var lbl))
            {
                configuredLabel = lbl;
            }

            return new CrmRelationshipMetadata
            {
                SchemaName = rel.SchemaName,
                Kind = rel.Kind,
                ReferencingEntity = rel.ReferencingEntity,
                ReferencedEntity = rel.ReferencedEntity,
                ReferencingAttribute = rel.ReferencingAttribute,
                ReferencedAttribute = rel.ReferencedAttribute,
                ReferencingEntityNavigationPropertyName = rel.ReferencingEntityNavigationPropertyName,
                ReferencedEntityNavigationPropertyName = rel.ReferencedEntityNavigationPropertyName,
                ReferencingAttributeDisplayName = rel.ReferencingAttributeDisplayName,
                BusinessLabel = Prefer(rel.BusinessLabel, configuredLabel),
                IsApproved = approved,
                Provenance = fromLive
                    ? CrmMetadataProvenance.Live
                    : liveSucceeded
                        ? CrmMetadataProvenance.Seed
                        : CrmMetadataProvenance.SeedFallback
            };
        }

        foreach (var seed in seeds)
        {
            if (string.IsNullOrWhiteSpace(seed.SchemaName))
            {
                continue;
            }

            // If live succeeded and this schema was also discovered, skip seed — live wins below.
            if (liveSucceeded && liveSchemas.Contains(seed.SchemaName))
            {
                continue;
            }

            bySchema[seed.SchemaName] = Materialize(seed, fromLive: false);
        }

        foreach (var live in discovered)
        {
            if (string.IsNullOrWhiteSpace(live.SchemaName))
            {
                continue;
            }

            var next = Materialize(live, fromLive: true);
            if (bySchema.TryGetValue(next.SchemaName, out var existing))
            {
                bySchema[next.SchemaName] = new CrmRelationshipMetadata
                {
                    SchemaName = next.SchemaName,
                    Kind = next.Kind != default ? next.Kind : existing.Kind,
                    ReferencingEntity = Prefer(next.ReferencingEntity, existing.ReferencingEntity),
                    ReferencedEntity = Prefer(next.ReferencedEntity, existing.ReferencedEntity),
                    ReferencingAttribute = Prefer(next.ReferencingAttribute, existing.ReferencingAttribute),
                    ReferencedAttribute = Prefer(next.ReferencedAttribute, existing.ReferencedAttribute),
                    ReferencingEntityNavigationPropertyName = Prefer(
                        next.ReferencingEntityNavigationPropertyName,
                        existing.ReferencingEntityNavigationPropertyName),
                    ReferencedEntityNavigationPropertyName = Prefer(
                        next.ReferencedEntityNavigationPropertyName,
                        existing.ReferencedEntityNavigationPropertyName),
                    ReferencingAttributeDisplayName = Prefer(
                        next.ReferencingAttributeDisplayName,
                        existing.ReferencingAttributeDisplayName),
                    BusinessLabel = Prefer(next.BusinessLabel, existing.BusinessLabel),
                    IsApproved = next.IsApproved,
                    Provenance = CrmMetadataProvenance.Live
                };
            }
            else
            {
                bySchema[next.SchemaName] = next;
            }
        }

        return bySchema.Values
            .OrderByDescending(r => r.IsApproved)
            .ThenBy(r => r.SchemaName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private IReadOnlyList<CrmRelationshipMetadata> EnrichRelationshipLabels(
        IReadOnlyList<CrmRelationshipMetadata> relationships,
        IReadOnlyList<CrmAttributeMetadata> attributes)
    {
        return relationships.Select(r =>
        {
            string? attrDisplay = r.ReferencingAttributeDisplayName;
            if (string.IsNullOrWhiteSpace(attrDisplay) &&
                !string.IsNullOrWhiteSpace(r.ReferencingAttribute))
            {
                var attr = attributes.FirstOrDefault(a =>
                    a.LogicalName.Equals(r.ReferencingAttribute, StringComparison.OrdinalIgnoreCase));
                attrDisplay = attr?.DisplayName;
            }

            string? business = r.BusinessLabel;
            if (string.IsNullOrWhiteSpace(business) &&
                _options.RelationshipLabels is not null &&
                _options.RelationshipLabels.TryGetValue(r.SchemaName, out var configured))
            {
                business = configured;
            }

            return new CrmRelationshipMetadata
            {
                SchemaName = r.SchemaName,
                Kind = r.Kind,
                ReferencingEntity = r.ReferencingEntity,
                ReferencedEntity = r.ReferencedEntity,
                ReferencingAttribute = r.ReferencingAttribute,
                ReferencedAttribute = r.ReferencedAttribute,
                ReferencingEntityNavigationPropertyName = r.ReferencingEntityNavigationPropertyName,
                ReferencedEntityNavigationPropertyName = r.ReferencedEntityNavigationPropertyName,
                ReferencingAttributeDisplayName = attrDisplay,
                BusinessLabel = business,
                IsApproved = r.IsApproved,
                Provenance = r.Provenance
            };
        }).ToArray();
    }

    private static string Prefer(string? preferred, string? fallback) =>
        !string.IsNullOrWhiteSpace(preferred) ? preferred! : (fallback ?? string.Empty);

    private List<CrmRelationshipMetadata> ParseLookupRelationships(string json, CrmRelationshipKind kind)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("value", out var values))
        {
            return [];
        }

        var list = new List<CrmRelationshipMetadata>();
        foreach (var item in values.EnumerateArray())
        {
            var schema = GetString(item, "SchemaName");
            var referencing = GetString(item, "ReferencingEntity");
            var referenced = GetString(item, "ReferencedEntity");
            if (string.IsNullOrWhiteSpace(schema) ||
                string.IsNullOrWhiteSpace(referencing) ||
                string.IsNullOrWhiteSpace(referenced))
            {
                continue;
            }

            // Keep only relationships that touch at least one approved entity (compact catalog).
            if (!_policy.IsApproved(referencing) && !_policy.IsApproved(referenced))
            {
                continue;
            }

            list.Add(new CrmRelationshipMetadata
            {
                SchemaName = schema,
                Kind = kind,
                ReferencingEntity = referencing,
                ReferencedEntity = referenced,
                ReferencingAttribute = GetString(item, "ReferencingAttribute"),
                ReferencedAttribute = GetString(item, "ReferencedAttribute"),
                ReferencingEntityNavigationPropertyName = GetString(item, "ReferencingEntityNavigationPropertyName"),
                ReferencedEntityNavigationPropertyName = GetString(item, "ReferencedEntityNavigationPropertyName"),
                IsApproved = false // MergeRelationships applies allowlist
            });
        }

        return list;
    }

    private List<CrmRelationshipMetadata> ParseManyToManyRelationships(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("value", out var values))
        {
            return [];
        }

        var list = new List<CrmRelationshipMetadata>();
        foreach (var item in values.EnumerateArray())
        {
            var schema = GetString(item, "SchemaName");
            var e1 = GetString(item, "Entity1LogicalName");
            var e2 = GetString(item, "Entity2LogicalName");
            if (string.IsNullOrWhiteSpace(schema) ||
                string.IsNullOrWhiteSpace(e1) ||
                string.IsNullOrWhiteSpace(e2))
            {
                continue;
            }

            if (!_policy.IsApproved(e1) && !_policy.IsApproved(e2))
            {
                continue;
            }

            list.Add(new CrmRelationshipMetadata
            {
                SchemaName = schema,
                Kind = CrmRelationshipKind.ManyToMany,
                ReferencingEntity = e1,
                ReferencedEntity = e2,
                ReferencingEntityNavigationPropertyName = GetString(item, "Entity1NavigationPropertyName"),
                ReferencedEntityNavigationPropertyName = GetString(item, "Entity2NavigationPropertyName"),
                IsApproved = false
            });
        }

        return list;
    }

    private async Task EnrichOptionSetsAsync(
        string logicalName,
        List<CrmAttributeMetadata> attributes,
        CancellationToken cancellationToken)
    {
        // statecode/statuscode only — full picklist enrichment is too chatty against Dynamics.
        var candidates = attributes
            .Where(a =>
                a.LogicalName.Equals("statecode", StringComparison.OrdinalIgnoreCase) ||
                a.LogicalName.Equals("statuscode", StringComparison.OrdinalIgnoreCase))
            .Where(a => a.Kind is CrmAttributeKind.Status or CrmAttributeKind.State or CrmAttributeKind.Picklist)
            .Take(2)
            .ToList();

        foreach (var attr in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var metaType = attr.Kind == CrmAttributeKind.State
                    ? "StateAttributeMetadata"
                    : "StatusAttributeMetadata";
                var path =
                    $"EntityDefinitions(LogicalName='{Escape(logicalName)}')" +
                    $"/Attributes(LogicalName='{Escape(attr.LogicalName)}')" +
                    $"/Microsoft.Dynamics.CRM.{metaType}?$select=LogicalName&$expand=OptionSet($select=Options)";
                var http = await _http.GetAsync(path, cancellationToken);
                if (!http.IsSuccess)
                {
                    continue;
                }

                using var doc = JsonDocument.Parse(http.Body);
                if (!doc.RootElement.TryGetProperty("OptionSet", out var optionSet) ||
                    !optionSet.TryGetProperty("Options", out var options))
                {
                    continue;
                }

                var byLabel = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                var byValue = new Dictionary<int, string>();
                foreach (var opt in options.EnumerateArray())
                {
                    if (!opt.TryGetProperty("Value", out var valueProp) || !valueProp.TryGetInt32(out var value))
                    {
                        continue;
                    }

                    var label = GetLocalizedLabel(opt, "Label") ?? value.ToString();
                    byLabel[label] = value;
                    byValue[value] = label;
                }

                var idx = attributes.FindIndex(a => a.LogicalName.Equals(attr.LogicalName, StringComparison.OrdinalIgnoreCase));
                if (idx >= 0)
                {
                    var old = attributes[idx];
                    attributes[idx] = new CrmAttributeMetadata
                    {
                        LogicalName = old.LogicalName,
                        ODataPropertyName = old.ODataPropertyName,
                        DisplayName = old.DisplayName,
                        Kind = old.Kind,
                        AttributeTypeName = old.AttributeTypeName,
                        IsValidForRead = old.IsValidForRead,
                        IsFilterable = old.IsFilterable,
                        IsSortable = old.IsSortable,
                        IsSearchable = old.IsSearchable,
                        IsCustom = old.IsCustom,
                        Aliases = old.Aliases,
                        AllowedOperators = old.AllowedOperators,
                        OptionSetByLabel = byLabel,
                        OptionSetByValue = byValue
                    };
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "OptionSet enrich failed for {Entity}.{Attr}", logicalName, attr.LogicalName);
            }
        }
    }

    private static List<CrmAttributeMetadata> ParseAttributes(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("value", out var values))
        {
            return [];
        }

        var list = new List<CrmAttributeMetadata>();
        foreach (var item in values.EnumerateArray())
        {
            var logical = GetString(item, "LogicalName");
            if (string.IsNullOrWhiteSpace(logical))
            {
                continue;
            }

            var typeName = GetString(item, "AttributeType") ?? "Unknown";
            var kind = AttributeKindMapper.FromDynamicsType(typeName);
            if (kind == CrmAttributeKind.Unknown)
            {
                continue;
            }

            // Skip large / unsafe attribute families for AI allowlist compactness.
            if (logical.StartsWith("traversedpath", StringComparison.OrdinalIgnoreCase) ||
                logical.Contains("yominame", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var isRead = !item.TryGetProperty("IsValidForRead", out var readProp) || readProp.ValueKind != JsonValueKind.False;
            list.Add(new CrmAttributeMetadata
            {
                LogicalName = logical,
                ODataPropertyName = AttributeKindMapper.ToODataPropertyName(logical, kind),
                DisplayName = GetLocalizedLabel(item, "DisplayName"),
                Kind = kind,
                AttributeTypeName = typeName,
                IsValidForRead = isRead,
                IsFilterable = AttributeKindMapper.IsTypicallyFilterable(kind),
                IsSortable = AttributeKindMapper.IsTypicallySortable(kind),
                IsSearchable = AttributeKindMapper.IsSearchable(kind),
                IsCustom = item.TryGetProperty("IsCustomAttribute", out var custom) && custom.ValueKind == JsonValueKind.True,
                AllowedOperators = AttributeKindMapper.DefaultOperators(kind)
            });
        }

        return list;
    }

    private static string Escape(string logicalName) =>
        logicalName.Replace("'", "''", StringComparison.Ordinal);

    private static string? GetString(JsonElement el, string name) =>
        el.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    private static string? GetLocalizedLabel(JsonElement el, string propertyName)
    {
        if (!el.TryGetProperty(propertyName, out var labelProp) ||
            labelProp.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        if (labelProp.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (labelProp.TryGetProperty("LocalizedLabels", out var localizedLabels) &&
            localizedLabels.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in localizedLabels.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                if (entry.TryGetProperty("LanguageCode", out var code) &&
                    code.ValueKind == JsonValueKind.Number &&
                    code.GetInt32() == 1033 &&
                    entry.TryGetProperty("Label", out var enLabel) &&
                    enLabel.ValueKind == JsonValueKind.String)
                {
                    return enLabel.GetString();
                }
            }
        }

        if (labelProp.TryGetProperty("UserLocalizedLabel", out var ull) &&
            ull.ValueKind == JsonValueKind.Object &&
            ull.TryGetProperty("Label", out var label) &&
            label.ValueKind == JsonValueKind.String)
        {
            return label.GetString();
        }

        return null;
    }
}
