namespace CrmAiPoc.Api.Crm.Metadata;

/// <summary>
/// Builds minimal metadata stubs for approved entities that are not in the offline seed catalog.
/// Used by Mock mode so config-only approvals (e.g. lead) need no entity-specific C# builders.
/// Live Dynamics discovery replaces stubs with real EntityDefinitions.
/// </summary>
public static class GenericApprovedEntityStubFactory
{
    public static CrmEntityMetadata Create(string logicalName, EntityApprovalPolicy policy)
    {
        var name = logicalName.Trim().ToLowerInvariant();
        var setName = GuessEntitySetName(name);
        var primaryId = name + "id";
        var primaryName = name is "lead" or "contact" ? "fullname" : "name";
        if (name is "phonecall" or "task" or "email" or "appointment")
        {
            primaryId = "activityid";
            primaryName = "subject";
        }

        return new CrmEntityMetadata
        {
            LogicalName = name,
            EntitySetName = setName,
            DisplayName = char.ToUpperInvariant(name[0]) + name[1..],
            PrimaryIdAttribute = primaryId,
            PrimaryNameAttribute = primaryName,
            IsApprovedForAi = policy.IsApproved(name),
            Aliases = policy.GetAliases(name),
            Attributes =
            [
                Attr(primaryId, CrmAttributeKind.Uniqueidentifier, filterable: true),
                Attr(primaryName, CrmAttributeKind.String, filterable: true, searchable: true, sortable: true),
                Attr("createdon", CrmAttributeKind.DateTime, filterable: true, sortable: true),
                Attr("modifiedon", CrmAttributeKind.DateTime, filterable: true)
            ],
            Relationships = Array.Empty<CrmRelationshipMetadata>(),
            DiscoveredAtUtc = DateTimeOffset.UtcNow
        };
    }

    private static string GuessEntitySetName(string logicalName) => logicalName switch
    {
        "opportunity" => "opportunities",
        "lead" => "leads",
        "quote" => "quotes",
        "invoice" => "invoices",
        "phonecall" => "phonecalls",
        _ when logicalName.EndsWith('y') => logicalName[..^1] + "ies",
        _ when logicalName.EndsWith('s') => logicalName + "es",
        _ => logicalName + "s"
    };

    private static CrmAttributeMetadata Attr(
        string logicalName,
        CrmAttributeKind kind,
        bool filterable = false,
        bool sortable = false,
        bool searchable = false) =>
        new()
        {
            LogicalName = logicalName,
            ODataPropertyName = AttributeKindMapper.ToODataPropertyName(logicalName, kind),
            Kind = kind,
            IsValidForRead = true,
            IsFilterable = filterable || AttributeKindMapper.IsTypicallyFilterable(kind),
            IsSortable = sortable,
            IsSearchable = searchable || AttributeKindMapper.IsSearchable(kind),
            AllowedOperators = AttributeKindMapper.DefaultOperators(kind)
        };
}
