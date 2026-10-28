using CrmAiPoc.Api.Ai;
using CrmAiPoc.Api.Configuration;
using CrmAiPoc.Api.Crm.Dynamics;
using CrmAiPoc.Api.Crm.Generic;
using CrmAiPoc.Api.Crm.Metadata;
using CrmAiPoc.Api.Crm.Validation;
using CrmAiPoc.Api.Diagnostics;
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
    options.ModelName = FirstNonEmpty(
        builder.Configuration["Groq:ModelName"],
        Environment.GetEnvironmentVariable("GROQ_MODEL")) ?? options.ModelName;
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

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new()
    {
        Title = "CRM AI POC API",
        Version = "v1",
        Description = "Read-only Dynamics 365 CRM assistant POC. AI via Ai:Provider; CRM data via Crm:Provider=Mock|Dynamics365 (generic query engine)."
    });
});

builder.Services.AddSingleton<ConversationStore>();
builder.Services.AddSingleton<CrmArgumentValidator>();
builder.Services.AddSingleton<CrmMetadataRegistry>();
builder.Services.AddSingleton<EntityApprovalPolicy>();
builder.Services.AddSingleton<ICrmAuthorizationService, CrmAuthorizationService>();
builder.Services.AddSingleton<IGenericCrmQueryBuilder, GenericCrmQueryBuilder>();
builder.Services.AddSingleton<ExecutionTraceAccessor>();
builder.Services.AddSingleton<IExecutionTraceStore, ExecutionTraceStore>();
builder.Services.AddSingleton<IExecutionTracer, ExecutionTracer>();
builder.Services.AddScoped<CrmAiTools>();
builder.Services.AddScoped<ChatService>();

// Dynamics HTTP + diagnostics are always registered so connectivity can be tested
// without permanently switching Crm:Provider (calls still require EnableLiveIntegration + BaseUrl).
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
builder.Services.AddSingleton<DynamicsSchemaDiscoveryService>();

var crmOptions = builder.Configuration.GetSection(CrmOptions.SectionName).Get<CrmOptions>() ?? new CrmOptions();
var crmProviderName = (crmOptions.Provider ?? "Mock").Trim();

if (crmOptions.IsDynamicsProvider)
{
    if (!crmOptions.EnableLiveIntegration || string.IsNullOrWhiteSpace(crmOptions.BaseUrl))
    {
        throw new InvalidOperationException(
            "Crm:Provider=Dynamics365 requires Crm:EnableLiveIntegration=true and Crm:BaseUrl. " +
            "Use Crm:Provider=Mock for local POC without live CRM.");
    }

    builder.Services.AddSingleton<ICrmMetadataService, DynamicsCrmMetadataService>();
    builder.Services.AddSingleton<IGenericCrmRepository, DynamicsGenericCrmRepository>();
}
else
{
    builder.Services.AddSingleton<ICrmMetadataService, MockCrmMetadataService>();
    builder.Services.AddSingleton<IGenericCrmRepository, MockGenericCrmRepository>();
}

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
    _ => builder.Configuration["Groq:ModelName"] ?? "openai/gpt-oss-120b"
};

app.Logger.LogInformation(
    "CRM AI POC starting. AiProvider={AiProvider}, Model={Model}, CrmProvider={CrmProvider}, LiveIntegration={Live}, AllowSendLiveDataToAi={AllowAi}",
    aiProvider,
    aiModel,
    crmProviderName,
    crmOptions.EnableLiveIntegration,
    crmOptions.AllowSendLiveDataToAi);

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseCors();
app.UseMiddleware<LoopbackOnlyMiddleware>();
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapChatEndpoints();
app.MapCrmDiagnosticsEndpoints();
app.MapExecutionInspectorEndpoints();

app.Run();

static string? FirstNonEmpty(params string?[] values) =>
    values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

static string EnsureTrailingSlash(string url) =>
    url.EndsWith('/') ? url : url + "/";

public partial class Program;
