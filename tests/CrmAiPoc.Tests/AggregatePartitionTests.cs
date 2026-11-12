using System.Xml.Linq;
using CrmAiPoc.Api.Configuration;
using CrmAiPoc.Api.Crm.FetchXml;
using CrmAiPoc.Api.Crm.Generic;
using CrmAiPoc.Api.Crm.Metadata;
using CrmAiPoc.Api.Crm.Query;

namespace CrmAiPoc.Tests;

public sealed class AggregatePartitionTests
{
    private const string GroupedSumFetchXml = """
        <fetch aggregate="true" top="2">
          <entity name="salesorder">
            <attribute name="totalamount" alias="revenue" aggregate="sum" />
            <attribute name="salesorderid" alias="order_count" aggregate="countcolumn" />
            <link-entity name="account" from="accountid" to="customerid" link-type="inner" alias="a">
              <attribute name="accountid" alias="account_id" groupby="true" />
              <attribute name="name" alias="account_name" groupby="true" />
            </link-entity>
            <filter type="and">
              <condition attribute="statecode" operator="eq" value="0" />
            </filter>
            <order alias="revenue" descending="true" />
          </entity>
        </fetch>
        """;

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
    public void Classifier_Maps_8004E023_To_AggregateRecordLimit_Not_InvalidField()
    {
        var parsed = new DynamicsCrmErrorParser.ParsedError
        {
            Code = "8004E023",
            Message = "AggregateQueryRecordLimit exceeded. Cannot perform this operation.",
            IsAggregateQueryRecordLimit = true
        };

        var c = DynamicsFetchXmlErrorClassifier.Classify(400, parsed);

        Assert.False(c.IsRecoverable);
        Assert.Equal("AGGREGATE_RECORD_LIMIT_EXCEEDED", c.ErrorCode);
        Assert.DoesNotContain("INVALID_AGGREGATE_FIELD", c.ErrorCode, StringComparison.Ordinal);
    }

    [Fact]
    public void Classifier_Message_Only_AggregateQueryRecordLimit_Is_Not_Recoverable()
    {
        var parsed = DynamicsCrmErrorParser.Parse(
            """{"error":{"code":"0x80040203","message":"AggregateQueryRecordLimit exceeded."}}""");

        var c = DynamicsFetchXmlErrorClassifier.Classify(400, parsed);

        Assert.True(parsed.IsAggregateQueryRecordLimit);
        Assert.False(c.IsRecoverable);
        Assert.Equal("AGGREGATE_RECORD_LIMIT_EXCEEDED", c.ErrorCode);
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
    public void Month_And_Day_Partitions_Cover_Parent_Without_Overlap()
    {
        var tz = BusinessTimeZone.Resolve("Africa/Cairo");
        var year = AggregateDatePartitioner.BuildYearPartitions(
            new DateTimeOffset(2025, 1, 15, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2025, 6, 1, 0, 0, 0, TimeSpan.Zero),
            tz)[0];
        var months = AggregateDatePartitioner.BuildMonthPartitions(year, tz);
        Assert.Equal(12, months.Count);
        Assert.Equal(year.StartInclusiveUtc, months[0].StartInclusiveUtc);
        Assert.Equal(year.EndExclusiveUtc, months[^1].EndExclusiveUtc);

        var days = AggregateDatePartitioner.BuildDayPartitions(months[0], tz);
        Assert.Equal(31, days.Count);
        Assert.Equal(months[0].StartInclusiveUtc, days[0].StartInclusiveUtc);
        Assert.Equal(months[0].EndExclusiveUtc, days[^1].EndExclusiveUtc);
    }

    [Fact]
    public void MergePartitionRows_Sums_And_Counts_By_Group_Identity()
    {
        var aggregates = new[]
        {
            new CrmAggregateSpec
            {
                Function = CrmAggregateFunction.Sum,
                Field = "totalamount",
                Alias = "revenue"
            },
            new CrmAggregateSpec
            {
                Function = CrmAggregateFunction.Count,
                Field = "salesorderid",
                Alias = "order_count"
            }
        };
        var groups = new[] { "account_id", "account_name" };

        var p1 = new IReadOnlyDictionary<string, object?>[]
        {
            new Dictionary<string, object?>
            {
                ["account_id"] = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
                ["account_name"] = "Acme",
                ["revenue"] = 100.50m,
                ["order_count"] = 2
            },
            new Dictionary<string, object?>
            {
                ["account_id"] = "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb",
                ["account_name"] = "Acme",
                ["revenue"] = 40m,
                ["order_count"] = 1
            }
        };
        var p2 = new IReadOnlyDictionary<string, object?>[]
        {
            new Dictionary<string, object?>
            {
                ["account_id"] = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
                ["account_name"] = "Acme",
                ["revenue"] = 25.25m,
                ["order_count"] = 3
            }
        };

        var merged = AggregateDatePartitioner.MergePartitionRows([p1, p2], aggregates, groups);
        Assert.Equal(2, merged.Count);

        var acmeA = merged.Single(r =>
            Convert.ToString(r["account_id"]) == "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        Assert.Equal(125.75m, Convert.ToDecimal(acmeA["revenue"]));
        Assert.Equal(5L, Convert.ToInt64(acmeA["order_count"]));

        var acmeB = merged.Single(r =>
            Convert.ToString(r["account_id"]) == "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
        Assert.Equal(40m, Convert.ToDecimal(acmeB["revenue"]));
        Assert.Equal(1L, Convert.ToInt64(acmeB["order_count"]));
    }

    [Fact]
    public void Duplicate_Display_Names_Stay_Separate_When_Ids_Differ()
    {
        var keyA = AggregateDatePartitioner.BuildGroupKey(
            new Dictionary<string, object?>
            {
                ["account_id"] = "id-1",
                ["account_name"] = "Same Name"
            },
            ["account_id", "account_name"]);
        var keyB = AggregateDatePartitioner.BuildGroupKey(
            new Dictionary<string, object?>
            {
                ["account_id"] = "id-2",
                ["account_name"] = "Same Name"
            },
            ["account_id", "account_name"]);

        Assert.NotEqual(keyA, keyB);
    }

    [Fact]
    public void CanMergeExactly_Refuses_Avg_Without_Companion_Stats()
    {
        Assert.False(AggregateDatePartitioner.CanMergeExactly([
            new CrmAggregateSpec
            {
                Function = CrmAggregateFunction.Avg,
                Field = "totalamount",
                Alias = "avg_amount"
            }
        ]));
        Assert.True(AggregateDatePartitioner.CanMergeExactly([
            new CrmAggregateSpec
            {
                Function = CrmAggregateFunction.Sum,
                Field = "totalamount",
                Alias = "revenue"
            }
        ]));
    }

    [Fact]
    public void Analyze_Grouped_Linked_FetchXml_Preserves_Filters_Orders_And_Top()
    {
        Assert.True(FetchXmlAggregatePartitionRecovery.TryAnalyze(GroupedSumFetchXml, out var shape, out var error), error);
        Assert.False(shape.HasDistinct);
        Assert.Equal(2, shape.Top);
        Assert.Contains(shape.Aggregates, a => a.Function == CrmAggregateFunction.Sum && a.Alias == "revenue");
        Assert.Contains(shape.Aggregates, a => a.Function == CrmAggregateFunction.Count && a.Alias == "order_count");
        Assert.Contains(shape.GroupAliases, a => a == "account_id");
        Assert.Contains(shape.GroupAliases, a => a == "account_name");
        Assert.Contains(shape.Orders, o => o.Alias == "revenue" && o.Descending);
    }

    [Fact]
    public void Partition_Filter_Preserves_Business_Filter_And_Removes_Order_Top()
    {
        var tz = BusinessTimeZone.Resolve("Africa/Cairo");
        var year = AggregateDatePartitioner.BuildYearPartitions(
            new DateTimeOffset(2025, 2, 1, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2025, 8, 1, 0, 0, 0, TimeSpan.Zero),
            tz)[0];

        Assert.True(FetchXmlAggregatePartitionRecovery.TryBuildPartitionedFetchXml(
            GroupedSumFetchXml,
            "createdon",
            year,
            out var fetchXml,
            out var error), error);

        Assert.Contains("attribute=\"statecode\"", fetchXml, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("operator=\"eq\"", fetchXml, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("attribute=\"createdon\"", fetchXml, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("operator=\"ge\"", fetchXml, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("operator=\"lt\"", fetchXml, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("link-entity", fetchXml, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("account_id", fetchXml, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<order", fetchXml, StringComparison.OrdinalIgnoreCase);

        var doc = XDocument.Parse(fetchXml);
        Assert.Null(doc.Root!.Attribute("top"));
    }

    [Fact]
    public void ApplyOrderAndTop_Runs_Only_After_Merge()
    {
        Assert.True(FetchXmlAggregatePartitionRecovery.TryAnalyze(GroupedSumFetchXml, out var shape, out _));

        var rows = new IReadOnlyDictionary<string, object?>[]
        {
            new Dictionary<string, object?> { ["account_id"] = "a", ["revenue"] = 10m },
            new Dictionary<string, object?> { ["account_id"] = "b", ["revenue"] = 50m },
            new Dictionary<string, object?> { ["account_id"] = "c", ["revenue"] = 30m }
        };

        // Mirror private ApplyOrderAndTop via merge then public partitioner + shape.Top/Orders.
        var merged = AggregateDatePartitioner.MergePartitionRows(
            [rows],
            shape.Aggregates.Where(a => a.Alias == "revenue").ToArray(),
            ["account_id"]);

        var ordered = shape.Orders.Aggregate(
            (IEnumerable<IReadOnlyDictionary<string, object?>>)merged,
            (current, order) =>
            {
                var key = order.Alias!;
                return order.Descending
                    ? current.OrderByDescending(r => Convert.ToDecimal(r[key]))
                    : current.OrderBy(r => Convert.ToDecimal(r[key]));
            }).ToList();

        if (shape.Top is > 0)
        {
            ordered = ordered.Take(shape.Top.Value).ToList();
        }

        Assert.Equal(2, ordered.Count);
        Assert.Equal(50m, Convert.ToDecimal(ordered[0]["revenue"]));
        Assert.Equal(30m, Convert.ToDecimal(ordered[1]["revenue"]));
    }

    [Fact]
    public void Distinct_Aggregate_Is_Not_Recoverable_Via_Partition()
    {
        var fetch = """
            <fetch aggregate="true" distinct="true">
              <entity name="salesorder">
                <attribute name="totalamount" alias="revenue" aggregate="sum" />
                <attribute name="customerid" alias="cid" groupby="true" />
              </entity>
            </fetch>
            """;
        Assert.True(FetchXmlAggregatePartitionRecovery.TryAnalyze(fetch, out var shape, out _));
        Assert.True(shape.HasDistinct);
    }

    [Fact]
    public void Nested_Link_Aggregates_Are_Rejected()
    {
        var fetch = """
            <fetch aggregate="true">
              <entity name="salesorder">
                <link-entity name="account" from="accountid" to="customerid" alias="a">
                  <link-entity name="contact" from="parentcustomerid" to="accountid" alias="c">
                    <attribute name="contactid" alias="contact_count" aggregate="countcolumn" />
                  </link-entity>
                </link-entity>
              </entity>
            </fetch>
            """;
        Assert.False(FetchXmlAggregatePartitionRecovery.TryAnalyze(fetch, out _, out var error));
        Assert.Contains("Nested linked-entity", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SelectPartitionAttribute_Prefers_CreatedOn_Then_ModifiedOn_Then_Any_DateTime()
    {
        var withCreated = Entity(
            Attr("name", CrmAttributeKind.String),
            Attr("createdon", CrmAttributeKind.DateTime),
            Attr("modifiedon", CrmAttributeKind.DateTime),
            Attr("customdate", CrmAttributeKind.DateTime));
        Assert.Equal("createdon", FetchXmlAggregatePartitionRecovery.SelectPartitionAttribute(withCreated));

        var withModified = Entity(
            Attr("name", CrmAttributeKind.String),
            Attr("modifiedon", CrmAttributeKind.DateTime),
            Attr("customdate", CrmAttributeKind.DateTime));
        Assert.Equal("modifiedon", FetchXmlAggregatePartitionRecovery.SelectPartitionAttribute(withModified));

        var customOnly = Entity(
            Attr("name", CrmAttributeKind.String),
            Attr("customdate", CrmAttributeKind.DateTime),
            Attr("anotherdate", CrmAttributeKind.DateTime));
        Assert.Equal("anotherdate", FetchXmlAggregatePartitionRecovery.SelectPartitionAttribute(customOnly));

        var none = Entity(Attr("name", CrmAttributeKind.String));
        Assert.Null(FetchXmlAggregatePartitionRecovery.SelectPartitionAttribute(none));
    }

    [Fact]
    public void CanMergeExactly_Supports_Min_Max()
    {
        Assert.True(AggregateDatePartitioner.CanMergeExactly([
            new CrmAggregateSpec { Function = CrmAggregateFunction.Min, Field = "totalamount", Alias = "min_amt" },
            new CrmAggregateSpec { Function = CrmAggregateFunction.Max, Field = "totalamount", Alias = "max_amt" }
        ]));

        var merged = AggregateDatePartitioner.MergePartitionRows(
            [
                [new Dictionary<string, object?> { ["min_amt"] = 5m, ["max_amt"] = 20m }],
                [new Dictionary<string, object?> { ["min_amt"] = 2m, ["max_amt"] = 15m }]
            ],
            [
                new CrmAggregateSpec { Function = CrmAggregateFunction.Min, Field = "totalamount", Alias = "min_amt" },
                new CrmAggregateSpec { Function = CrmAggregateFunction.Max, Field = "totalamount", Alias = "max_amt" }
            ],
            Array.Empty<string>());

        Assert.Single(merged);
        Assert.Equal(2m, Convert.ToDecimal(merged[0]["min_amt"]));
        Assert.Equal(20m, Convert.ToDecimal(merged[0]["max_amt"]));
    }

    private static CrmEntityMetadata Entity(params CrmAttributeMetadata[] attrs) =>
        new()
        {
            LogicalName = "salesorder",
            EntitySetName = "salesorders",
            PrimaryIdAttribute = "salesorderid",
            PrimaryNameAttribute = "name",
            IsApprovedForAi = true,
            Attributes = attrs
        };

    private static CrmAttributeMetadata Attr(string name, CrmAttributeKind kind) =>
        new()
        {
            LogicalName = name,
            ODataPropertyName = name,
            Kind = kind,
            IsValidForRead = true
        };
}
