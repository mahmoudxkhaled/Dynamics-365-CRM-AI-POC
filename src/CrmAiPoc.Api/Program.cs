using CrmAiPoc.Api.Ai;
using CrmAiPoc.Api.Configuration;
using CrmAiPoc.Api.Crm.Dynamics;
using CrmAiPoc.Api.Crm.Generic;
using CrmAiPoc.Api.Crm.Metadata;
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

builder.Services.Configure<CrmOptions>(options =>
{
    builder.Configuration.GetSection(CrmOptions.SectionName).Bind(options);
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

builder.Services.Configure<OllamaOptions>(options =>
{
    builder.Configuration.GetSection(OllamaOptions.SectionName).Bind(options);
    options.BaseUrl = FirstNonEmpty(
        options.BaseUrl,
        Environment.GetEnvironmentVariable("OLLAMA_BASE_URL")) ?? options.BaseUrl;
    options.EmbeddingModel = FirstNonEmpty(
        options.EmbeddingModel,
        Environment.GetEnvironmentVariable("OLLAMA_EMBEDDING_MODEL")) ?? options.EmbeddingModel;
});

builder.Services.Configure<SemanticMetadataOptions>(
    builder.Configuration.GetSection(SemanticMetadataOptions.SectionName));
builder.Services.PostConfigure<SemanticMetadataOptions>(options =>
{
    // Keep the embedding cache inside the API app folder (ContentRoot/App_Data/...).
    if (string.IsNullOrWhiteSpace(options.CacheDirectory))
    {
        options.CacheDirectory = Path.Combine("App_Data", "semantic-metadata-index");
    }

    if (!Path.IsPathRooted(options.CacheDirectory))
    {
        options.CacheDirectory = Path.GetFullPath(
            Path.Combine(builder.Environment.ContentRootPath, options.CacheDirectory));
    }
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
builder.Services.AddSingleton<IGenericCrmQueryBuilder, GenericCrmQueryBuilder>();
builder.Services.AddSingleton<FetchXmlAggregateBuilder>();
builder.Services.AddScoped<CrmAiTools>();
builder.Services.AddScoped<ChatService>();

// Semantic metadata retrieval (Ollama embeddings) — selects compact AI catalog for ChatService.
builder.Services.AddHttpClient(OllamaEmbeddingClient.HttpClientName, (sp, client) =>
{
    var opts = sp.GetRequiredService<IOptions<OllamaOptions>>().Value;
    var baseUrl = string.IsNullOrWhiteSpace(opts.BaseUrl)
        ? "http://localhost:11434/"
        : (opts.BaseUrl.EndsWith('/') ? opts.BaseUrl : opts.BaseUrl + "/");
    client.BaseAddress = new Uri(baseUrl);
    client.Timeout = TimeSpan.FromSeconds(Math.Clamp(opts.TimeoutSeconds, 5, 300));
});
builder.Services.AddSingleton<IOllamaEmbeddingClient, OllamaEmbeddingClient>();
builder.Services.AddSingleton<InMemorySemanticMetadataIndex>();
builder.Services.AddSingleton<ISemanticCrmMetadataRetriever, SemanticCrmMetadataRetriever>();

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
var crmProviderName = (crmOptions.Provider ?? "Dynamics365").Trim();

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
builder.Services.AddSingleton<IGenericCrmRepository, DynamicsGenericCrmRepository>();

// Warm approved CRM metadata after the host starts (does not block Kestrel listen).
builder.Services.AddHostedService<CrmMetadataWarmupHostedService>();

// All AI providers stay registered; Ai:Provider selects which IChatClient is used.
builder.Services.AddSingleton<LazyGeminiChatClient>();
builder.Services.AddSingleton<LazyOpenAiChatClient>();
builder.Services.AddSingleton<LazyGroqChatClient>();
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

    throw new InvalidOperationException(
        $"Unknown Ai:Provider '{provider}'. Use 'Gemini', 'OpenAI', or 'Groq'.");
});

builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
        policy.AllowAnyHeader().AllowAnyMethod().AllowAnyOrigin());
});

var app = builder.Build();

var aiProvider = builder.Configuration["Ai:Provider"] ?? "Groq";
var aiModel = aiProvider switch
{
    var p when p.Equals("OpenAI", StringComparison.OrdinalIgnoreCase)
        => builder.Configuration["OpenAI:ModelName"] ?? "gpt-4o-mini",
    var p when p.Equals("Gemini", StringComparison.OrdinalIgnoreCase)
        => builder.Configuration["Gemini:ModelName"] ?? "gemini-3.8-flash",
    _ => builder.Configuration["Groq:Model"]
         ?? builder.Configuration["Groq:ModelName"]
         ?? "openai/gpt-oss-120b"
};

app.Logger.LogInformation(
    "CRM AI POC starting. AiProvider={AiProvider}, Model={Model}, CrmProvider={CrmProvider}, LiveIntegration={Live}",
    aiProvider,
    aiModel,
    crmProviderName,
    crmOptions.EnableLiveIntegration);

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseCors();
app.UseMiddleware<LoopbackOnlyMiddleware>();

app.MapChatEndpoints();
if (app.Environment.IsDevelopment())
{
    app.MapSemanticMetadataEndpoints();
}

app.Run();

static string? FirstNonEmpty(params string?[] values) =>
    values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

static string EnsureTrailingSlash(string url) =>
    url.EndsWith('/') ? url : url + "/";

public partial class Program;
