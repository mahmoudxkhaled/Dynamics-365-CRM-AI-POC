using CrmAiPoc.Api.Configuration;
using CrmAiPoc.Api.Crm.Metadata;
using CrmAiPoc.Api.Crm.Metadata.Semantic;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CrmAiPoc.Tests;

public sealed class SemanticMetadataDiskCacheTests
{
    [Fact]
    public async Task First_Run_Builds_And_Saves_Cache()
    {
        using var dir = new TempCacheDir();
        var index = CreateIndex(dir.Path);
        var docs = BuildDocs();
        var hash = Hash();
        var calls = new CallCounter();

        var (snapshot, rebuilt) = await index.EnsureBuiltAsync(
            hash, docs, CountingEmbed(calls), CancellationToken.None);

        Assert.True(rebuilt);
        Assert.Equal(1, calls.Count);
        Assert.Equal(docs.Count, snapshot.Vectors.Count);
        Assert.True(File.Exists(index.CacheFilePath));
    }

    [Fact]
    public async Task Restart_With_Unchanged_Metadata_Loads_Cache_Without_Embedding()
    {
        using var dir = new TempCacheDir();
        var docs = BuildDocs();
        var hash = Hash();
        var calls = new CallCounter();
        var embed = CountingEmbed(calls);

        var first = CreateIndex(dir.Path);
        await first.EnsureBuiltAsync(hash, docs, embed, CancellationToken.None);
        Assert.Equal(1, calls.Count);

        // Simulate process restart: new index instance, same cache directory.
        var second = CreateIndex(dir.Path);
        calls.Count = 0;
        var (snapshot, rebuilt) = await second.EnsureBuiltAsync(
            hash, docs, embed, CancellationToken.None);

        Assert.False(rebuilt);
        Assert.Equal(0, calls.Count);
        Assert.Equal(docs.Count, snapshot.Vectors.Count);
        Assert.Equal(docs[0].Id, snapshot.Vectors[0].Document.Id);
        Assert.Equal(MakeUnitVector(0), snapshot.Vectors[0].Embedding);
    }

    [Fact]
    public async Task Changed_Metadata_Hash_Triggers_Rebuild_And_Overwrites_Cache()
    {
        using var dir = new TempCacheDir();
        var calls = new CallCounter();
        var embed = CountingEmbed(calls);

        var firstDocs = BuildDocs();
        var firstHash = Hash();
        var index1 = CreateIndex(dir.Path);
        await index1.EnsureBuiltAsync(firstHash, firstDocs, embed, CancellationToken.None);
        Assert.Equal(1, calls.Count);
        var cachePath = index1.CacheFilePath!;
        var firstBytes = await File.ReadAllBytesAsync(cachePath);

        var richer = MakeProductEntity(extraAttr: true);
        var secondDocs = MetadataDocumentBuilder.Build([richer]);
        var secondHash = MetadataDocumentBuilder.ComputeContentHash([richer]);
        Assert.NotEqual(firstHash, secondHash);

        var index2 = CreateIndex(dir.Path);
        var (snapshot, rebuilt) = await index2.EnsureBuiltAsync(
            secondHash, secondDocs, embed, CancellationToken.None);

        Assert.True(rebuilt);
        Assert.Equal(2, calls.Count);
        Assert.Equal(secondDocs.Count, snapshot.Vectors.Count);
        var secondBytes = await File.ReadAllBytesAsync(cachePath);
        Assert.NotEqual(firstBytes, secondBytes);
    }

    [Fact]
    public async Task Corrupted_Cache_Falls_Back_To_Build_Workflow()
    {
        using var dir = new TempCacheDir();
        var index = CreateIndex(dir.Path);
        var docs = BuildDocs();
        var hash = Hash();
        Directory.CreateDirectory(dir.Path);
        await File.WriteAllTextAsync(index.CacheFilePath!, "{ not-valid-json");

        var calls = new CallCounter();
        var (snapshot, rebuilt) = await index.EnsureBuiltAsync(
            hash, docs, CountingEmbed(calls), CancellationToken.None);

        Assert.True(rebuilt);
        Assert.Equal(1, calls.Count);
        Assert.Equal(docs.Count, snapshot.Vectors.Count);
        Assert.True(File.Exists(index.CacheFilePath));
    }

    [Fact]
    public async Task Concurrent_Initialization_Does_Not_Duplicate_Builds()
    {
        using var dir = new TempCacheDir();
        var index = CreateIndex(dir.Path);
        var docs = BuildDocs();
        var hash = Hash();
        var calls = new CallCounter();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        Task<IReadOnlyList<float[]>> EmbedSlow(IReadOnlyList<string> inputs, CancellationToken _)
        {
            Interlocked.Increment(ref calls.Count);
            return gate.Task.ContinueWith(
                _ => (IReadOnlyList<float[]>)inputs.Select((_, i) => MakeUnitVector(i)).ToArray(),
                TaskScheduler.Default);
        }

        var t1 = index.EnsureBuiltAsync(hash, docs, EmbedSlow, CancellationToken.None);
        var t2 = index.EnsureBuiltAsync(hash, docs, EmbedSlow, CancellationToken.None);
        await Task.Delay(50);
        gate.SetResult();

        var r1 = await t1;
        var r2 = await t2;

        Assert.Same(r1.Snapshot, r2.Snapshot);
        Assert.Equal(1, calls.Count);
        Assert.True(r1.Rebuilt ^ r2.Rebuilt);
        Assert.True(File.Exists(index.CacheFilePath));
    }

    [Fact]
    public async Task Incompatible_Model_Name_Invalidates_Cache()
    {
        using var dir = new TempCacheDir();
        var docs = BuildDocs();
        var hash = Hash();
        var calls = new CallCounter();
        var embed = CountingEmbed(calls);

        var first = CreateIndex(dir.Path, embeddingModel: "nomic-embed-text");
        await first.EnsureBuiltAsync(hash, docs, embed, CancellationToken.None);

        var second = CreateIndex(dir.Path, embeddingModel: "other-model");
        calls.Count = 0;
        var (_, rebuilt) = await second.EnsureBuiltAsync(hash, docs, embed, CancellationToken.None);

        Assert.True(rebuilt);
        Assert.Equal(1, calls.Count);
    }

    private static InMemorySemanticMetadataIndex CreateIndex(
        string cacheDirectory,
        string embeddingModel = "nomic-embed-text",
        int expectedDimensions = 8)
    {
        return new InMemorySemanticMetadataIndex(
            Options.Create(new SemanticMetadataOptions
            {
                EnableDiskCache = true,
                CacheDirectory = cacheDirectory
            }),
            Options.Create(new OllamaOptions
            {
                EmbeddingModel = embeddingModel,
                ExpectedDimensions = expectedDimensions
            }),
            NullLogger<InMemorySemanticMetadataIndex>.Instance);
    }

    private static Func<IReadOnlyList<string>, CancellationToken, Task<IReadOnlyList<float[]>>> CountingEmbed(
        CallCounter calls) =>
        (inputs, _) =>
        {
            Interlocked.Increment(ref calls.Count);
            return Task.FromResult<IReadOnlyList<float[]>>(
                inputs.Select((_, i) => MakeUnitVector(i)).ToArray());
        };

    private sealed class CallCounter
    {
        public int Count;
    }

    private static IReadOnlyList<MetadataSearchDocument> BuildDocs() =>
        MetadataDocumentBuilder.Build([MakeProductEntity()]);

    private static string Hash() =>
        MetadataDocumentBuilder.ComputeContentHash([MakeProductEntity()]);

    private static CrmEntityMetadata MakeProductEntity(bool extraAttr = false)
    {
        var attrs = new List<CrmAttributeMetadata>
        {
            new()
            {
                LogicalName = "name",
                ODataPropertyName = "name",
                DisplayName = "Name",
                Kind = CrmAttributeKind.String,
                IsValidForRead = true
            },
            new()
            {
                LogicalName = "price",
                ODataPropertyName = "price",
                DisplayName = "List Price",
                Kind = CrmAttributeKind.Money,
                IsValidForRead = true,
                IsSortable = true
            },
            new()
            {
                LogicalName = "productid",
                ODataPropertyName = "productid",
                Kind = CrmAttributeKind.Uniqueidentifier,
                IsValidForRead = true
            }
        };

        if (extraAttr)
        {
            attrs.Add(new CrmAttributeMetadata
            {
                LogicalName = "description",
                ODataPropertyName = "description",
                Kind = CrmAttributeKind.Memo,
                IsValidForRead = true
            });
        }

        return new CrmEntityMetadata
        {
            LogicalName = "product",
            EntitySetName = "products",
            DisplayName = "Product",
            PrimaryIdAttribute = "productid",
            PrimaryNameAttribute = "name",
            IsApprovedForAi = true,
            Attributes = attrs,
            Relationships =
            [
                new CrmRelationshipMetadata
                {
                    SchemaName = "approved_rel",
                    Kind = CrmRelationshipKind.ManyToOne,
                    ReferencingEntity = "product",
                    ReferencedEntity = "account",
                    ReferencingAttribute = "accountid",
                    ReferencedAttribute = "accountid",
                    IsApproved = true
                }
            ]
        };
    }

    private static float[] MakeUnitVector(int seed)
    {
        var v = new float[8];
        v[Math.Abs(seed) % v.Length] = 1f;
        return v;
    }

    private sealed class TempCacheDir : IDisposable
    {
        public string Path { get; } =
            System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "CrmAiPoc-semantic-cache-tests",
                Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Path))
                {
                    Directory.Delete(Path, recursive: true);
                }
            }
            catch
            {
                // best-effort cleanup
            }
        }
    }
}
