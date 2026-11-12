using System.Diagnostics;
using System.Globalization;
using System.Xml.Linq;
using CrmAiPoc.Api.Configuration;
using CrmAiPoc.Api.Crm.Dynamics;
using CrmAiPoc.Api.Crm.Generic;
using CrmAiPoc.Api.Crm.Metadata;
using CrmAiPoc.Api.Crm.Query;
using Microsoft.Extensions.Options;

namespace CrmAiPoc.Api.Crm.FetchXml;

/// <summary>
/// Deterministic AggregateQueryRecordLimit (0x8004E023) recovery for LLM-authored FetchXML.
/// Partitions on a dynamically selected root DateTime attribute, preserves filters/joins/grouping,
/// merges SUM/COUNT/MIN/MAX exactly, and applies order/top only after merge.
/// </summary>
public sealed class FetchXmlAggregatePartitionRecovery
{
    private readonly DynamicsCrmHttp _http;
    private readonly TimeZoneInfo _businessTz;
    private readonly AggregatePartitionOptions _options;
    private readonly ILogger<FetchXmlAggregatePartitionRecovery> _logger;

    public FetchXmlAggregatePartitionRecovery(
        DynamicsCrmHttp http,
        IOptions<CrmOptions> crmOptions,
        IOptions<AggregatePartitionOptions>? partitionOptions,
        ILogger<FetchXmlAggregatePartitionRecovery> logger)
    {
        _http = http;
        _businessTz = BusinessTimeZone.Resolve(crmOptions.Value.BusinessTimeZoneId);
        _options = partitionOptions?.Value ?? new AggregatePartitionOptions();
        _logger = logger;
    }

    public async Task<GenericCrmQueryResult?> TryRecoverAsync(
        ValidatedFetchXmlQuery query,
        CancellationToken cancellationToken = default)
    {
        if (!query.IsAggregate)
        {
            return null;
        }

        var sw = Stopwatch.StartNew();
        if (!TryAnalyze(query.FetchXml, out var shape, out var analyzeError))
        {
            _logger.LogWarning(
                "[AGGREGATE PARTITION] Cannot recover — {Reason}",
                analyzeError);
            return LimitationResult(query, analyzeError ?? "Aggregate partition recovery is not supported for this FetchXML.");
        }

        if (shape.HasDistinct)
        {
            return LimitationResult(
                query,
                "AggregateQueryRecordLimit exceeded and DISTINCT aggregates cannot be partition-merged safely. Narrow the date range and retry.");
        }

        if (!AggregateDatePartitioner.CanMergeExactly(shape.Aggregates))
        {
            return LimitationResult(
                query,
                "AggregateQueryRecordLimit exceeded and this aggregate set cannot be partition-merged exactly " +
                "(AVG or unsupported functions require a narrower date range).");
        }

        var partitionAttr = SelectPartitionAttribute(query.Entity);
        if (partitionAttr is null)
        {
            return LimitationResult(
                query,
                "AggregateQueryRecordLimit exceeded and no readable DateTime attribute is available on the root entity for safe partitioning.");
        }

        var bounds = await ResolveBoundsAsync(query, partitionAttr, cancellationToken);
        if (bounds is null)
        {
            _logger.LogInformation(
                "[AGGREGATE PARTITION] No rows in scope for Entity={Entity}; returning empty aggregate.",
                query.EntityLogicalName);
            return SuccessResult(query, Array.Empty<IReadOnlyDictionary<string, object?>>(), [
                $"AggregateQueryRecordLimit recovery found no rows in scope for {partitionAttr}."
            ]);
        }

        var yearSpan = AggregateDatePartitioner.BuildYearPartitions(
            bounds.Value.Min, bounds.Value.Max, _businessTz);
        if (yearSpan.Count == 0)
        {
            return LimitationResult(query, "AggregateQueryRecordLimit recovery could not build date partitions.");
        }

        var maxSingleYear = Math.Clamp(_options.MaxSingleYearPartitions, 1, _options.MaxPartitions);
        var chunkSize = Math.Clamp(_options.YearChunkSize, 1, 20);
        var topPartitions = yearSpan.Count > maxSingleYear
            ? AggregateDatePartitioner.BuildMultiYearChunkPartitions(
                bounds.Value.Min, bounds.Value.Max, _businessTz, chunkSize)
            : yearSpan;

        if (topPartitions.Count > _options.MaxPartitions)
        {
            return LimitationResult(
                query,
                $"AggregateQueryRecordLimit recovery would require {topPartitions.Count} partitions, exceeding the configured maximum ({_options.MaxPartitions}). Narrow the date range.");
        }

        var partitionRows = new List<IReadOnlyList<IReadOnlyDictionary<string, object?>>>();
        var summaries = new List<string>
        {
            $"Recovered AggregateQueryRecordLimit via non-overlapping {partitionAttr} partitions; totals are exact merges of partition results."
        };

        if (topPartitions.Count != yearSpan.Count)
        {
            summaries.Add(
                $"Date span covered {yearSpan.Count} calendar years; using {topPartitions.Count} multi-year chunk(s) of up to {chunkSize} year(s) to reduce CRM load.");
        }

        var executed = 0;
        var parallelism = Math.Clamp(_options.MaxDegreeOfParallelism, 1, 8);
        if (parallelism <= 1 || topPartitions.Count <= 1)
        {
            foreach (var part in topPartitions)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (sw.Elapsed > TimeSpan.FromSeconds(_options.TimeoutSeconds))
                {
                    return LimitationResult(query, "AggregateQueryRecordLimit recovery timed out before completing all partitions.");
                }

                var partResult = await ExecutePartitionRecursiveAsync(
                    query, shape, partitionAttr, part, depth: 1, sw, summaries, cancellationToken);
                if (partResult is null)
                {
                    return LimitationResult(
                        query,
                        $"AggregateQueryRecordLimit recovery failed for partition '{part.Label}'. Narrow the date range and retry.");
                }

                partitionRows.Add(partResult);
                executed++;
                if (executed > _options.MaxPartitions)
                {
                    return LimitationResult(query, "AggregateQueryRecordLimit recovery exceeded the configured partition execution limit.");
                }
            }
        }
        else
        {
            // Bounded parallelism for independent top-level partitions; merge order preserved.
            var bag = new IReadOnlyList<IReadOnlyDictionary<string, object?>>?[topPartitions.Count];
            using var gate = new SemaphoreSlim(parallelism, parallelism);
            var tasks = new Task[topPartitions.Count];
            for (var i = 0; i < topPartitions.Count; i++)
            {
                var index = i;
                var part = topPartitions[i];
                tasks[i] = Task.Run(async () =>
                {
                    await gate.WaitAsync(cancellationToken);
                    try
                    {
                        if (sw.Elapsed > TimeSpan.FromSeconds(_options.TimeoutSeconds))
                        {
                            return;
                        }

                        var localSummaries = new List<string>();
                        var partResult = await ExecutePartitionRecursiveAsync(
                            query, shape, partitionAttr, part, depth: 1, sw, localSummaries, cancellationToken);
                        bag[index] = partResult;
                        lock (summaries)
                        {
                            summaries.AddRange(localSummaries);
                        }
                    }
                    finally
                    {
                        gate.Release();
                    }
                }, cancellationToken);
            }

            await Task.WhenAll(tasks);
            for (var i = 0; i < bag.Length; i++)
            {
                if (bag[i] is null)
                {
                    return LimitationResult(
                        query,
                        $"AggregateQueryRecordLimit recovery failed for partition '{topPartitions[i].Label}'. Narrow the date range and retry.");
                }

                partitionRows.Add(bag[i]!);
                executed++;
            }

            if (executed > _options.MaxPartitions)
            {
                return LimitationResult(query, "AggregateQueryRecordLimit recovery exceeded the configured partition execution limit.");
            }
        }

        var merged = AggregateDatePartitioner.MergePartitionRows(
            partitionRows,
            shape.Aggregates,
            shape.GroupAliases);

        merged = ApplyOrderAndTop(merged, shape);

        _logger.LogInformation(
            "[AGGREGATE PARTITION] Success Entity={Entity} PartitionAttr={Attr} CalendarYears={Years} " +
            "TopPartitions={TopPartitions} Parallelism={Parallelism} MergedRows={Rows} DurationMs={Ms}",
            query.EntityLogicalName,
            partitionAttr,
            yearSpan.Count,
            topPartitions.Count,
            parallelism,
            merged.Count,
            sw.ElapsedMilliseconds);

        return SuccessResult(query, merged, summaries);
    }

    private async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>?> ExecutePartitionRecursiveAsync(
        ValidatedFetchXmlQuery query,
        AggregateShape shape,
        string partitionAttr,
        AggregateDatePartitioner.Partition partition,
        int depth,
        Stopwatch sw,
        List<string> summaries,
        CancellationToken cancellationToken)
    {
        if (sw.Elapsed > TimeSpan.FromSeconds(_options.TimeoutSeconds))
        {
            return null;
        }

        var rows = await ExecuteSinglePartitionAsync(query, partitionAttr, partition, cancellationToken);
        if (rows is not null)
        {
            return rows;
        }

        if (depth >= _options.MaxDepth)
        {
            _logger.LogWarning(
                "[AGGREGATE PARTITION] Still over limit at max depth Label={Label} Depth={Depth}",
                partition.Label,
                depth);
            return null;
        }

        IReadOnlyList<AggregateDatePartitioner.Partition> children = depth switch
        {
            1 => AggregateDatePartitioner.BuildMonthPartitions(partition, _businessTz),
            _ => AggregateDatePartitioner.BuildDayPartitions(partition, _businessTz)
        };

        if (children.Count == 0)
        {
            return null;
        }

        summaries.Add($"{partition.Label} subdivided into {children.Count} child partition(s).");
        var childRows = new List<IReadOnlyList<IReadOnlyDictionary<string, object?>>>();
        foreach (var child in children)
        {
            var part = await ExecutePartitionRecursiveAsync(
                query, shape, partitionAttr, child, depth + 1, sw, summaries, cancellationToken);
            if (part is null)
            {
                return null;
            }

            childRows.Add(part);
        }

        return AggregateDatePartitioner.MergePartitionRows(childRows, shape.Aggregates, shape.GroupAliases);
    }

    private async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>?> ExecuteSinglePartitionAsync(
        ValidatedFetchXmlQuery query,
        string partitionAttr,
        AggregateDatePartitioner.Partition partition,
        CancellationToken cancellationToken)
    {
        if (!TryBuildPartitionedFetchXml(query.FetchXml, partitionAttr, partition, out var fetchXml, out _))
        {
            return null;
        }

        var uri = $"{query.EntitySetName}?fetchXml={Uri.EscapeDataString(fetchXml)}";
        // Do not retry annotation variants for a known AggregateQueryRecordLimit partition probe.
        var http = await _http.GetAsync(uri, includeAnnotations: false, cancellationToken);
        if (!http.IsSuccess)
        {
            var err = DynamicsCrmErrorParser.Parse(http.Body);
            if (err.IsAggregateQueryRecordLimit)
            {
                return null; // caller subdivides
            }

            _logger.LogWarning(
                "[AGGREGATE PARTITION] Non-limit failure Label={Label} Status={Status} Code={Code}",
                partition.Label,
                http.StatusCode,
                err.Code);
            return null;
        }

        return ParseRows(http.Body);
    }

    private async Task<(DateTimeOffset Min, DateTimeOffset Max)?> ResolveBoundsAsync(
        ValidatedFetchXmlQuery query,
        string partitionAttr,
        CancellationToken cancellationToken)
    {
        var min = await FetchBoundAsync(query, partitionAttr, ascending: true, cancellationToken);
        var max = await FetchBoundAsync(query, partitionAttr, ascending: false, cancellationToken);
        if (min is null || max is null)
        {
            return null;
        }

        return (min.Value, max.Value);
    }

    private async Task<DateTimeOffset?> FetchBoundAsync(
        ValidatedFetchXmlQuery query,
        string partitionAttr,
        bool ascending,
        CancellationToken cancellationToken)
    {
        if (!TryBuildBoundProbeFetchXml(query.FetchXml, partitionAttr, ascending, out var fetchXml))
        {
            return null;
        }

        var uri = $"{query.EntitySetName}?fetchXml={Uri.EscapeDataString(fetchXml)}";
        var http = await _http.GetAsync(uri, includeAnnotations: false, cancellationToken);
        if (!http.IsSuccess)
        {
            return null;
        }

        var rows = ParseRows(http.Body);
        if (rows.Count == 0 || !rows[0].TryGetValue(partitionAttr, out var raw) || raw is null)
        {
            return null;
        }

        return TryParseDate(raw);
    }

    internal static string? SelectPartitionAttribute(CrmEntityMetadata entity)
    {
        foreach (var preferred in new[] { "createdon", "modifiedon" })
        {
            if (entity.TryGetAttribute(preferred, out var attr) &&
                attr.IsValidForRead &&
                attr.Kind == CrmAttributeKind.DateTime)
            {
                return attr.LogicalName;
            }
        }

        return entity.Attributes
            .Where(a => a.IsValidForRead && a.Kind == CrmAttributeKind.DateTime)
            .Select(a => a.LogicalName)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }

    internal static bool TryAnalyze(string fetchXml, out AggregateShape shape, out string? error)
    {
        shape = default!;
        error = null;
        try
        {
            var doc = XDocument.Parse(fetchXml, LoadOptions.None);
            var root = doc.Root;
            if (root is null || !root.Name.LocalName.Equals("fetch", StringComparison.OrdinalIgnoreCase))
            {
                error = "FetchXML root is missing.";
                return false;
            }

            var distinct = string.Equals((string?)root.Attribute("distinct"), "true", StringComparison.OrdinalIgnoreCase);
            int? top = null;
            var topRaw = (string?)root.Attribute("top");
            if (!string.IsNullOrWhiteSpace(topRaw) &&
                int.TryParse(topRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var topParsed) &&
                topParsed > 0)
            {
                top = topParsed;
            }

            var entity = root.Elements().FirstOrDefault(e =>
                e.Name.LocalName.Equals("entity", StringComparison.OrdinalIgnoreCase));
            if (entity is null)
            {
                error = "FetchXML entity element is missing.";
                return false;
            }

            var linkEntities = entity.Descendants()
                .Where(e => e.Name.LocalName.Equals("link-entity", StringComparison.OrdinalIgnoreCase))
                .ToList();
            // Nested multi-hop joins with aggregates on intermediate links are not safely partition-merged.
            if (linkEntities.Any(l =>
                    l.Descendants().Any(d =>
                        d.Name.LocalName.Equals("link-entity", StringComparison.OrdinalIgnoreCase)) &&
                    l.DescendantsAndSelf().Any(d =>
                        d.Name.LocalName.Equals("attribute", StringComparison.OrdinalIgnoreCase) &&
                        !string.IsNullOrWhiteSpace((string?)d.Attribute("aggregate")))))
            {
                error =
                    "Nested linked-entity aggregates cannot be partition-merged safely. Narrow the date range and retry.";
                return false;
            }

            var aggregates = new List<CrmAggregateSpec>();
            var groupAliases = new List<string>();
            var orders = new List<OrderSpec>();

            foreach (var attr in entity.Descendants().Where(e =>
                         e.Name.LocalName.Equals("attribute", StringComparison.OrdinalIgnoreCase)))
            {
                var aggregate = (string?)attr.Attribute("aggregate");
                var alias = (string?)attr.Attribute("alias");
                var name = (string?)attr.Attribute("name");
                var groupby = string.Equals((string?)attr.Attribute("groupby"), "true", StringComparison.OrdinalIgnoreCase);
                var isLinkAttr = attr.Parent is not null &&
                                 attr.Parent.Name.LocalName.Equals("link-entity", StringComparison.OrdinalIgnoreCase);

                if (!string.IsNullOrWhiteSpace(aggregate))
                {
                    if (string.IsNullOrWhiteSpace(alias) || string.IsNullOrWhiteSpace(name))
                    {
                        error = "Aggregate attributes require name and alias.";
                        return false;
                    }

                    if (!TryMapAggregate(aggregate, out var fn))
                    {
                        error = $"Unsupported aggregate function '{aggregate}' for partition recovery.";
                        return false;
                    }

                    aggregates.Add(new CrmAggregateSpec
                    {
                        Function = fn,
                        Field = name!,
                        Alias = alias!
                    });
                }
                else if (groupby)
                {
                    if (string.IsNullOrWhiteSpace(alias))
                    {
                        error = isLinkAttr
                            ? "Linked group-by attributes require an alias."
                            : "Group-by attributes require an alias.";
                        return false;
                    }

                    groupAliases.Add(alias!);
                }
            }

            foreach (var order in entity.Descendants().Where(e =>
                         e.Name.LocalName.Equals("order", StringComparison.OrdinalIgnoreCase)))
            {
                orders.Add(new OrderSpec(
                    (string?)order.Attribute("alias"),
                    (string?)order.Attribute("attribute"),
                    string.Equals((string?)order.Attribute("descending"), "true", StringComparison.OrdinalIgnoreCase)));
            }

            if (aggregates.Count == 0)
            {
                error = "No aggregate attributes found for partition recovery.";
                return false;
            }

            shape = new AggregateShape(aggregates, groupAliases, orders, distinct, top);
            return true;
        }
        catch (Exception ex)
        {
            error = "Failed to analyze FetchXML for partition recovery: " + ex.Message;
            return false;
        }
    }

    internal static bool TryBuildPartitionedFetchXml(
        string originalFetchXml,
        string partitionAttr,
        AggregateDatePartitioner.Partition partition,
        out string fetchXml,
        out string? error)
    {
        fetchXml = string.Empty;
        error = null;
        try
        {
            var doc = XDocument.Parse(originalFetchXml, LoadOptions.None);
            var root = doc.Root!;
            var entity = root.Elements().First(e =>
                e.Name.LocalName.Equals("entity", StringComparison.OrdinalIgnoreCase));

            // Order must not run before merge.
            foreach (var order in entity.Descendants()
                         .Where(e => e.Name.LocalName.Equals("order", StringComparison.OrdinalIgnoreCase))
                         .ToList())
            {
                order.Remove();
            }

            root.Attribute("top")?.Remove();
            root.Attribute("count")?.Remove();
            root.Attribute("page")?.Remove();

            var andFilter = entity.Elements()
                .FirstOrDefault(e =>
                    e.Name.LocalName.Equals("filter", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals((string?)e.Attribute("type"), "and", StringComparison.OrdinalIgnoreCase));
            if (andFilter is null)
            {
                andFilter = new XElement("filter", new XAttribute("type", "and"));
                entity.Add(andFilter);
            }

            andFilter.Add(new XElement(
                "condition",
                new XAttribute("attribute", partitionAttr),
                new XAttribute("operator", "ge"),
                new XAttribute("value", FetchXmlConditionBuilder.FormatFetchDate(partition.StartInclusiveUtc))));
            andFilter.Add(new XElement(
                "condition",
                new XAttribute("attribute", partitionAttr),
                new XAttribute("operator", "lt"),
                new XAttribute("value", FetchXmlConditionBuilder.FormatFetchDate(partition.EndExclusiveUtc))));

            fetchXml = doc.ToString(SaveOptions.DisableFormatting);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    internal static bool TryBuildBoundProbeFetchXml(
        string originalFetchXml,
        string partitionAttr,
        bool ascending,
        out string fetchXml)
    {
        fetchXml = string.Empty;
        try
        {
            var doc = XDocument.Parse(originalFetchXml, LoadOptions.None);
            var root = doc.Root!;
            var entity = root.Elements().First(e =>
                e.Name.LocalName.Equals("entity", StringComparison.OrdinalIgnoreCase));

            root.SetAttributeValue("aggregate", null);
            root.Attribute("aggregate")?.Remove();
            root.SetAttributeValue("top", "1");
            root.Attribute("count")?.Remove();
            root.Attribute("page")?.Remove();
            root.Attribute("distinct")?.Remove();

            foreach (var attr in entity.Elements()
                         .Where(e => e.Name.LocalName.Equals("attribute", StringComparison.OrdinalIgnoreCase))
                         .ToList())
            {
                attr.Remove();
            }

            foreach (var order in entity.Descendants()
                         .Where(e => e.Name.LocalName.Equals("order", StringComparison.OrdinalIgnoreCase))
                         .ToList())
            {
                order.Remove();
            }

            // Keep filters and link-entities; drop projected attributes inside links.
            foreach (var link in entity.Descendants().Where(e =>
                         e.Name.LocalName.Equals("link-entity", StringComparison.OrdinalIgnoreCase)))
            {
                foreach (var attr in link.Elements()
                             .Where(e => e.Name.LocalName.Equals("attribute", StringComparison.OrdinalIgnoreCase))
                             .ToList())
                {
                    attr.Remove();
                }
            }

            entity.AddFirst(new XElement("attribute", new XAttribute("name", partitionAttr)));
            entity.Add(new XElement(
                "order",
                new XAttribute("attribute", partitionAttr),
                new XAttribute("descending", ascending ? "false" : "true")));

            fetchXml = doc.ToString(SaveOptions.DisableFormatting);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static IReadOnlyList<IReadOnlyDictionary<string, object?>> ApplyOrderAndTop(
        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows,
        AggregateShape shape)
    {
        IEnumerable<IReadOnlyDictionary<string, object?>> ordered = rows;
        foreach (var order in shape.Orders.AsEnumerable().Reverse())
        {
            var key = !string.IsNullOrWhiteSpace(order.Alias) ? order.Alias! : order.Attribute;
            if (string.IsNullOrWhiteSpace(key))
            {
                continue;
            }

            ordered = order.Descending
                ? ordered.OrderByDescending(r => r.TryGetValue(key, out var v) ? v : null, ComparableComparer.Instance)
                : ordered.OrderBy(r => r.TryGetValue(key, out var v) ? v : null, ComparableComparer.Instance);
        }

        var list = ordered.ToList();
        // Top-N only after merge so partition slices never truncate the global ranking.
        if (shape.Top is > 0 && list.Count > shape.Top.Value)
        {
            list = list.Take(shape.Top.Value).ToList();
        }

        return list;
    }

    private static bool TryMapAggregate(string fetchFn, out CrmAggregateFunction function)
    {
        function = default;
        switch (fetchFn.Trim().ToLowerInvariant())
        {
            case "count":
            case "countcolumn":
                function = CrmAggregateFunction.Count;
                return true;
            case "sum":
                function = CrmAggregateFunction.Sum;
                return true;
            case "min":
                function = CrmAggregateFunction.Min;
                return true;
            case "max":
                function = CrmAggregateFunction.Max;
                return true;
            case "avg":
                function = CrmAggregateFunction.Avg;
                return true;
            default:
                return false;
        }
    }

    private static DateTimeOffset? TryParseDate(object raw) => raw switch
    {
        DateTimeOffset dto => dto,
        DateTime dt => new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Utc)),
        string s when DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var p) => p,
        _ => null
    };

    private static List<IReadOnlyDictionary<string, object?>> ParseRows(string json)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("value", out var values))
        {
            return [];
        }

        var list = new List<IReadOnlyDictionary<string, object?>>();
        foreach (var item in values.EnumerateArray())
        {
            var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            foreach (var prop in item.EnumerateObject())
            {
                if (prop.Name.StartsWith('@') ||
                    prop.Name.Contains("@OData.", StringComparison.OrdinalIgnoreCase) ||
                    prop.Name.Contains("@Microsoft.", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                row[prop.Name] = prop.Value.ValueKind switch
                {
                    System.Text.Json.JsonValueKind.Null => null,
                    System.Text.Json.JsonValueKind.String => prop.Value.GetString(),
                    System.Text.Json.JsonValueKind.True => true,
                    System.Text.Json.JsonValueKind.False => false,
                    System.Text.Json.JsonValueKind.Number when prop.Value.TryGetInt64(out var l) => l,
                    System.Text.Json.JsonValueKind.Number when prop.Value.TryGetDecimal(out var d) => d,
                    System.Text.Json.JsonValueKind.Number => prop.Value.GetDouble(),
                    _ => prop.Value.ToString()
                };
            }

            list.Add(row);
        }

        return list;
    }

    private static GenericCrmQueryResult SuccessResult(
        ValidatedFetchXmlQuery query,
        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows,
        IReadOnlyList<string> summaries) =>
        new()
        {
            Entity = query.EntityLogicalName,
            Operation = CrmQueryOperation.Aggregate,
            RelativeRequestUri = "(fetchXml aggregate partitioned)",
            AppliedFilterSummary = summaries,
            Limit = rows.Count,
            Items = rows,
            ProviderName = "Dynamics365",
            PrimaryIdAttribute = query.Entity.PrimaryIdAttribute,
            PrimaryNameAttribute = query.Entity.PrimaryNameAttribute,
            SelectFields = query.ProjectedAliases,
            Columns = FetchXmlQueryExecutor.InferColumnsFromRows(
                query.Columns, rows, query.Entity.PrimaryIdAttribute),
            ResultMode = query.ResultMode.ToString().ToLowerInvariant(),
            Limitation = query.Limitation
        };

    private static GenericCrmQueryResult LimitationResult(ValidatedFetchXmlQuery query, string message) =>
        new()
        {
            Entity = query.EntityLogicalName,
            Operation = CrmQueryOperation.Aggregate,
            RelativeRequestUri = string.Empty,
            Message = message,
            DynamicsErrorCode = DynamicsCrmErrorParser.AggregateQueryRecordLimitCode,
            DynamicsHttpStatus = 400,
            IsRecoverableFetchXmlError = false,
            CorrectionErrorCode = "AGGREGATE_RECORD_LIMIT_EXCEEDED",
            SanitizedCorrectionFeedback = message,
            ProviderName = "Dynamics365",
            ResultMode = "partial",
            Limitation = message
        };

    internal readonly record struct AggregateShape(
        IReadOnlyList<CrmAggregateSpec> Aggregates,
        IReadOnlyList<string> GroupAliases,
        IReadOnlyList<OrderSpec> Orders,
        bool HasDistinct,
        int? Top);

    internal readonly record struct OrderSpec(string? Alias, string? Attribute, bool Descending);

    private sealed class ComparableComparer : IComparer<object?>
    {
        public static readonly ComparableComparer Instance = new();

        public int Compare(object? x, object? y)
        {
            if (x is null && y is null) return 0;
            if (x is null) return -1;
            if (y is null) return 1;
            if (x is IComparable cx && y is IComparable && x.GetType() == y.GetType())
            {
                return cx.CompareTo(y);
            }

            return string.Compare(
                Convert.ToString(x, CultureInfo.InvariantCulture),
                Convert.ToString(y, CultureInfo.InvariantCulture),
                StringComparison.OrdinalIgnoreCase);
        }
    }
}
