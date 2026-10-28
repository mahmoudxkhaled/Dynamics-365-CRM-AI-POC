using CrmAiPoc.Api.Crm.Query;

namespace CrmAiPoc.Tests;

public class SalesOrderQueryPlanParserTests
{
    [Fact]
    public void Parse_NaturalLanguageStyleArgs_BuildsPlan()
    {
        var parsed = SalesOrderQueryPlanParser.Parse(
            "list",
            """[{"field":"totalamount","operator":"gt","value":"10000"}]""",
            null,
            null,
            "totalamount",
            "desc",
            5);

        Assert.True(parsed.Success);
        Assert.Equal(SalesOrderQueryOperation.List, parsed.Plan!.Operation);
        Assert.Single(parsed.Plan.Filters);
        Assert.Equal("totalamount", parsed.Plan.SortField);
        Assert.Equal(5, parsed.Plan.Limit);
    }

    [Fact]
    public void Parse_RelativeDateTokens()
    {
        var parsed = SalesOrderQueryPlanParser.Parse(
            "count",
            null,
            "createdon",
            "this_week",
            null,
            null,
            null);

        Assert.True(parsed.Success);
        Assert.Equal(SalesOrderQueryOperation.Count, parsed.Plan!.Operation);
        Assert.Equal(RelativeDateToken.ThisWeek, parsed.Plan.RelativeDate!.Token);
    }

    [Fact]
    public void Parse_LastMonthAlias()
    {
        Assert.True(RelativeDateResolver.TryParseToken("last_month", out var token));
        Assert.Equal(RelativeDateToken.LastCalendarMonth, token);
        Assert.True(RelativeDateResolver.TryParseToken("last_30_days", out var rolling));
        Assert.Equal(RelativeDateToken.Last30Days, rolling);
    }

    [Fact]
    public void Parse_RejectsInvalidOperator()
    {
        var parsed = SalesOrderQueryPlanParser.Parse(
            "list",
            """[{"field":"name","operator":"startswith","value":"A"}]""",
            null,
            null,
            null,
            null,
            10);

        Assert.False(parsed.Success);
    }

    [Fact]
    public void ParseFromToolArguments_Dictionary()
    {
        var args = new Dictionary<string, object?>
        {
            ["operation"] = "list",
            ["filtersJson"] = """[{"field":"statecode","operator":"eq","value":"Active"}]""",
            ["limit"] = 10
        };

        var parsed = SalesOrderQueryPlanParser.ParseFromToolArguments(args);
        Assert.True(parsed.Success);
        Assert.Equal("statecode", parsed.Plan!.Filters[0].Field);
        Assert.Equal(SalesOrderFilterOperator.Eq, parsed.Plan.Filters[0].Operator);
    }
}
