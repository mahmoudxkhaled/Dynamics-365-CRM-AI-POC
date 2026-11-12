using System.Runtime.CompilerServices;
using CrmAiPoc.Api.Configuration;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using OllamaSharp;
using OllamaSharp.Models;

namespace CrmAiPoc.Api.Ai;

/// <summary>
/// Ollama chat client via OllamaSharp as <see cref="IChatClient"/>.
/// Raw client only — ChatService owns privacy-safe local tool execution (no UseFunctionInvocation).
/// </summary>
public sealed class LazyOllamaChatClient : IChatClient
{
    private readonly IOptions<AiOptions> _aiOptions;
    private readonly ILogger<LazyOllamaChatClient> _logger;
    private readonly object _gate = new();
    private IChatClient? _inner;
    private HttpClient? _httpClient;
    private string? _resolvedModel;

    public LazyOllamaChatClient(
        IOptions<AiOptions> aiOptions,
        ILogger<LazyOllamaChatClient> logger)
    {
        _aiOptions = aiOptions;
        _logger = logger;
    }

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default) =>
        GetInner().GetResponseAsync(messages, ApplyOllamaChatOptions(options), cancellationToken);

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var update in GetInner().GetStreamingResponseAsync(
                           messages,
                           ApplyOllamaChatOptions(options),
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
        _httpClient?.Dispose();
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

            var ollama = _aiOptions.Value.Ollama ?? new OllamaChatOptions();
            var baseUrl = ollama.ResolveBaseUrl();
            var model = ollama.ResolveModel();

            if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var endpoint) ||
                (endpoint.Scheme != Uri.UriSchemeHttp && endpoint.Scheme != Uri.UriSchemeHttps))
            {
                throw new InvalidOperationException(
                    $"Ollama BaseUrl '{baseUrl}' is invalid. Set Ai:Ollama:BaseUrl to an absolute http(s) URL.");
            }

            // ChatService owns the authoritative CancelAfter timeout; keep HttpClient above Ai max (90s).
            _httpClient = new HttpClient
            {
                BaseAddress = endpoint,
                Timeout = TimeSpan.FromSeconds(120)
            };

            var api = new OllamaApiClient(_httpClient, model);

            try
            {
                EnsureModelAvailable(api, model, baseUrl);
            }
            catch
            {
                _httpClient.Dispose();
                _httpClient = null;
                throw;
            }

            _logger.LogInformation(
                "[OLLAMA CLIENT] Endpoint={Endpoint} Model={Model} ContextSize={ContextSize} " +
                "Temperature={Temperature} EnableThinking={EnableThinking} " +
                "Note=Raw IChatClient; ChatService owns tool execution and timeout policy.",
                baseUrl,
                model,
                Math.Clamp(ollama.ContextSize, 1024, 262144),
                ollama.Temperature,
                ollama.EnableThinking);

            _resolvedModel = model;
            // OllamaApiClient implements IChatClient; no UseFunctionInvocation (privacy: CRM rows stay local).
            _inner = api;
            return _inner;
        }
    }

    private void EnsureModelAvailable(OllamaApiClient api, string model, string baseUrl)
    {
        IEnumerable<OllamaSharp.Models.Model> localModels;
        try
        {
            localModels = api.ListLocalModelsAsync().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            throw new HttpRequestException(
                $"Cannot reach Ollama at {baseUrl}. Verify the server is running and reachable on the LAN. {ex.Message}",
                ex);
        }

        var names = localModels
            .Select(m => m.Name)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .ToList();

        var found = names.Any(n =>
            string.Equals(n, model, StringComparison.OrdinalIgnoreCase) ||
            n.StartsWith(model + ":", StringComparison.OrdinalIgnoreCase) ||
            model.StartsWith(n + ":", StringComparison.OrdinalIgnoreCase));

        if (!found)
        {
            var available = names.Count == 0 ? "(none)" : string.Join(", ", names.Take(20));
            throw new InvalidOperationException(
                $"Ollama model '{model}' was not found at {baseUrl}. Available models: {available}. " +
                $"Pull it with: ollama pull {model}");
        }
    }

    private ChatOptions ApplyOllamaChatOptions(ChatOptions? options)
    {
        var configured = _aiOptions.Value.Ollama ?? new OllamaChatOptions();
        var merged = options is null ? new ChatOptions() : options.Clone();

        merged.ModelId ??= _resolvedModel ?? configured.ResolveModel();
        merged.Temperature = configured.Temperature;

        // Map EnableThinking → MEAI ReasoningOptions → Ollama ChatRequest.Think (AbstractionMapper).
        merged.Reasoning ??= new ReasoningOptions();
        merged.Reasoning.Effort = configured.EnableThinking
            ? ReasoningEffort.Medium
            : ReasoningEffort.None;

        var ctx = Math.Clamp(configured.ContextSize, 1024, 262144);
        merged.AddOllamaOption(OllamaOption.NumCtx, ctx);

        return merged;
    }
}
