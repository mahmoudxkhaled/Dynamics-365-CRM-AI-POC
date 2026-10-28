namespace CrmAiPoc.Api.Crm.Query;

/// <summary>
/// Converts relative date tokens into half-open UTC ranges [StartUtc, EndUtcExclusive).
/// Calendar tokens use the business timezone; rolling windows use the provided UTC clock.
/// </summary>
public static class RelativeDateResolver
{
    public static (DateTimeOffset StartUtc, DateTimeOffset EndUtcExclusive, string Description) Resolve(
        RelativeDateToken token,
        DateTimeOffset utcNow,
        TimeZoneInfo businessTz)
    {
        var localNow = TimeZoneInfo.ConvertTime(utcNow, businessTz);

        return token switch
        {
            RelativeDateToken.Last7Days => RollingDays(utcNow, 7, "last 7 days (rolling)"),
            RelativeDateToken.Last30Days => RollingDays(utcNow, 30, "last 30 days (rolling)"),
            RelativeDateToken.ThisCalendarMonth => CalendarMonth(localNow.Year, localNow.Month, businessTz, "this calendar month"),
            RelativeDateToken.LastCalendarMonth => LastCalendarMonth(localNow, businessTz),
            RelativeDateToken.ThisWeek => ThisWeek(localNow, businessTz),
            _ => throw new ArgumentOutOfRangeException(nameof(token), token, "Unsupported relative date token.")
        };
    }

    public static bool TryParseToken(string? raw, out RelativeDateToken token)
    {
        token = default;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        var key = raw.Trim().ToLowerInvariant().Replace('-', '_').Replace(' ', '_');
        return key switch
        {
            "last_calendar_month" or "last_month" or "previous_month" or "الشهر_اللي_فات" =>
                Assign(RelativeDateToken.LastCalendarMonth, out token),
            "this_calendar_month" or "this_month" or "current_month" =>
                Assign(RelativeDateToken.ThisCalendarMonth, out token),
            "this_week" or "current_week" =>
                Assign(RelativeDateToken.ThisWeek, out token),
            "last_7_days" or "last7days" or "past_7_days" =>
                Assign(RelativeDateToken.Last7Days, out token),
            "last_30_days" or "last30days" or "past_30_days" or "rolling_30_days" =>
                Assign(RelativeDateToken.Last30Days, out token),
            _ => false
        };
    }

    private static bool Assign(RelativeDateToken value, out RelativeDateToken token)
    {
        token = value;
        return true;
    }

    private static (DateTimeOffset, DateTimeOffset, string) RollingDays(
        DateTimeOffset utcNow,
        int days,
        string description)
    {
        var end = utcNow;
        var start = utcNow.AddDays(-days);
        return (start, end, description);
    }

    private static (DateTimeOffset, DateTimeOffset, string) LastCalendarMonth(
        DateTimeOffset localNow,
        TimeZoneInfo businessTz)
    {
        var firstOfThisMonth = new DateTime(localNow.Year, localNow.Month, 1, 0, 0, 0, DateTimeKind.Unspecified);
        var firstOfLastMonth = firstOfThisMonth.AddMonths(-1);
        return (
            ToUtc(firstOfLastMonth, businessTz),
            ToUtc(firstOfThisMonth, businessTz),
            "last calendar month");
    }

    private static (DateTimeOffset, DateTimeOffset, string) CalendarMonth(
        int year,
        int month,
        TimeZoneInfo businessTz,
        string description)
    {
        var startLocal = new DateTime(year, month, 1, 0, 0, 0, DateTimeKind.Unspecified);
        var endLocal = startLocal.AddMonths(1);
        return (ToUtc(startLocal, businessTz), ToUtc(endLocal, businessTz), description);
    }

    private static (DateTimeOffset, DateTimeOffset, string) ThisWeek(
        DateTimeOffset localNow,
        TimeZoneInfo businessTz)
    {
        // ISO-style week: Monday start in the business timezone.
        var localDate = localNow.Date;
        var diff = ((int)localDate.DayOfWeek - (int)DayOfWeek.Monday + 7) % 7;
        var monday = localDate.AddDays(-diff);
        var nextMonday = monday.AddDays(7);
        return (
            ToUtc(monday, businessTz),
            ToUtc(nextMonday, businessTz),
            "this week (Mon–Sun, business timezone)");
    }

    private static DateTimeOffset ToUtc(DateTime localUnspecified, TimeZoneInfo businessTz)
    {
        var unspecified = DateTime.SpecifyKind(localUnspecified, DateTimeKind.Unspecified);
        return new DateTimeOffset(unspecified, businessTz.GetUtcOffset(unspecified)).ToUniversalTime();
    }
}
