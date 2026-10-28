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
    private readonly object _gate = new();
    private IChatClient? _inner;

    public LazyGeminiChatClient(IOptions<GeminiOptions> options)
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
                    "Gemini API key is missing. Set Gemini:ApiKey in appsettings or environment variable GEMINI_API_KEY.");
            }

            var model = string.IsNullOrWhiteSpace(options.ModelName)
                ? "gemini-3.8-flash"
                : options.ModelName;

            // Raw client — ChatService owns privacy-safe tool execution.
            var client = new Client(apiKey: options.ApiKey);
            _inner = client.AsIChatClient(model);

            return _inner;
        }
    }
}
