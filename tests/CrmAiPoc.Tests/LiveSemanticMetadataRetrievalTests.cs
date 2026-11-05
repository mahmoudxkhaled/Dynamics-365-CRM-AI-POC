using System.Text;
using System.Text.Json;
using CrmAiPoc.Api.Configuration;
using CrmAiPoc.Api.Crm.Dynamics;
using CrmAiPoc.Api.Crm.Metadata;
using CrmAiPoc.Api.Crm.Metadata.Semantic;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CrmAiPoc.Tests;

/// <summary>
/// Live Ollama + CRM metadata semantic retrieval (Phase 1.2 entity-scoped). Does not call Groq/ChatService.
/// Set CRM_LIVE_VERIFY=1. Report: %TEMP%\crm-poc-semantic-metadata-phase1.txt
/// </summary>
public sealed class LiveSemanticMetadataRetrievalTests
{
    private static readonly string ReportPath =
        Path.Combine(Path.GetTempPath(), "crm-poc-semantic-metadata-phase1.txt");

    private static readonly string[] Questions =
    [
        "Show the top 5 Sales Orders by total amount.",
        "Show the latest 3 products.",
        "Show Sales Orders with their Customer Account names.",
        "Show opportunities sorted by estimated revenue."
    ];

    [Fact]
    public async Task Live_Semantic_Retrieval_Four_Questions_Or_NotExecuted()
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

        var (baseUrl, crmOptions) = await LoadCrmConfigAsync();
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            L("STATUS: Not Executed — CRM BaseUrl missing");
            File.WriteAllText(ReportPath, sb.ToString());
            Assert.True(true);
            return;
        }

        var ollamaBase = Environment.GetEnvironmentVariable("OLLAMA_BASE_URL") ?? "http://localhost:11434";
        try
        {
            using var probe = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            using var ping = await probe.GetAsync(ollamaBase.TrimEnd('/') + "/api/tags");
            if (!ping.IsSuccessStatusCode)
            {
                L($"STATUS: Not Executed — Ollama not reachable at {ollamaBase} ({(int)ping.StatusCode})");
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
        services.AddHttpClient(DynamicsCrmHttp.HttpClientName, client =>
            {
                client.BaseAddress = new Uri(baseUrl.EndsWith('/') ? baseUrl : baseUrl + "/");
                client.Timeout = TimeSpan.FromSeconds(120);
            })
            .ConfigurePrimaryHttpMessageHandler(() => DynamicsCrmHttp.CreateHandler(crmOptions));
        services.AddSingleton<DynamicsCrmHttp>();
        services.AddSingleton<DynamicsConnectivityService>();
        services.AddSingleton<ICrmMetadataService, DynamicsCrmMetadataService>();
        services.AddHttpClient(OllamaEmbeddingClient.HttpClientName, (sp, client) =>
        {
            var opts = sp.GetRequiredService<IOptions<OllamaOptions>>().Value;
            client.BaseAddress = new Uri(opts.BaseUrl.EndsWith('/') ? opts.BaseUrl : opts.BaseUrl + "/");
            client.Timeout = TimeSpan.FromSeconds(opts.TimeoutSeconds);
        });
        services.AddSingleton<IOllamaEmbeddingClient, OllamaEmbeddingClient>();
        services.AddSingleton<InMemorySemanticMetadataIndex>();
        services.AddSingleton<ISemanticCrmMetadataRetriever, SemanticCrmMetadataRetriever>();

        await using var provider = services.BuildServiceProvider();
        var who = await provider.GetRequiredService<DynamicsConnectivityService>().WhoAmIAsync();
        if (!who.Success)
        {
            L($"STATUS: Not Executed — WhoAmI failed: {who.Message}");
            File.WriteAllText(ReportPath, sb.ToString());
            Assert.True(true);
            return;
        }

        L("STATUS: Executing Phase 1.2 entity-scoped semantic metadata retrieval (Ollama + live CRM metadata)");
        L($"OllamaBase={ollamaBase}");
        L($"CrmHost={new Uri(baseUrl).Host}");
        L("Config: TopKEntities=3, TopKAttributesPerEntity=20, TopKRelationships=10, MinSimilarity=0");

        var metadata = provider.GetRequiredService<ICrmMetadataService>();
        await metadata.EnsureApprovedEntitiesLoadedAsync();
        var retriever = provider.GetRequiredService<ISemanticCrmMetadataRetriever>();

        foreach (var q in Questions)
        {
            L("");
            L($"========== {q} ==========");
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var result = await retriever.RetrieveAsync(q);
            sw.Stop();
            if (!string.IsNullOrWhiteSpace(result.Error))
            {
                L($"ERROR={result.Error}");
                continue;
            }

            L($"LatencyMs={sw.ElapsedMilliseconds}");
            L($"IndexedDocumentCount={result.IndexedDocumentCount}");
            L($"RetrievedDocumentCount={result.RetrievedDocumentCount}");
            L($"EstimatedPayloadChars={result.EstimatedPayloadChars}");
            L($"IndexRebuilt={result.IndexRebuilt}");
            L($"ContentHash={result.ContentHash}");
            L($"EntityCount={result.EntityHits.Count} AttributeCount={result.AttributeHits.Count} RelationshipCount={result.RelationshipHits.Count}");
            L("Entities:");
            foreach (var h in result.EntityHits)
            {
                L($"  {h.Similarity:F4} {h.Document.EntityLogicalName} ({h.Document.DisplayName})");
            }

            L("Attribute counts per selected entity:");
            foreach (var entity in result.EntityHits.Select(e => e.Document.EntityLogicalName))
            {
                var count = result.AttributeHits.Count(a =>
                    a.Document.EntityLogicalName.Equals(entity, StringComparison.OrdinalIgnoreCase));
                L($"  {entity}: {count}");
            }

            L("Attributes:");
            foreach (var h in result.AttributeHits)
            {
                L($"  {h.Similarity:F4} {h.Document.EntityLogicalName}.{h.Document.AttributeLogicalName} ({h.Document.DisplayName}) type={h.Document.AttributeType}");
            }

            L("Relationships:");
            foreach (var h in result.RelationshipHits)
            {
                L($"  {h.Similarity:F4} {h.Document.RelationshipSchemaName} {h.Document.ReferencingEntity}->{h.Document.ReferencedEntity}");
            }

            ReportExpectations(L, q, result);
        }

        File.WriteAllText(ReportPath, sb.ToString());
    }

    private static void ReportExpectations(
        Action<string> L,
        string question,
        SemanticMetadataRetrievalResult result)
    {
        static bool HasEntity(SemanticMetadataRetrievalResult r, string name) =>
            r.EntityHits.Any(h => h.Document.EntityLogicalName.Equals(name, StringComparison.OrdinalIgnoreCase));

        static bool HasAttr(SemanticMetadataRetrievalResult r, string entity, string attr) =>
            r.AttributeHits.Any(h =>
                h.Document.EntityLogicalName.Equals(entity, StringComparison.OrdinalIgnoreCase) &&
                h.Document.AttributeLogicalName!.Equals(attr, StringComparison.OrdinalIgnoreCase));

        static bool HasAnyRel(SemanticMetadataRetrievalResult r, string a, string b) =>
            r.RelationshipHits.Any(h =>
                (h.Document.ReferencingEntity?.Equals(a, StringComparison.OrdinalIgnoreCase) == true &&
                 h.Document.ReferencedEntity?.Equals(b, StringComparison.OrdinalIgnoreCase) == true) ||
                (h.Document.ReferencingEntity?.Equals(b, StringComparison.OrdinalIgnoreCase) == true &&
                 h.Document.ReferencedEntity?.Equals(a, StringComparison.OrdinalIgnoreCase) == true));

        L("Expectation checks (diagnostic only):");
        if (question.Contains("Sales Orders by total amount", StringComparison.OrdinalIgnoreCase))
        {
            L($"  salesorder entity={HasEntity(result, "salesorder")}");
            L($"  salesorder.totalamount={HasAttr(result, "salesorder", "totalamount")}");
        }
        else if (question.Contains("latest 3 products", StringComparison.OrdinalIgnoreCase))
        {
            L($"  product entity={HasEntity(result, "product")}");
            L($"  product.createdon={HasAttr(result, "product", "createdon")}");
        }
        else if (question.Contains("Customer Account", StringComparison.OrdinalIgnoreCase))
        {
            L($"  salesorder entity={HasEntity(result, "salesorder")}");
            L($"  account entity={HasEntity(result, "account")}");
            L($"  salesorder<->account relationship={HasAnyRel(result, "salesorder", "account")}");
        }
        else if (question.Contains("estimated revenue", StringComparison.OrdinalIgnoreCase))
        {
            L($"  opportunity entity={HasEntity(result, "opportunity")}");
            L($"  opportunity.estimatedvalue={HasAttr(result, "opportunity", "estimatedvalue")}");
        }
    }

    private static async Task<(string? BaseUrl, CrmOptions Crm)> LoadCrmConfigAsync()
    {
        var baseUrl = Environment.GetEnvironmentVariable("Crm__BaseUrl")
                      ?? Environment.GetEnvironmentVariable("CRM_BASE_URL");
        var devPath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..",
            "src", "CrmAiPoc.Api", "appsettings.Development.json"));
        if (!File.Exists(devPath))
        {
            devPath = Path.GetFullPath(Path.Combine(
                AppContext.BaseDirectory, "..", "..", "..", "..",
                "src", "CrmAiPoc.Api", "appsettings.Development.json"));
        }

        var approved = new List<string>();
        var aliases = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var labels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (File.Exists(devPath))
        {
            using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(devPath));
            var root = doc.RootElement;
            if (string.IsNullOrWhiteSpace(baseUrl) &&
                root.TryGetProperty("Crm", out var crm) &&
                crm.TryGetProperty("BaseUrl", out var bu))
            {
                baseUrl = bu.GetString();
            }

            if (root.TryGetProperty("Crm", out var crm2))
            {
                if (crm2.TryGetProperty("ApprovedEntities", out var arr))
                {
                    foreach (var item in arr.EnumerateArray())
                    {
                        if (item.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(item.GetString()))
                        {
                            approved.Add(item.GetString()!);
                        }
                    }
                }

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
            ApprovedEntities = approved,
            ApprovedRelationships = [],
            MetadataCacheSeconds = 3600,
            EntityAliases = aliases,
            RelationshipLabels = labels
        };
        return (baseUrl, crmOptions);
    }
}
