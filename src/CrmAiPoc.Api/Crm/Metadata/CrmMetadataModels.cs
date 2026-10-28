namespace CrmAiPoc.Api.Crm.Metadata;

public enum CrmAttributeKind
{
    Unknown = 0,
    Guid,
    String,
    Memo,
    Integer,
    Decimal,
    Money,
    Boolean,
    DateTime,
    Picklist,
    Status,
    State,
    Lookup,
    Customer,
    Owner,
    Uniqueidentifier
}

public enum CrmRelationshipKind
{
    ManyToOne,
    OneToMany,
    ManyToMany
}

public sealed class CrmEntityMetadata
{
    public required string LogicalName { get; init; }
    public required string EntitySetName { get; init; }
    public string? DisplayName { get; init; }
    public required string PrimaryIdAttribute { get; init; }
    public required string PrimaryNameAttribute { get; init; }
    public bool IsApprovedForAi { get; init; }
    public IReadOnlyList<string> Aliases { get; init; } = Array.Empty<string>();
    public IReadOnlyList<CrmAttributeMetadata> Attributes { get; init; } = Array.Empty<CrmAttributeMetadata>();
    public IReadOnlyList<CrmRelationshipMetadata> Relationships { get; init; } = Array.Empty<CrmRelationshipMetadata>();
    public DateTimeOffset DiscoveredAtUtc { get; init; } = DateTimeOffset.UtcNow;

    public bool TryGetAttribute(string nameOrAlias, out CrmAttributeMetadata attribute)
    {
        attribute = null!;
        if (string.IsNullOrWhiteSpace(nameOrAlias))
        {
            return false;
        }

        var key = nameOrAlias.Trim();
        foreach (var attr in Attributes)
        {
            if (attr.LogicalName.Equals(key, StringComparison.OrdinalIgnoreCase) ||
                attr.ODataPropertyName.Equals(key, StringComparison.OrdinalIgnoreCase) ||
                attr.Aliases.Any(a => a.Equals(key, StringComparison.OrdinalIgnoreCase)))
            {
                attribute = attr;
                return true;
            }
        }

        return false;
    }
}

public sealed class CrmAttributeMetadata
{
    public required string LogicalName { get; init; }
    public required string ODataPropertyName { get; init; }
    public string? DisplayName { get; init; }
    public required CrmAttributeKind Kind { get; init; }
    public string? AttributeTypeName { get; init; }
    public bool IsValidForRead { get; init; } = true;
    public bool IsFilterable { get; init; }
    public bool IsSortable { get; init; }
    public bool IsSearchable { get; init; }
    public bool IsCustom { get; init; }
    public IReadOnlyList<string> Aliases { get; init; } = Array.Empty<string>();
    public IReadOnlyDictionary<string, int> OptionSetByLabel { get; init; } =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyDictionary<int, string> OptionSetByValue { get; init; } =
        new Dictionary<int, string>();

    public IReadOnlyList<CrmFilterOperator> AllowedOperators { get; init; } = Array.Empty<CrmFilterOperator>();
}

public sealed class CrmRelationshipMetadata
{
    public required string SchemaName { get; init; }
    public required CrmRelationshipKind Kind { get; init; }
    public required string ReferencingEntity { get; init; }
    public required string ReferencedEntity { get; init; }
    public string? ReferencingAttribute { get; init; }
    public string? ReferencedAttribute { get; init; }
    public string? ReferencingEntityNavigationPropertyName { get; init; }
    public string? ReferencedEntityNavigationPropertyName { get; init; }
    public bool IsApproved { get; init; }
}

public enum CrmFilterOperator
{
    Eq,
    Ne,
    Gt,
    Ge,
    Lt,
    Le,
    Contains
}
