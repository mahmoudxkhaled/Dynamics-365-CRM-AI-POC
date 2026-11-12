namespace CrmAiPoc.Api.Configuration;

/// <summary>Bounds for AggregateQueryRecordLimit (0x8004E023) partition recovery.</summary>
public sealed class AggregatePartitionOptions
{
    public const string SectionName = "AggregatePartition";

    /// <summary>Maximum leaf partition executions for a single recovery.</summary>
    public int MaxPartitions { get; set; } = 120;

    /// <summary>Max recursion depth (1=year, 2=month, 3=day).</summary>
    public int MaxDepth { get; set; } = 3;

    /// <summary>Wall-clock timeout for the full recovery attempt (seconds).</summary>
    public int TimeoutSeconds { get; set; } = 120;

    /// <summary>
    /// When the detected date span covers more than this many calendar years,
    /// recovery probes multi-year chunks instead of one request per year.
    /// </summary>
    public int MaxSingleYearPartitions { get; set; } = 12;

    /// <summary>Years combined into each coarse partition when MaxSingleYearPartitions is exceeded.</summary>
    public int YearChunkSize { get; set; } = 3;

    /// <summary>Bounded parallel CRM partition requests (1 = sequential).</summary>
    public int MaxDegreeOfParallelism { get; set; } = 3;
}
