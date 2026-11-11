using System.Text.Json;
using CrmAiPoc.Api.Crm.Generic;
using CrmAiPoc.Api.Crm.Metadata;

namespace CrmAiPoc.Tests;

/// <summary>
/// Regression: table column labels must come from Dynamics DisplayName (1033 / UserLocalizedLabel),
/// not LogicalName, once attributes are parsed into the metadata cache.
/// </summary>
public class DynamicsCrmDisplayNameMappingTests
{
    private const string AccountNumberAttributeJson = """
        {
          "value": [
            {
              "@odata.type": "#Microsoft.Dynamics.CRM.StringAttributeMetadata",
              "LogicalName": "accountnumber",
              "AttributeType": "String",
              "SchemaName": "AccountNumber",
              "IsValidForRead": true,
              "IsCustomAttribute": false,
              "IsValidForGrid": true,
              "DisplayName": {
                "LocalizedLabels": [
                  {
                    "Label": "Account Number",
                    "LanguageCode": 1033,
                    "IsManaged": true
                  },
                  {
                    "Label": "رقم الحساب",
                    "LanguageCode": 1025,
                    "IsManaged": true
                  }
                ],
                "UserLocalizedLabel": {
                  "Label": "Account Number",
                  "LanguageCode": 1033,
                  "IsManaged": true
                }
              }
            },
            {
              "LogicalName": "createdon",
              "AttributeType": "DateTime",
              "SchemaName": "CreatedOn",
              "IsValidForRead": true,
              "IsCustomAttribute": false,
              "IsValidForGrid": true,
              "DisplayName": {
                "LocalizedLabels": [
                  { "Label": "Created On", "LanguageCode": 1033 }
                ],
                "UserLocalizedLabel": { "Label": "Created On", "LanguageCode": 1033 }
              }
            },
            {
              "LogicalName": "modifiedbyname",
              "AttributeType": "String",
              "SchemaName": "ModifiedByName",
              "IsValidForRead": true,
              "IsCustomAttribute": false,
              "IsValidForGrid": true,
              "DisplayName": {
                "LocalizedLabels": [],
                "UserLocalizedLabel": null
              }
            }
          ]
        }
        """;

    [Fact]
    public void GetLocalizedLabel_Prefers_English_1033_Over_Arabic()
    {
        using var doc = JsonDocument.Parse(AccountNumberAttributeJson);
        var accountNumber = doc.RootElement.GetProperty("value").EnumerateArray().First();

        var label = DynamicsCrmMetadataService.GetLocalizedLabelForTests(accountNumber, "DisplayName");

        Assert.Equal("Account Number", label);
    }

    [Fact]
    public void ParseAttributes_Stores_DisplayName_From_Dynamics_Metadata()
    {
        var attrs = DynamicsCrmMetadataService.ParseAttributesForTests(AccountNumberAttributeJson);

        var accountNumber = Assert.Single(attrs, a => a.LogicalName == "accountnumber");
        Assert.Equal("Account Number", accountNumber.DisplayName);

        var createdOn = Assert.Single(attrs, a => a.LogicalName == "createdon");
        Assert.Equal("Created On", createdOn.DisplayName);

        var companion = Assert.Single(attrs, a => a.LogicalName == "modifiedbyname");
        Assert.Null(companion.DisplayName);
    }

    [Fact]
    public void ColumnBuilder_Maps_Parsed_DisplayNames_Into_GenericCrmQueryResult_Columns()
    {
        var attrs = DynamicsCrmMetadataService.ParseAttributesForTests(AccountNumberAttributeJson);
        var entity = new CrmEntityMetadata
        {
            LogicalName = "account",
            EntitySetName = "accounts",
            PrimaryIdAttribute = "accountid",
            PrimaryNameAttribute = "name",
            IsApprovedForAi = true,
            Attributes = attrs
                .Append(new CrmAttributeMetadata
                {
                    LogicalName = "accountid",
                    ODataPropertyName = "accountid",
                    Kind = CrmAttributeKind.Uniqueidentifier,
                    IsValidForRead = true
                })
                .ToArray()
        };

        var columns = CrmTableColumnBuilder.Build(entity, ["accountnumber", "createdon"]);

        Assert.Equal("Account Number", columns.First(c => c.Key == "accountnumber").Label);
        Assert.Equal("accountnumber", columns.First(c => c.Key == "accountnumber").LogicalName);
        Assert.Equal("Created On", columns.First(c => c.LogicalName == "createdon").Label);
    }
}
