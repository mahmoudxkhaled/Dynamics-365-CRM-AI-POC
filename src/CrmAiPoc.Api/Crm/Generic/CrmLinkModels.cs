namespace CrmAiPoc.Api.Crm.Generic;

public enum CrmLinkJoinType
{
    Outer = 0,
    Inner = 1
}

/// <summary>
/// Structured link request from the AI tool (never raw FetchXML).
/// Nested <see cref="Links"/> enable bounded multi-hop joins.
/// </summary>
public sealed class CrmLinkSpec
{
    /// <summary>Related entity logical name (must be approved).</summary>
    public required string Entity { get; init; }

    /// <summary>
    /// Optional exact relationship SchemaName from the approved catalog.
    /// Must not be invented — prefer <see cref="RelationshipPurpose"/> when unsure.
    /// </summary>
    public string? Relationship { get; init; }

    /// <summary>
    /// Business relationship purpose / display label (e.g. "Customer Account").
    /// Backend resolves this to a verified SchemaName via CRM metadata.
    /// </summary>
    public string? RelationshipPurpose { get; init; }

    public CrmLinkJoinType JoinType { get; init; } = CrmLinkJoinType.Outer;

    /// <summary>FetchXML link-entity alias (server may sanitize/generate).</summary>
    public string? Alias { get; init; }

    public IReadOnlyList<string> Select { get; init; } = Array.Empty<string>();
    public IReadOnlyList<CrmQueryFilter> Filters { get; init; } = Array.Empty<CrmQueryFilter>();
    public IReadOnlyList<CrmLinkSpec> Links { get; init; } = Array.Empty<CrmLinkSpec>();
}

/// <summary>Validated link ready for FetchXML emission.</summary>
public sealed class CrmResolvedLink
{
    public required string Alias { get; init; }
    public required string LinkEntityLogicalName { get; init; }
    public required string FromAttribute { get; init; }
    public required string ToAttribute { get; init; }
    public required CrmLinkJoinType JoinType { get; init; }
    public required string RelationshipSchemaName { get; init; }
    /// <summary>True when this link expands child rows (parent 1:N child), which can duplicate roots.</summary>
    public bool ExpandsToMany { get; init; }
    public required IReadOnlyList<string> SelectLogicalNames { get; init; }
    public required IReadOnlyList<(string Attribute, string Operator, string Value, string Summary)> Filters { get; init; }
    public required IReadOnlyList<CrmResolvedLink> Children { get; init; }
    public string Summary { get; init; } = string.Empty;
}
