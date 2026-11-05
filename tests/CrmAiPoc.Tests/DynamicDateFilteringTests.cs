using System.Reflection;
using System.Xml.Linq;
using CrmAiPoc.Api.Ai;
using CrmAiPoc.Api.Configuration;
using CrmAiPoc.Api.Crm.Generic;
using CrmAiPoc.Api.Crm.Metadata;
using CrmAiPoc.Api.Crm.Query;
using Microsoft.Extensions.Options;

namespace CrmAiPoc.Tests;

/// <summary>
/// Regression for generic dynamic date filtering (absolute + parameterized relative ranges).
/// Scenarios A–G from the date-filtering deliverable.
/// </summary>
public sealed class DynamicDateFilteringTests
{
    private static readonly TimeZoneInfo Cairo = BusinessTimeZone.Resolve("Africa/Cairo");

    private static (
        GenericCrmQueryBuilder ListBuilder,
        FetchXmlAggregateBuilder AggBuilder,
        CrmEntityMetadata SalesOrder,
        Func<string, CrmEntityMetadata?> GetEntity)
        CreateBuilders(Func<DateTimeOffset>? utcNow = null)
    {
        var options = Options.Create(new CrmOptions
        {
            ApprovedEntities = ["account", "contact", "salesorder", "lead"],
            ApprovedRelationships =
            [
                "te_account_salesorder_customeraccountid",
                "order_customer_accounts",
                "contact_customer_accounts"
            ],
            BusinessTimeZoneId = "Africa/Cairo"
        });
        var policy = new EntityApprovalPolicy(options);
        var seeds = StaticSeedCrmMetadata.CreateApprovedSeeds(policy);
        var entity = seeds.First(e => e.LogicalName == "salesorder");
        Func<string, CrmEntityMetadata?> getEntity = n =>
            seeds.FirstOrDefault(e => e.LogicalName.Equals(n, StringComparison.OrdinalIgnoreCase));
        return (
            new GenericCrmQueryBuilder(options, utcNow),
            new FetchXmlAggregateBuilder(options, utcNow),
            entity,
            getEntity);
    }

    private static string DecodeFetch(string? uri)
    {
        Assert.False(string.IsNullOrWhiteSpace(uri));
        Assert.Contains("fetchXml=", uri);
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

    [Fact]
    public void A_MonthlyCounts_JanThroughJune2025()
    {
        var parsed = CrmQueryPlanParser.ParseFromToolArguments(new Dictionary<string, object?>
        {
            ["entity"] = "salesorder",
            ["operation"] = "aggregate",
            ["aggregatesJson"] = """[{"function":"count","field":"salesorderid","alias":"record_count"}]""",
            ["groupByJson"] =
                """[{"field":"createdon","alias":"year","dateGrouping":"year"},{"field":"createdon","alias":"month","dateGrouping":"month"}]""",
            ["relativeDateField"] = "createdon",
            ["dateRangeJson"] = """{"kind":"calendar_month_range","year":2025,"monthStart":1,"monthEnd":6}"""
        });
        Assert.True(parsed.Success, parsed.Error);
        Assert.Equal(CrmQueryOperation.Aggregate, parsed.Plan!.Operation);
        Assert.Equal(CrmDateRangeKind.CalendarMonthRange, parsed.Plan.RelativeDate!.GetExpression().Kind);

        var resolved = DateRangeResolver.Resolve(parsed.Plan.RelativeDate.GetExpression(), DateTimeOffset.UtcNow, Cairo);
        Assert.Equal(6, resolved.CalendarMonthBuckets!.Count);
        Assert.Equal((2025, 1), resolved.CalendarMonthBuckets[0]);
        Assert.Equal((2025, 6), resolved.CalendarMonthBuckets[^1]);

        var startLocal = TimeZoneInfo.ConvertTime(resolved.StartUtc, Cairo);
        var endLocal = TimeZoneInfo.ConvertTime(resolved.EndUtcExclusive, Cairo);
        Assert.Equal(new DateTime(2025, 1, 1), startLocal.DateTime);
        Assert.Equal(new DateTime(2025, 7, 1), endLocal.DateTime);

        var (_, agg, entity, _) = CreateBuilders();
        var built = agg.Build(parsed.Plan, entity);
        Assert.True(built.Success, built.Error);
        Assert.True(built.FillMissingMonthBuckets);
        Assert.Equal(6, built.CalendarMonthBuckets!.Count);
        var (ge, lt) = ExtractCreatedOnBounds(built.FetchXml!);
        Assert.Equal(resolved.StartUtc, ge);
        Assert.Equal(resolved.EndUtcExclusive, lt);
    }

    [Fact]
    public void B_AmbiguousSixMonthsIn2025_HasNoKeywordOverride_AndPromptAsksClarification()
    {
        // Backend does not invent which six months from natural language — only structured dateRangeJson.
        var methods = typeof(CrmQueryIntentHelper).GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Select(m => m.Name)
            .ToArray();
        Assert.DoesNotContain(methods, n => n.Contains("Date", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(methods, n => n.Contains("Correct", StringComparison.OrdinalIgnoreCase));

        var prompt = typeof(ChatService)
            .GetField("SystemPrompt", BindingFlags.NonPublic | BindingFlags.Static)!
            .GetValue(null) as string;
        Assert.Contains("six months within 2025", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ask which period", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("dateRangeJson", prompt);
    }

    [Fact]
    public void C_MonthlyCounts_Last8CalendarMonths()
    {
        var utcNow = new DateTimeOffset(2026, 11, 4, 12, 0, 0, TimeSpan.Zero);
        var parsed = CrmQueryPlanParser.ParseFromToolArguments(new Dictionary<string, object?>
        {
            ["entity"] = "salesorder",
            ["operation"] = "aggregate",
            ["aggregatesJson"] = """[{"function":"count","field":"salesorderid","alias":"record_count"}]""",
            ["groupByJson"] =
                """[{"field":"createdon","alias":"year","dateGrouping":"year"},{"field":"createdon","alias":"month","dateGrouping":"month"}]""",
            ["relativeDateField"] = "createdon",
            ["dateRangeJson"] = """{"kind":"last_n_calendar_months","n":8}"""
        });
        Assert.True(parsed.Success, parsed.Error);
        Assert.Equal(CrmDateRangeKind.LastNCalendarMonths, parsed.Plan!.RelativeDate!.GetExpression().Kind);
        Assert.Equal(8, parsed.Plan.RelativeDate.GetExpression().N);

        var resolved = DateRangeResolver.Resolve(parsed.Plan.RelativeDate.GetExpression(), utcNow, Cairo);
        Assert.Equal(8, resolved.CalendarMonthBuckets!.Count);
        Assert.Equal((2026, 4), resolved.CalendarMonthBuckets[0]);
        Assert.Equal((2026, 11), resolved.CalendarMonthBuckets[^1]);

        var (_, agg, entity, _) = CreateBuilders(() => utcNow);
        var built = agg.Build(parsed.Plan, entity);
        Assert.True(built.Success, built.Error);
        Assert.Equal(8, built.CalendarMonthBuckets!.Count);
        var filled = AggregateResultNormalizer.Normalize(
            Array.Empty<IReadOnlyDictionary<string, object?>>(),
            parsed.Plan,
            entity,
            built);
        Assert.Equal(8, filled.Count);
        Assert.Equal(2026, filled[0]["year"]);
        Assert.Equal(4, filled[0]["month"]);
        Assert.Equal(2026, filled[^1]["year"]);
        Assert.Equal(11, filled[^1]["month"]);
    }

    [Fact]
    public void D_Top10HighestValue_ThisYear_WithCustomerAccount()
    {
        var utcNow = new DateTimeOffset(2026, 11, 4, 15, 30, 0, TimeSpan.Zero);
        var parsed = CrmQueryPlanParser.ParseFromToolArguments(new Dictionary<string, object?>
        {
            ["entity"] = "salesorder",
            ["operation"] = "list",
            ["selectJson"] = """["name","totalamount","createdon"]""",
            ["sortField"] = "totalamount",
            ["sortDirection"] = "desc",
            ["limit"] = 10,
            ["relativeDateField"] = "createdon",
            ["dateRangeJson"] = """{"kind":"this_calendar_year"}""",
            ["linksJson"] =
                """[{"entity":"account","relationship":"te_account_salesorder_customeraccountid","alias":"ca","select":["name"]}]"""
        });
        Assert.True(parsed.Success, parsed.Error);
        Assert.Equal(CrmQueryOperation.List, parsed.Plan!.Operation);
        Assert.Equal(10, parsed.Plan.Limit);
        Assert.Equal(CrmSortDirection.Desc, parsed.Plan.Sort[0].Direction);
        Assert.Equal("totalamount", parsed.Plan.Sort[0].Field);
        Assert.Equal(CrmDateRangeKind.ThisCalendarYear, parsed.Plan.RelativeDate!.GetExpression().Kind);
        Assert.Single(parsed.Plan.Links);

        var resolved = DateRangeResolver.Resolve(parsed.Plan.RelativeDate.GetExpression(), utcNow, Cairo);
        var startLocal = TimeZoneInfo.ConvertTime(resolved.StartUtc, Cairo);
        Assert.Equal(2026, startLocal.Year);
        Assert.Equal(1, startLocal.Month);
        Assert.Equal(1, startLocal.Day);
        Assert.Equal(utcNow, resolved.EndUtcExclusive);

        var (listBuilder, _, entity, getEntity) = CreateBuilders(() => utcNow);
        var built = listBuilder.Build(parsed.Plan, entity, getEntity: getEntity);
        Assert.True(built.Success, built.Error);
        var fetch = DecodeFetch(built.RelativeRequestUri);
        Assert.Contains("top=\"10\"", fetch);
        Assert.Contains("totalamount", fetch);
        Assert.Contains("descending=\"true\"", fetch);
        Assert.Contains("link-entity", fetch);
        var (ge, lt) = ExtractCreatedOnBounds(fetch);
        Assert.Equal(resolved.StartUtc, ge);
        Assert.Equal(resolved.EndUtcExclusive, lt);
    }

    [Fact]
    public void E_List_AbsoluteHistoricalRange()
    {
        // Inclusive Mar 15 – Sep 20 → half-open [2024-03-15, 2024-09-21)
        var parsed = CrmQueryPlanParser.ParseFromToolArguments(new Dictionary<string, object?>
        {
            ["entity"] = "salesorder",
            ["operation"] = "list",
            ["limit"] = 25,
            ["relativeDateField"] = "createdon",
            ["dateRangeJson"] =
                """{"kind":"absolute","start":"2024-03-15T00:00:00+02:00","endExclusive":"2024-09-21T00:00:00+02:00"}"""
        });
        Assert.True(parsed.Success, parsed.Error);
        Assert.Equal(CrmQueryOperation.List, parsed.Plan!.Operation);

        var resolved = DateRangeResolver.Resolve(parsed.Plan.RelativeDate!.GetExpression(), DateTimeOffset.UtcNow, Cairo);
        var startLocal = TimeZoneInfo.ConvertTime(resolved.StartUtc, Cairo);
        var endLocal = TimeZoneInfo.ConvertTime(resolved.EndUtcExclusive, Cairo);
        Assert.Equal(new DateTime(2024, 3, 15), startLocal.Date);
        Assert.Equal(new DateTime(2024, 9, 21), endLocal.Date);

        var (listBuilder, _, entity, _) = CreateBuilders();
        var built = listBuilder.Build(parsed.Plan, entity);
        Assert.True(built.Success, built.Error);
        var (ge, lt) = ExtractCreatedOnBounds(DecodeFetch(built.RelativeRequestUri));
        Assert.Equal(resolved.StartUtc, ge);
        Assert.Equal(resolved.EndUtcExclusive, lt);
    }

    [Fact]
    public void F_Count_Q2_2025()
    {
        var parsed = CrmQueryPlanParser.ParseFromToolArguments(new Dictionary<string, object?>
        {
            ["entity"] = "salesorder",
            ["operation"] = "count",
            ["relativeDateField"] = "createdon",
            ["dateRangeJson"] = """{"kind":"calendar_quarter","year":2025,"quarter":2}"""
        });
        Assert.True(parsed.Success, parsed.Error);
        Assert.Equal(CrmQueryOperation.Count, parsed.Plan!.Operation);

        var resolved = DateRangeResolver.Resolve(parsed.Plan.RelativeDate!.GetExpression(), DateTimeOffset.UtcNow, Cairo);
        var startLocal = TimeZoneInfo.ConvertTime(resolved.StartUtc, Cairo);
        var endLocal = TimeZoneInfo.ConvertTime(resolved.EndUtcExclusive, Cairo);
        Assert.Equal(new DateTime(2025, 4, 1), startLocal.DateTime);
        Assert.Equal(new DateTime(2025, 7, 1), endLocal.DateTime);

        var (listBuilder, _, entity, _) = CreateBuilders();
        var built = listBuilder.Build(parsed.Plan, entity);
        Assert.True(built.Success, built.Error);
        var fetch = DecodeFetch(built.RelativeRequestUri);
        Assert.Contains("aggregate=\"true\"", fetch);
        var (ge, lt) = ExtractCreatedOnBounds(fetch);
        Assert.Equal(resolved.StartUtc, ge);
        Assert.Equal(resolved.EndUtcExclusive, lt);
    }

    [Fact]
    public void G_List_Last45Days()
    {
        var utcNow = new DateTimeOffset(2026, 11, 4, 12, 0, 0, TimeSpan.Zero);
        var parsed = CrmQueryPlanParser.ParseFromToolArguments(new Dictionary<string, object?>
        {
            ["entity"] = "salesorder",
            ["operation"] = "list",
            ["limit"] = 20,
            ["relativeDateField"] = "createdon",
            ["dateRangeJson"] = """{"kind":"last_n_days","n":45}"""
        });
        Assert.True(parsed.Success, parsed.Error);
        Assert.Equal(45, parsed.Plan!.RelativeDate!.GetExpression().N);

        var resolved = DateRangeResolver.Resolve(parsed.Plan.RelativeDate.GetExpression(), utcNow, Cairo);
        Assert.Equal(utcNow.AddDays(-45), resolved.StartUtc);
        Assert.Equal(utcNow, resolved.EndUtcExclusive);

        var (listBuilder, _, entity, _) = CreateBuilders(() => utcNow);
        var built = listBuilder.Build(parsed.Plan, entity);
        Assert.True(built.Success, built.Error);
        var (ge, lt) = ExtractCreatedOnBounds(DecodeFetch(built.RelativeRequestUri));
        Assert.Equal(resolved.StartUtc, ge);
        Assert.Equal(resolved.EndUtcExclusive, lt);
    }

    [Fact]
    public void LegacyToken_StillMaps_ThroughExpression()
    {
        var parsed = CrmQueryPlanParser.ParseFromToolArguments(new Dictionary<string, object?>
        {
            ["entity"] = "salesorder",
            ["operation"] = "list",
            ["relativeDateField"] = "createdon",
            ["relativeDateToken"] = "last_6_calendar_months"
        });
        Assert.True(parsed.Success, parsed.Error);
        Assert.Equal(RelativeDateToken.Last6CalendarMonths, parsed.Plan!.RelativeDate!.Token);
        var expr = parsed.Plan.RelativeDate.GetExpression();
        Assert.Equal(CrmDateRangeKind.LastNCalendarMonths, expr.Kind);
        Assert.Equal(6, expr.N);
    }

    [Fact]
    public void Invalid_N_And_UnsupportedField_Fail()
    {
        var badN = CrmQueryPlanParser.ParseFromToolArguments(new Dictionary<string, object?>
        {
            ["entity"] = "salesorder",
            ["operation"] = "list",
            ["relativeDateField"] = "createdon",
            ["dateRangeJson"] = """{"kind":"last_n_days","n":0}"""
        });
        Assert.False(badN.Success);
        Assert.Contains("N must be", badN.Error);

        var badKind = CrmQueryPlanParser.ParseFromToolArguments(new Dictionary<string, object?>
        {
            ["entity"] = "salesorder",
            ["operation"] = "list",
            ["relativeDateField"] = "createdon",
            ["dateRangeJson"] = """{"kind":"next_tuesday"}"""
        });
        Assert.False(badKind.Success);
        Assert.Contains("Unsupported dateRangeJson kind", badKind.Error);

        var inverted = CrmQueryPlanParser.ParseFromToolArguments(new Dictionary<string, object?>
        {
            ["entity"] = "salesorder",
            ["operation"] = "list",
            ["relativeDateField"] = "createdon",
            ["dateRangeJson"] =
                """{"kind":"absolute","start":"2025-06-01T00:00:00Z","endExclusive":"2025-01-01T00:00:00Z"}"""
        });
        Assert.False(inverted.Success);

        var (listBuilder, _, entity, _) = CreateBuilders();
        var plan = new CrmQueryPlan
        {
            Entity = "salesorder",
            Operation = CrmQueryOperation.List,
            RelativeDate = new CrmRelativeDateFilter
            {
                Field = "name",
                Expression = new CrmDateRangeExpression { Kind = CrmDateRangeKind.ThisCalendarYear }
            }
        };
        var built = listBuilder.Build(plan, entity);
        Assert.False(built.Success);
        Assert.Contains("not an approved date field", built.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LeapYear_And_MonthBoundaries()
    {
        var leap = DateRangeResolver.Resolve(
            new CrmDateRangeExpression
            {
                Kind = CrmDateRangeKind.CalendarMonthRange,
                Year = 2024,
                MonthStart = 2,
                MonthEnd = 2
            },
            DateTimeOffset.UtcNow,
            Cairo);
        var startLocal = TimeZoneInfo.ConvertTime(leap.StartUtc, Cairo);
        var endLocal = TimeZoneInfo.ConvertTime(leap.EndUtcExclusive, Cairo);
        Assert.Equal(new DateTime(2024, 2, 1), startLocal.DateTime);
        Assert.Equal(new DateTime(2024, 3, 1), endLocal.DateTime);
        Assert.Equal(29, (endLocal - startLocal).Days);

        var yearEnd = DateRangeResolver.Resolve(
            new CrmDateRangeExpression { Kind = CrmDateRangeKind.CalendarYear, Year = 2025 },
            DateTimeOffset.UtcNow,
            Cairo);
        Assert.Equal(
            new DateTime(2025, 1, 1),
            TimeZoneInfo.ConvertTime(yearEnd.StartUtc, Cairo).DateTime);
        Assert.Equal(
            new DateTime(2026, 1, 1),
            TimeZoneInfo.ConvertTime(yearEnd.EndUtcExclusive, Cairo).DateTime);
        Assert.Equal(12, yearEnd.CalendarMonthBuckets!.Count);
    }

    [Fact]
    public void ToolSchema_And_Prompt_Expose_DateRangeJson()
    {
        var method = typeof(CrmAiTools).GetMethod("query_crm")!;
        var names = method.GetParameters().Select(p => p.Name).ToArray();
        Assert.Contains("dateRange", names);
        Assert.Contains("dateRangeJson", names);

        var prompt = typeof(ChatService)
            .GetField("SystemPrompt", BindingFlags.NonPublic | BindingFlags.Static)!
            .GetValue(null) as string;
        Assert.Contains("dateRange", prompt);
        Assert.Contains("this_calendar_year", prompt);
        Assert.Contains("last_n_calendar_months", prompt);
        Assert.DoesNotContain(
            "last_calendar_month | this_calendar_month | this_week | last_7_days | last_30_days | last_6_calendar_months",
            prompt);
    }

    [Fact]
    public void DateRangeJson_TakesPrecedence_Over_LegacyToken()
    {
        var parsed = CrmQueryPlanParser.ParseFromToolArguments(new Dictionary<string, object?>
        {
            ["entity"] = "salesorder",
            ["operation"] = "list",
            ["relativeDateField"] = "createdon",
            ["relativeDateToken"] = "last_7_days",
            ["dateRangeJson"] = """{"kind":"last_n_days","n":45}"""
        });
        Assert.True(parsed.Success, parsed.Error);
        Assert.Null(parsed.Plan!.RelativeDate!.Token);
        Assert.Equal(45, parsed.Plan.RelativeDate.GetExpression().N);
    }
}
