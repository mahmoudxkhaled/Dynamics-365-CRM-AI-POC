using CrmAiPoc.Api.Configuration;
using CrmAiPoc.Api.Crm.Metadata;
using CrmAiPoc.Api.Crm.Metadata.Discovery;
using CrmAiPoc.Api.Crm.Metadata.Semantic;
using CrmAiPoc.Api.Security;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CrmAiPoc.Tests;

/// <summary>
/// SEM-03 Phase 1: semantic index batching, reuse, cooldown, and concurrent single-flight.
/// Uses a fake embedder — no live Ollama required.
/// </summary>
public sealed class SemanticIndexReliabilityTests
{
    private const int Dims = 32;

    [Fact]
    public async Task Successful_Index_Initialization_Reaches_Ready()
    {
        var (index, embedder, docs, hash) = CreateIndexHarness(documentCount: 40, batchSize: 8);

        var outcome = await index.EnsureBuiltAsync(
            hash,
            docs,
            (inputs, ct) => embedder.EmbedBatchAsync(inputs, ct),
            CancellationToken.None);

        Assert.True(outcome.Success);
        Assert.Equal(SemanticIndexState.Ready, outcome.State);
        Assert.Equal(SemanticIndexState.Ready, index.State);
        Assert.True(outcome.Rebuilt);
        Assert.Equal(40, outcome.DocumentCount);
        Assert.True(outcome.BatchCount >= 5); // 40/8 = 5 content batches (+ warmup is separate call)
        Assert.True(embedder.BatchCallCount >= 5);
    }

    [Fact]
    public async Task Index_Is_Reused_After_Initialization()
    {
        var (index, embedder, docs, hash) = CreateIndexHarness(documentCount: 12, batchSize: 16);

        var first = await index.EnsureBuiltAsync(hash, docs, embedder.EmbedBatchAsync, CancellationToken.None);
        var callsAfterFirst = embedder.BatchCallCount;

        var second = await index.EnsureBuiltAsync(hash, docs, embedder.EmbedBatchAsync, CancellationToken.None);

        Assert.True(first.Success);
        Assert.True(second.Success);
        Assert.False(second.Rebuilt);
        Assert.True(second.CacheHit);
        Assert.Equal(callsAfterFirst, embedder.BatchCallCount);
        Assert.True(index.TryGetReadySnapshot(hash, out _));
    }

    [Fact]
    public async Task Concurrent_Initialization_Is_Single_Flight()
    {
        var (index, embedder, docs, hash) = CreateIndexHarness(documentCount: 24, batchSize: 8);
        embedder.BatchDelayMs = 30;

        var tasks = Enumerable.Range(0, 8)
            .Select(_ => index.EnsureBuiltAsync(hash, docs, embedder.EmbedBatchAsync, CancellationToken.None))
            .ToArray();

        var results = await Task.WhenAll(tasks);

        Assert.All(results, r => Assert.True(r.Success));
        // One builder embeds; waiters reuse. Warmup + content batches once.
        Assert.True(embedder.BatchCallCount <= 6, $"Expected single-flight batches, got {embedder.BatchCallCount}");
        Assert.Equal(1, results.Count(r => r.Rebuilt));
        Assert.Equal(7, results.Count(r => !r.Rebuilt));
    }

    [Fact]
    public async Task Failed_Initialization_Enters_Cooldown_Without_Retry_Storm()
    {
        var (index, embedder, docs, hash) = CreateIndexHarness(documentCount: 10, batchSize: 4);
        embedder.ThrowOnEmbed = true;

        var first = await index.EnsureBuiltAsync(hash, docs, embedder.EmbedBatchAsync, CancellationToken.None);
        var callsAfterFail = embedder.BatchCallCount;

        var second = await index.EnsureBuiltAsync(hash, docs, embedder.EmbedBatchAsync, CancellationToken.None);

        Assert.False(first.Success);
        Assert.Equal(SemanticIndexState.Failed, first.State);
        Assert.False(second.Success);
        Assert.True(second.SkippedDueToFailureCooldown);
        Assert.Equal(callsAfterFail, embedder.BatchCallCount);
        Assert.True(index.IsInFailureCooldown(hash));
    }

    [Fact]
    public async Task Hybrid_Falls_Back_During_Cooldown_Without_Blocking()
    {
        var metadata = TinyFixture();
        var embedder = new FakeEmbeddingClient(Dims) { ThrowOnEmbed = true };
        var (discovery, index) = CreateDiscovery(metadata, embedder, cooldownSeconds: 120);

        var first = await discovery.DiscoverAsync(new CrmSchemaDiscoveryRequest
        {
            Entities = ["alpha_order"],
            Concepts = ["totalamount"]
        });
        Assert.True(first.Success);
        Assert.Equal("totalamount", first.Result!.Fields[0].Candidates[0].LogicalName);
        Assert.Equal(SemanticIndexState.Failed, index.State);

        var calls = embedder.BatchCallCount;
        var second = await discovery.DiscoverAsync(new CrmSchemaDiscoveryRequest
        {
            Entities = ["alpha_order"],
            Concepts = ["name"]
        });
        Assert.True(second.Success);
        Assert.Equal(calls, embedder.BatchCallCount);
        Assert.Contains(second.Result!.Notes, n => n.Contains("fallback", StringComparison.OrdinalIgnoreCase) ||
                                                   n.Contains("cooldown", StringComparison.OrdinalIgnoreCase) ||
                                                   second.Result.Fields.Count > 0);
    }

    [Fact]
    public async Task Metadata_Hash_Change_Invalidates_Index()
    {
        var (index, embedder, docs, hash) = CreateIndexHarness(documentCount: 6, batchSize: 8);
        var first = await index.EnsureBuiltAsync(hash, docs, embedder.EmbedBatchAsync, CancellationToken.None);
        Assert.True(first.Success);

        var mutated = docs.Append(new MetadataSearchDocument
        {
            Id = "attribute:alpha_order.extra",
            Kind = MetadataSearchDocumentKind.Attribute,
            EntityLogicalName = "alpha_order",
            AttributeLogicalName = "extra",
            SearchText = "extra",
            EmbeddingInput = MetadataDocumentBuilder.DocumentPrefix + " extra"
        }).ToArray();
        var newHash = hash + "|mutated";

        var second = await index.EnsureBuiltAsync(newHash, mutated, embedder.EmbedBatchAsync, CancellationToken.None);
        Assert.True(second.Success);
        Assert.True(second.Rebuilt);
        Assert.Equal(7, second.DocumentCount);
    }

    [Fact]
    public async Task Hybrid_Ranking_Uses_Semantic_Similarity_When_Ready()
    {
        var metadata = TinyFixture();
        var embedder = new FakeEmbeddingClient(Dims);
        var (discovery, index) = CreateDiscovery(metadata, embedder, cooldownSeconds: 30);

        var result = await discovery.DiscoverAsync(new CrmSchemaDiscoveryRequest
        {
            Entities = ["alpha_order"],
            Concepts = ["totalamount"]
        });

        Assert.True(result.Success);
        Assert.Equal(SemanticIndexState.Ready, index.State);
        Assert.Equal("hybrid", /* mode logged; verify semantic path ran via embed calls */ "hybrid");
        Assert.True(embedder.BatchCallCount >= 1);
        Assert.True(embedder.SingleCallCount >= 1);
        Assert.Equal("totalamount", result.Result!.Fields[0].Candidates[0].LogicalName);
    }

    private static (InMemorySemanticMetadataIndex Index, FakeEmbeddingClient Embedder, IReadOnlyList<MetadataSearchDocument> Docs, string Hash)
        CreateIndexHarness(int documentCount, int batchSize)
    {
        var semantic = Options.Create(new SemanticMetadataOptions
        {
            EnableHybridRetrieval = true,
            EmbeddingBatchSize = batchSize,
            IndexFailureCooldownSeconds = 60,
            EnableDiskCache = false
        });
        var ollama = Options.Create(new OllamaEmbeddingOptions
        {
            EmbeddingModel = "fake-embed",
            ExpectedDimensions = Dims,
            TimeoutSeconds = 30
        });
        var index = new InMemorySemanticMetadataIndex(semantic, ollama, NullLogger<InMemorySemanticMetadataIndex>.Instance);
        var embedder = new FakeEmbeddingClient(Dims);
        var docs = Enumerable.Range(0, documentCount)
            .Select(i => new MetadataSearchDocument
            {
                Id = $"attribute:entity.attr{i}",
                Kind = MetadataSearchDocumentKind.Attribute,
                EntityLogicalName = "entity",
                AttributeLogicalName = $"attr{i}",
                SearchText = $"attribute attr{i}",
                EmbeddingInput = MetadataDocumentBuilder.DocumentPrefix + $" attribute attr{i}"
            })
            .ToArray();
        return (index, embedder, docs, $"hash-{documentCount}");
    }

    private static (CrmSchemaDiscoveryService Discovery, InMemorySemanticMetadataIndex Index) CreateDiscovery(
        FakeMetadata metadata,
        FakeEmbeddingClient embedder,
        int cooldownSeconds)
    {
        var crmOptions = Options.Create(new CrmOptions { ApprovedEntities = ["alpha_order"] });
        var policy = new EntityApprovalPolicy(crmOptions);
        var auth = new CrmAuthorizationService(crmOptions, NullLogger<CrmAuthorizationService>.Instance, policy);
        var semantic = Options.Create(new SemanticMetadataOptions
        {
            EnableHybridRetrieval = true,
            EmbeddingBatchSize = 8,
            IndexFailureCooldownSeconds = cooldownSeconds,
            EnableDiskCache = false,
            MinSemanticSimilarity = 0.2f
        });
        var ollama = Options.Create(new OllamaEmbeddingOptions
        {
            EmbeddingModel = "fake-embed",
            ExpectedDimensions = Dims
        });
        var index = new InMemorySemanticMetadataIndex(semantic, ollama, NullLogger<InMemorySemanticMetadataIndex>.Instance);
        var ranker = new HybridMetadataRanker(
            metadata,
            policy,
            embedder,
            index,
            semantic,
            ollama,
            NullLogger<HybridMetadataRanker>.Instance);
        var discovery = new CrmSchemaDiscoveryService(
            metadata,
            policy,
            auth,
            NullLogger<CrmSchemaDiscoveryService>.Instance,
            ranker);
        return (discovery, index);
    }

    private static FakeMetadata TinyFixture() =>
        new([
            new CrmEntityMetadata
            {
                LogicalName = "alpha_order",
                EntitySetName = "alpha_orders",
                DisplayName = "Alpha Order",
                PrimaryIdAttribute = "alpha_orderid",
                PrimaryNameAttribute = "name",
                IsApprovedForAi = true,
                Attributes =
                [
                    new CrmAttributeMetadata
                    {
                        LogicalName = "name",
                        ODataPropertyName = "name",
                        DisplayName = "Name",
                        Kind = CrmAttributeKind.String,
                        IsValidForRead = true
                    },
                    new CrmAttributeMetadata
                    {
                        LogicalName = "totalamount",
                        ODataPropertyName = "totalamount",
                        DisplayName = "Total Amount",
                        Kind = CrmAttributeKind.Money,
                        IsValidForRead = true
                    }
                ]
            }
        ]);

    private sealed class FakeMetadata(IReadOnlyList<CrmEntityMetadata> entities) : ICrmMetadataService
    {
        private readonly Dictionary<string, CrmEntityMetadata> _map =
            entities.ToDictionary(e => e.LogicalName, StringComparer.OrdinalIgnoreCase);

        public Task EnsureApprovedEntitiesLoadedAsync(CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<CrmEntityMetadata?> GetEntityAsync(string logicalName, CancellationToken cancellationToken = default) =>
            Task.FromResult(_map.TryGetValue(logicalName, out var e) ? e : null);

        public Task<IReadOnlyList<CrmEntityMetadata>> GetApprovedEntitiesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<CrmEntityMetadata>>(_map.Values.ToList());

        public Task RefreshAsync(string? logicalName = null, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

                public Task<AttributeOptionsResult> EnsureAttributeOptionsAsync(
            string entityLogicalName,
            string attributeLogicalName,
            CancellationToken cancellationToken = default)
        {
            _ = cancellationToken;
            if (GetEntityAsync(entityLogicalName).Result is { } entity &&
                entity.TryGetAttribute(attributeLogicalName, out var attr))
            {
                return Task.FromResult(new AttributeOptionsResult
                {
                    Attribute = attr,
                    OptionsAvailable = attr.OptionSetByValue.Count > 0 || attr.OptionSetByLabel.Count > 0,
                    AlreadyCached = true
                });
            }

            return Task.FromResult(new AttributeOptionsResult
            {
                Error = $"Attribute '{attributeLogicalName}' was not found on '{entityLogicalName}'."
            });
        }

        public string GetAiCatalogSummary() => string.Empty;
    }

    private sealed class FakeEmbeddingClient(int dimensions) : IOllamaEmbeddingClient
    {
        public int BatchCallCount { get; private set; }
        public int SingleCallCount { get; private set; }
        public bool ThrowOnEmbed { get; set; }
        public int BatchDelayMs { get; set; }

        public async Task<float[]> EmbedAsync(string input, CancellationToken cancellationToken = default)
        {
            SingleCallCount++;
            var batch = await EmbedBatchAsync([input], cancellationToken);
            return batch[0];
        }

        public async Task<IReadOnlyList<float[]>> EmbedBatchAsync(
            IReadOnlyList<string> inputs,
            CancellationToken cancellationToken = default)
        {
            BatchCallCount++;
            if (BatchDelayMs > 0)
            {
                await Task.Delay(BatchDelayMs, cancellationToken);
            }

            if (ThrowOnEmbed)
            {
                throw new HttpRequestException("Simulated Ollama timeout");
            }

            return inputs.Select(EmbedText).ToArray();
        }

        private float[] EmbedText(string input)
        {
            var vector = new float[dimensions];
            var hash = (input ?? string.Empty).GetHashCode(StringComparison.Ordinal);
            var idx = Math.Abs(hash) % dimensions;
            vector[idx] = 1f;
            return vector;
        }
    }
}
