namespace CrmAiPoc.Api.Configuration;

public sealed class OpenRouterOptions
{
    public const string SectionName = "OpenRouter";

    public string ApiKey { get; set; } = string.Empty;

    /// <summary>OpenRouter model id, e.g. openai/gpt-4o.</summary>
    public string? Model { get; set; }

    /// <summary>Legacy alias for <see cref="Model"/>.</summary>
    public string ModelName { get; set; } = "openai/gpt-4o";

    public float Temperature { get; set; }

    /// <summary>OpenAI-compatible OpenRouter base URL.</summary>
    public string BaseUrl { get; set; } = "https://openrouter.ai/api/v1";

    public string ResolveModel()
    {
        if (!string.IsNullOrWhiteSpace(Model))
        {
            return Model.Trim();
        }

        if (!string.IsNullOrWhiteSpace(ModelName))
        {
            return ModelName.Trim();
        }

        return "openai/gpt-4o";
    }
}
