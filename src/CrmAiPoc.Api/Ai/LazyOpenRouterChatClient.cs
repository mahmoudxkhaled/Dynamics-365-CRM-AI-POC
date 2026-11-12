using System.ClientModel;
using System.ClientModel.Primitives;
using System.Runtime.CompilerServices;
using CrmAiPoc.Api.Configuration;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using OpenAI;

namespace CrmAiPoc.Api.Ai;

/// <summary>
/// OpenRouter chat client using the OpenAI-compatible endpoint
/// (https://openrouter.ai/api/v1) via the OpenAI .NET SDK + MEAI.
/// </summary>
public sealed class LazyOpenRouterChatClient : IChatClient
{
    private readonly IOptions<OpenRouterOptions> _options;
    private readonly ILogger<LazyOpenRouterChatClient> _logger;
    private readonly object _gate = new();
    private IChatClient? _inner;

    public LazyOpenRouterChatClient(
        IOptions<OpenRouterOptions> options,
        ILogger<LazyOpenRouterChatClient> logger)
    {
        _options = options;
        _logger = logger;
    }

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default) =>
        GetInner().GetResponseAsync(messages, ApplyChatOptions(options), cancellationToken);

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var update in GetInner().GetStreamingResponseAsync(
                           messages,
                           ApplyChatOptions(options),
                           cancellationToken))
        {
            yield return update;
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        GetInner().GetService(serviceType, serviceKey);

    public void Dispose() => _inner?.Dispose();

    private IChatClient GetInner()
    {
        if (_inner is not null)
        {
            return _inner;
        }

        lock (_gate)
        {
            if (_inner is not null)
            {
                return _inner;
            }

            var options = _options.Value;
            if (string.IsNullOrWhiteSpace(options.ApiKey))
            {
                throw new InvalidOperationException(
                    "OpenRouter API key is missing. Set OpenRouter:ApiKey in appsettings or environment variable OPENROUTER_API_KEY.");
            }

            var model = options.ResolveModel();
            var baseUrl = string.IsNullOrWhiteSpace(options.BaseUrl)
                ? "https://openrouter.ai/api/v1"
                : options.BaseUrl.TrimEnd('/');

            // Disable SDK retries so ChatService timeout / transient policy stays authoritative.
            const int sdkMaxRetries = 0;
            var openAiClient = new OpenAIClient(
                new ApiKeyCredential(options.ApiKey),
                new OpenAIClientOptions
                {
                    Endpoint = new Uri(baseUrl),
                    RetryPolicy = new ClientRetryPolicy(sdkMaxRetries)
                });

            _logger.LogInformation(
                "[OPENROUTER CLIENT] Endpoint={Endpoint} Model={Model} Temperature={Temperature} SdkMaxRetries={SdkMaxRetries} " +
                "Note=Raw IChatClient; ChatService owns tool execution and timeout policy.",
                baseUrl,
                model,
                options.Temperature,
                sdkMaxRetries);

            _inner = openAiClient
                .GetChatClient(model)
                .AsIChatClient();

            return _inner;
        }
    }

    private ChatOptions ApplyChatOptions(ChatOptions? options)
    {
        var merged = options is null ? new ChatOptions() : options.Clone();
        merged.Temperature = _options.Value.Temperature;
        return merged;
    }
}
