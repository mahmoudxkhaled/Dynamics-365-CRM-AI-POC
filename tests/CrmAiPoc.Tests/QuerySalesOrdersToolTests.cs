using System.Text.Json;
using CrmAiPoc.Api.Ai;
using CrmAiPoc.Api.Configuration;
using CrmAiPoc.Api.Crm;
using CrmAiPoc.Api.Crm.Mock;
using CrmAiPoc.Api.Crm.Models;
using CrmAiPoc.Api.Crm.Query;
using CrmAiPoc.Api.Crm.Validation;
using CrmAiPoc.Api.Security;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CrmAiPoc.Tests;

public class QuerySalesOrdersToolTests
{
    [Fact]
    public async Task QuerySalesOrders_AmountFilter_ReturnsMatchingMockOrders()
    {
        var tools = CreateTools();
        var json = await tools.query_sales_orders(
            operation: "list",
            filtersJson: """[{"field":"totalamount","operator":"gt","value":"10000"}]""",
            limit: 10);

        using var doc = JsonDocument.Parse(json);
        Assert.False(doc.RootElement.TryGetProperty("error", out _));
        Assert.True(doc.RootElement.GetProperty("totalReturned").GetInt32() >= 1);
        foreach (var order in doc.RootElement.GetProperty("salesOrders").EnumerateArray())
        {
            Assert.True(order.GetProperty("totalAmount").GetDecimal() > 10000m);
        }
    }

    [Fact]
    public async Task QuerySalesOrders_ContainsName()
    {
        var tools = CreateTools();
        var json = await tools.query_sales_orders(
            filtersJson: """[{"field":"name","operator":"contains","value":"ABC"}]""");

        using var doc = JsonDocument.Parse(json);
        Assert.Equal(1, doc.RootElement.GetProperty("totalReturned").GetInt32());
    }

    [Fact]
    public async Task QuerySalesOrders_ActiveState()
    {
        var tools = CreateTools();
        var json = await tools.query_sales_orders(
            filtersJson: """[{"field":"statecode","operator":"eq","value":"Active"}]""");

        using var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.GetProperty("totalReturned").GetInt32() >= 1);
    }

    [Fact]
    public async Task QuerySalesOrders_Count()
    {
        var tools = CreateTools();
        var json = await tools.query_sales_orders(operation: "count");
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(3, doc.RootElement.GetProperty("count").GetInt32());
        Assert.False(doc.RootElement.GetProperty("countMayBeCapped").GetBoolean());
    }

    [Fact]
    public async Task QuerySalesOrders_HighestValue_Sort()
    {
        var tools = CreateTools();
        var json = await tools.query_sales_orders(
            sortField: "totalamount",
            sortDirection: "desc",
            limit: 5);

        using var doc = JsonDocument.Parse(json);
        var amounts = doc.RootElement.GetProperty("salesOrders").EnumerateArray()
            .Select(o => o.GetProperty("totalAmount").GetDecimal())
            .ToList();
        Assert.Equal(amounts.OrderByDescending(x => x), amounts);
    }

    [Fact]
    public async Task QuerySalesOrders_UnsupportedField_Fails()
    {
        var tools = CreateTools();
        var json = await tools.query_sales_orders(
            filtersJson: """[{"field":"creditcard","operator":"eq","value":"1"}]""");

        using var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.GetProperty("error").GetBoolean());
    }

    [Fact]
    public async Task QuerySalesOrders_EmptyResults_Graceful()
    {
        var tools = CreateTools();
        var json = await tools.query_sales_orders(
            filtersJson: """[{"field":"name","operator":"contains","value":"ZZZ_NO_MATCH"}]""");

        using var doc = JsonDocument.Parse(json);
        Assert.Equal(0, doc.RootElement.GetProperty("totalReturned").GetInt32());
    }

    [Fact]
    public void Formatter_CountCapped_ClarifiesIncomplete()
    {
        var text = LocalCrmReplyFormatter.FormatQueryResult(new SalesOrderQueryExecutionResult
        {
            Operation = SalesOrderQueryOperation.Count,
            RelativeRequestUri = "salesorders?$count=true",
            AppliedFilterSummary = ["statecode eq 0"],
            Count = 5000,
            CountMayBeCapped = true,
            ProviderName = "Dynamics365"
        }, "en", null);

        Assert.Contains("at least 5000", text);
        Assert.Contains("may be higher", text);
    }

    [Fact]
    public void Formatter_List_IncludesAmountAndStatus()
    {
        var text = LocalCrmReplyFormatter.FormatQueryResult(new SalesOrderQueryExecutionResult
        {
            Operation = SalesOrderQueryOperation.List,
            RelativeRequestUri = "salesorders",
            AppliedFilterSummary = ["totalamount gt 10000"],
            SortSummary = "totalamount desc",
            Limit = 5,
            Items =
            [
                new CrmSalesOrder
                {
                    SalesOrderId = Guid.NewGuid(),
                    Name = "Demo",
                    OrderNumber = "ORD-9",
                    CreatedOn = DateTimeOffset.UtcNow,
                    TotalAmount = 12000m,
                    StateLabel = "Active",
                    StatusLabel = "New"
                }
            ],
            ProviderName = "Mock"
        }, "en", null);

        Assert.Contains("12,000.00", text);
        Assert.Contains("Active", text);
        Assert.Contains("Applied filters", text);
        Assert.Contains("were not sent to the AI model", text);
    }

    [Fact]
    public void StatusOptionMapping_VerifiedLabels()
    {
        Assert.True(SalesOrderFieldCatalog.TryResolveStateCode("Active", out var state));
        Assert.Equal(0, state);
        Assert.True(SalesOrderFieldCatalog.TryResolveStatusCode("New", out var status));
        Assert.Equal(1, status);
        Assert.Equal("In Progress", SalesOrderFieldCatalog.GetStatusLabel(3));
    }

    private static CrmAiTools CreateTools()
    {
        var options = Options.Create(new CrmOptions
        {
            AllowSendLiveDataToAi = false,
            MaxPageSize = 25,
            DefaultPageSize = 10,
            BusinessTimeZoneId = "Africa/Cairo"
        });

        return new CrmAiTools(
            new MockCrmDataProvider(NullLogger<MockCrmDataProvider>.Instance, options),
            new CrmAuthorizationService(options, NullLogger<CrmAuthorizationService>.Instance),
            new CrmArgumentValidator(options),
            options,
            NullLogger<CrmAiTools>.Instance);
    }
}
