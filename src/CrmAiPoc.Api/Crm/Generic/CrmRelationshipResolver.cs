using System.Globalization;
using System.Text.RegularExpressions;
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

public sealed class CrmLinkResolveResult
{
    public bool Success { get; init; }
    public string? Error { get; init; }
    public IReadOnlyList<CrmResolvedLink> Links { get; init; } = Array.Empty<CrmResolvedLink>();

    public static CrmLinkResolveResult Ok(IReadOnlyList<CrmResolvedLink> links) =>
        new() { Success = true, Links = links };

    public static CrmLinkResolveResult Fail(string error) =>
        new() { Success = false, Error = error };
}

/// <summary>
/// Resolves approved CRM relationships for GUID filters and FetchXML link-entity joins.
/// Never invents attribute or relationship names.
/// </summary>
public static partial class CrmRelationshipResolver
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

        var candidates = FindRelationshipsBetween(targetEntity, relatedFromEntity, relatedFromMetadata);
        if (candidates.Count == 0)
        {
            return CrmRelationshipResolveResult.Fail(
                $"No approved one-hop relationship from '{relatedFromEntity}' to '{targetEntity.LogicalName}'.");
        }

        // relatedFromId filters the TARGET by its FK pointing at the related entity.
        // Keep only relationships where the target is the referencing (N) side.
        var filterable = candidates
            .Where(c =>
                c.ReferencingEntity.Equals(targetEntity.LogicalName, StringComparison.OrdinalIgnoreCase) &&
                c.ReferencedEntity.Equals(relatedFromEntity.Trim(), StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(c.ReferencingAttribute) &&
                (targetEntity.TryGetAttribute(c.ReferencingAttribute!, out _) ||
                 targetEntity.TryGetAttribute(
                     AttributeKindMapper.ToODataPropertyName(c.ReferencingAttribute!, CrmAttributeKind.Lookup),
                     out _)))
            .ToList();

        if (filterable.Count == 0)
        {
            return CrmRelationshipResolveResult.Fail(
                $"No approved one-hop relationship from '{relatedFromEntity}' to '{targetEntity.LogicalName}' " +
                "(target must hold the lookup attribute for relatedFromId filters).");
        }

        CrmRelationshipMetadata rel;
        if (filterable.Count > 1)
        {
            // Deterministic policy for legacy relatedFromId filters only:
            // prefer custom customer-account lookup when present.
            var preferred = filterable.FirstOrDefault(c =>
                string.Equals(c.ReferencingAttribute, "te_customeraccountid", StringComparison.OrdinalIgnoreCase));
            if (preferred is null)
            {
                return CrmRelationshipResolveResult.Fail(
                    $"Ambiguous relationship from '{relatedFromEntity}' to '{targetEntity.LogicalName}'. " +
                    $"Specify relationship SchemaName. Candidates: {string.Join(", ", filterable.Select(c => c.SchemaName))}.");
            }

            rel = preferred;
        }
        else
        {
            rel = filterable[0];
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

    /// <summary>
    /// Resolves a tree of <see cref="CrmLinkSpec"/> against approved metadata.
    /// </summary>
    public static CrmLinkResolveResult ResolveLinks(
        CrmEntityMetadata rootEntity,
        IReadOnlyList<CrmLinkSpec> links,
        Func<string, CrmEntityMetadata?> getEntity)
    {
        if (links.Count == 0)
        {
            return CrmLinkResolveResult.Ok(Array.Empty<CrmResolvedLink>());
        }

        var usedAliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var totalLinks = 0;
        var resolved = new List<CrmResolvedLink>();

        foreach (var link in links)
        {
            var one = ResolveLinkRecursive(
                parentEntity: rootEntity,
                spec: link,
                depth: 1,
                getEntity,
                usedAliases,
                ref totalLinks);
            if (!one.Success)
            {
                return CrmLinkResolveResult.Fail(one.Error!);
            }

            resolved.Add(one.Link!);
        }

        return CrmLinkResolveResult.Ok(resolved);
    }

    private sealed class SingleLinkResult
    {
        public bool Success { get; init; }
        public string? Error { get; init; }
        public CrmResolvedLink? Link { get; init; }

        public static SingleLinkResult Ok(CrmResolvedLink link) =>
            new() { Success = true, Link = link };

        public static SingleLinkResult Fail(string error) =>
            new() { Success = false, Error = error };
    }

    private static SingleLinkResult ResolveLinkRecursive(
        CrmEntityMetadata parentEntity,
        CrmLinkSpec spec,
        int depth,
        Func<string, CrmEntityMetadata?> getEntity,
        HashSet<string> usedAliases,
        ref int totalLinks)
    {
        if (depth > CrmQueryLimits.MaxLinkDepth)
        {
            return SingleLinkResult.Fail(
                $"Link depth exceeds MaxLinkDepth={CrmQueryLimits.MaxLinkDepth}.");
        }

        if (totalLinks >= CrmQueryLimits.MaxLinkedEntities)
        {
            return SingleLinkResult.Fail(
                $"Total linked entities exceed MaxLinkedEntities={CrmQueryLimits.MaxLinkedEntities}.");
        }

        if (string.IsNullOrWhiteSpace(spec.Entity))
        {
            return SingleLinkResult.Fail("Each link requires entity.");
        }

        var target = getEntity(spec.Entity.Trim());
        if (target is null || !target.IsApprovedForAi)
        {
            return SingleLinkResult.Fail($"Linked entity '{spec.Entity}' is not approved or unavailable.");
        }

        var candidates = FindRelationshipsBetween(parentEntity, target.LogicalName, target);
        // Also search relationships declared on the target looking back to parent.
        candidates = candidates
            .Concat(FindRelationshipsBetween(target, parentEntity.LogicalName, parentEntity))
            .GroupBy(r => r.SchemaName, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();

        // Keep only relationships that connect parent <-> target.
        candidates = candidates
            .Where(r => Relates(r, parentEntity.LogicalName, target.LogicalName))
            .ToList();

        if (candidates.Count == 0)
        {
            return SingleLinkResult.Fail(
                $"No approved relationship between '{parentEntity.LogicalName}' and '{target.LogicalName}'.");
        }

        CrmRelationshipMetadata rel;
        if (!string.IsNullOrWhiteSpace(spec.Relationship))
        {
            var key = spec.Relationship.Trim();
            var matches = candidates
                .Where(c =>
                    c.SchemaName.Equals(key, StringComparison.OrdinalIgnoreCase) ||
                    (c.ReferencingAttribute?.Equals(key, StringComparison.OrdinalIgnoreCase) ?? false))
                .ToList();
            if (matches.Count == 0)
            {
                return SingleLinkResult.Fail(
                    $"Relationship '{key}' is not an approved relationship between " +
                    $"'{parentEntity.LogicalName}' and '{target.LogicalName}'. " +
                    $"Candidates: {string.Join(", ", candidates.Select(c => c.SchemaName))}.");
            }

            if (matches.Count > 1)
            {
                return SingleLinkResult.Fail(
                    $"Relationship '{key}' is ambiguous. Candidates: {string.Join(", ", matches.Select(m => m.SchemaName))}.");
            }

            rel = matches[0];
        }
        else if (candidates.Count == 1)
        {
            rel = candidates[0];
        }
        else
        {
            return SingleLinkResult.Fail(
                $"Ambiguous relationship between '{parentEntity.LogicalName}' and '{target.LogicalName}'. " +
                $"Specify relationship SchemaName. Candidates: {string.Join(", ", candidates.Select(c => c.SchemaName))}.");
        }

        if (string.IsNullOrWhiteSpace(rel.ReferencingAttribute) ||
            string.IsNullOrWhiteSpace(rel.ReferencedAttribute))
        {
            return SingleLinkResult.Fail(
                $"Relationship '{rel.SchemaName}' is missing referencing/referenced attributes.");
        }

        // FetchXML link-entity on the parent: from=referenced PK on related entity, to=FK on parent
        // when ManyToOne from parent → related (parent has FK).
        // When OneToMany from parent → children, from=FK on child, to=PK on parent — link-entity name is child.
        string fromAttr;
        string toAttr;
        string linkEntityName;
        bool expandsToMany;
        if (rel.ReferencingEntity.Equals(parentEntity.LogicalName, StringComparison.OrdinalIgnoreCase) &&
            rel.ReferencedEntity.Equals(target.LogicalName, StringComparison.OrdinalIgnoreCase))
        {
            // Parent N:1 Related — FK on parent
            linkEntityName = target.LogicalName;
            fromAttr = rel.ReferencedAttribute!;
            toAttr = rel.ReferencingAttribute!;
            expandsToMany = false;
        }
        else if (rel.ReferencedEntity.Equals(parentEntity.LogicalName, StringComparison.OrdinalIgnoreCase) &&
                 rel.ReferencingEntity.Equals(target.LogicalName, StringComparison.OrdinalIgnoreCase))
        {
            // Parent 1:N Child — FK on child
            linkEntityName = target.LogicalName;
            fromAttr = rel.ReferencingAttribute!;
            toAttr = rel.ReferencedAttribute!;
            expandsToMany = true;
        }
        else
        {
            return SingleLinkResult.Fail(
                $"Relationship '{rel.SchemaName}' orientation is unsupported for link-entity.");
        }

        var alias = SanitizeAlias(
            string.IsNullOrWhiteSpace(spec.Alias) ? GenerateAlias(target.LogicalName, usedAliases) : spec.Alias!);
        if (!usedAliases.Add(alias))
        {
            return SingleLinkResult.Fail($"Duplicate link alias '{alias}'.");
        }

        if (spec.PostJoinFilters.Count > 0 && spec.JoinType != CrmLinkJoinType.Outer)
        {
            return SingleLinkResult.Fail(
                "postJoinFilters require joinType=outer for post-join / anti-join semantics.");
        }

        var select = ResolveLinkSelect(target, spec.Select);
        if (!TryResolveFilterConditions(target, spec.Filters, out var filters, out var filterError))
        {
            return SingleLinkResult.Fail(filterError ?? "Invalid related filter.");
        }

        if (!TryResolveFilterConditions(target, spec.PostJoinFilters, out var postJoinFilters, out var postJoinError))
        {
            return SingleLinkResult.Fail(postJoinError ?? "Invalid post-join filter.");
        }

        totalLinks++;
        var children = new List<CrmResolvedLink>();
        foreach (var childSpec in spec.Links)
        {
            var child = ResolveLinkRecursive(target, childSpec, depth + 1, getEntity, usedAliases, ref totalLinks);
            if (!child.Success)
            {
                return child;
            }

            children.Add(child.Link!);
        }

        return SingleLinkResult.Ok(new CrmResolvedLink
        {
            Alias = alias,
            LinkEntityLogicalName = linkEntityName,
            FromAttribute = fromAttr,
            ToAttribute = toAttr,
            JoinType = spec.JoinType,
            RelationshipSchemaName = rel.SchemaName,
            ExpandsToMany = expandsToMany,
            SelectLogicalNames = select,
            Filters = filters,
            PostJoinFilters = postJoinFilters,
            Children = children,
            Summary =
                $"{parentEntity.LogicalName}-[{rel.SchemaName}/{spec.JoinType}]->{target.LogicalName} as {alias}"
        });
    }

    private static bool TryResolveFilterConditions(
        CrmEntityMetadata target,
        IReadOnlyList<CrmQueryFilter> source,
        out List<CrmResolvedFilterCondition> conditions,
        out string? error)
    {
        conditions = [];
        error = null;
        foreach (var filter in source.Take(CrmQueryLimits.MaxFilters))
        {
            var built = FetchXmlConditionBuilder.BuildFilter(target, filter);
            if (!built.Success || built.Condition is null)
            {
                error = built.Error ?? "Invalid related filter.";
                return false;
            }

            var attrName = built.Condition.Attribute("attribute")?.Value ?? filter.Field;
            var op = built.Condition.Attribute("operator")?.Value ?? "eq";
            var value = built.Condition.Attribute("value")?.Value;
            conditions.Add(new CrmResolvedFilterCondition(
                attrName,
                op,
                value,
                built.Summary ?? filter.Field));
        }

        return true;
    }

    private static bool Relates(CrmRelationshipMetadata r, string a, string b) =>
        (r.ReferencingEntity.Equals(a, StringComparison.OrdinalIgnoreCase) &&
         r.ReferencedEntity.Equals(b, StringComparison.OrdinalIgnoreCase)) ||
        (r.ReferencingEntity.Equals(b, StringComparison.OrdinalIgnoreCase) &&
         r.ReferencedEntity.Equals(a, StringComparison.OrdinalIgnoreCase));

    private static List<CrmRelationshipMetadata> FindRelationshipsBetween(
        CrmEntityMetadata primary,
        string otherEntity,
        CrmEntityMetadata? otherMetadata)
    {
        var other = otherEntity.Trim();
        var list = new List<CrmRelationshipMetadata>();

        foreach (var r in primary.Relationships.Where(r => r.IsApproved))
        {
            if (r.Kind == CrmRelationshipKind.ManyToMany)
            {
                continue; // not supported in this PoC phase
            }

            if (Relates(r, primary.LogicalName, other) &&
                !string.IsNullOrWhiteSpace(r.ReferencingAttribute) &&
                !string.IsNullOrWhiteSpace(r.ReferencedAttribute))
            {
                list.Add(r);
            }
        }

        if (otherMetadata is not null)
        {
            foreach (var r in otherMetadata.Relationships.Where(r => r.IsApproved))
            {
                if (r.Kind == CrmRelationshipKind.ManyToMany)
                {
                    continue;
                }

                if (Relates(r, primary.LogicalName, other) &&
                    !string.IsNullOrWhiteSpace(r.ReferencingAttribute) &&
                    !string.IsNullOrWhiteSpace(r.ReferencedAttribute) &&
                    list.All(x => !x.SchemaName.Equals(r.SchemaName, StringComparison.OrdinalIgnoreCase)))
                {
                    list.Add(r);
                }
            }
        }

        return list;
    }

    private static IReadOnlyList<string> ResolveLinkSelect(CrmEntityMetadata entity, IReadOnlyList<string> requested)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(entity.PrimaryNameAttribute))
        {
            set.Add(entity.PrimaryNameAttribute);
        }

        foreach (var name in requested)
        {
            if (set.Count >= CrmQueryLimits.MaxLinkSelectFields)
            {
                break;
            }

            if (entity.TryGetAttribute(name, out var attr) && attr.IsValidForRead)
            {
                set.Add(attr.LogicalName);
            }
        }

        if (set.Count == 0 && !string.IsNullOrWhiteSpace(entity.PrimaryIdAttribute))
        {
            set.Add(entity.PrimaryIdAttribute);
        }

        return set.Take(CrmQueryLimits.MaxLinkSelectFields).ToArray();
    }

    private static string GenerateAlias(string entityLogicalName, HashSet<string> used)
    {
        var baseAlias = AliasSanitizeRegex().Replace(entityLogicalName, "");
        if (string.IsNullOrWhiteSpace(baseAlias) || !char.IsAsciiLetter(baseAlias[0]))
        {
            baseAlias = "lnk";
        }

        baseAlias = baseAlias.Length > 12 ? baseAlias[..12] : baseAlias;
        if (!used.Contains(baseAlias))
        {
            return baseAlias;
        }

        for (var i = 2; i < 100; i++)
        {
            var candidate = baseAlias + i.ToString(CultureInfo.InvariantCulture);
            if (!used.Contains(candidate))
            {
                return candidate;
            }
        }

        return "lnk" + Guid.NewGuid().ToString("N")[..6];
    }

    private static string SanitizeAlias(string alias)
    {
        var cleaned = AliasSanitizeRegex().Replace(alias.Trim(), "");
        if (string.IsNullOrWhiteSpace(cleaned) || !char.IsAsciiLetter(cleaned[0]))
        {
            cleaned = "lnk" + cleaned;
        }

        return cleaned.Length > 40 ? cleaned[..40] : cleaned;
    }

    [GeneratedRegex(@"[^A-Za-z0-9_]", RegexOptions.CultureInvariant)]
    private static partial Regex AliasSanitizeRegex();
}
