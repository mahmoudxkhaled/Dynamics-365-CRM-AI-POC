namespace CrmAiPoc.Api.Configuration;

public sealed class CrmOptions
{
    public const string SectionName = "Crm";

    /// <summary>Mock (default) or Dynamics365.</summary>
    public string Provider { get; set; } = "Mock";

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
    /// When true, a compact approved CRM metadata catalog (entities/fields only — never records)
    /// is included in the outbound AI context. Default false; enable explicitly for PoC/Dev.
    /// Live CRM rows are never sent to the AI (privacy-safe by design).
    /// </summary>
    public bool AllowSendApprovedMetadataCatalogToAi { get; set; }

    /// <summary>Max approved entities included in the AI metadata catalog.</summary>
    public int AiMetadataCatalogMaxEntities { get; set; } = 8;

    /// <summary>Max attributes per entity in the AI metadata catalog.</summary>
    public int AiMetadataCatalogMaxAttributesPerEntity { get; set; } = 20;

    /// <summary>Hard character budget for the serialized catalog block sent to the AI.</summary>
    public int AiMetadataCatalogMaxChars { get; set; } = 6000;

    /// <summary>
    /// When true (default), load approved entity metadata from CRM/Mock in a background hosted service at startup
    /// so chat does not wait on first-request discovery.
    /// </summary>
    public bool WarmApprovedMetadataOnStartup { get; set; } = true;

    /// <summary>
    /// POC safety: when true (default in Development), reject non-loopback callers for chat/API.
    /// Remote multi-user access requires proper incoming authentication first.
    /// </summary>
    public bool RestrictToLoopback { get; set; } = true;

    /// <summary>
    /// Development-only AI execution inspector (in-memory traces + diagnostic endpoints).
    /// Disabled by default; enable only in Development appsettings. Not a security boundary — also requires loopback.
    /// </summary>
    public bool EnableExecutionInspector { get; set; }

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
    /// Optional business aliases per entity logical name for natural-language routing hints.
    /// </summary>
    public Dictionary<string, List<string>> EntityAliases { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["account"] = ["accounts", "customers", "عملاء", "حسابات", "عميل"],
        ["contact"] = ["contacts", "جهات اتصال", "اتصل"],
        ["salesorder"] = ["sales order", "sales orders", "أوامر البيع", "امر بيع", "أمر بيع", "اوادر"],
        ["lead"] = ["leads", "عميل محتمل", "عملاء محتملين", "ليد"]
    };

    /// <summary>Seconds to cache discovered entity metadata before refresh.</summary>
    public int MetadataCacheSeconds { get; set; } = 3600;

    public bool HasExplicitNetworkCredentials =>
        !string.IsNullOrWhiteSpace(Username) && !string.IsNullOrWhiteSpace(Password);

    public bool IsDynamicsProvider =>
        Provider.Equals("Dynamics365", StringComparison.OrdinalIgnoreCase) ||
        Provider.Equals("DynamicsOnPrem", StringComparison.OrdinalIgnoreCase);
}
