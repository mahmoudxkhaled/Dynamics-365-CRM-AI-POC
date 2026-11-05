using CrmAiPoc.Api.Configuration;
using Microsoft.Extensions.Options;

namespace CrmAiPoc.Api.Crm.Metadata;

/// <summary>
/// Config-driven approval of which discovered entities the AI may query.
/// </summary>
public sealed class EntityApprovalPolicy
{
    private readonly HashSet<string> _approved;
    private readonly HashSet<string> _approvedRelationships;
    private readonly Dictionary<string, IReadOnlyList<string>> _aliases;

    public EntityApprovalPolicy(IOptions<CrmOptions> options)
    {
        var crm = options.Value;
        _approved = new HashSet<string>(
            (crm.ApprovedEntities ?? []).Where(e => !string.IsNullOrWhiteSpace(e)).Select(e => e.Trim()),
            StringComparer.OrdinalIgnoreCase);

        _approvedRelationships = new HashSet<string>(
            (crm.ApprovedRelationships ?? []).Where(r => !string.IsNullOrWhiteSpace(r)).Select(r => r.Trim()),
            StringComparer.OrdinalIgnoreCase);

        _aliases = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in crm.EntityAliases ?? new Dictionary<string, List<string>>())
        {
            _aliases[kv.Key] = (kv.Value ?? []).Where(a => !string.IsNullOrWhiteSpace(a)).Select(a => a.Trim()).ToArray();
        }
    }

    public IReadOnlyCollection<string> ApprovedLogicalNames => _approved;

    public IReadOnlyCollection<string> ApprovedRelationshipSchemaNames => _approvedRelationships;

    /// <summary>
    /// When true, only SchemaNames listed in <c>Crm:ApprovedRelationships</c> may be joined.
    /// When false (default / empty list), any discovered non-M2M relationship between two
    /// approved entities is joinable.
    /// </summary>
    public bool HasRelationshipAllowlist => _approvedRelationships.Count > 0;

    public bool IsApproved(string? logicalName) =>
        !string.IsNullOrWhiteSpace(logicalName) && _approved.Contains(logicalName.Trim());

    /// <summary>
    /// Relationship approval: both endpoint entities must be approved.
    /// Optional SchemaName allowlist restricts further when configured; empty allowlist = open
    /// within the approved-entity boundary (Many-to-Many still rejected by join builders).
    /// </summary>
    public bool IsRelationshipApproved(
        string? schemaName,
        string? referencingEntity,
        string? referencedEntity)
    {
        if (string.IsNullOrWhiteSpace(schemaName) ||
            !IsApproved(referencingEntity) ||
            !IsApproved(referencedEntity))
        {
            return false;
        }

        if (!HasRelationshipAllowlist)
        {
            return true;
        }

        return _approvedRelationships.Contains(schemaName.Trim());
    }

    public IReadOnlyList<string> GetAliases(string logicalName) =>
        _aliases.TryGetValue(logicalName, out var list) ? list : Array.Empty<string>();

    public bool TryResolveEntityAlias(string? nameOrAlias, out string logicalName)
    {
        logicalName = string.Empty;
        if (string.IsNullOrWhiteSpace(nameOrAlias))
        {
            return false;
        }

        var key = nameOrAlias.Trim();
        if (_approved.Contains(key))
        {
            logicalName = _approved.First(a => a.Equals(key, StringComparison.OrdinalIgnoreCase));
            return true;
        }

        foreach (var approved in _approved)
        {
            if (!_aliases.TryGetValue(approved, out var aliases))
            {
                continue;
            }

            if (aliases.Any(a => a.Equals(key, StringComparison.OrdinalIgnoreCase)))
            {
                logicalName = approved;
                return true;
            }
        }

        return false;
    }
}
