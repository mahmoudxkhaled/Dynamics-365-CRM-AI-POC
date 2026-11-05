namespace CrmAiPoc.Api.Ai;

/// <summary>Privacy-safe classification of AI provider call failures (no CRM payloads).</summary>
public enum AiProviderFailureKind
{
    None = 0,
    ClientTimeout,
    Http429,
    Http400,
    Http503,
    Network,
    ProviderLimit,
    Other
}
