using CrmAiPoc.Api.Crm.Metadata;

namespace CrmAiPoc.Api.Crm.Generic;

public sealed class CrmRelationshipResolveResult
{
    public bool Success { get; init; }
    public string? Error { get; init; }
    public string? FilterField { get; init; }
    public string? FilterValue { get; init; }
    public string? Summary { get; init; }

    public static CrmRelationshipResolveResult Ok(string field, string value, string summary) =>
        new() { Success = true, FilterField = field, FilterValue = value, Summary = summary };

    public static CrmRelationshipResolveResult Fail(string error) =>
        new() { Success = false, Error = error };
}

/// <summary>
/// Resolves one-hop approved relationships into a GUID filter on the child entity.
/// Does not invent navigation property names for $expand joins.
/// </summary>
public static class CrmRelationshipResolver
{
    public static CrmRelationshipResolveResult ResolveOneHop(
        CrmEntityMetadata targetEntity,
        string relatedFromEntity,
        string relatedFromId,
        CrmEntityMetadata? relatedFromMetadata)
    {
        if (!Guid.TryParse(relatedFromId, out var id) || id == Guid.Empty)
        {
            return CrmRelationshipResolveResult.Fail(
                "relatedFromId must be a non-empty GUID for relationship queries.");
        }

        var from = relatedFromEntity.Trim();
        var rel = targetEntity.Relationships.FirstOrDefault(r =>
            r.IsApproved &&
            r.Kind == CrmRelationshipKind.ManyToOne &&
            r.ReferencingEntity.Equals(targetEntity.LogicalName, StringComparison.OrdinalIgnoreCase) &&
            r.ReferencedEntity.Equals(from, StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(r.ReferencingAttribute));

        if (rel is null)
        {
            // Also accept OneToMany declared on the parent (referenced) side.
            rel = relatedFromMetadata?.Relationships.FirstOrDefault(r =>
                r.IsApproved &&
                r.Kind == CrmRelationshipKind.OneToMany &&
                r.ReferencedEntity.Equals(from, StringComparison.OrdinalIgnoreCase) &&
                r.ReferencingEntity.Equals(targetEntity.LogicalName, StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(r.ReferencingAttribute));
        }

        if (rel is null || string.IsNullOrWhiteSpace(rel.ReferencingAttribute))
        {
            return CrmRelationshipResolveResult.Fail(
                $"No approved one-hop relationship from '{from}' to '{targetEntity.LogicalName}'.");
        }

        var lookupLogical = rel.ReferencingAttribute!;
        var odataName = AttributeKindMapper.ToODataPropertyName(lookupLogical, CrmAttributeKind.Lookup);
        if (targetEntity.TryGetAttribute(lookupLogical, out var attr) ||
            targetEntity.TryGetAttribute(odataName, out attr))
        {
            odataName = attr.ODataPropertyName;
        }

        return CrmRelationshipResolveResult.Ok(
            odataName,
            id.ToString("D"),
            $"{targetEntity.LogicalName}.{odataName} eq {id:D} (via {rel.SchemaName})");
    }
}
