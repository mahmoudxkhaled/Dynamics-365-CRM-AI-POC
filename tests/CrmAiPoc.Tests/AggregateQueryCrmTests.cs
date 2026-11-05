using CrmAiPoc.Api.Ai;
using CrmAiPoc.Api.Configuration;
using CrmAiPoc.Api.Crm.Generic;
using CrmAiPoc.Api.Crm.Metadata;
using CrmAiPoc.Api.Crm.Query;
using CrmAiPoc.Api.Security;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CrmAiPoc.Tests;

public sealed class AggregateQueryCrmTests
{
    private static MockGenericCrmRepository CreateRepo()
    {
        var options = Options.Create(new CrmOptions
        {
            ApprovedEntities = ["account", "contact", "salesorder"],
            BusinessTimeZoneId = "Africa/Cairo"
        });
        var policy = new EntityApprovalPolicy(options);
        var registry = new CrmMetadataRegistry();
        var metadata = new MockCrmMetadataService(registry, policy);
        var builder = new GenericCrmQueryBuilder(options);
        var auth = new CrmAuthorizationService(options, NullLogger<CrmAuthorizationService>.Instance, policy);
        return new MockGenericCrmRepository(metadata, policy, builder, new FetchXmlAggregateBuilder(options), auth);
    }

    [Fact]
    public void Parser_AcceptsAggregatePlan()
    {
        var parsed = CrmQueryPlanParser.Parse(
            entity: "salesorder",
            operation: "aggregate",
            selectJson: null,
            filtersJson: null,
            relativeDateField: null,
            relativeDateToken: null,
            sortJson: null,
            sortField: null,
            sortDirection: null,
            limit: 10,
            relatedFromEntity: null,
            relatedFromId: null,
            aggregatesJson: """[{"function":"count","field":"salesorderid","alias":"record_count"}]""",
            groupByJson: """[{"field":"statuscode","alias":"statuscode"}]""");

        Assert.True(parsed.Success, parsed.Error);
        Assert.Equal(CrmQueryOperation.Aggregate, parsed.Plan!.Operation);
        Assert.Single(parsed.Plan.Aggregates);
        Assert.Equal(CrmAggregateFunction.Count, parsed.Plan.Aggregates[0].Function);
        Assert.Single(parsed.Plan.GroupBy);
    }

    [Fact]
    public void Parser_RejectsSumOnNonNumericField()
    {
        var options = Options.Create(new CrmOptions { ApprovedEntities = ["salesorder"] });
        var policy = new EntityApprovalPolicy(options);
        var metadata = new MockCrmMetadataService(new CrmMetadataRegistry(), policy);
        var entity = metadata.GetEntityAsync("salesorder").GetAwaiter().GetResult()!;
        var plan = new CrmQueryPlan
        {
            Entity = "salesorder",
            Operation = CrmQueryOperation.Aggregate,
            Aggregates =
            [
                new CrmAggregateSpec { Function = CrmAggregateFunction.Sum, Field = "name", Alias = "bad_sum" }
            ]
        };

        var built = new FetchXmlAggregateBuilder(options).Build(plan, entity);
        Assert.False(built.Success);
        Assert.Contains("numeric", built.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task MockRepo_OrdersGroupedByStatus_Counts()
    {
        var repo = CreateRepo();
        var plan = CrmQueryPlanParser.Parse(
            "salesorder",
            "aggregate",
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            10,
            null,
            null,
            """[{"function":"count","field":"salesorderid","alias":"record_count"}]""",
            """[{"field":"statuscode","alias":"statuscode"}]""").Plan!;

        var result = await repo.QueryAsync(plan);
        Assert.Equal(CrmQueryOperation.Aggregate, result.Operation);
        Assert.True(result.Items.Count >= 2);
        Assert.Contains(result.Items, r => r.ContainsKey("record_count"));
        Assert.Contains(result.Items, r => r.ContainsKey("statuscode_label") || r.ContainsKey("statuscode"));

        var reply = GenericCrmReplyFormatter.Format(result, "en");
        Assert.Contains("Found", reply, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("invent", reply, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task MockRepo_MonthlyOrders_FillsSixMonths()
    {
        var repo = CreateRepo();
        var plan = CrmQueryPlanParser.Parse(
            "salesorder",
            "aggregate",
            null,
            null,
            "createdon",
            "last_6_calendar_months",
            null,
            null,
            null,
            10,
            null,
            null,
            """[{"function":"count","field":"salesorderid","alias":"record_count"}]""",
            """[{"field":"createdon","alias":"year","dateGrouping":"year"},{"field":"createdon","alias":"month","dateGrouping":"month"}]""").Plan!;

        var result = await repo.QueryAsync(plan);
        Assert.Equal(6, result.Items.Count);
        Assert.All(result.Items, row => Assert.True(row.ContainsKey("year") && row.ContainsKey("month")));
    }

    [Fact]
    public async Task MockRepo_SumTotalAmountByStatus()
    {
        var repo = CreateRepo();
        var plan = CrmQueryPlanParser.Parse(
            "salesorder",
            "aggregate",
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            10,
            null,
            null,
            """[{"function":"sum","field":"totalamount","alias":"total_amount"}]""",
            """[{"field":"statuscode","alias":"statuscode"}]""").Plan!;

        var result = await repo.QueryAsync(plan);
        Assert.True(result.Items.Count >= 1);
        var sum = result.Items.Sum(r => Convert.ToDecimal(r["total_amount"]));
        Assert.Equal(15000m + 8500m + 22000m + 4100m + 9900m, sum);
    }

    [Fact]
    public void Stubbed_Grouped_Status_Aggregate_Is_Preserved()
    {
        var parsed = CrmQueryPlanParser.ParseFromToolArguments(new Dictionary<string, object?>
        {
            ["entity"] = "salesorder",
            ["operation"] = "aggregate",
            ["aggregatesJson"] = """[{"function":"count","field":"salesorderid","alias":"record_count"}]""",
            ["groupByJson"] = """[{"field":"statuscode","alias":"statuscode"}]"""
        });
        Assert.True(parsed.Success, parsed.Error);
        Assert.Equal(CrmQueryOperation.Aggregate, parsed.Plan!.Operation);
        Assert.NotEmpty(parsed.Plan.Aggregates);
        Assert.NotEmpty(parsed.Plan.GroupBy);
    }

    [Fact]
    public void Stubbed_Monthly_Aggregate_Preserves_Relative_Date()
    {
        var parsed = CrmQueryPlanParser.ParseFromToolArguments(new Dictionary<string, object?>
        {
            ["entity"] = "salesorder",
            ["operation"] = "aggregate",
            ["aggregatesJson"] = """[{"function":"count","field":"salesorderid","alias":"record_count"}]""",
            ["groupByJson"] =
                """[{"field":"createdon","alias":"year","dateGrouping":"year"},{"field":"createdon","alias":"month","dateGrouping":"month"}]""",
            ["relativeDateField"] = "createdon",
            ["relativeDateToken"] = "last_6_calendar_months"
        });
        Assert.True(parsed.Success, parsed.Error);
        Assert.Equal(CrmQueryOperation.Aggregate, parsed.Plan!.Operation);
        Assert.Equal(RelativeDateToken.Last6CalendarMonths, parsed.Plan.RelativeDate!.Token);
    }

    [Fact]
    public void RelativeDate_Last6CalendarMonths_UsesBusinessTimezoneHalfOpen()
    {
        var tz = BusinessTimeZone.Resolve("Africa/Cairo");
        var utcNow = new DateTimeOffset(2026, 11, 4, 12, 0, 0, TimeSpan.Zero);
        var (start, end, _) = RelativeDateResolver.Resolve(RelativeDateToken.Last6CalendarMonths, utcNow, tz);
        var months = RelativeDateResolver.EnumerateLast6CalendarMonths(utcNow, tz);
        Assert.Equal(6, months.Count);
        Assert.Equal((2026, 6), months[0]);
        Assert.Equal((2026, 11), months[^1]);
        Assert.True(start < end);
    }

    [Fact]
    public void FetchXml_DoesNotEmbedRawUserFetch()
    {
        var options = Options.Create(new CrmOptions { ApprovedEntities = ["salesorder"] });
        var policy = new EntityApprovalPolicy(options);
        var metadata = new MockCrmMetadataService(new CrmMetadataRegistry(), policy);
        var entity = metadata.GetEntityAsync("salesorder").GetAwaiter().GetResult()!;
        var plan = new CrmQueryPlan
        {
            Entity = "salesorder",
            Operation = CrmQueryOperation.Aggregate,
            Aggregates =
            [
                new CrmAggregateSpec
                {
                    Function = CrmAggregateFunction.Count,
                    Field = "salesorderid",
                    Alias = "record_count"
                }
            ],
            GroupBy =
            [
                new CrmGroupBySpec { Field = "statuscode", Alias = "statuscode" }
            ]
        };

        var built = new FetchXmlAggregateBuilder(options).Build(plan, entity);
        Assert.True(built.Success, built.Error);
        Assert.Contains("aggregate=\"true\"", built.FetchXml);
        Assert.Contains("groupby=\"true\"", built.FetchXml);
        Assert.DoesNotContain("<script", built.FetchXml, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ListAndCount_StillWork_Regression()
    {
        var repo = CreateRepo();
        var list = await repo.QueryAsync(new CrmQueryPlan
        {
            Entity = "salesorder",
            Operation = CrmQueryOperation.List,
            Limit = 5,
            Sort = [new CrmQuerySort { Field = "createdon", Direction = CrmSortDirection.Desc }]
        });
        Assert.Equal(CrmQueryOperation.List, list.Operation);
        Assert.True(list.Items.Count >= 1);

        var count = await repo.QueryAsync(new CrmQueryPlan
        {
            Entity = "salesorder",
            Operation = CrmQueryOperation.Count
        });
        Assert.Equal(5, count.Count);
    }
}
