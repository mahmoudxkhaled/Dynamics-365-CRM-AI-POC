using System.Text.Json;
using CrmAiPoc.Api.Crm.Metadata;

namespace CrmAiPoc.Tests;

/// <summary>
/// SEM-03.1: type-specific OptionSet OData requests and parsing (no live Dynamics).
/// </summary>
public sealed class OptionSetMetadataRequestTests
{
    [Theory]
    [InlineData(CrmAttributeKind.Picklist, null, "PicklistAttributeMetadata", "OptionSet($select=Options)")]
    [InlineData(CrmAttributeKind.Picklist, "Picklist", "PicklistAttributeMetadata", "OptionSet($select=Options)")]
    [InlineData(CrmAttributeKind.Picklist, "MultiSelectPicklist", "MultiSelectPicklistAttributeMetadata", "OptionSet($select=Options)")]
    [InlineData(CrmAttributeKind.State, "State", "StateAttributeMetadata", "OptionSet($select=Options)")]
    [InlineData(CrmAttributeKind.Status, "Status", "StatusAttributeMetadata", "OptionSet($select=Options)")]
    [InlineData(CrmAttributeKind.Boolean, "Boolean", "BooleanAttributeMetadata", "OptionSet($select=TrueOption,FalseOption)")]
    public void Resolves_Exact_Type_Specific_Request(
        CrmAttributeKind kind,
        string? typeName,
        string expectedMetaType,
        string expectedExpand)
    {
        var attr = new CrmAttributeMetadata
        {
            LogicalName = "samplefield",
            ODataPropertyName = "samplefield",
            Kind = kind,
            AttributeTypeName = typeName
        };

        var request = OptionSetMetadataRequest.TryResolve(attr);
        Assert.NotNull(request);
        Assert.Equal(expectedMetaType, request!.Value.MetadataType);
        Assert.Equal(expectedExpand, request.Value.ExpandSelect);
    }

    [Fact]
    public void Picklist_Does_Not_Probe_MultiSelect_Or_Boolean_Expand()
    {
        var attr = new CrmAttributeMetadata
        {
            LogicalName = "prioritycode",
            ODataPropertyName = "prioritycode",
            Kind = CrmAttributeKind.Picklist,
            AttributeTypeName = "Picklist"
        };

        var request = OptionSetMetadataRequest.TryResolve(attr)!.Value;
        Assert.Equal("PicklistAttributeMetadata", request.MetadataType);
        Assert.DoesNotContain("TrueOption", request.ExpandSelect, StringComparison.Ordinal);
        Assert.DoesNotContain("FalseOption", request.ExpandSelect, StringComparison.Ordinal);
        Assert.DoesNotContain("MultiSelect", request.MetadataType, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildPath_Matches_Dynamics_V91_Shape()
    {
        var request = new OptionSetMetadataRequest(
            "PicklistAttributeMetadata",
            "OptionSet($select=Options)");

        var path = OptionSetMetadataRequest.BuildPath("alpha_case", "prioritycode", request);

        Assert.Equal(
            "EntityDefinitions(LogicalName='alpha_case')" +
            "/Attributes(LogicalName='prioritycode')" +
            "/Microsoft.Dynamics.CRM.PicklistAttributeMetadata" +
            "?$select=LogicalName&$expand=OptionSet($select=Options)",
            path);
    }

    [Fact]
    public void BuildPath_Escapes_OData_String_Literals()
    {
        var request = new OptionSetMetadataRequest(
            "StatusAttributeMetadata",
            "OptionSet($select=Options)");
        var path = OptionSetMetadataRequest.BuildPath("te_widget", "te_status'code", request);
        Assert.Contains("LogicalName='te_status''code'", path, StringComparison.Ordinal);
    }

    [Fact]
    public void Boolean_Path_Uses_TrueFalse_Options_Only()
    {
        var attr = new CrmAttributeMetadata
        {
            LogicalName = "donotemail",
            ODataPropertyName = "donotemail",
            Kind = CrmAttributeKind.Boolean,
            AttributeTypeName = "Boolean"
        };
        var request = OptionSetMetadataRequest.TryResolve(attr)!.Value;
        var path = OptionSetMetadataRequest.BuildPath("contact", "donotemail", request);

        Assert.Contains("BooleanAttributeMetadata", path, StringComparison.Ordinal);
        Assert.Contains("OptionSet($select=TrueOption,FalseOption)", path, StringComparison.Ordinal);
        Assert.DoesNotContain("Options,TrueOption", path, StringComparison.Ordinal);
    }

    [Fact]
    public void Parses_Picklist_Options_Labels_And_Values()
    {
        using var doc = JsonDocument.Parse(
            """
            {
              "Options": [
                { "Value": 1, "Label": { "UserLocalizedLabel": { "Label": "High", "LanguageCode": 1033 } } },
                { "Value": 2, "Label": { "UserLocalizedLabel": { "Label": "Low", "LanguageCode": 1033 } } }
              ]
            }
            """);

        var byLabel = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var byValue = new Dictionary<int, string>();
        DynamicsCrmMetadataService.ParseOptionSetElement(
            doc.RootElement,
            CrmAttributeKind.Picklist,
            byLabel,
            byValue);

        Assert.Equal(2, byValue.Count);
        Assert.Equal(1, byLabel["High"]);
        Assert.Equal("Low", byValue[2]);
    }

    [Fact]
    public void Parses_Boolean_TrueFalse_Options()
    {
        using var doc = JsonDocument.Parse(
            """
            {
              "TrueOption": { "Value": 1, "Label": { "UserLocalizedLabel": { "Label": "Yes" } } },
              "FalseOption": { "Value": 0, "Label": { "UserLocalizedLabel": { "Label": "No" } } }
            }
            """);

        var byLabel = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var byValue = new Dictionary<int, string>();
        DynamicsCrmMetadataService.ParseOptionSetElement(
            doc.RootElement,
            CrmAttributeKind.Boolean,
            byLabel,
            byValue);

        Assert.Equal(1, byLabel["Yes"]);
        Assert.Equal(0, byLabel["No"]);
        Assert.Equal("Yes", byValue[1]);
    }

    [Fact]
    public void Boolean_Parser_Ignores_Options_Array()
    {
        using var doc = JsonDocument.Parse(
            """
            {
              "Options": [ { "Value": 9, "Label": { "UserLocalizedLabel": { "Label": "Ignored" } } } ],
              "TrueOption": { "Value": 1, "Label": { "UserLocalizedLabel": { "Label": "Yes" } } },
              "FalseOption": { "Value": 0, "Label": { "UserLocalizedLabel": { "Label": "No" } } }
            }
            """);

        var byLabel = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var byValue = new Dictionary<int, string>();
        DynamicsCrmMetadataService.ParseOptionSetElement(
            doc.RootElement,
            CrmAttributeKind.Boolean,
            byLabel,
            byValue);

        Assert.False(byLabel.ContainsKey("Ignored"));
        Assert.Equal(2, byValue.Count);
    }

    [Fact]
    public void SanitizeDynamicsErrorBody_Extracts_Code_And_Message()
    {
        var body =
            """{"error":{"code":"0x0","message":"Could not find a property named 'TrueOption' on type 'Microsoft.Dynamics.CRM.OptionSetMetadata'."}}""";

        var sanitized = DynamicsCrmMetadataService.SanitizeDynamicsErrorBody(body);

        Assert.Contains("TrueOption", sanitized, StringComparison.Ordinal);
        Assert.Contains("OptionSetMetadata", sanitized, StringComparison.Ordinal);
        Assert.DoesNotContain("\n", sanitized, StringComparison.Ordinal);
    }

    [Fact]
    public void NonChoice_Kind_Does_Not_Resolve_Request()
    {
        var attr = new CrmAttributeMetadata
        {
            LogicalName = "name",
            ODataPropertyName = "name",
            Kind = CrmAttributeKind.String
        };
        Assert.Null(OptionSetMetadataRequest.TryResolve(attr));
    }
}
