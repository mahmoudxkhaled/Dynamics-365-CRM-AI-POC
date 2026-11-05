namespace CrmAiPoc.Api.Crm.Generic;

public enum CrmAggregateFunction
{
    Count = 0,
    Sum = 1,
    Avg = 2,
    Min = 3,
    Max = 4
}

/// <summary>
/// CRM FetchXML dateGrouping values for aggregate group-by on DateTime attributes.
/// </summary>
public enum CrmDateGrouping
{
    None = 0,
    Year = 1,
    Month = 2,
    Day = 3,
    Week = 4,
    Quarter = 5
}

public sealed class CrmAggregateSpec
{
    public required CrmAggregateFunction Function { get; init; }
    /// <summary>Logical attribute name (primary id for count; numeric/money for sum/avg/min/max).</summary>
    public required string Field { get; init; }
    public required string Alias { get; init; }
}

public sealed class CrmGroupBySpec
{
    public required string Field { get; init; }
    public CrmDateGrouping DateGrouping { get; init; } = CrmDateGrouping.None;
    public required string Alias { get; init; }
}
