namespace CrmAiPoc.Api.Security;

/// <summary>
/// Explicit allowlist of CRM entities and fields the POC may read.
/// salesorder fields are limited to verified/safe defaults until schema discovery updates them at runtime.
/// </summary>
public static class CrmEntityAllowlist
{
    public const string Account = "account";
    public const string Contact = "contact";
    public const string SalesOrder = "salesorder";

    /// <summary>Previously discovered custom lookup logical name (Web API uses _te_customeraccountid_value).</summary>
    public const string SalesOrderCustomerAccountLookup = "te_customeraccountid";

    private static readonly Dictionary<string, HashSet<string>> AllowedFields = new(StringComparer.OrdinalIgnoreCase)
    {
        [Account] =
        [
            "accountid",
            "name",
            "accountnumber",
            "telephone1",
            "emailaddress1",
            "address1_city",
            "address1_country",
            "industrycode"
        ],
        [Contact] =
        [
            "contactid",
            "parentcustomerid",
            "fullname",
            "emailaddress1",
            "telephone1",
            "jobtitle"
        ],
        // Verified ECRMBSS Phase 4 defaults; runtime discovery may refine via ReplaceSalesOrderFields.
        [SalesOrder] =
        [
            "salesorderid",
            "name",
            "ordernumber",
            "createdon",
            "modifiedon",
            "totalamount",
            "statuscode",
            "statecode",
            "_customerid_value",
            "_te_customeraccountid_value"
        ]
    };

    public static bool IsEntityAllowed(string entityLogicalName) =>
        AllowedFields.ContainsKey(entityLogicalName);

    public static bool IsFieldAllowed(string entityLogicalName, string fieldLogicalName) =>
        AllowedFields.TryGetValue(entityLogicalName, out var fields) &&
        fields.Contains(fieldLogicalName);

    public static IReadOnlyCollection<string> GetAllowedFields(string entityLogicalName) =>
        AllowedFields.TryGetValue(entityLogicalName, out var fields)
            ? fields.ToArray()
            : Array.Empty<string>();

    public static IReadOnlyCollection<string> GetAllowedEntities() => AllowedFields.Keys.ToArray();

    /// <summary>
    /// Replaces salesorder allowlisted fields with verified discovery results (intersection of recommended + known-safe).
    /// </summary>
    public static void ReplaceSalesOrderFields(IEnumerable<string> verifiedFields)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var field in verifiedFields)
        {
            if (!string.IsNullOrWhiteSpace(field))
            {
                set.Add(field.Trim());
            }
        }

        if (set.Count == 0)
        {
            return;
        }

        AllowedFields[SalesOrder] = set;
    }
}
