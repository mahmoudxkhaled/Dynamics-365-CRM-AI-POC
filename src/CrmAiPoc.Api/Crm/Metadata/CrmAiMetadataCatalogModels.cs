namespace CrmAiPoc.Api.Crm.Metadata;

/// <summary>Compact catalog payload for the AI (schema only — never CRM records).</summary>
public sealed class CrmAiMetadataCatalog
{
    /// <summary>Key legend for abbreviated JSON fields.</summary>
    public Dictionary<string, string>? Keys { get; init; }

    /// <summary>All approved entities (compact index).</summary>
    public IReadOnlyList<CrmAiCatalogEntityIndex> Index { get; init; } = Array.Empty<CrmAiCatalogEntityIndex>();

    /// <summary>Expanded attribute/relationship detail for planning-relevant entities.</summary>
    public IReadOnlyList<CrmAiCatalogEntityDetail> Detail { get; init; } = Array.Empty<CrmAiCatalogEntityDetail>();

    // Backward-compatible projection used by older tests/diagnostics.
    public IReadOnlyList<CrmAiCatalogEntity> Entities { get; init; } = Array.Empty<CrmAiCatalogEntity>();
}

public sealed class CrmAiCatalogEntityIndex
{
    public required string LogicalName { get; init; }
    public string? DisplayName { get; init; }
    public IReadOnlyList<string> Aliases { get; init; } = Array.Empty<string>();
    public string? PrimaryNameAttribute { get; init; }
}

public sealed class CrmAiCatalogEntityDetail
{
    public required string LogicalName { get; init; }
    public string? DisplayName { get; init; }
    public string? EntitySetName { get; init; }
    public IReadOnlyList<string> Aliases { get; init; } = Array.Empty<string>();
    public string? PrimaryIdAttribute { get; init; }
    public string? PrimaryNameAttribute { get; init; }
    public IReadOnlyList<CrmAiCatalogAttribute> Attributes { get; init; } = Array.Empty<CrmAiCatalogAttribute>();
    public IReadOnlyList<CrmAiCatalogRelationship> Relationships { get; init; } = Array.Empty<CrmAiCatalogRelationship>();
}

/// <summary>Legacy flat entity shape retained for tests and diagnostics projections.</summary>
public sealed class CrmAiCatalogEntity
{
    public required string LogicalName { get; init; }
    public string? EntitySetName { get; init; }
    public string? DisplayName { get; init; }
    public IReadOnlyList<string> Aliases { get; init; } = Array.Empty<string>();
    public string? PrimaryIdAttribute { get; init; }
    public string? PrimaryNameAttribute { get; init; }
    public IReadOnlyList<CrmAiCatalogAttribute> Attributes { get; init; } = Array.Empty<CrmAiCatalogAttribute>();
    public IReadOnlyList<CrmAiCatalogRelationship> Relationships { get; init; } = Array.Empty<CrmAiCatalogRelationship>();
}

public sealed class CrmAiCatalogAttribute
{
    public required string LogicalName { get; init; }
    public string? DisplayName { get; init; }
    public required string Type { get; init; }
    public bool Filterable { get; init; }
    public bool Sortable { get; init; }
    public bool Selectable { get; init; } = true;
}

public sealed class CrmAiCatalogRelationship
{
    public required string SchemaName { get; init; }
    public required string Kind { get; init; }
    public required string ReferencingEntity { get; init; }
    public required string ReferencedEntity { get; init; }
    public string? ReferencingAttribute { get; init; }
    /// <summary>Business-facing label for AI planning (never CRM record data).</summary>
    public string? DisplayName { get; init; }
    /// <summary>N:1, 1:N, or N:N relative to the detail entity.</summary>
    public string? Direction { get; init; }
}

public sealed class CrmAiMetadataCatalogBuildResult
{
    public bool Enabled { get; init; }
    public bool Success { get; init; }
    public string? Error { get; init; }
    public CrmAiMetadataCatalog Catalog { get; init; } = new();
    public string FormattedBlock { get; init; } = string.Empty;
    public int EntityCount { get; init; }
    public int AttributeCount { get; init; }
    public int CharacterCount { get; init; }

    // Diagnostics
    public string Source { get; init; } = "Unknown";
    public int ApprovedEntityCount { get; init; }
    public int IncludedEntityCount { get; init; }
    public int IncludedAttributeCount { get; init; }
    public int IncludedRelationshipCount { get; init; }
    public int OriginalCharacterCount { get; init; }
    public int FinalCharacterCount { get; init; }
    public int ConfiguredBudget { get; init; }
    public bool TruncationApplied { get; init; }
}
