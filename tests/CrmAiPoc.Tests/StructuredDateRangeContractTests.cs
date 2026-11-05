using System.Reflection;
using System.Text.Json;
using System.Xml.Linq;
using CrmAiPoc.Api.Ai;
using CrmAiPoc.Api.Configuration;
using CrmAiPoc.Api.Crm.Generic;
using CrmAiPoc.Api.Crm.Metadata;
using CrmAiPoc.Api.Crm.Query;
using Microsoft.Extensions.Options;

namespace CrmAiPoc.Tests;

/// <summary>
/// Regression for structured dateRange / dateRangeJson contract (canonical + Groq shorthand + failures).
/// </summary>
public sealed class StructuredDateRangeContractTests
{
    private static readonly TimeZoneInfo Cairo = BusinessTimeZone.Resolve("Africa/Cairo");

    private static (
        GenericCrmQueryBuilder ListBuilder,
        FetchXmlAggregateBuilder AggBuilder,
        CrmEntityMetadata SalesOrder,
        Func<string, CrmEntityMetadata?> GetEntity)
        CreateBuilders()
    {
        var options = Options.Create(new CrmOptions
        {
            ApprovedEntities = ["account", "contact", "salesorder", "lead"],
            ApprovedRelationships = [],
            BusinessTimeZoneId = "Africa/Cairo"
        });
        var policy = new EntityApprovalPolicy(options);
        var seeds = StaticSeedCrmMetadata.CreateApprovedSeeds(policy);
        var entity = seeds.First(e => e.LogicalName == "salesorder");
        Func<string, CrmEntityMetadata?> getEntity = n =>
            seeds.FirstOrDefault(e => e.LogicalName.Equals(n, StringComparison.OrdinalIgnoreCase));
        return (
            new GenericCrmQueryBuilder(options),
            new FetchXmlAggregateBuilder(options),
            entity,
            getEntity);
    }

    private static string DecodeFetch(string? uri)
    {
        Assert.False(string.IsNullOrWhiteSpace(uri));
        return Uri.UnescapeDataString(uri!.Split("fetchXml=", 2)[1]);
    }

    private static (DateTimeOffset Ge, DateTimeOffset Lt) ExtractCreatedOnBounds(string fetchXml)
    {
        var doc = XDocument.Parse(fetchXml);
        var conditions = doc.Descendants("condition")
            .Where(c => string.Equals((string?)c.Attribute("attribute"), "createdon", StringComparison.OrdinalIgnoreCase))
            .ToList();
        var ge = conditions.Single(c => (string?)c.Attribute("operator") == "ge");
        var lt = conditions.Single(c => (string?)c.Attribute("operator") == "lt");
        return (
            DateTimeOffset.Parse((string)ge.Attribute("value")!),
            DateTimeOffset.Parse((string)lt.Attribute("value")!));
    }

    private static CrmQueryPlanParseResult ParseDate(string dateRangeJson) =>
        CrmQueryPlanParser.ParseFromToolArguments(new Dictionary<string, object?>
        {
            ["entity"] = "salesorder",
            ["operation"] = "list",
            ["relativeDateField"] = "createdon",
            ["dateRangeJson"] = dateRangeJson
        });

    [Fact]
    public void T01_CalendarYear_2025_Canonical_And_GroqShorthand()
    {
        foreach (var json in new[]
                 {
                     """{"kind":"calendar_year","year":2025}""",
                     """{"calendar_year":2025}""",
                     """{"year":2025}"""
                 })
        {
            var parsed = ParseDate(json);
            Assert.True(parsed.Success, $"{json}: {parsed.Error}");
            var expr = parsed.Plan!.RelativeDate!.GetExpression();
            Assert.Equal(CrmDateRangeKind.CalendarYear, expr.Kind);
            Assert.Equal(2025, expr.Year);

            var resolved = DateRangeResolver.Resolve(expr, DateTimeOffset.UtcNow, Cairo);
            Assert.Equal(new DateTime(2025, 1, 1), TimeZoneInfo.ConvertTime(resolved.StartUtc, Cairo).DateTime);
            Assert.Equal(new DateTime(2026, 1, 1), TimeZoneInfo.ConvertTime(resolved.EndUtcExclusive, Cairo).DateTime);
            Assert.Equal(12, resolved.CalendarMonthBuckets!.Count);
        }
    }

    [Fact]
    public void T02_CalendarYear_2024()
    {
        var parsed = ParseDate("""{"kind":"calendar_year","year":2024}""");
        Assert.True(parsed.Success, parsed.Error);
        var resolved = DateRangeResolver.Resolve(
            parsed.Plan!.RelativeDate!.GetExpression(), DateTimeOffset.UtcNow, Cairo);
        Assert.Equal(new DateTime(2024, 1, 1), TimeZoneInfo.ConvertTime(resolved.StartUtc, Cairo).DateTime);
        Assert.Equal(new DateTime(2025, 1, 1), TimeZoneInfo.ConvertTime(resolved.EndUtcExclusive, Cairo).DateTime);
    }

    [Fact]
    public void T03_Last_8_CalendarMonths()
    {
        var parsed = ParseDate("""{"kind":"last_n_calendar_months","n":8}""");
        Assert.True(parsed.Success, parsed.Error);
        Assert.Equal(CrmDateRangeKind.LastNCalendarMonths, parsed.Plan!.RelativeDate!.GetExpression().Kind);
        Assert.Equal(8, parsed.Plan.RelativeDate.GetExpression().N);

        var shorthand = ParseDate("""{"last_n_calendar_months":8}""");
        Assert.True(shorthand.Success, shorthand.Error);
        Assert.Equal(8, shorthand.Plan!.RelativeDate!.GetExpression().N);
    }

    [Fact]
    public void T04_January_Through_June_2025()
    {
        var parsed = ParseDate("""{"kind":"calendar_month_range","year":2025,"monthStart":1,"monthEnd":6}""");
        Assert.True(parsed.Success, parsed.Error);
        var resolved = DateRangeResolver.Resolve(
            parsed.Plan!.RelativeDate!.GetExpression(), DateTimeOffset.UtcNow, Cairo);
        Assert.Equal(new DateTime(2025, 1, 1), TimeZoneInfo.ConvertTime(resolved.StartUtc, Cairo).DateTime);
        Assert.Equal(new DateTime(2025, 7, 1), TimeZoneInfo.ConvertTime(resolved.EndUtcExclusive, Cairo).DateTime);
        Assert.Equal(6, resolved.CalendarMonthBuckets!.Count);

        var shorthand = ParseDate("""{"year":2025,"monthStart":1,"monthEnd":6}""");
        Assert.True(shorthand.Success, shorthand.Error);
        Assert.Equal(CrmDateRangeKind.CalendarMonthRange, shorthand.Plan!.RelativeDate!.GetExpression().Kind);
    }

    [Fact]
    public void T05_Current_Calendar_Year_Ytd()
    {
        var parsed = ParseDate("""{"kind":"this_calendar_year"}""");
        Assert.True(parsed.Success, parsed.Error);
        Assert.Equal(CrmDateRangeKind.ThisCalendarYear, parsed.Plan!.RelativeDate!.GetExpression().Kind);
    }

    [Fact]
    public void T06_Q2_2025()
    {
        var parsed = ParseDate("""{"kind":"calendar_quarter","year":2025,"quarter":2}""");
        Assert.True(parsed.Success, parsed.Error);
        var resolved = DateRangeResolver.Resolve(
            parsed.Plan!.RelativeDate!.GetExpression(), DateTimeOffset.UtcNow, Cairo);
        Assert.Equal(new DateTime(2025, 4, 1), TimeZoneInfo.ConvertTime(resolved.StartUtc, Cairo).DateTime);
        Assert.Equal(new DateTime(2025, 7, 1), TimeZoneInfo.ConvertTime(resolved.EndUtcExclusive, Cairo).DateTime);

        var shorthand = ParseDate("""{"year":2025,"quarter":2}""");
        Assert.True(shorthand.Success, shorthand.Error);
        Assert.Equal(CrmDateRangeKind.CalendarQuarter, shorthand.Plan!.RelativeDate!.GetExpression().Kind);
    }

    [Fact]
    public void T07_Absolute_Start_End()
    {
        var parsed = ParseDate(
            """{"kind":"absolute","start":"2024-03-15T00:00:00Z","endExclusive":"2024-09-21T00:00:00Z"}""");
        Assert.True(parsed.Success, parsed.Error);
        var expr = parsed.Plan!.RelativeDate!.GetExpression();
        Assert.Equal(CrmDateRangeKind.Absolute, expr.Kind);
        Assert.Equal(
            DateTimeOffset.Parse("2024-03-15T00:00:00Z"),
            expr.AbsoluteStartUtc);
        Assert.Equal(
            DateTimeOffset.Parse("2024-09-21T00:00:00Z"),
            expr.AbsoluteEndExclusiveUtc);
    }

    [Fact]
    public void T08_Missing_Date_Kind()
    {
        var parsed = ParseDate("""{"n":8}""");
        Assert.False(parsed.Success);
        Assert.Contains("kind", parsed.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void T09_Invalid_Date_Kind()
    {
        var parsed = ParseDate("""{"kind":"next_tuesday"}""");
        Assert.False(parsed.Success);
        Assert.Contains("Unsupported dateRangeJson kind", parsed.Error);
    }

    [Fact]
    public void T10_Malformed_Json()
    {
        var parsed = ParseDate("""{"kind":"calendar_year",""");
        Assert.False(parsed.Success);
        Assert.Contains("not valid JSON", parsed.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void T11_Null_Date_Expression_Requires_Token_Or_Range()
    {
        var parsed = CrmQueryPlanParser.ParseFromToolArguments(new Dictionary<string, object?>
        {
            ["entity"] = "salesorder",
            ["operation"] = "list",
            ["relativeDateField"] = "createdon"
        });
        Assert.False(parsed.Success);
        Assert.Contains("dateRangeJson", parsed.Error);
    }

    [Fact]
    public void T12_Legacy_Relative_Tokens()
    {
        foreach (var token in new[]
                 {
                     "last_7_days", "last_30_days", "this_calendar_month", "last_calendar_month",
                     "this_week", "last_6_calendar_months"
                 })
        {
            var parsed = CrmQueryPlanParser.ParseFromToolArguments(new Dictionary<string, object?>
            {
                ["entity"] = "salesorder",
                ["operation"] = "list",
                ["relativeDateField"] = "createdon",
                ["relativeDateToken"] = token
            });
            Assert.True(parsed.Success, $"{token}: {parsed.Error}");
            Assert.NotNull(parsed.Plan!.RelativeDate!.Token);
        }
    }

    [Fact]
    public void Monthly_Aggregate_2025_Adds_Year_Context_And_Correct_Bounds()
    {
        // Exact Groq failure payload shape (shorthand calendar_year without kind).
        var parsed = CrmQueryPlanParser.ParseFromToolArguments(new Dictionary<string, object?>
        {
            ["entity"] = "salesorder",
            ["operation"] = "aggregate",
            ["relativeDateField"] = "createdon",
            ["dateRangeJson"] = """{"calendar_year":2025}""",
            ["aggregatesJson"] = """[{"function":"count","field":"salesorderid","alias":"OrderCount"}]""",
            ["groupByJson"] = """[{"field":"createdon","alias":"Month","dateGrouping":"month"}]"""
        });
        Assert.True(parsed.Success, parsed.Error);
        var plan = parsed.Plan!;
        Assert.Equal("salesorder", plan.Entity);
        Assert.Equal(CrmQueryOperation.Aggregate, plan.Operation);
        Assert.Equal(CrmDateRangeKind.CalendarYear, plan.RelativeDate!.GetExpression().Kind);
        Assert.Equal(2025, plan.RelativeDate.GetExpression().Year);
        Assert.Contains(plan.GroupBy, g => g.DateGrouping == CrmDateGrouping.Month);
        Assert.Contains(plan.GroupBy, g => g.DateGrouping == CrmDateGrouping.Year);
        Assert.Single(plan.Aggregates);
        Assert.Equal(CrmAggregateFunction.Count, plan.Aggregates[0].Function);

        var (_, agg, entity, _) = CreateBuilders();
        var built = agg.Build(plan, entity);
        Assert.True(built.Success, built.Error);
        var (ge, lt) = ExtractCreatedOnBounds(DecodeFetch(built.RelativeRequestUri));
        var expected = DateRangeResolver.Resolve(
            plan.RelativeDate.GetExpression(), DateTimeOffset.UtcNow, Cairo);
        Assert.Equal(expected.StartUtc, ge);
        Assert.Equal(expected.EndUtcExclusive, lt);
        Assert.True(built.FillMissingMonthBuckets);
        Assert.Equal(12, built.CalendarMonthBuckets!.Count);
    }

    [Fact]
    public void Typed_DateRange_Object_Arg_And_JsonElement_Object()
    {
        var typed = CrmAiTools.ResolveDateRangeJson(
            new QueryCrmDateRangeArg { Kind = "calendar_year", Year = 2025 },
            dateRangeJson: null);
        Assert.Contains("calendar_year", typed);
        Assert.Contains("2025", typed);

        var fromObject = CrmQueryPlanParser.ParseFromToolArguments(new Dictionary<string, object?>
        {
            ["entity"] = "salesorder",
            ["operation"] = "list",
            ["relativeDateField"] = "createdon",
            ["dateRange"] = JsonDocument.Parse("""{"kind":"calendar_year","year":2025}""").RootElement
        });
        Assert.True(fromObject.Success, fromObject.Error);
        Assert.Equal(2025, fromObject.Plan!.RelativeDate!.GetExpression().Year);
    }

    [Fact]
    public void Missing_Parameters_For_Supported_Kind()
    {
        var noYear = ParseDate("""{"kind":"calendar_year"}""");
        Assert.False(noYear.Success);
        Assert.Contains("Year", noYear.Error, StringComparison.OrdinalIgnoreCase);

        var badQuarter = ParseDate("""{"kind":"calendar_quarter","year":2025,"quarter":9}""");
        Assert.False(badQuarter.Success);
    }

    [Fact]
    public void Tool_Exposes_Typed_DateRange_And_Legacy_Json()
    {
        var names = typeof(CrmAiTools).GetMethod("query_crm")!.GetParameters().Select(p => p.Name).ToArray();
        Assert.Contains("dateRange", names);
        Assert.Contains("dateRangeJson", names);

        var prompt = typeof(ChatService)
            .GetField("SystemPrompt", BindingFlags.NonPublic | BindingFlags.Static)!
            .GetValue(null) as string;
        Assert.Contains("\"kind\":\"calendar_year\",\"year\":2025", prompt);
        Assert.Contains("ALWAYS include kind", prompt);
    }

    [Fact]
    public void List_Count_Filter_Sort_Unaffected()
    {
        var list = CrmQueryPlanParser.ParseFromToolArguments(new Dictionary<string, object?>
        {
            ["entity"] = "account",
            ["operation"] = "list",
            ["selectJson"] = """["name"]""",
            ["filtersJson"] = """[{"field":"name","operator":"eq","value":"acme"}]""",
            ["sortJson"] = """[{"field":"name","direction":"asc"}]""",
            ["limit"] = 5
        });
        Assert.True(list.Success, list.Error);
        Assert.Equal(CrmQueryOperation.List, list.Plan!.Operation);
        Assert.Single(list.Plan.Filters);
        Assert.Single(list.Plan.Sort);

        var count = CrmQueryPlanParser.ParseFromToolArguments(new Dictionary<string, object?>
        {
            ["entity"] = "lead",
            ["operation"] = "count"
        });
        Assert.True(count.Success, count.Error);
        Assert.Equal(CrmQueryOperation.Count, count.Plan!.Operation);
    }
}
