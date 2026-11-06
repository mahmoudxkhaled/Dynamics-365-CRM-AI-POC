using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using CrmAiPoc.Api.Configuration;
using Microsoft.Extensions.Options;

namespace CrmAiPoc.Api.Crm.Metadata.Semantic;

/// <summary>
/// In-memory embedding index keyed by metadata content hash.
/// Concurrent callers single-flight through one rebuild for a given hash.
/// Optionally persists vectors to a local JSON file so restarts skip Ollama re-embedding
/// when metadata/model/dimensions are unchanged.
/// </summary>
public sealed class InMemorySemanticMetadataIndex
{
    public const string CacheFileName = "semantic-metadata-embeddings.json";
    private const int CacheFormatVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    private readonly SemaphoreSlim _rebuildGate = new(1, 1);
    private readonly ConcurrentDictionary<string, byte> _buildLocks = new(StringComparer.Ordinal);
    private readonly bool _diskCacheEnabled;
    private readonly string? _cacheFilePath;
    private readonly string _embeddingModel;
    private readonly int _expectedDimensions;
    private readonly ILogger<InMemorySemanticMetadataIndex>? _logger;
    private volatile IndexSnapshot? _snapshot;

    public InMemorySemanticMetadataIndex(
        IOptions<SemanticMetadataOptions>? semanticOptions = null,
        IOptions<OllamaOptions>? ollamaOptions = null,
        ILogger<InMemorySemanticMetadataIndex>? logger = null)
    {
        _logger = logger;
        var semantic = semanticOptions?.Value;
        var ollama = ollamaOptions?.Value;

        _embeddingModel = string.IsNullOrWhiteSpace(ollama?.EmbeddingModel)
            ? "nomic-embed-text"
            : ollama!.EmbeddingModel.Trim();
        _expectedDimensions = ollama?.ExpectedDimensions ?? 0;

        _diskCacheEnabled = semantic?.EnableDiskCache == true;
        _cacheFilePath = _diskCacheEnabled
            ? Path.Combine(ResolveCacheDirectory(semantic?.CacheDirectory), CacheFileName)
            : null;
    }

    public IndexSnapshot? Current => _snapshot;

    public int DocumentCount => _snapshot?.Vectors.Count ?? 0;

    public string? ContentHash => _snapshot?.ContentHash;

    /// <summary>Resolved cache file path when disk cache is enabled; otherwise null.</summary>
    public string? CacheFilePath => _cacheFilePath;

    /// <summary>
    /// Ensures an index snapshot exists for <paramref name="contentHash"/>.
    /// Only one embed rebuild runs at a time; waiters reuse the same snapshot.
    /// <paramref name="rebuilt"/> is true only for the caller that performed the embed
    /// (cache hits report rebuilt=false).
    /// </summary>
    public async Task<(IndexSnapshot Snapshot, bool Rebuilt)> EnsureBuiltAsync(
        string contentHash,
        IReadOnlyList<MetadataSearchDocument> documents,
        Func<IReadOnlyList<string>, CancellationToken, Task<IReadOnlyList<float[]>>> embedBatch,
        CancellationToken cancellationToken)
    {
        var current = _snapshot;
        if (current is not null &&
            current.ContentHash.Equals(contentHash, StringComparison.Ordinal))
        {
            return (current, Rebuilt: false);
        }

        await _rebuildGate.WaitAsync(cancellationToken);
        try
        {
            current = _snapshot;
            if (current is not null &&
                current.ContentHash.Equals(contentHash, StringComparison.Ordinal))
            {
                // Another caller already completed the shared build while we waited.
                return (current, Rebuilt: false);
            }

            _buildLocks[contentHash] = 0;
            try
            {
                if (TryLoadFromDisk(contentHash, documents, out var cachedVectors))
                {
                    _logger?.LogInformation(
                        "[SEMANTIC INDEX] CacheHit — loaded {DocumentCount} vectors from disk",
                        cachedVectors.Count);
                    var cachedSnapshot = new IndexSnapshot(contentHash, cachedVectors);
                    _snapshot = cachedSnapshot;
                    return (cachedSnapshot, Rebuilt: false);
                }

                _logger?.LogInformation(
                    "[SEMANTIC INDEX] CacheMiss — building embeddings with Ollama");

                var inputs = documents.Select(d => d.EmbeddingInput).ToArray();
                var embeddings = await embedBatch(inputs, cancellationToken);
                if (embeddings.Count != documents.Count)
                {
                    throw new InvalidOperationException(
                        $"Embedding batch size mismatch: docs={documents.Count}, vectors={embeddings.Count}.");
                }

                var vectors = new List<IndexedMetadataVector>(documents.Count);
                for (var i = 0; i < documents.Count; i++)
                {
                    vectors.Add(new IndexedMetadataVector
                    {
                        Document = documents[i],
                        Embedding = embeddings[i]
                    });
                }

                var snapshot = new IndexSnapshot(contentHash, vectors);
                _snapshot = snapshot;
                TrySaveToDisk(contentHash, vectors);
                return (snapshot, Rebuilt: true);
            }
            finally
            {
                _buildLocks.TryRemove(contentHash, out _);
            }
        }
        finally
        {
            _rebuildGate.Release();
        }
    }

    public void Clear() => _snapshot = null;

    internal static string ResolveCacheDirectory(string? configuredDirectory)
    {
        var relativeOrAbsolute = string.IsNullOrWhiteSpace(configuredDirectory)
            ? Path.Combine("App_Data", "semantic-metadata-index")
            : configuredDirectory.Trim();

        if (Path.IsPathRooted(relativeOrAbsolute))
        {
            return Path.GetFullPath(relativeOrAbsolute);
        }

        // Prefer already-resolved absolute paths from Program PostConfigure (content root).
        // Fallback for non-DI construction: resolve under the process base directory.
        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, relativeOrAbsolute));
    }

    private bool TryLoadFromDisk(
        string contentHash,
        IReadOnlyList<MetadataSearchDocument> documents,
        out IReadOnlyList<IndexedMetadataVector> vectors)
    {
        vectors = Array.Empty<IndexedMetadataVector>();
        if (!_diskCacheEnabled || string.IsNullOrWhiteSpace(_cacheFilePath))
        {
            return false;
        }

        if (!File.Exists(_cacheFilePath))
        {
            return false;
        }

        try
        {
            var json = File.ReadAllText(_cacheFilePath);
            var cache = JsonSerializer.Deserialize<DiskCacheFile>(json, JsonOptions);
            if (cache is null)
            {
                _logger?.LogInformation(
                    "[SEMANTIC INDEX] CacheInvalid — metadata/model mismatch, rebuilding");
                return false;
            }

            if (cache.Version != CacheFormatVersion ||
                !string.Equals(cache.ContentHash, contentHash, StringComparison.Ordinal) ||
                !string.Equals(cache.EmbeddingModel, _embeddingModel, StringComparison.OrdinalIgnoreCase) ||
                cache.DocumentCount != documents.Count ||
                cache.Entries is null ||
                cache.Entries.Count != documents.Count)
            {
                _logger?.LogInformation(
                    "[SEMANTIC INDEX] CacheInvalid — metadata/model mismatch, rebuilding");
                return false;
            }

            if (_expectedDimensions > 0 && cache.Dimensions != _expectedDimensions)
            {
                _logger?.LogInformation(
                    "[SEMANTIC INDEX] CacheInvalid — metadata/model mismatch, rebuilding");
                return false;
            }

            var loaded = new List<IndexedMetadataVector>(documents.Count);
            for (var i = 0; i < documents.Count; i++)
            {
                var entry = cache.Entries[i];
                if (entry?.Embedding is null ||
                    !string.Equals(entry.Id, documents[i].Id, StringComparison.Ordinal))
                {
                    _logger?.LogInformation(
                        "[SEMANTIC INDEX] CacheInvalid — metadata/model mismatch, rebuilding");
                    return false;
                }

                if (_expectedDimensions > 0 && entry.Embedding.Length != _expectedDimensions)
                {
                    _logger?.LogInformation(
                        "[SEMANTIC INDEX] CacheInvalid — metadata/model mismatch, rebuilding");
                    return false;
                }

                if (cache.Dimensions > 0 && entry.Embedding.Length != cache.Dimensions)
                {
                    _logger?.LogInformation(
                        "[SEMANTIC INDEX] CacheInvalid — metadata/model mismatch, rebuilding");
                    return false;
                }

                loaded.Add(new IndexedMetadataVector
                {
                    Document = documents[i],
                    Embedding = entry.Embedding
                });
            }

            vectors = loaded;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            _logger?.LogInformation(
                ex,
                "[SEMANTIC INDEX] CacheInvalid — metadata/model mismatch, rebuilding");
            return false;
        }
    }

    private void TrySaveToDisk(string contentHash, IReadOnlyList<IndexedMetadataVector> vectors)
    {
        if (!_diskCacheEnabled || string.IsNullOrWhiteSpace(_cacheFilePath) || vectors.Count == 0)
        {
            return;
        }

        try
        {
            var directory = Path.GetDirectoryName(_cacheFilePath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var dimensions = vectors[0].Embedding.Length;
            var cache = new DiskCacheFile
            {
                Version = CacheFormatVersion,
                ContentHash = contentHash,
                EmbeddingModel = _embeddingModel,
                Dimensions = dimensions,
                DocumentCount = vectors.Count,
                Entries = vectors.Select(v => new DiskCacheEntry
                {
                    Id = v.Document.Id,
                    Embedding = v.Embedding
                }).ToList()
            };

            var json = JsonSerializer.Serialize(cache, JsonOptions);
            var tempPath = _cacheFilePath + ".tmp";
            File.WriteAllText(tempPath, json);

            // Atomic replace where the OS supports it; fall back to overwrite copy.
            if (File.Exists(_cacheFilePath))
            {
                File.Replace(tempPath, _cacheFilePath, destinationBackupFileName: null);
            }
            else
            {
                File.Move(tempPath, _cacheFilePath);
            }

            _logger?.LogInformation(
                "[SEMANTIC INDEX] CacheSaved — documents={DocumentCount}",
                vectors.Count);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // Keep serving from memory; disk persistence is best-effort for the POC.
            _logger?.LogWarning(
                ex,
                "[SEMANTIC INDEX] CacheSaveFailed — continuing with in-memory index only");
        }
    }

    private sealed class DiskCacheFile
    {
        public int Version { get; set; }
        public string ContentHash { get; set; } = string.Empty;
        public string EmbeddingModel { get; set; } = string.Empty;
        public int Dimensions { get; set; }
        public int DocumentCount { get; set; }
        public List<DiskCacheEntry>? Entries { get; set; }
    }

    private sealed class DiskCacheEntry
    {
        public string Id { get; set; } = string.Empty;
        public float[] Embedding { get; set; } = Array.Empty<float>();
    }

    public sealed class IndexSnapshot
    {
        public IndexSnapshot(string contentHash, IReadOnlyList<IndexedMetadataVector> vectors)
        {
            ContentHash = contentHash;
            Vectors = vectors;
        }

        public string ContentHash { get; }
        public IReadOnlyList<IndexedMetadataVector> Vectors { get; }
    }
}
