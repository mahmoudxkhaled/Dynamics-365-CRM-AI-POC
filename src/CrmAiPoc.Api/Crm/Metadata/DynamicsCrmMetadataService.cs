using System.Collections.Concurrent;
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
    private readonly ConcurrentDictionary<string, DateTimeOffset> _failedDiscoveryUtc =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, (DateTimeOffset At, string Reason)> _optionSetFailures =
        new(StringComparer.OrdinalIgnoreCase);
    private DateTimeOffset _lastFullLoadUtc = DateTimeOffset.MinValue;
    private bool _fullRefreshAttempted;
    private int _backgroundRefreshRunning;
    private static readonly TimeSpan DiscoveryFailureCooldown = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan OptionSetFailureCooldown = TimeSpan.FromMinutes(5);

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
        var cacheTtl = TimeSpan.FromSeconds(Math.Max(60, _options.MetadataCacheSeconds));
        var allLive = _policy.ApprovedLogicalNames.All(IsLiveCached);
        var fresh = _fullRefreshAttempted &&
                    allLive &&
                    DateTimeOffset.UtcNow - _lastFullLoadUtc < cacheTtl;

        if (fresh)
        {
            return;
        }

        // Stale-while-revalidate: serve existing live cache while refreshing in background.
        if (_options.MetadataStaleWhileRevalidate &&
            _fullRefreshAttempted &&
            _registry.GetApproved().Any(e => e.Provenance == CrmMetadataProvenance.Live) &&
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
                    _logger.LogWarning(ex, "Background metadata refresh failed; continuing with live cache.");
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
        var name = logicalName.Trim();
        if (_registry.TryGet(name, out var cached) &&
            cached.Provenance == CrmMetadataProvenance.Live &&
            cached.Attributes.Count > 0)
        {
            return cached;
        }

        if (IsInFailureCooldown(name))
        {
            _logger.LogWarning(
                "Skipping Dynamics metadata discovery for {Entity}; recent failure cooldown active.",
                name);
            return null;
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(25));
        try
        {
            await RefreshAsync(name, cts.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("Metadata refresh timed out for {Entity}.", name);
            RememberDiscoveryFailure(name);
            return null;
        }

        return _registry.TryGet(name, out var entity) &&
               entity.Provenance == CrmMetadataProvenance.Live &&
               entity.Attributes.Count > 0
            ? entity
            : null;
    }

    public Task<IReadOnlyList<CrmEntityMetadata>> GetApprovedEntitiesAsync(CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        // Live cache only — never invent entities that Dynamics has not returned.
        return Task.FromResult(_registry.GetApproved());
    }

    public async Task<AttributeOptionsResult> EnsureAttributeOptionsAsync(
        string entityLogicalName,
        string attributeLogicalName,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(entityLogicalName) || string.IsNullOrWhiteSpace(attributeLogicalName))
        {
            return new AttributeOptionsResult
            {
                Error = "Entity and attribute logical names are required."
            };
        }

        var entity = await GetEntityAsync(entityLogicalName, cancellationToken);
        if (entity is null || !entity.TryGetAttribute(attributeLogicalName, out var attr))
        {
            return new AttributeOptionsResult
            {
                Error = $"Attribute '{attributeLogicalName}' was not found on approved entity '{entityLogicalName}'."
            };
        }

        if (!IsChoiceAttributeKind(attr.Kind))
        {
            return new AttributeOptionsResult
            {
                Attribute = attr,
                OptionsAvailable = false
            };
        }

        if (attr.OptionSetByValue.Count > 0 || attr.OptionSetByLabel.Count > 0)
        {
            return new AttributeOptionsResult
            {
                Attribute = attr,
                OptionsAvailable = true,
                AlreadyCached = true
            };
        }

        var failureKey = OptionSetFailureKey(entity.LogicalName, attr.LogicalName);
        if (_optionSetFailures.TryGetValue(failureKey, out var prior) &&
            DateTimeOffset.UtcNow - prior.At < OptionSetFailureCooldown)
        {
            _logger.LogInformation(
                "[OPTIONSET] SkipRetry Entity={Entity} Attribute={Attribute} Reason={Reason} CooldownRemainingSec={Sec}",
                entity.LogicalName,
                attr.LogicalName,
                TruncateForLog(prior.Reason, 160),
                (int)(OptionSetFailureCooldown - (DateTimeOffset.UtcNow - prior.At)).TotalSeconds);

            return new AttributeOptionsResult
            {
                Attribute = attr,
                OptionsAvailable = false,
                Error = prior.Reason
            };
        }

        var loaded = await LoadAttributeOptionsAsync(entity.LogicalName, attr, cancellationToken);
        if (!loaded.Success || loaded.Attribute is null)
        {
            var reason = loaded.Error ?? "OptionSet enrichment failed.";
            _optionSetFailures[failureKey] = (DateTimeOffset.UtcNow, reason);
            return new AttributeOptionsResult
            {
                Attribute = attr,
                OptionsAvailable = false,
                Error = reason,
                RequestPath = loaded.RequestPath
            };
        }

        _optionSetFailures.TryRemove(failureKey, out _);

        // Persist enriched options into the in-memory registry for subsequent requests.
        var enriched = loaded.Attribute;
        var updatedAttributes = entity.Attributes
            .Select(a =>
                a.LogicalName.Equals(enriched.LogicalName, StringComparison.OrdinalIgnoreCase)
                    ? enriched
                    : a)
            .ToArray();
        _registry.Upsert(new CrmEntityMetadata
        {
            LogicalName = entity.LogicalName,
            EntitySetName = entity.EntitySetName,
            DisplayName = entity.DisplayName,
            PrimaryIdAttribute = entity.PrimaryIdAttribute,
            PrimaryNameAttribute = entity.PrimaryNameAttribute,
            IsApprovedForAi = entity.IsApprovedForAi,
            Aliases = entity.Aliases,
            Attributes = updatedAttributes,
            Relationships = entity.Relationships,
            DiscoveredAtUtc = entity.DiscoveredAtUtc,
            Provenance = entity.Provenance,
            IsLiveVerified = entity.IsLiveVerified,
            DiscoveryError = entity.DiscoveryError
        });

        return new AttributeOptionsResult
        {
            Attribute = enriched,
            OptionsAvailable = true,
            AlreadyCached = false,
            RequestPath = loaded.RequestPath
        };
    }

    public async Task RefreshAsync(string? logicalName = null, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!_options.EnableLiveIntegration || string.IsNullOrWhiteSpace(_options.BaseUrl))
            {
                _logger.LogWarning(
                    "Dynamics metadata refresh skipped: EnableLiveIntegration={Enabled}, BaseUrlConfigured={HasBaseUrl}.",
                    _options.EnableLiveIntegration,
                    !string.IsNullOrWhiteSpace(_options.BaseUrl));
                if (string.IsNullOrWhiteSpace(logicalName))
                {
                    _fullRefreshAttempted = true;
                    _lastFullLoadUtc = DateTimeOffset.UtcNow;
                }

                return;
            }

            try
            {
                _http.EnsureLiveConfigured();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Metadata discovery skipped because live CRM is not configured.");
                if (string.IsNullOrWhiteSpace(logicalName))
                {
                    _fullRefreshAttempted = true;
                    _lastFullLoadUtc = DateTimeOffset.UtcNow;
                }

                return;
            }

            var targets = string.IsNullOrWhiteSpace(logicalName)
                ? _policy.ApprovedLogicalNames.ToArray()
                : [logicalName.Trim()];

            foreach (var name in targets)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!_policy.IsApproved(name))
                {
                    _logger.LogWarning("Skipping metadata discovery for unapproved entity {Entity}.", name);
                    continue;
                }

                try
                {
                    using var entityCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    entityCts.CancelAfter(TimeSpan.FromSeconds(20));
                    var meta = await DiscoverEntityAsync(name, entityCts.Token);
                    if (meta is not null)
                    {
                        _registry.Upsert(meta);
                        _failedDiscoveryUtc.TryRemove(name, out _);
                        _logger.LogInformation(
                            "Dynamics metadata discovered for {Entity}: attributes={AttrCount}, relationships={RelCount}, liveVerified={LiveVerified}",
                            name,
                            meta.Attributes.Count,
                            meta.Relationships.Count,
                            meta.IsLiveVerified);
                    }
                    else
                    {
                        RememberDiscoveryFailure(name);
                        _logger.LogWarning(
                            "Dynamics metadata discovery returned no usable schema for {Entity}; cache entry not created.",
                            name);
                    }
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    RememberDiscoveryFailure(name);
                    _logger.LogWarning("Metadata discovery timed out for {Entity}; no fabricated fallback cached.", name);
                }
                catch (Exception ex)
                {
                    RememberDiscoveryFailure(name);
                    _logger.LogWarning(ex, "Metadata discovery failed for {Entity}; no fabricated fallback cached.", name);
                }
            }

            if (string.IsNullOrWhiteSpace(logicalName))
            {
                _fullRefreshAttempted = true;
                _lastFullLoadUtc = DateTimeOffset.UtcNow;
                var liveNames = _registry.GetApproved().Select(e => e.LogicalName).ToArray();
                var missing = _policy.ApprovedLogicalNames
                    .Where(n => !liveNames.Contains(n, StringComparer.OrdinalIgnoreCase))
                    .ToArray();
                _logger.LogInformation(
                    "Dynamics metadata full refresh finished. LiveCached={LiveCount}/{ApprovedCount}. Missing=[{Missing}]",
                    liveNames.Length,
                    _policy.ApprovedLogicalNames.Count,
                    missing.Length == 0 ? "(none)" : string.Join(", ", missing));
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private bool IsLiveCached(string logicalName) =>
        _registry.TryGet(logicalName, out var entity) &&
        entity.Provenance == CrmMetadataProvenance.Live &&
        entity.Attributes.Count > 0;

    private bool IsInFailureCooldown(string logicalName) =>
        _failedDiscoveryUtc.TryGetValue(logicalName, out var at) &&
        DateTimeOffset.UtcNow - at < DiscoveryFailureCooldown;

    private void RememberDiscoveryFailure(string logicalName) =>
        _failedDiscoveryUtc[logicalName] = DateTimeOffset.UtcNow;

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
            "?$select=LogicalName,AttributeType,SchemaName,DisplayName,Description,IsValidForRead,IsCustomAttribute,IsValidForGrid" +
            "&$filter=IsValidForRead eq true";
        var attrsHttp = await _http.GetAsync(attrsPath, cancellationToken);
        if (!attrsHttp.IsSuccess)
        {
            _logger.LogWarning(
                "Attributes discovery failed for {Entity}: {Status}",
                logicalName,
                attrsHttp.StatusCode);
            return null;
        }

        var attributes = ParseAttributes(attrsHttp.Body);
        if (attributes.Count == 0)
        {
            _logger.LogWarning("Attributes discovery returned no usable attributes for {Entity}.", logicalName);
            return null;
        }

        await EnrichOptionSetsAsync(logicalName, attributes, cancellationToken);

        var previousRelationships = _registry.TryGet(logicalName, out var existing)
            ? existing.Relationships
            : Array.Empty<CrmRelationshipMetadata>();

        IReadOnlyList<CrmRelationshipMetadata> relationships;
        string? discoveryError = null;
        var liveRelationshipsOk = false;
        try
        {
            relationships = await DiscoverRelationshipsAsync(logicalName, previousRelationships, cancellationToken);
            liveRelationshipsOk = true;
        }
        catch (Exception ex)
        {
            discoveryError = "Relationship discovery failed; entity attributes were still loaded from Dynamics.";
            _logger.LogWarning(ex, "Relationship discovery failed for {Entity}; keeping previously cached live relationships if any.", logicalName);
            relationships = MergeRelationships(previousRelationships, Array.Empty<CrmRelationshipMetadata>(), liveSucceeded: false);
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
        IReadOnlyList<CrmRelationshipMetadata> previousRelationships,
        CancellationToken cancellationToken)
    {
        // Unfiltered relationship collections on Dynamics On-Prem are huge and can hang chat.
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(8));

        try
        {
            var discovered = new List<CrmRelationshipMetadata>();
            // Collection navigation is already scoped to this entity. Filter only the opposite
            // endpoint so the OData predicate depth stays under Dynamics' limit of 25.
            // (A dual-role OR per approved entity exceeds that limit at 13+ approved entities.)
            var manyToOneFilter = BuildApprovedEntityODataFilter(oppositeEndpointProperty: "ReferencedEntity");
            var oneToManyFilter = BuildApprovedEntityODataFilter(oppositeEndpointProperty: "ReferencingEntity");

            var manyToOnePath =
                $"EntityDefinitions(LogicalName='{Escape(logicalName)}')/ManyToOneRelationships" +
                "?$select=SchemaName,ReferencingEntity,ReferencedEntity,ReferencingAttribute,ReferencedAttribute," +
                "ReferencingEntityNavigationPropertyName,ReferencedEntityNavigationPropertyName" +
                manyToOneFilter;
            var oneToManyPath =
                $"EntityDefinitions(LogicalName='{Escape(logicalName)}')/OneToManyRelationships" +
                "?$select=SchemaName,ReferencingEntity,ReferencedEntity,ReferencingAttribute,ReferencedAttribute," +
                "ReferencingEntityNavigationPropertyName,ReferencedEntityNavigationPropertyName" +
                oneToManyFilter;
            // Skip ManyToMany live GET — unsupported for joins and often the largest payload.

            var m2o = await _http.GetAsync(manyToOnePath, cts.Token);
            if (m2o.IsSuccess)
            {
                discovered.AddRange(ParseLookupRelationships(m2o.Body, CrmRelationshipKind.ManyToOne));
            }
            else
            {
                _logger.LogWarning(
                    "ManyToOneRelationships failed for {Entity}: Status={Status}",
                    logicalName,
                    m2o.StatusCode);
            }

            var o2m = await _http.GetAsync(oneToManyPath, cts.Token);
            if (o2m.IsSuccess)
            {
                discovered.AddRange(ParseLookupRelationships(o2m.Body, CrmRelationshipKind.OneToMany));
            }
            else
            {
                _logger.LogWarning(
                    "OneToManyRelationships failed for {Entity}: Status={Status}",
                    logicalName,
                    o2m.StatusCode);
            }

            // Only treat discovery as live-succeeded when at least one collection GET worked.
            // Otherwise keep previously cached live relationships instead of wiping them.
            var liveSucceeded = m2o.IsSuccess || o2m.IsSuccess;
            return MergeRelationships(previousRelationships, discovered, liveSucceeded);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(
                "Relationship discovery timed out for {Entity}; keeping previously cached live relationships if any.",
                logicalName);
            return MergeRelationships(previousRelationships, Array.Empty<CrmRelationshipMetadata>(), liveSucceeded: false);
        }
    }

    /// <summary>
    /// Limits relationship GETs to rows whose opposite endpoint is an approved entity.
    /// Dynamics EntityRelationship queries reject filters deeper than 25 predicates.
    /// </summary>
    private string BuildApprovedEntityODataFilter(string oppositeEndpointProperty)
    {
        var names = _policy.ApprovedLogicalNames
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Select(n => n.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (names.Length == 0)
        {
            return string.Empty;
        }

        // Dynamics error 0x80044183: max supported filter depth is 25. Fall back to an
        // unfiltered collection GET; ParseLookupRelationships still drops non-approved endpoints.
        const int maxDynamicsRelationshipFilterDepth = 25;
        if (names.Length >= maxDynamicsRelationshipFilterDepth)
        {
            _logger.LogWarning(
                "Approved entity count {Count} reaches Dynamics relationship filter depth limit {Limit}; " +
                "requesting unfiltered {Property} collection and filtering client-side.",
                names.Length,
                maxDynamicsRelationshipFilterDepth,
                oppositeEndpointProperty);
            return string.Empty;
        }

        var parts = names.Select(n => $"{oppositeEndpointProperty} eq '{Escape(n)}'");
        return "&$filter=" + Uri.EscapeDataString(string.Join(" or ", parts));
    }

    private IReadOnlyList<CrmRelationshipMetadata> MergeRelationships(
        IReadOnlyList<CrmRelationshipMetadata> previous,
        IReadOnlyList<CrmRelationshipMetadata> discovered,
        bool liveSucceeded)
    {
        if (!liveSucceeded)
        {
            // Keep previously cached live relationships only — never invent schema.
            return previous
                .Where(r => r.Provenance == CrmMetadataProvenance.Live)
                .OrderByDescending(r => r.IsApproved)
                .ThenBy(r => r.SchemaName, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        var bySchema = new Dictionary<string, CrmRelationshipMetadata>(StringComparer.OrdinalIgnoreCase);
        foreach (var live in discovered)
        {
            if (string.IsNullOrWhiteSpace(live.SchemaName))
            {
                continue;
            }

            var policyOk = live.Kind != CrmRelationshipKind.ManyToMany &&
                           _policy.IsRelationshipApproved(live.SchemaName, live.ReferencingEntity, live.ReferencedEntity);

            string? configuredLabel = null;
            if (_options.RelationshipLabels is not null &&
                _options.RelationshipLabels.TryGetValue(live.SchemaName, out var lbl))
            {
                configuredLabel = lbl;
            }

            bySchema[live.SchemaName] = new CrmRelationshipMetadata
            {
                SchemaName = live.SchemaName,
                Kind = live.Kind,
                ReferencingEntity = live.ReferencingEntity,
                ReferencedEntity = live.ReferencedEntity,
                ReferencingAttribute = live.ReferencingAttribute,
                ReferencedAttribute = live.ReferencedAttribute,
                ReferencingEntityNavigationPropertyName = live.ReferencingEntityNavigationPropertyName,
                ReferencedEntityNavigationPropertyName = live.ReferencedEntityNavigationPropertyName,
                ReferencingAttributeDisplayName = Prefer(
                    live.ReferencingAttributeDisplayName,
                    previous.FirstOrDefault(p =>
                        p.SchemaName.Equals(live.SchemaName, StringComparison.OrdinalIgnoreCase))
                        ?.ReferencingAttributeDisplayName),
                BusinessLabel = Prefer(live.BusinessLabel, configuredLabel),
                IsApproved = policyOk,
                Provenance = CrmMetadataProvenance.Live
            };
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
        // Startup warm-up: only common state/status fields. Other choice fields load lazily via EnsureAttributeOptionsAsync.
        var candidates = attributes
            .Where(a =>
                a.LogicalName.Equals("statecode", StringComparison.OrdinalIgnoreCase) ||
                a.LogicalName.Equals("statuscode", StringComparison.OrdinalIgnoreCase))
            .Where(a => IsChoiceAttributeKind(a.Kind))
            .Take(2)
            .ToList();

        foreach (var attr in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var loaded = await LoadAttributeOptionsAsync(logicalName, attr, cancellationToken);
            if (!loaded.Success || loaded.Attribute is null)
            {
                continue;
            }

            var idx = attributes.FindIndex(a =>
                a.LogicalName.Equals(attr.LogicalName, StringComparison.OrdinalIgnoreCase));
            if (idx >= 0)
            {
                attributes[idx] = loaded.Attribute;
            }
        }
    }

    private async Task<(bool Success, CrmAttributeMetadata? Attribute, string? Error, string? RequestPath)>
        LoadAttributeOptionsAsync(
            string entityLogicalName,
            CrmAttributeMetadata attr,
            CancellationToken cancellationToken)
    {
        var request = OptionSetMetadataRequest.TryResolve(attr);
        if (request is null)
        {
            return (false, null, $"Attribute kind '{attr.Kind}' does not support OptionSet enrichment.", null);
        }

        var path = OptionSetMetadataRequest.BuildPath(entityLogicalName, attr.LogicalName, request.Value);
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            var http = await _http.GetAsync(path, cancellationToken);
            if (!http.IsSuccess)
            {
                var sanitizedBody = SanitizeDynamicsErrorBody(http.Body);
                _logger.LogWarning(
                    "[OPTIONSET] Dynamics HTTP {Status} Entity={Entity} Attribute={Attribute} Kind={Kind} " +
                    "MetaType={MetaType} Path={Path} Body={Body}",
                    http.StatusCode,
                    entityLogicalName,
                    attr.LogicalName,
                    attr.Kind,
                    request.Value.MetadataType,
                    path,
                    sanitizedBody);

                return (
                    false,
                    null,
                    $"Dynamics OptionSet metadata request failed (HTTP {http.StatusCode}) for " +
                    $"{entityLogicalName}.{attr.LogicalName} via {request.Value.MetadataType}: {sanitizedBody}",
                    path);
            }

            using var doc = JsonDocument.Parse(http.Body);
            if (!doc.RootElement.TryGetProperty("OptionSet", out var optionSet))
            {
                return (
                    false,
                    null,
                    $"Dynamics response for {entityLogicalName}.{attr.LogicalName} did not include OptionSet.",
                    path);
            }

            var byLabel = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var byValue = new Dictionary<int, string>();
            ParseOptionSetElement(optionSet, attr.Kind, byLabel, byValue);
            if (byValue.Count == 0)
            {
                return (
                    false,
                    null,
                    $"Dynamics OptionSet for {entityLogicalName}.{attr.LogicalName} contained no usable options.",
                    path);
            }

            _logger.LogInformation(
                "[OPTIONSET] Loaded Entity={Entity} Attribute={Attribute} Kind={Kind} MetaType={MetaType} " +
                "OptionCount={Count} Path={Path}",
                entityLogicalName,
                attr.LogicalName,
                attr.Kind,
                request.Value.MetadataType,
                byValue.Count,
                path);

            return (true, CloneWithOptions(attr, byLabel, byValue), null, path);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(
                ex,
                "[OPTIONSET] Enrichment exception Entity={Entity} Attribute={Attribute} MetaType={MetaType} Path={Path}",
                entityLogicalName,
                attr.LogicalName,
                request.Value.MetadataType,
                path);

            return (
                false,
                null,
                $"OptionSet enrichment exception for {entityLogicalName}.{attr.LogicalName}: {TruncateForLog(ex.Message, 160)}",
                path);
        }
    }

    internal static bool IsChoiceAttributeKind(CrmAttributeKind kind) =>
        kind is CrmAttributeKind.Picklist or CrmAttributeKind.Status or CrmAttributeKind.State
            or CrmAttributeKind.Boolean;

    /// <summary>
    /// Parses OptionSet JSON using only properties valid for the known attribute kind.
    /// </summary>
    internal static void ParseOptionSetElement(
        JsonElement optionSet,
        CrmAttributeKind kind,
        IDictionary<string, int> byLabel,
        IDictionary<int, string> byValue)
    {
        if (kind == CrmAttributeKind.Boolean)
        {
            if (optionSet.TryGetProperty("TrueOption", out var trueOpt))
            {
                TryAddOption(trueOpt, byLabel, byValue);
            }

            if (optionSet.TryGetProperty("FalseOption", out var falseOpt))
            {
                TryAddOption(falseOpt, byLabel, byValue);
            }

            return;
        }

        if (optionSet.TryGetProperty("Options", out var options) &&
            options.ValueKind == JsonValueKind.Array)
        {
            foreach (var opt in options.EnumerateArray())
            {
                TryAddOption(opt, byLabel, byValue);
            }
        }
    }

    private static string OptionSetFailureKey(string entity, string attribute) =>
        entity.Trim() + "|" + attribute.Trim();

    /// <summary>Truncate Dynamics error JSON for logs — no CRM business records.</summary>
    internal static string SanitizeDynamicsErrorBody(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return "(empty)";
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var error))
            {
                var code = error.TryGetProperty("code", out var codeEl) ? codeEl.ToString() : null;
                var message = error.TryGetProperty("message", out var msgEl) ? msgEl.GetString() : null;
                var combined = string.Join(" | ", new[] { code, message }.Where(s => !string.IsNullOrWhiteSpace(s)));
                if (!string.IsNullOrWhiteSpace(combined))
                {
                    return TruncateForLog(combined, 400);
                }
            }
        }
        catch (JsonException)
        {
            // fall through to raw truncate
        }

        return TruncateForLog(body.Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal), 400);
    }

    private static string TruncateForLog(string? value, int max)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= max)
        {
            return value ?? string.Empty;
        }

        return value[..max] + "…";
    }

    private static void TryAddOption(
        JsonElement opt,
        IDictionary<string, int> byLabel,
        IDictionary<int, string> byValue)
    {
        if (!opt.TryGetProperty("Value", out var valueProp) || !valueProp.TryGetInt32(out var value))
        {
            return;
        }

        var label = GetLocalizedLabel(opt, "Label") ?? value.ToString();
        byLabel[label] = value;
        byValue[value] = label;
    }

    private static CrmAttributeMetadata CloneWithOptions(
        CrmAttributeMetadata old,
        IReadOnlyDictionary<string, int> byLabel,
        IReadOnlyDictionary<int, string> byValue) =>
        new()
        {
            LogicalName = old.LogicalName,
            ODataPropertyName = old.ODataPropertyName,
            DisplayName = old.DisplayName,
            Description = old.Description,
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

    /// <summary>
    /// Prefer English (1033) LocalizedLabels, then UserLocalizedLabel; otherwise null
    /// (callers fall back to LogicalName for table headers).
    /// </summary>
    internal static string? GetLocalizedLabelForTests(JsonElement el, string propertyName) =>
        GetLocalizedLabel(el, propertyName);

    internal static List<CrmAttributeMetadata> ParseAttributesForTests(string json) =>
        ParseAttributes(json);

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
                Description = GetLocalizedLabel(item, "Description"),
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
