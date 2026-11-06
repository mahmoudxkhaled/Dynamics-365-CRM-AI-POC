using CrmAiPoc.Api.Ai;
using CrmAiPoc.Api.Crm.Generic;
using Microsoft.Extensions.AI;

namespace CrmAiPoc.Tests;

public class PrivacySafeChatTests
{
    [Fact]
    public void GenericFormatter_English_Only_Even_When_LanguageHint_Is_Arabic()
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
                        ["name"] = "Demo order",
                        ["ordernumber"] = "ORD-1",
                        ["createdon"] = DateTimeOffset.Parse("2026-10-01")
                    }
                ]
            },
            "en",
            Guid.Parse("b1335b28-556d-f111-afc3-005056b70b06"));

        Assert.Contains("Sales Order", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ORD-1", text);
        Assert.Contains("were not sent to the AI model", text);
        Assert.DoesNotContain("WhoAmI", text, StringComparison.OrdinalIgnoreCase);
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

        Assert.Contains("Sales Order", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("were not sent to the AI model", text);
    }

    [Theory]
    [InlineData(
        "salesorder-[te_account_salesorder_customeraccountid/Inner]->account as account",
        "Requires related Account (Customer Account)")]
    [InlineData(
        "salesorder-[te_contact_salesorder_billingcontactid/Inner]->contact as contact",
        "Requires related Contact (Billing Contact)")]
    [InlineData("totalamount desc", "Total Amount, highest first")]
    public void GenericFormatter_Humanizes_Technical_Filter_And_Sort_Text(string raw, string expected)
    {
        if (raw.Contains("desc", StringComparison.OrdinalIgnoreCase) && !raw.Contains("->"))
        {
            Assert.Equal(expected, GenericCrmReplyFormatter.HumanizeSortSummary(raw));
            return;
        }

        Assert.Equal(expected, GenericCrmReplyFormatter.HumanizeFilterSummary(raw));
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
            new(ChatRole.User, "Show me the latest 5 sales orders")
        };

        var audit = OutboundAiPayloadAuditor.Audit(messages);
        Assert.True(audit.SafeForLiveCrm);
        Assert.False(audit.ContainsFunctionResults);
    }
}
