using CrmAiPoc.Api.Crm.Query;

namespace CrmAiPoc.Api.Ai;

/// <summary>
/// Keeps the AI tool description short while referencing the verified allowlist (no full CRM schema).
/// </summary>
internal static class SalesOrderFieldCatalogAiHint
{
    // Attribute arguments must be compile-time constants; keep a compact fixed summary here.
    public const string Text =
        "Approved fields: name, ordernumber, createdon, modifiedon, totalamount, statuscode, statecode, te_customeraccountid (GUID only). " +
        "Active orders use statecode=Active. Status labels: New, Pending, In Progress, No Money, Complete, Partial, Invoiced. " +
        "Sort: createdon|totalamount. Max 3 filters. No joins/other entities.";

    public static string LongDescription => SalesOrderFieldCatalog.DescribeForAiTool();
}
