namespace CrmAiPoc.Api.Crm.Query;

/// <summary>
/// Non-executable date range intent. Backend resolves to half-open UTC [Start, EndExclusive).
/// </summary>
public sealed class CrmDateRangeExpression
{
    public required CrmDateRangeKind Kind { get; init; }
    public int? N { get; init; }
    public int? Year { get; init; }
    public int? Quarter { get; init; }
    public int? MonthStart { get; init; }
    public int? MonthEnd { get; init; }
    public DateTimeOffset? AbsoluteStartUtc { get; init; }
    public DateTimeOffset? AbsoluteEndExclusiveUtc { get; init; }
}

/// <summary>Resolved half-open range plus optional calendar-month buckets for aggregate fill.</summary>
public sealed class ResolvedDateRange
{
    public required DateTimeOffset StartUtc { get; init; }
    public required DateTimeOffset EndUtcExclusive { get; init; }
    public required string Description { get; init; }
    public IReadOnlyList<(int Year, int Month)>? CalendarMonthBuckets { get; init; }
}
