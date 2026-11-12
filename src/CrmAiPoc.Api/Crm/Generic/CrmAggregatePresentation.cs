namespace CrmAiPoc.Api.Crm.Generic;

/// <summary>
/// Presentation metadata for aggregate results, captured before Top-N truncation.
/// Distinguishes overall totals from displayed Top-N subtotals. Never invents denominators.
/// </summary>
public sealed class CrmAggregatePresentation
{
    public int TotalGroupCount { get; init; }
    public int DisplayedGroupCount { get; init; }
    public bool IsTopN { get; init; }
    public int? TopNLimit { get; init; }

    /// <summary>Primary aggregate alias used for totals (Count or Sum preferred).</summary>
    public string? PrimaryMetricAlias { get; init; }

    /// <summary>count | sum | avg | min | max</summary>
    public string? PrimaryMetricKind { get; init; }

    /// <summary>
    /// Sum of the primary metric across the complete group set before Top-N.
    /// Null when no numeric primary metric exists.
    /// </summary>
    public decimal? OverallMetricTotal { get; init; }

    /// <summary>Sum of the primary metric across displayed (possibly Top-N) rows.</summary>
    public decimal? DisplayedMetricSubtotal { get; init; }

    /// <summary>
    /// Displayed subtotal as percent of overall total when overall is known and non-zero.
    /// </summary>
    public decimal? DisplayedShareOfOverallPercent { get; init; }

    /// <summary>Human-readable label for the leading displayed group (after sort/Top-N).</summary>
    public string? LeadingGroupLabel { get; init; }

    public decimal? LeadingGroupMetric { get; init; }
    public decimal? LeadingGroupSharePercent { get; init; }

    /// <summary>months | years | groups — derived from groupBy dateGrouping, not entity names.</summary>
    public string GroupNoun { get; init; } = "groups";

    /// <summary>True when OverallMetricTotal was computed from the complete pre-Top-N set.</summary>
    public bool OverallTotalKnown { get; init; }
}
