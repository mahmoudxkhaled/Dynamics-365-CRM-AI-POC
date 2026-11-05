using System.Globalization;
using System.Text.Json;
using CrmAiPoc.Api.Configuration;
using CrmAiPoc.Api.Crm.Dynamics;
using CrmAiPoc.Api.Crm.Metadata;
using CrmAiPoc.Api.Crm.Query;
using CrmAiPoc.Api.Security;
using Microsoft.Extensions.Options;

namespace CrmAiPoc.Api.Crm.Generic;

public sealed class DynamicsGenericCrmRepository : IGenericCrmRepository
{
    private readonly DynamicsCrmHttp _http;
    private readonly ICrmMetadataService _metadata;
    private readonly EntityApprovalPolicy _policy;
    private readonly IGenericCrmQueryBuilder _builder;
    private readonly FetchXmlAggregateBuilder _aggregateBuilder;
    private readonly ICrmAuthorizationService _auth;
    private readonly TimeZoneInfo _businessTz;
    private readonly ILogger<DynamicsGenericCrmRepository> _logger;

    public DynamicsGenericCrmRepository(
        DynamicsCrmHttp http,
        ICrmMetadataService metadata,
        EntityApprovalPolicy policy,
        IGenericCrmQueryBuilder builder,
        FetchXmlAggregateBuilder aggregateBuilder,
        ICrmAuthorizationService auth,
        IOptions<CrmOptions> crmOptions,
        ILogger<DynamicsGenericCrmRepository> logger)
    {
        _http = http;
        _metadata = metadata;
        _policy = policy;
        _builder = builder;
        _aggregateBuilder = aggregateBuilder;
        _auth = auth;
        _businessTz = BusinessTimeZone.Resolve(crmOptions.Value.BusinessTimeZoneId);
        _logger = logger;
    }

    public string ProviderName => "Dynamics365";

    public async Task<GenericCrmQueryResult> QueryAsync(CrmQueryPlan plan, CancellationToken cancellationToken = default)
    {
        if (!_policy.TryResolveEntityAlias(plan.Entity, out var logical) &&
            !_policy.IsApproved(plan.Entity))
        {
            return Fail(plan, $"Entity '{plan.Entity}' is not an approved AI-accessible CRM entity.");
        }

        if (!_policy.IsApproved(logical) && _policy.IsApproved(plan.Entity))
        {
            logical = plan.Entity.Trim();
        }

        var auth = _auth.AuthorizeRead(logical, "query_crm");
        if (!auth.Allowed)
        {
            return Fail(plan, auth.Reason);
        }

        var entity = await _metadata.GetEntityAsync(logical, cancellationToken);
        if (entity is null || !entity.IsApprovedForAi)
        {
            return Fail(plan, $"Metadata for approved entity '{logical}' is unavailable.");
        }

        CrmEntityMetadata? relatedFrom = null;
        if (!string.IsNullOrWhiteSpace(plan.RelatedFromEntity))
        {
            if (!_policy.IsApproved(plan.RelatedFromEntity) &&
                !_policy.TryResolveEntityAlias(plan.RelatedFromEntity, out _))
            {
                return Fail(plan, $"relatedFromEntity '{plan.RelatedFromEntity}' is not approved.");
            }

            var fromName = _policy.TryResolveEntityAlias(plan.RelatedFromEntity, out var resolved)
                ? resolved
                : plan.RelatedFromEntity.Trim();
            relatedFrom = await _metadata.GetEntityAsync(fromName, cancellationToken);
        }

        plan = CloneWithEntity(plan, logical);

        // All business CRM data queries use FetchXML (list / count / aggregate).
        if (plan.Operation == CrmQueryOperation.Aggregate)
        {
            return await ExecuteAggregateAsync(plan, entity, relatedFrom, cancellationToken);
        }

        var entityCache = new Dictionary<string, CrmEntityMetadata?>(StringComparer.OrdinalIgnoreCase)
        {
            [entity.LogicalName] = entity
        };
        if (relatedFrom is not null)
        {
            entityCache[relatedFrom.LogicalName] = relatedFrom;
        }

        await PrefetchLinkEntitiesAsync(plan.Links, entityCache, cancellationToken);

        CrmEntityMetadata? ResolveEntity(string name) =>
            entityCache.TryGetValue(name, out var cached) ? cached : null;

        var built = _builder.Build(plan, entity, relatedFrom, ResolveEntity);

        if (!built.Success || string.IsNullOrWhiteSpace(built.RelativeRequestUri))
        {
            return Fail(plan, built.Error ?? "Invalid CRM query plan.");
        }

        _http.EnsureLiveConfigured();
        _logger.LogInformation(
            "[FETCHXML] Entity={Entity} Operation={Operation} Limit={Limit} FetchLength={FetchLength}",
            logical,
            built.Operation,
            built.Limit,
            built.FetchXml?.Length ?? 0);

        // Prefer formatted-value annotations when supported (On-Prem may reject Prefer — fallback below).
        var http = await _http.GetAsync(built.RelativeRequestUri, includeAnnotations: true, cancellationToken);
        if (!http.IsSuccess)
        {
            _logger.LogWarning(
                "FetchXML query with annotations failed ({Status}); retrying without Prefer.",
                http.StatusCode);
            http = await _http.GetAsync(built.RelativeRequestUri, includeAnnotations: false, cancellationToken);
        }

        if (!http.IsSuccess)
        {
            var err = DynamicsCrmErrorParser.Parse(http.Body);
            if (built.Operation == CrmQueryOperation.Count && err.IsAggregateQueryRecordLimit)
            {
                var partitioned = await TryPartitionedCountAsync(
                    plan, entity, relatedFrom, cancellationToken);
                if (partitioned is not null)
                {
                    return partitioned;
                }

                return Fail(
                    plan,
                    "CRM AggregateQueryRecordLimit (0x8004E023) exceeded for count. " +
                    "Exact total cannot be computed safely over the full dataset; narrow the date range and retry.");
            }

            _logger.LogWarning(
                "FetchXML GET failed. Status={Status}, Entity={Entity}, Op={Op}, Code={Code}, Message={Message}",
                http.StatusCode,
                logical,
                built.Operation,
                err.Code,
                err.Message);
            return Fail(
                plan,
                $"Dynamics Web API FetchXML GET failed. Status={http.StatusCode}, Code={err.Code}, Entity={logical}.");
        }

        if (built.Operation == CrmQueryOperation.Count)
        {
            var countInfo = ReadFetchXmlAggregateCount(http.Body);
            var capped = countInfo.MayBeCapped ||
                         countInfo.Count >= CrmQueryLimits.DynamicsFetchXmlAggregateCountCeiling;
            return new GenericCrmQueryResult
            {
                Entity = logical,
                Operation = CrmQueryOperation.Count,
                RelativeRequestUri = "(fetchXml count)",
                AppliedFilterSummary = built.AppliedFilterSummary,
                SortSummary = built.SortSummary,
                Limit = built.Limit,
                Count = countInfo.Count,
                CountMayBeCapped = capped,
                CountLimitExceededAnnotation = countInfo.MayBeCapped,
                Message = capped
                    ? $"CRM FetchXML aggregate count returned {countInfo.Count} and may hit the Dynamics aggregate evaluation ceiling — not guaranteed exact."
                    : null,
                ProviderName = ProviderName,
                PrimaryIdAttribute = entity.PrimaryIdAttribute,
                PrimaryNameAttribute = entity.PrimaryNameAttribute,
                SelectFields = built.SelectFields,
                Columns = LinkResultNormalizer.MergeColumns(
                    CrmTableColumnBuilder.Build(entity, built.SelectFields),
                    built.LinkColumns)
            };
        }

        var items = LinkResultNormalizer.Normalize(ParseRows(http.Body, entity), built.LinkColumns);
        items = ClientSideResultSorter.Apply(items, plan.ClientSideSort);
        return new GenericCrmQueryResult
        {
            Entity = logical,
            Operation = CrmQueryOperation.List,
            RelativeRequestUri = "(fetchXml list)",
            AppliedFilterSummary = built.AppliedFilterSummary,
            SortSummary = ClientSideResultSorter.AppendSortSummary(built.SortSummary, plan.ClientSideSort),
            Limit = built.Limit,
            Items = items,
            Message = items.Count == 0 ? "No records matched the query." : null,
            ProviderName = ProviderName,
            PrimaryIdAttribute = entity.PrimaryIdAttribute,
            PrimaryNameAttribute = entity.PrimaryNameAttribute,
            SelectFields = built.SelectFields,
            Columns = LinkResultNormalizer.MergeColumns(
                CrmTableColumnBuilder.Build(entity, built.SelectFields, items),
                built.LinkColumns)
        };
    }

    private async Task PrefetchLinkEntitiesAsync(
        IReadOnlyList<CrmLinkSpec> links,
        Dictionary<string, CrmEntityMetadata?> cache,
        CancellationToken cancellationToken)
    {
        foreach (var link in links)
        {
            var name = link.Entity.Trim();
            if (!cache.ContainsKey(name))
            {
                if (!_policy.IsApproved(name) && !_policy.TryResolveEntityAlias(name, out _))
                {
                    cache[name] = null;
                }
                else
                {
                    var logical = _policy.TryResolveEntityAlias(name, out var resolved) ? resolved : name;
                    cache[logical] = await _metadata.GetEntityAsync(logical, cancellationToken);
                    if (!logical.Equals(name, StringComparison.OrdinalIgnoreCase))
                    {
                        cache[name] = cache[logical];
                    }
                }
            }

            if (link.Links.Count > 0)
            {
                await PrefetchLinkEntitiesAsync(link.Links, cache, cancellationToken);
            }
        }
    }

    private async Task<GenericCrmQueryResult> ExecuteAggregateAsync(
        CrmQueryPlan plan,
        CrmEntityMetadata entity,
        CrmEntityMetadata? relatedFrom,
        CancellationToken cancellationToken)
    {
        var built = _aggregateBuilder.Build(plan, entity, relatedFrom);
        if (!built.Success || string.IsNullOrWhiteSpace(built.RelativeRequestUri))
        {
            return Fail(plan, built.Error ?? "Invalid aggregate CRM query plan.");
        }

        _http.EnsureLiveConfigured();
        _logger.LogInformation(
            "query_crm aggregate prepared. Entity={Entity}, PathLength={PathLength}",
            entity.LogicalName,
            built.RelativeRequestUri.Length);

        var http = await GetAggregateHttpAsync(built.RelativeRequestUri, cancellationToken);
        if (http.IsSuccess)
        {
            return ToAggregateResult(plan, entity, built, ParseRows(http.Body, entity));
        }

        var err = DynamicsCrmErrorParser.Parse(http.Body);
        _logger.LogWarning(
            "Aggregate GET failed. Status={Status}, Entity={Entity}, Code={Code}, Message={Message}",
            http.StatusCode,
            entity.LogicalName,
            err.Code,
            err.Message);

        if (err.IsAggregateQueryRecordLimit)
        {
            var partitioned = await TryPartitionedAggregateAsync(plan, entity, relatedFrom, cancellationToken);
            if (partitioned is not null)
            {
                return partitioned;
            }

            return Fail(
                plan,
                "CRM AggregateQueryRecordLimit (0x8004E023) exceeded. " +
                "Exact grouped totals cannot be computed safely over the full dataset; " +
                "narrow the date range (for example last_6_calendar_months) and retry.");
        }

        return Fail(
            plan,
            $"Dynamics Web API aggregate GET failed. Status={http.StatusCode}, Code={err.Code}, Entity={entity.LogicalName}.");
    }

    private async Task<DynamicsHttpResult> GetAggregateHttpAsync(
        string relativeUri,
        CancellationToken cancellationToken)
    {
        var http = await _http.GetAsync(relativeUri, includeAnnotations: true, cancellationToken);
        if (!http.IsSuccess)
        {
            _logger.LogWarning(
                "Aggregate query with annotations failed ({Status}); retrying without Prefer.",
                http.StatusCode);
            http = await _http.GetAsync(relativeUri, includeAnnotations: false, cancellationToken);
        }

        return http;
    }

    private GenericCrmQueryResult ToAggregateResult(
        CrmQueryPlan plan,
        CrmEntityMetadata entity,
        FetchXmlAggregateBuildResult built,
        IReadOnlyList<IReadOnlyDictionary<string, object?>> rawItems,
        IReadOnlyList<string>? extraSummaries = null)
    {
        var items = AggregateResultNormalizer.Normalize(rawItems, plan, entity, built);
        var summaries = built.AppliedFilterSummary.ToList();
        if (extraSummaries is not null)
        {
            summaries.AddRange(extraSummaries);
        }

        return new GenericCrmQueryResult
        {
            Entity = entity.LogicalName,
            Operation = CrmQueryOperation.Aggregate,
            RelativeRequestUri = "(fetchXml aggregate)",
            AppliedFilterSummary = summaries,
            Limit = items.Count,
            Items = items,
            Message = items.Count == 0
                ? "No aggregate rows returned."
                : string.Join(" ", summaries.Where(s => s.Contains("Money SUM", StringComparison.Ordinal))),
            ProviderName = ProviderName,
            PrimaryIdAttribute = entity.PrimaryIdAttribute,
            PrimaryNameAttribute = entity.PrimaryNameAttribute,
            SelectFields = built.ResultFieldKeys,
            Columns = built.Columns
        };
    }

    private async Task<GenericCrmQueryResult?> TryPartitionedAggregateAsync(
        CrmQueryPlan plan,
        CrmEntityMetadata entity,
        CrmEntityMetadata? relatedFrom,
        CancellationToken cancellationToken)
    {
        if (!AggregateDatePartitioner.CanMergeExactly(plan))
        {
            _logger.LogWarning(
                "AggregateQueryRecordLimit hit but plan aggregates cannot be partition-merged exactly.");
            return null;
        }

        var bounds = await ResolveCreatedOnBoundsAsync(plan, entity, cancellationToken);
        if (bounds is null)
        {
            // No rows in scope — exact empty result.
            var emptyBuilt = _aggregateBuilder.Build(plan, entity, relatedFrom);
            return emptyBuilt.Success
                ? ToAggregateResult(plan, entity, emptyBuilt, Array.Empty<IReadOnlyDictionary<string, object?>>())
                : null;
        }

        var years = AggregateDatePartitioner.BuildYearPartitions(bounds.Value.Min, bounds.Value.Max, _businessTz);
        var partitionRowSets = new List<IReadOnlyList<IReadOnlyDictionary<string, object?>>>();
        var summaries = new List<string>
        {
            $"Recovered AggregateQueryRecordLimit via {years.Count} non-overlapping createdon year partition(s); totals are exact sums of partition results."
        };

        foreach (var year in years)
        {
            var yearRows = await ExecuteAggregatePartitionAsync(
                plan, entity, relatedFrom, year, cancellationToken);
            if (yearRows is not null)
            {
                partitionRowSets.Add(yearRows);
                continue;
            }

            // Year still over limit — subdivide into months (still non-overlapping).
            var months = AggregateDatePartitioner.BuildMonthPartitions(year, _businessTz);
            if (months.Count == 0)
            {
                return null;
            }

            summaries.Add($"Year partition {year.Label} subdivided into {months.Count} month partition(s).");
            foreach (var month in months)
            {
                var monthRows = await ExecuteAggregatePartitionAsync(
                    plan, entity, relatedFrom, month, cancellationToken);
                if (monthRows is null)
                {
                    _logger.LogWarning(
                        "Aggregate partition {Label} still exceeds AggregateQueryRecordLimit; cannot compute exact totals.",
                        month.Label);
                    return null;
                }

                partitionRowSets.Add(monthRows);
            }
        }

        var merged = AggregateDatePartitioner.MergePartitionRows(partitionRowSets, plan);
        var built = _aggregateBuilder.Build(plan, entity, relatedFrom);
        if (!built.Success)
        {
            return null;
        }

        return ToAggregateResult(plan, entity, built, merged, summaries);
    }

    private async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>?> ExecuteAggregatePartitionAsync(
        CrmQueryPlan plan,
        CrmEntityMetadata entity,
        CrmEntityMetadata? relatedFrom,
        AggregateDatePartitioner.Partition partition,
        CancellationToken cancellationToken)
    {
        var built = _aggregateBuilder.Build(plan, entity, relatedFrom, partition);
        if (!built.Success || string.IsNullOrWhiteSpace(built.RelativeRequestUri))
        {
            return null;
        }

        var http = await GetAggregateHttpAsync(built.RelativeRequestUri, cancellationToken);
        if (!http.IsSuccess)
        {
            var err = DynamicsCrmErrorParser.Parse(http.Body);
            if (err.IsAggregateQueryRecordLimit)
            {
                return null;
            }

            _logger.LogWarning(
                "Partition aggregate failed non-limit error. Label={Label}, Status={Status}, Code={Code}",
                partition.Label,
                http.StatusCode,
                err.Code);
            return null;
        }

        return ParseRows(http.Body, entity);
    }

    private async Task<GenericCrmQueryResult?> TryPartitionedCountAsync(
        CrmQueryPlan plan,
        CrmEntityMetadata entity,
        CrmEntityMetadata? relatedFrom,
        CancellationToken cancellationToken)
    {
        // Reuse aggregate COUNT partition merge (exact).
        var aggPlan = new CrmQueryPlan
        {
            Entity = plan.Entity,
            Operation = CrmQueryOperation.Aggregate,
            Filters = plan.Filters,
            RelativeDate = plan.RelativeDate,
            RelatedFromEntity = plan.RelatedFromEntity,
            RelatedFromId = plan.RelatedFromId,
            Aggregates =
            [
                new CrmAggregateSpec
                {
                    Function = CrmAggregateFunction.Count,
                    Field = entity.PrimaryIdAttribute,
                    Alias = GenericCrmQueryBuilder.CountAlias
                }
            ]
        };

        var partitioned = await TryPartitionedAggregateAsync(aggPlan, entity, relatedFrom, cancellationToken);
        if (partitioned is null || partitioned.Items.Count == 0)
        {
            return partitioned is null
                ? null
                : new GenericCrmQueryResult
                {
                    Entity = entity.LogicalName,
                    Operation = CrmQueryOperation.Count,
                    RelativeRequestUri = "(fetchXml count partitioned)",
                    AppliedFilterSummary = partitioned.AppliedFilterSummary,
                    Count = 0,
                    CountMayBeCapped = false,
                    ProviderName = ProviderName,
                    PrimaryIdAttribute = entity.PrimaryIdAttribute,
                    PrimaryNameAttribute = entity.PrimaryNameAttribute
                };
        }

        var total = 0L;
        foreach (var row in partitioned.Items)
        {
            if (row.TryGetValue(GenericCrmQueryBuilder.CountAlias, out var raw))
            {
                total += raw switch
                {
                    int i => i,
                    long l => l,
                    decimal d => (long)d,
                    string s when long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var p) => p,
                    _ => 0
                };
            }
        }

        return new GenericCrmQueryResult
        {
            Entity = entity.LogicalName,
            Operation = CrmQueryOperation.Count,
            RelativeRequestUri = "(fetchXml count partitioned)",
            AppliedFilterSummary = partitioned.AppliedFilterSummary,
            Count = total > int.MaxValue ? int.MaxValue : (int)total,
            CountMayBeCapped = false,
            Message = null,
            ProviderName = ProviderName,
            PrimaryIdAttribute = entity.PrimaryIdAttribute,
            PrimaryNameAttribute = entity.PrimaryNameAttribute,
            SelectFields = Array.Empty<string>(),
            Columns = Array.Empty<CrmTableColumn>()
        };
    }

    /// <summary>
    /// Resolves half-open createdon coverage for partition planning using list queries (not aggregates).
    /// </summary>
    private async Task<(DateTimeOffset Min, DateTimeOffset Max)?> ResolveCreatedOnBoundsAsync(
        CrmQueryPlan plan,
        CrmEntityMetadata entity,
        CancellationToken cancellationToken)
    {
        if (plan.RelativeDate is not null)
        {
            var resolved = DateRangeResolver.Resolve(
                plan.RelativeDate.GetExpression(),
                DateTimeOffset.UtcNow,
                _businessTz);
            return (resolved.StartUtc, resolved.EndUtcExclusive.AddTicks(-1));
        }

        var min = await FetchCreatedOnBoundAsync(entity, plan, ascending: true, cancellationToken);
        var max = await FetchCreatedOnBoundAsync(entity, plan, ascending: false, cancellationToken);
        if (min is null || max is null)
        {
            return null;
        }

        return (min.Value, max.Value);
    }

    private async Task<DateTimeOffset?> FetchCreatedOnBoundAsync(
        CrmEntityMetadata entity,
        CrmQueryPlan sourcePlan,
        bool ascending,
        CancellationToken cancellationToken)
    {
        var listPlan = new CrmQueryPlan
        {
            Entity = sourcePlan.Entity,
            Operation = CrmQueryOperation.List,
            Filters = sourcePlan.Filters,
            RelativeDate = sourcePlan.RelativeDate,
            RelatedFromEntity = sourcePlan.RelatedFromEntity,
            RelatedFromId = sourcePlan.RelatedFromId,
            Select = ["createdon"],
            Limit = 1,
            Sort =
            [
                new CrmQuerySort
                {
                    Field = "createdon",
                    Direction = ascending ? CrmSortDirection.Asc : CrmSortDirection.Desc
                }
            ]
        };

        var built = _builder.Build(listPlan, entity);
        if (!built.Success || string.IsNullOrWhiteSpace(built.RelativeRequestUri))
        {
            return null;
        }

        var http = await _http.GetAsync(built.RelativeRequestUri, includeAnnotations: false, cancellationToken);
        if (!http.IsSuccess)
        {
            return null;
        }

        var rows = ParseRows(http.Body, entity);
        if (rows.Count == 0 || !rows[0].TryGetValue("createdon", out var raw) || raw is null)
        {
            return null;
        }

        return raw switch
        {
            DateTimeOffset dto => dto,
            DateTime dt => new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Utc)),
            string s when DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var p) => p,
            _ => null
        };
    }

    private static CrmQueryPlan CloneWithEntity(CrmQueryPlan plan, string entity) =>
        new()
        {
            Entity = entity,
            Operation = plan.Operation,
            Select = plan.Select,
            Filters = plan.Filters,
            RelativeDate = plan.RelativeDate,
            Sort = plan.Sort,
            ClientSideSort = plan.ClientSideSort,
            Limit = plan.Limit,
            RelatedFromEntity = plan.RelatedFromEntity,
            RelatedFromId = plan.RelatedFromId,
            Aggregates = plan.Aggregates,
            GroupBy = plan.GroupBy,
            Links = plan.Links
        };

    private static GenericCrmQueryResult Fail(CrmQueryPlan plan, string message) =>
        new()
        {
            Entity = plan.Entity,
            Operation = plan.Operation,
            RelativeRequestUri = string.Empty,
            Message = message,
            ProviderName = "Dynamics365"
        };

    /// <summary>
    /// Reads aggregate FetchXML count alias (<see cref="GenericCrmQueryBuilder.CountAlias"/>).
    /// Does not treat page size as a total count.
    /// </summary>
    private static (int Count, bool MayBeCapped) ReadFetchXmlAggregateCount(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("value", out var values) ||
            values.ValueKind != JsonValueKind.Array)
        {
            return (0, false);
        }

        var first = values.EnumerateArray().FirstOrDefault();
        if (first.ValueKind != JsonValueKind.Object)
        {
            return (0, false);
        }

        foreach (var name in new[] { GenericCrmQueryBuilder.CountAlias, "count", "recordcount" })
        {
            if (first.TryGetProperty(name, out var prop) && TryReadInt(prop, out var n))
            {
                return (n, n >= CrmQueryLimits.DynamicsFetchXmlAggregateCountCeiling);
            }
        }

        // Some orgs return the alias with a type suffix or as the only numeric property.
        foreach (var prop in first.EnumerateObject())
        {
            if (prop.Name.StartsWith("@", StringComparison.Ordinal))
            {
                continue;
            }

            if (TryReadInt(prop.Value, out var n))
            {
                return (n, n >= CrmQueryLimits.DynamicsFetchXmlAggregateCountCeiling);
            }
        }

        return (0, false);
    }

    private static bool TryReadInt(JsonElement prop, out int value)
    {
        value = 0;
        if (prop.ValueKind == JsonValueKind.Number && prop.TryGetInt32(out value))
        {
            return true;
        }

        if (prop.ValueKind == JsonValueKind.String &&
            int.TryParse(prop.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
        {
            return true;
        }

        return false;
    }

    private static List<IReadOnlyDictionary<string, object?>> ParseRows(string json, CrmEntityMetadata entity)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("value", out var values))
        {
            return [];
        }

        var list = new List<IReadOnlyDictionary<string, object?>>();
        foreach (var item in values.EnumerateArray())
        {
            var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            var formattedByField = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (var prop in item.EnumerateObject())
            {
                // Capture Dynamics formatted-value annotations when Prefer returned them.
                // Example: totalamount@OData.Community.Display.V1.FormattedValue
                var annotationField = TryGetFormattedValueAnnotationField(prop.Name);
                if (annotationField is not null)
                {
                    var formatted = prop.Value.ValueKind == JsonValueKind.String
                        ? prop.Value.GetString()
                        : prop.Value.ToString();
                    if (!string.IsNullOrWhiteSpace(formatted))
                    {
                        formattedByField[annotationField] = formatted!;
                    }

                    continue;
                }

                if (prop.Name.StartsWith('@'))
                {
                    continue;
                }

                row[prop.Name] = ConvertJson(prop.Value);

                // Attach option labels when possible (skip nulls — TryGetInt32 throws on Null).
                if (prop.Value.ValueKind == JsonValueKind.Number &&
                    entity.TryGetAttribute(prop.Name, out var attr) &&
                    attr.Kind is CrmAttributeKind.Status or CrmAttributeKind.State or CrmAttributeKind.Picklist &&
                    prop.Value.TryGetInt32(out var code) &&
                    attr.OptionSetByValue.TryGetValue(code, out var label))
                {
                    row[prop.Name + "_label"] = label;
                }
            }

            foreach (var kv in formattedByField)
            {
                row[kv.Key + "_formatted"] = kv.Value;
            }

            list.Add(row);
        }

        return list;
    }

    private static string? TryGetFormattedValueAnnotationField(string propertyName)
    {
        const string community = "@OData.Community.Display.V1.FormattedValue";
        const string microsoft = "@Microsoft.Dynamics.CRM.formattedvalue";
        if (propertyName.EndsWith(community, StringComparison.OrdinalIgnoreCase))
        {
            return propertyName[..^community.Length];
        }

        if (propertyName.EndsWith(microsoft, StringComparison.OrdinalIgnoreCase))
        {
            return propertyName[..^microsoft.Length];
        }

        return null;
    }

    private static object? ConvertJson(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString(),
        JsonValueKind.Number when value.TryGetInt64(out var l) => l,
        JsonValueKind.Number when value.TryGetDecimal(out var d) => d,
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null => null,
        _ => value.ToString()
    };
}
