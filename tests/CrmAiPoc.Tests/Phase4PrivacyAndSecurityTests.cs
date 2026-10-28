using CrmAiPoc.Api.Ai;
using CrmAiPoc.Api.Configuration;
using CrmAiPoc.Api.Crm.Mock;
using CrmAiPoc.Api.Crm.Query;
using CrmAiPoc.Api.Crm.Validation;
using CrmAiPoc.Api.Security;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CrmAiPoc.Tests;

public class Phase4PrivacyAndSecurityTests
{
    [Fact]
    public void OutboundAudit_UserQuestionOnly_NoCrmRows()
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, "routing assistant"),
            new(ChatRole.User, "هات الـ Sales Orders اللي اتعملت الشهر اللي فات")
        };

        var audit = OutboundAiPayloadAuditor.Audit(messages);
        Assert.True(audit.SafeForLiveCrm);
        Assert.False(audit.ContainsFunctionResults);
    }

    [Fact]
    public void OutboundAudit_RejectsSerializedSalesOrderPayload()
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, "show orders"),
            new(ChatRole.Assistant, "ORD-1001 totalAmount 15000 customerAccountId")
        };

        var audit = OutboundAiPayloadAuditor.Audit(messages);
        Assert.True(audit.SuspiciousCrmRecordText);
        Assert.False(audit.SafeForLiveCrm);
    }

    [Fact]
    public async Task QuerySalesOrders_RefusesWhenAllowSendLiveDataToAiTrue()
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

        var json = await tools.query_sales_orders(limit: 5);
        Assert.Contains("\"error\":true", json.Replace(" ", string.Empty), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Builder_RejectsArbitraryODataFragmentAsField()
    {
        var builder = new SalesOrderODataQueryBuilder(Options.Create(new CrmOptions()));
        var result = builder.Build(new SalesOrderQueryPlan
        {
            Filters =
            [
                new SalesOrderQueryFilter
                {
                    Field = "name eq 'x') or (1 eq 1",
                    Operator = SalesOrderFilterOperator.Eq,
                    Value = "y"
                }
            ]
        });

        Assert.False(result.Success);
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
            throw new NotSupportedException();

        public Task<SalesOrderQueryExecutionResult> QuerySalesOrdersAsync(
            SalesOrderQueryPlan plan,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Should not execute when AllowSendLiveDataToAi=true.");
    }
}
