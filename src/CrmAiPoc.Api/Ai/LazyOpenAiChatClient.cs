using System.Runtime.CompilerServices;
using CrmAiPoc.Api.Configuration;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using OpenAI;

namespace CrmAiPoc.Api.Ai;

/// <summary>
/// Builds the OpenAI IChatClient on first use so the host can start without a key.
/// </summary>
public sealed class LazyOpenAiChatClient : IChatClient
{
    private readonly IOptions<OpenAiOptions> _options;
    private readonly object _gate = new();
    private IChatClient? _inner;

    public LazyOpenAiChatClient(IOptions<OpenAiOptions> options)
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
                    "OpenAI API key is missing. Set OpenAI:ApiKey in appsettings or environment variable OPENAI_API_KEY.");
            }

            var model = string.IsNullOrWhiteSpace(options.ModelName) ? "gpt-4o-mini" : options.ModelName;
            // Raw client — ChatService owns privacy-safe tool execution.
            _inner = new OpenAIClient(options.ApiKey)
                .GetChatClient(model)
                .AsIChatClient();

            return _inner;
        }
    }
}
