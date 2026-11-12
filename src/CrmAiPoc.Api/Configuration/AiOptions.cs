namespace CrmAiPoc.Api.Configuration;

/// <summary>
/// Selects which AI backend to use. Groq, Gemini, OpenAI, OpenRouter, and Ollama remain configured.
/// </summary>
public sealed class AiOptions
{
    public const string SectionName = "Ai";

    /// <summary>Groq (default), Gemini, OpenAI, OpenRouter, or Ollama.</summary>
    public string Provider { get; set; } = "Groq";

    /// <summary>
    /// Max seconds to wait for a single AI provider planning response.
    /// Client-side timeouts do not automatically retry another full wait.
    /// Bound in ChatService to 8–90 seconds.
    /// </summary>
    public int ProviderTimeoutSeconds { get; set; } = 60;

    /// <summary>Ollama chat LLM settings when <see cref="Provider"/> is Ollama.</summary>
    public OllamaChatOptions Ollama { get; set; } = new();

    /// <summary>
    /// Max dialogue messages (user + privacy-safe assistant) included in a Groq planning request,
    /// including the current user question. Older turns are dropped first. Default 10.
    /// </summary>
    public int MaxUserQuestionsInHistory { get; set; } = 10;

    /// <summary>
    /// Soft character budget for outbound dialogue text (sum of included turn texts).
    /// Oldest turns are dropped first; the current user question is never dropped or truncated.
    /// </summary>
    public int MaxUserHistoryCharacters { get; set; } = 8000;

    /// <summary>
    /// Maximum additional Groq correction attempts after the initial planning call fails
    /// a recoverable query-plan validation. Total planning attempts = 1 + this value.
    /// Bound in ChatService to 0–5.
    /// </summary>
    public int MaxQueryPlanCorrectionRetries { get; set; } = 2;

    /// <summary>
    /// Max discover_crm_schema tool calls allowed per user request (POC v1). Bound 0–3.
    /// </summary>
    public int MaxSchemaDiscoveryCallsPerRequest { get; set; } = 2;

    /// <summary>
    /// Max attribute candidates returned per business concept. Bound 1–10.
    /// </summary>
    public int MaxSchemaDiscoveryCandidatesPerConcept { get; set; } = 5;
}
