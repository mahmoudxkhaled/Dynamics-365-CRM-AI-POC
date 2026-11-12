using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using CrmAiPoc.Api.Crm.Generic;
using CrmAiPoc.Api.Crm.Metadata;
using CrmAiPoc.Api.Security;

namespace CrmAiPoc.Api.Crm.FetchXml;

/// <summary>
/// Strict, allowlisted FetchXML validator. Never executes CRM calls.
/// Disables DTD/external entities and rejects unknown constructs.
/// </summary>
public sealed class FetchXmlSafetyValidator
{
    private static readonly HashSet<string> AllowedElements = new(StringComparer.OrdinalIgnoreCase)
    {
        "fetch", "entity", "attribute", "filter", "condition", "link-entity", "order", "value"
    };

    private static readonly HashSet<string> AllowedFetchAttrs = new(StringComparer.OrdinalIgnoreCase)
    {
        "version", "mapping", "distinct", "aggregate", "count", "page", "top",
        "returntotalrecordcount", "nolock", "outputformat", "minactivrowversion"
    };

    private static readonly HashSet<string> AllowedEntityAttrs = new(StringComparer.OrdinalIgnoreCase)
    {
        "name", "alias", "enableprefiltering", "prefilterparametername"
    };

    private static readonly HashSet<string> AllowedAttributeAttrs = new(StringComparer.OrdinalIgnoreCase)
    {
        "name", "alias", "aggregate", "groupby", "dategrouping", "distinct",
        "usertimezone", "addedby"
    };

    private static readonly HashSet<string> AllowedFilterAttrs = new(StringComparer.OrdinalIgnoreCase)
    {
        "type", "isquickfindfields", "overridequickfindrecordlimitenabled"
    };

    private static readonly HashSet<string> AllowedConditionAttrs = new(StringComparer.OrdinalIgnoreCase)
    {
        "attribute", "operator", "value", "entityname", "alias", "uitype", "uiname", "column"
    };

    private static readonly HashSet<string> AllowedLinkAttrs = new(StringComparer.OrdinalIgnoreCase)
    {
        "name", "from", "to", "alias", "link-type", "visible", "intersect",
        "relationship", "enableprefiltering", "prefilterparametername"
    };

    private static readonly HashSet<string> AllowedOrderAttrs = new(StringComparer.OrdinalIgnoreCase)
    {
        "attribute", "descending", "alias", "entityname"
    };

    private static readonly HashSet<string> AllowedOperators = new(StringComparer.OrdinalIgnoreCase)
    {
        "eq", "neq", "ne", "gt", "ge", "lt", "le",
        "like", "not-like", "begins-with", "not-begin-with", "ends-with", "not-end-with",
        "null", "not-null", "in", "not-in",
        "on", "on-or-before", "on-or-after", "yesterday", "today", "tomorrow",
        "last-seven-days", "next-seven-days", "last-week", "this-week", "next-week",
        "last-month", "this-month", "next-month", "last-year", "this-year", "next-year",
        "last-x-hours", "next-x-hours", "last-x-days", "next-x-days",
        "last-x-weeks", "next-x-weeks", "last-x-months", "next-x-months",
        "last-x-years", "next-x-years", "olderthan-x-months", "olderthan-x-years",
        "olderthan-x-weeks", "olderthan-x-days", "olderthan-x-hours", "olderthan-x-minutes",
        "eq-userid", "ne-userid", "eq-userteams", "eq-useroruserteams",
        "eq-businessid", "ne-businessid", "eq-userlanguage",
        "under", "eq-or-under", "not-under", "above", "eq-or-above",
        "contain-values", "not-contain-values"
    };

    private static readonly HashSet<string> AllowedAggregates = new(StringComparer.OrdinalIgnoreCase)
    {
        "count", "countcolumn", "sum", "avg", "min", "max"
    };

    private static readonly HashSet<string> AllowedDateGrouping = new(StringComparer.OrdinalIgnoreCase)
    {
        "day", "week", "month", "quarter", "year", "fiscal-period", "fiscal-year"
    };

    private readonly ICrmMetadataService _metadata;
    private readonly EntityApprovalPolicy _policy;
    private readonly ICrmAuthorizationService _auth;

    public FetchXmlSafetyValidator(
        ICrmMetadataService metadata,
        EntityApprovalPolicy policy,
        ICrmAuthorizationService auth)
    {
        _metadata = metadata;
        _policy = policy;
        _auth = auth;
    }

    public async Task<FetchXmlValidationOutcome> ValidateAsync(
        FetchXmlToolRequest request,
        CancellationToken cancellationToken = default)
    {
        XDocument doc;
        try
        {
            doc = ParseSecure(request.FetchXml);
        }
        catch (Exception ex) when (ex is XmlException or InvalidOperationException or ArgumentException)
        {
            return FetchXmlValidationOutcome.Fail(
                "Parse",
                "INVALID_XML",
                "fetchXml is not well-formed or uses unsafe XML constructs.");
        }

        var root = doc.Root;
        if (root is null || !root.Name.LocalName.Equals("fetch", StringComparison.OrdinalIgnoreCase))
        {
            return FetchXmlValidationOutcome.Fail("Parse", "INVALID_XML", "Root element must be <fetch>.");
        }

        if (!string.IsNullOrEmpty(root.Name.NamespaceName))
        {
            return FetchXmlValidationOutcome.Fail("Parse", "INVALID_XML", "Namespaced FetchXML is not allowed.");
        }

        var structureError = ValidateElementTree(root, depth: 0);
        if (structureError is not null)
        {
            return structureError;
        }

        var entityEl = root.Elements().FirstOrDefault(e =>
            e.Name.LocalName.Equals("entity", StringComparison.OrdinalIgnoreCase));
        if (entityEl is null)
        {
            return FetchXmlValidationOutcome.Fail("Parse", "INVALID_XML", "<fetch> must contain one root <entity>.");
        }

        var entityName = (string?)entityEl.Attribute("name");
        if (string.IsNullOrWhiteSpace(entityName))
        {
            return FetchXmlValidationOutcome.Fail("Parse", "INVALID_XML", "Root entity name is required.");
        }

        if (!_policy.TryResolveEntityAlias(entityName, out var logical) && !_policy.IsApproved(entityName))
        {
            return FetchXmlValidationOutcome.Fail(
                "EntityApproval",
                "ENTITY_NOT_APPROVED",
                $"Entity '{entityName}' is not an approved AI-accessible CRM entity.",
                entity: entityName);
        }

        if (!_policy.IsApproved(logical) && _policy.IsApproved(entityName))
        {
            logical = entityName.Trim();
        }

        var auth = _auth.AuthorizeRead(logical, "query_crm");
        if (!auth.Allowed)
        {
            return FetchXmlValidationOutcome.Fail(
                "Authorization",
                "AUTHORIZATION_DENIED",
                auth.Reason,
                entity: logical);
        }

        var entity = await _metadata.GetEntityAsync(logical, cancellationToken);
        if (entity is null || !entity.IsApprovedForAi)
        {
            return FetchXmlValidationOutcome.Fail(
                "MetadataLoad",
                "METADATA_UNAVAILABLE",
                $"CRM metadata for '{logical}' could not be loaded from Dynamics 365. Please try again later.",
                entity: logical);
        }

        // Force canonical logical name on the root entity element.
        entityEl.SetAttributeValue("name", entity.LogicalName);

        var isAggregate = IsTrue(root.Attribute("aggregate"));
        var topAttr = root.Attribute("top");
        var countAttr = root.Attribute("count");
        var top = ParseOptionalInt(topAttr);
        var pageCount = ParseOptionalInt(countAttr);
        var hasTop = topAttr is not null && !string.IsNullOrWhiteSpace(topAttr.Value);
        var hasCount = countAttr is not null && !string.IsNullOrWhiteSpace(countAttr.Value);

        // Reject paging beyond first page before list-limit normalization.
        var page = ParseOptionalInt(root.Attribute("page"));
        if (page is > 1)
        {
            return FetchXmlValidationOutcome.Fail(
                "FetchXmlBuild",
                "UNSUPPORTED_FETCHXML",
                "Paging beyond page 1 is not supported.");
        }

        if (hasTop && top is null)
        {
            return FetchXmlValidationOutcome.Fail(
                "FetchXmlBuild",
                "INVALID_QUERY_PLAN",
                "fetch top must be an integer when set.");
        }

        if (hasCount && pageCount is null)
        {
            return FetchXmlValidationOutcome.Fail(
                "FetchXmlBuild",
                "INVALID_QUERY_PLAN",
                "fetch count must be an integer when set.");
        }

        if (top is < 1)
        {
            return FetchXmlValidationOutcome.Fail("FetchXmlBuild", "INVALID_QUERY_PLAN", "fetch top must be >= 1 when set.");
        }

        if (pageCount is < 1)
        {
            return FetchXmlValidationOutcome.Fail("FetchXmlBuild", "INVALID_QUERY_PLAN", "fetch count must be >= 1 when set.");
        }

        if (!isAggregate)
        {
            // Dynamics rejects list FetchXML that specifies both top and paging count.
            if (hasTop && hasCount)
            {
                return FetchXmlValidationOutcome.Fail(
                    "FetchXmlBuild",
                    "INVALID_QUERY_PLAN",
                    "Non-aggregate FetchXML cannot specify both top and count. " +
                    $"Use top for the row limit (1–{FetchXmlSafetyLimits.MaxTop}); do not set count on list queries.");
            }

            if (!hasTop && hasCount)
            {
                // First-page row-limit request expressed as count → normalize to top (no multi-page API).
                if (pageCount > FetchXmlSafetyLimits.MaxTop)
                {
                    return FetchXmlValidationOutcome.Fail(
                        "FetchXmlBuild",
                        "QUERY_TOO_LARGE",
                        $"fetch count cannot exceed {FetchXmlSafetyLimits.MaxTop} for list queries.");
                }

                root.SetAttributeValue("top", pageCount!.Value);
                countAttr!.Remove();
                top = pageCount;
            }
            else if (!hasTop)
            {
                root.SetAttributeValue("top", FetchXmlSafetyLimits.DefaultTop);
                top = FetchXmlSafetyLimits.DefaultTop;
            }
            else if (top > FetchXmlSafetyLimits.MaxTop)
            {
                return FetchXmlValidationOutcome.Fail(
                    "FetchXmlBuild",
                    "QUERY_TOO_LARGE",
                    $"fetch top cannot exceed {FetchXmlSafetyLimits.MaxTop}.");
            }
        }
        else if (pageCount is > FetchXmlSafetyLimits.MaxAggregatePageSize)
        {
            return FetchXmlValidationOutcome.Fail(
                "FetchXmlBuild",
                "QUERY_TOO_LARGE",
                $"aggregate fetch count cannot exceed {FetchXmlSafetyLimits.MaxAggregatePageSize}.");
        }

        var linkCounter = new LinkCounter();
        var aliasMap = new Dictionary<string, CrmEntityMetadata>(StringComparer.OrdinalIgnoreCase);
        var columnAliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var metaError = await ValidateEntityContentAsync(
            entityEl,
            entity,
            isAggregate,
            depth: 0,
            linkCounter,
            aliasMap,
            columnAliases,
            cancellationToken);
        if (metaError is not null)
        {
            return metaError;
        }

        // Validate entityname references on conditions against known aliases.
        foreach (var condition in root.Descendants().Where(e =>
                     e.Name.LocalName.Equals("condition", StringComparison.OrdinalIgnoreCase)))
        {
            var entityname = (string?)condition.Attribute("entityname");
            if (!string.IsNullOrWhiteSpace(entityname) &&
                !aliasMap.ContainsKey(entityname) &&
                !entityname.Equals(entity.LogicalName, StringComparison.OrdinalIgnoreCase))
            {
                return FetchXmlValidationOutcome.Fail(
                    "FetchXmlBuild",
                    "INVALID_ALIAS",
                    $"condition entityname '{entityname}' does not match a declared link-entity alias.",
                    field: entityname,
                    entity: entity.LogicalName);
            }
        }

        var projected = CollectProjectedColumns(entityEl, entity, aliasMap, isAggregate, out var isCountOnly);
        var normalizedXml = root.ToString(SaveOptions.DisableFormatting);
        var uri = FetchXmlConditionBuilder.EncodeFetchUri(entity.EntitySetName, normalizedXml);

        return FetchXmlValidationOutcome.Ok(new ValidatedFetchXmlQuery
        {
            FetchXml = normalizedXml,
            EntityLogicalName = entity.LogicalName,
            EntitySetName = entity.EntitySetName,
            Entity = entity,
            RelativeRequestUri = uri,
            IsAggregate = isAggregate,
            IsCountOnly = isCountOnly,
            Top = top,
            Columns = projected,
            ProjectedAliases = projected.Select(c => c.Key).ToArray(),
            ResultMode = request.ResultMode,
            Limitation = request.Limitation
        });
    }

    private sealed class LinkCounter
    {
        public int Count;
    }

    private async Task<FetchXmlValidationOutcome?> ValidateEntityContentAsync(
        XElement entityEl,
        CrmEntityMetadata entity,
        bool isAggregate,
        int depth,
        LinkCounter linkCounter,
        Dictionary<string, CrmEntityMetadata> aliasMap,
        HashSet<string> columnAliases,
        CancellationToken cancellationToken)
    {
        var attrCount = 0;
        var orderCount = 0;
        var aggregateAttrCount = 0;
        var groupByCount = 0;
        // Physical CRM attribute name -> declared column alias for aggregate/groupby projections.
        var projectedAggregateFields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var projectedGroupByFields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var localAliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Pass 1: attributes (collect aliases before validating orders; attribute/order order is not guaranteed).
        foreach (var child in entityEl.Elements())
        {
            if (!child.Name.LocalName.Equals("attribute", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            attrCount++;
            if (depth == 0 && attrCount > FetchXmlSafetyLimits.MaxRootAttributes)
            {
                return FetchXmlValidationOutcome.Fail(
                    "FetchXmlBuild",
                    "QUERY_TOO_LARGE",
                    $"A maximum of {FetchXmlSafetyLimits.MaxRootAttributes} root attributes is allowed.");
            }

            if (depth > 0 && attrCount > FetchXmlSafetyLimits.MaxLinkAttributes)
            {
                return FetchXmlValidationOutcome.Fail(
                    "FetchXmlBuild",
                    "QUERY_TOO_LARGE",
                    $"A maximum of {FetchXmlSafetyLimits.MaxLinkAttributes} attributes per link-entity is allowed.");
            }

            var field = (string?)child.Attribute("name");
            if (string.IsNullOrWhiteSpace(field))
            {
                return FetchXmlValidationOutcome.Fail(
                    "FetchXmlBuild", "INVALID_FIELD", "attribute name is required.", entity: entity.LogicalName);
            }

            if (!entity.TryGetAttribute(field, out var attr) || !attr.IsValidForRead)
            {
                return FetchXmlValidationOutcome.Fail(
                    "FetchXmlBuild",
                    "INVALID_FIELD",
                    $"Attribute '{field}' is not approved/readable on '{entity.LogicalName}'.",
                    field: field,
                    entity: entity.LogicalName);
            }

            var alias = (string?)child.Attribute("alias");
            var agg = (string?)child.Attribute("aggregate");
            var isGroupBy = IsTrue(child.Attribute("groupby"));
            var hasAggregateOp = !string.IsNullOrWhiteSpace(agg);

            // Dynamics On-Prem (8004111E): every projected attribute in an aggregate fetch
            // must be groupby or a supported aggregate — including attributes inside link-entity.
            if (isAggregate)
            {
                if (hasAggregateOp && isGroupBy)
                {
                    return FetchXmlValidationOutcome.Fail(
                        "FetchXmlBuild",
                        "INVALID_AGGREGATE_FIELD",
                        $"Aggregate query attribute '{entity.LogicalName}.{field}' cannot specify both groupby and aggregate.",
                        field: field,
                        entity: entity.LogicalName);
                }

                if (!hasAggregateOp && !isGroupBy)
                {
                    return FetchXmlValidationOutcome.Fail(
                        "FetchXmlBuild",
                        "INVALID_AGGREGATE_FIELD",
                        $"Aggregate query attribute '{entity.LogicalName}.{field}' must specify groupby='true' or a supported aggregate operation.",
                        field: field,
                        entity: entity.LogicalName);
                }
            }

            if (hasAggregateOp)
            {
                if (!isAggregate)
                {
                    return FetchXmlValidationOutcome.Fail(
                        "FetchXmlBuild",
                        "INVALID_QUERY_PLAN",
                        "aggregate attributes require fetch aggregate=\"true\".");
                }

                if (!AllowedAggregates.Contains(agg!))
                {
                    return FetchXmlValidationOutcome.Fail(
                        "FetchXmlBuild",
                        "INVALID_AGGREGATE_FIELD",
                        $"Unsupported aggregate function '{agg}'.",
                        field: field,
                        entity: entity.LogicalName);
                }

                if (string.IsNullOrWhiteSpace(alias))
                {
                    return FetchXmlValidationOutcome.Fail(
                        "FetchXmlBuild",
                        "INVALID_ALIAS",
                        $"Aggregate attribute '{field}' requires an alias.",
                        field: field,
                        entity: entity.LogicalName);
                }

                aggregateAttrCount++;
                if (aggregateAttrCount > FetchXmlSafetyLimits.MaxAggregateAttributes)
                {
                    return FetchXmlValidationOutcome.Fail(
                        "FetchXmlBuild",
                        "QUERY_TOO_LARGE",
                        $"A maximum of {FetchXmlSafetyLimits.MaxAggregateAttributes} aggregate attributes is allowed.");
                }

                projectedAggregateFields[field] = alias!;
            }

            if (isGroupBy)
            {
                if (!isAggregate)
                {
                    return FetchXmlValidationOutcome.Fail(
                        "FetchXmlBuild",
                        "INVALID_QUERY_PLAN",
                        "groupby attributes require fetch aggregate=\"true\".");
                }

                groupByCount++;
                if (groupByCount > FetchXmlSafetyLimits.MaxGroupByAttributes)
                {
                    return FetchXmlValidationOutcome.Fail(
                        "FetchXmlBuild",
                        "QUERY_TOO_LARGE",
                        $"A maximum of {FetchXmlSafetyLimits.MaxGroupByAttributes} groupby attributes is allowed.");
                }

                if (string.IsNullOrWhiteSpace(alias))
                {
                    return FetchXmlValidationOutcome.Fail(
                        "FetchXmlBuild",
                        "INVALID_ALIAS",
                        $"groupby attribute '{field}' requires an alias.",
                        field: field,
                        entity: entity.LogicalName);
                }

                var dg = (string?)child.Attribute("dategrouping");
                if (!string.IsNullOrWhiteSpace(dg) && !AllowedDateGrouping.Contains(dg))
                {
                    return FetchXmlValidationOutcome.Fail(
                        "FetchXmlBuild",
                        "INVALID_GROUP_BY_FIELD",
                        $"Unsupported dategrouping '{dg}'.",
                        field: field,
                        entity: entity.LogicalName);
                }

                projectedGroupByFields[field] = alias!;
            }

            if (!string.IsNullOrWhiteSpace(alias))
            {
                if (!localAliases.Add(alias) || !columnAliases.Add(alias))
                {
                    return FetchXmlValidationOutcome.Fail(
                        "FetchXmlBuild",
                        "INVALID_ALIAS",
                        $"Duplicate attribute alias '{alias}'.",
                        field: alias,
                        entity: entity.LogicalName);
                }
            }
        }

        // Pass 2: filters, orders, links.
        foreach (var child in entityEl.Elements())
        {
            var name = child.Name.LocalName;
            if (name.Equals("attribute", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (name.Equals("order", StringComparison.OrdinalIgnoreCase))
            {
                orderCount++;
                if (orderCount > FetchXmlSafetyLimits.MaxOrders)
                {
                    return FetchXmlValidationOutcome.Fail(
                        "FetchXmlBuild",
                        "QUERY_TOO_LARGE",
                        $"A maximum of {FetchXmlSafetyLimits.MaxOrders} order elements is allowed.");
                }

                var orderError = ValidateOrder(
                    child, entity, isAggregate, localAliases, projectedAggregateFields, projectedGroupByFields);
                if (orderError is not null)
                {
                    return orderError;
                }
            }
            else if (name.Equals("filter", StringComparison.OrdinalIgnoreCase))
            {
                var filterError = await ValidateFilterAsync(child, entity, aliasMap, cancellationToken);
                if (filterError is not null)
                {
                    return filterError;
                }
            }
            else if (name.Equals("link-entity", StringComparison.OrdinalIgnoreCase))
            {
                linkCounter.Count++;
                if (linkCounter.Count > FetchXmlSafetyLimits.MaxLinkedEntities)
                {
                    return FetchXmlValidationOutcome.Fail(
                        "FetchXmlBuild",
                        "QUERY_TOO_LARGE",
                        $"A maximum of {FetchXmlSafetyLimits.MaxLinkedEntities} linked entities is allowed.");
                }

                if (depth >= FetchXmlSafetyLimits.MaxLinkDepth)
                {
                    return FetchXmlValidationOutcome.Fail(
                        "FetchXmlBuild",
                        "QUERY_TOO_LARGE",
                        $"link-entity depth cannot exceed {FetchXmlSafetyLimits.MaxLinkDepth}.");
                }

                var linkError = await ValidateLinkAsync(
                    child, entity, isAggregate, depth, linkCounter, aliasMap, columnAliases, cancellationToken);
                if (linkError is not null)
                {
                    return linkError;
                }
            }
            else
            {
                return FetchXmlValidationOutcome.Fail(
                    "FetchXmlBuild",
                    "UNSUPPORTED_FETCHXML",
                    $"Unsupported element <{name}> inside entity.");
            }
        }

        if (isAggregate)
        {
            var identityError = ValidateAggregateRecordGroupIdentity(entity, projectedGroupByFields);
            if (identityError is not null)
            {
                return identityError;
            }
        }

        return null;
    }

    /// <summary>
    /// When grouping by an entity's primary display name, also require that entity's primary ID
    /// so distinct records with the same name are not merged. Categorical fields (option sets,
    /// state/status, dates, numbers) do not need this.
    /// </summary>
    private static FetchXmlValidationOutcome? ValidateAggregateRecordGroupIdentity(
        CrmEntityMetadata entity,
        IReadOnlyDictionary<string, string> projectedGroupByFields)
    {
        if (projectedGroupByFields.Count == 0 ||
            string.IsNullOrWhiteSpace(entity.PrimaryNameAttribute) ||
            string.IsNullOrWhiteSpace(entity.PrimaryIdAttribute))
        {
            return null;
        }

        var groupsByName = projectedGroupByFields.ContainsKey(entity.PrimaryNameAttribute);
        var groupsById = projectedGroupByFields.ContainsKey(entity.PrimaryIdAttribute);
        if (!groupsByName || groupsById)
        {
            return null;
        }

        return FetchXmlValidationOutcome.Fail(
            "FetchXmlBuild",
            "AGGREGATE_GROUP_IDENTITY",
            $"Grouping '{entity.LogicalName}' by '{entity.PrimaryNameAttribute}' without '{entity.PrimaryIdAttribute}' " +
            "can merge different records that share the same display name. " +
            $"Add <attribute name=\"{entity.PrimaryIdAttribute}\" alias=\"...\" groupby=\"true\"/> on the same entity " +
            $"(keep the name groupby for display). For categorical fields (status, option sets, dates), do not group by the primary name alone.",
            field: entity.PrimaryNameAttribute,
            entity: entity.LogicalName);
    }

    private static FetchXmlValidationOutcome? ValidateOrder(
        XElement orderEl,
        CrmEntityMetadata entity,
        bool isAggregate,
        IReadOnlySet<string> localAliases,
        IReadOnlyDictionary<string, string> projectedAggregateFields,
        IReadOnlyDictionary<string, string> projectedGroupByFields)
    {
        var orderAttr = (string?)orderEl.Attribute("attribute");
        var orderAlias = (string?)orderEl.Attribute("alias");
        if (string.IsNullOrWhiteSpace(orderAttr) && string.IsNullOrWhiteSpace(orderAlias))
        {
            return FetchXmlValidationOutcome.Fail(
                "FetchXmlBuild", "INVALID_SORT", "order requires attribute or alias.");
        }

        if (isAggregate)
        {
            if (!string.IsNullOrWhiteSpace(orderAlias))
            {
                if (!localAliases.Contains(orderAlias))
                {
                    return FetchXmlValidationOutcome.Fail(
                        "FetchXmlBuild",
                        "INVALID_SORT",
                        $"Sort alias '{orderAlias}' is not declared by an aggregate or groupby attribute on '{entity.LogicalName}'.",
                        field: orderAlias,
                        entity: entity.LogicalName);
                }

                return null;
            }

            // attribute= on aggregate fetch: common LLM mistake — alias name stuffed into attribute=.
            if (localAliases.Contains(orderAttr!))
            {
                return FetchXmlValidationOutcome.Fail(
                    "FetchXmlBuild",
                    "INVALID_SORT",
                    $"In aggregate FetchXML, sort declared alias '{orderAttr}' using <order alias=\"{orderAttr}\"/>, not attribute=.",
                    field: orderAttr,
                    entity: entity.LogicalName);
            }

            if (projectedAggregateFields.TryGetValue(orderAttr!, out var aggAlias))
            {
                return FetchXmlValidationOutcome.Fail(
                    "FetchXmlBuild",
                    "INVALID_SORT",
                    $"Aggregate column '{orderAttr}' must be sorted with <order alias=\"{aggAlias}\"/>, not attribute=\"{orderAttr}\".",
                    field: orderAttr,
                    entity: entity.LogicalName);
            }

            if (projectedGroupByFields.TryGetValue(orderAttr!, out var groupAlias))
            {
                return FetchXmlValidationOutcome.Fail(
                    "FetchXmlBuild",
                    "INVALID_SORT",
                    $"groupby column '{orderAttr}' must be sorted with <order alias=\"{groupAlias}\"/>, not attribute=\"{orderAttr}\".",
                    field: orderAttr,
                    entity: entity.LogicalName);
            }

            return FetchXmlValidationOutcome.Fail(
                "FetchXmlBuild",
                "INVALID_SORT",
                $"In aggregate FetchXML, order must reference a declared aggregate/groupby alias via alias= (not attribute='{orderAttr}').",
                field: orderAttr,
                entity: entity.LogicalName);
        }

        // Non-aggregate list sorting.
        if (!string.IsNullOrWhiteSpace(orderAlias) && string.IsNullOrWhiteSpace(orderAttr))
        {
            return FetchXmlValidationOutcome.Fail(
                "FetchXmlBuild",
                "INVALID_SORT",
                "Non-aggregate FetchXML order must use attribute=, not alias=.",
                field: orderAlias,
                entity: entity.LogicalName);
        }

        if (!entity.TryGetAttribute(orderAttr!, out var sortAttr))
        {
            return FetchXmlValidationOutcome.Fail(
                "FetchXmlBuild",
                "INVALID_SORT",
                $"Sort attribute '{orderAttr}' is not valid on '{entity.LogicalName}'.",
                field: orderAttr,
                entity: entity.LogicalName);
        }

        if (!sortAttr.IsSortable)
        {
            return FetchXmlValidationOutcome.Fail(
                "FetchXmlBuild",
                "INVALID_SORT",
                $"Attribute '{orderAttr}' is not sortable on '{entity.LogicalName}'.",
                field: orderAttr,
                entity: entity.LogicalName);
        }

        return null;
    }

    private async Task<FetchXmlValidationOutcome?> ValidateLinkAsync(
        XElement linkEl,
        CrmEntityMetadata fromEntity,
        bool isAggregate,
        int depth,
        LinkCounter linkCounter,
        Dictionary<string, CrmEntityMetadata> aliasMap,
        HashSet<string> columnAliases,
        CancellationToken cancellationToken)
    {
        var linkName = (string?)linkEl.Attribute("name");
        var from = (string?)linkEl.Attribute("from");
        var to = (string?)linkEl.Attribute("to");
        var alias = (string?)linkEl.Attribute("alias");
        var relationship = (string?)linkEl.Attribute("relationship");

        if (string.IsNullOrWhiteSpace(linkName))
        {
            return FetchXmlValidationOutcome.Fail(
                "FetchXmlBuild", "INVALID_RELATIONSHIP", "link-entity name is required.");
        }

        if (string.IsNullOrWhiteSpace(from) || string.IsNullOrWhiteSpace(to))
        {
            return FetchXmlValidationOutcome.Fail(
                "FetchXmlBuild",
                "INVALID_RELATIONSHIP",
                "link-entity requires from and to attributes.",
                entity: linkName);
        }

        if (!_policy.IsApproved(linkName) && !_policy.TryResolveEntityAlias(linkName, out _))
        {
            return FetchXmlValidationOutcome.Fail(
                "EntityApproval",
                "ENTITY_NOT_APPROVED",
                $"Linked entity '{linkName}' is not an approved AI-accessible CRM entity.",
                entity: linkName);
        }

        var resolvedLinkName = _policy.TryResolveEntityAlias(linkName, out var resolved)
            ? resolved
            : linkName.Trim();

        var linkAuth = _auth.AuthorizeRead(resolvedLinkName, "query_crm");
        if (!linkAuth.Allowed)
        {
            return FetchXmlValidationOutcome.Fail(
                "Authorization",
                "AUTHORIZATION_DENIED",
                linkAuth.Reason,
                entity: resolvedLinkName);
        }

        var linkEntity = await _metadata.GetEntityAsync(resolvedLinkName, cancellationToken);
        if (linkEntity is null || !linkEntity.IsApprovedForAi)
        {
            return FetchXmlValidationOutcome.Fail(
                "MetadataLoad",
                "METADATA_UNAVAILABLE",
                $"CRM metadata for linked entity '{resolvedLinkName}' could not be loaded.",
                entity: resolvedLinkName);
        }

        linkEl.SetAttributeValue("name", linkEntity.LogicalName);

        if (!string.IsNullOrWhiteSpace(relationship))
        {
            var endpointError = ValidateNamedRelationshipEndpoints(
                relationship,
                fromEntity,
                linkEntity,
                from!,
                to!);
            if (endpointError is not null)
            {
                return endpointError;
            }
        }
        else
        {
            // No SchemaName: keep best-effort attribute existence checks.
            var attrError = ValidateJoinAttributesExist(fromEntity, linkEntity, from!, to!);
            if (attrError is not null)
            {
                return attrError;
            }
        }

        if (!string.IsNullOrWhiteSpace(alias))
        {
            if (!aliasMap.TryAdd(alias, linkEntity))
            {
                return FetchXmlValidationOutcome.Fail(
                    "FetchXmlBuild",
                    "INVALID_ALIAS",
                    $"Duplicate link-entity alias '{alias}'.",
                    field: alias);
            }
        }

        return await ValidateEntityContentAsync(
            linkEl, linkEntity, isAggregate, depth + 1, linkCounter, aliasMap, columnAliases, cancellationToken);
    }

    /// <summary>
    /// When relationship= is present, resolve metadata and require from/to to match that relationship's
    /// endpoints for the current parent → linked-entity direction. Does not infer business intent.
    /// </summary>
    private FetchXmlValidationOutcome? ValidateNamedRelationshipEndpoints(
        string relationshipSchemaName,
        CrmEntityMetadata parentEntity,
        CrmEntityMetadata linkedEntity,
        string fromAttr,
        string toAttr)
    {
        var rel = FindRelationshipBetween(relationshipSchemaName, parentEntity, linkedEntity);
        if (rel is null)
        {
            // Incomplete metadata stubs: do not hard-fail when neither side has relationships loaded.
            if (parentEntity.Relationships.Count == 0 && linkedEntity.Relationships.Count == 0)
            {
                return ValidateJoinAttributesExist(parentEntity, linkedEntity, fromAttr, toAttr);
            }

            return FetchXmlValidationOutcome.Fail(
                "FetchXmlBuild",
                "INVALID_RELATIONSHIP",
                $"relationship '{relationshipSchemaName}' is not valid between '{parentEntity.LogicalName}' and '{linkedEntity.LogicalName}'.",
                field: relationshipSchemaName,
                entity: parentEntity.LogicalName);
        }

        if (!rel.IsApproved ||
            !_policy.IsRelationshipApproved(rel.SchemaName, rel.ReferencingEntity, rel.ReferencedEntity))
        {
            return FetchXmlValidationOutcome.Fail(
                "FetchXmlBuild",
                "INVALID_RELATIONSHIP",
                $"relationship '{relationshipSchemaName}' is not approved for AI joins.",
                field: relationshipSchemaName,
                entity: parentEntity.LogicalName);
        }

        if (!TryMatchRelationshipEndpoints(
                rel,
                parentEntity.LogicalName,
                linkedEntity.LogicalName,
                out var expectedFrom,
                out var expectedTo))
        {
            return FetchXmlValidationOutcome.Fail(
                "FetchXmlBuild",
                "INVALID_RELATIONSHIP",
                $"relationship '{relationshipSchemaName}' does not connect '{parentEntity.LogicalName}' to '{linkedEntity.LogicalName}' with the declared endpoints.",
                field: relationshipSchemaName,
                entity: parentEntity.LogicalName);
        }

        if (!fromAttr.Equals(expectedFrom, StringComparison.OrdinalIgnoreCase) ||
            !toAttr.Equals(expectedTo, StringComparison.OrdinalIgnoreCase))
        {
            return FetchXmlValidationOutcome.Fail(
                "FetchXmlBuild",
                "INVALID_RELATIONSHIP",
                $"link-entity from/to must match relationship '{relationshipSchemaName}' endpoints " +
                $"(expected from='{expectedFrom}' to='{expectedTo}' for parent '{parentEntity.LogicalName}' → linked '{linkedEntity.LogicalName}'; " +
                $"got from='{fromAttr}' to='{toAttr}').",
                field: relationshipSchemaName,
                entity: parentEntity.LogicalName);
        }

        return null;
    }

    private static CrmRelationshipMetadata? FindRelationshipBetween(
        string schemaName,
        CrmEntityMetadata parentEntity,
        CrmEntityMetadata linkedEntity)
    {
        static bool Relates(CrmRelationshipMetadata r, string a, string b) =>
            (r.ReferencingEntity.Equals(a, StringComparison.OrdinalIgnoreCase) &&
             r.ReferencedEntity.Equals(b, StringComparison.OrdinalIgnoreCase)) ||
            (r.ReferencingEntity.Equals(b, StringComparison.OrdinalIgnoreCase) &&
             r.ReferencedEntity.Equals(a, StringComparison.OrdinalIgnoreCase));

        return parentEntity.Relationships.FirstOrDefault(r =>
                   r.SchemaName.Equals(schemaName, StringComparison.OrdinalIgnoreCase) &&
                   Relates(r, parentEntity.LogicalName, linkedEntity.LogicalName))
               ?? linkedEntity.Relationships.FirstOrDefault(r =>
                   r.SchemaName.Equals(schemaName, StringComparison.OrdinalIgnoreCase) &&
                   Relates(r, parentEntity.LogicalName, linkedEntity.LogicalName));
    }

    /// <summary>
    /// Maps relationship endpoints onto FetchXML from/to for the current parent and linked entity.
    /// from = attribute on linked entity; to = attribute on parent entity.
    /// </summary>
    private static bool TryMatchRelationshipEndpoints(
        CrmRelationshipMetadata rel,
        string parentLogicalName,
        string linkedLogicalName,
        out string expectedFrom,
        out string expectedTo)
    {
        expectedFrom = string.Empty;
        expectedTo = string.Empty;

        if (string.IsNullOrWhiteSpace(rel.ReferencingAttribute) ||
            string.IsNullOrWhiteSpace(rel.ReferencedAttribute))
        {
            return false;
        }

        // Parent holds the lookup (Many-to-One from parent → linked).
        if (rel.ReferencingEntity.Equals(parentLogicalName, StringComparison.OrdinalIgnoreCase) &&
            rel.ReferencedEntity.Equals(linkedLogicalName, StringComparison.OrdinalIgnoreCase))
        {
            expectedFrom = rel.ReferencedAttribute!;
            expectedTo = rel.ReferencingAttribute!;
            return true;
        }

        // Linked entity holds the lookup toward parent (One-to-Many children).
        if (rel.ReferencedEntity.Equals(parentLogicalName, StringComparison.OrdinalIgnoreCase) &&
            rel.ReferencingEntity.Equals(linkedLogicalName, StringComparison.OrdinalIgnoreCase))
        {
            expectedFrom = rel.ReferencingAttribute!;
            expectedTo = rel.ReferencedAttribute!;
            return true;
        }

        return false;
    }

    private static FetchXmlValidationOutcome? ValidateJoinAttributesExist(
        CrmEntityMetadata parentEntity,
        CrmEntityMetadata linkedEntity,
        string fromAttr,
        string toAttr)
    {
        if (!parentEntity.TryGetAttribute(toAttr, out _) &&
            !linkedEntity.TryGetAttribute(toAttr, out _))
        {
            var toOk = toAttr.Equals(parentEntity.PrimaryIdAttribute, StringComparison.OrdinalIgnoreCase) ||
                       toAttr.Equals(linkedEntity.PrimaryIdAttribute, StringComparison.OrdinalIgnoreCase);
            var fromOk = fromAttr.Equals(parentEntity.PrimaryIdAttribute, StringComparison.OrdinalIgnoreCase) ||
                         fromAttr.Equals(linkedEntity.PrimaryIdAttribute, StringComparison.OrdinalIgnoreCase) ||
                         parentEntity.TryGetAttribute(fromAttr, out _) ||
                         linkedEntity.TryGetAttribute(fromAttr, out _);
            if (!toOk || !fromOk)
            {
                return FetchXmlValidationOutcome.Fail(
                    "FetchXmlBuild",
                    "INVALID_RELATIONSHIP",
                    $"link-entity join attributes from='{fromAttr}' to='{toAttr}' are invalid.",
                    entity: linkedEntity.LogicalName);
            }
        }

        return null;
    }

    private async Task<FetchXmlValidationOutcome?> ValidateFilterAsync(
        XElement filterEl,
        CrmEntityMetadata entity,
        IReadOnlyDictionary<string, CrmEntityMetadata> aliasMap,
        CancellationToken cancellationToken)
    {
        var type = (string?)filterEl.Attribute("type");
        if (!string.IsNullOrWhiteSpace(type) &&
            !type.Equals("and", StringComparison.OrdinalIgnoreCase) &&
            !type.Equals("or", StringComparison.OrdinalIgnoreCase))
        {
            return FetchXmlValidationOutcome.Fail(
                "FetchXmlBuild",
                "UNSUPPORTED_FETCHXML",
                $"Unsupported filter type '{type}'.");
        }

        foreach (var child in filterEl.Elements())
        {
            var name = child.Name.LocalName;
            if (name.Equals("filter", StringComparison.OrdinalIgnoreCase))
            {
                var nested = await ValidateFilterAsync(child, entity, aliasMap, cancellationToken);
                if (nested is not null)
                {
                    return nested;
                }

                continue;
            }

            if (!name.Equals("condition", StringComparison.OrdinalIgnoreCase))
            {
                return FetchXmlValidationOutcome.Fail(
                    "FetchXmlBuild",
                    "UNSUPPORTED_FETCHXML",
                    $"Unsupported element <{name}> inside filter.");
            }

            var op = (string?)child.Attribute("operator");
            if (string.IsNullOrWhiteSpace(op) || !AllowedOperators.Contains(op))
            {
                return FetchXmlValidationOutcome.Fail(
                    "FetchXmlBuild",
                    "INVALID_FILTER",
                    $"Unsupported or missing condition operator '{op}'.");
            }

            var attribute = (string?)child.Attribute("attribute");
            if (string.IsNullOrWhiteSpace(attribute))
            {
                return FetchXmlValidationOutcome.Fail(
                    "FetchXmlBuild", "INVALID_FILTER", "condition attribute is required.");
            }

            var entityname = (string?)child.Attribute("entityname");
            CrmEntityMetadata target = entity;
            if (!string.IsNullOrWhiteSpace(entityname) &&
                aliasMap.TryGetValue(entityname, out var aliased))
            {
                target = aliased;
            }

            if (!target.TryGetAttribute(attribute, out var attr) || !attr.IsValidForRead)
            {
                return FetchXmlValidationOutcome.Fail(
                    "FetchXmlBuild",
                    "INVALID_FILTER",
                    $"Filter attribute '{attribute}' is not approved/readable on '{target.LogicalName}'.",
                    field: attribute,
                    entity: target.LogicalName);
            }

            // Null operators must not carry a value attribute.
            if ((op.Equals("null", StringComparison.OrdinalIgnoreCase) ||
                 op.Equals("not-null", StringComparison.OrdinalIgnoreCase)) &&
                child.Attribute("value") is not null)
            {
                return FetchXmlValidationOutcome.Fail(
                    "FetchXmlBuild",
                    "INVALID_FILTER",
                    $"Operator '{op}' must not include a value attribute.",
                    field: attribute);
            }

            if (RequiresConditionValue(op))
            {
                var valueError = await ValidateConditionValuesAsync(
                    child,
                    target,
                    attr,
                    op,
                    cancellationToken);
                if (valueError is not null)
                {
                    return valueError;
                }
            }
        }

        return null;
    }

    private static bool RequiresConditionValue(string op) =>
        !op.Equals("null", StringComparison.OrdinalIgnoreCase) &&
        !op.Equals("not-null", StringComparison.OrdinalIgnoreCase) &&
        !op.Equals("yesterday", StringComparison.OrdinalIgnoreCase) &&
        !op.Equals("today", StringComparison.OrdinalIgnoreCase) &&
        !op.Equals("tomorrow", StringComparison.OrdinalIgnoreCase) &&
        !op.Equals("last-seven-days", StringComparison.OrdinalIgnoreCase) &&
        !op.Equals("next-seven-days", StringComparison.OrdinalIgnoreCase) &&
        !op.Equals("last-week", StringComparison.OrdinalIgnoreCase) &&
        !op.Equals("this-week", StringComparison.OrdinalIgnoreCase) &&
        !op.Equals("next-week", StringComparison.OrdinalIgnoreCase) &&
        !op.Equals("last-month", StringComparison.OrdinalIgnoreCase) &&
        !op.Equals("this-month", StringComparison.OrdinalIgnoreCase) &&
        !op.Equals("next-month", StringComparison.OrdinalIgnoreCase) &&
        !op.Equals("last-year", StringComparison.OrdinalIgnoreCase) &&
        !op.Equals("this-year", StringComparison.OrdinalIgnoreCase) &&
        !op.Equals("next-year", StringComparison.OrdinalIgnoreCase) &&
        !op.Equals("eq-userid", StringComparison.OrdinalIgnoreCase) &&
        !op.Equals("ne-userid", StringComparison.OrdinalIgnoreCase) &&
        !op.Equals("eq-userteams", StringComparison.OrdinalIgnoreCase) &&
        !op.Equals("eq-useroruserteams", StringComparison.OrdinalIgnoreCase) &&
        !op.Equals("eq-businessid", StringComparison.OrdinalIgnoreCase) &&
        !op.Equals("ne-businessid", StringComparison.OrdinalIgnoreCase) &&
        !op.Equals("eq-userlanguage", StringComparison.OrdinalIgnoreCase);

    private async Task<FetchXmlValidationOutcome?> ValidateConditionValuesAsync(
        XElement condition,
        CrmEntityMetadata entity,
        CrmAttributeMetadata attr,
        string op,
        CancellationToken cancellationToken)
    {
        var rawValues = CollectConditionValues(condition);
        if (rawValues.Count == 0)
        {
            // Some operators may omit value in edge cases; leave to Dynamics if not choice-typed.
            if (!DynamicsCrmMetadataService.IsChoiceAttributeKind(attr.Kind))
            {
                return null;
            }

            return FetchXmlValidationOutcome.Fail(
                "FetchXmlBuild",
                "INVALID_FILTER",
                $"Choice attribute '{attr.LogicalName}' on '{entity.LogicalName}' requires a numeric option value.",
                field: attr.LogicalName,
                entity: entity.LogicalName);
        }

        if (!DynamicsCrmMetadataService.IsChoiceAttributeKind(attr.Kind))
        {
            // Preserve existing behavior for string/number/date/lookup — no OptionSet coercion.
            return null;
        }

        var load = await _metadata.EnsureAttributeOptionsAsync(
            entity.LogicalName,
            attr.LogicalName,
            cancellationToken);
        var enriched = load.Attribute ?? attr;

        foreach (var raw in rawValues)
        {
            var choiceError = ValidateChoiceFilterValue(entity, enriched, raw, op);
            if (choiceError is not null)
            {
                return choiceError;
            }
        }

        return null;
    }

    private static IReadOnlyList<string> CollectConditionValues(XElement condition)
    {
        var values = new List<string>();
        var attrValue = (string?)condition.Attribute("value");
        if (!string.IsNullOrWhiteSpace(attrValue))
        {
            values.Add(attrValue.Trim());
        }

        foreach (var valueEl in condition.Elements().Where(e =>
                     e.Name.LocalName.Equals("value", StringComparison.OrdinalIgnoreCase)))
        {
            var text = (valueEl.Value ?? string.Empty).Trim();
            if (text.Length > 0)
            {
                values.Add(text);
            }
        }

        return values;
    }

    /// <summary>
    /// Rejects display labels and unknown codes for choice fields. Does not silently rewrite values.
    /// </summary>
    internal static FetchXmlValidationOutcome? ValidateChoiceFilterValue(
        CrmEntityMetadata entity,
        CrmAttributeMetadata attr,
        string rawValue,
        string operatorName)
    {
        _ = operatorName;
        if (string.IsNullOrWhiteSpace(rawValue))
        {
            return FetchXmlValidationOutcome.Fail(
                "FetchXmlBuild",
                "INVALID_FILTER",
                $"Empty value is not allowed for choice attribute '{attr.LogicalName}'.",
                field: attr.LogicalName,
                entity: entity.LogicalName);
        }

        var trimmed = rawValue.Trim();

        // Accept valid numeric option codes when OptionSet metadata is known.
        if (int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var code))
        {
            if (attr.OptionSetByValue.Count > 0 && !attr.OptionSetByValue.ContainsKey(code))
            {
                return FetchXmlValidationOutcome.Fail(
                    "FetchXmlBuild",
                    "INVALID_FILTER",
                    $"Value '{code}' is not a valid option for '{entity.LogicalName}.{attr.LogicalName}'. " +
                    FormatKnownOptionsHint(attr),
                    field: attr.LogicalName,
                    entity: entity.LogicalName);
            }

            return null;
        }

        // Boolean convenience: true/false when attribute is Boolean (still numeric in FetchXML preferred).
        if (attr.Kind == CrmAttributeKind.Boolean &&
            bool.TryParse(trimmed, out var _))
        {
            return FetchXmlValidationOutcome.Fail(
                "FetchXmlBuild",
                "INVALID_FILTER",
                $"Boolean attribute '{attr.LogicalName}' requires numeric option value 0 or 1 in FetchXML " +
                $"(not '{trimmed}'). Call discover_crm_schema for options, then retry with the numeric value.",
                field: attr.LogicalName,
                entity: entity.LogicalName);
        }

        // Unresolved text label — never invent a numeric mapping here.
        var hint = attr.OptionSetByLabel.Count > 0
            ? FormatKnownOptionsHint(attr)
            : "Call discover_crm_schema for this attribute's options, then use the numeric value.";

        return FetchXmlValidationOutcome.Fail(
            "FetchXmlBuild",
            "INVALID_FILTER",
            $"Choice attribute '{entity.LogicalName}.{attr.LogicalName}' requires a numeric OptionSet value; " +
            $"display label '{TruncateDiag(trimmed, 64)}' is not accepted in FetchXML. {hint}",
            field: attr.LogicalName,
            entity: entity.LogicalName);
    }

    private static string FormatKnownOptionsHint(CrmAttributeMetadata attr)
    {
        var samples = attr.OptionSetByValue
            .OrderBy(kv => kv.Key)
            .Take(8)
            .Select(kv => $"{kv.Key}={kv.Value}");
        return "Known options: " + string.Join(", ", samples) +
               (attr.OptionSetByValue.Count > 8 ? ", …" : ".") +
               " Use the numeric value in the FetchXML condition.";
    }

    private static string TruncateDiag(string value, int max) =>
        value.Length <= max ? value : value[..max] + "…";

    private static FetchXmlValidationOutcome? ValidateElementTree(XElement element, int depth)
    {
        if (depth > 40)
        {
            return FetchXmlValidationOutcome.Fail("Parse", "QUERY_TOO_LARGE", "FetchXML nesting is too deep.");
        }

        if (!AllowedElements.Contains(element.Name.LocalName))
        {
            return FetchXmlValidationOutcome.Fail(
                "Parse",
                "UNSUPPORTED_FETCHXML",
                $"Unsupported FetchXML element <{element.Name.LocalName}>.");
        }

        if (!string.IsNullOrEmpty(element.Name.NamespaceName))
        {
            return FetchXmlValidationOutcome.Fail("Parse", "INVALID_XML", "Namespaced FetchXML is not allowed.");
        }

        var allowedAttrs = element.Name.LocalName.ToLowerInvariant() switch
        {
            "fetch" => AllowedFetchAttrs,
            "entity" => AllowedEntityAttrs,
            "attribute" => AllowedAttributeAttrs,
            "filter" => AllowedFilterAttrs,
            "condition" => AllowedConditionAttrs,
            "link-entity" => AllowedLinkAttrs,
            "order" => AllowedOrderAttrs,
            "value" => new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "uiname", "uitype" },
            _ => new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        };

        foreach (var attr in element.Attributes())
        {
            if (!string.IsNullOrEmpty(attr.Name.NamespaceName) ||
                attr.Name.LocalName.Contains(':') ||
                attr.Name.LocalName.StartsWith("xmlns", StringComparison.OrdinalIgnoreCase))
            {
                return FetchXmlValidationOutcome.Fail(
                    "Parse", "INVALID_XML", "XML namespaces and prefixed attributes are not allowed.");
            }

            if (!allowedAttrs.Contains(attr.Name.LocalName))
            {
                return FetchXmlValidationOutcome.Fail(
                    "Parse",
                    "UNSUPPORTED_FETCHXML",
                    $"Unsupported attribute '{attr.Name.LocalName}' on <{element.Name.LocalName}>.");
            }
        }

        foreach (var child in element.Elements())
        {
            var childError = ValidateElementTree(child, depth + 1);
            if (childError is not null)
            {
                return childError;
            }
        }

        return null;
    }

    /// <summary>
    /// Builds UI columns from every projected attribute (root + linked), resolving labels from
    /// owning-entity CRM metadata. Aggregate/link aliases are preserved for zero-result schemas.
    /// </summary>
    private static IReadOnlyList<CrmTableColumn> CollectProjectedColumns(
        XElement entityEl,
        CrmEntityMetadata rootEntity,
        IReadOnlyDictionary<string, CrmEntityMetadata> aliasMap,
        bool isAggregate,
        out bool isCountOnly)
    {
        isCountOnly = false;
        var cols = new List<CrmTableColumn>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var rootAttrs = entityEl.Elements()
            .Where(e => e.Name.LocalName.Equals("attribute", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (isAggregate &&
            rootAttrs.Count == 1 &&
            string.Equals((string?)rootAttrs[0].Attribute("aggregate"), "count", StringComparison.OrdinalIgnoreCase) &&
            !rootAttrs.Any(a => IsTrue(a.Attribute("groupby"))))
        {
            isCountOnly = true;
        }

        void AddFromScope(XElement scopeEl, CrmEntityMetadata owningEntity, string? linkAlias)
        {
            foreach (var attrEl in scopeEl.Elements().Where(e =>
                         e.Name.LocalName.Equals("attribute", StringComparison.OrdinalIgnoreCase)))
            {
                var logical = (string?)attrEl.Attribute("name") ?? "";
                var alias = (string?)attrEl.Attribute("alias");
                var key = !string.IsNullOrWhiteSpace(alias)
                    ? alias!
                    : (!string.IsNullOrWhiteSpace(linkAlias) ? $"{linkAlias}.{logical}" : logical);
                if (string.IsNullOrWhiteSpace(key) || !seen.Add(key))
                {
                    continue;
                }

                owningEntity.TryGetAttribute(logical, out var meta);
                cols.Add(BuildProjectedColumn(key, logical, attrEl, meta));
            }

            foreach (var link in scopeEl.Elements().Where(e =>
                         e.Name.LocalName.Equals("link-entity", StringComparison.OrdinalIgnoreCase)))
            {
                var childAlias = (string?)link.Attribute("alias");
                CrmEntityMetadata linkedMeta = owningEntity;
                if (!string.IsNullOrWhiteSpace(childAlias) &&
                    aliasMap.TryGetValue(childAlias, out var mapped))
                {
                    linkedMeta = mapped;
                }
                else
                {
                    var linkName = (string?)link.Attribute("name");
                    if (!string.IsNullOrWhiteSpace(linkName) &&
                        aliasMap.Values.FirstOrDefault(e =>
                            e.LogicalName.Equals(linkName, StringComparison.OrdinalIgnoreCase)) is { } byName)
                    {
                        linkedMeta = byName;
                    }
                }

                AddFromScope(link, linkedMeta, childAlias);
            }
        }

        AddFromScope(entityEl, rootEntity, linkAlias: null);
        return cols;
    }

    private static CrmTableColumn BuildProjectedColumn(
        string key,
        string logical,
        XElement attrEl,
        CrmAttributeMetadata? meta)
    {
        var agg = (string?)attrEl.Attribute("aggregate");
        var dg = (string?)attrEl.Attribute("dategrouping");
        var display = !string.IsNullOrWhiteSpace(meta?.DisplayName) ? meta!.DisplayName!.Trim() : null;
        var kind = "string";
        var label = display ?? key;

        if (!string.IsNullOrWhiteSpace(dg))
        {
            if (dg.Equals("year", StringComparison.OrdinalIgnoreCase))
            {
                kind = "year";
                label = display is null ? "Year" : $"{display} (Year)";
            }
            else if (dg.Equals("month", StringComparison.OrdinalIgnoreCase))
            {
                kind = "month";
                label = display is null ? "Month" : $"{display} (Month)";
            }
            else if (dg.Equals("day", StringComparison.OrdinalIgnoreCase))
            {
                kind = "day";
                label = display is null ? "Day" : $"{display} (Day)";
            }
            else if (dg.Equals("quarter", StringComparison.OrdinalIgnoreCase))
            {
                kind = "quarter";
                label = display is null ? "Quarter" : $"{display} (Quarter)";
            }
            else if (dg.Equals("week", StringComparison.OrdinalIgnoreCase))
            {
                kind = "week";
                label = display is null ? "Week" : $"{display} (Week)";
            }
        }
        else if (!string.IsNullOrWhiteSpace(agg))
        {
            var aggLower = agg.Trim().ToLowerInvariant();
            kind = aggLower is "count" or "countcolumn"
                ? "integer"
                : meta?.Kind == CrmAttributeKind.Money ? "money" : "decimal";

            label = aggLower switch
            {
                "count" or "countcolumn" => display is null ? "Count" : $"{display} (Count)",
                "sum" => display is null ? "Sum" : $"{display} (Sum)",
                "avg" => display is null ? "Average" : $"{display} (Average)",
                "min" => display is null ? "Min" : $"{display} (Min)",
                "max" => display is null ? "Max" : $"{display} (Max)",
                _ => display ?? CultureInfo.InvariantCulture.TextInfo.ToTitleCase(key.Replace('_', ' '))
            };
        }
        else if (meta is not null)
        {
            kind = meta.Kind.ToString().ToLowerInvariant();
            label = display ?? key;
        }

        return new CrmTableColumn
        {
            Key = key,
            Label = label,
            Kind = kind,
            LogicalName = logical
        };
    }

    private static XDocument ParseSecure(string xml)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            IgnoreComments = true,
            IgnoreProcessingInstructions = true,
            MaxCharactersFromEntities = 0,
            MaxCharactersInDocument = FetchXmlSafetyLimits.MaxXmlCharacters
        };

        using var reader = XmlReader.Create(new StringReader(xml), settings);
        var doc = XDocument.Load(reader, LoadOptions.None);
        if (doc.DocumentType is not null)
        {
            throw new InvalidOperationException("DTD is not allowed.");
        }

        return doc;
    }

    private static bool IsTrue(XAttribute? attr) =>
        attr is not null &&
        (attr.Value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
         attr.Value.Equals("1", StringComparison.OrdinalIgnoreCase));

    private static int? ParseOptionalInt(XAttribute? attr)
    {
        if (attr is null || string.IsNullOrWhiteSpace(attr.Value))
        {
            return null;
        }

        return int.TryParse(attr.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)
            ? n
            : null;
    }
}
