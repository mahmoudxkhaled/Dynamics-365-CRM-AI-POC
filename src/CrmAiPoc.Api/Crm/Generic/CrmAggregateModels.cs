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

/// <summary>Post-aggregate derived metric computed from complete grouped results.</summary>
public enum CrmCalculatedMetricKind
{
    /// <summary>
    /// (row[sourceAlias] / sum(all rows[sourceAlias])) * 100.
    /// Only valid for Sum or Count aggregate aliases when the full group set is available.
    /// </summary>
    PercentOfTotal = 0
}

public sealed class CrmCalculatedMetricSpec
{
    public required CrmCalculatedMetricKind Kind { get; init; }
    /// <summary>Aggregate alias used as numerator / total basis.</summary>
    public required string SourceAlias { get; init; }
    /// <summary>Output column alias written onto each aggregate row.</summary>
    public required string ResultAlias { get; init; }
}
