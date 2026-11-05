namespace CrmAiPoc.Api.Configuration;

/// <summary>
/// Development diagnostics for AI ↔ backend ↔ Dynamics communication.
/// Disabled by default. Full payloads require explicit development opt-in.
/// </summary>
public sealed class AiDiagnosticsOptions
{
    public const string SectionName = "AiDiagnostics";

    /// <summary>Master switch. When false, all diagnostic sections are skipped.</summary>
    public bool Enabled { get; set; }

    public bool LogMetadata { get; set; } = true;
    public bool LogAiRequest { get; set; } = true;
    public bool LogAiResponse { get; set; } = true;
    public bool LogQueryPlan { get; set; } = true;
    public bool LogFetchXml { get; set; } = true;
    public bool LogCrmHttpRequests { get; set; } = true;
    public bool LogCrmHttpResponses { get; set; } = true;
    public bool LogCrmODataRequests { get; set; } = true;

    /// <summary>
    /// When true AND the host is Development, log full user questions, raw AI JSON,
    /// unredacted FetchXML filter values, and full CRM URLs. Never enable in production.
    /// </summary>
    public bool AllowSensitivePayloadLogging { get; set; }

    /// <summary>
    /// Soft character budget for a single diagnostic block. When exceeded, the log states
    /// truncation explicitly (never silent).
    /// </summary>
    public int MaxSectionChars { get; set; } = 100_000;
}
