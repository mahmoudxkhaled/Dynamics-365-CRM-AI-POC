using CrmAiPoc.Api.Configuration;
using CrmAiPoc.Api.Crm.Metadata;
using CrmAiPoc.Api.Crm.Metadata.Semantic;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CrmAiPoc.Tests;

public sealed class SemanticMetadataRetrievalTests
{
    [Fact]
    public void CosineSimilarity_Identical_Vectors_Is_One()
    {
        float[] a = [1f, 0f, 0f];
        Assert.Equal(1f, CosineSimilarity.Compute(a, a), precision: 5);
    }

    [Fact]
    public void CosineSimilarity_Orthogonal_Is_Zero()
    {
        float[] a = [1f, 0f];
        float[] b = [0f, 1f];
        Assert.Equal(0f, CosineSimilarity.Compute(a, b), precision: 5);
    }

    [Fact]
    public void Ollama_ParseEmbeddings_Reads_Batch_Array()
    {
        var json = """{"model":"nomic-embed-text","embeddings":[[0.1,0.2,0.3],[0.4,0.5,0.6]]}""";
        var parsed = OllamaEmbeddingClient.ParseEmbeddings(json, expectedCount: 2);
        Assert.Equal(2, parsed.Count);
        Assert.Equal(3, parsed[0].Length);
        Assert.Equal(0.1f, parsed[0][0], precision: 5);
        Assert.Equal(0.6f, parsed[1][2], precision: 5);
    }

    [Fact]
    public void Ollama_ParseEmbeddings_Reads_Legacy_Single_Embedding()
    {
        var json = """{"embedding":[1,2,3]}""";
        var parsed = OllamaEmbeddingClient.ParseEmbeddings(json, expectedCount: 1);
        Assert.Single(parsed);
        Assert.Equal([1f, 2f, 3f], parsed[0]);
    }

    [Fact]
    public void DocumentBuilder_Uses_Nomic_Prefixes_And_Approved_Relationships_Only()
    {
        var entity = MakeProductEntity();
        var docs = MetadataDocumentBuilder.Build([entity]);
        Assert.Contains(docs, d => d.Kind == MetadataSearchDocumentKind.Entity && d.EntityLogicalName == "product");
        Assert.Contains(docs, d => d.Id == "attribute:product.price");
        Assert.Contains(docs, d => d.Id.StartsWith("relationship:approved_rel", StringComparison.Ordinal));
        Assert.DoesNotContain(docs, d => d.RelationshipSchemaName == "unapproved_rel");
        Assert.All(docs, d => Assert.StartsWith("search_document:", d.EmbeddingInput, StringComparison.Ordinal));
        Assert.StartsWith("search_query:", MetadataDocumentBuilder.PrefixQuery("hello"), StringComparison.Ordinal);
    }

    [Fact]
    public void DocumentBuilder_ContentHash_Changes_When_Attribute_Added()
    {
        var a = MakeProductEntity();
        var hash1 = MetadataDocumentBuilder.ComputeContentHash([a]);
        var richer = MakeProductEntity(extraAttr: true);
        var hash2 = MetadataDocumentBuilder.ComputeContentHash([richer]);
        Assert.NotEqual(hash1, hash2);
    }

    [Fact]
    public async Task Index_Reuses_Snapshot_Until_Hash_Changes()
    {
        var index = new InMemorySemanticMetadataIndex();
        var docs = MetadataDocumentBuilder.Build([MakeProductEntity()]);
        var hash = MetadataDocumentBuilder.ComputeContentHash([MakeProductEntity()]);
        var embedCalls = 0;

        Task<IReadOnlyList<float[]>> Embed(IReadOnlyList<string> inputs, CancellationToken _)
        {
            embedCalls++;
            return Task.FromResult<IReadOnlyList<float[]>>(
                inputs.Select((_, i) => MakeUnitVector(i)).ToArray());
        }

        var (s1, rebuilt1) = await index.EnsureBuiltAsync(hash, docs, Embed, CancellationToken.None);
        var (s2, rebuilt2) = await index.EnsureBuiltAsync(hash, docs, Embed, CancellationToken.None);
        Assert.Same(s1, s2);
        Assert.True(rebuilt1);
        Assert.False(rebuilt2);
        Assert.Equal(1, embedCalls);

        var richer = MakeProductEntity(extraAttr: true);
        var docs2 = MetadataDocumentBuilder.Build([richer]);
        var hash2 = MetadataDocumentBuilder.ComputeContentHash([richer]);
        var (s3, rebuilt3) = await index.EnsureBuiltAsync(hash2, docs2, Embed, CancellationToken.None);
        Assert.NotSame(s1, s3);
        Assert.True(rebuilt3);
        Assert.Equal(2, embedCalls);
    }

    [Fact]
    public async Task Index_Concurrent_Callers_Single_Flight_One_Embed()
    {
        var index = new InMemorySemanticMetadataIndex();
        var docs = MetadataDocumentBuilder.Build([MakeProductEntity()]);
        var hash = MetadataDocumentBuilder.ComputeContentHash([MakeProductEntity()]);
        var embedCalls = 0;
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        Task<IReadOnlyList<float[]>> Embed(IReadOnlyList<string> inputs, CancellationToken _)
        {
            Interlocked.Increment(ref embedCalls);
            return gate.Task.ContinueWith(
                _ => (IReadOnlyList<float[]>)inputs.Select((_, i) => MakeUnitVector(i)).ToArray(),
                TaskScheduler.Default);
        }

        var t1 = index.EnsureBuiltAsync(hash, docs, Embed, CancellationToken.None);
        var t2 = index.EnsureBuiltAsync(hash, docs, Embed, CancellationToken.None);

        // Allow the in-flight embed to complete after both callers are waiting/building.
        await Task.Delay(50);
        gate.SetResult();

        var r1 = await t1;
        var r2 = await t2;

        Assert.Same(r1.Snapshot, r2.Snapshot);
        Assert.Equal(1, embedCalls);
        Assert.True(r1.Rebuilt ^ r2.Rebuilt); // exactly one reports the actual rebuild
        Assert.True(r1.Rebuilt || r2.Rebuilt);
    }

    [Fact]
    public async Task Retriever_Ranks_By_Similarity_And_Filters_Unapproved()
    {
        var options = Options.Create(new CrmOptions
        {
            ApprovedEntities = ["product", "salesorder"]
        });
        var policy = new EntityApprovalPolicy(options);
        var registry = new CrmMetadataRegistry();
        registry.Upsert(MakeProductEntity());
        registry.Upsert(MakeSalesOrderEntity());
        var metadata = new MockCrmMetadataService(registry, policy);

        // Deterministic fake embeddings: query aligns with salesorder.totalamount document.
        var fake = new ScriptedEmbeddingClient((input, _) =>
        {
            if (input.StartsWith("search_query:", StringComparison.Ordinal))
            {
                return Task.FromResult(MakeUnitVector(42));
            }

            if (input.Contains("totalamount", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(MakeUnitVector(42));
            }

            if (input.Contains("logicalName=salesorder", StringComparison.OrdinalIgnoreCase) &&
                input.Contains("CRM entity", StringComparison.Ordinal))
            {
                return Task.FromResult(MakeUnitVector(41));
            }

            return Task.FromResult(MakeUnitVector(1));
        });

        var retriever = new SemanticCrmMetadataRetriever(
            metadata,
            policy,
            fake,
            new InMemorySemanticMetadataIndex(),
            Options.Create(new SemanticMetadataOptions
            {
                TopKEntities = 3,
                TopKAttributesPerEntity = 10,
                TopKRelationships = 5
            }),
            NullLogger<SemanticCrmMetadataRetriever>.Instance);

        var result = await retriever.RetrieveAsync("Show the top 5 Sales Orders by total amount.");
        Assert.Null(result.Error);
        Assert.True(result.IndexedDocumentCount > 0);
        Assert.Contains(result.AttributeHits, h => h.Document.AttributeLogicalName == "totalamount");
        Assert.Contains(result.EntityHits, h => h.Document.EntityLogicalName == "salesorder");
        Assert.DoesNotContain(result.RelationshipHits, h =>
            h.Document.RelationshipSchemaName == "unapproved_rel");
        // Structural primary attributes are always included for selected entities.
        Assert.Contains(result.AttributeHits, h =>
            h.Document.EntityLogicalName == "salesorder" &&
            h.Document.AttributeLogicalName == "salesorderid");
        Assert.Contains(result.AttributeHits, h =>
            h.Document.EntityLogicalName == "salesorder" &&
            h.Document.AttributeLogicalName == "name");
    }

    [Fact]
    public async Task Retriever_Scopes_Attributes_Per_Selected_Entity()
    {
        var options = Options.Create(new CrmOptions
        {
            ApprovedEntities = ["product", "salesorder"]
        });
        var policy = new EntityApprovalPolicy(options);
        var registry = new CrmMetadataRegistry();
        registry.Upsert(MakeProductEntity());
        registry.Upsert(MakeSalesOrderEntity());
        var metadata = new MockCrmMetadataService(registry, policy);

        // Query aligns with product entity; salesorder attrs should still only appear if salesorder is selected.
        var fake = new ScriptedEmbeddingClient((input, _) =>
        {
            if (input.StartsWith("search_query:", StringComparison.Ordinal))
            {
                return Task.FromResult(MakeUnitVector(50));
            }

            if (input.Contains("CRM entity", StringComparison.Ordinal) &&
                input.Contains("logicalName=product", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(MakeUnitVector(50));
            }

            if (input.Contains("entity=product", StringComparison.OrdinalIgnoreCase) &&
                input.Contains("createdon", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(MakeUnitVector(49));
            }

            if (input.Contains("CRM entity", StringComparison.Ordinal) &&
                input.Contains("logicalName=salesorder", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(MakeUnitVector(10));
            }

            return Task.FromResult(MakeUnitVector(1));
        });

        var retriever = new SemanticCrmMetadataRetriever(
            metadata,
            policy,
            fake,
            new InMemorySemanticMetadataIndex(),
            Options.Create(new SemanticMetadataOptions
            {
                TopKEntities = 1,
                TopKAttributesPerEntity = 5,
                TopKRelationships = 5
            }),
            NullLogger<SemanticCrmMetadataRetriever>.Instance);

        var result = await retriever.RetrieveAsync("Show the latest 3 products.");
        Assert.Null(result.Error);
        Assert.Single(result.EntityHits);
        Assert.Equal("product", result.EntityHits[0].Document.EntityLogicalName);
        Assert.Contains(result.AttributeHits, h =>
            h.Document.EntityLogicalName == "product" &&
            h.Document.AttributeLogicalName == "createdon");
        Assert.DoesNotContain(result.AttributeHits, h =>
            h.Document.EntityLogicalName == "salesorder");
        Assert.All(result.AttributeHits.Where(h =>
                !string.Equals(h.Document.AttributeLogicalName, "productid", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(h.Document.AttributeLogicalName, "name", StringComparison.OrdinalIgnoreCase)),
            h => Assert.Equal("product", h.Document.EntityLogicalName));
    }

    [Fact]
    public async Task Retriever_Returns_Error_When_Ollama_Fails()
    {
        var options = Options.Create(new CrmOptions { ApprovedEntities = ["product"] });
        var policy = new EntityApprovalPolicy(options);
        var registry = new CrmMetadataRegistry();
        registry.Upsert(MakeProductEntity());
        var metadata = new MockCrmMetadataService(registry, policy);
        var failing = new ScriptedEmbeddingClient((_, _) =>
            throw new HttpRequestException("Connection refused"));

        var retriever = new SemanticCrmMetadataRetriever(
            metadata,
            policy,
            failing,
            new InMemorySemanticMetadataIndex(),
            Options.Create(new SemanticMetadataOptions()),
            NullLogger<SemanticCrmMetadataRetriever>.Instance);

        var result = await retriever.RetrieveAsync("latest products");
        Assert.NotNull(result.Error);
        Assert.Contains("Connection refused", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Retriever_Empty_Question_Fails_Controlled()
    {
        var options = Options.Create(new CrmOptions { ApprovedEntities = ["product"] });
        var policy = new EntityApprovalPolicy(options);
        var retriever = new SemanticCrmMetadataRetriever(
            new MockCrmMetadataService(new CrmMetadataRegistry(), policy),
            policy,
            new ScriptedEmbeddingClient((_, _) => Task.FromResult(MakeUnitVector(0))),
            new InMemorySemanticMetadataIndex(),
            Options.Create(new SemanticMetadataOptions()),
            NullLogger<SemanticCrmMetadataRetriever>.Instance);

        var result = await retriever.RetrieveAsync("  ");
        Assert.Equal("Question is required.", result.Error);
    }

    [Fact]
    public async Task Retriever_Includes_CreatedOn_And_ModifiedOn_As_Structural_Even_When_Not_TopK()
    {
        var options = Options.Create(new CrmOptions { ApprovedEntities = ["product"] });
        var policy = new EntityApprovalPolicy(options);
        var registry = new CrmMetadataRegistry();
        registry.Upsert(MakeProductEntity());
        var metadata = new MockCrmMetadataService(registry, policy);

        // Boost only entity + price; leave createdon/modifiedon at baseline similarity.
        var fake = new ScriptedEmbeddingClient((input, _) =>
        {
            if (input.StartsWith("search_query:", StringComparison.Ordinal))
            {
                return Task.FromResult(MakeUnitVector(50));
            }

            if (input.Contains("CRM entity", StringComparison.Ordinal) &&
                input.Contains("logicalName=product", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(MakeUnitVector(50));
            }

            if (input.Contains("entity=product", StringComparison.OrdinalIgnoreCase) &&
                input.Contains("logicalName=price", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(MakeUnitVector(49));
            }

            return Task.FromResult(MakeUnitVector(1));
        });

        var retriever = new SemanticCrmMetadataRetriever(
            metadata,
            policy,
            fake,
            new InMemorySemanticMetadataIndex(),
            Options.Create(new SemanticMetadataOptions
            {
                TopKEntities = 1,
                TopKAttributesPerEntity = 1,
                TopKRelationships = 0
            }),
            NullLogger<SemanticCrmMetadataRetriever>.Instance);

        var result = await retriever.RetrieveAsync("product price list");
        Assert.Null(result.Error);
        Assert.Contains(result.AttributeHits, h =>
            h.Document.EntityLogicalName == "product" &&
            h.Document.AttributeLogicalName == "createdon");
        Assert.Contains(result.AttributeHits, h =>
            h.Document.EntityLogicalName == "product" &&
            h.Document.AttributeLogicalName == "modifiedon");
        Assert.Contains(result.AttributeHits, h =>
            h.Document.EntityLogicalName == "product" &&
            h.Document.AttributeLogicalName == "productid");
        Assert.Contains(result.AttributeHits, h =>
            h.Document.EntityLogicalName == "product" &&
            h.Document.AttributeLogicalName == "name");
    }

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
                LogicalName = "createdon",
                ODataPropertyName = "createdon",
                DisplayName = "Created On",
                Kind = CrmAttributeKind.DateTime,
                IsValidForRead = true,
                IsSortable = true,
                IsFilterable = true
            },
            new()
            {
                LogicalName = "modifiedon",
                ODataPropertyName = "modifiedon",
                DisplayName = "Modified On",
                Kind = CrmAttributeKind.DateTime,
                IsValidForRead = true,
                IsSortable = true,
                IsFilterable = true
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
                },
                new CrmRelationshipMetadata
                {
                    SchemaName = "unapproved_rel",
                    Kind = CrmRelationshipKind.ManyToOne,
                    ReferencingEntity = "product",
                    ReferencedEntity = "systemuser",
                    ReferencingAttribute = "ownerid",
                    ReferencedAttribute = "systemuserid",
                    IsApproved = false
                }
            ]
        };
    }

    private static CrmEntityMetadata MakeSalesOrderEntity() =>
        new()
        {
            LogicalName = "salesorder",
            EntitySetName = "salesorders",
            DisplayName = "Sales Order",
            PrimaryIdAttribute = "salesorderid",
            PrimaryNameAttribute = "name",
            IsApprovedForAi = true,
            Attributes =
            [
                new CrmAttributeMetadata
                {
                    LogicalName = "name",
                    ODataPropertyName = "name",
                    Kind = CrmAttributeKind.String,
                    IsValidForRead = true
                },
                new CrmAttributeMetadata
                {
                    LogicalName = "totalamount",
                    ODataPropertyName = "totalamount",
                    DisplayName = "Total Amount",
                    Kind = CrmAttributeKind.Money,
                    IsValidForRead = true,
                    IsSortable = true
                },
                new CrmAttributeMetadata
                {
                    LogicalName = "salesorderid",
                    ODataPropertyName = "salesorderid",
                    Kind = CrmAttributeKind.Uniqueidentifier,
                    IsValidForRead = true
                }
            ],
            Relationships =
            [
                new CrmRelationshipMetadata
                {
                    SchemaName = "te_account_salesorder_customeraccountid",
                    Kind = CrmRelationshipKind.ManyToOne,
                    ReferencingEntity = "salesorder",
                    ReferencedEntity = "account",
                    ReferencingAttribute = "customeraccountid",
                    ReferencedAttribute = "accountid",
                    BusinessLabel = "Customer Account",
                    IsApproved = true
                }
            ]
        };

    private static float[] MakeUnitVector(int seed)
    {
        var v = new float[8];
        v[Math.Abs(seed) % v.Length] = 1f;
        return v;
    }

    private sealed class ScriptedEmbeddingClient : IOllamaEmbeddingClient
    {
        private readonly Func<string, CancellationToken, Task<float[]>> _embed;

        public ScriptedEmbeddingClient(Func<string, CancellationToken, Task<float[]>> embed) =>
            _embed = embed;

        public Task<float[]> EmbedAsync(string input, CancellationToken cancellationToken = default) =>
            _embed(input, cancellationToken);

        public async Task<IReadOnlyList<float[]>> EmbedBatchAsync(
            IReadOnlyList<string> inputs,
            CancellationToken cancellationToken = default)
        {
            var list = new List<float[]>(inputs.Count);
            foreach (var input in inputs)
            {
                list.Add(await _embed(input, cancellationToken));
            }

            return list;
        }
    }
}
