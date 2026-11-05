using System.Globalization;

namespace CrmAiPoc.Api.Crm.Query;

/// <summary>
/// Resolves structured date-range expressions into half-open UTC ranges [StartUtc, EndUtcExclusive).
/// Calendar kinds use the business timezone; rolling kinds use the provided UTC clock.
/// </summary>
public static class DateRangeResolver
{
    public const int MaxNDays = 3660;
    public const int MaxNWeeks = 520;
    public const int MaxNMonths = 120;
    public const int MaxNYears = 50;

    public static ResolvedDateRange Resolve(
        CrmDateRangeExpression expression,
        DateTimeOffset utcNow,
        TimeZoneInfo businessTz)
    {
        var localNow = TimeZoneInfo.ConvertTime(utcNow, businessTz);

        return expression.Kind switch
        {
            CrmDateRangeKind.Absolute => ResolveAbsolute(expression, businessTz),
            CrmDateRangeKind.LastNDays => RollingDays(utcNow, RequireN(expression, 1, MaxNDays), "days"),
            CrmDateRangeKind.LastNWeeks => RollingDays(utcNow, RequireN(expression, 1, MaxNWeeks) * 7, "weeks",
                expression.N),
            CrmDateRangeKind.LastNCalendarMonths => LastNCalendarMonths(
                localNow, businessTz, RequireN(expression, 1, MaxNMonths)),
            CrmDateRangeKind.LastNRollingMonths => RollingMonths(
                utcNow, RequireN(expression, 1, MaxNMonths)),
            CrmDateRangeKind.LastNYears => LastNCalendarYears(
                localNow, businessTz, RequireN(expression, 1, MaxNYears)),
            CrmDateRangeKind.ThisCalendarMonth => WithBuckets(
                CalendarMonth(localNow.Year, localNow.Month, businessTz, "this calendar month")),
            CrmDateRangeKind.ThisCalendarQuarter => ThisCalendarQuarter(localNow, businessTz),
            CrmDateRangeKind.ThisCalendarYear => ThisCalendarYearYtd(localNow, utcNow, businessTz),
            CrmDateRangeKind.ThisWeek => ThisWeek(localNow, businessTz),
            CrmDateRangeKind.PreviousCalendarMonth => PreviousCalendarMonth(localNow, businessTz),
            CrmDateRangeKind.PreviousCalendarQuarter => PreviousCalendarQuarter(localNow, businessTz),
            CrmDateRangeKind.PreviousCalendarYear => PreviousCalendarYear(localNow, businessTz),
            CrmDateRangeKind.CalendarYear => FullCalendarYear(RequireYear(expression), businessTz),
            CrmDateRangeKind.CalendarQuarter => CalendarQuarterRange(
                RequireYear(expression), RequireQuarter(expression), businessTz),
            CrmDateRangeKind.CalendarMonthRange => CalendarMonthRange(
                RequireYear(expression),
                RequireMonth(expression.MonthStart, "monthStart"),
                RequireMonth(expression.MonthEnd ?? expression.MonthStart, "monthEnd"),
                businessTz),
            _ => throw new ArgumentOutOfRangeException(nameof(expression), expression.Kind, "Unsupported date range kind.")
        };
    }

    /// <summary>Legacy fixed-token resolve (backward compatible).</summary>
    public static (DateTimeOffset StartUtc, DateTimeOffset EndUtcExclusive, string Description) Resolve(
        RelativeDateToken token,
        DateTimeOffset utcNow,
        TimeZoneInfo businessTz)
    {
        var resolved = Resolve(FromLegacyToken(token), utcNow, businessTz);
        return (resolved.StartUtc, resolved.EndUtcExclusive, resolved.Description);
    }

    public static CrmDateRangeExpression FromLegacyToken(RelativeDateToken token) => token switch
    {
        RelativeDateToken.Last7Days => new() { Kind = CrmDateRangeKind.LastNDays, N = 7 },
        RelativeDateToken.Last30Days => new() { Kind = CrmDateRangeKind.LastNDays, N = 30 },
        RelativeDateToken.ThisCalendarMonth => new() { Kind = CrmDateRangeKind.ThisCalendarMonth },
        RelativeDateToken.LastCalendarMonth => new() { Kind = CrmDateRangeKind.PreviousCalendarMonth },
        RelativeDateToken.ThisWeek => new() { Kind = CrmDateRangeKind.ThisWeek },
        RelativeDateToken.Last6CalendarMonths => new() { Kind = CrmDateRangeKind.LastNCalendarMonths, N = 6 },
        _ => throw new ArgumentOutOfRangeException(nameof(token), token, "Unsupported relative date token.")
    };

    public static bool TryParseLegacyToken(string? raw, out RelativeDateToken token) =>
        RelativeDateResolver.TryParseToken(raw, out token);

    public static bool TryParseKind(string? raw, out CrmDateRangeKind kind)
    {
        kind = default;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        var key = raw.Trim().ToLowerInvariant().Replace('-', '_').Replace(' ', '_');
        kind = key switch
        {
            "absolute" => CrmDateRangeKind.Absolute,
            "last_n_days" or "last_days" or "past_n_days" => CrmDateRangeKind.LastNDays,
            "last_n_weeks" or "last_weeks" => CrmDateRangeKind.LastNWeeks,
            "last_n_calendar_months" or "last_n_months" or "past_n_months" => CrmDateRangeKind.LastNCalendarMonths,
            "last_n_rolling_months" or "rolling_n_months" => CrmDateRangeKind.LastNRollingMonths,
            "last_n_years" or "last_years" => CrmDateRangeKind.LastNYears,
            "this_calendar_month" or "this_month" or "current_month" => CrmDateRangeKind.ThisCalendarMonth,
            "this_calendar_quarter" or "this_quarter" or "current_quarter" => CrmDateRangeKind.ThisCalendarQuarter,
            "this_calendar_year" or "this_year" or "year_to_date" or "ytd" or "current_year" =>
                CrmDateRangeKind.ThisCalendarYear,
            "this_week" or "current_week" => CrmDateRangeKind.ThisWeek,
            "previous_calendar_month" or "last_calendar_month" or "last_month" or "previous_month" =>
                CrmDateRangeKind.PreviousCalendarMonth,
            "previous_calendar_quarter" or "last_quarter" or "previous_quarter" =>
                CrmDateRangeKind.PreviousCalendarQuarter,
            "previous_calendar_year" or "last_year" or "previous_year" => CrmDateRangeKind.PreviousCalendarYear,
            "calendar_year" or "year" => CrmDateRangeKind.CalendarYear,
            "calendar_quarter" or "quarter" => CrmDateRangeKind.CalendarQuarter,
            "calendar_month_range" or "month_range" => CrmDateRangeKind.CalendarMonthRange,
            _ => default
        };

        return key is "absolute" or "last_n_days" or "last_days" or "past_n_days"
            or "last_n_weeks" or "last_weeks"
            or "last_n_calendar_months" or "last_n_months" or "past_n_months"
            or "last_n_rolling_months" or "rolling_n_months"
            or "last_n_years" or "last_years"
            or "this_calendar_month" or "this_month" or "current_month"
            or "this_calendar_quarter" or "this_quarter" or "current_quarter"
            or "this_calendar_year" or "this_year" or "year_to_date" or "ytd" or "current_year"
            or "this_week" or "current_week"
            or "previous_calendar_month" or "last_calendar_month" or "last_month" or "previous_month"
            or "previous_calendar_quarter" or "last_quarter" or "previous_quarter"
            or "previous_calendar_year" or "last_year" or "previous_year"
            or "calendar_year" or "year"
            or "calendar_quarter" or "quarter"
            or "calendar_month_range" or "month_range";
    }

    public static IReadOnlyList<(int Year, int Month)> EnumerateCalendarMonths(
        DateTimeOffset startUtc,
        DateTimeOffset endUtcExclusive,
        TimeZoneInfo businessTz)
    {
        var startLocal = TimeZoneInfo.ConvertTime(startUtc, businessTz);
        var endLocal = TimeZoneInfo.ConvertTime(endUtcExclusive, businessTz);
        var cursor = new DateTime(startLocal.Year, startLocal.Month, 1);
        var endMonth = new DateTime(endLocal.Year, endLocal.Month, 1);
        // Half-open: if end is exactly a month boundary, last included month is the previous one
        // (e.g. [Jan 1, Jul 1) → Jan–Jun).
        if (endLocal.Day == 1 && endLocal.TimeOfDay == TimeSpan.Zero)
        {
            endMonth = endMonth.AddMonths(-1);
        }

        if (cursor > endMonth)
        {
            return Array.Empty<(int, int)>();
        }

        var list = new List<(int, int)>();
        while (cursor <= endMonth)
        {
            list.Add((cursor.Year, cursor.Month));
            cursor = cursor.AddMonths(1);
            if (list.Count > MaxNMonths)
            {
                break;
            }
        }

        return list;
    }

    /// <summary>Backward-compatible helper for last-6 fill tests.</summary>
    public static IReadOnlyList<(int Year, int Month)> EnumerateLast6CalendarMonths(
        DateTimeOffset utcNow,
        TimeZoneInfo businessTz) =>
        Resolve(new CrmDateRangeExpression { Kind = CrmDateRangeKind.LastNCalendarMonths, N = 6 }, utcNow, businessTz)
            .CalendarMonthBuckets ?? Array.Empty<(int, int)>();

    private static ResolvedDateRange ResolveAbsolute(CrmDateRangeExpression expression, TimeZoneInfo businessTz)
    {
        if (expression.AbsoluteStartUtc is null || expression.AbsoluteEndExclusiveUtc is null)
        {
            throw new ArgumentException("Absolute date ranges require start and endExclusive.");
        }

        var start = expression.AbsoluteStartUtc.Value;
        var end = expression.AbsoluteEndExclusiveUtc.Value;
        if (end <= start)
        {
            throw new ArgumentException("Absolute endExclusive must be after start.");
        }

        return new ResolvedDateRange
        {
            StartUtc = start,
            EndUtcExclusive = end,
            Description = $"absolute [{start:u} .. {end:u})",
            CalendarMonthBuckets = EnumerateCalendarMonths(start, end, businessTz)
        };
    }

    private static int RequireN(CrmDateRangeExpression expression, int min, int max)
    {
        if (expression.N is not int n || n < min || n > max)
        {
            throw new ArgumentException($"N must be an integer between {min} and {max} for {expression.Kind}.");
        }

        return n;
    }

    private static int RequireYear(CrmDateRangeExpression expression)
    {
        if (expression.Year is not int y || y is < 1970 or > 2100)
        {
            throw new ArgumentException("Year must be between 1970 and 2100.");
        }

        return y;
    }

    private static int RequireQuarter(CrmDateRangeExpression expression)
    {
        if (expression.Quarter is not int q || q is < 1 or > 4)
        {
            throw new ArgumentException("Quarter must be 1–4.");
        }

        return q;
    }

    private static int RequireMonth(int? month, string name)
    {
        if (month is not int m || m is < 1 or > 12)
        {
            throw new ArgumentException($"{name} must be 1–12.");
        }

        return m;
    }

    private static ResolvedDateRange RollingDays(
        DateTimeOffset utcNow,
        int dayCount,
        string unitLabel,
        int? n = null)
    {
        var end = utcNow;
        var start = utcNow.AddDays(-dayCount);
        var label = n is int nn
            ? $"last {nn} {unitLabel} (rolling)"
            : $"last {dayCount} {unitLabel} (rolling)";
        return new ResolvedDateRange
        {
            StartUtc = start,
            EndUtcExclusive = end,
            Description = label
        };
    }

    private static ResolvedDateRange RollingMonths(DateTimeOffset utcNow, int months) =>
        new()
        {
            StartUtc = utcNow.AddMonths(-months),
            EndUtcExclusive = utcNow,
            Description = $"last {months} rolling months"
        };

    private static ResolvedDateRange LastNCalendarMonths(
        DateTimeOffset localNow,
        TimeZoneInfo businessTz,
        int months)
    {
        var firstOfThisMonth = new DateTime(localNow.Year, localNow.Month, 1, 0, 0, 0, DateTimeKind.Unspecified);
        var startLocal = firstOfThisMonth.AddMonths(-(months - 1));
        var endLocal = firstOfThisMonth.AddMonths(1);
        var start = ToUtc(startLocal, businessTz);
        var end = ToUtc(endLocal, businessTz);
        return new ResolvedDateRange
        {
            StartUtc = start,
            EndUtcExclusive = end,
            Description = $"last {months} calendar months (business timezone, half-open)",
            CalendarMonthBuckets = EnumerateCalendarMonths(start, end, businessTz)
        };
    }

    private static ResolvedDateRange LastNCalendarYears(
        DateTimeOffset localNow,
        TimeZoneInfo businessTz,
        int years)
    {
        var startYear = localNow.Year - (years - 1);
        var startLocal = new DateTime(startYear, 1, 1, 0, 0, 0, DateTimeKind.Unspecified);
        var endLocal = new DateTime(localNow.Year + 1, 1, 1, 0, 0, 0, DateTimeKind.Unspecified);
        return new ResolvedDateRange
        {
            StartUtc = ToUtc(startLocal, businessTz),
            EndUtcExclusive = ToUtc(endLocal, businessTz),
            Description = $"last {years} calendar years (through end of current year)"
        };
    }

    private static ResolvedDateRange ThisCalendarYearYtd(
        DateTimeOffset localNow,
        DateTimeOffset utcNow,
        TimeZoneInfo businessTz)
    {
        var startLocal = new DateTime(localNow.Year, 1, 1, 0, 0, 0, DateTimeKind.Unspecified);
        return new ResolvedDateRange
        {
            StartUtc = ToUtc(startLocal, businessTz),
            EndUtcExclusive = utcNow,
            Description = "this calendar year (year-to-date)",
            CalendarMonthBuckets = EnumerateCalendarMonths(ToUtc(startLocal, businessTz), utcNow, businessTz)
        };
    }

    private static ResolvedDateRange ThisCalendarQuarter(DateTimeOffset localNow, TimeZoneInfo businessTz)
    {
        var q = ((localNow.Month - 1) / 3) + 1;
        return CalendarQuarterRange(localNow.Year, q, businessTz, "this calendar quarter");
    }

    private static ResolvedDateRange PreviousCalendarMonth(DateTimeOffset localNow, TimeZoneInfo businessTz)
    {
        var firstOfThisMonth = new DateTime(localNow.Year, localNow.Month, 1, 0, 0, 0, DateTimeKind.Unspecified);
        var firstOfLastMonth = firstOfThisMonth.AddMonths(-1);
        var start = ToUtc(firstOfLastMonth, businessTz);
        var end = ToUtc(firstOfThisMonth, businessTz);
        return WithBuckets(new ResolvedDateRange
        {
            StartUtc = start,
            EndUtcExclusive = end,
            Description = "previous calendar month",
            CalendarMonthBuckets = EnumerateCalendarMonths(start, end, businessTz)
        });
    }

    private static ResolvedDateRange PreviousCalendarQuarter(DateTimeOffset localNow, TimeZoneInfo businessTz)
    {
        var q = ((localNow.Month - 1) / 3) + 1;
        var year = localNow.Year;
        q--;
        if (q < 1)
        {
            q = 4;
            year--;
        }

        return CalendarQuarterRange(year, q, businessTz, "previous calendar quarter");
    }

    private static ResolvedDateRange PreviousCalendarYear(DateTimeOffset localNow, TimeZoneInfo businessTz) =>
        FullCalendarYear(localNow.Year - 1, businessTz, "previous calendar year");

    private static ResolvedDateRange FullCalendarYear(int year, TimeZoneInfo businessTz, string? description = null)
    {
        var startLocal = new DateTime(year, 1, 1, 0, 0, 0, DateTimeKind.Unspecified);
        var endLocal = new DateTime(year + 1, 1, 1, 0, 0, 0, DateTimeKind.Unspecified);
        var start = ToUtc(startLocal, businessTz);
        var end = ToUtc(endLocal, businessTz);
        return new ResolvedDateRange
        {
            StartUtc = start,
            EndUtcExclusive = end,
            Description = description ?? $"calendar year {year}",
            CalendarMonthBuckets = EnumerateCalendarMonths(start, end, businessTz)
        };
    }

    private static ResolvedDateRange CalendarQuarterRange(
        int year,
        int quarter,
        TimeZoneInfo businessTz,
        string? description = null)
    {
        var startMonth = ((quarter - 1) * 3) + 1;
        var startLocal = new DateTime(year, startMonth, 1, 0, 0, 0, DateTimeKind.Unspecified);
        var endLocal = startLocal.AddMonths(3);
        var start = ToUtc(startLocal, businessTz);
        var end = ToUtc(endLocal, businessTz);
        return new ResolvedDateRange
        {
            StartUtc = start,
            EndUtcExclusive = end,
            Description = description ?? $"Q{quarter} {year}",
            CalendarMonthBuckets = EnumerateCalendarMonths(start, end, businessTz)
        };
    }

    private static ResolvedDateRange CalendarMonthRange(
        int year,
        int monthStart,
        int monthEnd,
        TimeZoneInfo businessTz)
    {
        if (monthEnd < monthStart)
        {
            throw new ArgumentException("monthEnd must be >= monthStart.");
        }

        var startLocal = new DateTime(year, monthStart, 1, 0, 0, 0, DateTimeKind.Unspecified);
        var endLocal = new DateTime(year, monthEnd, 1, 0, 0, 0, DateTimeKind.Unspecified).AddMonths(1);
        var start = ToUtc(startLocal, businessTz);
        var end = ToUtc(endLocal, businessTz);
        return new ResolvedDateRange
        {
            StartUtc = start,
            EndUtcExclusive = end,
            Description =
                $"calendar months {year}-{monthStart:D2} through {year}-{monthEnd:D2} (half-open)",
            CalendarMonthBuckets = EnumerateCalendarMonths(start, end, businessTz)
        };
    }

    private static ResolvedDateRange CalendarMonth(int year, int month, TimeZoneInfo businessTz, string description)
    {
        var startLocal = new DateTime(year, month, 1, 0, 0, 0, DateTimeKind.Unspecified);
        var endLocal = startLocal.AddMonths(1);
        var start = ToUtc(startLocal, businessTz);
        var end = ToUtc(endLocal, businessTz);
        return new ResolvedDateRange
        {
            StartUtc = start,
            EndUtcExclusive = end,
            Description = description,
            CalendarMonthBuckets = EnumerateCalendarMonths(start, end, businessTz)
        };
    }

    private static ResolvedDateRange ThisWeek(DateTimeOffset localNow, TimeZoneInfo businessTz)
    {
        var localDate = localNow.Date;
        var diff = ((int)localDate.DayOfWeek - (int)DayOfWeek.Monday + 7) % 7;
        var monday = localDate.AddDays(-diff);
        var nextMonday = monday.AddDays(7);
        return new ResolvedDateRange
        {
            StartUtc = ToUtc(monday, businessTz),
            EndUtcExclusive = ToUtc(nextMonday, businessTz),
            Description = "this week (Mon–Sun, business timezone)"
        };
    }

    private static ResolvedDateRange WithBuckets(ResolvedDateRange range) => range;

    private static DateTimeOffset ToUtc(DateTime localUnspecified, TimeZoneInfo businessTz)
    {
        var unspecified = DateTime.SpecifyKind(localUnspecified, DateTimeKind.Unspecified);
        return new DateTimeOffset(unspecified, businessTz.GetUtcOffset(unspecified)).ToUniversalTime();
    }
}
