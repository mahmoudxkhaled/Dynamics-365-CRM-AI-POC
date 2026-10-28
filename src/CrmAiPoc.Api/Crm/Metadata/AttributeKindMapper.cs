namespace CrmAiPoc.Api.Crm.Metadata;

public static class AttributeKindMapper
{
    public static CrmAttributeKind FromDynamicsType(string? attributeType)
    {
        if (string.IsNullOrWhiteSpace(attributeType))
        {
            return CrmAttributeKind.Unknown;
        }

        return attributeType.Trim().ToLowerInvariant() switch
        {
            "string" or "memo" => attributeType.Equals("Memo", StringComparison.OrdinalIgnoreCase)
                ? CrmAttributeKind.Memo
                : CrmAttributeKind.String,
            "integer" or "bigint" => CrmAttributeKind.Integer,
            "decimal" or "double" or "float" => CrmAttributeKind.Decimal,
            "money" => CrmAttributeKind.Money,
            "boolean" => CrmAttributeKind.Boolean,
            "datetime" => CrmAttributeKind.DateTime,
            "picklist" or "multiselectpicklist" => CrmAttributeKind.Picklist,
            "status" => CrmAttributeKind.Status,
            "state" => CrmAttributeKind.State,
            "lookup" => CrmAttributeKind.Lookup,
            "customer" => CrmAttributeKind.Customer,
            "owner" => CrmAttributeKind.Owner,
            "uniqueidentifier" => CrmAttributeKind.Uniqueidentifier,
            _ => CrmAttributeKind.Unknown
        };
    }

    public static IReadOnlyList<CrmFilterOperator> DefaultOperators(CrmAttributeKind kind) => kind switch
    {
        CrmAttributeKind.String or CrmAttributeKind.Memo =>
        [
            CrmFilterOperator.Eq, CrmFilterOperator.Ne, CrmFilterOperator.Contains
        ],
        CrmAttributeKind.Integer or CrmAttributeKind.Decimal or CrmAttributeKind.Money or CrmAttributeKind.DateTime =>
        [
            CrmFilterOperator.Eq, CrmFilterOperator.Ne,
            CrmFilterOperator.Gt, CrmFilterOperator.Ge, CrmFilterOperator.Lt, CrmFilterOperator.Le
        ],
        CrmAttributeKind.Boolean or CrmAttributeKind.Picklist or CrmAttributeKind.Status or CrmAttributeKind.State
            or CrmAttributeKind.Guid or CrmAttributeKind.Uniqueidentifier
            or CrmAttributeKind.Lookup or CrmAttributeKind.Customer or CrmAttributeKind.Owner =>
        [
            CrmFilterOperator.Eq, CrmFilterOperator.Ne
        ],
        _ => [CrmFilterOperator.Eq]
    };

    public static bool IsTypicallySortable(CrmAttributeKind kind) =>
        kind is CrmAttributeKind.DateTime or CrmAttributeKind.Money or CrmAttributeKind.Integer
            or CrmAttributeKind.Decimal or CrmAttributeKind.String;

    public static bool IsTypicallyFilterable(CrmAttributeKind kind) =>
        kind is not CrmAttributeKind.Unknown;

    public static bool IsSearchable(CrmAttributeKind kind) =>
        kind is CrmAttributeKind.String or CrmAttributeKind.Memo;

    public static string ToODataPropertyName(string logicalName, CrmAttributeKind kind)
    {
        if (kind is CrmAttributeKind.Lookup or CrmAttributeKind.Customer or CrmAttributeKind.Owner)
        {
            if (logicalName.StartsWith('_') && logicalName.EndsWith("_value", StringComparison.OrdinalIgnoreCase))
            {
                return logicalName;
            }

            return $"_{logicalName}_value";
        }

        return logicalName;
    }
}
