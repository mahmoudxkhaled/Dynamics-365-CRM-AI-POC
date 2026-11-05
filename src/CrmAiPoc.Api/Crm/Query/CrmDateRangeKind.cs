namespace CrmAiPoc.Api.Crm.Query;

/// <summary>
/// Structured date-range kinds for query_crm. Resolved on the backend (never by the LLM clock).
/// </summary>
public enum CrmDateRangeKind
{
    Absolute = 0,
    LastNDays = 1,
    LastNWeeks = 2,
    LastNCalendarMonths = 3,
    LastNRollingMonths = 4,
    LastNYears = 5,
    ThisCalendarMonth = 6,
    ThisCalendarQuarter = 7,
    /// <summary>Current calendar year from Jan 1 through now (year-to-date), half-open end = now.</summary>
    ThisCalendarYear = 8,
    ThisWeek = 9,
    PreviousCalendarMonth = 10,
    PreviousCalendarQuarter = 11,
    PreviousCalendarYear = 12,
    /// <summary>Full calendar year [Year-01-01, Year+1-01-01).</summary>
    CalendarYear = 13,
    /// <summary>Calendar quarter within a year (Q1=Jan–Mar, …).</summary>
    CalendarQuarter = 14,
    /// <summary>Inclusive month range within a year, e.g. Jan–Jun → [Y-01-01, Y-07-01).</summary>
    CalendarMonthRange = 15
}
