using System.Text.Json;
using CrmAiPoc.Api.Ai;
using CrmAiPoc.Api.Configuration;
using CrmAiPoc.Api.Crm.Mock;
using CrmAiPoc.Api.Crm.Query;
using CrmAiPoc.Api.Crm.Validation;
using CrmAiPoc.Api.Security;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CrmAiPoc.Tests;

public class SalesOrderToolTests
{
    [Fact]
    public async Task GetSalesOrders_Mock_ReturnsSyntheticOrders()
    {
        var tools = CreateTools(new CrmOptions { AllowSendLiveDataToAi = false });
        var json = await tools.get_sales_orders(10);
        using var doc = JsonDocument.Parse(json);
        Assert.False(doc.RootElement.TryGetProperty("error", out _));
        Assert.True(doc.RootElement.GetProperty("totalMatched").GetInt32() >= 1);
        Assert.Equal("Mock", doc.RootElement.GetProperty("provider").GetString());
    }

    [Fact]
    public async Task GetSalesOrders_EnforcesMaxLimit()
    {
        var tools = CreateTools(new CrmOptions { MaxPageSize = 25, DefaultPageSize = 10 });
        var json = await tools.get_sales_orders(1000);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(25, doc.RootElement.GetProperty("limit").GetInt32());
    }

    [Fact]
    public async Task GetSalesOrders_LiveProvider_AllowsLocalExecution_WhenAiTransmissionDisabled()
    {
        var options = Options.Create(new CrmOptions
        {
            AllowSendLiveDataToAi = false,
            MaxPageSize = 25,
            DefaultPageSize = 10
        });

        var tools = new CrmAiTools(
            new FakeDynamicsProvider(),
            new CrmAuthorizationService(options, NullLogger<CrmAuthorizationService>.Instance),
            new CrmArgumentValidator(options),
            options,
            NullLogger<CrmAiTools>.Instance);

        var json = await tools.get_sales_orders(5);
        using var doc = JsonDocument.Parse(json);
        Assert.False(doc.RootElement.TryGetProperty("error", out _));
        Assert.Equal("Dynamics365", doc.RootElement.GetProperty("provider").GetString());
    }

    [Fact]
    public async Task GetSalesOrders_Refuses_WhenAllowSendLiveDataToAiTrue()
    {
        var options = Options.Create(new CrmOptions
        {
            AllowSendLiveDataToAi = true,
            MaxPageSize = 25,
            DefaultPageSize = 10
        });

        var tools = new CrmAiTools(
            new FakeDynamicsProvider(),
            new CrmAuthorizationService(options, NullLogger<CrmAuthorizationService>.Instance),
            new CrmArgumentValidator(options),
            options,
            NullLogger<CrmAiTools>.Instance);

        var json = await tools.get_sales_orders(5);
        using var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.GetProperty("error").GetBoolean());
    }

    [Fact]
    public void SalesOrder_IsAllowlisted()
    {
        Assert.True(CrmEntityAllowlist.IsEntityAllowed(CrmEntityAllowlist.SalesOrder));
        Assert.True(CrmEntityAllowlist.IsFieldAllowed(CrmEntityAllowlist.SalesOrder, "salesorderid"));
        Assert.False(CrmEntityAllowlist.IsFieldAllowed(CrmEntityAllowlist.SalesOrder, "not_a_real_field_xyz"));
    }

    [Fact]
    public void ReplaceSalesOrderFields_UpdatesAllowlist()
    {
        CrmEntityAllowlist.ReplaceSalesOrderFields(["salesorderid", "name", "ordernumber"]);
        Assert.True(CrmEntityAllowlist.IsFieldAllowed(CrmEntityAllowlist.SalesOrder, "name"));
        Assert.False(CrmEntityAllowlist.IsFieldAllowed(CrmEntityAllowlist.SalesOrder, "statuscode"));

        CrmEntityAllowlist.ReplaceSalesOrderFields(
        [
            "salesorderid",
            "name",
            "ordernumber",
            "createdon",
            "modifiedon",
            "totalamount",
            "statuscode",
            "statecode",
            "_customerid_value",
            "_te_customeraccountid_value"
        ]);
    }

    private static CrmAiTools CreateTools(CrmOptions crmOptions)
    {
        var options = Options.Create(crmOptions);
        return new CrmAiTools(
            new MockCrmDataProvider(NullLogger<MockCrmDataProvider>.Instance),
            new CrmAuthorizationService(options, NullLogger<CrmAuthorizationService>.Instance),
            new CrmArgumentValidator(options),
            options,
            NullLogger<CrmAiTools>.Instance);
    }

    private sealed class FakeDynamicsProvider : CrmAiPoc.Api.Crm.ICrmDataProvider
    {
        public string ProviderName => "Dynamics365";

        public Task<CrmAiPoc.Api.Crm.Models.CrmQueryResult<CrmAiPoc.Api.Crm.Models.CrmAccount>> SearchAccountsAsync(
            string name, int limit, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<CrmAiPoc.Api.Crm.Models.CrmQueryResult<CrmAiPoc.Api.Crm.Models.CrmContact>> GetContactsForAccountAsync(
            Guid accountId, int limit, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<int> CountAccountsAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<CrmAiPoc.Api.Crm.Models.CrmQueryResult<CrmAiPoc.Api.Crm.Models.CrmSalesOrder>> GetSalesOrdersAsync(
            int limit, CancellationToken cancellationToken = default) =>
            Task.FromResult(new CrmAiPoc.Api.Crm.Models.CrmQueryResult<CrmAiPoc.Api.Crm.Models.CrmSalesOrder>
            {
                Items =
                [
                    new CrmAiPoc.Api.Crm.Models.CrmSalesOrder
                    {
                        SalesOrderId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
                        Name = "Test Order",
                        OrderNumber = "ORD-TEST-1",
                        CreatedOn = DateTimeOffset.UtcNow
                    }
                ],
                TotalMatched = 1
            });

        public Task<SalesOrderQueryExecutionResult> QuerySalesOrdersAsync(
            SalesOrderQueryPlan plan,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new SalesOrderQueryExecutionResult
            {
                Operation = plan.Operation,
                RelativeRequestUri = "salesorders?$top=5",
                AppliedFilterSummary = [],
                Limit = 5,
                Items =
                [
                    new CrmAiPoc.Api.Crm.Models.CrmSalesOrder
                    {
                        SalesOrderId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
                        Name = "Test Order",
                        OrderNumber = "ORD-TEST-1",
                        CreatedOn = DateTimeOffset.UtcNow
                    }
                ],
                ProviderName = ProviderName
            });
    }
}
