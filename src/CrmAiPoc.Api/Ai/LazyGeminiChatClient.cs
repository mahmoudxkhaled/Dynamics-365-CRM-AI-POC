using System.Runtime.CompilerServices;
using CrmAiPoc.Api.Configuration;
using Google.GenAI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace CrmAiPoc.Api.Ai;

/// <summary>
/// Google AI Studio / Gemini Developer API client via official Google.GenAI SDK.
/// </summary>
public sealed class LazyGeminiChatClient : IChatClient
{
    private readonly IOptions<GeminiOptions> _options;
    private readonly IOptions<AiOptions> _aiOptions;
    private readonly ILogger<LazyGeminiChatClient> _logger;
    private readonly object _gate = new();
    private IChatClient? _inner;

    public LazyGeminiChatClient(
        IOptions<GeminiOptions> options,
        IOptions<AiOptions> aiOptions,
        ILogger<LazyGeminiChatClient> logger)
    {
        _options = options;
        _aiOptions = aiOptions;
        _logger = logger;
    }

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default) =>
        GetInner().GetResponseAsync(messages, options, cancellationToken);

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var update in GetInner().GetStreamingResponseAsync(messages, options, cancellationToken))
        {
            yield return update;
        }
    }

    public object? GetService(System.Type serviceType, object? serviceKey = null) =>
        GetInner().GetService(serviceType, serviceKey);

    public void Dispose()
    {
        _inner?.Dispose();
    }

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
                    "Gemini API key is missing. Set Gemini:ApiKey in appsettings or environment variable GEMINI_API_KEY.");
            }

            var model = string.IsNullOrWhiteSpace(options.ModelName)
                ? "gemini-3.8-flash"
                : options.ModelName;

            // Align with Ai:ProviderTimeoutSeconds. Attempts=1 disables SDK retries so ChatService
            // owns timeout/retry (same approach as Groq). Default GenAI retries (up to 5) can burn
            // the full 60s budget and surface only as ClientTimeout.
            var timeoutSeconds = Math.Clamp(_aiOptions.Value.ProviderTimeoutSeconds, 8, 90);
            var httpOptions = new Google.GenAI.Types.HttpOptions
            {
                Timeout = timeoutSeconds * 1000,
                RetryOptions = new Google.GenAI.Types.HttpRetryOptions { Attempts = 1 }
            };

            _logger.LogInformation(
                "[GEMINI CLIENT] Model={Model} HttpTimeoutMs={HttpTimeoutMs} SdkAttempts={SdkAttempts} " +
                "Note=SDK retries disabled; ChatService owns timeout and transient retry policy.",
                model,
                httpOptions.Timeout,
                httpOptions.RetryOptions.Attempts);

            var client = new Client(apiKey: options.ApiKey, httpOptions: httpOptions);
            _inner = client.AsIChatClient(model);

            return _inner;
        }
    }
}
