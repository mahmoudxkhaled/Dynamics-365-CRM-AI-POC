using System.Diagnostics;
using System.Text;
using System.Text.Json;
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
using Microsoft.Extensions.Options;

namespace CrmAiPoc.Tests;

/// <summary>
/// Phase 2 live E2E: Ollama semantic retrieval → one Groq call → query_crm → Dynamics.
/// Set CRM_LIVE_VERIFY=1. Report: %TEMP%\crm-poc-semantic-chat-phase2.txt
/// </summary>
public sealed class LiveSemanticChatIntegrationTests
{
    private static readonly string ReportPath =
        Path.Combine(Path.GetTempPath(), "crm-poc-semantic-chat-phase2.txt");

    private static readonly string[] Questions =
    [
        "Show the top 5 Sales Orders by total amount.",
        "Show the latest 3 products.",
        "Show Sales Orders with their Customer Account names.",
        "Show opportunities sorted by estimated revenue."
    ];

    [Fact]
    public async Task Live_Semantic_Chat_Four_Questions_Or_NotExecuted()
    {
        var sb = new StringBuilder();
        void L(string s) => sb.AppendLine(s);

        if (!string.Equals(Environment.GetEnvironmentVariable("CRM_LIVE_VERIFY"), "1", StringComparison.Ordinal))
        {
            L("STATUS: Not Executed — CRM_LIVE_VERIFY!=1");
            File.WriteAllText(ReportPath, sb.ToString());
            Assert.True(true);
            return;
        }

        Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Development");

        var (baseUrl, groqKey, groqModel, groqBase, ollamaBase, crmOptions) = await LoadDevConfigAsync();
        if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(groqKey))
        {
            L("STATUS: Not Executed — BaseUrl or Groq key missing");
            File.WriteAllText(ReportPath, sb.ToString());
            Assert.True(true);
            return;
        }

        try
        {
            using var probe = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            using var ping = await probe.GetAsync(ollamaBase.TrimEnd('/') + "/api/tags");
            if (!ping.IsSuccessStatusCode)
            {
                L($"STATUS: Not Executed — Ollama not reachable at {ollamaBase}");
                File.WriteAllText(ReportPath, sb.ToString());
                Assert.True(true);
                return;
            }
        }
        catch (Exception ex)
        {
            L($"STATUS: Not Executed — Ollama probe failed: {ex.Message}");
            File.WriteAllText(ReportPath, sb.ToString());
            Assert.True(true);
            return;
        }

        var services = new ServiceCollection();
        services.AddLogging(b => b.AddConsole().SetMinimumLevel(LogLevel.Information));
        services.AddSingleton(Options.Create(crmOptions));
        services.AddSingleton(Options.Create(new AiOptions
        {
            Provider = "Groq",
            ProviderTimeoutSeconds = 90
        }));
        services.AddSingleton(Options.Create(new GroqOptions
        {
            ApiKey = groqKey,
            ModelName = groqModel,
            BaseUrl = groqBase
        }));
        services.AddSingleton(Options.Create(new GeminiOptions()));
        services.AddSingleton(Options.Create(new OpenAiOptions()));
        services.AddSingleton(Options.Create(new OllamaOptions
        {
            BaseUrl = ollamaBase,
            EmbeddingModel = "nomic-embed-text",
            ExpectedDimensions = 768,
            TimeoutSeconds = 180
        }));
        services.AddSingleton(Options.Create(new SemanticMetadataOptions
        {
            TopKEntities = 3,
            TopKAttributesPerEntity = 20,
            TopKRelationships = 10,
            MinSimilarity = 0
        }));
        services.AddSingleton<EntityApprovalPolicy>();
        services.AddSingleton<CrmMetadataRegistry>();
        services.AddSingleton<CrmRelationshipLabeler>();
        services.AddSingleton<ConversationStore>();
        services.AddHttpClient(DynamicsCrmHttp.HttpClientName, client =>
            {
                client.BaseAddress = new Uri(baseUrl.EndsWith('/') ? baseUrl : baseUrl + "/");
                client.Timeout = TimeSpan.FromSeconds(120);
            })
            .ConfigurePrimaryHttpMessageHandler(() => DynamicsCrmHttp.CreateHandler(crmOptions));
        services.AddSingleton<DynamicsCrmHttp>();
        services.AddSingleton<DynamicsConnectivityService>();
        services.AddSingleton<ICrmMetadataService, DynamicsCrmMetadataService>();
        services.AddSingleton<IGenericCrmQueryBuilder, GenericCrmQueryBuilder>();
        services.AddSingleton<FetchXmlAggregateBuilder>();
        services.AddSingleton<ICrmAuthorizationService, CrmAuthorizationService>();
        services.AddSingleton<IGenericCrmRepository, DynamicsGenericCrmRepository>();
        services.AddSingleton<ICrmAiMetadataCatalogService, CrmAiMetadataCatalogService>();
        services.AddHttpClient(OllamaEmbeddingClient.HttpClientName, (sp, client) =>
        {
            var opts = sp.GetRequiredService<IOptions<OllamaOptions>>().Value;
            client.BaseAddress = new Uri(opts.BaseUrl.EndsWith('/') ? opts.BaseUrl : opts.BaseUrl + "/");
            client.Timeout = TimeSpan.FromSeconds(opts.TimeoutSeconds);
        });
        services.AddSingleton<IOllamaEmbeddingClient, OllamaEmbeddingClient>();
        services.AddSingleton<InMemorySemanticMetadataIndex>();
        services.AddSingleton<ISemanticCrmMetadataRetriever, SemanticCrmMetadataRetriever>();
        services.AddSingleton<LazyGroqChatClient>();
        services.AddSingleton<RecordingChatClient>();
        services.AddSingleton<IChatClient>(sp => sp.GetRequiredService<RecordingChatClient>());
        services.AddScoped<CrmAiTools>();
        services.AddScoped<ChatService>();

        await using var provider = services.BuildServiceProvider();
        var who = await provider.GetRequiredService<DynamicsConnectivityService>().WhoAmIAsync();
        if (!who.Success)
        {
            L($"STATUS: Not Executed — WhoAmI failed: {who.Message}");
            File.WriteAllText(ReportPath, sb.ToString());
            Assert.True(true);
            return;
        }

        L("STATUS: Executing Phase 2 semantic → Groq → CRM live verification");
        L($"WhoAmI Success={who.Success}");
        L($"GroqModel={groqModel}");
        L($"OllamaBase={ollamaBase}");
        L($"CrmHost={new Uri(baseUrl).Host}");
        L("Config: TopKEntities=3 TopKAttributesPerEntity=20 TopKRelationships=10");

        var metadata = provider.GetRequiredService<ICrmMetadataService>();
        await metadata.EnsureApprovedEntitiesLoadedAsync();
        var semantic = provider.GetRequiredService<ISemanticCrmMetadataRetriever>();
        var recorder = provider.GetRequiredService<RecordingChatClient>();
        using var scope = provider.CreateScope();
        var chat = scope.ServiceProvider.GetRequiredService<ChatService>();

        for (var i = 0; i < Questions.Length; i++)
        {
            var q = Questions[i];
            L("");
            L($"========== Q{i + 1}: {q} ==========");
            recorder.Reset();

            var semSw = Stopwatch.StartNew();
            var retrieval = await semantic.RetrieveAsync(q);
            semSw.Stop();
            L($"SemanticLatencyMs={semSw.ElapsedMilliseconds}");
            L($"SemanticError={retrieval.Error ?? "(none)"}");
            L($"IndexRebuilt={retrieval.IndexRebuilt} Indexed={retrieval.IndexedDocumentCount} Retrieved={retrieval.RetrievedDocumentCount}");
            L("SelectedEntities:");
            foreach (var h in retrieval.EntityHits)
            {
                L($"  {h.Similarity:F4} {h.Document.EntityLogicalName}");
            }

            L($"SelectedAttributeCount={retrieval.AttributeHits.Count}");
            foreach (var h in retrieval.AttributeHits.Take(12))
            {
                L($"  {h.Similarity:F4} {h.Document.EntityLogicalName}.{h.Document.AttributeLogicalName}");
            }

            L("SelectedRelationships:");
            foreach (var h in retrieval.RelationshipHits)
            {
                L($"  {h.Similarity:F4} {h.Document.RelationshipSchemaName}");
            }

            ReportExpectationFlags(L, q, retrieval);

            ChatResponseDto? response = null;
            Exception? chatEx = null;
            var chatSw = Stopwatch.StartNew();
            try
            {
                response = await chat.ChatAsync(new ChatRequestDto { Message = q }, CancellationToken.None);
            }
            catch (Exception ex)
            {
                chatEx = ex;
            }

            chatSw.Stop();

            L($"ChatLatencyMs={chatSw.ElapsedMilliseconds}");
            L($"GroqCallCount={recorder.CallCount}");
            L($"UserMessageUnchanged={recorder.LastUserMessage == q}");
            L($"CatalogIncludedInRequest={recorder.LastRequestHadCatalog}");
            L($"RequestPayloadCharacters={recorder.LastRequestPayloadCharacters}");
            L($"PromptTokens={recorder.LastPromptTokens?.ToString() ?? "(n/a)"}");
            L($"CompletionTokens={recorder.LastCompletionTokens?.ToString() ?? "(n/a)"}");
            L($"TotalTokens={recorder.LastTotalTokens?.ToString() ?? "(n/a)"}");
            if (recorder.LastProviderError is not null)
            {
                L($"ProviderError={recorder.LastProviderError}");
            }

            if (recorder.LastFunctionCalls.Count > 0)
            {
                var call = recorder.LastFunctionCalls[0];
                L($"ToolName={call.Name}");
                L($"QueryCrmArguments={FormatArgs(call.Arguments)}");
            }
            else if (!string.IsNullOrWhiteSpace(recorder.LastText))
            {
                L($"NoToolCall TextPreview={Truncate(recorder.LastText, 600)}");
            }

            if (chatEx is not null)
            {
                L($"ChatException={chatEx.GetType().Name}: {chatEx.Message}");
                continue;
            }

            L($"SelectedTool={response!.SelectedTool}");
            L($"CrmDataProvider={response.CrmDataProvider}");
            L($"ReplyPreview={Truncate(response.Reply, 900)}");
            if (response.StructuredData is not null)
            {
                try
                {
                    L($"StructuredData={Truncate(JsonSerializer.Serialize(response.StructuredData), 1500)}");
                }
                catch
                {
                    L("StructuredData=(unserializable)");
                }
            }

            L($"SingleStagePlanningCall={recorder.CallCount == 1}");
            L($"BackendReachedCrm={string.Equals(response.SelectedTool, "query_crm", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(response.CrmDataProvider)}");
        }

        File.WriteAllText(ReportPath, sb.ToString());
        L($"Report written: {ReportPath}");
        File.WriteAllText(ReportPath, sb.ToString());
    }

    private static void ReportExpectationFlags(
        Action<string> L,
        string question,
        SemanticMetadataRetrievalResult retrieval)
    {
        static bool HasEntity(SemanticMetadataRetrievalResult r, string name) =>
            r.EntityHits.Any(h => h.Document.EntityLogicalName.Equals(name, StringComparison.OrdinalIgnoreCase));

        static bool HasAttr(SemanticMetadataRetrievalResult r, string entity, string attr) =>
            r.AttributeHits.Any(h =>
                h.Document.EntityLogicalName.Equals(entity, StringComparison.OrdinalIgnoreCase) &&
                h.Document.AttributeLogicalName!.Equals(attr, StringComparison.OrdinalIgnoreCase));

        static bool HasRel(SemanticMetadataRetrievalResult r, string a, string b) =>
            r.RelationshipHits.Any(h =>
                (h.Document.ReferencingEntity?.Equals(a, StringComparison.OrdinalIgnoreCase) == true &&
                 h.Document.ReferencedEntity?.Equals(b, StringComparison.OrdinalIgnoreCase) == true) ||
                (h.Document.ReferencingEntity?.Equals(b, StringComparison.OrdinalIgnoreCase) == true &&
                 h.Document.ReferencedEntity?.Equals(a, StringComparison.OrdinalIgnoreCase) == true));

        L("Semantic expectation checks (diagnostic only):");
        if (question.Contains("total amount", StringComparison.OrdinalIgnoreCase))
        {
            L($"  salesorder={HasEntity(retrieval, "salesorder")} totalamount={HasAttr(retrieval, "salesorder", "totalamount")}");
        }
        else if (question.Contains("latest 3 products", StringComparison.OrdinalIgnoreCase))
        {
            L($"  product={HasEntity(retrieval, "product")} createdon={HasAttr(retrieval, "product", "createdon")} name={HasAttr(retrieval, "product", "name")} productid={HasAttr(retrieval, "product", "productid")}");
        }
        else if (question.Contains("Customer Account", StringComparison.OrdinalIgnoreCase))
        {
            L($"  salesorder={HasEntity(retrieval, "salesorder")} account={HasEntity(retrieval, "account")} rel={HasRel(retrieval, "salesorder", "account")}");
        }
        else if (question.Contains("estimated revenue", StringComparison.OrdinalIgnoreCase))
        {
            L($"  opportunity={HasEntity(retrieval, "opportunity")} estimatedvalue={HasAttr(retrieval, "opportunity", "estimatedvalue")}");
        }
    }

    private static async Task<(string? BaseUrl, string? GroqKey, string GroqModel, string GroqBase, string OllamaBase, CrmOptions Crm)>
        LoadDevConfigAsync()
    {
        var baseUrl = Environment.GetEnvironmentVariable("Crm__BaseUrl")
                      ?? Environment.GetEnvironmentVariable("CRM_BASE_URL");
        var groqKey = Environment.GetEnvironmentVariable("Groq__ApiKey")
                      ?? Environment.GetEnvironmentVariable("GROQ_API_KEY");
        var ollamaBase = Environment.GetEnvironmentVariable("OLLAMA_BASE_URL") ?? "http://localhost:11434";

        var devPath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..",
            "src", "CrmAiPoc.Api", "appsettings.Development.json"));
        if (!File.Exists(devPath))
        {
            devPath = Path.GetFullPath(Path.Combine(
                AppContext.BaseDirectory, "..", "..", "..", "..",
                "src", "CrmAiPoc.Api", "appsettings.Development.json"));
        }

        JsonElement? dev = null;
        if (File.Exists(devPath))
        {
            using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(devPath));
            dev = doc.RootElement.Clone();
            baseUrl ??= GetDevString(dev.Value, "Crm", "BaseUrl");
            groqKey ??= GetDevString(dev.Value, "Groq", "ApiKey");
            ollamaBase = GetDevString(dev.Value, "Ollama", "BaseUrl") ?? ollamaBase;
        }

        var groqModel = Environment.GetEnvironmentVariable("Groq__ModelName")
                        ?? GetDevString(dev, "Groq", "ModelName")
                        ?? "openai/gpt-oss-120b";
        var groqBase = Environment.GetEnvironmentVariable("Groq__BaseUrl")
                       ?? GetDevString(dev, "Groq", "BaseUrl")
                       ?? "https://api.groq.com/openai/v1";

        var approved = new List<string>();
        var aliases = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var labels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (dev is { } d && d.TryGetProperty("Crm", out var crmEl))
        {
            if (crmEl.TryGetProperty("ApprovedEntities", out var arr))
            {
                foreach (var item in arr.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(item.GetString()))
                    {
                        approved.Add(item.GetString()!);
                    }
                }
            }

            if (crmEl.TryGetProperty("EntityAliases", out var aliasEl))
            {
                foreach (var prop in aliasEl.EnumerateObject())
                {
                    aliases[prop.Name] = prop.Value.EnumerateArray()
                        .Select(x => x.GetString()!)
                        .Where(x => !string.IsNullOrWhiteSpace(x))
                        .ToList();
                }
            }

            if (crmEl.TryGetProperty("RelationshipLabels", out var labelEl))
            {
                foreach (var prop in labelEl.EnumerateObject())
                {
                    labels[prop.Name] = prop.Value.GetString() ?? prop.Name;
                }
            }
        }

        if (approved.Count == 0)
        {
            approved =
            [
                "account", "contact", "lead", "salesorder", "opportunity", "quote", "invoice",
                "salesorderdetail", "product", "campaign", "te_serviceorder"
            ];
        }

        var crmOptions = new CrmOptions
        {
            Provider = "Dynamics365",
            EnableLiveIntegration = true,
            BaseUrl = baseUrl ?? string.Empty,
            UseDefaultCredentials = true,
            AllowSendApprovedMetadataCatalogToAi = true,
            WarmApprovedMetadataOnStartup = false,
            ApprovedEntities = approved,
            ApprovedRelationships = [],
            MetadataCacheSeconds = 3600,
            BusinessTimeZoneId = "Africa/Cairo",
            EntityAliases = aliases,
            RelationshipLabels = labels
        };

        return (baseUrl, groqKey, groqModel!, groqBase!, ollamaBase, crmOptions);
    }

    private static string? GetDevString(JsonElement? root, string section, string key)
    {
        if (root is null ||
            !root.Value.TryGetProperty(section, out var sec) ||
            !sec.TryGetProperty(key, out var val))
        {
            return null;
        }

        return val.ValueKind == JsonValueKind.String ? val.GetString() : null;
    }

    private static string FormatArgs(IDictionary<string, object?>? args)
    {
        if (args is null)
        {
            return "{}";
        }

        try
        {
            return JsonSerializer.Serialize(args.ToDictionary(kv => kv.Key, kv => kv.Value));
        }
        catch
        {
            return string.Join("; ", args.Select(kv => $"{kv.Key}={kv.Value}"));
        }
    }

    private static string Truncate(string? value, int max)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        return value.Length <= max ? value : value[..max] + "…";
    }

    private sealed class RecordingChatClient : IChatClient
    {
        private readonly LazyGroqChatClient _inner;

        public RecordingChatClient(LazyGroqChatClient inner) => _inner = inner;

        public int CallCount { get; private set; }
        public string? LastUserMessage { get; private set; }
        public bool LastRequestHadCatalog { get; private set; }
        public int LastRequestPayloadCharacters { get; private set; }
        public List<FunctionCallContent> LastFunctionCalls { get; private set; } = [];
        public string? LastText { get; private set; }
        public string? LastProviderError { get; private set; }
        public long? LastPromptTokens { get; private set; }
        public long? LastCompletionTokens { get; private set; }
        public long? LastTotalTokens { get; private set; }

        public void Reset()
        {
            CallCount = 0;
            LastUserMessage = null;
            LastRequestHadCatalog = false;
            LastRequestPayloadCharacters = 0;
            LastFunctionCalls = [];
            LastText = null;
            LastProviderError = null;
            LastPromptTokens = null;
            LastCompletionTokens = null;
            LastTotalTokens = null;
        }

        public async Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> chatMessages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var messages = chatMessages.ToList();
            CallCount++;
            LastUserMessage = messages.LastOrDefault(m => m.Role == ChatRole.User)?.Text;
            LastRequestHadCatalog = messages.Any(m =>
                m.Text?.Contains(CrmAiMetadataCatalogService.BeginMarker, StringComparison.Ordinal) == true);
            LastRequestPayloadCharacters = messages.Sum(m => m.Text?.Length ?? 0);

            try
            {
                var response = await _inner.GetResponseAsync(messages, options, cancellationToken);
                LastFunctionCalls = response.Messages
                    .SelectMany(m => m.Contents.OfType<FunctionCallContent>())
                    .ToList();
                LastText = response.Text;
                LastPromptTokens = response.Usage?.InputTokenCount;
                LastCompletionTokens = response.Usage?.OutputTokenCount;
                LastTotalTokens = response.Usage?.TotalTokenCount;
                return response;
            }
            catch (Exception ex)
            {
                LastProviderError = $"{ex.GetType().Name}: {ex.Message}";
                throw;
            }
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> chatMessages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            _inner.GetStreamingResponseAsync(chatMessages, options, cancellationToken);

        public object? GetService(Type serviceType, object? serviceKey = null) =>
            _inner.GetService(serviceType, serviceKey);

        public void Dispose() => _inner.Dispose();
    }
}
