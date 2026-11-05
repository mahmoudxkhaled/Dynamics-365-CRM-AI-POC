using CrmAiPoc.Api.Configuration;
using CrmAiPoc.Api.Crm.Metadata.Semantic;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace CrmAiPoc.Tests;

/// <summary>Shared Ollama/semantic DI registration for live ChatService hosts.</summary>
internal static class LiveSemanticDiRegistration
{
    public static void AddSemanticMetadataRetrieval(
        IServiceCollection services,
        string? ollamaBaseUrl = null)
    {
        var baseUrl = ollamaBaseUrl
                      ?? Environment.GetEnvironmentVariable("OLLAMA_BASE_URL")
                      ?? "http://localhost:11434";

        services.AddSingleton(Options.Create(new OllamaOptions
        {
            BaseUrl = baseUrl,
            EmbeddingModel = "nomic-embed-text",
            ExpectedDimensions = 768,
            TimeoutSeconds = 180
        }));
        services.AddSingleton(Options.Create(new SemanticMetadataOptions
        {
            TopKEntities = 3,
            TopKAttributesPerEntity = 20,
            TopKRelationships = 10,
            MinSimilarity = 0
        }));
        services.AddHttpClient(OllamaEmbeddingClient.HttpClientName, (sp, client) =>
        {
            var opts = sp.GetRequiredService<IOptions<OllamaOptions>>().Value;
            client.BaseAddress = new Uri(opts.BaseUrl.EndsWith('/') ? opts.BaseUrl : opts.BaseUrl + "/");
            client.Timeout = TimeSpan.FromSeconds(opts.TimeoutSeconds);
        });
        services.AddSingleton<IOllamaEmbeddingClient, OllamaEmbeddingClient>();
        services.AddSingleton<InMemorySemanticMetadataIndex>();
        services.AddSingleton<ISemanticCrmMetadataRetriever, SemanticCrmMetadataRetriever>();
    }
}
