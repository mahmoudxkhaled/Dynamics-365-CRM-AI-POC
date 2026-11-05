using System.Collections.Concurrent;

namespace CrmAiPoc.Api.Crm.Metadata.Semantic;

/// <summary>
/// In-memory embedding index keyed by metadata content hash.
/// Concurrent callers single-flight through one rebuild for a given hash.
/// </summary>
public sealed class InMemorySemanticMetadataIndex
{
    private readonly SemaphoreSlim _rebuildGate = new(1, 1);
    private readonly ConcurrentDictionary<string, byte> _buildLocks = new(StringComparer.Ordinal);
    private volatile IndexSnapshot? _snapshot;

    public IndexSnapshot? Current => _snapshot;

    public int DocumentCount => _snapshot?.Vectors.Count ?? 0;

    public string? ContentHash => _snapshot?.ContentHash;

    /// <summary>
    /// Ensures an index snapshot exists for <paramref name="contentHash"/>.
    /// Only one embed rebuild runs at a time; waiters reuse the same snapshot.
    /// <paramref name="rebuilt"/> is true only for the caller that performed the embed.
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
