using CrmAiPoc.Api.Crm.FetchXml;
using CrmAiPoc.Api.Crm.Generic;

namespace CrmAiPoc.Tests;

/// <summary>REP-02: identity vs display columns; no entity-specific hardcoding.</summary>
public sealed class Rep02DisplayColumnPolicyTests
{
    [Fact]
    public void InferColumnsFromRows_Does_Not_Append_Unplanned_PrimaryId()
    {
        var planned = new[]
        {
            new CrmTableColumn { Key = "name", Label = "Name", Kind = "string", LogicalName = "name" },
            new CrmTableColumn { Key = "totalamount", Label = "Total Amount", Kind = "money", LogicalName = "totalamount" }
        };
        var rows = new IReadOnlyDictionary<string, object?>[]
        {
            new Dictionary<string, object?>
            {
                ["name"] = "W-100",
                ["totalamount"] = 42m,
                ["omega_widgetid"] = "11111111-1111-1111-1111-111111111111"
            }
        };

        var cols = FetchXmlQueryExecutor.InferColumnsFromRows(planned, rows, "omega_widgetid");
        Assert.Equal(2, cols.Count);
        Assert.DoesNotContain(cols, c => c.Key.Equals("omega_widgetid", StringComparison.OrdinalIgnoreCase));
        Assert.True(rows[0].ContainsKey("omega_widgetid"));
    }

    [Fact]
    public void ApplyListDisplayColumnPolicy_Hides_Uniqueidentifier_And_PrimaryId()
    {
        var columns = new[]
        {
            new CrmTableColumn { Key = "name", Label = "Name", Kind = "string", LogicalName = "name" },
            new CrmTableColumn
            {
                Key = "omega_widgetid",
                Label = "omega_widgetid",
                Kind = "uniqueidentifier",
                LogicalName = "omega_widgetid"
            },
            new CrmTableColumn { Key = "ownerid", Label = "Owner", Kind = "Uniqueidentifier", LogicalName = "ownerid" },
            new CrmTableColumn { Key = "amount", Label = "Amount", Kind = "money", LogicalName = "totalamount" }
        };

        var visible = FetchXmlQueryExecutor.ApplyListDisplayColumnPolicy(columns, "omega_widgetid");
        Assert.Equal(2, visible.Count);
        Assert.Contains(visible, c => c.Key == "name");
        Assert.Contains(visible, c => c.Key == "amount");
    }

    [Fact]
    public void ApplyListDisplayColumnPolicy_Keeps_NonIdentity_Columns_With_Arbitrary_Names()
    {
        var columns = new[]
        {
            new CrmTableColumn { Key = "gamma_label", Label = "Label", Kind = "string", LogicalName = "te_label" },
            new CrmTableColumn { Key = "gamma_score", Label = "Score", Kind = "decimal", LogicalName = "te_score" }
        };

        var visible = FetchXmlQueryExecutor.ApplyListDisplayColumnPolicy(columns, "gamma_caseid");
        Assert.Equal(2, visible.Count);
    }

    [Fact]
    public void InferColumnsFromRows_Keeps_Explicitly_Planned_PrimaryId_Until_Display_Policy()
    {
        var planned = new[]
        {
            new CrmTableColumn
            {
                Key = "omega_widgetid",
                Label = "Widget Id",
                Kind = "uniqueidentifier",
                LogicalName = "omega_widgetid"
            },
            new CrmTableColumn { Key = "name", Label = "Name", Kind = "string", LogicalName = "name" }
        };
        var rows = new IReadOnlyDictionary<string, object?>[]
        {
            new Dictionary<string, object?>
            {
                ["omega_widgetid"] = "22222222-2222-2222-2222-222222222222",
                ["name"] = "Widget"
            }
        };

        var inferred = FetchXmlQueryExecutor.InferColumnsFromRows(planned, rows, "omega_widgetid");
        Assert.Contains(inferred, c => c.Key == "omega_widgetid");

        var visible = FetchXmlQueryExecutor.ApplyListDisplayColumnPolicy(inferred, "omega_widgetid");
        Assert.DoesNotContain(visible, c => c.Key == "omega_widgetid");
        Assert.Contains(visible, c => c.Key == "name");
        Assert.Equal("22222222-2222-2222-2222-222222222222", rows[0]["omega_widgetid"]);
    }

    [Fact]
    public void InferColumnsFromRows_Appends_Link_Alias_Text_But_Not_Raw_Guid_Aliases()
    {
        var planned = new[]
        {
            new CrmTableColumn { Key = "name", Label = "Name", Kind = "string", LogicalName = "name" }
        };
        var rows = new IReadOnlyDictionary<string, object?>[]
        {
            new Dictionary<string, object?>
            {
                ["name"] = "Order A",
                ["acct.name"] = "Acme",
                ["acct.accountid"] = "33333333-3333-3333-3333-333333333333"
            }
        };

        var cols = FetchXmlQueryExecutor.InferColumnsFromRows(planned, rows, "salesorderid");
        Assert.Contains(cols, c => c.Key == "acct.name");
        Assert.DoesNotContain(cols, c => c.Key == "acct.accountid");
    }
}
