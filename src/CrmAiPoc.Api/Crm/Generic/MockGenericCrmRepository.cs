using CrmAiPoc.Api.Crm.Metadata;
using CrmAiPoc.Api.Crm.Mock;
using CrmAiPoc.Api.Crm.Query;
using CrmAiPoc.Api.Security;

namespace CrmAiPoc.Api.Crm.Generic;

/// <summary>
/// In-memory generic repository backed by mock seed data for account/contact/salesorder.
/// </summary>
public sealed class MockGenericCrmRepository : IGenericCrmRepository
{
    private readonly ICrmMetadataService _metadata;
    private readonly EntityApprovalPolicy _policy;
    private readonly IGenericCrmQueryBuilder _builder;
    private readonly FetchXmlAggregateBuilder _aggregateBuilder;
    private readonly ICrmAuthorizationService _auth;

    public MockGenericCrmRepository(
        ICrmMetadataService metadata,
        EntityApprovalPolicy policy,
        IGenericCrmQueryBuilder builder,
        FetchXmlAggregateBuilder aggregateBuilder,
        ICrmAuthorizationService auth)
    {
        _metadata = metadata;
        _policy = policy;
        _builder = builder;
        _aggregateBuilder = aggregateBuilder;
        _auth = auth;
    }

    public string ProviderName => "Mock";

    public async Task<GenericCrmQueryResult> QueryAsync(CrmQueryPlan plan, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!_policy.TryResolveEntityAlias(plan.Entity, out var logical))
        {
            logical = plan.Entity.Trim();
        }

        if (!_policy.IsApproved(logical))
        {
            return Fail(plan, $"Entity '{plan.Entity}' is not an approved AI-accessible CRM entity.");
        }

        var auth = _auth.AuthorizeRead(logical, "query_crm");
        if (!auth.Allowed)
        {
            return Fail(plan, auth.Reason);
        }

        var entity = await _metadata.GetEntityAsync(logical, cancellationToken);
        if (entity is null)
        {
            return Fail(plan, $"Metadata for '{logical}' is unavailable.");
        }

        CrmEntityMetadata? relatedFrom = null;
        if (!string.IsNullOrWhiteSpace(plan.RelatedFromEntity))
        {
            var fromName = _policy.TryResolveEntityAlias(plan.RelatedFromEntity, out var resolved)
                ? resolved
                : plan.RelatedFromEntity.Trim();
            relatedFrom = await _metadata.GetEntityAsync(fromName, cancellationToken);
        }

        plan = new CrmQueryPlan
        {
            Entity = logical,
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

        if (plan.Operation == CrmQueryOperation.Aggregate)
        {
            return ExecuteAggregate(plan, entity, relatedFrom);
        }

        var entityCache = new Dictionary<string, CrmEntityMetadata?>(StringComparer.OrdinalIgnoreCase)
        {
            [entity.LogicalName] = entity
        };
        await PrefetchLinkEntitiesAsync(plan.Links, entityCache, cancellationToken);
        CrmEntityMetadata? ResolveEntity(string name) =>
            entityCache.TryGetValue(name, out var cached) ? cached : null;

        var built = _builder.Build(plan, entity, relatedFrom, ResolveEntity);
        if (!built.Success || string.IsNullOrWhiteSpace(built.RelativeRequestUri))
        {
            return Fail(plan, built.Error ?? "Invalid CRM query plan.");
        }

        var rows = LoadRows(logical);
        if (!string.IsNullOrWhiteSpace(plan.RelatedFromEntity) || !string.IsNullOrWhiteSpace(plan.RelatedFromId))
        {
            var hop = CrmRelationshipResolver.ResolveOneHop(
                entity,
                plan.RelatedFromEntity ?? string.Empty,
                plan.RelatedFromId ?? string.Empty,
                relatedFrom);
            if (!hop.Success)
            {
                return Fail(plan, hop.Error ?? "Unsupported relationship.");
            }
        }

        rows = ApplyRelationship(rows, plan, entity, relatedFrom);
        rows = ApplyRelative(rows, plan);
        rows = ApplyFilters(rows, plan, entity);

        var linkResult = plan.Links.Count == 0
            ? CrmLinkResolveResult.Ok(Array.Empty<CrmResolvedLink>())
            : CrmRelationshipResolver.ResolveLinks(entity, plan.Links, ResolveEntity);
        if (!linkResult.Success)
        {
            return Fail(plan, linkResult.Error ?? "Invalid links.");
        }

        rows = ApplyLinks(rows, linkResult.Links, ResolveEntity);

        if (plan.Operation == CrmQueryOperation.Count)
        {
            return new GenericCrmQueryResult
            {
                Entity = logical,
                Operation = CrmQueryOperation.Count,
                RelativeRequestUri = built.RelativeRequestUri,
                AppliedFilterSummary = built.AppliedFilterSummary,
                Count = rows.Count,
                ProviderName = ProviderName,
                PrimaryIdAttribute = entity.PrimaryIdAttribute,
                PrimaryNameAttribute = entity.PrimaryNameAttribute,
                SelectFields = built.SelectFields,
                Columns = LinkResultNormalizer.MergeColumns(
                    CrmTableColumnBuilder.Build(entity, built.SelectFields),
                    built.LinkColumns)
            };
        }

        rows = ApplySort(rows, plan, entity).Take(built.Limit).ToList();
        // Project to validated $select keys so table columns align with the query plan.
        // Preserve alias-qualified link columns already attached by ApplyLinks.
        rows = ProjectSelect(rows, built.SelectFields, entity, preserveKeys: built.LinkColumns.Select(c => c.Key).ToArray());
        rows = LinkResultNormalizer.Normalize(rows, built.LinkColumns).ToList();
        rows = ClientSideResultSorter.Apply(rows, plan.ClientSideSort).ToList();
        return new GenericCrmQueryResult
        {
            Entity = logical,
            Operation = CrmQueryOperation.List,
            RelativeRequestUri = built.RelativeRequestUri,
            AppliedFilterSummary = built.AppliedFilterSummary,
            SortSummary = ClientSideResultSorter.AppendSortSummary(built.SortSummary, plan.ClientSideSort),
            Limit = built.Limit,
            Items = rows,
            Message = rows.Count == 0 ? "No records matched the query." : null,
            ProviderName = ProviderName,
            PrimaryIdAttribute = entity.PrimaryIdAttribute,
            PrimaryNameAttribute = entity.PrimaryNameAttribute,
            SelectFields = built.SelectFields,
            Columns = LinkResultNormalizer.MergeColumns(
                CrmTableColumnBuilder.Build(entity, built.SelectFields, rows),
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
                var logical = _policy.TryResolveEntityAlias(name, out var resolved) ? resolved : name;
                cache[logical] = await _metadata.GetEntityAsync(logical, cancellationToken);
                if (!logical.Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    cache[name] = cache[logical];
                }
            }

            if (link.Links.Count > 0)
            {
                await PrefetchLinkEntitiesAsync(link.Links, cache, cancellationToken);
            }
        }
    }

    /// <summary>
    /// In-memory join enrichment mirroring FetchXML link-entity projection/filter behavior.
    /// Outer joins keep roots with null related values; inner joins drop unmatched roots.
    /// 1:N expands may duplicate roots (same as Dynamics without DISTINCT on list).
    /// </summary>
    private static List<IReadOnlyDictionary<string, object?>> ApplyLinks(
        List<IReadOnlyDictionary<string, object?>> rows,
        IReadOnlyList<CrmResolvedLink> links,
        Func<string, CrmEntityMetadata?> getEntity)
    {
        foreach (var link in links)
        {
            rows = ApplySingleLink(rows, link, getEntity);
        }

        return rows;
    }

    private static List<IReadOnlyDictionary<string, object?>> ApplySingleLink(
        List<IReadOnlyDictionary<string, object?>> rows,
        CrmResolvedLink link,
        Func<string, CrmEntityMetadata?> getEntity)
    {
        var relatedMeta = getEntity(link.LinkEntityLogicalName);
        var relatedRows = LoadRows(link.LinkEntityLogicalName);
        var next = new List<IReadOnlyDictionary<string, object?>>();

        foreach (var row in rows)
        {
            var matches = relatedRows.Where(rel => MatchJoin(row, rel, link) && MatchLinkFilters(rel, link, relatedMeta)).ToList();
            if (matches.Count == 0)
            {
                if (link.JoinType == CrmLinkJoinType.Inner)
                {
                    continue;
                }

                var outer = new Dictionary<string, object?>(row, StringComparer.OrdinalIgnoreCase);
                foreach (var attr in link.SelectLogicalNames)
                {
                    outer[$"{link.Alias}.{attr}"] = null;
                }

                var nestedOuter = ApplyLinks([outer], link.Children, getEntity);
                next.AddRange(nestedOuter);
                continue;
            }

            foreach (var match in matches)
            {
                var merged = new Dictionary<string, object?>(row, StringComparer.OrdinalIgnoreCase);
                foreach (var attr in link.SelectLogicalNames)
                {
                    merged[$"{link.Alias}.{attr}"] = GetField(match, attr, relatedMeta);
                }

                var nested = ApplyLinks([merged], link.Children, getEntity);
                next.AddRange(nested);
            }
        }

        return next;
    }

    private static bool MatchJoin(
        IReadOnlyDictionary<string, object?> parent,
        IReadOnlyDictionary<string, object?> related,
        CrmResolvedLink link)
    {
        // FetchXML: link from=attr on related, to=attr on parent
        var parentKey = link.ToAttribute;
        var relatedKey = link.FromAttribute;
        var parentVal = GetField(parent, parentKey, null);
        var relatedVal = GetField(related, relatedKey, null);
        if (parentVal is null || relatedVal is null)
        {
            // Also try OData lookup forms on parent (e.g. _te_customeraccountid_value)
            parentVal ??= GetField(parent, "_" + link.ToAttribute + "_value", null)
                          ?? GetField(parent, link.ToAttribute.Replace("id", "", StringComparison.OrdinalIgnoreCase), null);
            relatedVal ??= GetField(related, relatedKey, null);
        }

        return string.Equals(
            Convert.ToString(parentVal),
            Convert.ToString(relatedVal),
            StringComparison.OrdinalIgnoreCase);
    }

    private static bool MatchLinkFilters(
        IReadOnlyDictionary<string, object?> related,
        CrmResolvedLink link,
        CrmEntityMetadata? relatedMeta)
    {
        foreach (var (attribute, op, value, _) in link.Filters)
        {
            var actual = Convert.ToString(GetField(related, attribute, relatedMeta)) ?? string.Empty;
            var needle = value.Trim('%');
            var ok = op.Equals("like", StringComparison.OrdinalIgnoreCase) ||
                     op.Equals("contains", StringComparison.OrdinalIgnoreCase)
                ? actual.Contains(needle, StringComparison.OrdinalIgnoreCase)
                : op.Equals("ne", StringComparison.OrdinalIgnoreCase)
                    ? !actual.Equals(value, StringComparison.OrdinalIgnoreCase)
                    : actual.Equals(value, StringComparison.OrdinalIgnoreCase);
            if (!ok)
            {
                return false;
            }
        }

        return true;
    }

    private static object? GetField(
        IReadOnlyDictionary<string, object?> row,
        string logicalOrOData,
        CrmEntityMetadata? meta)
    {
        if (row.TryGetValue(logicalOrOData, out var direct))
        {
            return direct;
        }

        if (meta is not null && meta.TryGetAttribute(logicalOrOData, out var attr))
        {
            if (row.TryGetValue(attr.ODataPropertyName, out var odata))
            {
                return odata;
            }

            if (row.TryGetValue(attr.LogicalName, out var logical))
            {
                return logical;
            }
        }

        var underscored = "_" + logicalOrOData + "_value";
        if (row.TryGetValue(underscored, out var lookup))
        {
            return lookup;
        }

        return null;
    }

    private GenericCrmQueryResult ExecuteAggregate(
        CrmQueryPlan plan,
        CrmEntityMetadata entity,
        CrmEntityMetadata? relatedFrom)
    {
        var built = _aggregateBuilder.Build(plan, entity, relatedFrom);
        if (!built.Success || string.IsNullOrWhiteSpace(built.RelativeRequestUri))
        {
            return Fail(plan, built.Error ?? "Invalid aggregate CRM query plan.");
        }

        var rows = LoadRows(plan.Entity);
        rows = ApplyRelationship(rows, plan, entity, relatedFrom);
        rows = ApplyRelative(rows, plan);
        rows = ApplyFilters(rows, plan, entity);

        var grouped = rows.GroupBy(r => BuildGroupKey(r, plan, entity)).ToList();
        var aggregateRows = new List<IReadOnlyDictionary<string, object?>>();
        foreach (var g in grouped)
        {
            var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            ApplyGroupValues(row, g.First(), plan, entity);
            foreach (var agg in plan.Aggregates)
            {
                row[agg.Alias] = ComputeAggregate(g, agg, entity);
            }

            aggregateRows.Add(row);
        }

        var normalized = AggregateResultNormalizer.Normalize(aggregateRows, plan, entity, built);
        return new GenericCrmQueryResult
        {
            Entity = plan.Entity,
            Operation = CrmQueryOperation.Aggregate,
            RelativeRequestUri = "(mock aggregate)",
            AppliedFilterSummary = built.AppliedFilterSummary,
            Limit = normalized.Count,
            Items = normalized,
            Message = normalized.Count == 0 ? "No aggregate rows returned." : null,
            ProviderName = ProviderName,
            PrimaryIdAttribute = entity.PrimaryIdAttribute,
            PrimaryNameAttribute = entity.PrimaryNameAttribute,
            SelectFields = built.ResultFieldKeys,
            Columns = built.Columns
        };
    }

    private static string BuildGroupKey(
        IReadOnlyDictionary<string, object?> row,
        CrmQueryPlan plan,
        CrmEntityMetadata entity)
    {
        if (plan.GroupBy.Count == 0)
        {
            return "__all__";
        }

        var parts = new List<string>();
        foreach (var group in plan.GroupBy)
        {
            parts.Add($"{group.Alias}={FormatGroupValue(row, group, entity)}");
        }

        return string.Join("|", parts);
    }

    private static void ApplyGroupValues(
        Dictionary<string, object?> target,
        IReadOnlyDictionary<string, object?> sample,
        CrmQueryPlan plan,
        CrmEntityMetadata entity)
    {
        foreach (var group in plan.GroupBy)
        {
            target[group.Alias] = ResolveGroupValue(sample, group, entity);
        }
    }

    private static object? ResolveGroupValue(
        IReadOnlyDictionary<string, object?> row,
        CrmGroupBySpec group,
        CrmEntityMetadata entity)
    {
        if (group.DateGrouping != CrmDateGrouping.None)
        {
            if (!TryGetDate(row, group.Field, entity, out var dto))
            {
                return null;
            }

            return group.DateGrouping switch
            {
                CrmDateGrouping.Year => dto.Year,
                CrmDateGrouping.Month => dto.Month,
                CrmDateGrouping.Day => dto.Day,
                CrmDateGrouping.Week => System.Globalization.ISOWeek.GetWeekOfYear(dto.UtcDateTime),
                CrmDateGrouping.Quarter => ((dto.Month - 1) / 3) + 1,
                _ => dto.Month
            };
        }

        entity.TryGetAttribute(group.Field, out var attr);
        var key = attr?.ODataPropertyName ?? group.Field;
        if (row.TryGetValue(key, out var value))
        {
            return value;
        }

        return row.TryGetValue(group.Field, out var alt) ? alt : null;
    }

    private static string FormatGroupValue(
        IReadOnlyDictionary<string, object?> row,
        CrmGroupBySpec group,
        CrmEntityMetadata entity) =>
        Convert.ToString(ResolveGroupValue(row, group, entity), System.Globalization.CultureInfo.InvariantCulture) ?? "";

    private static object? ComputeAggregate(
        IEnumerable<IReadOnlyDictionary<string, object?>> group,
        CrmAggregateSpec agg,
        CrmEntityMetadata entity)
    {
        entity.TryGetAttribute(agg.Field, out var attr);
        var key = attr?.ODataPropertyName ?? agg.Field;
        var values = group
            .Select(r => r.TryGetValue(key, out var v) ? v : (r.TryGetValue(agg.Field, out var alt) ? alt : null))
            .Where(v => v is not null)
            .ToList();

        return agg.Function switch
        {
            CrmAggregateFunction.Count => group.Count(),
            CrmAggregateFunction.Sum => values.Select(ToDecimal).Sum(),
            CrmAggregateFunction.Avg => values.Count == 0 ? 0m : values.Select(ToDecimal).Average(),
            CrmAggregateFunction.Min => values.Count == 0 ? null : values.Select(ToDecimal).Min(),
            CrmAggregateFunction.Max => values.Count == 0 ? null : values.Select(ToDecimal).Max(),
            _ => group.Count()
        };
    }

    private static decimal ToDecimal(object? value) => value switch
    {
        decimal d => d,
        double db => (decimal)db,
        float f => (decimal)f,
        int i => i,
        long l => l,
        string s when decimal.TryParse(s, System.Globalization.NumberStyles.Number,
            System.Globalization.CultureInfo.InvariantCulture, out var p) => p,
        _ => 0m
    };

    private static bool TryGetDate(
        IReadOnlyDictionary<string, object?> row,
        string field,
        CrmEntityMetadata entity,
        out DateTimeOffset dto)
    {
        dto = default;
        entity.TryGetAttribute(field, out var attr);
        var key = attr?.ODataPropertyName ?? field;
        if (!row.TryGetValue(key, out var raw) && !row.TryGetValue(field, out raw))
        {
            return false;
        }

        switch (raw)
        {
            case DateTimeOffset offset:
                dto = offset;
                return true;
            case DateTime dt:
                dto = new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Utc));
                return true;
            case string s when DateTimeOffset.TryParse(s, out var parsed):
                dto = parsed;
                return true;
            default:
                return false;
        }
    }

    private static List<IReadOnlyDictionary<string, object?>> ProjectSelect(
        List<IReadOnlyDictionary<string, object?>> rows,
        IReadOnlyList<string> selectFields,
        CrmEntityMetadata entity,
        IReadOnlyList<string>? preserveKeys = null)
    {
        if (selectFields.Count == 0 || rows.Count == 0)
        {
            return rows;
        }

        var projected = new List<IReadOnlyDictionary<string, object?>>(rows.Count);
        foreach (var row in rows)
        {
            var next = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            foreach (var field in selectFields)
            {
                if (row.TryGetValue(field, out var value))
                {
                    next[field] = value;
                }
                else
                {
                    next[field] = null;
                }

                var labelKey = field + "_label";
                if (row.TryGetValue(labelKey, out var label))
                {
                    next[labelKey] = label;
                }
                else if (entity.TryGetAttribute(field, out var attr) &&
                         attr.Kind is CrmAttributeKind.Status or CrmAttributeKind.State or CrmAttributeKind.Picklist &&
                         value is int code &&
                         attr.OptionSetByValue.TryGetValue(code, out var mapped))
                {
                    next[labelKey] = mapped;
                }
            }

            if (preserveKeys is not null)
            {
                foreach (var key in preserveKeys)
                {
                    if (row.TryGetValue(key, out var linked))
                    {
                        next[key] = linked;
                    }
                }
            }

            projected.Add(next);
        }

        return projected;
    }

    private static List<IReadOnlyDictionary<string, object?>> LoadRows(string logical) =>
        logical.ToLowerInvariant() switch
        {
            "account" => MockCrmSeedData.CreateAccounts().Select(a => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
            {
                ["accountid"] = a.AccountId,
                ["name"] = a.Name,
                ["accountnumber"] = a.AccountNumber,
                ["telephone1"] = a.Telephone1,
                ["emailaddress1"] = a.EmailAddress1,
                ["address1_city"] = a.City,
                ["address1_country"] = a.Country,
                ["createdon"] = DateTimeOffset.UtcNow.AddDays(-30)
            }).ToList(),
            "contact" => MockCrmSeedData.CreateContacts().Select(c => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
            {
                ["contactid"] = c.ContactId,
                ["fullname"] = c.FullName,
                ["emailaddress1"] = c.EmailAddress1,
                ["telephone1"] = c.Telephone1,
                ["jobtitle"] = c.JobTitle,
                ["_parentcustomerid_value"] = c.AccountId,
                ["createdon"] = DateTimeOffset.UtcNow.AddDays(-20)
            }).ToList(),
            "salesorder" => MockCrmSeedData.CreateSalesOrders().Select(o => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
            {
                ["salesorderid"] = o.SalesOrderId,
                ["name"] = o.Name,
                ["ordernumber"] = o.OrderNumber,
                ["createdon"] = o.CreatedOn,
                ["modifiedon"] = o.ModifiedOn,
                ["totalamount"] = o.TotalAmount,
                ["statuscode"] = o.StatusCode,
                ["statuscode_label"] = o.StatusLabel,
                ["statecode"] = o.StateCode,
                ["statecode_label"] = o.StateLabel,
                ["_te_customeraccountid_value"] = o.CustomerAccountId
            }).ToList(),
            _ => []
        };

    private static List<IReadOnlyDictionary<string, object?>> ApplyRelationship(
        List<IReadOnlyDictionary<string, object?>> rows,
        CrmQueryPlan plan,
        CrmEntityMetadata entity,
        CrmEntityMetadata? relatedFrom)
    {
        if (string.IsNullOrWhiteSpace(plan.RelatedFromEntity) || string.IsNullOrWhiteSpace(plan.RelatedFromId))
        {
            return rows;
        }

        var hop = CrmRelationshipResolver.ResolveOneHop(entity, plan.RelatedFromEntity, plan.RelatedFromId, relatedFrom);
        if (!hop.Success)
        {
            return [];
        }

        return rows.Where(r =>
            r.TryGetValue(hop.FilterField!, out var v) &&
            string.Equals(Convert.ToString(v), hop.FilterValue, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    private static List<IReadOnlyDictionary<string, object?>> ApplyRelative(
        List<IReadOnlyDictionary<string, object?>> rows,
        CrmQueryPlan plan)
    {
        if (plan.RelativeDate is null)
        {
            return rows;
        }

        var tz = BusinessTimeZone.Resolve();
        var resolved = Query.DateRangeResolver.Resolve(
            plan.RelativeDate.GetExpression(),
            DateTimeOffset.UtcNow,
            tz);
        return rows.Where(r =>
        {
            if (!r.TryGetValue(plan.RelativeDate.Field, out var v) || v is not DateTimeOffset dto)
            {
                return false;
            }

            return dto >= resolved.StartUtc && dto < resolved.EndUtcExclusive;
        }).ToList();
    }

    private static List<IReadOnlyDictionary<string, object?>> ApplyFilters(
        List<IReadOnlyDictionary<string, object?>> rows,
        CrmQueryPlan plan,
        CrmEntityMetadata entity)
    {
        foreach (var filter in plan.Filters)
        {
            if (!entity.TryGetAttribute(filter.Field, out var attr))
            {
                continue;
            }

            rows = rows.Where(r => Match(r, attr, filter)).ToList();
        }

        return rows;
    }

    private static bool Match(
        IReadOnlyDictionary<string, object?> row,
        CrmAttributeMetadata attr,
        CrmQueryFilter filter)
    {
        row.TryGetValue(attr.ODataPropertyName, out var raw);
        raw ??= row.TryGetValue(attr.LogicalName, out var alt) ? alt : null;
        var text = Convert.ToString(raw) ?? string.Empty;

        if (filter.Operator == CrmFilterOperator.Contains)
        {
            return text.Contains(filter.Value, StringComparison.OrdinalIgnoreCase);
        }

        if (attr.Kind is CrmAttributeKind.Money or CrmAttributeKind.Decimal or CrmAttributeKind.Integer &&
            decimal.TryParse(filter.Value, out var expected) &&
            decimal.TryParse(text, out var actual))
        {
            return filter.Operator switch
            {
                CrmFilterOperator.Gt => actual > expected,
                CrmFilterOperator.Ge => actual >= expected,
                CrmFilterOperator.Lt => actual < expected,
                CrmFilterOperator.Le => actual <= expected,
                CrmFilterOperator.Ne => actual != expected,
                _ => actual == expected
            };
        }

        if (attr.Kind is CrmAttributeKind.Status or CrmAttributeKind.State or CrmAttributeKind.Picklist)
        {
            var code = attr.OptionSetByLabel.TryGetValue(filter.Value, out var c) ? c.ToString() : filter.Value;
            return filter.Operator == CrmFilterOperator.Ne
                ? !text.Equals(code, StringComparison.OrdinalIgnoreCase)
                : text.Equals(code, StringComparison.OrdinalIgnoreCase);
        }

        return filter.Operator == CrmFilterOperator.Ne
            ? !text.Equals(filter.Value, StringComparison.OrdinalIgnoreCase)
            : text.Equals(filter.Value, StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<IReadOnlyDictionary<string, object?>> ApplySort(
        List<IReadOnlyDictionary<string, object?>> rows,
        CrmQueryPlan plan,
        CrmEntityMetadata entity)
    {
        var field = plan.Sort.FirstOrDefault()?.Field
                    ?? (entity.TryGetAttribute("createdon", out _) ? "createdon" : entity.PrimaryNameAttribute);
        var desc = plan.Sort.FirstOrDefault()?.Direction != CrmSortDirection.Asc;
        entity.TryGetAttribute(field, out var attr);
        var key = attr?.ODataPropertyName ?? field;

        return desc
            ? rows.OrderByDescending(r => r.TryGetValue(key, out var v) ? v?.ToString() : null)
            : rows.OrderBy(r => r.TryGetValue(key, out var v) ? v?.ToString() : null);
    }

    private static GenericCrmQueryResult Fail(CrmQueryPlan plan, string message) =>
        new()
        {
            Entity = plan.Entity,
            Operation = plan.Operation,
            RelativeRequestUri = string.Empty,
            Message = message,
            ProviderName = "Mock"
        };
}
