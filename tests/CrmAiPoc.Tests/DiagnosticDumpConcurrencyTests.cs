using System.Reflection;
using CrmAiPoc.Api.Ai;
using CrmAiPoc.Api.Configuration;
using CrmAiPoc.Api.Crm.Dynamics;
using CrmAiPoc.Api.Crm.Generic;
using CrmAiPoc.Api.Crm.Metadata;
using CrmAiPoc.Api.Crm.Metadata.Semantic;
using CrmAiPoc.Api.Security;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CrmAiPoc.Tests;

public sealed class DiagnosticDumpConcurrencyTests
{
    [Fact]
    public async Task Concurrent_Diagnostic_Dumps_Use_Unique_CorrelationId_Paths()
    {
        var previous = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");
        Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Development");
        try
        {
            var method = GetDumpMethod();
            var chat = CreateMinimalChatService();
            var ids = Enumerable.Range(0, 12).Select(_ => Guid.NewGuid().ToString("N")[..12]).ToArray();

            await Task.WhenAll(ids.Select(id => Task.Run(() =>
                method.Invoke(chat, [id, $"<<<APPROVED_CRM_METADATA_CATALOG>>>\n{id}\n<<<END>>>"]))));

            var paths = ids
                .Select(id => Path.Combine(Path.GetTempPath(), $"crm-poc-ai-request-metadata-catalog-{id}.txt"))
                .ToArray();

            Assert.Equal(ids.Length, paths.Distinct(StringComparer.OrdinalIgnoreCase).Count());
            Assert.All(paths, p => Assert.True(File.Exists(p), $"Missing dump: {p}"));
            Assert.All(paths, p => Assert.Contains(Path.GetFileNameWithoutExtension(p).Split('-').Last(), File.ReadAllText(p)));

            foreach (var path in paths)
            {
                TryDelete(path);
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", previous);
        }
    }

    [Fact]
    public void Diagnostic_Write_Failure_Is_Swallowed()
    {
        var previous = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");
        Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Development");
        try
        {
            var method = GetDumpMethod();
            var chat = CreateMinimalChatService();
            var id = "blockedwrite01";
            var path = Path.Combine(Path.GetTempPath(), $"crm-poc-ai-request-metadata-catalog-{id}.txt");

            // Force File.WriteAllText to fail: a directory occupying the target path.
            TryDelete(path);
            Directory.CreateDirectory(path);
            try
            {
                var ex = Record.Exception(() =>
                    method.Invoke(chat, [id, "<<<APPROVED_CRM_METADATA_CATALOG>>>\n{}\n<<<END>>>"]));
                // TargetInvocationException would wrap a thrown exception from the method.
                Assert.Null(ex);
            }
            finally
            {
                try
                {
                    Directory.Delete(path, recursive: true);
                }
                catch
                {
                    // best-effort
                }
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", previous);
        }
    }

    [Fact]
    public void ClientRetryPolicy_Accepts_Zero_MaxRetries()
    {
        // LazyGroqChatClient sets RetryPolicy = new ClientRetryPolicy(0) so app-level
        // timeout/429 handling stays authoritative. Default SDK policy is maxRetries=3.
        var zero = new System.ClientModel.Primitives.ClientRetryPolicy(0);
        var defaults = new System.ClientModel.Primitives.ClientRetryPolicy();
        Assert.NotNull(zero);
        Assert.NotNull(defaults);
    }

    private static MethodInfo GetDumpMethod()
    {
        var method = typeof(ChatService).GetMethod(
            "TryWriteDevelopmentCatalogDump",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        return method!;
    }

    private static ChatService CreateMinimalChatService()
    {
        var crmOptions = Options.Create(new CrmOptions
        {
            Provider = "Mock",
            EnableLiveIntegration = false,
            AllowSendApprovedMetadataCatalogToAi = true,
            ApprovedEntities = ["product"]
        });
        var policy = new EntityApprovalPolicy(crmOptions);
        var registry = new CrmMetadataRegistry();
        registry.Upsert(new CrmEntityMetadata
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
                }
            ]
        });
        var metadata = new MockCrmMetadataService(registry, policy);
        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        services.AddSingleton(crmOptions);
        services.AddSingleton(Options.Create(new AiOptions { Provider = "Groq", ProviderTimeoutSeconds = 60 }));
        services.AddSingleton(policy);
        services.AddSingleton(registry);
        services.AddSingleton<ICrmMetadataService>(metadata);
        services.AddSingleton(new CrmRelationshipLabeler(crmOptions));
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
        services.AddSingleton<ISemanticCrmMetadataRetriever>(new StubSemanticRetriever());
        services.AddSingleton<IChatClient>(new NoOpChatClient());
        services.AddScoped<ChatService>();
        return services.BuildServiceProvider().CreateScope().ServiceProvider.GetRequiredService<ChatService>();
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // best-effort
        }
    }

    private sealed class StubSemanticRetriever : ISemanticCrmMetadataRetriever
    {
        public Task<SemanticMetadataRetrievalResult> RetrieveAsync(
            string userQuestion,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new SemanticMetadataRetrievalResult
            {
                Question = userQuestion,
                ContentHash = "t",
                IndexedDocumentCount = 1,
                RetrievedDocumentCount = 1,
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
                            SearchText = "product",
                            EmbeddingInput = "product"
                        }
                    }
                ],
                AttributeHits = [],
                RelationshipHits = []
            });
    }

    private sealed class NoOpChatClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> chatMessages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatResponse([new ChatMessage(ChatRole.Assistant, "ok")]));

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
