using CrmAiPoc.Api.Ai;
using CrmAiPoc.Api.Configuration;
using CrmAiPoc.Api.Crm.Generic;
using CrmAiPoc.Api.Crm.Metadata;
using Microsoft.Extensions.AI;
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
    public void OutboundHistory_NeverIncludesCrmRows_OnlyCatalogAndLatestUser()
    {
        var session = new ConversationSession("test");
        session.Add(new ChatMessage(ChatRole.User, "earlier accounts question"));
        session.Add(new ChatMessage(ChatRole.Assistant, "ORD-1001 should not be resent"));

        var catalog = new CrmAiMetadataCatalogBuildResult
        {
            Enabled = true,
            Success = true,
            FormattedBlock = $"{CrmAiMetadataCatalogService.BeginMarker}\n{{\"entities\":[]}}\n{CrmAiMetadataCatalogService.EndMarker}",
            EntityCount = 0,
            AttributeCount = 0,
            CharacterCount = 40
        };

        var outbound = ChatService.BuildOutboundHistory(session, "how many leads?", catalog);
        Assert.Equal(3, outbound.Count);
        Assert.All(outbound, m => Assert.DoesNotContain("ORD-1001", m.Text ?? string.Empty, StringComparison.Ordinal));
        Assert.Contains(outbound, m => m.Role == ChatRole.System && (m.Text ?? string.Empty).Contains(CrmAiMetadataCatalogService.BeginMarker, StringComparison.Ordinal));
        Assert.Equal("how many leads?", outbound[^1].Text);

        var audit = OutboundAiPayloadAuditor.Audit(outbound);
        Assert.True(audit.SafeForLiveCrm);
    }

    [Fact]
    public void GenericBuilder_RejectsArbitraryODataFragmentAsField()
    {
        var options = Options.Create(new CrmOptions());
        var policy = new EntityApprovalPolicy(options);
        var entity = StaticSeedCrmMetadata.CreateApprovedSeeds(policy).First(e => e.LogicalName == "salesorder");
        var built = new GenericCrmQueryBuilder(options).Build(
            new CrmQueryPlan
            {
                Entity = "salesorder",
                Filters =
                [
                    new CrmQueryFilter
                    {
                        Field = "name eq 'x') or (1 eq 1",
                        Operator = CrmFilterOperator.Eq,
                        Value = "y"
                    }
                ]
            },
            entity);

        Assert.False(built.Success);
    }
}
