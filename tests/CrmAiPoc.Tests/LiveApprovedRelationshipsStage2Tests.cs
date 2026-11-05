using System.Text;
using System.Text.Json;
using CrmAiPoc.Api.Ai;
using CrmAiPoc.Api.Configuration;
using CrmAiPoc.Api.Crm.Dynamics;
using CrmAiPoc.Api.Crm.Generic;
using CrmAiPoc.Api.Crm.Metadata;
using CrmAiPoc.Api.Models;
using CrmAiPoc.Api.Security;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CrmAiPoc.Tests;

/// <summary>
/// Live verify: Stage 2 sends only IsApproved relationships; attributes remain complete.
/// Set CRM_LIVE_VERIFY=1. Report: %TEMP%\crm-poc-approved-rels-stage2.txt
/// </summary>
public sealed class LiveApprovedRelationshipsStage2Tests
{
    private static readonly string ReportPath =
        Path.Combine(Path.GetTempPath(), "crm-poc-approved-rels-stage2.txt");

    [Fact]
    public async Task Live_Product_Approved_Relationships_And_Latest_3_Or_NotExecuted()
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

        if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(groqKey))
        {
            L("STATUS: Not Executed — BaseUrl or Groq key missing");
            File.WriteAllText(ReportPath, sb.ToString());
            Assert.True(true);
            return;
        }

        var groqModel = GetDevString(dev, "Groq", "ModelName") ?? "openai/gpt-oss-120b";
        var groqBase = GetDevString(dev, "Groq", "BaseUrl") ?? "https://api.groq.com/openai/v1";

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
            approved = ["account", "contact", "lead", "salesorder", "opportunity", "quote", "invoice",
                "salesorderdetail", "product", "campaign", "te_serviceorder"];
        }

        var crmOptions = new CrmOptions
        {
            Provider = "Dynamics365",
            EnableLiveIntegration = true,
            BaseUrl = baseUrl,
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

        var services = new ServiceCollection();
        services.AddLogging(b => b.AddConsole().SetMinimumLevel(LogLevel.Information));
        services.AddSingleton(Options.Create(crmOptions));
        services.AddSingleton(Options.Create(new AiOptions { Provider = "Groq", ProviderTimeoutSeconds = 90 }));
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
        services.AddSingleton<IGenericCrmQueryBuilder, GenericCrmQueryBuilder>();
        services.AddSingleton<FetchXmlAggregateBuilder>();
        services.AddSingleton<ICrmAuthorizationService, CrmAuthorizationService>();
        services.AddSingleton<IGenericCrmRepository, DynamicsGenericCrmRepository>();
        services.AddSingleton<ICrmAiMetadataCatalogService, CrmAiMetadataCatalogService>();
        services.AddSingleton<LazyGroqChatClient>();
        services.AddSingleton<IChatClient>(sp => sp.GetRequiredService<LazyGroqChatClient>());
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

        L("STATUS: Executing approved-relationship Stage 2 live verify");
        var metadata = provider.GetRequiredService<ICrmMetadataService>();
        await metadata.EnsureApprovedEntitiesLoadedAsync();
        var productMeta = await metadata.GetEntityAsync("product");
        Assert.NotNull(productMeta);

        var discoveredRels = productMeta!.Relationships.Count;
        var approvedRels = productMeta.Relationships.Count(r => r.IsApproved);
        var readableAttrs = productMeta.Attributes.Count(a => a.IsValidForRead);

        var catalog = provider.GetRequiredService<ICrmAiMetadataCatalogService>();
        var stage2 = await catalog.BuildApprovedCatalogForSelectedEntitiesAsync(["product"]);
        Assert.True(stage2.Success, stage2.Error);
        var detail = Assert.Single(stage2.Catalog.Detail);
        Assert.Equal("product", detail.LogicalName, ignoreCase: true);

        L($"ProductDiscoveredAttributes={readableAttrs}");
        L($"ProductSentAttributes={detail.Attributes.Count}");
        L($"ProductDiscoveredRelationships={discoveredRels}");
        L($"ProductApprovedRelationships={approvedRels}");
        L($"ProductSentRelationships={detail.Relationships.Count}");
        L($"Stage2PayloadChars={stage2.CharacterCount}");
        L($"AnyUnapprovedInCatalog={detail.Relationships.Any(r => !r.IsApproved)}");
        L($"BlockContainsFalseIsApproved={stage2.FormattedBlock.Contains("\"isApproved\":false", StringComparison.Ordinal)}");
        foreach (var r in detail.Relationships)
        {
            L($"  sentRel schema={r.SchemaName} isApproved={r.IsApproved}");
        }

        Assert.Equal(readableAttrs, detail.Attributes.Count);
        Assert.Equal(approvedRels, detail.Relationships.Count);
        Assert.DoesNotContain(detail.Relationships, r => !r.IsApproved);

        using var scope = provider.CreateScope();
        var chat = scope.ServiceProvider.GetRequiredService<ChatService>();
        var response = await chat.ChatAsync(
            new ChatRequestDto { Message = "Show the latest 3 products." },
            CancellationToken.None);

        L("");
        L("=== Live question: Show the latest 3 products. ===");
        L($"SelectedTool={response.SelectedTool}");
        L($"CrmDataProvider={response.CrmDataProvider}");
        L($"ReplyPreview={Truncate(response.Reply, 1200)}");
        if (response.StructuredData is not null)
        {
            L($"StructuredData={Truncate(JsonSerializer.Serialize(response.StructuredData), 1500)}");
        }

        File.WriteAllText(ReportPath, sb.ToString());
    }

    private static string? GetDevString(JsonElement? root, string section, string key)
    {
        if (root is null ||
            !root.Value.TryGetProperty(section, out var sec) ||
            !sec.TryGetProperty(key, out var val) ||
            val.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return val.GetString();
    }

    private static string Truncate(string? value, int max) =>
        string.IsNullOrEmpty(value) ? string.Empty :
        value.Length <= max ? value : value[..max] + "…";
}
