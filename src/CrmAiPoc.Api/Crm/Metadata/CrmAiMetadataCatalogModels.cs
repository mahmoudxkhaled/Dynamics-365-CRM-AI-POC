namespace CrmAiPoc.Api.Crm.Metadata;

public sealed class CrmAiMetadataCatalog
{
    public IReadOnlyList<CrmAiCatalogEntity> Entities { get; init; } = Array.Empty<CrmAiCatalogEntity>();
}

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
}
