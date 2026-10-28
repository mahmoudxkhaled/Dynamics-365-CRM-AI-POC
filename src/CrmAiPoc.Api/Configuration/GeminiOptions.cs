namespace CrmAiPoc.Api.Configuration;

public sealed class GeminiOptions
{
    public const string SectionName = "Gemini";

    /// <summary>Google AI Studio / Gemini Developer API key.</summary>
    public string ApiKey { get; set; } = string.Empty;

    public string ModelName { get; set; } = "gemini-3.8-flash";
}
