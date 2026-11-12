using CrmAiPoc.Api.Configuration;
using CrmAiPoc.Api.Crm.Metadata.Semantic;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CrmAiPoc.Tests;

/// <summary>SEM-03.2: project-local persistent embedding cache under ContentRootPath.</summary>
public sealed class SemanticDiskCacheTests
{
    private const int Dims = 16;

    [Fact]
    public void ResolveCacheDirectory_Uses_ContentRoot_Not_BaseDirectory()
    {
        var contentRoot = Path.Combine(Path.GetTempPath(), "crm-poc-content-root-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(contentRoot);
        try
        {
            var resolved = InMemorySemanticMetadataIndex.ResolveCacheDirectory(
                "Cache/SemanticIndex",
                contentRoot);

            Assert.Equal(
                Path.GetFullPath(Path.Combine(contentRoot, "Cache", "SemanticIndex")),
                resolved);
            Assert.DoesNotContain(
                Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar,
                resolved,
                StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            TryDelete(contentRoot);
        }
    }

    [Fact]
    public async Task First_Build_Creates_Cache_File_Under_ContentRoot()
    {
        await using var harness = DiskHarness.Create();
        var callsBefore = harness.Embedder.BatchCallCount;

        var first = await harness.Index.EnsureBuiltAsync(
            harness.Hash,
            harness.Docs,
            harness.Embedder.EmbedBatchAsync,
            CancellationToken.None);

        Assert.True(first.Success);
        Assert.True(first.Rebuilt);
        Assert.False(first.LoadedFromDisk);
        Assert.True(harness.Embedder.BatchCallCount > callsBefore);
        Assert.True(File.Exists(harness.CacheFilePath));
        Assert.StartsWith(harness.ContentRoot, harness.CacheFilePath, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            Path.Combine("Cache", "SemanticIndex"),
            harness.CacheFilePath,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Second_Startup_Loads_From_Disk_Without_ReEmbedding()
    {
        await using var harness = DiskHarness.Create();

        var first = await harness.Index.EnsureBuiltAsync(
            harness.Hash,
            harness.Docs,
            harness.Embedder.EmbedBatchAsync,
            CancellationToken.None);
        Assert.True(first.Success);
        Assert.True(File.Exists(harness.CacheFilePath));
        var embedCallsAfterBuild = harness.Embedder.BatchCallCount;

        // Simulate process restart: new index instance, same cache file + content root.
        var restarted = harness.CreateFreshIndex();
        var second = await restarted.EnsureBuiltAsync(
            harness.Hash,
            harness.Docs,
            harness.Embedder.EmbedBatchAsync,
            CancellationToken.None);

        Assert.True(second.Success);
        Assert.True(second.LoadedFromDisk);
        Assert.False(second.Rebuilt);
        Assert.Equal(SemanticIndexState.Ready, second.State);
        Assert.Equal(harness.Docs.Count, second.DocumentCount);
        Assert.Equal(embedCallsAfterBuild, harness.Embedder.BatchCallCount);
    }

    [Fact]
    public async Task ContentHash_Mismatch_Rebuilds_And_Updates_Cache()
    {
        await using var harness = DiskHarness.Create();
        await harness.Index.EnsureBuiltAsync(
            harness.Hash,
            harness.Docs,
            harness.Embedder.EmbedBatchAsync,
            CancellationToken.None);

        var newHash = harness.Hash + "|changed";
        var restarted = harness.CreateFreshIndex();
        var callsBefore = harness.Embedder.BatchCallCount;
        var outcome = await restarted.EnsureBuiltAsync(
            newHash,
            harness.Docs,
            harness.Embedder.EmbedBatchAsync,
            CancellationToken.None);

        Assert.True(outcome.Success);
        Assert.True(outcome.Rebuilt);
        Assert.False(outcome.LoadedFromDisk);
        Assert.True(harness.Embedder.BatchCallCount > callsBefore);

        var json = await File.ReadAllTextAsync(harness.CacheFilePath);
        Assert.Contains(newHash, json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Model_Change_Rejects_Cache_And_Rebuilds()
    {
        await using var harness = DiskHarness.Create();
        await harness.Index.EnsureBuiltAsync(
            harness.Hash,
            harness.Docs,
            harness.Embedder.EmbedBatchAsync,
            CancellationToken.None);

        var otherModel = harness.CreateFreshIndex(embeddingModel: "other-embed-model");
        var callsBefore = harness.Embedder.BatchCallCount;
        var outcome = await otherModel.EnsureBuiltAsync(
            harness.Hash,
            harness.Docs,
            harness.Embedder.EmbedBatchAsync,
            CancellationToken.None);

        Assert.True(outcome.Success);
        Assert.True(outcome.Rebuilt);
        Assert.False(outcome.LoadedFromDisk);
        Assert.True(harness.Embedder.BatchCallCount > callsBefore);
    }

    [Fact]
    public async Task Corrupted_Cache_Recovers_Without_Crash()
    {
        await using var harness = DiskHarness.Create();
        await harness.Index.EnsureBuiltAsync(
            harness.Hash,
            harness.Docs,
            harness.Embedder.EmbedBatchAsync,
            CancellationToken.None);

        await File.WriteAllTextAsync(harness.CacheFilePath, "{ not-valid-json");

        var restarted = harness.CreateFreshIndex();
        var outcome = await restarted.EnsureBuiltAsync(
            harness.Hash,
            harness.Docs,
            harness.Embedder.EmbedBatchAsync,
            CancellationToken.None);

        Assert.True(outcome.Success);
        Assert.True(outcome.Rebuilt);
        Assert.False(outcome.LoadedFromDisk);
        Assert.Equal(SemanticIndexState.Ready, outcome.State);
        Assert.True(File.Exists(harness.CacheFilePath));
        var json = await File.ReadAllTextAsync(harness.CacheFilePath);
        Assert.Contains("\"version\":1", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Valid_Cache_Is_Not_Deleted_When_Missing_Validation_Fails_Before_Rebuild()
    {
        // Ensure Resolve path keeps existing file when only probing Exists — no delete API.
        var contentRoot = Path.Combine(Path.GetTempPath(), "crm-poc-cache-keep-" + Guid.NewGuid().ToString("N"));
        var cacheDir = Path.Combine(contentRoot, "Cache", "SemanticIndex");
        Directory.CreateDirectory(cacheDir);
        var cacheFile = Path.Combine(cacheDir, InMemorySemanticMetadataIndex.CacheFileName);
        File.WriteAllText(cacheFile, """{"version":1,"contentHash":"x","embeddingModel":"m","dimensions":16,"documentCount":0,"entries":[]}""");
        try
        {
            Assert.True(File.Exists(cacheFile));
            var resolved = InMemorySemanticMetadataIndex.ResolveCacheDirectory(
                InMemorySemanticMetadataIndex.DefaultRelativeCacheDirectory,
                contentRoot);
            Assert.Equal(Path.GetFullPath(cacheDir), resolved);
            Assert.True(File.Exists(cacheFile));
        }
        finally
        {
            TryDelete(contentRoot);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
            // best-effort cleanup
        }
    }

    private sealed class DiskHarness : IAsyncDisposable
    {
        public required string ContentRoot { get; init; }
        public required string CacheFilePath { get; init; }
        public required InMemorySemanticMetadataIndex Index { get; init; }
        public required FakeEmbedder Embedder { get; init; }
        public required IReadOnlyList<MetadataSearchDocument> Docs { get; init; }
        public required string Hash { get; init; }
        public required FakeHostEnvironment Host { get; init; }

        public static DiskHarness Create(int documentCount = 6)
        {
            var contentRoot = Path.Combine(
                Path.GetTempPath(),
                "crm-poc-semantic-cache-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(contentRoot);
            var host = new FakeHostEnvironment(contentRoot);
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

            var index = CreateIndex(host, "fake-embed");
            return new DiskHarness
            {
                ContentRoot = contentRoot,
                CacheFilePath = index.CacheFilePath!,
                Index = index,
                Embedder = new FakeEmbedder(Dims),
                Docs = docs,
                Hash = $"hash-{documentCount}|fake-embed|{Dims}",
                Host = host
            };
        }

        public InMemorySemanticMetadataIndex CreateFreshIndex(string embeddingModel = "fake-embed") =>
            CreateIndex(Host, embeddingModel);

        private static InMemorySemanticMetadataIndex CreateIndex(FakeHostEnvironment host, string embeddingModel)
        {
            var semantic = Options.Create(new SemanticMetadataOptions
            {
                EnableDiskCache = true,
                CacheDirectory = InMemorySemanticMetadataIndex.DefaultRelativeCacheDirectory,
                EmbeddingBatchSize = 8,
                IndexFailureCooldownSeconds = 30
            });
            var ollama = Options.Create(new OllamaEmbeddingOptions
            {
                EmbeddingModel = embeddingModel,
                ExpectedDimensions = Dims
            });
            return new InMemorySemanticMetadataIndex(
                semantic,
                ollama,
                NullLogger<InMemorySemanticMetadataIndex>.Instance,
                host);
        }

        public ValueTask DisposeAsync()
        {
            TryDelete(ContentRoot);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeHostEnvironment(string contentRoot) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "CrmAiPoc.Tests";
        public string ContentRootPath { get; set; } = contentRoot;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private sealed class FakeEmbedder(int dimensions)
    {
        public int BatchCallCount { get; private set; }

        public Task<IReadOnlyList<float[]>> EmbedBatchAsync(
            IReadOnlyList<string> inputs,
            CancellationToken cancellationToken)
        {
            BatchCallCount++;
            return Task.FromResult<IReadOnlyList<float[]>>(
                inputs.Select(input =>
                {
                    var vector = new float[dimensions];
                    var idx = Math.Abs((input ?? string.Empty).GetHashCode(StringComparison.Ordinal)) % dimensions;
                    vector[idx] = 1f;
                    return vector;
                }).ToArray());
        }
    }
}
