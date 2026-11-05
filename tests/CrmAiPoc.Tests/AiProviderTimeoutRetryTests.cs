using System.Net;
using CrmAiPoc.Api.Ai;
using CrmAiPoc.Api.Configuration;
using CrmAiPoc.Api.Crm.Dynamics;
using CrmAiPoc.Api.Crm.Generic;
using CrmAiPoc.Api.Crm.Metadata;
using CrmAiPoc.Api.Crm.Metadata.Semantic;
using CrmAiPoc.Api.Models;
using CrmAiPoc.Api.Security;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CrmAiPoc.Tests;

public sealed class AiProviderTimeoutRetryTests
{
    [Fact]
    public void Appsettings_Configures_ProviderTimeoutSeconds_60()
    {
        var basePath = FindApiProjectDirectory();
        Assert.False(string.IsNullOrWhiteSpace(basePath), "Could not locate src/CrmAiPoc.Api for appsettings.json");

        var config = new ConfigurationBuilder()
            .SetBasePath(basePath!)
            .AddJsonFile("appsettings.json", optional: false)
            .AddJsonFile("appsettings.Development.json", optional: true)
            .Build();

        var ai = new AiOptions();
        config.GetSection(AiOptions.SectionName).Bind(ai);
        Assert.Equal(60, ai.ProviderTimeoutSeconds);
    }

    [Fact]
    public void AiOptions_Default_ProviderTimeoutSeconds_Is_60()
    {
        Assert.Equal(60, new AiOptions().ProviderTimeoutSeconds);
    }

    [Theory]
    [InlineData("rate limit 429", AiProviderFailureKind.Http429)]
    [InlineData("HTTP 503 service unavailable", AiProviderFailureKind.Http503)]
    [InlineData("HTTP 400 bad request invalid_request", AiProviderFailureKind.Http400)]
    [InlineData("context_length_exceeded", AiProviderFailureKind.ProviderLimit)]
    [InlineData("connection refused", AiProviderFailureKind.Network)]
    public void Classifier_Maps_Common_Failures(string message, AiProviderFailureKind expected)
    {
        Assert.Equal(expected, AiProviderFailureClassifier.Classify(new InvalidOperationException(message)));
    }

    [Fact]
    public void Classifier_Maps_HttpRequestException_Status_Codes()
    {
        Assert.Equal(
            AiProviderFailureKind.Http429,
            AiProviderFailureClassifier.Classify(new HttpRequestException("too many", null, HttpStatusCode.TooManyRequests)));
        Assert.Equal(
            AiProviderFailureKind.Http503,
            AiProviderFailureClassifier.Classify(new HttpRequestException("unavailable", null, HttpStatusCode.ServiceUnavailable)));
        Assert.Equal(
            AiProviderFailureKind.Http400,
            AiProviderFailureClassifier.Classify(new HttpRequestException("bad", null, HttpStatusCode.BadRequest)));
    }

    [Fact]
    public void Transient_Retry_Delay_Honors_RetryAfter_And_Stays_Bounded()
    {
        var delay = AiProviderFailureClassifier.GetTransientRetryDelay(
            new HttpRequestException("rate limited Retry-After: 30"),
            attempt: 1);
        Assert.Equal(8, delay.TotalSeconds);

        var fallback = AiProviderFailureClassifier.GetTransientRetryDelay(
            new HttpRequestException("503"),
            attempt: 1);
        Assert.Equal(2, fallback.TotalSeconds);
    }

    [Fact]
    public async Task Client_Timeout_Does_Not_Trigger_Second_Attempt()
    {
        var client = new ScriptedFailingChatClient(FailMode.ClientTimeout);
        var harness = await CreateHarnessAsync(client, providerTimeoutSeconds: 60);

        var response = await harness.Chat.ChatAsync(
            new ChatRequestDto { Message = "Show the top 5 Sales Orders by total amount." },
            CancellationToken.None);

        Assert.Equal(1, client.CallCount);
        Assert.Contains("did not respond in time", response.Reply, StringComparison.OrdinalIgnoreCase);
        Assert.Null(response.SelectedTool);
        Assert.NotEqual("clarification", response.ResponseType);
    }

    [Fact]
    public async Task User_Cancellation_Is_Propagated_Separately()
    {
        var client = new ScriptedFailingChatClient(FailMode.HangUntilCancelled);
        var harness = await CreateHarnessAsync(client, providerTimeoutSeconds: 60);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            harness.Chat.ChatAsync(
                new ChatRequestDto { Message = "Show the top 5 Sales Orders by total amount." },
                cts.Token));
        Assert.True(client.CallCount <= 1);
    }

    [Fact]
    public async Task Http429_Uses_Bounded_Transient_Retry_Then_Succeeds()
    {
        var client = new ScriptedFailingChatClient(FailMode.Http429ThenSuccess);
        var harness = await CreateHarnessAsync(client, providerTimeoutSeconds: 60);

        var response = await harness.Chat.ChatAsync(
            new ChatRequestDto { Message = "Show the top 5 Sales Orders by total amount." },
            CancellationToken.None);

        Assert.Equal(2, client.CallCount);
        Assert.Equal("answer", response.ResponseType);
        Assert.Contains("planned", response.Reply, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Http400_Is_Classified_Without_Retry()
    {
        var client = new ScriptedFailingChatClient(FailMode.Http400);
        var harness = await CreateHarnessAsync(client, providerTimeoutSeconds: 60);

        var response = await harness.Chat.ChatAsync(
            new ChatRequestDto { Message = "Show the top 5 Sales Orders by total amount." },
            CancellationToken.None);

        Assert.Equal(1, client.CallCount);
        Assert.Contains("could not use the CRM planning tools", response.Reply, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Normal_And_Clarification_Paths_Still_Single_Stage()
    {
        var normal = new ScriptedFailingChatClient(FailMode.SuccessPlain);
        var harness = await CreateHarnessAsync(normal, providerTimeoutSeconds: 60);
        var answer = await harness.Chat.ChatAsync(
            new ChatRequestDto { Message = "Show the top 5 Sales Orders by total amount." },
            CancellationToken.None);
        Assert.Equal(1, normal.CallCount);
        Assert.Equal(1, normal.LastOutboundUserMessageCount);
        Assert.Equal("answer", answer.ResponseType);

        var clarify = new ScriptedFailingChatClient(FailMode.SuccessClarification);
        var harness2 = await CreateHarnessAsync(clarify, providerTimeoutSeconds: 60);
        var clarification = await harness2.Chat.ChatAsync(
            new ChatRequestDto { Message = "Show the latest 5 products." },
            CancellationToken.None);
        Assert.Equal(1, clarify.CallCount);
        Assert.Equal(1, clarify.LastOutboundUserMessageCount);
        Assert.Equal("clarification", clarification.ResponseType);
        Assert.NotNull(clarification.Suggestions);
        Assert.Equal(2, clarification.Suggestions!.Count);
    }

    private static string? FindApiProjectDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "src", "CrmAiPoc.Api");
            if (File.Exists(Path.Combine(candidate, "appsettings.json")))
            {
                return candidate;
            }

            candidate = Path.Combine(dir.FullName, "CrmAiPoc.Api");
            if (File.Exists(Path.Combine(candidate, "appsettings.json")))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        return null;
    }

    private static async Task<Harness> CreateHarnessAsync(
        ScriptedFailingChatClient client,
        int providerTimeoutSeconds)
    {
        var crmOptions = new CrmOptions
        {
            Provider = "Mock",
            EnableLiveIntegration = false,
            AllowSendApprovedMetadataCatalogToAi = true,
            ApprovedEntities = ["product", "salesorder"]
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
        services.AddSingleton(Options.Create(new AiOptions
        {
            Provider = "Groq",
            ProviderTimeoutSeconds = providerTimeoutSeconds
        }));
        services.AddSingleton(policy);
        services.AddSingleton(registry);
        services.AddSingleton<ICrmMetadataService>(metadata);
        services.AddSingleton(new CrmRelationshipLabeler(opts));
        services.AddSingleton<ICrmAiMetadataCatalogService, CrmAiMetadataCatalogService>();
        services.AddSingleton<ConversationStore>();
        services.AddSingleton<ICrmAuthorizationService, CrmAuthorizationService>();
        services.AddSingleton<IGenericCrmQueryBuilder, GenericCrmQueryBuilder>();
        services.AddSingleton<FetchXmlAggregateBuilder>();
        services.AddSingleton<IGenericCrmRepository, MockGenericCrmRepository>();
        services.AddHttpClient(DynamicsCrmHttp.HttpClientName);
        services.AddSingleton<DynamicsCrmHttp>();
        services.AddSingleton<DynamicsConnectivityService>();
        services.AddScoped<CrmAiTools>();
        services.AddSingleton<ISemanticCrmMetadataRetriever>(new ScriptedSemanticRetriever(registry));
        services.AddSingleton<IChatClient>(client);
        services.AddScoped<ChatService>();

        var provider = services.BuildServiceProvider();
        var scope = provider.CreateScope();
        await Task.CompletedTask;
        return new Harness(provider, scope, scope.ServiceProvider.GetRequiredService<ChatService>());
    }

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
                Attr("productid", CrmAttributeKind.Uniqueidentifier),
                Attr("name", CrmAttributeKind.String, "Name"),
                Attr("createdon", CrmAttributeKind.DateTime, "Created On", sortable: true),
                Attr("modifiedon", CrmAttributeKind.DateTime, "Modified On", sortable: true)
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
                Attr("salesorderid", CrmAttributeKind.Uniqueidentifier),
                Attr("name", CrmAttributeKind.String, "Name"),
                Attr("totalamount", CrmAttributeKind.Money, "Total Amount", sortable: true)
            ]
        };

    private static CrmAttributeMetadata Attr(
        string name,
        CrmAttributeKind kind,
        string? display = null,
        bool sortable = false) =>
        new()
        {
            LogicalName = name,
            ODataPropertyName = name,
            DisplayName = display,
            Kind = kind,
            IsValidForRead = true,
            IsSortable = sortable
        };

    private sealed class Harness(ServiceProvider root, IServiceScope scope, ChatService chat) : IAsyncDisposable
    {
        public ChatService Chat { get; } = chat;

        public async ValueTask DisposeAsync()
        {
            scope.Dispose();
            await root.DisposeAsync();
        }
    }

    private sealed class ScriptedSemanticRetriever(CrmMetadataRegistry registry) : ISemanticCrmMetadataRetriever
    {
        public Task<SemanticMetadataRetrievalResult> RetrieveAsync(
            string userQuestion,
            CancellationToken cancellationToken = default)
        {
            var entity = userQuestion.Contains("product", StringComparison.OrdinalIgnoreCase)
                ? "product"
                : "salesorder";
            _ = registry.TryGet(entity, out var meta);
            var attrs = (meta?.Attributes ?? [])
                .Where(a => a.IsValidForRead)
                .Select(a => new SemanticHit
                {
                    Similarity = 0.8f,
                    Document = new MetadataSearchDocument
                    {
                        Id = $"attribute:{entity}.{a.LogicalName}",
                        Kind = MetadataSearchDocumentKind.Attribute,
                        EntityLogicalName = entity,
                        AttributeLogicalName = a.LogicalName,
                        SearchText = a.LogicalName,
                        EmbeddingInput = a.LogicalName
                    }
                })
                .ToArray();

            return Task.FromResult(new SemanticMetadataRetrievalResult
            {
                Question = userQuestion,
                ContentHash = "test",
                IndexedDocumentCount = 10,
                RetrievedDocumentCount = attrs.Length + 1,
                EntityHits =
                [
                    new SemanticHit
                    {
                        Similarity = 0.9f,
                        Document = new MetadataSearchDocument
                        {
                            Id = $"entity:{entity}",
                            Kind = MetadataSearchDocumentKind.Entity,
                            EntityLogicalName = entity,
                            SearchText = entity,
                            EmbeddingInput = entity
                        }
                    }
                ],
                AttributeHits = attrs,
                RelationshipHits = []
            });
        }
    }

    private enum FailMode
    {
        ClientTimeout,
        HangUntilCancelled,
        Http429ThenSuccess,
        Http400,
        SuccessPlain,
        SuccessClarification
    }

    private sealed class ScriptedFailingChatClient(FailMode mode) : IChatClient
    {
        public int CallCount { get; private set; }
        public int LastOutboundUserMessageCount { get; private set; }

        public async Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> chatMessages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            var messages = chatMessages.ToList();
            LastOutboundUserMessageCount = messages.Count(m => m.Role == ChatRole.User);

            switch (mode)
            {
                case FailMode.ClientTimeout:
                    throw new OperationCanceledException("client CancelAfter");
                case FailMode.HangUntilCancelled:
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                    throw new InvalidOperationException("unreachable");
                case FailMode.Http429ThenSuccess when CallCount == 1:
                    throw new HttpRequestException(
                        "rate limit Retry-After: 2",
                        null,
                        HttpStatusCode.TooManyRequests);
                case FailMode.Http400:
                    throw new HttpRequestException("400 bad request invalid_request", null, HttpStatusCode.BadRequest);
                case FailMode.SuccessClarification:
                    return new ChatResponse(
                    [
                        new ChatMessage(
                            ChatRole.Assistant,
                            """
                            {"type":"clarification","message":"Which of these queries would you like to run?","suggestions":[{"label":"By Created On","question":"Show the latest 5 products by Created On.","entityLogicalName":"product","attributeLogicalNames":["createdon"]},{"label":"By Modified On","question":"Show the latest 5 products by Modified On.","entityLogicalName":"product","attributeLogicalNames":["modifiedon"]}]}
                            """)
                    ]);
                default:
                    return new ChatResponse(
                    [
                        new ChatMessage(ChatRole.Assistant, "Query planned from semantic catalog.")
                    ]);
            }
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
