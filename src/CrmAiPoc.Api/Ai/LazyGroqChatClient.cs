using System.ClientModel;
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
    private readonly object _gate = new();
    private IChatClient? _inner;

    public LazyGroqChatClient(IOptions<GroqOptions> options)
    {
        _options = options;
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

            var model = string.IsNullOrWhiteSpace(options.ModelName)
                ? "openai/gpt-oss-120b"
                : options.ModelName;

            var baseUrl = string.IsNullOrWhiteSpace(options.BaseUrl)
                ? "https://api.groq.com/openai/v1"
                : options.BaseUrl.TrimEnd('/');

            var openAiClient = new OpenAIClient(
                new ApiKeyCredential(options.ApiKey),
                new OpenAIClientOptions
                {
                    Endpoint = new Uri(baseUrl)
                });

            // Raw client (no UseFunctionInvocation). ChatService executes tools locally
            // so live CRM records are never forwarded back to Groq.
            _inner = openAiClient
                .GetChatClient(model)
                .AsIChatClient();

            return _inner;
        }
    }
}
