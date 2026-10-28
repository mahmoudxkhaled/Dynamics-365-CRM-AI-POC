using System.Collections.Concurrent;

namespace CrmAiPoc.Api.Crm.Metadata;

/// <summary>
/// In-memory cache of discovered CRM entity metadata. Refreshable without code changes.
/// </summary>
public sealed class CrmMetadataRegistry
{
    private readonly ConcurrentDictionary<string, CrmEntityMetadata> _entities =
        new(StringComparer.OrdinalIgnoreCase);

    public void Upsert(CrmEntityMetadata entity)
    {
        ArgumentNullException.ThrowIfNull(entity);
        _entities[entity.LogicalName] = entity;
    }

    public bool TryGet(string logicalName, out CrmEntityMetadata metadata) =>
        _entities.TryGetValue(logicalName, out metadata!);

    public IReadOnlyList<CrmEntityMetadata> GetApproved() =>
        _entities.Values.Where(e => e.IsApprovedForAi).OrderBy(e => e.LogicalName).ToArray();

    public void Clear() => _entities.Clear();

    /// <summary>Compact one-line schema summary for diagnostics (not the outbound AI catalog block).</summary>
    public string BuildAiCatalogSummary()
    {
        var approved = GetApproved();
        if (approved.Count == 0)
        {
            return "No approved CRM entities are currently loaded.";
        }

        var parts = new List<string>();
        foreach (var entity in approved)
        {
            var fields = entity.Attributes
                .Where(a => a.IsValidForRead && a.IsFilterable)
                .Take(24)
                .Select(a => a.LogicalName + (a.Kind is CrmAttributeKind.Picklist or CrmAttributeKind.Status or CrmAttributeKind.State
                    ? "(choice)"
                    : a.Kind is CrmAttributeKind.Lookup or CrmAttributeKind.Customer ? "(guid)" : ""))
                .ToArray();
            var aliases = entity.Aliases.Count > 0 ? $" aliases=[{string.Join(',', entity.Aliases.Take(6))}]" : "";
            parts.Add($"{entity.LogicalName} ({entity.DisplayName ?? entity.LogicalName}){aliases}: {string.Join(", ", fields)}");
        }

        return string.Join(" | ", parts);
    }
}
