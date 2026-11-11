using System.ClientModel;
using System.ClientModel.Primitives;
using System.Runtime.CompilerServices;
using CrmAiPoc.Api.Configuration;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using OpenAI;

namespace CrmAiPoc.Api.Ai;

/// <summary>
/// Groq chat client using the OpenAI-compatible endpoint
/// (https://api.groq.com/openai/v1) via the OpenAI .NET SDK + MEAI.
/// </summary>
public sealed class LazyGroqChatClient : IChatClient
{
    private readonly IOptions<GroqOptions> _options;
    private readonly ILogger<LazyGroqChatClient> _logger;
    private readonly object _gate = new();
    private IChatClient? _inner;

    public LazyGroqChatClient(
        IOptions<GroqOptions> options,
        ILogger<LazyGroqChatClient> logger)
    {
        _options = options;
        _logger = logger;
    }

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default) =>
        GetInner().GetResponseAsync(messages, ApplyGroqChatOptions(options), cancellationToken);

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var update in GetInner().GetStreamingResponseAsync(
                           messages,
                           ApplyGroqChatOptions(options),
                           cancellationToken))
        {
            yield return update;
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null) =>
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
                    "Groq API key is missing. Set Groq:ApiKey in appsettings or environment variable GROQ_API_KEY.");
            }

            var model = options.ResolveModel();

            var baseUrl = string.IsNullOrWhiteSpace(options.BaseUrl)
                ? "https://api.groq.com/openai/v1"
                : options.BaseUrl.TrimEnd('/');

            // Evidence: System.ClientModel ClientRetryPolicy defaults to maxRetries=3 with
            // exponential backoff (0.8s base, up to 60s) and retries 429/5xx inside WaitAsync.
            // That can consume most of Ai:ProviderTimeoutSeconds=60 before ChatService sees
            // the failure, hiding FailureKind=Http429 from application logs.
            // Disable SDK retries so app-level timeout + bounded 429/503 handling remain authoritative.
            const int sdkMaxRetries = 0;
            var openAiClient = new OpenAIClient(
                new ApiKeyCredential(options.ApiKey),
                new OpenAIClientOptions
                {
                    Endpoint = new Uri(baseUrl),
                    RetryPolicy = new ClientRetryPolicy(sdkMaxRetries)
                });

            _logger.LogInformation(
                "[GROQ CLIENT] Endpoint={Endpoint} Model={Model} ReasoningEffort={ReasoningEffort} " +
                "Temperature={Temperature} SdkMaxRetries={SdkMaxRetries} " +
                "Note=SDK internal retries disabled; ChatService owns timeout and transient retry policy.",
                baseUrl,
                model,
                options.ReasoningEffort,
                options.Temperature,
                sdkMaxRetries);

            // Raw client (no UseFunctionInvocation). ChatService executes tools locally
            // so live CRM records are never forwarded back to Groq.
            _inner = openAiClient
                .GetChatClient(model)
                .AsIChatClient();

            return _inner;
        }
    }

    /// <summary>
    /// Applies configured Groq temperature and reasoning_effort onto every chat request.
    /// </summary>
    private ChatOptions ApplyGroqChatOptions(ChatOptions? options)
    {
        var configured = _options.Value;
        var merged = options is null ? new ChatOptions() : options.Clone();
        merged.Temperature = configured.Temperature;

        var effort = ParseReasoningEffort(configured.ReasoningEffort);
        if (effort is not null)
        {
            merged.Reasoning ??= new ReasoningOptions();
            merged.Reasoning.Effort = effort;
        }

        return merged;
    }

    private static ReasoningEffort? ParseReasoningEffort(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return ReasoningEffort.Medium;
        }

        return value.Trim().ToLowerInvariant() switch
        {
            "none" => ReasoningEffort.None,
            "low" or "minimal" => ReasoningEffort.Low,
            "medium" or "default" => ReasoningEffort.Medium,
            "high" => ReasoningEffort.High,
            "xhigh" or "extra_high" or "extrahigh" or "max" => ReasoningEffort.ExtraHigh,
            _ => ReasoningEffort.Medium
        };
    }
}

