using CrmAiPoc.Api.Ai;
using CrmAiPoc.Api.Configuration;
using CrmAiPoc.Api.Crm.Generic;
using CrmAiPoc.Api.Crm.Metadata;
using CrmAiPoc.Api.Crm.Query;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace CrmAiPoc.Tests;

public sealed class AggregatePartitionTests
{
    [Theory]
    [InlineData("How many sales orders do we have by status?")]
    public void Status_Aggregate_Intent_English(string message)
    {
        var options = Options.Create(new CrmOptions
        {
            ApprovedEntities = ["account", "contact", "salesorder", "lead"],
            EntityAliases = new Dictionary<string, List<string>>
            {
                ["salesorder"] = ["sales order", "sales orders", "orders"]
            }
        });
        var policy = new EntityApprovalPolicy(options);

        Assert.Equal("salesorder", CrmQueryIntentHelper.DetectEntityLogicalName(message, policy));

        // Stubbed AI aggregate plan (backend must not invent this from keywords).
        var parsed = CrmQueryPlanParser.ParseFromToolArguments(new Dictionary<string, object?>
        {
            ["entity"] = "salesorder",
            ["operation"] = "aggregate",
            ["aggregatesJson"] = """[{"function":"count","field":"salesorderid","alias":"record_count"}]""",
            ["groupByJson"] = """[{"field":"statuscode","alias":"statuscode"}]"""
        });
        Assert.True(parsed.Success, parsed.Error);
        Assert.Equal(CrmQueryOperation.Aggregate, parsed.Plan!.Operation);
        Assert.Contains(parsed.Plan.Aggregates, a =>
            a.Function == CrmAggregateFunction.Count &&
            a.Field.Equals("salesorderid", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(parsed.Plan.GroupBy, g =>
            g.Field.Equals("statuscode", StringComparison.OrdinalIgnoreCase));

        var so = StaticSeedCrmMetadata.CreateApprovedSeeds(policy)
            .First(e => e.LogicalName == "salesorder");
        var built = new FetchXmlAggregateBuilder(options).Build(parsed.Plan, so);
        Assert.True(built.Success, built.Error);
        Assert.Contains("aggregate=\"true\"", built.FetchXml);
        Assert.Contains("aggregate=\"countcolumn\"", built.FetchXml);
        Assert.Contains("name=\"statuscode\"", built.FetchXml);
        Assert.Contains("groupby=\"true\"", built.FetchXml);
    }

    [Fact]
    public void Year_Partitions_Are_NonOverlapping_HalfOpen()
    {
        var tz = BusinessTimeZone.Resolve("Africa/Cairo");
        var min = new DateTimeOffset(2024, 6, 15, 10, 0, 0, TimeSpan.Zero);
        var max = new DateTimeOffset(2026, 3, 1, 10, 0, 0, TimeSpan.Zero);
        var parts = AggregateDatePartitioner.BuildYearPartitions(min, max, tz);
        Assert.Equal(3, parts.Count);
        for (var i = 0; i < parts.Count; i++)
        {
            Assert.True(parts[i].StartInclusiveUtc < parts[i].EndExclusiveUtc);
            if (i > 0)
            {
                Assert.Equal(parts[i - 1].EndExclusiveUtc, parts[i].StartInclusiveUtc);
            }
        }
    }

    [Fact]
    public void MergePartitionRows_Sums_Counts_By_Status_Exactly()
    {
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
            GroupBy = [new CrmGroupBySpec { Field = "statuscode", Alias = "statuscode" }]
        };

        var p1 = new IReadOnlyDictionary<string, object?>[]
        {
            new Dictionary<string, object?> { ["statuscode"] = 1, ["record_count"] = 10 },
            new Dictionary<string, object?> { ["statuscode"] = 2, ["record_count"] = 3 }
        };
        var p2 = new IReadOnlyDictionary<string, object?>[]
        {
            new Dictionary<string, object?> { ["statuscode"] = 1, ["record_count"] = 5 },
            new Dictionary<string, object?> { ["statuscode"] = 3, ["record_count"] = 7 }
        };

        var merged = AggregateDatePartitioner.MergePartitionRows([p1, p2], plan);
        Assert.Equal(3, merged.Count);

        int CountFor(int status) =>
            Convert.ToInt32(
                merged.First(r => Convert.ToInt32(r["statuscode"]) == status)["record_count"]);

        Assert.Equal(15, CountFor(1));
        Assert.Equal(3, CountFor(2));
        Assert.Equal(7, CountFor(3));
    }

    [Fact]
    public void Parser_Detects_AggregateQueryRecordLimit()
    {
        var body =
            """{"error":{"code":"0x8004e023","message":"AggregateQueryRecordLimit exceeded. Cannot perform this operation."}}""";
        var parsed = DynamicsCrmErrorParser.Parse(body);
        Assert.True(parsed.IsAggregateQueryRecordLimit);
        Assert.Equal("8004E023", parsed.Code);
    }

    [Fact]
    public void Partition_FetchXml_Adds_NonOverlapping_CreatedOn_Bounds()
    {
        var options = Options.Create(new CrmOptions
        {
            ApprovedEntities = ["salesorder"],
            BusinessTimeZoneId = "Africa/Cairo"
        });
        var policy = new EntityApprovalPolicy(options);
        var so = StaticSeedCrmMetadata.CreateApprovedSeeds(policy)
            .First(e => e.LogicalName == "salesorder");
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
            GroupBy = [new CrmGroupBySpec { Field = "statuscode", Alias = "statuscode" }]
        };

        var tz = BusinessTimeZone.Resolve("Africa/Cairo");
        var year = AggregateDatePartitioner.BuildYearPartitions(
            new DateTimeOffset(2025, 2, 1, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2025, 8, 1, 0, 0, 0, TimeSpan.Zero),
            tz)[0];

        var built = new FetchXmlAggregateBuilder(options).Build(plan, so, createdOnPartition: year);
        Assert.True(built.Success, built.Error);
        Assert.Contains("attribute=\"createdon\"", built.FetchXml);
        Assert.Contains("operator=\"ge\"", built.FetchXml);
        Assert.Contains("operator=\"lt\"", built.FetchXml);
        Assert.Contains("groupby=\"true\"", built.FetchXml);
    }
}
