namespace CrmAiPoc.Api.Crm.Query;

/// <summary>
/// Relative date tokens resolved on the backend (never by the LLM clock).
/// Calendar tokens use the configured business timezone; rolling tokens use UTC-anchored windows from "now".
/// </summary>
public enum RelativeDateToken
{
    LastCalendarMonth,
    ThisCalendarMonth,
    ThisWeek,
    Last7Days,
    Last30Days
}
