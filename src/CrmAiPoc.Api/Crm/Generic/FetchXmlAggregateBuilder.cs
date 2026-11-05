using System.Globalization;
using System.Xml.Linq;
using CrmAiPoc.Api.Configuration;
using CrmAiPoc.Api.Crm.Metadata;
using CrmAiPoc.Api.Crm.Query;
using Microsoft.Extensions.Options;

namespace CrmAiPoc.Api.Crm.Generic;

/// <summary>
/// Builds validated Dynamics FetchXML aggregate queries from a structured <see cref="CrmQueryPlan"/>.
/// Never accepts raw FetchXML from the model — only typed plan fields.
/// </summary>
public sealed class FetchXmlAggregateBuilder
{
    private readonly TimeZoneInfo _businessTz;
    private readonly Func<DateTimeOffset> _utcNow;

    public FetchXmlAggregateBuilder(IOptions<CrmOptions>? crmOptions = null, Func<DateTimeOffset>? utcNow = null)
    {
        _businessTz = BusinessTimeZone.Resolve(crmOptions?.Value.BusinessTimeZoneId);
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    }

    public FetchXmlAggregateBuildResult Build(
        CrmQueryPlan plan,
        CrmEntityMetadata entity,
        CrmEntityMetadata? relatedFrom = null,
        AggregateDatePartitioner.Partition? createdOnPartition = null)
    {
        if (plan.Operation != CrmQueryOperation.Aggregate)
        {
            return FetchXmlAggregateBuildResult.Fail("FetchXML aggregate builder requires operation=aggregate.");
        }

        if (!entity.IsApprovedForAi)
        {
            return FetchXmlAggregateBuildResult.Fail($"Entity '{entity.LogicalName}' is not approved for AI queries.");
        }

        if (plan.Aggregates.Count == 0)
        {
            return FetchXmlAggregateBuildResult.Fail("aggregates are required for operation=aggregate.");
        }

        if (plan.Aggregates.Count > CrmQueryLimits.MaxAggregates)
        {
            return FetchXmlAggregateBuildResult.Fail($"A maximum of {CrmQueryLimits.MaxAggregates} aggregates is allowed.");
        }

        if (plan.GroupBy.Count > CrmQueryLimits.MaxGroupByFields)
        {
            return FetchXmlAggregateBuildResult.Fail($"A maximum of {CrmQueryLimits.MaxGroupByFields} groupBy fields is allowed.");
        }

        if (plan.Filters.Count > CrmQueryLimits.MaxFilters)
        {
            return FetchXmlAggregateBuildResult.Fail($"A maximum of {CrmQueryLimits.MaxFilters} filters is allowed.");
        }

        if (!string.IsNullOrWhiteSpace(plan.RelatedFromEntity) || !string.IsNullOrWhiteSpace(plan.RelatedFromId))
        {
            return FetchXmlAggregateBuildResult.Fail(
                "relatedFromEntity/relatedFromId is not supported with aggregate queries in this PoC.");
        }

        if (plan.Links.Count > 0)
        {
            return FetchXmlAggregateBuildResult.Fail(
                "Aggregates with link-entity joins are not supported: one-to-many joins can inflate COUNT/SUM. " +
                "Run the aggregate on the root entity without links, or filter with relatedFromId one-hop when applicable.");
        }

        var summaries = new List<string>();
        var filterConditions = new List<XElement>();
        var moneySumWithoutCurrency = false;

        foreach (var agg in plan.Aggregates)
        {
            var validated = ValidateAggregate(entity, agg);
            if (!validated.Success)
            {
                return FetchXmlAggregateBuildResult.Fail(validated.Error!);
            }

            if (validated.IsMoneySum)
            {
                moneySumWithoutCurrency = true;
            }
        }

        foreach (var group in plan.GroupBy)
        {
            var validated = ValidateGroupBy(entity, group);
            if (!validated.Success)
            {
                return FetchXmlAggregateBuildResult.Fail(validated.Error!);
            }
        }

        if (moneySumWithoutCurrency &&
            !plan.GroupBy.Any(g => g.Field.Equals("transactioncurrencyid", StringComparison.OrdinalIgnoreCase)))
        {
            summaries.Add(
                "Money SUM uses stored totalamount values (org/transaction currency as stored in CRM); " +
                "amounts are not converted across currencies.");
        }

        if (plan.RelativeDate is not null)
        {
            var relative = FetchXmlConditionBuilder.BuildRelativeDate(
                entity, plan.RelativeDate, _utcNow(), _businessTz);
            if (!relative.Success)
            {
                return FetchXmlAggregateBuildResult.Fail(relative.Error!);
            }

            filterConditions.AddRange(relative.Conditions!);
            summaries.Add(relative.Summary!);
        }

        foreach (var filter in plan.Filters)
        {
            var built = FetchXmlConditionBuilder.BuildFilter(entity, filter);
            if (!built.Success)
            {
                return FetchXmlAggregateBuildResult.Fail(built.Error!);
            }

            filterConditions.Add(built.Condition!);
            summaries.Add(built.Summary!);
        }

        // Internal partition bounds (AggregateQueryRecordLimit recovery) — not counted against MaxFilters.
        if (createdOnPartition is { } partition)
        {
            if (!entity.TryGetAttribute("createdon", out var createdOn) ||
                createdOn.Kind != CrmAttributeKind.DateTime)
            {
                return FetchXmlAggregateBuildResult.Fail(
                    "Partitioned aggregation requires a readable createdon DateTime attribute.");
            }

            filterConditions.Add(new XElement(
                "condition",
                new XAttribute("attribute", createdOn.LogicalName),
                new XAttribute("operator", "ge"),
                new XAttribute("value", FetchXmlConditionBuilder.FormatFetchDate(partition.StartInclusiveUtc))));
            filterConditions.Add(new XElement(
                "condition",
                new XAttribute("attribute", createdOn.LogicalName),
                new XAttribute("operator", "lt"),
                new XAttribute("value", FetchXmlConditionBuilder.FormatFetchDate(partition.EndExclusiveUtc))));
            summaries.Add(
                $"createdon partition {partition.Label} " +
                $"[{partition.StartInclusiveUtc:u} .. {partition.EndExclusiveUtc:u})");
        }

        var entityEl = new XElement("entity", new XAttribute("name", entity.LogicalName));
        foreach (var agg in plan.Aggregates)
        {
            var fetchFn = MapAggregateFunction(agg.Function);
            entityEl.Add(new XElement(
                "attribute",
                new XAttribute("name", ResolveLogicalName(entity, agg.Field)),
                new XAttribute("alias", SanitizeAlias(agg.Alias)),
                new XAttribute("aggregate", fetchFn)));
        }

        foreach (var group in plan.GroupBy)
        {
            var attrs = new List<object>
            {
                new XAttribute("name", ResolveLogicalName(entity, group.Field)),
                new XAttribute("groupby", "true"),
                new XAttribute("alias", SanitizeAlias(group.Alias))
            };
            if (group.DateGrouping != CrmDateGrouping.None)
            {
                attrs.Add(new XAttribute("dategrouping", MapDateGrouping(group.DateGrouping)));
            }

            entityEl.Add(new XElement("attribute", attrs.ToArray()));
        }

        if (filterConditions.Count > 0)
        {
            entityEl.Add(new XElement("filter", new XAttribute("type", "and"), filterConditions));
        }

        var fetch = new XElement(
            "fetch",
            new XAttribute("mapping", "logical"),
            new XAttribute("aggregate", "true"),
            new XAttribute("distinct", "false"),
            entityEl);

        var fetchXml = fetch.ToString(SaveOptions.DisableFormatting);
        var encoded = Uri.EscapeDataString(fetchXml);
        var uri = $"{entity.EntitySetName}?fetchXml={encoded}";

        IReadOnlyList<(int Year, int Month)>? monthBuckets = null;
        var fillMonths = false;
        if (plan.RelativeDate is not null &&
            plan.GroupBy.Any(g => g.DateGrouping == CrmDateGrouping.Month) &&
            plan.GroupBy.Any(g => g.DateGrouping == CrmDateGrouping.Year))
        {
            try
            {
                var resolved = DateRangeResolver.Resolve(plan.RelativeDate.GetExpression(), _utcNow(), _businessTz);
                if (resolved.CalendarMonthBuckets is { Count: > 0 })
                {
                    fillMonths = true;
                    monthBuckets = resolved.CalendarMonthBuckets;
                }
            }
            catch (ArgumentException)
            {
                // Relative date already validated when conditions were built.
            }
        }

        return new FetchXmlAggregateBuildResult
        {
            Success = true,
            FetchXml = fetchXml,
            RelativeRequestUri = uri,
            AppliedFilterSummary = summaries,
            FillMissingMonthBuckets = fillMonths,
            CalendarMonthBuckets = monthBuckets,
            BusinessTimeZone = _businessTz,
            UtcNow = _utcNow(),
            Columns = BuildAggregateColumns(entity, plan),
            ResultFieldKeys = plan.GroupBy.Select(g => SanitizeAlias(g.Alias))
                .Concat(plan.Aggregates.Select(a => SanitizeAlias(a.Alias)))
                .ToArray()
        };
    }

    private static (bool Success, string? Error, bool IsMoneySum) ValidateAggregate(
        CrmEntityMetadata entity,
        CrmAggregateSpec agg)
    {
        if (string.IsNullOrWhiteSpace(agg.Field) || string.IsNullOrWhiteSpace(agg.Alias))
        {
            return (false, "Each aggregate requires field and alias.", false);
        }

        if (!IsSafeAlias(agg.Alias))
        {
            return (false, $"Invalid aggregate alias '{agg.Alias}'.", false);
        }

        if (!entity.TryGetAttribute(agg.Field, out var attr) || !attr.IsValidForRead)
        {
            return (false, $"Aggregate field '{agg.Field}' is not approved/readable on '{entity.LogicalName}'.", false);
        }

        switch (agg.Function)
        {
            case CrmAggregateFunction.Count:
                // Prefer primary id; allow any readable attribute for countcolumn.
                return (true, null, false);
            case CrmAggregateFunction.Sum:
            case CrmAggregateFunction.Avg:
            case CrmAggregateFunction.Min:
            case CrmAggregateFunction.Max:
                if (attr.Kind is not (CrmAttributeKind.Money or CrmAttributeKind.Decimal or CrmAttributeKind.Integer))
                {
                    return (false,
                        $"Aggregate {agg.Function} requires a numeric/money field; '{agg.Field}' is {attr.Kind}.",
                        false);
                }

                return (true, null, attr.Kind == CrmAttributeKind.Money && agg.Function == CrmAggregateFunction.Sum);
            default:
                return (false, $"Unsupported aggregate function '{agg.Function}'.", false);
        }
    }

    private static (bool Success, string? Error) ValidateGroupBy(CrmEntityMetadata entity, CrmGroupBySpec group)
    {
        if (string.IsNullOrWhiteSpace(group.Field) || string.IsNullOrWhiteSpace(group.Alias))
        {
            return (false, "Each groupBy requires field and alias.");
        }

        if (!IsSafeAlias(group.Alias))
        {
            return (false, $"Invalid groupBy alias '{group.Alias}'.");
        }

        if (!entity.TryGetAttribute(group.Field, out var attr) || !attr.IsValidForRead)
        {
            return (false, $"groupBy field '{group.Field}' is not approved/readable on '{entity.LogicalName}'.");
        }

        if (group.DateGrouping != CrmDateGrouping.None)
        {
            if (attr.Kind != CrmAttributeKind.DateTime)
            {
                return (false, $"dateGrouping requires a DateTime field; '{group.Field}' is {attr.Kind}.");
            }

            return (true, null);
        }

        if (attr.Kind is CrmAttributeKind.Memo or CrmAttributeKind.Unknown)
        {
            return (false, $"groupBy is not supported on '{group.Field}' ({attr.Kind}).");
        }

        return (true, null);
    }

    private static IReadOnlyList<CrmTableColumn> BuildAggregateColumns(CrmEntityMetadata entity, CrmQueryPlan plan)
    {
        var cols = new List<CrmTableColumn>();
        foreach (var group in plan.GroupBy)
        {
            var alias = SanitizeAlias(group.Alias);
            entity.TryGetAttribute(group.Field, out var attr);
            var label = group.DateGrouping != CrmDateGrouping.None
                ? $"{attr?.LogicalName ?? group.Field} ({group.DateGrouping})"
                : attr?.LogicalName ?? group.Field;

            cols.Add(new CrmTableColumn
            {
                Key = alias,
                Label = NiceLabel(alias, label),
                Kind = group.DateGrouping != CrmDateGrouping.None
                    ? "integer"
                    : attr?.Kind.ToString().ToLowerInvariant() ?? "string",
                LogicalName = attr?.LogicalName ?? group.Field
            });

            if (attr is { Kind: CrmAttributeKind.Status or CrmAttributeKind.State or CrmAttributeKind.Picklist })
            {
                cols.Add(new CrmTableColumn
                {
                    Key = alias + "_label",
                    Label = NiceLabel(alias, label) + " label",
                    Kind = "string",
                    LogicalName = attr.LogicalName
                });
            }
        }

        foreach (var agg in plan.Aggregates)
        {
            var alias = SanitizeAlias(agg.Alias);
            entity.TryGetAttribute(agg.Field, out var attr);
            var kind = agg.Function == CrmAggregateFunction.Count
                ? "integer"
                : attr?.Kind == CrmAttributeKind.Money ? "money" : "decimal";
            cols.Add(new CrmTableColumn
            {
                Key = alias,
                Label = NiceLabel(alias, $"{agg.Function} {agg.Field}"),
                Kind = kind,
                LogicalName = attr?.LogicalName ?? agg.Field
            });
        }

        return cols;
    }

    private static string NiceLabel(string alias, string fallback)
    {
        if (alias.Equals("record_count", StringComparison.OrdinalIgnoreCase) ||
            alias.Equals("ordercount", StringComparison.OrdinalIgnoreCase))
        {
            return "Count";
        }

        if (alias.Equals("total_amount", StringComparison.OrdinalIgnoreCase) ||
            alias.Equals("totalamount_sum", StringComparison.OrdinalIgnoreCase))
        {
            return "Total amount";
        }

        if (alias.Equals("statuscode", StringComparison.OrdinalIgnoreCase))
        {
            return "Status";
        }

        if (alias.Equals("year", StringComparison.OrdinalIgnoreCase))
        {
            return "Year";
        }

        if (alias.Equals("month", StringComparison.OrdinalIgnoreCase))
        {
            return "Month";
        }

        return fallback;
    }

    private static string ResolveLogicalName(CrmEntityMetadata entity, string field) =>
        entity.TryGetAttribute(field, out var attr) ? attr.LogicalName : field.Trim();

    private static string MapAggregateFunction(CrmAggregateFunction fn) => fn switch
    {
        CrmAggregateFunction.Count => "countcolumn",
        CrmAggregateFunction.Sum => "sum",
        CrmAggregateFunction.Avg => "avg",
        CrmAggregateFunction.Min => "min",
        CrmAggregateFunction.Max => "max",
        _ => "countcolumn"
    };

    private static string MapDateGrouping(CrmDateGrouping g) => g switch
    {
        CrmDateGrouping.Year => "year",
        CrmDateGrouping.Month => "month",
        CrmDateGrouping.Day => "day",
        CrmDateGrouping.Week => "week",
        CrmDateGrouping.Quarter => "quarter",
        _ => "month"
    };

    private static bool IsSafeAlias(string alias) =>
        alias.Length is >= 1 and <= 40 &&
        alias.All(c => char.IsAsciiLetterOrDigit(c) || c == '_') &&
        char.IsAsciiLetter(alias[0]);

    private static string SanitizeAlias(string alias) =>
        IsSafeAlias(alias) ? alias : "agg_" + Math.Abs(alias.GetHashCode(StringComparison.Ordinal)).ToString(CultureInfo.InvariantCulture);
}

public sealed class FetchXmlAggregateBuildResult
{
    public bool Success { get; init; }
    public string? Error { get; init; }
    public string? FetchXml { get; init; }
    public string? RelativeRequestUri { get; init; }
    public IReadOnlyList<string> AppliedFilterSummary { get; init; } = Array.Empty<string>();
    public bool FillMissingMonthBuckets { get; init; }
    public IReadOnlyList<(int Year, int Month)>? CalendarMonthBuckets { get; init; }
    public TimeZoneInfo? BusinessTimeZone { get; init; }
    public DateTimeOffset UtcNow { get; init; }
    public IReadOnlyList<CrmTableColumn> Columns { get; init; } = Array.Empty<CrmTableColumn>();
    public IReadOnlyList<string> ResultFieldKeys { get; init; } = Array.Empty<string>();

    public static FetchXmlAggregateBuildResult Fail(string error) =>
        new() { Success = false, Error = error };
}
