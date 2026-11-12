using System.Globalization;
using System.Xml.Linq;
using CrmAiPoc.Api.Crm.Metadata;
using CrmAiPoc.Api.Crm.Query;

namespace CrmAiPoc.Api.Crm.Generic;

/// <summary>
/// Shared FetchXML condition/filter helpers for list, count, and aggregate builders.
/// Never accepts raw FetchXML from the model.
/// </summary>
public static class FetchXmlConditionBuilder
{
    public static string ToFetchAttributeName(string logicalOrOData)
    {
        var name = logicalOrOData.Trim();
        if (name.StartsWith('_') && name.EndsWith("_value", StringComparison.OrdinalIgnoreCase) && name.Length > 7)
        {
            return name[1..^6];
        }

        return name;
    }

    public static string EncodeFetchUri(string entitySetName, string fetchXml) =>
        $"{entitySetName}?fetchXml={Uri.EscapeDataString(fetchXml)}";

    public static string FormatFetchDate(DateTimeOffset dto) =>
        dto.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);

    public static string EscapeFetchValue(string value) =>
        value.Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("\"", "&quot;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal)
            .Replace("'", "&apos;", StringComparison.Ordinal);

    public static bool ContainsInjectionAttempt(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var lower = value.ToLowerInvariant();
        return lower.Contains("<fetch", StringComparison.Ordinal) ||
               lower.Contains("fetchxml", StringComparison.Ordinal) ||
               lower.Contains("$filter", StringComparison.Ordinal) ||
               lower.Contains("$select", StringComparison.Ordinal) ||
               lower.Contains("$orderby", StringComparison.Ordinal) ||
               lower.Contains("$expand", StringComparison.Ordinal) ||
               lower.Contains(" select ", StringComparison.Ordinal) ||
               lower.Contains(" drop ", StringComparison.Ordinal) ||
               lower.Contains("--", StringComparison.Ordinal);
    }

    public static string MapCompareOp(CrmFilterOperator op) => op switch
    {
        CrmFilterOperator.Eq => "eq",
        CrmFilterOperator.Ne => "ne",
        CrmFilterOperator.Gt => "gt",
        CrmFilterOperator.Ge => "ge",
        CrmFilterOperator.Lt => "lt",
        CrmFilterOperator.Le => "le",
        CrmFilterOperator.Null => "null",
        CrmFilterOperator.NotNull => "not-null",
        _ => "eq"
    };

    /// <summary>
    /// Builds a FetchXML condition element. Valueless for null/not-null.
    /// Optional <paramref name="entityNameAlias"/> emits post-join <c>entityname</c>.
    /// </summary>
    public static XElement CreateConditionElement(
        string attributeLogicalName,
        string fetchOperator,
        string? value = null,
        string? entityNameAlias = null)
    {
        var condition = new XElement(
            "condition",
            new XAttribute("attribute", attributeLogicalName),
            new XAttribute("operator", fetchOperator));

        if (!string.IsNullOrWhiteSpace(entityNameAlias))
        {
            condition.Add(new XAttribute("entityname", entityNameAlias));
        }

        if (!string.IsNullOrEmpty(value) &&
            !fetchOperator.Equals("null", StringComparison.OrdinalIgnoreCase) &&
            !fetchOperator.Equals("not-null", StringComparison.OrdinalIgnoreCase))
        {
            condition.Add(new XAttribute("value", value));
        }

        return condition;
    }

    public static (bool Success, string? Error, List<XElement>? Conditions, string? Summary) BuildRelativeDate(
        CrmEntityMetadata entity,
        CrmRelativeDateFilter relative,
        DateTimeOffset utcNow,
        TimeZoneInfo businessTz)
    {
        if (!entity.TryGetAttribute(relative.Field, out var field) || field.Kind != CrmAttributeKind.DateTime)
        {
            return (false,
                $"Relative date field '{relative.Field}' is not an approved date field on {entity.LogicalName}.",
                null,
                null);
        }

        ResolvedDateRange resolved;
        try
        {
            resolved = DateRangeResolver.Resolve(relative.GetExpression(), utcNow, businessTz);
        }
        catch (ArgumentException ex)
        {
            return (false, ex.Message, null, null);
        }

        var conditions = new List<XElement>
        {
            new(
                "condition",
                new XAttribute("attribute", field.LogicalName),
                new XAttribute("operator", "ge"),
                new XAttribute("value", FormatFetchDate(resolved.StartUtc))),
            new(
                "condition",
                new XAttribute("attribute", field.LogicalName),
                new XAttribute("operator", "lt"),
                new XAttribute("value", FormatFetchDate(resolved.EndUtcExclusive)))
        };

        return (true, null, conditions,
            $"{field.LogicalName} in {resolved.Description} [{resolved.StartUtc:u} .. {resolved.EndUtcExclusive:u})");
    }

    public static (bool Success, string? Error, XElement? Condition, string? Summary) BuildFilter(
        CrmEntityMetadata entity,
        CrmQueryFilter filter,
        string? entityNameAlias = null)
    {
        if (string.IsNullOrWhiteSpace(filter.Field))
        {
            return (false, "Each filter requires field.", null, null);
        }

        if (!entity.TryGetAttribute(filter.Field, out var field) || !field.IsFilterable)
        {
            return (false, $"Field '{filter.Field}' is not allowlisted/filterable on '{entity.LogicalName}'.", null, null);
        }

        if (!field.AllowedOperators.Contains(filter.Operator))
        {
            return (false, $"Operator '{filter.Operator}' is not allowed for '{field.LogicalName}'.", null, null);
        }

        if (CrmQueryFilter.IsValuelessOperator(filter.Operator))
        {
            if (!string.IsNullOrWhiteSpace(filter.Value))
            {
                return (false,
                    $"Operator '{filter.Operator}' must not include a value (field '{field.LogicalName}').",
                    null,
                    null);
            }

            var nullOp = MapCompareOp(filter.Operator);
            var nullSummary = string.IsNullOrWhiteSpace(entityNameAlias)
                ? $"{field.LogicalName} {nullOp}"
                : $"{entityNameAlias}.{field.LogicalName} {nullOp} (post-join)";
            return (true, null,
                CreateConditionElement(field.LogicalName, nullOp, entityNameAlias: entityNameAlias),
                nullSummary);
        }

        if (string.IsNullOrWhiteSpace(filter.Value))
        {
            return (false, "Each filter requires field and value.", null, null);
        }

        if (filter.Operator == CrmFilterOperator.Contains && !field.IsSearchable)
        {
            return (false, $"contains is only allowed on searchable text fields, not '{field.LogicalName}'.", null, null);
        }

        if (ContainsInjectionAttempt(filter.Value))
        {
            return (false, "Filter value is invalid or disallowed.", null, null);
        }

        string fetchOp;
        string value;
        string summary;

        switch (field.Kind)
        {
            case CrmAttributeKind.String or CrmAttributeKind.Memo:
                if (filter.Value.Length > 200)
                {
                    return (false, "Text filter value is invalid or disallowed.", null, null);
                }

                // Pass raw text into XAttribute — XElement serializes XML escaping safely.
                if (filter.Operator == CrmFilterOperator.Contains)
                {
                    fetchOp = "like";
                    value = $"%{filter.Value}%";
                }
                else
                {
                    fetchOp = MapCompareOp(filter.Operator);
                    value = filter.Value;
                }

                summary = $"{field.LogicalName} {filter.Operator} '{filter.Value}'";
                break;

            case CrmAttributeKind.Money or CrmAttributeKind.Decimal or CrmAttributeKind.Integer:
                if (!decimal.TryParse(filter.Value.Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out var number))
                {
                    return (false, $"Value for '{field.LogicalName}' must be a number.", null, null);
                }

                fetchOp = MapCompareOp(filter.Operator);
                value = field.Kind == CrmAttributeKind.Integer
                    ? ((long)number).ToString(CultureInfo.InvariantCulture)
                    : number.ToString(CultureInfo.InvariantCulture);
                summary = $"{field.LogicalName} {filter.Operator} {value}";
                break;

            case CrmAttributeKind.DateTime:
                if (!DateTimeOffset.TryParse(filter.Value.Trim(), CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dto))
                {
                    return (false, $"Value for '{field.LogicalName}' must be an ISO date/time or use relativeDate.", null, null);
                }

                fetchOp = MapCompareOp(filter.Operator);
                value = FormatFetchDate(dto);
                summary = $"{field.LogicalName} {filter.Operator} {value}";
                break;

            case CrmAttributeKind.Boolean:
                if (!bool.TryParse(filter.Value.Trim(), out var b))
                {
                    return (false, $"Value for '{field.LogicalName}' must be true or false.", null, null);
                }

                fetchOp = MapCompareOp(filter.Operator);
                value = b ? "1" : "0";
                summary = $"{field.LogicalName} {filter.Operator} {b}";
                break;

            case CrmAttributeKind.Picklist or CrmAttributeKind.Status or CrmAttributeKind.State:
                int code;
                if (int.TryParse(filter.Value.Trim(), out code) && field.OptionSetByValue.ContainsKey(code))
                {
                    // ok
                }
                else if (!field.OptionSetByLabel.TryGetValue(filter.Value.Trim(), out code))
                {
                    return (false, $"Unknown option value '{filter.Value}' for '{field.LogicalName}'.", null, null);
                }

                fetchOp = MapCompareOp(filter.Operator);
                value = code.ToString(CultureInfo.InvariantCulture);
                var label = field.OptionSetByValue.TryGetValue(code, out var l) ? l : value;
                summary = $"{field.LogicalName} {filter.Operator} {code} ({label})";
                break;

            case CrmAttributeKind.Lookup or CrmAttributeKind.Customer or CrmAttributeKind.Owner
                or CrmAttributeKind.Guid or CrmAttributeKind.Uniqueidentifier:
                if (!Guid.TryParse(filter.Value.Trim(), out var id) || id == Guid.Empty)
                {
                    return (false, $"Lookup/GUID filter on '{field.LogicalName}' requires a non-empty GUID.", null, null);
                }

                fetchOp = MapCompareOp(filter.Operator);
                value = id.ToString("D");
                summary = $"{field.LogicalName} {filter.Operator} {value}";
                break;

            default:
                return (false, $"Unsupported field type for '{field.LogicalName}'.", null, null);
        }

        if (!string.IsNullOrWhiteSpace(entityNameAlias))
        {
            summary = $"{entityNameAlias}.{summary} (post-join)";
        }

        return (true, null,
            CreateConditionElement(field.LogicalName, fetchOp, value, entityNameAlias),
            summary);
    }

    public static (bool Success, string? Error, XElement? Condition, string? Summary) BuildRelationshipCondition(
        CrmEntityMetadata entity,
        string? relatedFromEntity,
        string? relatedFromId,
        CrmEntityMetadata? relatedFrom)
    {
        if (string.IsNullOrWhiteSpace(relatedFromEntity) && string.IsNullOrWhiteSpace(relatedFromId))
        {
            return (true, null, null, null);
        }

        if (string.IsNullOrWhiteSpace(relatedFromEntity) || string.IsNullOrWhiteSpace(relatedFromId))
        {
            return (false, "relatedFromEntity and relatedFromId must be provided together.", null, null);
        }

        var hop = CrmRelationshipResolver.ResolveOneHop(entity, relatedFromEntity, relatedFromId, relatedFrom);
        if (!hop.Success)
        {
            return (false, hop.Error!, null, null);
        }

        var attribute = ToFetchAttributeName(hop.FilterField!);
        var condition = new XElement(
            "condition",
            new XAttribute("attribute", attribute),
            new XAttribute("operator", "eq"),
            new XAttribute("value", hop.FilterValue!));
        return (true, null, condition, hop.Summary);
    }
}
