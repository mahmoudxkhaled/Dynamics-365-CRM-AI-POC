namespace CrmAiPoc.Api.Crm.Query;

public enum SalesOrderQueryOperation
{
    List = 0,
    Count = 1
}

public enum SalesOrderFilterOperator
{
    Eq,
    Ne,
    Gt,
    Ge,
    Lt,
    Le,
    Contains
}

public enum SalesOrderSortDirection
{
    Asc,
    Desc
}

public enum SalesOrderFieldType
{
    Guid,
    String,
    DateTime,
    Money,
    Status,
    State,
    LookupGuid
}

/// <summary>
/// Relative date tokens resolved on the backend (never by the LLM clock).
/// Calendar tokens use Africa/Cairo business days; rolling tokens use UTC-anchored windows from "now".
/// </summary>
public enum RelativeDateToken
{
    LastCalendarMonth,
    ThisCalendarMonth,
    ThisWeek,
    Last7Days,
    Last30Days
}
