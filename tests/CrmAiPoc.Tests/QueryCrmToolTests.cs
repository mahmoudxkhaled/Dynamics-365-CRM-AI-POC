using System.Text.Json;
using CrmAiPoc.Api.Ai;
using CrmAiPoc.Api.Configuration;
using CrmAiPoc.Api.Crm.Generic;
using CrmAiPoc.Api.Crm.Metadata;
using CrmAiPoc.Api.Security;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CrmAiPoc.Tests;

public class QueryCrmToolTests
{
    private static CrmAiTools CreateTools()
    {
        var options = Options.Create(new CrmOptions
        {
            AllowSendLiveDataToAi = false,
            ApprovedEntities = ["account", "contact", "salesorder", "lead"],
            BusinessTimeZoneId = "Africa/Cairo"
        });
        var policy = new EntityApprovalPolicy(options);
        var registry = new CrmMetadataRegistry();
        var metadata = new MockCrmMetadataService(registry, policy);
        var builder = new GenericCrmQueryBuilder(options);
        var auth = new CrmAuthorizationService(options, NullLogger<CrmAuthorizationService>.Instance, policy);
        var repo = new MockGenericCrmRepository(metadata, policy, builder, auth);
        return new CrmAiTools(repo, options, NullLogger<CrmAiTools>.Instance, metadata);
    }

    [Fact]
    public async Task QueryCrm_ListsSalesOrders()
    {
        var json = await CreateTools().query_crm(entity: "salesorder", operation: "list", limit: 5);
        using var doc = JsonDocument.Parse(json);
        Assert.False(doc.RootElement.TryGetProperty("error", out _));
        Assert.Equal("query_crm", doc.RootElement.GetProperty("function").GetString());
        Assert.Equal("salesorder", doc.RootElement.GetProperty("entity").GetString());
        Assert.True(doc.RootElement.GetProperty("totalReturned").GetInt32() <= 5);
    }

    [Fact]
    public async Task QueryCrm_CountsAccounts()
    {
        var json = await CreateTools().query_crm(entity: "account", operation: "count");
        using var doc = JsonDocument.Parse(json);
        Assert.False(doc.RootElement.TryGetProperty("error", out _));
        Assert.True(doc.RootElement.GetProperty("count").GetInt32() >= 1);
    }

    [Fact]
    public async Task QueryCrm_RejectsUnapprovedEntity()
    {
        var json = await CreateTools().query_crm(entity: "opportunity", operation: "list");
        using var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.GetProperty("error").GetBoolean());
    }

    [Fact]
    public async Task QueryCrm_EnforcesLimit()
    {
        var json = await CreateTools().query_crm(entity: "contact", operation: "list", limit: 1000);
        using var doc = JsonDocument.Parse(json);
        Assert.False(doc.RootElement.TryGetProperty("error", out _));
        Assert.True(doc.RootElement.GetProperty("limit").GetInt32() <= 50);
    }
}
