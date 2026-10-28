using CrmAiPoc.Api.Crm.Metadata;

namespace CrmAiPoc.Api.Crm.Generic;

/// <summary>
/// Builds entity-agnostic table columns from validated query $select fields and CRM metadata.
/// </summary>
public static class CrmTableColumnBuilder
{
    public static IReadOnlyList<CrmTableColumn> Build(
        CrmEntityMetadata entity,
        IReadOnlyList<string> selectFields,
        IReadOnlyList<IReadOnlyDictionary<string, object?>>? sampleRows = null)
    {
        if (entity is null || selectFields is null || selectFields.Count == 0)
        {
            return Array.Empty<CrmTableColumn>();
        }

        var columns = new List<CrmTableColumn>();
        var seenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var select in selectFields)
        {
            if (string.IsNullOrWhiteSpace(select))
            {
                continue;
            }

            if (!TryResolveAttribute(entity, select, out var attr))
            {
                // Selected primary id/name may exist in $select without a full attribute entry (stubs).
                if (select.Equals(entity.PrimaryIdAttribute, StringComparison.OrdinalIgnoreCase))
                {
                    continue; // never show primary id as a table column
                }

                if (select.Equals(entity.PrimaryNameAttribute, StringComparison.OrdinalIgnoreCase) &&
                    seenKeys.Add(select))
                {
                    columns.Add(new CrmTableColumn
                    {
                        Key = select,
                        Label = select,
                        Kind = nameof(CrmAttributeKind.String),
                        LogicalName = select
                    });
                }

                continue;
            }

            if (!attr.IsValidForRead)
            {
                continue;
            }

            // Primary GUID columns are useful for $select/filter but not for readable tables.
            if (attr.LogicalName.Equals(entity.PrimaryIdAttribute, StringComparison.OrdinalIgnoreCase) ||
                attr.Kind == CrmAttributeKind.Uniqueidentifier)
            {
                continue;
            }

            var key = ResolveDisplayKey(attr, sampleRows);
            if (!seenKeys.Add(key))
            {
                continue;
            }

            columns.Add(new CrmTableColumn
            {
                Key = key,
                Label = ResolveLabel(attr),
                Kind = attr.Kind.ToString(),
                LogicalName = attr.LogicalName
            });
        }

        return columns;
    }

    private static bool TryResolveAttribute(
        CrmEntityMetadata entity,
        string select,
        out CrmAttributeMetadata attr)
    {
        if (entity.TryGetAttribute(select, out attr))
        {
            return true;
        }

        // $select uses OData names like _customerid_value — match by ODataPropertyName.
        foreach (var candidate in entity.Attributes)
        {
            if (candidate.ODataPropertyName.Equals(select, StringComparison.OrdinalIgnoreCase))
            {
                attr = candidate;
                return true;
            }
        }

        attr = null!;
        return false;
    }

    private static string ResolveLabel(CrmAttributeMetadata attr)
    {
        if (!string.IsNullOrWhiteSpace(attr.DisplayName))
        {
            return attr.DisplayName.Trim();
        }

        return attr.LogicalName;
    }

    private static string ResolveDisplayKey(
        CrmAttributeMetadata attr,
        IReadOnlyList<IReadOnlyDictionary<string, object?>>? sampleRows)
    {
        // Choice fields: always target *_label so empty and non-empty tables share the same headers.
        // Cells fall back to the raw code when a label is absent.
        if (attr.Kind is CrmAttributeKind.Status or CrmAttributeKind.State or CrmAttributeKind.Picklist)
        {
            return attr.LogicalName + "_label";
        }

        // Prefer Dynamics formatted-value companion keys when ParseRows attached them.
        var formattedKey = attr.ODataPropertyName + "_formatted";
        if (SampleHasKey(sampleRows, formattedKey))
        {
            return formattedKey;
        }

        return attr.ODataPropertyName;
    }

    private static bool SampleHasKey(
        IReadOnlyList<IReadOnlyDictionary<string, object?>>? sampleRows,
        string key)
    {
        if (sampleRows is null || sampleRows.Count == 0)
        {
            return false;
        }

        foreach (var row in sampleRows)
        {
            if (row.ContainsKey(key))
            {
                return true;
            }
        }

        return false;
    }
}
