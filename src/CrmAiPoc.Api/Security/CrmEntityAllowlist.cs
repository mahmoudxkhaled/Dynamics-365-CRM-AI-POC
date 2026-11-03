namespace CrmAiPoc.Api.Security;

/// <summary>Legacy entity names still accepted by auth (ApprovedEntities / policy is authoritative for AI).</summary>
public static class CrmEntityAllowlist
{
    public const string Account = "account";
    public const string Contact = "contact";
    public const string SalesOrder = "salesorder";

    public const string SalesOrderCustomerAccountLookup = "te_customeraccountid";

    private static readonly HashSet<string> AllowedEntities = new(StringComparer.OrdinalIgnoreCase)
    {
        Account,
        Contact,
        SalesOrder
    };

    public static bool IsEntityAllowed(string entityLogicalName) =>
        AllowedEntities.Contains(entityLogicalName);
}
