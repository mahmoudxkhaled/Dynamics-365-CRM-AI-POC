using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using CrmAiPoc.Api.Configuration;
using Microsoft.Extensions.Options;

namespace CrmAiPoc.Api.Crm.Metadata.Semantic;

public sealed class OllamaEmbeddingClient : IOllamaEmbeddingClient
{
    public const string HttpClientName = "OllamaEmbeddings";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly OllamaEmbeddingOptions _options;
    private readonly ILogger<OllamaEmbeddingClient> _logger;

    public OllamaEmbeddingClient(
        IHttpClientFactory httpClientFactory,
        IOptions<OllamaEmbeddingOptions> options,
        ILogger<OllamaEmbeddingClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<float[]> EmbedAsync(string input, CancellationToken cancellationToken = default)
    {
        var batch = await EmbedBatchAsync([input], cancellationToken);
        return batch[0];
    }

    public async Task<IReadOnlyList<float[]>> EmbedBatchAsync(
        IReadOnlyList<string> inputs,
        CancellationToken cancellationToken = default)
    {
        if (inputs.Count == 0)
        {
            return Array.Empty<float[]>();
        }

        var client = _httpClientFactory.CreateClient(HttpClientName);
        // Ollama /api/embed accepts string or string[]; send batch when possible.
        var payload = new OllamaEmbedRequest
        {
            Model = _options.EmbeddingModel,
            Input = inputs.Count == 1 ? inputs[0] : inputs.ToArray()
        };

        using var response = await client.PostAsJsonAsync("/api/embed", payload, JsonOptions, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning(
                "Ollama embed failed: Status={Status} Body={Body}",
                (int)response.StatusCode,
                Truncate(body, 400));
            throw new InvalidOperationException(
                $"Ollama embedding request failed ({(int)response.StatusCode}).");
        }

        var parsed = ParseEmbeddings(body, expectedCount: inputs.Count);
        foreach (var vector in parsed)
        {
            if (_options.ExpectedDimensions > 0 && vector.Length != _options.ExpectedDimensions)
            {
                throw new InvalidOperationException(
                    $"Ollama embedding dimension mismatch: expected {_options.ExpectedDimensions}, got {vector.Length}.");
            }
        }

        return parsed;
    }

    internal static IReadOnlyList<float[]> ParseEmbeddings(string json, int expectedCount)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (root.TryGetProperty("embeddings", out var embeddingsEl) &&
            embeddingsEl.ValueKind == JsonValueKind.Array)
        {
            var list = new List<float[]>(embeddingsEl.GetArrayLength());
            foreach (var row in embeddingsEl.EnumerateArray())
            {
                list.Add(ReadFloatArray(row));
            }

            if (list.Count == 0)
            {
                throw new InvalidOperationException("Ollama response contained an empty embeddings array.");
            }

            if (expectedCount > 1 && list.Count != expectedCount)
            {
                throw new InvalidOperationException(
                    $"Ollama embeddings count mismatch: expected {expectedCount}, got {list.Count}.");
            }

            return list;
        }

        // Compatibility with older /api/embeddings shape: { "embedding": [ ... ] }
        if (root.TryGetProperty("embedding", out var embeddingEl) &&
            embeddingEl.ValueKind == JsonValueKind.Array)
        {
            return [ReadFloatArray(embeddingEl)];
        }

        throw new InvalidOperationException("Ollama response did not contain embeddings.");
    }

    private static float[] ReadFloatArray(JsonElement arrayEl)
    {
        var values = new float[arrayEl.GetArrayLength()];
        var i = 0;
        foreach (var n in arrayEl.EnumerateArray())
        {
            values[i++] = n.GetSingle();
        }

        return values;
    }

    private static string Truncate(string? value, int max) =>
        string.IsNullOrEmpty(value) ? string.Empty :
        value.Length <= max ? value : value[..max] + "…";

    private sealed class OllamaEmbedRequest
    {
        [JsonPropertyName("model")]
        public required string Model { get; init; }

        [JsonPropertyName("input")]
        public required object Input { get; init; }
    }
}

