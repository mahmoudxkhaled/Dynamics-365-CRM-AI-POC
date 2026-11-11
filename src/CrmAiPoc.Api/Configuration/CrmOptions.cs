namespace CrmAiPoc.Api.Configuration;

public sealed class CrmOptions
{
    public const string SectionName = "Crm";

    /// <summary>Must be Dynamics365 (live CRM only).</summary>
    public string Provider { get; set; } = "Dynamics365";

    /// <summary>Dynamics 365 organization Web API base URL.</summary>
    public string? BaseUrl { get; set; }

    /// <summary>Must be true to enable live Dynamics HTTP calls.</summary>
    public bool EnableLiveIntegration { get; set; }

    /// <summary>When true (and no explicit network credentials), use Windows default credentials.</summary>
    public bool UseDefaultCredentials { get; set; } = true;

    /// <summary>
    /// Optional explicit AD username. Prefer environment variables over config files.
    /// Never hardcode. Empty = use default Windows identity when UseDefaultCredentials is true.
    /// </summary>
    public string? Username { get; set; }

    /// <summary>Optional AD password from secure config/env only. Never logged.</summary>
    public string? Password { get; set; }

    /// <summary>Optional AD domain for NetworkCredential.</summary>
    public string? Domain { get; set; }

    public int DefaultPageSize { get; set; } = 10;
    public int MaxPageSize { get; set; } = 25;

    /// <summary>
    /// IANA or Windows timezone id for calendar relative dates (default Africa/Cairo).
    /// </summary>
    public string BusinessTimeZoneId { get; set; } = "Africa/Cairo";

    /// <summary>
    /// When true, the full approved CRM metadata catalog (entities/fields/relationships only — never records)
    /// is included in the outbound AI context. Default false; enable explicitly for PoC/Dev.
    /// Live CRM rows are never sent to the AI (privacy-safe by design).
    /// </summary>
    public bool AllowSendApprovedMetadataCatalogToAi { get; set; }

    /// <summary>
    /// When true (default), load approved entity metadata from Dynamics in a background hosted service at startup
    /// so chat does not wait on first-request discovery.
    /// </summary>
    public bool WarmApprovedMetadataOnStartup { get; set; } = true;

    /// <summary>
    /// POC safety: when true (default in Development), reject non-loopback callers for chat/API.
    /// Remote multi-user access requires proper incoming authentication first.
    /// </summary>
    public bool RestrictToLoopback { get; set; } = true;

    /// <summary>
    /// Mock authenticated user for authorization demos.
    /// Do not treat client-supplied role names as authenticated identity.
    /// </summary>
    public string MockUserId { get; set; } = "demo-user";

    public string MockUserDisplayName { get; set; } = "Demo CRM User";

    /// <summary>
    /// Logical names approved for AI query_crm access (config-driven; not all discovered entities).
    /// </summary>
    public List<string> ApprovedEntities { get; set; } = ["account", "contact", "salesorder", "lead"];

    /// <summary>
    /// Optional relationship SchemaName allowlist for AI link-entity joins.
    /// Empty (default) = all discovered non-M2M relationships between ApprovedEntities are joinable.
    /// Non-empty = additional security restriction on top of ApprovedEntities.
    /// </summary>
    public List<string> ApprovedRelationships { get; set; } = [];

    /// <summary>
    /// Optional business aliases per entity logical name for natural-language routing hints.
    /// </summary>
    public Dictionary<string, List<string>> EntityAliases { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["account"] =
        [
            "accounts", "customers", "customer account", "customer accounts"
        ],
        ["contact"] =
        [
            "contacts", "contact persons", "primary contact", "primary contacts"
        ],
        ["salesorder"] =
        [
            "sales order", "sales orders", "orders"
        ],
        ["lead"] = ["leads", "prospects"]
    };

    /// <summary>Seconds to cache discovered entity metadata before refresh.</summary>
    public int MetadataCacheSeconds { get; set; } = 3600;

    /// <summary>
    /// When true (default), expired metadata may still be served while a background refresh runs
    /// (stale-while-revalidate). Chat stays fast; live CRM remains authoritative after refresh.
    /// </summary>
    public bool MetadataStaleWhileRevalidate { get; set; } = true;

    /// <summary>
    /// Optional friendly labels for relationship SchemaNames used in clarification UX.
    /// Key = SchemaName, Value = English business description.
    /// </summary>
    public Dictionary<string, string> RelationshipLabels { get; set; } =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["te_account_salesorder_customeraccountid"] = "Customer Account",
            ["order_customer_accounts"] = "Potential Customer (Account)",
            ["te_account_salesorder_branchaccountid"] = "Branch Account",
            ["te_account_salesorder_customerbranchaccountid"] = "Customer Branch Account",
            ["te_account_salesorder_calculated"] = "Calculated Account",
            ["contact_customer_accounts"] = "Account Contacts",
            ["account_primary_contact"] = "Primary Contact",
            ["order_customer_contacts"] = "Potential Customer (Contact)"
        };

    public bool HasExplicitNetworkCredentials =>
        !string.IsNullOrWhiteSpace(Username) && !string.IsNullOrWhiteSpace(Password);

    public bool IsDynamicsProvider =>
        Provider.Equals("Dynamics365", StringComparison.OrdinalIgnoreCase) ||
        Provider.Equals("DynamicsOnPrem", StringComparison.OrdinalIgnoreCase);
}
