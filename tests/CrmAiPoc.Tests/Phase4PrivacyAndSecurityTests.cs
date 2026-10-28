using CrmAiPoc.Api.Ai;
using CrmAiPoc.Api.Configuration;
using CrmAiPoc.Api.Crm.Generic;
using CrmAiPoc.Api.Crm.Metadata;
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
    public async Task QueryCrm_RefusesWhenAllowSendLiveDataToAiTrue_OnDynamics()
    {
        var options = Options.Create(new CrmOptions
        {
            Provider = "Dynamics365",
            EnableLiveIntegration = true,
            BaseUrl = "https://example.invalid/api/data/v9.1/",
            AllowSendLiveDataToAi = true,
            MaxPageSize = 25,
            DefaultPageSize = 10,
            ApprovedEntities = ["account", "contact", "salesorder"]
        });

        var policy = new EntityApprovalPolicy(options);
        var registry = new CrmMetadataRegistry();
        var metadata = new MockCrmMetadataService(registry, policy);
        var builder = new GenericCrmQueryBuilder(options);
        var auth = new CrmAuthorizationService(options, NullLogger<CrmAuthorizationService>.Instance, policy);
        var repo = new MockGenericCrmRepository(metadata, policy, builder, auth);

        var tools = new CrmAiTools(
            repo,
            options,
            NullLogger<CrmAiTools>.Instance,
            metadata);

        var json = await tools.query_crm(entity: "salesorder", operation: "list", limit: 5);
        Assert.Contains("\"error\":true", json.Replace(" ", string.Empty), StringComparison.OrdinalIgnoreCase);
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
