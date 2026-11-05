namespace CrmAiPoc.Api.Crm.Query;

/// <summary>
/// Legacy facade over <see cref="DateRangeResolver"/> for fixed relative-date tokens.
/// Prefer structured dateRangeJson / <see cref="CrmDateRangeExpression"/> for new callers.
/// </summary>
public static class RelativeDateResolver
{
    public static (DateTimeOffset StartUtc, DateTimeOffset EndUtcExclusive, string Description) Resolve(
        RelativeDateToken token,
        DateTimeOffset utcNow,
        TimeZoneInfo businessTz) =>
        DateRangeResolver.Resolve(token, utcNow, businessTz);

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
            "last_calendar_month" or "last_month" or "previous_month" =>
                Assign(RelativeDateToken.LastCalendarMonth, out token),
            "this_calendar_month" or "this_month" or "current_month" =>
                Assign(RelativeDateToken.ThisCalendarMonth, out token),
            "this_week" or "current_week" =>
                Assign(RelativeDateToken.ThisWeek, out token),
            "last_7_days" or "last7days" or "past_7_days" =>
                Assign(RelativeDateToken.Last7Days, out token),
            "last_30_days" or "last30days" or "past_30_days" or "rolling_30_days" =>
                Assign(RelativeDateToken.Last30Days, out token),
            "last_6_calendar_months" or "last_6_months" or "past_6_months" or "last6months" =>
                Assign(RelativeDateToken.Last6CalendarMonths, out token),
            _ => false
        };
    }

    public static IReadOnlyList<(int Year, int Month)> EnumerateLast6CalendarMonths(
        DateTimeOffset utcNow,
        TimeZoneInfo businessTz) =>
        DateRangeResolver.EnumerateLast6CalendarMonths(utcNow, businessTz);

    private static bool Assign(RelativeDateToken value, out RelativeDateToken token)
    {
        token = value;
        return true;
    }
}
