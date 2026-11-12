using CrmAiPoc.Api.Ai;
using CrmAiPoc.Api.Configuration;
using CrmAiPoc.Api.Crm.Dynamics;
using CrmAiPoc.Api.Crm.FetchXml;
using CrmAiPoc.Api.Crm.Metadata;
using CrmAiPoc.Api.Crm.Metadata.Discovery;
using CrmAiPoc.Api.Crm.Metadata.Semantic;
using CrmAiPoc.Api.Crm.Validation;
using CrmAiPoc.Api.Endpoints;
using CrmAiPoc.Api.Security;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<AiOptions>(options =>
{
    builder.Configuration.GetSection(AiOptions.SectionName).Bind(options);
    options.Provider = FirstNonEmpty(
        builder.Configuration["Ai:Provider"],
        Environment.GetEnvironmentVariable("AI_PROVIDER")) ?? options.Provider;

    // Nested Ai:Ollama (preferred) with optional top-level Ollama:* overrides for ops convenience.
    options.Ollama ??= new OllamaChatOptions();
    builder.Configuration.GetSection(OllamaChatOptions.SectionName).Bind(options.Ollama);
    options.Ollama.BaseUrl = FirstNonEmpty(
        options.Ollama.BaseUrl,
        builder.Configuration["Ollama:BaseUrl"],
        Environment.GetEnvironmentVariable("OLLAMA_HOST"),
        Environment.GetEnvironmentVariable("OLLAMA_BASE_URL")) ?? options.Ollama.BaseUrl;
    options.Ollama.Model = FirstNonEmpty(
        options.Ollama.Model,
        options.Ollama.ModelName,
        builder.Configuration["Ollama:Model"],
        builder.Configuration["Ollama:ModelName"],
        Environment.GetEnvironmentVariable("OLLAMA_MODEL")) ?? options.Ollama.Model;
    if (int.TryParse(
            FirstNonEmpty(
                builder.Configuration["Ai:Ollama:ContextSize"],
                builder.Configuration["Ollama:ContextSize"],
                Environment.GetEnvironmentVariable("OLLAMA_NUM_CTX")),
            out var ctx) &&
        ctx > 0)
    {
        options.Ollama.ContextSize = ctx;
    }

    if (bool.TryParse(
            FirstNonEmpty(
                builder.Configuration["Ai:Ollama:EnableThinking"],
                builder.Configuration["Ollama:EnableThinking"],
                Environment.GetEnvironmentVariable("OLLAMA_ENABLE_THINKING")),
            out var think))
    {
        options.Ollama.EnableThinking = think;
    }
});

builder.Services.Configure<GeminiOptions>(options =>
{
    builder.Configuration.GetSection(GeminiOptions.SectionName).Bind(options);
    options.ApiKey = FirstNonEmpty(
        builder.Configuration["Gemini:ApiKey"],
        builder.Configuration["GeminiApiKey"],
        Environment.GetEnvironmentVariable("GEMINI_API_KEY"),
        Environment.GetEnvironmentVariable("GeminiApiKey")) ?? options.ApiKey;
    options.ModelName = FirstNonEmpty(
        builder.Configuration["Gemini:ModelName"],
        Environment.GetEnvironmentVariable("GEMINI_MODEL")) ?? options.ModelName;
});

builder.Services.Configure<OpenAiOptions>(options =>
{
    builder.Configuration.GetSection(OpenAiOptions.SectionName).Bind(options);
    options.ApiKey = FirstNonEmpty(
        builder.Configuration["OpenAI:ApiKey"],
        builder.Configuration["OpenAIKey"],
        Environment.GetEnvironmentVariable("OPENAI_API_KEY"),
        Environment.GetEnvironmentVariable("OpenAIKey")) ?? options.ApiKey;
    options.ModelName = FirstNonEmpty(
        builder.Configuration["OpenAI:ModelName"],
        Environment.GetEnvironmentVariable("OPENAI_MODEL"),
        Environment.GetEnvironmentVariable("ModelName")) ?? options.ModelName;
});

builder.Services.Configure<GroqOptions>(options =>
{
    builder.Configuration.GetSection(GroqOptions.SectionName).Bind(options);
    options.ApiKey = FirstNonEmpty(
        builder.Configuration["Groq:ApiKey"],
        builder.Configuration["GroqApiKey"],
        Environment.GetEnvironmentVariable("GROQ_API_KEY"),
        Environment.GetEnvironmentVariable("GroqApiKey")) ?? options.ApiKey;
    options.Model = FirstNonEmpty(
        builder.Configuration["Groq:Model"],
        builder.Configuration["Groq:ModelName"],
        Environment.GetEnvironmentVariable("GROQ_MODEL")) ?? options.Model;
    options.ModelName = FirstNonEmpty(
        options.Model,
        builder.Configuration["Groq:ModelName"],
        Environment.GetEnvironmentVariable("GROQ_MODEL")) ?? options.ModelName;
    options.ReasoningEffort = FirstNonEmpty(
        builder.Configuration["Groq:ReasoningEffort"],
        Environment.GetEnvironmentVariable("GROQ_REASONING_EFFORT")) ?? options.ReasoningEffort;
    options.BaseUrl = FirstNonEmpty(
        builder.Configuration["Groq:BaseUrl"],
        Environment.GetEnvironmentVariable("GROQ_BASE_URL")) ?? options.BaseUrl;
});

builder.Services.Configure<OpenRouterOptions>(options =>
{
    builder.Configuration.GetSection(OpenRouterOptions.SectionName).Bind(options);
    options.ApiKey = FirstNonEmpty(
        builder.Configuration["OpenRouter:ApiKey"],
        builder.Configuration["OpenRouterApiKey"],
        Environment.GetEnvironmentVariable("OPENROUTER_API_KEY"),
        Environment.GetEnvironmentVariable("OpenRouterApiKey")) ?? options.ApiKey;
    options.Model = FirstNonEmpty(
        builder.Configuration["OpenRouter:Model"],
        builder.Configuration["OpenRouter:ModelName"],
        Environment.GetEnvironmentVariable("OPENROUTER_MODEL")) ?? options.Model;
    options.ModelName = FirstNonEmpty(
        options.Model,
        builder.Configuration["OpenRouter:ModelName"],
        Environment.GetEnvironmentVariable("OPENROUTER_MODEL")) ?? options.ModelName;
    options.BaseUrl = FirstNonEmpty(
        builder.Configuration["OpenRouter:BaseUrl"],
        Environment.GetEnvironmentVariable("OPENROUTER_BASE_URL")) ?? options.BaseUrl;
});

builder.Services.Configure<CrmOptions>(options =>
{
    builder.Configuration.GetSection(CrmOptions.SectionName).Bind(options);
    // Replace-not-merge for allowlist arrays (avoids index leftovers from appsettings.json).
    options.ApprovedEntities = ConfigurationListBinding.GetStringArrayReplace(
        builder.Configuration, "Crm:ApprovedEntities");
    options.ApprovedRelationships = ConfigurationListBinding.GetStringArrayReplace(
        builder.Configuration, "Crm:ApprovedRelationships");
    options.BaseUrl = FirstNonEmpty(
        options.BaseUrl,
        builder.Configuration["Crm:BaseUrl"],
        Environment.GetEnvironmentVariable("CRM_BASE_URL")) ?? options.BaseUrl;
    options.Username = FirstNonEmpty(
        options.Username,
        Environment.GetEnvironmentVariable("CRM_USERNAME"));
    options.Password = FirstNonEmpty(
        options.Password,
        Environment.GetEnvironmentVariable("CRM_PASSWORD"));
    options.Domain = FirstNonEmpty(
        options.Domain,
        Environment.GetEnvironmentVariable("CRM_DOMAIN"));
});



builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new()
    {
        Title = "CRM AI POC API",
        Version = "v1",
        Description = "Read-only Dynamics 365 CRM assistant POC. AI via Ai:Provider; CRM data via live Dynamics 365 only."
    });
});

builder.Services.AddSingleton<ConversationStore>();
builder.Services.AddSingleton<CrmArgumentValidator>();
builder.Services.AddSingleton<CrmMetadataRegistry>();
builder.Services.AddSingleton<EntityApprovalPolicy>();
builder.Services.AddSingleton<CrmRelationshipLabeler>();
builder.Services.AddSingleton<ICrmAiMetadataCatalogService, CrmAiMetadataCatalogService>();
builder.Services.AddSingleton<ICrmAuthorizationService, CrmAuthorizationService>();
builder.Services.Configure<AggregatePartitionOptions>(
    builder.Configuration.GetSection(AggregatePartitionOptions.SectionName));
builder.Services.AddSingleton<FetchXmlSafetyValidator>();
builder.Services.AddSingleton<FetchXmlAggregatePartitionRecovery>();
builder.Services.AddSingleton<IFetchXmlQueryExecutor, FetchXmlQueryExecutor>();

// SEM-02: Ollama embeddings + hybrid lexical/semantic metadata discovery for discover_crm_schema.
builder.Services.Configure<OllamaEmbeddingOptions>(options =>
{
    builder.Configuration.GetSection(OllamaEmbeddingOptions.SectionName).Bind(options);
    // Prefer dedicated OllamaEmbeddings section; fall back to Ai:Ollama host / legacy Ollama:EmbeddingModel.
    options.BaseUrl = FirstNonEmpty(
        options.BaseUrl,
        builder.Configuration["OllamaEmbeddings:BaseUrl"],
        builder.Configuration["Ai:Ollama:BaseUrl"],
        builder.Configuration["Ollama:BaseUrl"],
        Environment.GetEnvironmentVariable("OLLAMA_EMBEDDINGS_BASE_URL"),
        Environment.GetEnvironmentVariable("OLLAMA_HOST"),
        Environment.GetEnvironmentVariable("OLLAMA_BASE_URL")) ?? options.BaseUrl;
    options.EmbeddingModel = FirstNonEmpty(
        options.EmbeddingModel,
        builder.Configuration["OllamaEmbeddings:EmbeddingModel"],
        builder.Configuration["Ollama:EmbeddingModel"],
        Environment.GetEnvironmentVariable("OLLAMA_EMBEDDING_MODEL")) ?? options.EmbeddingModel;
});
builder.Services.Configure<SemanticMetadataOptions>(options =>
{
    builder.Configuration.GetSection(SemanticMetadataOptions.SectionName).Bind(options);
});
builder.Services.PostConfigure<SemanticMetadataOptions>(options =>
{
    if (string.IsNullOrWhiteSpace(options.CacheDirectory))
    {
        options.CacheDirectory = Path.Combine("Cache", "SemanticIndex");
    }

    // Always resolve relative paths against ContentRootPath (project directory),
    // never the process working directory or bin/ output folder.
    if (!Path.IsPathRooted(options.CacheDirectory))
    {
        options.CacheDirectory = Path.GetFullPath(
            Path.Combine(builder.Environment.ContentRootPath, options.CacheDirectory));
    }
});
builder.Services.AddHttpClient(OllamaEmbeddingClient.HttpClientName, (sp, client) =>
    {
        var opts = sp.GetRequiredService<IOptions<OllamaEmbeddingOptions>>().Value;
        var baseUrl = string.IsNullOrWhiteSpace(opts.BaseUrl)
            ? "http://localhost:11434"
            : opts.BaseUrl.Trim().TrimEnd('/');
        client.BaseAddress = new Uri(baseUrl + "/");
        client.Timeout = TimeSpan.FromSeconds(Math.Clamp(opts.TimeoutSeconds, 5, 300));
    });
builder.Services.AddSingleton<IOllamaEmbeddingClient, OllamaEmbeddingClient>();
builder.Services.AddSingleton<InMemorySemanticMetadataIndex>();
builder.Services.AddSingleton<HybridMetadataRanker>();
builder.Services.AddSingleton<CrmSchemaDiscoveryService>();
builder.Services.AddScoped<CrmAiTools>();
builder.Services.AddScoped<ChatService>();


// Dynamics HTTP client (Windows auth / configured credentials). Live CRM is required.
builder.Services.AddHttpClient(DynamicsCrmHttp.HttpClientName, (sp, client) =>
    {
        var opts = sp.GetRequiredService<IOptions<CrmOptions>>().Value;
        if (!string.IsNullOrWhiteSpace(opts.BaseUrl))
        {
            client.BaseAddress = new Uri(EnsureTrailingSlash(opts.BaseUrl));
        }

        client.Timeout = TimeSpan.FromSeconds(60);
    })
    .ConfigurePrimaryHttpMessageHandler(sp =>
    {
        var opts = sp.GetRequiredService<IOptions<CrmOptions>>().Value;
        return DynamicsCrmHttp.CreateHandler(opts);
    });

builder.Services.AddSingleton<DynamicsCrmHttp>();
builder.Services.AddSingleton<DynamicsConnectivityService>();

var crmOptions = builder.Configuration.GetSection(CrmOptions.SectionName).Get<CrmOptions>() ?? new CrmOptions();
crmOptions.ApprovedEntities = ConfigurationListBinding.GetStringArrayReplace(
    builder.Configuration, "Crm:ApprovedEntities");
crmOptions.ApprovedRelationships = ConfigurationListBinding.GetStringArrayReplace(
    builder.Configuration, "Crm:ApprovedRelationships");
var crmProviderName = (crmOptions.Provider ?? "Dynamics365").Trim();
var approvedEntitiesProvider = ConfigurationListBinding.DescribeWinningArrayProvider(
    builder.Configuration, "Crm:ApprovedEntities") ?? "(none)";

if (!crmOptions.IsDynamicsProvider)
{
    throw new InvalidOperationException(
        $"Crm:Provider='{crmProviderName}' is not supported. This POC uses live Dynamics 365 only " +
        "(set Crm:Provider=Dynamics365, Crm:EnableLiveIntegration=true, and Crm:BaseUrl).");
}

if (!crmOptions.EnableLiveIntegration || string.IsNullOrWhiteSpace(crmOptions.BaseUrl))
{
    throw new InvalidOperationException(
        "Dynamics 365 requires Crm:EnableLiveIntegration=true and Crm:BaseUrl.");
}

builder.Services.AddSingleton<ICrmMetadataService, DynamicsCrmMetadataService>();

// Warm approved CRM metadata after the host starts (does not block Kestrel listen).
builder.Services.AddHostedService<CrmMetadataWarmupHostedService>();

// All AI providers stay registered; Ai:Provider selects which IChatClient is used.
// No silent fallback between providers — a misconfigured selection fails explicitly.
builder.Services.AddSingleton<LazyGeminiChatClient>();
builder.Services.AddSingleton<LazyOpenAiChatClient>();
builder.Services.AddSingleton<LazyGroqChatClient>();
builder.Services.AddSingleton<LazyOpenRouterChatClient>();
builder.Services.AddSingleton<LazyOllamaChatClient>();
builder.Services.AddSingleton<IChatClient>(sp =>
{
    var ai = sp.GetRequiredService<IOptions<AiOptions>>().Value;
    var provider = (ai.Provider ?? "Groq").Trim();

    if (provider.Equals("OpenAI", StringComparison.OrdinalIgnoreCase))
    {
        return sp.GetRequiredService<LazyOpenAiChatClient>();
    }

    if (provider.Equals("Gemini", StringComparison.OrdinalIgnoreCase))
    {
        return sp.GetRequiredService<LazyGeminiChatClient>();
    }

    if (provider.Equals("Groq", StringComparison.OrdinalIgnoreCase))
    {
        return sp.GetRequiredService<LazyGroqChatClient>();
    }

    if (provider.Equals("OpenRouter", StringComparison.OrdinalIgnoreCase))
    {
        return sp.GetRequiredService<LazyOpenRouterChatClient>();
    }

    if (provider.Equals("Ollama", StringComparison.OrdinalIgnoreCase))
    {
        return sp.GetRequiredService<LazyOllamaChatClient>();
    }

    throw new InvalidOperationException(
        $"Unknown Ai:Provider '{provider}'. Use 'Gemini', 'OpenAI', 'Groq', 'OpenRouter', or 'Ollama'.");
});

builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
        policy.AllowAnyHeader().AllowAnyMethod().AllowAnyOrigin());
});

var app = builder.Build();

var aiOptionsSnapshot = app.Services.GetRequiredService<IOptions<AiOptions>>().Value;
var aiProvider = aiOptionsSnapshot.Provider ?? "Groq";
var aiModel = aiProvider switch
{
    var p when p.Equals("OpenAI", StringComparison.OrdinalIgnoreCase)
        => builder.Configuration["OpenAI:ModelName"] ?? "gpt-4o-mini",
    var p when p.Equals("Gemini", StringComparison.OrdinalIgnoreCase)
        => builder.Configuration["Gemini:ModelName"] ?? "gemini-3.8-flash",
    var p when p.Equals("OpenRouter", StringComparison.OrdinalIgnoreCase)
        => builder.Configuration["OpenRouter:Model"]
           ?? builder.Configuration["OpenRouter:ModelName"]
           ?? "openai/gpt-4o",
    var p when p.Equals("Ollama", StringComparison.OrdinalIgnoreCase)
        => aiOptionsSnapshot.Ollama.ResolveModel(),
    _ => builder.Configuration["Groq:Model"]
         ?? builder.Configuration["Groq:ModelName"]
         ?? "openai/gpt-oss-120b"
};

var semanticOpts = app.Services.GetRequiredService<IOptions<SemanticMetadataOptions>>().Value;
var embedOpts = app.Services.GetRequiredService<IOptions<OllamaEmbeddingOptions>>().Value;
var effectiveCrm = app.Services.GetRequiredService<IOptions<CrmOptions>>().Value;
var approvalPolicy = app.Services.GetRequiredService<EntityApprovalPolicy>();
app.Logger.LogInformation(
    "CRM AI POC starting. AiProvider={AiProvider}, Model={Model}, CrmProvider={CrmProvider}, LiveIntegration={Live}, " +
    "HybridMetadata={Hybrid}, EmbeddingModel={EmbeddingModel}, EmbeddingBaseUrl={EmbeddingBaseUrl}",
    aiProvider,
    aiModel,
    crmProviderName,
    crmOptions.EnableLiveIntegration,
    semanticOpts.EnableHybridRetrieval,
    embedOpts.EmbeddingModel,
    embedOpts.BaseUrl);
app.Logger.LogInformation(
    "Crm ApprovedEntities diagnostics: Environment={Environment} ContentRootPath={ContentRoot} " +
    "Section=Crm:ApprovedEntities Binding=replace-not-merge WinningProvider={WinningProvider} " +
    "EffectiveCount={Count} EffectiveNames=[{Names}] " +
    "AuthorizationPolicyCount={AuthCount} OptionsBoundCount={OptionsCount}",
    app.Environment.EnvironmentName,
    app.Environment.ContentRootPath,
    approvedEntitiesProvider,
    approvalPolicy.ApprovedLogicalNames.Count,
    string.Join(", ", approvalPolicy.ApprovedLogicalNames.OrderBy(n => n, StringComparer.OrdinalIgnoreCase)),
    approvalPolicy.ApprovedLogicalNames.Count,
    effectiveCrm.ApprovedEntities?.Count ?? 0);
app.Logger.LogInformation(
    "Crm metadata warm-up and semantic index entity/document counts are logged by " +
    "CrmMetadataWarmupHostedService / HybridMetadataRanker after startup (WarmApprovedMetadataOnStartup / WarmIndexOnStartup).");

if (aiProvider.Equals("Ollama", StringComparison.OrdinalIgnoreCase))
{
    app.Logger.LogInformation(
        "Ollama chat configured. BaseUrl={BaseUrl}, ContextSize={ContextSize}, Temperature={Temperature}, EnableThinking={EnableThinking}",
        aiOptionsSnapshot.Ollama.ResolveBaseUrl(),
        aiOptionsSnapshot.Ollama.ContextSize,
        aiOptionsSnapshot.Ollama.Temperature,
        aiOptionsSnapshot.Ollama.EnableThinking);
}

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseCors();
app.UseMiddleware<LoopbackOnlyMiddleware>();

app.MapChatEndpoints();

app.Run();

static string? FirstNonEmpty(params string?[] values) =>
    values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

static string EnsureTrailingSlash(string url) =>
    url.EndsWith('/') ? url : url + "/";

public partial class Program;
