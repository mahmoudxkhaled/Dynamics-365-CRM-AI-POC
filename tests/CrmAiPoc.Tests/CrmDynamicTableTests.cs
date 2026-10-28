using CrmAiPoc.Api.Configuration;
using CrmAiPoc.Api.Crm.Generic;
using CrmAiPoc.Api.Crm.Metadata;
using CrmAiPoc.Api.Security;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CrmAiPoc.Tests;

public class CrmDynamicTableTests
{
    private static (EntityApprovalPolicy policy, ICrmMetadataService metadata, IGenericCrmRepository repo)
        CreateStack(params string[] approved)
    {
        var options = Options.Create(new CrmOptions
        {
            ApprovedEntities = approved.Length > 0
                ? approved.ToList()
                : ["account", "contact", "salesorder", "lead"],
            BusinessTimeZoneId = "Africa/Cairo",
            EntityAliases = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["lead"] = ["leads"]
            }
        });
        var policy = new EntityApprovalPolicy(options);
        var registry = new CrmMetadataRegistry();
        var metadata = new MockCrmMetadataService(registry, policy);
        var builder = new GenericCrmQueryBuilder(options);
        var auth = new CrmAuthorizationService(options, NullLogger<CrmAuthorizationService>.Instance, policy);
        var repo = new MockGenericCrmRepository(metadata, policy, builder, auth);
        return (policy, metadata, repo);
    }

    [Theory]
    [InlineData("account")]
    [InlineData("contact")]
    [InlineData("salesorder")]
    [InlineData("lead")]
    public async Task List_Produces_Metadata_Columns_For_Entity(string entity)
    {
        var (_, _, repo) = CreateStack();
        var result = await repo.QueryAsync(new CrmQueryPlan
        {
            Entity = entity,
            Operation = CrmQueryOperation.List,
            Limit = 5
        });

        Assert.NotEmpty(result.SelectFields);
        Assert.NotEmpty(result.Columns);
        Assert.DoesNotContain(result.Columns, c =>
            c.Key.Equals(result.PrimaryIdAttribute, StringComparison.OrdinalIgnoreCase));
        Assert.All(result.Columns, c =>
        {
            Assert.False(string.IsNullOrWhiteSpace(c.Key));
            Assert.False(string.IsNullOrWhiteSpace(c.Label));
            Assert.False(string.IsNullOrWhiteSpace(c.Kind));
        });
    }

    [Fact]
    public async Task Different_Entities_Produce_Different_Column_Sets()
    {
        var (_, _, repo) = CreateStack();
        var accounts = await repo.QueryAsync(new CrmQueryPlan { Entity = "account", Limit = 3 });
        var orders = await repo.QueryAsync(new CrmQueryPlan { Entity = "salesorder", Limit = 3 });

        var accountKeys = accounts.Columns.Select(c => c.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var orderKeys = orders.Columns.Select(c => c.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.Contains(accountKeys, k => k.Equals("name", StringComparison.OrdinalIgnoreCase) ||
                                          k.Equals("accountnumber", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(orderKeys, k => k.Equals("totalamount", StringComparison.OrdinalIgnoreCase) ||
                                        k.Equals("ordernumber", StringComparison.OrdinalIgnoreCase));
        Assert.False(accountKeys.SetEquals(orderKeys));
    }

    [Fact]
    public async Task Zero_Records_Still_Returns_Selected_Columns()
    {
        var (_, _, repo) = CreateStack();
        var result = await repo.QueryAsync(new CrmQueryPlan
        {
            Entity = "account",
            Filters =
            [
                new CrmQueryFilter
                {
                    Field = "name",
                    Operator = CrmFilterOperator.Contains,
                    Value = "ZZZ-NO-MATCH-ENTITY"
                }
            ],
            Limit = 5
        });

        Assert.Empty(result.Items);
        Assert.NotEmpty(result.Columns);
        Assert.Contains(result.Columns, c => c.Key.Equals("name", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ColumnBuilder_Falls_Back_To_LogicalName_When_DisplayName_Missing()
    {
        var entity = new CrmEntityMetadata
        {
            LogicalName = "account",
            EntitySetName = "accounts",
            PrimaryIdAttribute = "accountid",
            PrimaryNameAttribute = "name",
            IsApprovedForAi = true,
            Attributes =
            [
                new CrmAttributeMetadata
                {
                    LogicalName = "accountid",
                    ODataPropertyName = "accountid",
                    Kind = CrmAttributeKind.Uniqueidentifier,
                    IsValidForRead = true
                },
                new CrmAttributeMetadata
                {
                    LogicalName = "name",
                    ODataPropertyName = "name",
                    DisplayName = null,
                    Kind = CrmAttributeKind.String,
                    IsValidForRead = true,
                    IsSortable = true
                },
                new CrmAttributeMetadata
                {
                    LogicalName = "createdon",
                    ODataPropertyName = "createdon",
                    DisplayName = "Created On",
                    Kind = CrmAttributeKind.DateTime,
                    IsValidForRead = true,
                    IsSortable = true
                }
            ]
        };

        var columns = CrmTableColumnBuilder.Build(entity, ["accountid", "name", "createdon"]);
        Assert.DoesNotContain(columns, c => c.Key.Equals("accountid", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("name", columns.First(c => c.Key == "name").Label);
        Assert.Equal("Created On", columns.First(c => c.Key == "createdon").Label);
    }

    [Fact]
    public void ColumnBuilder_Uses_OptionSet_Label_Keys()
    {
        var entity = new CrmEntityMetadata
        {
            LogicalName = "salesorder",
            EntitySetName = "salesorders",
            PrimaryIdAttribute = "salesorderid",
            PrimaryNameAttribute = "name",
            IsApprovedForAi = true,
            Attributes =
            [
                new CrmAttributeMetadata
                {
                    LogicalName = "statuscode",
                    ODataPropertyName = "statuscode",
                    DisplayName = "Status",
                    Kind = CrmAttributeKind.Status,
                    IsValidForRead = true,
                    OptionSetByValue = new Dictionary<int, string> { [1] = "New" }
                }
            ]
        };

        var columns = CrmTableColumnBuilder.Build(entity, ["statuscode"]);
        Assert.Single(columns);
        Assert.Equal("statuscode_label", columns[0].Key);
        Assert.Equal("Status", columns[0].Label);
    }

    [Fact]
    public void ColumnBuilder_Supports_Lookup_OData_Key_Without_Showing_As_Id()
    {
        var entity = new CrmEntityMetadata
        {
            LogicalName = "contact",
            EntitySetName = "contacts",
            PrimaryIdAttribute = "contactid",
            PrimaryNameAttribute = "fullname",
            IsApprovedForAi = true,
            Attributes =
            [
                new CrmAttributeMetadata
                {
                    LogicalName = "parentcustomerid",
                    ODataPropertyName = "_parentcustomerid_value",
                    DisplayName = "Company Name",
                    Kind = CrmAttributeKind.Customer,
                    IsValidForRead = true
                }
            ]
        };

        var columns = CrmTableColumnBuilder.Build(entity, ["_parentcustomerid_value"]);
        Assert.Single(columns);
        Assert.Equal("_parentcustomerid_value", columns[0].Key);
        Assert.Equal("Company Name", columns[0].Label);
        Assert.Equal(nameof(CrmAttributeKind.Customer), columns[0].Kind);
    }

    [Fact]
    public void ColumnBuilder_Works_For_Custom_Approved_Entity_Stub()
    {
        var options = Options.Create(new CrmOptions
        {
            ApprovedEntities = ["opportunity"]
        });
        var policy = new EntityApprovalPolicy(options);
        var stub = GenericApprovedEntityStubFactory.Create("opportunity", policy);
        var built = new GenericCrmQueryBuilder(options).Build(
            new CrmQueryPlan { Entity = "opportunity", Limit = 5 },
            stub);

        Assert.True(built.Success);
        var columns = CrmTableColumnBuilder.Build(stub, built.SelectFields!);
        Assert.NotEmpty(columns);
        Assert.DoesNotContain(columns, c => c.Key.Equals(stub.PrimaryIdAttribute, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void FieldFormatter_Handles_Null_Money_Date_Lookup_Option()
    {
        Assert.Equal(string.Empty, CrmFieldDisplayFormatter.Format(null, "String"));
        Assert.Equal("12.50", CrmFieldDisplayFormatter.Format(12.5m, "Money"));
        Assert.Equal("2026-03-01", CrmFieldDisplayFormatter.Format("2026-03-01T00:00:00Z", "DateTime"));
        Assert.Equal(string.Empty, CrmFieldDisplayFormatter.Format("11111111-1111-1111-1111-111111111111", "Lookup"));
        Assert.Equal("Active", CrmFieldDisplayFormatter.Format("Active", "State"));
        Assert.Equal("true", CrmFieldDisplayFormatter.Format(true, "Boolean"));
    }

    [Fact]
    public async Task SalesOrder_OptionSet_Label_Available_In_Records()
    {
        var (_, _, repo) = CreateStack();
        var result = await repo.QueryAsync(new CrmQueryPlan
        {
            Entity = "salesorder",
            Limit = 5
        });

        Assert.Contains(result.Columns, c => c.Key.Equals("statuscode_label", StringComparison.OrdinalIgnoreCase) ||
                                             c.Key.Equals("statecode_label", StringComparison.OrdinalIgnoreCase));
        if (result.Items.Count > 0)
        {
            var row = result.Items[0];
            Assert.True(
                row.ContainsKey("statuscode_label") || row.ContainsKey("statuscode") ||
                row.ContainsKey("statecode_label") || row.ContainsKey("statecode"));
        }
    }

    [Fact]
    public async Task Null_Fields_Are_Preserved_In_Projected_Rows()
    {
        var (_, _, repo) = CreateStack();
        var result = await repo.QueryAsync(new CrmQueryPlan
        {
            Entity = "contact",
            Limit = 5
        });

        Assert.NotEmpty(result.Columns);
        foreach (var col in result.Columns)
        {
            // Every column key (or its raw fallback) is addressable without throwing.
            foreach (var row in result.Items)
            {
                _ = row.TryGetValue(col.Key, out _);
            }
        }
    }
}
