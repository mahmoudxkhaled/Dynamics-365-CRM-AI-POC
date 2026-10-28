using CrmAiPoc.Api.Ai;
using CrmAiPoc.Api.Crm.Generic;
using Microsoft.Extensions.AI;

namespace CrmAiPoc.Tests;

public class PrivacySafeChatTests
{
    [Fact]
    public void GenericFormatter_Arabic_SalesOrders()
    {
        var text = GenericCrmReplyFormatter.Format(
            new GenericCrmQueryResult
            {
                Entity = "salesorder",
                Operation = CrmQueryOperation.List,
                RelativeRequestUri = "salesorders?$top=5",
                Limit = 5,
                ProviderName = "Dynamics365",
                Items =
                [
                    new Dictionary<string, object?>
                    {
                        ["name"] = "طلب تجريبي",
                        ["ordernumber"] = "ORD-1",
                        ["createdon"] = DateTimeOffset.Parse("2026-10-01")
                    }
                ]
            },
            "ar",
            Guid.Parse("b1335b28-556d-f111-afc3-005056b70b06"));

        Assert.Contains("salesorder", text);
        Assert.Contains("ORD-1", text);
        Assert.Contains("لم تُرسل", text);
    }

    [Fact]
    public void GenericFormatter_English_SalesOrders()
    {
        var text = GenericCrmReplyFormatter.Format(
            new GenericCrmQueryResult
            {
                Entity = "salesorder",
                Operation = CrmQueryOperation.List,
                RelativeRequestUri = "salesorders?$top=5",
                Limit = 5,
                ProviderName = "Dynamics365",
                Items =
                [
                    new Dictionary<string, object?>
                    {
                        ["name"] = "Demo",
                        ["ordernumber"] = "ORD-2",
                        ["createdon"] = DateTimeOffset.UtcNow
                    }
                ]
            },
            "en");

        Assert.Contains("salesorder", text, StringComparison.OrdinalIgnoreCase);
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
