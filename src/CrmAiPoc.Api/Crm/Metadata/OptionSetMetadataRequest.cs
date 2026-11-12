namespace CrmAiPoc.Api.Crm.Metadata;

/// <summary>
/// Type-specific Dynamics v9.1 OptionSet metadata request.
/// One attribute kind → one metadata cast → one compatible $expand shape.
/// </summary>
public readonly record struct OptionSetMetadataRequest(
    string MetadataType,
    string ExpandSelect)
{
    /// <summary>
    /// Resolves exactly one Dynamics AttributeMetadata cast and OptionSet $expand
    /// from the already-known CRM attribute kind / type name.
    /// Does not probe alternate types.
    /// </summary>
    public static OptionSetMetadataRequest? TryResolve(CrmAttributeMetadata attr)
    {
        if (attr is null)
        {
            return null;
        }

        return attr.Kind switch
        {
            CrmAttributeKind.State => new(
                "StateAttributeMetadata",
                "OptionSet($select=Options)"),
            CrmAttributeKind.Status => new(
                "StatusAttributeMetadata",
                "OptionSet($select=Options)"),
            CrmAttributeKind.Boolean => new(
                "BooleanAttributeMetadata",
                "OptionSet($select=TrueOption,FalseOption)"),
            CrmAttributeKind.Picklist when IsMultiSelect(attr) => new(
                "MultiSelectPicklistAttributeMetadata",
                "OptionSet($select=Options)"),
            CrmAttributeKind.Picklist => new(
                "PicklistAttributeMetadata",
                "OptionSet($select=Options)"),
            _ => null
        };
    }

    public static bool IsMultiSelect(CrmAttributeMetadata attr)
    {
        var typeName = attr.AttributeTypeName ?? string.Empty;
        return typeName.Contains("MultiSelect", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Builds the relative Dynamics Web API path for OptionSet enrichment.
    /// </summary>
    public static string BuildPath(string entityLogicalName, string attributeLogicalName, OptionSetMetadataRequest request)
    {
        var entity = EscapeODataString(entityLogicalName);
        var attribute = EscapeODataString(attributeLogicalName);
        return
            $"EntityDefinitions(LogicalName='{entity}')" +
            $"/Attributes(LogicalName='{attribute}')" +
            $"/Microsoft.Dynamics.CRM.{request.MetadataType}" +
            $"?$select=LogicalName&$expand={request.ExpandSelect}";
    }

    private static string EscapeODataString(string value) =>
        (value ?? string.Empty).Replace("'", "''", StringComparison.Ordinal);
}
