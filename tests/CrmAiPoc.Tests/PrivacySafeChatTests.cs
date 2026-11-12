using CrmAiPoc.Api.Ai;
using Microsoft.Extensions.AI;

namespace CrmAiPoc.Tests;

public class PrivacySafeChatTests
{
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
