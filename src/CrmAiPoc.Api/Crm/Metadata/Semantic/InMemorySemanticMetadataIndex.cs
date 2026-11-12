using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using CrmAiPoc.Api.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace CrmAiPoc.Api.Crm.Metadata.Semantic;

/// <summary>
/// In-memory embedding index keyed by metadata content hash.
/// Concurrent callers single-flight through one rebuild for a given hash.
/// Embeddings are requested in bounded batches. Failures enter a cooldown so
/// chat requests do not repeatedly retry a full rebuild against a cold/unavailable Ollama.
/// Optionally persists vectors to a project-local JSON file under ContentRootPath (Cache/SemanticIndex).
/// </summary>
public sealed class InMemorySemanticMetadataIndex
{
    public const string CacheFileName = "semantic-metadata-embeddings.json";
    public const string DefaultRelativeCacheDirectory = "Cache/SemanticIndex";
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
    private readonly int _batchSize;
    private readonly TimeSpan _failureCooldown;
    private readonly ILogger<InMemorySemanticMetadataIndex>? _logger;

    private volatile IndexSnapshot? _snapshot;
    private volatile SemanticIndexState _state = SemanticIndexState.NotStarted;
    private string? _failedHash;
    private string? _lastFailureReason;
    private DateTimeOffset _failedUtc;
    private int _lastBatchCount;
    private int _lastCompletedBatches;
    private int _lastVectorDimensions;

    public InMemorySemanticMetadataIndex(
        IOptions<SemanticMetadataOptions>? semanticOptions = null,
        IOptions<OllamaEmbeddingOptions>? OllamaEmbeddingOptions = null,
        ILogger<InMemorySemanticMetadataIndex>? logger = null,
        IHostEnvironment? hostEnvironment = null)
    {
        _logger = logger;
        var semantic = semanticOptions?.Value;
        var ollama = OllamaEmbeddingOptions?.Value;

        _embeddingModel = string.IsNullOrWhiteSpace(ollama?.EmbeddingModel)
            ? "nomic-embed-text"
            : ollama!.EmbeddingModel.Trim();
        _expectedDimensions = ollama?.ExpectedDimensions ?? 0;
        _batchSize = Math.Clamp(semantic?.EmbeddingBatchSize ?? 16, 1, 128);
        _failureCooldown = TimeSpan.FromSeconds(Math.Clamp(semantic?.IndexFailureCooldownSeconds ?? 90, 5, 3600));

        _diskCacheEnabled = semantic?.EnableDiskCache == true;
        _cacheFilePath = _diskCacheEnabled
            ? Path.Combine(
                ResolveCacheDirectory(semantic?.CacheDirectory, hostEnvironment?.ContentRootPath),
                CacheFileName)
            : null;

        if (_diskCacheEnabled)
        {
            _logger?.LogInformation(
                "[SEMANTIC INDEX] DiskCache enabled CacheFilePath={CacheFilePath} Exists={Exists} " +
                "EmbeddingModel={Model} ExpectedDimensions={Dims}",
                _cacheFilePath,
                !string.IsNullOrWhiteSpace(_cacheFilePath) && File.Exists(_cacheFilePath),
                _embeddingModel,
                _expectedDimensions);
        }
    }

    public IndexSnapshot? Current => _snapshot;

    public SemanticIndexState State => _state;

    public int DocumentCount => _snapshot?.Vectors.Count ?? 0;

    public string? ContentHash => _snapshot?.ContentHash;

    public string EmbeddingModel => _embeddingModel;

    public int VectorDimensions => _lastVectorDimensions;

    public string? LastFailureReason => _lastFailureReason;

    /// <summary>Resolved cache file path when disk cache is enabled; otherwise null.</summary>
    public string? CacheFilePath => _cacheFilePath;

    /// <summary>
    /// Returns the ready snapshot when it matches <paramref name="contentHash"/> without rebuilding.
    /// </summary>
    public bool TryGetReadySnapshot(string contentHash, out IndexSnapshot snapshot)
    {
        var current = _snapshot;
        if (_state == SemanticIndexState.Ready &&
            current is not null &&
            current.ContentHash.Equals(contentHash, StringComparison.Ordinal))
        {
            snapshot = current;
            return true;
        }

        snapshot = null!;
        return false;
    }

    /// <summary>
    /// True when a recent build for this hash failed and retries should wait for cooldown.
    /// </summary>
    public bool IsInFailureCooldown(string contentHash)
    {
        if (_state != SemanticIndexState.Failed ||
            string.IsNullOrWhiteSpace(_failedHash) ||
            !_failedHash.Equals(contentHash, StringComparison.Ordinal))
        {
            return false;
        }

        return DateTimeOffset.UtcNow - _failedUtc < _failureCooldown;
    }

    /// <summary>
    /// Ensures an index snapshot exists for <paramref name="contentHash"/>.
    /// Only one embed rebuild runs at a time; waiters reuse the same snapshot.
    /// On failure, records Failed state and cooldown so callers can fall back to lexical search.
    /// </summary>
    public async Task<EnsureIndexOutcome> EnsureBuiltAsync(
        string contentHash,
        IReadOnlyList<MetadataSearchDocument> documents,
        Func<IReadOnlyList<string>, CancellationToken, Task<IReadOnlyList<float[]>>> embedBatch,
        CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();

        if (TryGetReadySnapshot(contentHash, out var ready))
        {
            return new EnsureIndexOutcome
            {
                Success = true,
                Snapshot = ready,
                Rebuilt = false,
                State = SemanticIndexState.Ready,
                DocumentCount = ready.Vectors.Count,
                CacheHit = true,
                DurationMs = sw.ElapsedMilliseconds,
                BatchCount = 0,
                CompletedBatches = 0
            };
        }

        if (IsInFailureCooldown(contentHash))
        {
            _logger?.LogInformation(
                "[SEMANTIC INDEX] State={State} SkipRebuild=failure-cooldown HashPrefix={Hash} Reason={Reason} CooldownSeconds={Cooldown}",
                SemanticIndexState.Failed,
                Truncate(contentHash, 16),
                Truncate(_lastFailureReason, 160),
                (int)_failureCooldown.TotalSeconds);

            return new EnsureIndexOutcome
            {
                Success = false,
                State = SemanticIndexState.Failed,
                FailureReason = _lastFailureReason ?? "index-failure-cooldown",
                SkippedDueToFailureCooldown = true,
                DurationMs = sw.ElapsedMilliseconds,
                DocumentCount = documents.Count
            };
        }

        await _rebuildGate.WaitAsync(cancellationToken);
        try
        {
            if (TryGetReadySnapshot(contentHash, out ready))
            {
                return new EnsureIndexOutcome
                {
                    Success = true,
                    Snapshot = ready,
                    Rebuilt = false,
                    State = SemanticIndexState.Ready,
                    DocumentCount = ready.Vectors.Count,
                    CacheHit = true,
                    DurationMs = sw.ElapsedMilliseconds
                };
            }

            if (IsInFailureCooldown(contentHash))
            {
                return new EnsureIndexOutcome
                {
                    Success = false,
                    State = SemanticIndexState.Failed,
                    FailureReason = _lastFailureReason ?? "index-failure-cooldown",
                    SkippedDueToFailureCooldown = true,
                    DurationMs = sw.ElapsedMilliseconds,
                    DocumentCount = documents.Count
                };
            }

            _buildLocks[contentHash] = 0;
            _state = SemanticIndexState.Building;
            _logger?.LogInformation(
                "[SEMANTIC INDEX] State={State} DocumentCount={DocumentCount} EmbeddingModel={Model} ExpectedDimensions={Dims} BatchSize={BatchSize}",
                SemanticIndexState.Building,
                documents.Count,
                _embeddingModel,
                _expectedDimensions,
                _batchSize);

            try
            {
                if (TryLoadFromDisk(contentHash, documents, out var cachedVectors))
                {
                    var cachedDims = cachedVectors.Count > 0 ? cachedVectors[0].Embedding.Length : 0;
                    var cachedSnapshot = new IndexSnapshot(contentHash, cachedVectors);
                    // Atomic publish — only assign after fully constructed.
                    _snapshot = cachedSnapshot;
                    _state = SemanticIndexState.Ready;
                    _failedHash = null;
                    _lastFailureReason = null;
                    _lastVectorDimensions = cachedDims;
                    _lastBatchCount = 0;
                    _lastCompletedBatches = 0;

                    _logger?.LogInformation(
                        "[SEMANTIC INDEX] State={State} LoadedFromDisk=true CacheFilePath={CacheFilePath} " +
                        "DocumentCount={DocumentCount} Dimensions={Dims} EmbeddingModel={Model} " +
                        "ContentHashPrefix={Hash} DurationMs={Ms}",
                        SemanticIndexState.Ready,
                        _cacheFilePath,
                        cachedVectors.Count,
                        cachedDims,
                        _embeddingModel,
                        Truncate(contentHash, 16),
                        sw.ElapsedMilliseconds);

                    return new EnsureIndexOutcome
                    {
                        Success = true,
                        Snapshot = cachedSnapshot,
                        Rebuilt = false,
                        State = SemanticIndexState.Ready,
                        CacheHit = true,
                        LoadedFromDisk = true,
                        DocumentCount = cachedVectors.Count,
                        DurationMs = sw.ElapsedMilliseconds
                    };
                }

                _logger?.LogInformation(
                    "[SEMANTIC INDEX] RebuildRequired LoadedFromDisk=false CacheFilePath={CacheFilePath} " +
                    "DocumentCount={DocumentCount} EmbeddingModel={Model} ExpectedDimensions={Dims}",
                    _cacheFilePath ?? "(disabled)",
                    documents.Count,
                    _embeddingModel,
                    _expectedDimensions);

                // Warm the embedding model with a tiny request before the full catalog.
                try
                {
                    _ = await embedBatch(
                        [MetadataDocumentBuilder.DocumentPrefix + " CRM metadata index warmup"],
                        cancellationToken);
                    _logger?.LogInformation(
                        "[SEMANTIC INDEX] ModelWarmup=ok EmbeddingModel={Model}",
                        _embeddingModel);
                }
                catch (Exception warmEx) when (
                    warmEx is HttpRequestException or InvalidOperationException or TaskCanceledException)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }

                    // Continue to full build; warmup failure is diagnostic only unless full build also fails.
                    _logger?.LogWarning(
                        warmEx,
                        "[SEMANTIC INDEX] ModelWarmup=failed — continuing with batched build");
                }

                var inputs = documents.Select(d => d.EmbeddingInput).ToArray();
                var batchCount = inputs.Length == 0
                    ? 0
                    : (int)Math.Ceiling(inputs.Length / (double)_batchSize);
                _lastBatchCount = batchCount;
                _lastCompletedBatches = 0;

                var embeddings = await EmbedInBoundedBatchesAsync(
                    inputs,
                    embedBatch,
                    batchCount,
                    cancellationToken);

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

                var dims = vectors.Count > 0 ? vectors[0].Embedding.Length : 0;
                var snapshot = new IndexSnapshot(contentHash, vectors);
                // Atomic publish after complete build only.
                _snapshot = snapshot;
                _state = SemanticIndexState.Ready;
                _failedHash = null;
                _lastFailureReason = null;
                _lastVectorDimensions = dims;

                TrySaveToDisk(contentHash, vectors);

                _logger?.LogInformation(
                    "[SEMANTIC INDEX] State={State} LoadedFromDisk=false Rebuilt=true DocumentCount={DocumentCount} " +
                    "BatchCount={Batches} CompletedBatches={Completed} Dimensions={Dims} EmbeddingModel={Model} " +
                    "DurationMs={Ms} CacheFilePath={CacheFilePath}",
                    SemanticIndexState.Ready,
                    vectors.Count,
                    batchCount,
                    _lastCompletedBatches,
                    dims,
                    _embeddingModel,
                    sw.ElapsedMilliseconds,
                    _cacheFilePath ?? "(disabled)");

                return new EnsureIndexOutcome
                {
                    Success = true,
                    Snapshot = snapshot,
                    Rebuilt = true,
                    State = SemanticIndexState.Ready,
                    CacheHit = false,
                    LoadedFromDisk = false,
                    DocumentCount = vectors.Count,
                    BatchCount = batchCount,
                    CompletedBatches = _lastCompletedBatches,
                    DurationMs = sw.ElapsedMilliseconds
                };
            }
            catch (Exception ex) when (
                ex is HttpRequestException or InvalidOperationException or TaskCanceledException or
                    OperationCanceledException or IOException)
            {
                if (cancellationToken.IsCancellationRequested && ex is OperationCanceledException)
                {
                    _state = _snapshot is null ? SemanticIndexState.NotStarted : SemanticIndexState.Ready;
                    throw;
                }

                _failedHash = contentHash;
                _failedUtc = DateTimeOffset.UtcNow;
                _lastFailureReason = Truncate(ex.Message, 200);
                _state = SemanticIndexState.Failed;

                _logger?.LogWarning(
                    ex,
                    "[SEMANTIC INDEX] State={State} DocumentCount={DocumentCount} CompletedBatches={Completed}/{Batches} " +
                    "Reason={Reason} CooldownSeconds={Cooldown}",
                    SemanticIndexState.Failed,
                    documents.Count,
                    _lastCompletedBatches,
                    _lastBatchCount,
                    _lastFailureReason,
                    (int)_failureCooldown.TotalSeconds);

                return new EnsureIndexOutcome
                {
                    Success = false,
                    State = SemanticIndexState.Failed,
                    FailureReason = _lastFailureReason,
                    DocumentCount = documents.Count,
                    BatchCount = _lastBatchCount,
                    CompletedBatches = _lastCompletedBatches,
                    DurationMs = sw.ElapsedMilliseconds
                };
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

    public void Clear()
    {
        _snapshot = null;
        _state = SemanticIndexState.NotStarted;
        _failedHash = null;
        _lastFailureReason = null;
        _lastBatchCount = 0;
        _lastCompletedBatches = 0;
        _lastVectorDimensions = 0;
    }

    /// <summary>
    /// Resolves the cache directory against ASP.NET ContentRootPath when relative.
    /// Never uses the process working directory, user profile, temp, or bin output folder.
    /// </summary>
    internal static string ResolveCacheDirectory(string? configuredDirectory, string? contentRootPath)
    {
        var relativeOrAbsolute = string.IsNullOrWhiteSpace(configuredDirectory)
            ? DefaultRelativeCacheDirectory
            : configuredDirectory.Trim();

        if (Path.IsPathRooted(relativeOrAbsolute))
        {
            return Path.GetFullPath(relativeOrAbsolute);
        }

        if (string.IsNullOrWhiteSpace(contentRootPath))
        {
            throw new InvalidOperationException(
                "SemanticMetadata:CacheDirectory is relative but ContentRootPath is unavailable. " +
                "Configure an absolute CacheDirectory or provide IHostEnvironment.");
        }

        return Path.GetFullPath(Path.Combine(contentRootPath, relativeOrAbsolute));
    }

    private async Task<IReadOnlyList<float[]>> EmbedInBoundedBatchesAsync(
        IReadOnlyList<string> inputs,
        Func<IReadOnlyList<string>, CancellationToken, Task<IReadOnlyList<float[]>>> embedBatch,
        int batchCount,
        CancellationToken cancellationToken)
    {
        if (inputs.Count == 0)
        {
            return Array.Empty<float[]>();
        }

        var all = new List<float[]>(inputs.Count);
        var batchIndex = 0;
        for (var offset = 0; offset < inputs.Count; offset += _batchSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            batchIndex++;
            var take = Math.Min(_batchSize, inputs.Count - offset);
            var chunk = new string[take];
            for (var i = 0; i < take; i++)
            {
                chunk[i] = inputs[offset + i];
            }

            var batchSw = Stopwatch.StartNew();
            var vectors = await embedBatch(chunk, cancellationToken);
            if (vectors.Count != chunk.Length)
            {
                throw new InvalidOperationException(
                    $"Embedding chunk size mismatch: expected {chunk.Length}, got {vectors.Count}.");
            }

            all.AddRange(vectors);
            _lastCompletedBatches = batchIndex;

            _logger?.LogInformation(
                "[SEMANTIC INDEX] EmbedBatch Completed={Completed}/{Total} ChunkSize={ChunkSize} DurationMs={Ms}",
                batchIndex,
                batchCount,
                chunk.Length,
                batchSw.ElapsedMilliseconds);
        }

        return all;
    }

    private bool TryLoadFromDisk(
        string contentHash,
        IReadOnlyList<MetadataSearchDocument> documents,
        out IReadOnlyList<IndexedMetadataVector> vectors)
    {
        vectors = Array.Empty<IndexedMetadataVector>();
        if (!_diskCacheEnabled || string.IsNullOrWhiteSpace(_cacheFilePath))
        {
            _logger?.LogInformation(
                "[SEMANTIC INDEX] CacheValidation Result=skipped Reason=disk-cache-disabled");
            return false;
        }

        var exists = File.Exists(_cacheFilePath);
        _logger?.LogInformation(
            "[SEMANTIC INDEX] CacheFilePath={CacheFilePath} Exists={Exists}",
            _cacheFilePath,
            exists);

        if (!exists)
        {
            _logger?.LogInformation(
                "[SEMANTIC INDEX] CacheValidation Result=miss Reason=missing RebuildRequired=true");
            return false;
        }

        try
        {
            var json = File.ReadAllText(_cacheFilePath);
            var cache = JsonSerializer.Deserialize<DiskCacheFile>(json, JsonOptions);
            if (cache is null)
            {
                LogCacheInvalid("deserialize-failed");
                return false;
            }

            if (cache.Version != CacheFormatVersion)
            {
                LogCacheInvalid(
                    $"schema-version mismatch cache={cache.Version} expected={CacheFormatVersion}");
                return false;
            }

            var hashMatch = string.Equals(cache.ContentHash, contentHash, StringComparison.Ordinal);
            _logger?.LogInformation(
                "[SEMANTIC INDEX] ContentHash Match={Match} CachePrefix={CacheHash} CurrentPrefix={CurrentHash}",
                hashMatch,
                Truncate(cache.ContentHash, 16),
                Truncate(contentHash, 16));
            if (!hashMatch)
            {
                LogCacheInvalid("content-hash-mismatch");
                return false;
            }

            if (!string.Equals(cache.EmbeddingModel, _embeddingModel, StringComparison.OrdinalIgnoreCase))
            {
                LogCacheInvalid(
                    $"model-mismatch cache={cache.EmbeddingModel} current={_embeddingModel}");
                return false;
            }

            if (_expectedDimensions > 0 && cache.Dimensions != _expectedDimensions)
            {
                LogCacheInvalid(
                    $"dimensions-mismatch cache={cache.Dimensions} expected={_expectedDimensions}");
                return false;
            }

            if (cache.DocumentCount != documents.Count ||
                cache.Entries is null ||
                cache.Entries.Count != documents.Count)
            {
                LogCacheInvalid(
                    $"completeness-mismatch cacheDocs={cache.DocumentCount} currentDocs={documents.Count}");
                return false;
            }

            var loaded = new List<IndexedMetadataVector>(documents.Count);
            for (var i = 0; i < documents.Count; i++)
            {
                var entry = cache.Entries[i];
                if (entry?.Embedding is null ||
                    !string.Equals(entry.Id, documents[i].Id, StringComparison.Ordinal))
                {
                    LogCacheInvalid($"document-id-mismatch index={i}");
                    return false;
                }

                if (_expectedDimensions > 0 && entry.Embedding.Length != _expectedDimensions)
                {
                    LogCacheInvalid(
                        $"vector-dimensions-mismatch index={i} len={entry.Embedding.Length}");
                    return false;
                }

                if (cache.Dimensions > 0 && entry.Embedding.Length != cache.Dimensions)
                {
                    LogCacheInvalid(
                        $"cache-entry-dimensions-mismatch index={i} len={entry.Embedding.Length}");
                    return false;
                }

                loaded.Add(new IndexedMetadataVector
                {
                    Document = documents[i],
                    Embedding = entry.Embedding
                });
            }

            _logger?.LogInformation(
                "[SEMANTIC INDEX] CacheValidation Result=valid Model={Model} Dimensions={Dims} DocumentCount={Count}",
                cache.EmbeddingModel,
                cache.Dimensions,
                loaded.Count);

            vectors = loaded;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            _logger?.LogWarning(
                ex,
                "[SEMANTIC INDEX] CacheValidation Result=invalid Reason=corrupted-or-unreadable RebuildRequired=true " +
                "CacheFilePath={CacheFilePath}",
                _cacheFilePath);
            return false;
        }
    }

    private void LogCacheInvalid(string reason) =>
        _logger?.LogInformation(
            "[SEMANTIC INDEX] CacheValidation Result=invalid Reason={Reason} RebuildRequired=true CacheFilePath={CacheFilePath}",
            reason,
            _cacheFilePath);

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
            // Atomic replace: write temp then swap so readers never see a partial file.
            File.WriteAllText(tempPath, json);

            if (File.Exists(_cacheFilePath))
            {
                File.Replace(tempPath, _cacheFilePath, destinationBackupFileName: null);
            }
            else
            {
                File.Move(tempPath, _cacheFilePath);
            }

            _logger?.LogInformation(
                "[SEMANTIC INDEX] CacheSave Success=true CacheFilePath={CacheFilePath} DocumentCount={DocumentCount} " +
                "Dimensions={Dims} EmbeddingModel={Model} ContentHashPrefix={Hash}",
                _cacheFilePath,
                vectors.Count,
                dimensions,
                _embeddingModel,
                Truncate(contentHash, 16));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            _logger?.LogWarning(
                ex,
                "[SEMANTIC INDEX] CacheSave Success=false CacheFilePath={CacheFilePath} — continuing with in-memory index only",
                _cacheFilePath);
        }
    }

    private static string Truncate(string? value, int max)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= max)
        {
            return value ?? string.Empty;
        }

        return value[..max];
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
