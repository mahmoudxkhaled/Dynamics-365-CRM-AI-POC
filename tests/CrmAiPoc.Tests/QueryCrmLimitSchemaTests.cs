using System.Text.Json;
using CrmAiPoc.Api.Ai;
using CrmAiPoc.Api.Configuration;
using CrmAiPoc.Api.Crm.Generic;
using CrmAiPoc.Api.Crm.Metadata;
using CrmAiPoc.Api.Security;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CrmAiPoc.Tests;

public sealed class QueryCrmLimitSchemaTests
{
    [Fact]
    public void QueryCrm_Schema_Allows_Null_Or_Omitted_Limit()
    {
        var fn = AIFunctionFactory.Create(CreateTools().query_crm);
        var schema = fn.JsonSchema;

        Assert.True(schema.TryGetProperty("properties", out var props));
        Assert.True(props.TryGetProperty("limit", out var limit));

        // MEAI emits ["integer","null"] for int? parameters.
        Assert.Equal(JsonValueKind.Array, limit.GetProperty("type").ValueKind);
        var types = limit.GetProperty("type").EnumerateArray().Select(e => e.GetString()).ToHashSet();
        Assert.Contains("integer", types);
        Assert.Contains("null", types);

        // Still optional (not required).
        if (schema.TryGetProperty("required", out var required))
        {
            Assert.DoesNotContain(
                required.EnumerateArray().Select(e => e.GetString()),
                r => string.Equals(r, "limit", StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public void ParseFromToolArguments_Accepts_Null_Limit_And_Uses_Default()
    {
        var parsed = CrmQueryPlanParser.ParseFromToolArguments(new Dictionary<string, object?>
        {
            ["entity"] = "salesorder",
            ["operation"] = "aggregate",
            ["aggregatesJson"] = """[{"function":"count","field":"salesorderid","alias":"cnt"}]""",
            ["groupByJson"] = """[{"field":"statuscode","alias":"status"}]""",
            ["limit"] = null
        });

        Assert.True(parsed.Success, parsed.Error);
        Assert.NotNull(parsed.Plan);
        Assert.Equal(CrmQueryLimits.DefaultLimit, parsed.Plan!.Limit);
        Assert.Equal(CrmQueryOperation.Aggregate, parsed.Plan.Operation);
    }

    [Fact]
    public void ParseFromToolArguments_Accepts_JsonNull_Limit_Element()
    {
        using var doc = JsonDocument.Parse("""{"limit":null}""");
        var limitEl = doc.RootElement.GetProperty("limit").Clone();

        var parsed = CrmQueryPlanParser.ParseFromToolArguments(new Dictionary<string, object?>
        {
            ["entity"] = "salesorder",
            ["operation"] = "count",
            ["limit"] = limitEl
        });

        Assert.True(parsed.Success, parsed.Error);
        Assert.Equal(CrmQueryLimits.DefaultLimit, parsed.Plan!.Limit);
        Assert.Equal(CrmQueryOperation.Count, parsed.Plan.Operation);
    }

    [Fact]
    public void ParseFromToolArguments_Accepts_Omitted_Limit()
    {
        var parsed = CrmQueryPlanParser.ParseFromToolArguments(new Dictionary<string, object?>
        {
            ["entity"] = "salesorder",
            ["operation"] = "list"
        });

        Assert.True(parsed.Success, parsed.Error);
        Assert.Equal(CrmQueryLimits.DefaultLimit, parsed.Plan!.Limit);
    }

    [Fact]
    public void ParseFromToolArguments_Preserves_Explicit_Integer_Limit()
    {
        var parsed = CrmQueryPlanParser.ParseFromToolArguments(new Dictionary<string, object?>
        {
            ["entity"] = "salesorder",
            ["operation"] = "list",
            ["limit"] = 7
        });

        Assert.True(parsed.Success, parsed.Error);
        Assert.Equal(7, parsed.Plan!.Limit);
    }

    [Fact]
    public async Task QueryCrm_Tool_Accepts_Null_Limit_Argument()
    {
        var json = await CreateTools().query_crm(entity: "account", operation: "count", limit: null);
        using var doc = JsonDocument.Parse(json);
        Assert.False(doc.RootElement.TryGetProperty("error", out _));
        Assert.True(doc.RootElement.GetProperty("count").GetInt32() >= 1);
    }

    private static CrmAiTools CreateTools()
    {
        var options = Options.Create(new CrmOptions
        {
            ApprovedEntities = ["account", "contact", "salesorder", "lead"],
            BusinessTimeZoneId = "Africa/Cairo"
        });
        var policy = new EntityApprovalPolicy(options);
        var registry = new CrmMetadataRegistry();
        var metadata = new MockCrmMetadataService(registry, policy);
        var builder = new GenericCrmQueryBuilder(options);
        var auth = new CrmAuthorizationService(options, NullLogger<CrmAuthorizationService>.Instance, policy);
        var repo = new MockGenericCrmRepository(metadata, policy, builder, new FetchXmlAggregateBuilder(), auth);
        return new CrmAiTools(repo, NullLogger<CrmAiTools>.Instance);
    }
}
