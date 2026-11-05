using System.ComponentModel;
using System.Text.Json.Serialization;

namespace CrmAiPoc.Api.Ai;

/// <summary>
/// Structured date-range argument for query_crm. Prefer this typed object over dateRangeJson strings.
/// Backend resolves relative kinds; the AI supplies only kind + parameters.
/// </summary>
public sealed class QueryCrmDateRangeArg
{
    [Description(
        "Required. One of: absolute, last_n_days, last_n_weeks, last_n_calendar_months, " +
        "last_n_rolling_months, last_n_years, this_calendar_month, this_calendar_quarter, " +
        "this_calendar_year, this_week, previous_calendar_month, previous_calendar_quarter, " +
        "previous_calendar_year, calendar_year, calendar_quarter, calendar_month_range.")]
    [JsonPropertyName("kind")]
    public string? Kind { get; set; }

    [Description("For last_n_* kinds: positive integer N.")]
    [JsonPropertyName("n")]
    public int? N { get; set; }

    [Description("For calendar_year / calendar_quarter / calendar_month_range.")]
    [JsonPropertyName("year")]
    public int? Year { get; set; }

    [Description("For calendar_quarter: 1–4.")]
    [JsonPropertyName("quarter")]
    public int? Quarter { get; set; }

    [Description("For calendar_month_range: first month 1–12.")]
    [JsonPropertyName("monthStart")]
    public int? MonthStart { get; set; }

    [Description("For calendar_month_range: last month 1–12.")]
    [JsonPropertyName("monthEnd")]
    public int? MonthEnd { get; set; }

    [Description("For absolute: inclusive range start (ISO-8601).")]
    [JsonPropertyName("start")]
    public string? Start { get; set; }

    [Description("For absolute: exclusive range end (ISO-8601).")]
    [JsonPropertyName("endExclusive")]
    public string? EndExclusive { get; set; }
}
