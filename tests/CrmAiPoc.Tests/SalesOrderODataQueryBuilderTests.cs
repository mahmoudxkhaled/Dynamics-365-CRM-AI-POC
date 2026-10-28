using CrmAiPoc.Api.Configuration;
using CrmAiPoc.Api.Crm.Query;
using Microsoft.Extensions.Options;

namespace CrmAiPoc.Tests;

public class SalesOrderODataQueryBuilderTests
{
    private static SalesOrderODataQueryBuilder CreateBuilder(DateTimeOffset? utcNow = null)
    {
        var options = Options.Create(new CrmOptions { BusinessTimeZoneId = "Africa/Cairo" });
        return new SalesOrderODataQueryBuilder(options, () => utcNow ?? new DateTimeOffset(2026, 10, 28, 12, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public void Build_DefaultList_SelectsAllowlistedFieldsAndDefaultSort()
    {
        var result = CreateBuilder().Build(new SalesOrderQueryPlan());
        Assert.True(result.Success);
        Assert.Contains("salesorders?$select=", result.RelativeRequestUri);
        Assert.Contains("totalamount", result.RelativeRequestUri);
        Assert.Contains("$orderby=createdon desc", result.RelativeRequestUri);
        Assert.Contains("$top=10", result.RelativeRequestUri);
        Assert.DoesNotContain("$filter=", result.RelativeRequestUri);
    }

    [Fact]
    public void Build_RejectsUnknownField()
    {
        var plan = new SalesOrderQueryPlan
        {
            Filters =
            [
                new SalesOrderQueryFilter
                {
                    Field = "passwordhash",
                    Operator = SalesOrderFilterOperator.Eq,
                    Value = "x"
                }
            ]
        };

        var result = CreateBuilder().Build(plan);
        Assert.False(result.Success);
        Assert.Contains("not allowlisted", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Build_RejectsContainsOnMoney()
    {
        var plan = new SalesOrderQueryPlan
        {
            Filters =
            [
                new SalesOrderQueryFilter
                {
                    Field = "totalamount",
                    Operator = SalesOrderFilterOperator.Contains,
                    Value = "100"
                }
            ]
        };

        var result = CreateBuilder().Build(plan);
        Assert.False(result.Success);
    }

    [Fact]
    public void Build_EscapesODataStringInjection()
    {
        var plan = new SalesOrderQueryPlan
        {
            Filters =
            [
                new SalesOrderQueryFilter
                {
                    Field = "name",
                    Operator = SalesOrderFilterOperator.Contains,
                    Value = "ABC' or 1 eq 1"
                }
            ]
        };

        var result = CreateBuilder().Build(plan);
        Assert.True(result.Success);
        Assert.Contains("contains(name,'ABC'' or 1 eq 1')", result.RelativeRequestUri);
    }

    [Fact]
    public void Build_RejectsFetchXmlPayloadInText()
    {
        var plan = new SalesOrderQueryPlan
        {
            Filters =
            [
                new SalesOrderQueryFilter
                {
                    Field = "name",
                    Operator = SalesOrderFilterOperator.Contains,
                    Value = "<fetch version='1.0'>"
                }
            ]
        };

        var result = CreateBuilder().Build(plan);
        Assert.False(result.Success);
    }

    [Fact]
    public void Build_AmountFilter_UsesInvariantLiteral()
    {
        var plan = new SalesOrderQueryPlan
        {
            Filters =
            [
                new SalesOrderQueryFilter
                {
                    Field = "totalamount",
                    Operator = SalesOrderFilterOperator.Gt,
                    Value = "10000"
                }
            ]
        };

        var result = CreateBuilder().Build(plan);
        Assert.True(result.Success);
        Assert.Contains("totalamount gt 10000", result.RelativeRequestUri);
    }

    [Fact]
    public void Build_ActiveState_ResolvesLabelToCode()
    {
        var plan = new SalesOrderQueryPlan
        {
            Filters =
            [
                new SalesOrderQueryFilter
                {
                    Field = "statecode",
                    Operator = SalesOrderFilterOperator.Eq,
                    Value = "Active"
                }
            ]
        };

        var result = CreateBuilder().Build(plan);
        Assert.True(result.Success);
        Assert.Contains("statecode eq 0", result.RelativeRequestUri);
        Assert.Contains("Active", result.AppliedFilterSummary[0]);
    }

    [Fact]
    public void Build_LastCalendarMonth_UsesCairoBoundaries()
    {
        // 2026-10-28 Cairo → last calendar month = September 2026 (UTC offset depends on TZ database)
        var utcNow = new DateTimeOffset(2026, 10, 28, 12, 0, 0, TimeSpan.Zero);
        var builder = CreateBuilder(utcNow);
        var plan = new SalesOrderQueryPlan
        {
            RelativeDate = new SalesOrderRelativeDateFilter
            {
                Field = "createdon",
                Token = RelativeDateToken.LastCalendarMonth
            }
        };

        var result = builder.Build(plan);
        Assert.True(result.Success);
        Assert.Contains("createdon ge ", result.RelativeRequestUri);
        Assert.Contains("createdon lt ", result.RelativeRequestUri);
        Assert.Contains(" and ", result.RelativeRequestUri);
        Assert.Contains("last calendar month", result.AppliedFilterSummary[0]);

        var (start, end, _) = RelativeDateResolver.Resolve(
            RelativeDateToken.LastCalendarMonth,
            utcNow,
            BusinessTimeZone.Resolve("Africa/Cairo"));
        Assert.True(start < end);
        Assert.Equal(9, TimeZoneInfo.ConvertTime(start, BusinessTimeZone.Resolve("Africa/Cairo")).Month);
    }

    [Fact]
    public void Build_Last7Days_IsRollingNotCalendar()
    {
        var now = new DateTimeOffset(2026, 10, 28, 12, 0, 0, TimeSpan.Zero);
        var builder = CreateBuilder(now);
        var plan = new SalesOrderQueryPlan
        {
            RelativeDate = new SalesOrderRelativeDateFilter
            {
                Field = "createdon",
                Token = RelativeDateToken.Last7Days
            }
        };

        var result = builder.Build(plan);
        Assert.True(result.Success);
        Assert.Contains("createdon ge 2026-10-21T12:00:00Z", result.RelativeRequestUri);
        Assert.Contains("createdon lt 2026-10-28T12:00:00Z", result.RelativeRequestUri);
    }

    [Fact]
    public void Build_HighestValue_SortAndLimit()
    {
        var plan = new SalesOrderQueryPlan
        {
            SortField = "totalamount",
            SortDirection = SalesOrderSortDirection.Desc,
            Limit = 5
        };

        var result = CreateBuilder().Build(plan);
        Assert.True(result.Success);
        Assert.Contains("$orderby=totalamount desc", result.RelativeRequestUri);
        Assert.Contains("$top=5", result.RelativeRequestUri);
    }

    [Fact]
    public void Build_ClampsLimitTo50()
    {
        var plan = new SalesOrderQueryPlan { Limit = 999 };
        var result = CreateBuilder().Build(plan);
        Assert.True(result.Success);
        Assert.Equal(50, result.Limit);
        Assert.Contains("$top=50", result.RelativeRequestUri);
    }

    [Fact]
    public void Build_RejectsMoreThanThreeFilters()
    {
        var plan = new SalesOrderQueryPlan
        {
            Filters =
            [
                new() { Field = "statecode", Operator = SalesOrderFilterOperator.Eq, Value = "0" },
                new() { Field = "statuscode", Operator = SalesOrderFilterOperator.Eq, Value = "1" },
                new() { Field = "totalamount", Operator = SalesOrderFilterOperator.Gt, Value = "1" },
                new() { Field = "name", Operator = SalesOrderFilterOperator.Contains, Value = "A" }
            ]
        };

        var result = CreateBuilder().Build(plan);
        Assert.False(result.Success);
    }

    [Fact]
    public void Build_Count_UsesCountTrueAndTop1()
    {
        var plan = new SalesOrderQueryPlan
        {
            Operation = SalesOrderQueryOperation.Count,
            RelativeDate = new SalesOrderRelativeDateFilter
            {
                Field = "createdon",
                Token = RelativeDateToken.ThisWeek
            }
        };

        var result = CreateBuilder().Build(plan);
        Assert.True(result.Success);
        Assert.Contains("$count=true", result.RelativeRequestUri);
        Assert.Contains("$top=1", result.RelativeRequestUri);
        Assert.DoesNotContain("$orderby=", result.RelativeRequestUri);
    }

    [Fact]
    public void Build_LookupRequiresGuid_NotCustomerName()
    {
        var plan = new SalesOrderQueryPlan
        {
            Filters =
            [
                new SalesOrderQueryFilter
                {
                    Field = "te_customeraccountid",
                    Operator = SalesOrderFilterOperator.Eq,
                    Value = "Ahmed Trading"
                }
            ]
        };

        var result = CreateBuilder().Build(plan);
        Assert.False(result.Success);
        Assert.Contains("GUID", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Build_LookupGuid_UsesWebApiAttribute()
    {
        var id = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
        var plan = new SalesOrderQueryPlan
        {
            Filters =
            [
                new SalesOrderQueryFilter
                {
                    Field = "te_customeraccountid",
                    Operator = SalesOrderFilterOperator.Eq,
                    Value = id.ToString()
                }
            ]
        };

        var result = CreateBuilder().Build(plan);
        Assert.True(result.Success);
        Assert.Contains($"_te_customeraccountid_value eq {id:D}", result.RelativeRequestUri);
    }

    [Fact]
    public void Build_RejectsUnsupportedSortField()
    {
        var plan = new SalesOrderQueryPlan { SortField = "name" };
        var result = CreateBuilder().Build(plan);
        Assert.False(result.Success);
    }
}
