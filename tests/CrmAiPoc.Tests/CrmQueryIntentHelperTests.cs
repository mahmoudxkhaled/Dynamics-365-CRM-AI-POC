using CrmAiPoc.Api.Ai;
using Microsoft.Extensions.AI;

namespace CrmAiPoc.Tests;

public class CrmQueryIntentHelperTests
{
    [Theory]
    [InlineData("Show me the latest 5 accounts", "account")]
    [InlineData("هاتلي آخر 5 Sales Orders", "salesorder")]
    [InlineData("عدد أوامر البيع", "salesorder")]
    [InlineData("show contacts for this account", "contact")]
    [InlineData("Show me the latest 5 leads", "lead")]
    public void Detects_Entity_From_Current_Message(string message, string expected)
    {
        Assert.Equal(expected, CrmQueryIntentHelper.DetectEntityLogicalName(message));
    }

    [Fact]
    public void Corrects_Sticky_Account_Entity_When_User_Asks_SalesOrders()
    {
        var sticky = new FunctionCallContent(
            "1",
            "query_crm",
            new Dictionary<string, object?>
            {
                ["entity"] = "account",
                ["operation"] = "list",
                ["limit"] = 5
            });

        var corrected = CrmQueryIntentHelper.CorrectToolCall("هاتلي آخر 5 Sales Orders", sticky);

        Assert.Equal("query_crm", corrected.Name);
        Assert.Equal("salesorder", corrected.Arguments!["entity"]?.ToString());
        Assert.Equal(5, Convert.ToInt32(corrected.Arguments["limit"]));
    }

    [Fact]
    public void Corrects_Sticky_SalesOrder_When_User_Asks_Accounts()
    {
        var sticky = new FunctionCallContent(
            "1",
            "query_crm",
            new Dictionary<string, object?>
            {
                ["entity"] = "salesorder",
                ["limit"] = 5
            });

        var corrected = CrmQueryIntentHelper.CorrectToolCall("Show me the latest 5 accounts", sticky);
        Assert.Equal("account", corrected.Arguments!["entity"]?.ToString());
    }

    [Fact]
    public void Highest_Value_SalesOrders_Forces_TotalAmount_Desc()
    {
        var incomplete = new FunctionCallContent(
            "1",
            "query_crm",
            new Dictionary<string, object?>
            {
                ["entity"] = "salesorder",
                ["operation"] = "list",
                ["limit"] = 10
            });

        var corrected = CrmQueryIntentHelper.CorrectToolCall("Highest-value sales orders", incomplete);

        Assert.Equal("salesorder", corrected.Arguments!["entity"]?.ToString());
        Assert.Equal("totalamount", corrected.Arguments["sortField"]?.ToString());
        Assert.Equal("desc", corrected.Arguments["sortDirection"]?.ToString());
    }

    [Fact]
    public void Lowest_Value_SalesOrders_Forces_TotalAmount_Asc()
    {
        var call = new FunctionCallContent(
            "1",
            "query_crm",
            new Dictionary<string, object?>
            {
                ["entity"] = "salesorder",
                ["operation"] = "list",
                ["sortField"] = "createdon",
                ["sortDirection"] = "desc"
            });

        var corrected = CrmQueryIntentHelper.CorrectToolCall("lowest value sales orders", call);
        Assert.Equal("totalamount", corrected.Arguments!["sortField"]?.ToString());
        Assert.Equal("asc", corrected.Arguments["sortDirection"]?.ToString());
    }
}
