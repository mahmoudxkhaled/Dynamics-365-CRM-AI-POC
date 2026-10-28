using CrmAiPoc.Api.Ai;
using CrmAiPoc.Api.Crm.Models;
using Microsoft.Extensions.AI;

namespace CrmAiPoc.Tests;

public class PrivacySafeChatTests
{
    [Fact]
    public void LocalFormatter_Arabic_SalesOrders()
    {
        var text = LocalCrmReplyFormatter.FormatSalesOrders(
            [
                new CrmSalesOrder
                {
                    SalesOrderId = Guid.NewGuid(),
                    Name = "طلب تجريبي",
                    OrderNumber = "ORD-1",
                    CreatedOn = DateTimeOffset.Parse("2026-10-01")
                }
            ],
            5,
            "ar",
            "Dynamics365",
            Guid.Parse("b1335b28-556d-f111-afc3-005056b70b06"));

        Assert.Contains("أوامر بيع", text);
        Assert.Contains("ORD-1", text);
        Assert.Contains("لم تُرسل", text);
    }

    [Fact]
    public void LocalFormatter_English_SalesOrders()
    {
        var text = LocalCrmReplyFormatter.FormatSalesOrders(
            [
                new CrmSalesOrder
                {
                    SalesOrderId = Guid.NewGuid(),
                    Name = "Demo",
                    OrderNumber = "ORD-2",
                    CreatedOn = DateTimeOffset.UtcNow
                }
            ],
            5,
            "en",
            "Dynamics365",
            null);

        Assert.Contains("sales order", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("were not sent to the AI model", text);
    }

    [Fact]
    public void OutboundAuditor_FlagsFunctionResults()
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, "hi"),
            new(ChatRole.Tool, [new FunctionResultContent("1", "{\"salesOrders\":[]}")])
        };

        var audit = OutboundAiPayloadAuditor.Audit(messages);
        Assert.True(audit.ContainsFunctionResults);
        Assert.False(audit.SafeForLiveCrm);
    }

    [Fact]
    public void OutboundAuditor_AllowsCleanToolSelectionPayload()
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, "system"),
            new(ChatRole.User, "هاتلي آخر 5 Sales Orders")
        };

        var audit = OutboundAiPayloadAuditor.Audit(messages);
        Assert.True(audit.SafeForLiveCrm);
        Assert.False(audit.ContainsFunctionResults);
    }
}
