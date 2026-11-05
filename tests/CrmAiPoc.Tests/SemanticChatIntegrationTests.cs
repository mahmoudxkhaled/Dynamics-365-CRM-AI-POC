using CrmAiPoc.Api.Ai;
using CrmAiPoc.Api.Configuration;
using CrmAiPoc.Api.Crm.Dynamics;
using CrmAiPoc.Api.Crm.Generic;
using CrmAiPoc.Api.Crm.Metadata;
using CrmAiPoc.Api.Crm.Metadata.Semantic;
using CrmAiPoc.Api.Models;
using CrmAiPoc.Api.Security;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CrmAiPoc.Tests;

/// <summary>Non-live unit tests for ChatService + semantic catalog integration.</summary>
public sealed class SemanticChatPipelineUnitTests
{
    [Fact]
    public async Task ChatService_Uses_Semantic_Catalog_And_Single_Groq_Call()
    {
        var harness = await CreateHarnessAsync(semanticError: null);
        harness.Recorder.Reset();

        var response = await harness.Chat.ChatAsync(
            new ChatRequestDto { Message = "Show the latest 3 products." },
            CancellationToken.None);

        Assert.Equal(1, harness.Recorder.CallCount);
        Assert.Equal("Show the latest 3 products.", harness.Recorder.LastUserMessage);
        Assert.True(harness.Recorder.LastRequestHadCatalog);
        Assert.True(harness.Recorder.LastRequestHadQueryCrmTool);
        Assert.Contains("product", harness.Recorder.LastCatalogText!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("createdon", harness.Recorder.LastCatalogText!, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("campaign", harness.Recorder.LastCatalogText!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("poc-semantic-selected-metadata", harness.Recorder.LastCatalogText!, StringComparison.OrdinalIgnoreCase);
        // Text-only AI reply avoids exercising the mock CRM executor in this unit test.
        Assert.Null(response.SelectedTool);
        Assert.Equal("answer", response.ResponseType);
        Assert.Null(response.Suggestions);
        Assert.Contains("planned", response.Reply, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, harness.Semantic.CallCount);
        Assert.Equal("Show the latest 3 products.", harness.Semantic.LastQuestion);
        Assert.Equal(0, harness.Crm.QueryCount);
    }

    [Fact]
    public async Task Ambiguous_Question_Returns_Clarification_Without_Crm_Query()
    {
        var harness = await CreateHarnessAsync(
            semanticError: null,
            aiMode: RecordingChatClient.Mode.Clarification);
        harness.Recorder.Reset();
        harness.Crm.Reset();

        var response = await harness.Chat.ChatAsync(
            new ChatRequestDto { Message = "Show the latest 5 products." },
            CancellationToken.None);

        Assert.Equal(1, harness.Recorder.CallCount);
        Assert.Equal(1, harness.Semantic.CallCount);
        Assert.Equal(0, harness.Crm.QueryCount);
        Assert.Equal("clarification", response.ResponseType);
        Assert.Null(response.SelectedTool);
        Assert.Null(response.CrmDataProvider);
        Assert.NotNull(response.Suggestions);
        Assert.Equal(2, response.Suggestions!.Count);
        Assert.All(response.Suggestions, s =>
        {
            Assert.False(string.IsNullOrWhiteSpace(s.Label));
            Assert.Contains("products", s.Question, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(' ', s.Question);
        });
        Assert.Contains("Which of these", response.Reply, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Malformed_Clarification_Returns_Safe_Error_Without_Crm()
    {
        var harness = await CreateHarnessAsync(
            semanticError: null,
            aiMode: RecordingChatClient.Mode.MalformedClarification);
        harness.Crm.Reset();

        var response = await harness.Chat.ChatAsync(
            new ChatRequestDto { Message = "Show the latest 5 products." },
            CancellationToken.None);

        Assert.Equal(1, harness.Recorder.CallCount);
        Assert.Equal(0, harness.Crm.QueryCount);
        Assert.NotEqual("clarification", response.ResponseType);
        Assert.Null(response.Suggestions);
        Assert.Contains("could not prepare clarification", response.Reply, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Unauthorized_Field_Suggestion_Is_Rejected()
    {
        var harness = await CreateHarnessAsync(
            semanticError: null,
            aiMode: RecordingChatClient.Mode.UnauthorizedAttributeClarification);
        harness.Crm.Reset();

        var response = await harness.Chat.ChatAsync(
            new ChatRequestDto { Message = "Show the latest 5 products." },
            CancellationToken.None);

        Assert.Equal(0, harness.Crm.QueryCount);
        Assert.Null(response.Suggestions);
        Assert.Contains("could not prepare clarification", response.Reply, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Selected_Clarification_Question_Triggers_Fresh_Semantic_Retrieval()
    {
        var harness = await CreateHarnessAsync(semanticError: null);
        harness.Recorder.Reset();
        harness.Semantic.Reset();

        _ = await harness.Chat.ChatAsync(
            new ChatRequestDto { Message = "Show the latest 5 products." },
            CancellationToken.None);
        Assert.Equal(1, harness.Semantic.CallCount);

        // Clicked suggestion is a brand-new /api/chat message — no Groq history, fresh retrieval.
        _ = await harness.Chat.ChatAsync(
            new ChatRequestDto { Message = "Show the latest 5 products by Created On." },
            CancellationToken.None);

        Assert.Equal(2, harness.Semantic.CallCount);
        Assert.Equal("Show the latest 5 products by Created On.", harness.Semantic.LastQuestion);
        Assert.Equal(2, harness.Recorder.CallCount);
        // Each independent /api/chat request sends exactly one user message (no Groq chat history).
        Assert.Equal(1, harness.Recorder.LastOutboundUserMessageCount);
    }

    [Fact]
    public async Task ChatService_Does_Not_Call_Groq_When_Semantic_Fails()
    {
        var harness = await CreateHarnessAsync(semanticError: "Connection refused");
        harness.Recorder.Reset();

        var response = await harness.Chat.ChatAsync(
            new ChatRequestDto { Message = "Show the latest 3 products." },
            CancellationToken.None);

        Assert.Equal(0, harness.Recorder.CallCount);
        Assert.Null(response.SelectedTool);
        Assert.Contains("semantic metadata retrieval failed", response.Reply, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Connection refused", response.Reply, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Backend_Validator_Still_Sees_Full_Authorized_Metadata()
    {
        var harness = await CreateHarnessAsync(semanticError: null);

        // Semantic catalog sent to AI is product-only, but salesorder remains fully authorized in cache.
        var approved = await harness.Metadata.GetApprovedEntitiesAsync();
        Assert.Contains(approved, e => e.LogicalName.Equals("salesorder", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(approved, e => e.LogicalName.Equals("product", StringComparison.OrdinalIgnoreCase));

        var salesorder = approved.First(e => e.LogicalName.Equals("salesorder", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(salesorder.Attributes, a => a.LogicalName == "totalamount");

        // Unauthorized relationship remains blocked by approval flag on authoritative metadata.
        Assert.DoesNotContain(
            salesorder.Relationships.Where(r => r.IsApproved),
            r => r.SchemaName.Equals("unapproved_rel", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Semantic_Catalog_Builder_Uses_Authoritative_Attribute_Metadata()
    {
        var options = Options.Create(new CrmOptions
        {
            AllowSendApprovedMetadataCatalogToAi = true,
            ApprovedEntities = ["product", "salesorder"]
        });
        var policy = new EntityApprovalPolicy(options);
        var registry = new CrmMetadataRegistry();
        registry.Upsert(MakeProduct());
        registry.Upsert(MakeSalesOrder());
        var catalog = new CrmAiMetadataCatalogService(
            registry,
            policy,
            new CrmRelationshipLabeler(options),
            options,
            NullLogger<CrmAiMetadataCatalogService>.Instance);

        var retrieval = new SemanticMetadataRetrievalResult
        {
            Question = "latest products",
            ContentHash = "abc",
            EntityHits =
            [
                new SemanticHit
                {
                    Similarity = 0.9f,
                    Document = new MetadataSearchDocument
                    {
                        Id = "entity:product",
                        Kind = MetadataSearchDocumentKind.Entity,
                        EntityLogicalName = "product",
                        SearchText = "x",
                        EmbeddingInput = "x"
                    }
                }
            ],
            AttributeHits =
            [
                HitAttr("product", "createdon", 0.8f),
                HitAttr("product", "name", 0.7f),
                HitAttr("product", "productid", 0.7f)
            ],
            RelationshipHits = []
        };

        var built = await catalog.BuildCatalogFromSemanticRetrievalAsync(retrieval);
        Assert.True(built.Success, built.Error);
        Assert.Equal("SemanticRetrieval", built.Source);
        Assert.Equal(1, built.IncludedEntityCount);
        var product = Assert.Single(built.Catalog.Detail);
        Assert.Equal("product", product.LogicalName);
        Assert.Contains(product.Attributes, a => a.LogicalName == "createdon" && a.Type == "DateTime" && a.Sortable);
        Assert.Contains(product.Attributes, a => a.LogicalName == "name");
        Assert.Contains(product.Attributes, a => a.LogicalName == "productid");
        Assert.DoesNotContain(product.Attributes, a => a.LogicalName == "price");
        Assert.Contains("poc-semantic-selected-metadata", built.FormattedBlock, StringComparison.Ordinal);
    }

    private static async Task<Harness> CreateHarnessAsync(
        string? semanticError,
        RecordingChatClient.Mode aiMode = RecordingChatClient.Mode.PlainText)
    {
        var crmOptions = new CrmOptions
        {
            Provider = "Mock",
            EnableLiveIntegration = false,
            AllowSendApprovedMetadataCatalogToAi = true,
            ApprovedEntities = ["product", "salesorder"],
            ApprovedRelationships = []
        };
        var opts = Options.Create(crmOptions);
        var policy = new EntityApprovalPolicy(opts);
        var registry = new CrmMetadataRegistry();
        registry.Upsert(MakeProduct());
        registry.Upsert(MakeSalesOrder());
        var metadata = new MockCrmMetadataService(registry, policy);

        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        services.AddSingleton(opts);
        services.AddSingleton(Options.Create(new AiOptions { Provider = "Groq", ProviderTimeoutSeconds = 60 }));
        services.AddSingleton(policy);
        services.AddSingleton(registry);
        services.AddSingleton<ICrmMetadataService>(metadata);
        services.AddSingleton(new CrmRelationshipLabeler(opts));
        services.AddSingleton<ICrmAiMetadataCatalogService, CrmAiMetadataCatalogService>();
        services.AddSingleton<ConversationStore>();
        services.AddSingleton<ICrmAuthorizationService, CrmAuthorizationService>();
        services.AddSingleton<IGenericCrmQueryBuilder, GenericCrmQueryBuilder>();
        services.AddSingleton<FetchXmlAggregateBuilder>();
        services.AddSingleton<MockGenericCrmRepository>();
        services.AddSingleton<TrackingGenericCrmRepository>();
        services.AddSingleton<IGenericCrmRepository>(sp => sp.GetRequiredService<TrackingGenericCrmRepository>());
        services.AddHttpClient(DynamicsCrmHttp.HttpClientName);
        services.AddSingleton<DynamicsCrmHttp>();
        services.AddSingleton<DynamicsConnectivityService>();
        services.AddScoped<CrmAiTools>();

        var semantic = new ScriptedSemanticRetriever(semanticError, registry);
        services.AddSingleton<ISemanticCrmMetadataRetriever>(semantic);

        var recorder = new RecordingChatClient { ResponseMode = aiMode };
        services.AddSingleton<IChatClient>(recorder);
        services.AddScoped<ChatService>();

        var provider = services.BuildServiceProvider();
        var scope = provider.CreateScope();
        await Task.CompletedTask;
        return new Harness(
            provider,
            scope,
            scope.ServiceProvider.GetRequiredService<ChatService>(),
            recorder,
            semantic,
            metadata,
            scope.ServiceProvider.GetRequiredService<TrackingGenericCrmRepository>());
    }

    private static SemanticHit HitAttr(string entity, string attr, float score) =>
        new()
        {
            Similarity = score,
            Document = new MetadataSearchDocument
            {
                Id = $"attribute:{entity}.{attr}",
                Kind = MetadataSearchDocumentKind.Attribute,
                EntityLogicalName = entity,
                AttributeLogicalName = attr,
                SearchText = "x",
                EmbeddingInput = "x"
            }
        };

    private static CrmEntityMetadata MakeProduct() =>
        new()
        {
            LogicalName = "product",
            EntitySetName = "products",
            DisplayName = "Product",
            PrimaryIdAttribute = "productid",
            PrimaryNameAttribute = "name",
            IsApprovedForAi = true,
            Attributes =
            [
                new()
                {
                    LogicalName = "productid",
                    ODataPropertyName = "productid",
                    Kind = CrmAttributeKind.Uniqueidentifier,
                    IsValidForRead = true
                },
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
                    LogicalName = "price",
                    ODataPropertyName = "price",
                    DisplayName = "List Price",
                    Kind = CrmAttributeKind.Money,
                    IsValidForRead = true,
                    IsSortable = true
                }
            ]
        };

    private static CrmEntityMetadata MakeSalesOrder() =>
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
                new()
                {
                    LogicalName = "salesorderid",
                    ODataPropertyName = "salesorderid",
                    Kind = CrmAttributeKind.Uniqueidentifier,
                    IsValidForRead = true
                },
                new()
                {
                    LogicalName = "name",
                    ODataPropertyName = "name",
                    Kind = CrmAttributeKind.String,
                    IsValidForRead = true
                },
                new()
                {
                    LogicalName = "totalamount",
                    ODataPropertyName = "totalamount",
                    DisplayName = "Total Amount",
                    Kind = CrmAttributeKind.Money,
                    IsValidForRead = true,
                    IsSortable = true
                }
            ],
            Relationships =
            [
                new CrmRelationshipMetadata
                {
                    SchemaName = "unapproved_rel",
                    Kind = CrmRelationshipKind.ManyToOne,
                    ReferencingEntity = "salesorder",
                    ReferencedEntity = "systemuser",
                    ReferencingAttribute = "ownerid",
                    ReferencedAttribute = "systemuserid",
                    IsApproved = false
                }
            ]
        };

    private sealed class ScriptedSemanticRetriever : ISemanticCrmMetadataRetriever
    {
        private readonly string? _error;
        private readonly CrmMetadataRegistry _registry;

        public ScriptedSemanticRetriever(string? error, CrmMetadataRegistry registry)
        {
            _error = error;
            _registry = registry;
        }

        public int CallCount { get; private set; }
        public string? LastQuestion { get; private set; }

        public Task<SemanticMetadataRetrievalResult> RetrieveAsync(
            string userQuestion,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            LastQuestion = userQuestion;
            if (!string.IsNullOrWhiteSpace(_error))
            {
                return Task.FromResult(new SemanticMetadataRetrievalResult
                {
                    Question = userQuestion,
                    ContentHash = string.Empty,
                    Error = _error
                });
            }

            _ = _registry.TryGet("product", out var product);
            return Task.FromResult(new SemanticMetadataRetrievalResult
            {
                Question = userQuestion,
                ContentHash = "test",
                IndexedDocumentCount = 10,
                RetrievedDocumentCount = 4,
                EntityHits =
                [
                    new SemanticHit
                    {
                        Similarity = 0.9f,
                        Document = new MetadataSearchDocument
                        {
                            Id = "entity:product",
                            Kind = MetadataSearchDocumentKind.Entity,
                            EntityLogicalName = "product",
                            DisplayName = product?.DisplayName,
                            SearchText = "product",
                            EmbeddingInput = "product"
                        }
                    }
                ],
                AttributeHits =
                [
                    HitAttr("product", "createdon", 0.85f),
                    HitAttr("product", "modifiedon", 0.84f),
                    HitAttr("product", "name", 0.8f),
                    HitAttr("product", "productid", 0.8f)
                ],
                RelationshipHits = []
            });
        }

        public void Reset()
        {
            CallCount = 0;
            LastQuestion = null;
        }
    }

    private sealed class TrackingGenericCrmRepository(MockGenericCrmRepository inner) : IGenericCrmRepository
    {
        public int QueryCount { get; private set; }
        public string ProviderName => inner.ProviderName;

        public void Reset() => QueryCount = 0;

        public Task<GenericCrmQueryResult> QueryAsync(
            CrmQueryPlan plan,
            CancellationToken cancellationToken = default)
        {
            QueryCount++;
            return inner.QueryAsync(plan, cancellationToken);
        }
    }

    private sealed class Harness(
        ServiceProvider root,
        IServiceScope scope,
        ChatService chat,
        RecordingChatClient recorder,
        ScriptedSemanticRetriever semantic,
        ICrmMetadataService metadata,
        TrackingGenericCrmRepository crm) : IAsyncDisposable
    {
        public ChatService Chat { get; } = chat;
        public RecordingChatClient Recorder { get; } = recorder;
        public ScriptedSemanticRetriever Semantic { get; } = semantic;
        public ICrmMetadataService Metadata { get; } = metadata;
        public TrackingGenericCrmRepository Crm { get; } = crm;

        public async ValueTask DisposeAsync()
        {
            scope.Dispose();
            await root.DisposeAsync();
        }
    }

    private sealed class RecordingChatClient : IChatClient
    {
        public enum Mode
        {
            PlainText,
            Clarification,
            MalformedClarification,
            UnauthorizedAttributeClarification
        }

        public Mode ResponseMode { get; set; } = Mode.PlainText;
        public int CallCount { get; private set; }
        public string? LastUserMessage { get; private set; }
        public bool LastRequestHadCatalog { get; private set; }
        public bool LastRequestHadQueryCrmTool { get; private set; }
        public string? LastCatalogText { get; private set; }
        public int LastOutboundUserMessageCount { get; private set; }

        public void Reset()
        {
            CallCount = 0;
            LastUserMessage = null;
            LastRequestHadCatalog = false;
            LastRequestHadQueryCrmTool = false;
            LastCatalogText = null;
            LastOutboundUserMessageCount = 0;
        }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> chatMessages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var messages = chatMessages.ToList();
            CallCount++;
            LastUserMessage = messages.LastOrDefault(m => m.Role == ChatRole.User)?.Text;
            LastOutboundUserMessageCount = messages.Count(m => m.Role == ChatRole.User);
            LastCatalogText = messages.FirstOrDefault(m =>
                m.Text?.Contains(CrmAiMetadataCatalogService.BeginMarker, StringComparison.Ordinal) == true)?.Text;
            LastRequestHadCatalog = LastCatalogText is not null;
            LastRequestHadQueryCrmTool = options?.Tools?.Count > 0;
            Assert.Equal(ChatToolMode.Auto, options?.ToolMode);

            var text = ResponseMode switch
            {
                Mode.Clarification =>
                    """
                    {"type":"clarification","message":"Which of these queries would you like to run?","suggestions":[{"label":"By Created On","question":"Show the latest 5 products by Created On.","entityLogicalName":"product","attributeLogicalNames":["createdon"]},{"label":"By Modified On","question":"Show the latest 5 products by Modified On.","entityLogicalName":"product","attributeLogicalNames":["modifiedon"]}]}
                    """,
                Mode.MalformedClarification =>
                    """
                    {"type":"clarification","message":"Pick one","suggestions":[{"label":"Bad","question":"Created On","entityLogicalName":"product","attributeLogicalNames":["createdon"]}]}
                    """,
                Mode.UnauthorizedAttributeClarification =>
                    """
                    {"type":"clarification","message":"Which of these queries would you like to run?","suggestions":[{"label":"Fake","question":"Show the latest 5 products by Secret Field.","entityLogicalName":"product","attributeLogicalNames":["secret_field"]},{"label":"By Created On","question":"Show the latest 5 products by Created On.","entityLogicalName":"product","attributeLogicalNames":["createdon"]}]}
                    """,
                _ => "Query planned from semantic catalog."
            };

            var message = new ChatMessage(ChatRole.Assistant, text);
            return Task.FromResult(new ChatResponse([message]));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> chatMessages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
