using CrmAiPoc.Api.Ai;
using CrmAiPoc.Api.Configuration;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace CrmAiPoc.Tests;

public sealed class OllamaProviderSelectionTests
{
    [Fact]
    public void Provider_Ollama_Resolves_LazyOllamaChatClient()
    {
        using var sp = BuildServices(provider: "Ollama");
        var client = sp.GetRequiredService<IChatClient>();
        Assert.IsType<LazyOllamaChatClient>(client);
    }

    [Fact]
    public void Provider_Groq_Resolves_LazyGroqChatClient()
    {
        using var sp = BuildServices(provider: "Groq");
        var client = sp.GetRequiredService<IChatClient>();
        Assert.IsType<LazyGroqChatClient>(client);
    }

    [Fact]
    public void Provider_Unknown_Throws()
    {
        using var sp = BuildServices(provider: "NotARealProvider");
        var ex = Assert.Throws<InvalidOperationException>(() => sp.GetRequiredService<IChatClient>());
        Assert.Contains("Unknown Ai:Provider", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Ollama", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AiOptions_Binds_Nested_Ollama_Section()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Ai:Provider"] = "Ollama",
                ["Ai:Ollama:BaseUrl"] = "http://192.168.1.6:11434",
                ["Ai:Ollama:Model"] = "qwen3.5:4b-q4_K_M",
                ["Ai:Ollama:ContextSize"] = "16384",
                ["Ai:Ollama:Temperature"] = "0",
                ["Ai:Ollama:EnableThinking"] = "false"
            })
            .Build();

        var ai = new AiOptions();
        config.GetSection(AiOptions.SectionName).Bind(ai);

        Assert.Equal("Ollama", ai.Provider);
        Assert.Equal("http://192.168.1.6:11434", ai.Ollama.BaseUrl);
        Assert.Equal("qwen3.5:4b-q4_K_M", ai.Ollama.ResolveModel());
        Assert.Equal(16384, ai.Ollama.ContextSize);
        Assert.Equal(0f, ai.Ollama.Temperature);
        Assert.False(ai.Ollama.EnableThinking);
    }

    [Fact]
    public void OllamaChatOptions_ResolveBaseUrl_Trims_Trailing_Slash()
    {
        var opts = new OllamaChatOptions { BaseUrl = "http://192.168.1.6:11434/" };
        Assert.Equal("http://192.168.1.6:11434", opts.ResolveBaseUrl());
    }

    private static ServiceProvider BuildServices(string provider)
    {
        var services = new ServiceCollection();
        services.AddSingleton(Options.Create(new AiOptions
        {
            Provider = provider,
            Ollama = new OllamaChatOptions
            {
                BaseUrl = "http://192.168.1.6:11434",
                Model = "qwen3.5:4b-q4_K_M",
                ContextSize = 16384,
                Temperature = 0,
                EnableThinking = false
            }
        }));
        services.AddSingleton(Options.Create(new GroqOptions
        {
            ApiKey = "test-key",
            Model = "openai/gpt-oss-120b"
        }));
        services.AddSingleton(Options.Create(new GeminiOptions()));
        services.AddSingleton(Options.Create(new OpenAiOptions()));
        services.AddLogging();
        services.AddSingleton<LazyGeminiChatClient>();
        services.AddSingleton<LazyOpenAiChatClient>();
        services.AddSingleton<LazyGroqChatClient>();
        services.AddSingleton<LazyOllamaChatClient>();
        services.AddSingleton<IChatClient>(sp =>
        {
            var ai = sp.GetRequiredService<IOptions<AiOptions>>().Value;
            var p = (ai.Provider ?? "Groq").Trim();
            if (p.Equals("OpenAI", StringComparison.OrdinalIgnoreCase))
            {
                return sp.GetRequiredService<LazyOpenAiChatClient>();
            }

            if (p.Equals("Gemini", StringComparison.OrdinalIgnoreCase))
            {
                return sp.GetRequiredService<LazyGeminiChatClient>();
            }

            if (p.Equals("Groq", StringComparison.OrdinalIgnoreCase))
            {
                return sp.GetRequiredService<LazyGroqChatClient>();
            }

            if (p.Equals("Ollama", StringComparison.OrdinalIgnoreCase))
            {
                return sp.GetRequiredService<LazyOllamaChatClient>();
            }

            throw new InvalidOperationException(
                $"Unknown Ai:Provider '{p}'. Use 'Gemini', 'OpenAI', 'Groq', or 'Ollama'.");
        });

        return services.BuildServiceProvider();
    }
}
