using System.Globalization;
using System.Xml.Linq;
using CrmAiPoc.Api.Configuration;
using CrmAiPoc.Api.Crm.Metadata;
using CrmAiPoc.Api.Crm.Query;
using Microsoft.Extensions.Options;

namespace CrmAiPoc.Api.Crm.Generic;

/// <summary>
/// Metadata-validated FetchXML builder for list and count business queries.
/// Aggregates are handled by <see cref="FetchXmlAggregateBuilder"/>.
/// </summary>
public sealed class GenericCrmQueryBuilder : IGenericCrmQueryBuilder
{
    public const string CountAlias = "record_count";

    private readonly TimeZoneInfo _businessTz;
    private readonly Func<DateTimeOffset> _utcNow;

    public GenericCrmQueryBuilder(IOptions<CrmOptions>? crmOptions = null, Func<DateTimeOffset>? utcNow = null)
    {
        _businessTz = BusinessTimeZone.Resolve(crmOptions?.Value.BusinessTimeZoneId);
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    }

    public GenericCrmQueryBuildResult Build(
        CrmQueryPlan plan,
        CrmEntityMetadata entity,
        CrmEntityMetadata? relatedFrom = null,
        Func<string, CrmEntityMetadata?>? getEntity = null)
    {
        if (plan is null || entity is null)
        {
            return GenericCrmQueryBuildResult.Fail("Query plan and entity metadata are required.");
        }

        if (!entity.IsApprovedForAi)
        {
            return GenericCrmQueryBuildResult.Fail($"Entity '{entity.LogicalName}' is not approved for AI queries.");
        }

        if (plan.Operation == CrmQueryOperation.Aggregate)
        {
            return GenericCrmQueryBuildResult.Fail(
                "operation=aggregate must use FetchXmlAggregateBuilder (routed by the CRM repository).");
        }

        if (plan.Filters.Count > CrmQueryLimits.MaxFilters)
        {
            return GenericCrmQueryBuildResult.Fail($"A maximum of {CrmQueryLimits.MaxFilters} filters is allowed.");
        }

        if (plan.Sort.Count > CrmQueryLimits.MaxSortFields)
        {
            return GenericCrmQueryBuildResult.Fail($"A maximum of {CrmQueryLimits.MaxSortFields} sort fields is allowed.");
        }

        var summaries = new List<string>();
        var conditions = new List<XElement>();

        var related = FetchXmlConditionBuilder.BuildRelationshipCondition(
            entity, plan.RelatedFromEntity, plan.RelatedFromId, relatedFrom);
        if (!related.Success)
        {
            return GenericCrmQueryBuildResult.Fail(related.Error!);
        }

        if (related.Condition is not null)
        {
            conditions.Add(related.Condition);
            if (!string.IsNullOrWhiteSpace(related.Summary))
            {
                summaries.Add(related.Summary!);
            }
        }

        if (plan.RelativeDate is not null)
        {
            var relative = FetchXmlConditionBuilder.BuildRelativeDate(
                entity, plan.RelativeDate, _utcNow(), _businessTz);
            if (!relative.Success)
            {
                return GenericCrmQueryBuildResult.Fail(relative.Error!);
            }

            conditions.AddRange(relative.Conditions!);
            summaries.Add(relative.Summary!);
        }

        foreach (var filter in plan.Filters)
        {
            var built = FetchXmlConditionBuilder.BuildFilter(entity, filter);
            if (!built.Success)
            {
                return GenericCrmQueryBuildResult.Fail(built.Error!);
            }

            conditions.Add(built.Condition!);
            summaries.Add(built.Summary!);
        }

        IReadOnlyList<CrmResolvedLink> resolvedLinks = Array.Empty<CrmResolvedLink>();
        IReadOnlyList<CrmTableColumn> linkColumns = Array.Empty<CrmTableColumn>();
        if (plan.Links.Count > 0)
        {
            if (plan.Operation == CrmQueryOperation.Count)
            {
                // Related filters on links are allowed for root counting; projection links without filters
                // do not change the count. Nested selects are ignored for count.
            }

            getEntity ??= static _ => null;
            var linkResult = CrmRelationshipResolver.ResolveLinks(entity, plan.Links, getEntity);
            if (!linkResult.Success)
            {
                return GenericCrmQueryBuildResult.Fail(linkResult.Error!);
            }

            resolvedLinks = linkResult.Links;
            foreach (var link in FlattenLinks(resolvedLinks))
            {
                summaries.Add(link.Summary);
                if (link.ExpandsToMany)
                {
                    summaries.Add(
                        "expands-to-many: joined rows may duplicate root records; " +
                        "FetchXML top/limit applies to joined rows, not distinct roots.");
                }
            }

            if (plan.Operation == CrmQueryOperation.Count &&
                FlattenLinks(resolvedLinks).Any(l => l.ExpandsToMany && l.Filters.Count == 0 && l.JoinType != CrmLinkJoinType.Inner))
            {
                // Outer 1:N projection links do not change root count; strip them from count FetchXML path.
                // Inner / filtered links remain and use distinct root counting below.
            }

            linkColumns = BuildLinkColumns(resolvedLinks, getEntity);
        }

        var selectLogical = ResolveSelectLogical(entity, plan.Select);
        var selectOData = selectLogical
            .Select(logical => entity.TryGetAttribute(logical, out var attr) ? attr.ODataPropertyName : logical)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var limit = ClampLimit(plan.Limit);
        string? sortSummary = null;
        IReadOnlyList<(string Attribute, bool Descending)> orders = Array.Empty<(string, bool)>();

        if (plan.Operation == CrmQueryOperation.List)
        {
            var sort = BuildSort(entity, plan.Sort);
            if (!sort.Success)
            {
                return GenericCrmQueryBuildResult.Fail(sort.Error!);
            }

            orders = sort.Orders;
            sortSummary = sort.Summary;
        }

        var fetchXml = plan.Operation == CrmQueryOperation.Count
            ? BuildCountFetchXml(entity, conditions, selectLogical, resolvedLinks)
            : BuildListFetchXml(entity, conditions, selectLogical, orders, limit, resolvedLinks);

        var uri = FetchXmlConditionBuilder.EncodeFetchUri(entity.EntitySetName, fetchXml);
        return GenericCrmQueryBuildResult.Ok(
            uri,
            fetchXml,
            plan.Operation,
            summaries,
            sortSummary,
            limit,
            selectOData,
            linkColumns);
    }

    private static string BuildListFetchXml(
        CrmEntityMetadata entity,
        IReadOnlyList<XElement> conditions,
        IReadOnlyList<string> selectLogical,
        IReadOnlyList<(string Attribute, bool Descending)> orders,
        int top,
        IReadOnlyList<CrmResolvedLink> links)
    {
        var entityEl = new XElement("entity", new XAttribute("name", entity.LogicalName));
        foreach (var attr in selectLogical)
        {
            entityEl.Add(new XElement("attribute", new XAttribute("name", attr)));
        }

        foreach (var (attribute, descending) in orders)
        {
            entityEl.Add(new XElement(
                "order",
                new XAttribute("attribute", attribute),
                new XAttribute("descending", descending ? "true" : "false")));
        }

        if (conditions.Count > 0)
        {
            entityEl.Add(new XElement("filter", new XAttribute("type", "and"), conditions));
        }

        foreach (var link in links)
        {
            entityEl.Add(BuildLinkEntityElement(link, includeAttributes: true));
        }

        var fetch = new XElement(
            "fetch",
            new XAttribute("version", "1.0"),
            new XAttribute("mapping", "logical"),
            new XAttribute("top", top.ToString(CultureInfo.InvariantCulture)),
            entityEl);

        return fetch.ToString(SaveOptions.DisableFormatting);
    }

    private static string BuildCountFetchXml(
        CrmEntityMetadata entity,
        IReadOnlyList<XElement> conditions,
        IReadOnlyList<string> selectLogical,
        IReadOnlyList<CrmResolvedLink> links)
    {
        var countField = !string.IsNullOrWhiteSpace(entity.PrimaryIdAttribute)
            ? entity.PrimaryIdAttribute
            : selectLogical.FirstOrDefault() ?? "activityid";

        var entityEl = new XElement(
            "entity",
            new XAttribute("name", entity.LogicalName),
            new XElement(
                "attribute",
                new XAttribute("name", countField),
                new XAttribute("alias", CountAlias),
                new XAttribute("aggregate", "count")));

        if (conditions.Count > 0)
        {
            entityEl.Add(new XElement("filter", new XAttribute("type", "and"), conditions));
        }

        // Include link-entity only when they carry filters or are inner joins (affect matching roots).
        var countLinks = links
            .Where(l => l.Filters.Count > 0 || l.JoinType == CrmLinkJoinType.Inner)
            .ToList();
        foreach (var link in countLinks)
        {
            entityEl.Add(BuildLinkEntityElement(link, includeAttributes: false));
        }

        // Root-record count semantics: when a 1:N expand could duplicate roots, use distinct=true
        // explicitly so COUNT(root PK) is a root count — never applied silently to list queries.
        var needsDistinctRootCount = FlattenLinks(countLinks).Any(l => l.ExpandsToMany);

        var fetch = new XElement(
            "fetch",
            new XAttribute("mapping", "logical"),
            new XAttribute("aggregate", "true"),
            new XAttribute("distinct", needsDistinctRootCount ? "true" : "false"),
            entityEl);

        return fetch.ToString(SaveOptions.DisableFormatting);
    }

    private static XElement BuildLinkEntityElement(CrmResolvedLink link, bool includeAttributes)
    {
        var linkType = link.JoinType == CrmLinkJoinType.Inner ? "inner" : "outer";
        var el = new XElement(
            "link-entity",
            new XAttribute("name", link.LinkEntityLogicalName),
            new XAttribute("from", link.FromAttribute),
            new XAttribute("to", link.ToAttribute),
            new XAttribute("alias", link.Alias),
            new XAttribute("link-type", linkType));

        if (includeAttributes)
        {
            foreach (var attr in link.SelectLogicalNames)
            {
                el.Add(new XElement("attribute", new XAttribute("name", attr)));
            }
        }

        if (link.Filters.Count > 0)
        {
            var filterEl = new XElement("filter", new XAttribute("type", "and"));
            foreach (var (attribute, op, value, _) in link.Filters)
            {
                filterEl.Add(new XElement(
                    "condition",
                    new XAttribute("attribute", attribute),
                    new XAttribute("operator", op),
                    new XAttribute("value", value)));
            }

            el.Add(filterEl);
        }

        foreach (var child in link.Children)
        {
            el.Add(BuildLinkEntityElement(child, includeAttributes));
        }

        return el;
    }

    private static IEnumerable<CrmResolvedLink> FlattenLinks(IEnumerable<CrmResolvedLink> links)
    {
        foreach (var link in links)
        {
            yield return link;
            foreach (var child in FlattenLinks(link.Children))
            {
                yield return child;
            }
        }
    }

    private static IReadOnlyList<CrmTableColumn> BuildLinkColumns(
        IReadOnlyList<CrmResolvedLink> links,
        Func<string, CrmEntityMetadata?> getEntity)
    {
        var cols = new List<CrmTableColumn>();
        foreach (var link in FlattenLinks(links))
        {
            var meta = getEntity(link.LinkEntityLogicalName);
            foreach (var attrName in link.SelectLogicalNames)
            {
                CrmAttributeMetadata? attr = null;
                if (meta is not null && meta.TryGetAttribute(attrName, out var resolved))
                {
                    attr = resolved;
                }

                var display = attr?.DisplayName ?? attrName;
                var key = $"{link.Alias}.{attrName}";
                cols.Add(new CrmTableColumn
                {
                    Key = key,
                    Label = $"{meta?.DisplayName ?? link.LinkEntityLogicalName} {display}".Trim(),
                    Kind = attr?.Kind.ToString().ToLowerInvariant() ?? "string",
                    LogicalName = attrName
                });
            }
        }

        return cols;
    }

    private static int ClampLimit(int limit) =>
        limit <= 0 ? CrmQueryLimits.DefaultLimit : Math.Clamp(limit, CrmQueryLimits.MinLimit, CrmQueryLimits.MaxLimit);

    private static readonly string[] PreferredSelectFields =
    [
        "createdon", "modifiedon", "statuscode", "statecode",
        "totalamount", "ordernumber", "accountnumber",
        "emailaddress1", "telephone1", "firstname", "lastname", "jobtitle",
        "address1_city", "address1_country", "fullname", "name", "subject",
        "customerid", "te_customeraccountid", "parentcustomerid"
    ];

    private static IReadOnlyList<string> ResolveSelectLogical(CrmEntityMetadata entity, IReadOnlyList<string> requested)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        TryAddSelect(set, entity, entity.PrimaryIdAttribute);
        TryAddSelect(set, entity, entity.PrimaryNameAttribute);

        if (requested.Count > 0)
        {
            foreach (var name in requested)
            {
                if (set.Count >= CrmQueryLimits.MaxSelectFields)
                {
                    break;
                }

                TryAddSelect(set, entity, name);
            }

            return set.ToArray();
        }

        foreach (var preferred in PreferredSelectFields)
        {
            if (set.Count >= CrmQueryLimits.MaxSelectFields)
            {
                break;
            }

            TryAddSelect(set, entity, preferred, requireSafeDefault: true);
        }

        if (set.Count < 6)
        {
            foreach (var attr in entity.Attributes
                         .Where(a => IsSafeDefaultSelectField(a, entity))
                         .OrderBy(a => a.LogicalName, StringComparer.OrdinalIgnoreCase))
            {
                if (set.Count >= CrmQueryLimits.MaxSelectFields)
                {
                    break;
                }

                set.Add(attr.LogicalName);
            }
        }

        return set.ToArray();
    }

    private static void TryAddSelect(
        HashSet<string> set,
        CrmEntityMetadata entity,
        string? logicalOrOData,
        bool requireSafeDefault = false)
    {
        if (string.IsNullOrWhiteSpace(logicalOrOData))
        {
            return;
        }

        if (!entity.TryGetAttribute(logicalOrOData, out var attr) || !attr.IsValidForRead)
        {
            if (!requireSafeDefault &&
                (logicalOrOData.Equals(entity.PrimaryIdAttribute, StringComparison.OrdinalIgnoreCase) ||
                 logicalOrOData.Equals(entity.PrimaryNameAttribute, StringComparison.OrdinalIgnoreCase)))
            {
                set.Add(FetchXmlConditionBuilder.ToFetchAttributeName(logicalOrOData));
            }

            return;
        }

        if (requireSafeDefault && !IsSafeDefaultSelectField(attr, entity))
        {
            return;
        }

        set.Add(attr.LogicalName);
    }

    private static bool IsSafeDefaultSelectField(CrmAttributeMetadata attr, CrmEntityMetadata entity)
    {
        if (!attr.IsValidForRead)
        {
            return false;
        }

        if (attr.Kind is CrmAttributeKind.Memo or CrmAttributeKind.Unknown)
        {
            return false;
        }

        var n = attr.LogicalName;
        if (n.StartsWith("entityimage", StringComparison.OrdinalIgnoreCase) ||
            n.StartsWith("traversedpath", StringComparison.OrdinalIgnoreCase) ||
            n.Contains("yominame", StringComparison.OrdinalIgnoreCase) ||
            n.EndsWith("idname", StringComparison.OrdinalIgnoreCase) ||
            n.EndsWith("_base", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (n.EndsWith("name", StringComparison.OrdinalIgnoreCase) &&
            !n.Equals(entity.PrimaryNameAttribute, StringComparison.OrdinalIgnoreCase) &&
            n is not ("name" or "fullname" or "firstname" or "lastname" or "subject"))
        {
            return false;
        }

        return attr.Kind is CrmAttributeKind.String or CrmAttributeKind.Integer or CrmAttributeKind.Decimal
            or CrmAttributeKind.Money or CrmAttributeKind.Boolean or CrmAttributeKind.DateTime
            or CrmAttributeKind.Picklist or CrmAttributeKind.Status or CrmAttributeKind.State
            or CrmAttributeKind.Uniqueidentifier or CrmAttributeKind.Lookup or CrmAttributeKind.Customer
            or CrmAttributeKind.Owner;
    }

    private static SortBuild BuildSort(CrmEntityMetadata entity, IReadOnlyList<CrmQuerySort> sorts)
    {
        if (sorts.Count == 0)
        {
            if (entity.TryGetAttribute("createdon", out var created) && created.IsSortable)
            {
                return SortBuild.Ok([(created.LogicalName, true)], "createdon desc (default)");
            }

            if (entity.TryGetAttribute(entity.PrimaryNameAttribute, out var name) && name.IsSortable)
            {
                return SortBuild.Ok([(name.LogicalName, false)], $"{name.LogicalName} asc (default)");
            }

            return SortBuild.Ok([], null);
        }

        var parts = new List<(string, bool)>();
        var summaries = new List<string>();
        foreach (var sort in sorts)
        {
            if (!entity.TryGetAttribute(sort.Field, out var field) || !field.IsSortable)
            {
                return SortBuild.Fail($"Sort field '{sort.Field}' is not approved/sortable on '{entity.LogicalName}'.");
            }

            var desc = sort.Direction != CrmSortDirection.Asc;
            parts.Add((field.LogicalName, desc));
            summaries.Add($"{field.LogicalName} {(desc ? "desc" : "asc")}");
        }

        return SortBuild.Ok(parts, string.Join(", ", summaries));
    }

    private sealed class SortBuild
    {
        public bool Success { get; init; }
        public string? Error { get; init; }
        public IReadOnlyList<(string Attribute, bool Descending)> Orders { get; init; } =
            Array.Empty<(string, bool)>();
        public string? Summary { get; init; }

        public static SortBuild Ok(IReadOnlyList<(string, bool)> orders, string? summary) =>
            new() { Success = true, Orders = orders, Summary = summary };

        public static SortBuild Fail(string error) =>
            new() { Success = false, Error = error };
    }
}
