using System.Text;
using System.Text.Json;
using CrmAiPoc.Api.Ai;
using CrmAiPoc.Api.Configuration;
using CrmAiPoc.Api.Crm.Dynamics;
using CrmAiPoc.Api.Crm.FetchXml;
using CrmAiPoc.Api.Crm.Generic;
using CrmAiPoc.Api.Crm.Metadata;
using CrmAiPoc.Api.Crm.Metadata.Discovery;
using CrmAiPoc.Api.Models;
using CrmAiPoc.Api.Security;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CrmAiPoc.Tests;

/// <summary>
/// POC live verify: complete approved metadata → Groq → query_crm → CRM for the five required questions.
/// Set CRM_LIVE_VERIFY=1. Report: %TEMP%\crm-poc-full-metadata-verify.txt
/// </summary>
public sealed class LiveFullApprovedMetadataPocTests
{
    private static readonly string ReportPath =
        Path.Combine(Path.GetTempPath(), "crm-poc-full-metadata-verify.txt");

    private static readonly string CatalogDumpPath =
        Path.Combine(Path.GetTempPath(), "crm-poc-full-metadata-catalog-dump.txt");

    private static readonly string[] Questions =
    [
        "Show the top 10 opportunities by estimated revenue.",
        "Show monthly invoice totals for 2025.",
        "Show active marketing campaigns created this year.",
        "Show the 10 highest-priced products.",
        "Show the 10 highest-value Sales Orders created this year, including their customer account names."
    ];

    [Fact]
    public async Task Live_Full_Approved_Metadata_Five_Questions_Or_NotExecuted()
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

        var (baseUrl, groqKey, groqModel, groqBase, crmOptions) = await LoadDevConfigAsync();
        if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(groqKey))
        {
            L($"STATUS: Not Executed — BaseUrl or Groq key missing. baseUrlSet={!string.IsNullOrWhiteSpace(baseUrl)}");
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
            // Large full-catalog payloads need the upper configured wait; do not fabricate on timeout.
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
        services.AddSingleton<ICrmAuthorizationService, CrmAuthorizationService>();
        services.AddSingleton(Options.Create(new AggregatePartitionOptions()));
        services.AddSingleton<FetchXmlSafetyValidator>();
        services.AddSingleton<FetchXmlAggregatePartitionRecovery>();
        services.AddSingleton<IFetchXmlQueryExecutor, FetchXmlQueryExecutor>();
        services.AddSingleton<ICrmAiMetadataCatalogService, CrmAiMetadataCatalogService>();
        services.AddSingleton<CrmSchemaDiscoveryService>();
        services.AddSingleton<LazyGroqChatClient>();
        services.AddSingleton<RecordingChatClient>();
        services.AddSingleton<IChatClient>(sp => sp.GetRequiredService<RecordingChatClient>());
        services.AddScoped<CrmAiTools>();
        services.AddScoped<ChatService>();

        await using var provider = services.BuildServiceProvider();
        var connectivity = provider.GetRequiredService<DynamicsConnectivityService>();
        var who = await connectivity.WhoAmIAsync();
        if (!who.Success)
        {
            L($"STATUS: Not Executed — WhoAmI failed: {who.Message}");
            File.WriteAllText(ReportPath, sb.ToString());
            Assert.True(true);
            return;
        }

        L("STATUS: Executing POC full-approved-metadata live verification");
        L($"WhoAmI: Success={who.Success}");
        L($"GroqModel: {groqModel}");
        L($"BaseUrlHost: {new Uri(baseUrl).Host}");
        L($"ApprovedEntities: {string.Join(", ", crmOptions.ApprovedEntities)}");
        L("");

        var metadata = provider.GetRequiredService<ICrmMetadataService>();
        await metadata.EnsureApprovedEntitiesLoadedAsync();
        var approvedLive = await metadata.GetApprovedEntitiesAsync();

        L("=== Live discovery summary (per approved entity) ===");
        var totalDiscoveredAttrs = 0;
        var totalDiscoveredRels = 0;
        foreach (var name in crmOptions.ApprovedEntities.OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
        {
            var entity = approvedLive.FirstOrDefault(e =>
                e.LogicalName.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (entity is null)
            {
                L($"  {name}: MISSING from GetApprovedEntitiesAsync (discovery failure)");
                continue;
            }

            var readable = entity.Attributes.Count(a => a.IsValidForRead);
            totalDiscoveredAttrs += readable;
            totalDiscoveredRels += entity.Relationships.Count;
            L(
                $"  {name}: attrsReadable={readable} rels={entity.Relationships.Count} " +
                $"liveVerified={entity.IsLiveVerified} provenance={entity.Provenance} " +
                $"discoveryError={entity.DiscoveryError ?? "(none)"}");
        }

        var catalogService = provider.GetRequiredService<ICrmAiMetadataCatalogService>();
        var catalog = await catalogService.BuildApprovedCatalogAsync();
        File.WriteAllText(CatalogDumpPath, catalog.FormattedBlock ?? string.Empty);
        L("");
        L("=== Single-stage outbound catalog ===");
        L($"Success={catalog.Success} Error={catalog.Error}");
        L($"IncludedEntityCount={catalog.IncludedEntityCount}");
        L($"IncludedAttributeCount={catalog.IncludedAttributeCount}");
        L($"IncludedRelationshipCount={catalog.IncludedRelationshipCount}");
        L($"MetadataPayloadChars={catalog.CharacterCount}");
        L($"TruncationApplied={catalog.TruncationApplied}");

        static bool HasAttr(IReadOnlyList<CrmEntityMetadata> list, string entity, string attr) =>
            list.FirstOrDefault(e => e.LogicalName.Equals(entity, StringComparison.OrdinalIgnoreCase))
                ?.Attributes.Any(a => a.LogicalName.Equals(attr, StringComparison.OrdinalIgnoreCase) && a.IsValidForRead) == true;

        var hasEstimated = HasAttr(approvedLive.ToList(), "opportunity", "estimatedvalue");
        var hasInvoiceTotal = HasAttr(approvedLive.ToList(), "invoice", "totalamount");
        var hasPrice = HasAttr(approvedLive.ToList(), "product", "price");
        var campaign = approvedLive.FirstOrDefault(e => e.LogicalName.Equals("campaign", StringComparison.OrdinalIgnoreCase));
        var campaignState = campaign?.Attributes.FirstOrDefault(a =>
            a.LogicalName.Equals("statecode", StringComparison.OrdinalIgnoreCase));
        L("");
        L("=== Live metadata field presence ===");
        L($"opportunity.estimatedvalue={hasEstimated}");
        L($"invoice.totalamount={hasInvoiceTotal}");
        L($"product.price={hasPrice}");
        L($"campaign.statecode present={campaignState is not null} options={campaignState?.OptionSetByValue.Count ?? 0}");
        L($"CatalogContainsEstimatedValue={catalog.FormattedBlock.Contains("estimatedvalue", StringComparison.OrdinalIgnoreCase)}");

        var recorder = provider.GetRequiredService<RecordingChatClient>();
        using var scope = provider.CreateScope();
        var chat = scope.ServiceProvider.GetRequiredService<ChatService>();

        for (var i = 0; i < Questions.Length; i++)
        {
            var q = Questions[i];
            L("");
            L($"========== Q{i + 1}: {q} ==========");
            recorder.Reset();
            ChatResponseDto? response = null;
            Exception? chatEx = null;
            try
            {
                response = await chat.ChatAsync(new ChatRequestDto { Message = q }, CancellationToken.None);
            }
            catch (Exception ex)
            {
                chatEx = ex;
            }

            L($"GroqCallCount={recorder.CallCount}");
            L($"UserMessageUnchanged={recorder.LastUserMessage == q}");
            L($"CatalogIncludedInRequest={recorder.LastRequestHadCatalog}");
            L($"QueryCrmToolCallReturned={recorder.LastFunctionCalls.Count > 0}");
            if (recorder.LastProviderError is not null)
            {
                L($"ProviderError={recorder.LastProviderError}");
            }

            if (recorder.LastFunctionCalls.Count > 0)
            {
                var call = recorder.LastFunctionCalls[0];
                L($"ToolName={call.Name}");
                L($"OriginalAiToolArguments={FormatArgs(call.Arguments)}");
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
            L($"ReplyPreview={Truncate(response.Reply, 800)}");
            if (response.StructuredData is not null)
            {
                try
                {
                    L($"StructuredData={Truncate(JsonSerializer.Serialize(response.StructuredData), 1200)}");
                }
                catch
                {
                    L("StructuredData=(unserializable)");
                }
            }

            var validationOk = string.Equals(response.SelectedTool, "query_crm", StringComparison.OrdinalIgnoreCase)
                               && !string.IsNullOrWhiteSpace(response.CrmDataProvider);
            L($"BackendReachedCrmExecution={validationOk}");
            L($"SingleStagePlanningCall={recorder.CallCount == 1}");
            L($"Outcome={(chatEx is null ? "completed" : "failed")}");
        }

        File.WriteAllText(ReportPath, sb.ToString());
        L("");
        L($"Report written: {ReportPath}");
        File.WriteAllText(ReportPath, sb.ToString());

        Assert.True(catalog.Success, catalog.Error);
        Assert.Equal(crmOptions.ApprovedEntities.Count, catalog.IncludedEntityCount);
        Assert.True(totalDiscoveredAttrs > 0);
        Assert.True(totalDiscoveredRels > 0);
    }

    private static async Task<(string? BaseUrl, string? GroqKey, string GroqModel, string GroqBase, CrmOptions Crm)>
        LoadDevConfigAsync()
    {
        var baseUrl = Environment.GetEnvironmentVariable("Crm__BaseUrl")
                      ?? Environment.GetEnvironmentVariable("CRM_BASE_URL");
        var groqKey = Environment.GetEnvironmentVariable("Groq__ApiKey")
                      ?? Environment.GetEnvironmentVariable("GROQ_API_KEY");

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
        }

        var groqModel = Environment.GetEnvironmentVariable("Groq__ModelName")
                        ?? GetDevString(dev, "Groq", "ModelName")
                        ?? "openai/gpt-oss-120b";
        var groqBase = Environment.GetEnvironmentVariable("Groq__BaseUrl")
                       ?? GetDevString(dev, "Groq", "BaseUrl")
                       ?? "https://api.groq.com/openai/v1";

        var approved = new List<string>();
        if (dev is { } d && d.TryGetProperty("Crm", out var crmEl) &&
            crmEl.TryGetProperty("ApprovedEntities", out var arr) &&
            arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in arr.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(item.GetString()))
                {
                    approved.Add(item.GetString()!);
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

        var aliases = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var labels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (dev is { } d2 && d2.TryGetProperty("Crm", out var crm2))
        {
            if (crm2.TryGetProperty("EntityAliases", out var aliasEl))
            {
                foreach (var prop in aliasEl.EnumerateObject())
                {
                    aliases[prop.Name] = prop.Value.EnumerateArray()
                        .Select(x => x.GetString()!)
                        .Where(x => !string.IsNullOrWhiteSpace(x))
                        .ToList();
                }
            }

            if (crm2.TryGetProperty("RelationshipLabels", out var labelEl))
            {
                foreach (var prop in labelEl.EnumerateObject())
                {
                    labels[prop.Name] = prop.Value.GetString() ?? prop.Name;
                }
            }
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

        return (baseUrl, groqKey, groqModel!, groqBase!, crmOptions);
    }

    private static string? GetDevString(JsonElement? root, string section, string key)
    {
        if (root is null)
        {
            return null;
        }

        if (!root.Value.TryGetProperty(section, out var sec) || !sec.TryGetProperty(key, out var val))
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

    /// <summary>Captures single-stage Groq exchanges for the POC report.</summary>
    private sealed class RecordingChatClient : IChatClient
    {
        private readonly LazyGroqChatClient _inner;

        public RecordingChatClient(LazyGroqChatClient inner) => _inner = inner;

        public int CallCount { get; private set; }
        public string? LastUserMessage { get; private set; }
        public bool LastRequestHadCatalog { get; private set; }
        public List<FunctionCallContent> LastFunctionCalls { get; private set; } = [];
        public string? LastText { get; private set; }
        public string? LastProviderError { get; private set; }

        public void Reset()
        {
            CallCount = 0;
            LastUserMessage = null;
            LastRequestHadCatalog = false;
            LastFunctionCalls = [];
            LastText = null;
            LastProviderError = null;
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

            try
            {
                var response = await _inner.GetResponseAsync(messages, options, cancellationToken);
                LastFunctionCalls = response.Messages
                    .SelectMany(m => m.Contents.OfType<FunctionCallContent>())
                    .ToList();
                LastText = response.Text;
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

