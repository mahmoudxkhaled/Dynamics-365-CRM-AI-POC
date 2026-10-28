using CrmAiPoc.Api.Ai;
using Microsoft.Extensions.AI;

namespace CrmAiPoc.Tests;

public class SalesOrderIntentHelperTests
{
    [Theory]
    [InlineData("عدد ال Sales Order")]
    [InlineData("عدد أوامر البيع")]
    [InlineData("كم عدد الـ Sales Orders")]
    [InlineData("Count sales orders")]
    [InlineData("How many sales orders are there?")]
    public void Detects_SalesOrderCount(string message)
    {
        Assert.True(SalesOrderIntentHelper.LooksLikeSalesOrderCount(message));
    }

    [Theory]
    [InlineData("كم عدد العملاء")]
    [InlineData("how many accounts")]
    [InlineData("هات آخر 10 Sales Orders")]
    public void DoesNot_Misclassify_OtherQuestions(string message)
    {
        Assert.False(SalesOrderIntentHelper.LooksLikeSalesOrderCount(message));
    }

    [Fact]
    public void Corrects_CountAccounts_To_QuerySalesOrdersCount()
    {
        var original = new FunctionCallContent(
            "1",
            "count_accounts",
            new Dictionary<string, object?>());

        var corrected = SalesOrderIntentHelper.CorrectToolCall("عدد ال Sales Order", original);
        Assert.Equal("query_crm", corrected.Name);
        Assert.Equal("count", corrected.Arguments!["operation"]?.ToString());
        Assert.Equal("salesorder", corrected.Arguments["entity"]?.ToString());
    }

    [Fact]
    public void Corrects_ListOperation_To_Count()
    {
        var original = new FunctionCallContent(
            "1",
            "query_sales_orders",
            new Dictionary<string, object?> { ["operation"] = "list", ["limit"] = 10 });

        var corrected = SalesOrderIntentHelper.CorrectToolCall("عدد أوامر البيع", original);
        Assert.Equal("query_crm", corrected.Name);
        Assert.Equal("count", corrected.Arguments!["operation"]?.ToString());
        Assert.Equal("salesorder", corrected.Arguments["entity"]?.ToString());
    }

    [Fact]
    public void Remaps_GetSalesOrders_To_QueryCrm()
    {
        var original = new FunctionCallContent("1", "get_sales_orders", new Dictionary<string, object?> { ["limit"] = 5 });
        var corrected = SalesOrderIntentHelper.CorrectToolCall("Show me the latest 5 sales orders", original);
        Assert.Equal("query_crm", corrected.Name);
        Assert.Equal("salesorder", corrected.Arguments!["entity"]?.ToString());
        Assert.Equal("list", corrected.Arguments["operation"]?.ToString());
    }
}
