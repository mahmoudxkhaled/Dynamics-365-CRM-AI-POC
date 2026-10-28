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
        if (_registry.GetApproved().Count >= _policy.ApprovedLogicalNames.Count &&
            DateTimeOffset.UtcNow - _lastFullLoadUtc < cacheTtl)
        {
            return;
        }

        await RefreshAsync(null, cancellationToken);
    }

    public async Task<CrmEntityMetadata?> GetEntityAsync(string logicalName, CancellationToken cancellationToken = default)
    {
        if (_registry.TryGet(logicalName, out var cached) &&
            DateTimeOffset.UtcNow - cached.DiscoveredAtUtc < TimeSpan.FromSeconds(Math.Max(60, _options.MetadataCacheSeconds)))
        {
            return cached;
        }

        await RefreshAsync(logicalName, cancellationToken);
        return _registry.TryGet(logicalName, out var entity) ? entity : null;
    }

    public async Task<IReadOnlyList<CrmEntityMetadata>> GetApprovedEntitiesAsync(CancellationToken cancellationToken = default)
    {
        await EnsureApprovedEntitiesLoadedAsync(cancellationToken);
        return _registry.GetApproved();
    }

    public async Task RefreshAsync(string? logicalName = null, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!_options.EnableLiveIntegration || string.IsNullOrWhiteSpace(_options.BaseUrl))
            {
                SeedStatic();
                return;
            }

            try
            {
                _http.EnsureLiveConfigured();
            }
            catch
            {
                SeedStatic();
                return;
            }

            var targets = string.IsNullOrWhiteSpace(logicalName)
                ? _policy.ApprovedLogicalNames.ToArray()
                : [_policy.IsApproved(logicalName) ? logicalName.Trim() : logicalName.Trim()];

            foreach (var name in targets)
            {
                try
                {
                    var meta = await DiscoverEntityAsync(name, cancellationToken);
                    if (meta is not null)
                    {
                        _registry.Upsert(meta);
                    }
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
                _lastFullLoadUtc = DateTimeOffset.UtcNow;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private void SeedStatic()
    {
        foreach (var entity in StaticSeedCrmMetadata.CreateApprovedSeeds(_policy))
        {
            _registry.Upsert(entity);
        }

        foreach (var approved in _policy.ApprovedLogicalNames)
        {
            if (!_registry.TryGet(approved, out _))
            {
                UpsertFallbackMetadata(approved);
            }
        }

        _lastFullLoadUtc = DateTimeOffset.UtcNow;
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

        // OptionSets for status/state/picklist — best effort for approved compact set.
        await EnrichOptionSetsAsync(logicalName, attributes, cancellationToken);

        var relationships = await DiscoverRelationshipsAsync(logicalName, cancellationToken);

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
            DiscoveredAtUtc = DateTimeOffset.UtcNow
        };
    }

    private async Task EnrichOptionSetsAsync(
        string logicalName,
        List<CrmAttributeMetadata> attributes,
        CancellationToken cancellationToken)
    {
        // Cap option-set enrichment: state/status always; at most a few other picklists (lead has many).
        var candidates = attributes
            .Where(a => a.Kind is CrmAttributeKind.Status or CrmAttributeKind.State or CrmAttributeKind.Picklist)
            .OrderBy(a => a.LogicalName is "statecode" or "statuscode" ? 0 : 1)
            .ThenBy(a => a.LogicalName, StringComparer.OrdinalIgnoreCase)
            .Take(8)
            .ToList();

        foreach (var attr in candidates)
        {
            try
            {
                var metaType = attr.Kind == CrmAttributeKind.State
                    ? "StateAttributeMetadata"
                    : attr.Kind == CrmAttributeKind.Status
                        ? "StatusAttributeMetadata"
                        : "PicklistAttributeMetadata";
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

    private async Task<IReadOnlyList<CrmRelationshipMetadata>> DiscoverRelationshipsAsync(
        string logicalName,
        CancellationToken cancellationToken)
    {
        var list = new List<CrmRelationshipMetadata>();
        try
        {
            var manyToOne =
                $"EntityDefinitions(LogicalName='{Escape(logicalName)}')" +
                "/ManyToOneRelationships?$select=SchemaName,ReferencingEntity,ReferencedEntity,ReferencingAttribute,ReferencedAttribute,ReferencingEntityNavigationPropertyName,ReferencedEntityNavigationPropertyName";
            var http = await _http.GetAsync(manyToOne, cancellationToken);
            if (http.IsSuccess)
            {
                list.AddRange(ParseRelationships(http.Body, CrmRelationshipKind.ManyToOne));
            }

            var oneToMany =
                $"EntityDefinitions(LogicalName='{Escape(logicalName)}')" +
                "/OneToManyRelationships?$select=SchemaName,ReferencingEntity,ReferencedEntity,ReferencingAttribute,ReferencedAttribute,ReferencingEntityNavigationPropertyName,ReferencedEntityNavigationPropertyName";
            http = await _http.GetAsync(oneToMany, cancellationToken);
            if (http.IsSuccess)
            {
                list.AddRange(ParseRelationships(http.Body, CrmRelationshipKind.OneToMany));
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Relationship discovery failed for {Entity}", logicalName);
        }

        // Only mark relationships between approved entities as AI-approved.
        return list.Select(r => new CrmRelationshipMetadata
        {
            SchemaName = r.SchemaName,
            Kind = r.Kind,
            ReferencingEntity = r.ReferencingEntity,
            ReferencedEntity = r.ReferencedEntity,
            ReferencingAttribute = r.ReferencingAttribute,
            ReferencedAttribute = r.ReferencedAttribute,
            ReferencingEntityNavigationPropertyName = r.ReferencingEntityNavigationPropertyName,
            ReferencedEntityNavigationPropertyName = r.ReferencedEntityNavigationPropertyName,
            IsApproved = _policy.IsApproved(r.ReferencingEntity) && _policy.IsApproved(r.ReferencedEntity)
        }).ToArray();
    }

    private static List<CrmRelationshipMetadata> ParseRelationships(string json, CrmRelationshipKind kind)
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
            if (string.IsNullOrWhiteSpace(schema) || string.IsNullOrWhiteSpace(referencing) || string.IsNullOrWhiteSpace(referenced))
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
                IsApproved = false
            });
        }

        return list;
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
