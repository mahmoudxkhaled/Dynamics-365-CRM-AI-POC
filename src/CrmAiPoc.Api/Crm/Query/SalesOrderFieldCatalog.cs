namespace CrmAiPoc.Api.Crm.Query;

/// <summary>
/// Verified salesorder field allowlist from ECRMBSS EntityDefinitions (Phase 4).
/// Only these logical/Web API names may appear in query plans or $select/$filter/$orderby.
/// </summary>
public static class SalesOrderFieldCatalog
{
    public const string EntityLogicalName = "salesorder";
    public const string EntitySetName = "salesorders";
    public const string PrimaryId = "salesorderid";
    public const string PrimaryName = "name";

    private static readonly Dictionary<string, SalesOrderFieldDefinition> Fields =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["salesorderid"] = new(
                "salesorderid",
                SalesOrderFieldType.Guid,
                Filterable: true,
                Sortable: false,
                Searchable: false,
                Selectable: true,
                [SalesOrderFilterOperator.Eq, SalesOrderFilterOperator.Ne]),

            ["name"] = new(
                "name",
                SalesOrderFieldType.String,
                Filterable: true,
                Sortable: false,
                Searchable: true,
                Selectable: true,
                [
                    SalesOrderFilterOperator.Eq,
                    SalesOrderFilterOperator.Ne,
                    SalesOrderFilterOperator.Contains
                ]),

            ["ordernumber"] = new(
                "ordernumber",
                SalesOrderFieldType.String,
                Filterable: true,
                Sortable: false,
                Searchable: true,
                Selectable: true,
                [
                    SalesOrderFilterOperator.Eq,
                    SalesOrderFilterOperator.Ne,
                    SalesOrderFilterOperator.Contains
                ]),

            ["createdon"] = new(
                "createdon",
                SalesOrderFieldType.DateTime,
                Filterable: true,
                Sortable: true,
                Searchable: false,
                Selectable: true,
                [
                    SalesOrderFilterOperator.Eq,
                    SalesOrderFilterOperator.Ne,
                    SalesOrderFilterOperator.Gt,
                    SalesOrderFilterOperator.Ge,
                    SalesOrderFilterOperator.Lt,
                    SalesOrderFilterOperator.Le
                ]),

            ["modifiedon"] = new(
                "modifiedon",
                SalesOrderFieldType.DateTime,
                Filterable: true,
                Sortable: false,
                Searchable: false,
                Selectable: true,
                [
                    SalesOrderFilterOperator.Eq,
                    SalesOrderFilterOperator.Ne,
                    SalesOrderFilterOperator.Gt,
                    SalesOrderFilterOperator.Ge,
                    SalesOrderFilterOperator.Lt,
                    SalesOrderFilterOperator.Le
                ]),

            ["totalamount"] = new(
                "totalamount",
                SalesOrderFieldType.Money,
                Filterable: true,
                Sortable: true,
                Searchable: false,
                Selectable: true,
                [
                    SalesOrderFilterOperator.Eq,
                    SalesOrderFilterOperator.Ne,
                    SalesOrderFilterOperator.Gt,
                    SalesOrderFilterOperator.Ge,
                    SalesOrderFilterOperator.Lt,
                    SalesOrderFilterOperator.Le
                ]),

            ["statuscode"] = new(
                "statuscode",
                SalesOrderFieldType.Status,
                Filterable: true,
                Sortable: false,
                Searchable: false,
                Selectable: true,
                [SalesOrderFilterOperator.Eq, SalesOrderFilterOperator.Ne]),

            ["statecode"] = new(
                "statecode",
                SalesOrderFieldType.State,
                Filterable: true,
                Sortable: false,
                Searchable: false,
                Selectable: true,
                [SalesOrderFilterOperator.Eq, SalesOrderFilterOperator.Ne]),

            // Lookup Web API properties (GUID filter only — no related-name search).
            ["_te_customeraccountid_value"] = new(
                "_te_customeraccountid_value",
                SalesOrderFieldType.LookupGuid,
                Filterable: true,
                Sortable: false,
                Searchable: false,
                Selectable: true,
                [SalesOrderFilterOperator.Eq, SalesOrderFilterOperator.Ne],
                Aliases: ["te_customeraccountid"]),

            ["_customerid_value"] = new(
                "_customerid_value",
                SalesOrderFieldType.LookupGuid,
                Filterable: true,
                Sortable: false,
                Searchable: false,
                Selectable: true,
                [SalesOrderFilterOperator.Eq, SalesOrderFilterOperator.Ne],
                Aliases: ["customerid"])
        };

    /// <summary>Verified OptionSet values from StatusAttributeMetadata (ECRMBSS).</summary>
    private static readonly Dictionary<string, int> StatusCodeByLabel =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["New"] = 1,
            ["Pending"] = 2,
            ["In Progress"] = 3,
            ["No Money"] = 4,
            ["Complete"] = 100001,
            ["Partial"] = 100002,
            ["Invoiced"] = 100003
        };

    private static readonly Dictionary<int, string> StatusLabelByCode =
        StatusCodeByLabel.ToDictionary(kv => kv.Value, kv => kv.Key);

    /// <summary>Verified OptionSet values from StateAttributeMetadata (ECRMBSS).</summary>
    private static readonly Dictionary<string, int> StateCodeByLabel =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["Active"] = 0,
            ["Submitted"] = 1,
            ["Canceled"] = 2,
            ["Fulfilled"] = 3,
            ["Invoiced"] = 4
        };

    private static readonly Dictionary<int, string> StateLabelByCode =
        StateCodeByLabel.ToDictionary(kv => kv.Value, kv => kv.Key);

    public static IReadOnlyCollection<string> SelectFields { get; } =
    [
        "salesorderid",
        "name",
        "ordernumber",
        "createdon",
        "modifiedon",
        "totalamount",
        "statuscode",
        "statecode",
        "_te_customeraccountid_value",
        "_customerid_value"
    ];

    public static IReadOnlyCollection<string> SortableFields { get; } =
        Fields.Values.Where(f => f.Sortable).Select(f => f.LogicalName).ToArray();

    public static bool TryResolveField(string? nameOrAlias, out SalesOrderFieldDefinition definition)
    {
        definition = null!;
        if (string.IsNullOrWhiteSpace(nameOrAlias))
        {
            return false;
        }

        var key = nameOrAlias.Trim();
        if (Fields.TryGetValue(key, out definition!))
        {
            return true;
        }

        foreach (var field in Fields.Values)
        {
            if (field.Aliases.Any(a => a.Equals(key, StringComparison.OrdinalIgnoreCase)))
            {
                definition = field;
                return true;
            }
        }

        return false;
    }

    public static bool IsOperatorAllowed(SalesOrderFieldDefinition field, SalesOrderFilterOperator op) =>
        field.AllowedOperators.Contains(op);

    public static bool TryResolveStatusCode(string value, out int code)
    {
        if (int.TryParse(value.Trim(), out code) && StatusLabelByCode.ContainsKey(code))
        {
            return true;
        }

        return StatusCodeByLabel.TryGetValue(value.Trim(), out code);
    }

    public static bool TryResolveStateCode(string value, out int code)
    {
        if (int.TryParse(value.Trim(), out code) && StateLabelByCode.ContainsKey(code))
        {
            return true;
        }

        return StateCodeByLabel.TryGetValue(value.Trim(), out code);
    }

    public static string? GetStatusLabel(int? code) =>
        code is int c && StatusLabelByCode.TryGetValue(c, out var label) ? label : null;

    public static string? GetStateLabel(int? code) =>
        code is int c && StateLabelByCode.TryGetValue(c, out var label) ? label : null;

    public static string DescribeForAiTool() =>
        """
        Approved salesorder fields only:
        - name, ordernumber (text; contains/eq/ne)
        - createdon, modifiedon (dates; ge/gt/lt/le/eq/ne; prefer relativeDate tokens)
        - totalamount (money; comparison operators)
        - statuscode (New, Pending, In Progress, No Money, Complete, Partial, Invoiced)
        - statecode (Active, Submitted, Canceled, Fulfilled, Invoiced) — use statecode for "Active"
        - te_customeraccountid / _te_customeraccountid_value (GUID only)
        - customerid / _customerid_value (GUID only)
        Sortable: createdon, totalamount. Max 3 filters, limit 1-50, entity salesorder only.
        """;
}

public sealed record SalesOrderFieldDefinition(
    string LogicalName,
    SalesOrderFieldType FieldType,
    bool Filterable,
    bool Sortable,
    bool Searchable,
    bool Selectable,
    IReadOnlyList<SalesOrderFilterOperator> AllowedOperators,
    IReadOnlyList<string>? Aliases = null)
{
    public IReadOnlyList<string> Aliases { get; } = Aliases ?? Array.Empty<string>();
}
