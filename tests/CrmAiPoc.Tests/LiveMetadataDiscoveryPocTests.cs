using System.Diagnostics;
using System.Text;
using System.Text.Json;
using CrmAiPoc.Api.Ai;
using CrmAiPoc.Api.Configuration;
using CrmAiPoc.Api.Crm.Dynamics;
using CrmAiPoc.Api.Crm.FetchXml;
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
/// Live POC: lexical discover_crm_schema against Dynamics metadata, plus optional Groq on/off comparison.
/// Set CRM_LIVE_VERIFY=1. Report: %TEMP%\crm-poc-metadata-discovery.txt
/// </summary>
public sealed class LiveMetadataDiscoveryPocTests
{
    private static readonly string ReportPath =
        Path.Combine(Path.GetTempPath(), "crm-poc-metadata-discovery.txt");

    private static readonly (string Concept, string Entity, string ExpectedLogical)[] ExpectedFields =
    [
        ("order total value", "salesorder", "totalamount"),
        ("customer account name", "account", "name"),
        ("billing contact full name", "contact", "fullname"),
        ("order created date", "salesorder", "createdon")
    ];

    private static readonly string[] EvalQuestions =
    [
        "Show me our biggest orders this year.",
        "Show me our 10 biggest orders with customer names.",
        "Who are our most valuable customers?",
        "Show me orders with their billing contacts.",
        "Which customers haven't placed an order recently?",
        "Which customers have opportunities but no contacts?",
        "Which months had the highest sales last year?"
    ];

    [Fact]
    public async Task Live_Metadata_Discovery_And_Optional_Groq_Comparison_Or_NotExecuted()
    {
        var sb = new StringBuilder();
        void L(string line) => sb.AppendLine(line);

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

        var aiOptions = new AiOptions
        {
            Provider = "Groq",
            ProviderTimeoutSeconds = 90,
            MaxQueryPlanCorrectionRetries = 2,
            MaxSchemaDiscoveryCallsPerRequest = 2,
            MaxSchemaDiscoveryCandidatesPerConcept = 5
        };

        var services = new ServiceCollection();
        services.AddLogging(b => b.AddConsole().SetMinimumLevel(LogLevel.Information));
        services.AddSingleton(Options.Create(crmOptions));
        services.AddSingleton(Options.Create(aiOptions));
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

        L($"STATUS: Executing WhoAmI={who.UserId}");
        await provider.GetRequiredService<ICrmMetadataService>().EnsureApprovedEntitiesLoadedAsync();
        var discovery = provider.GetRequiredService<CrmSchemaDiscoveryService>();

        L("");
        L("=== Direct discover_crm_schema against live metadata ===");
        var sw = Stopwatch.StartNew();
        var discovered = await discovery.DiscoverAsync(new CrmSchemaDiscoveryRequest
        {
            Entities = ["salesorder", "account", "contact", "opportunity"],
            Concepts = ExpectedFields.Select(e => e.Concept).ToArray(),
            RelationshipPairs =
            [
                ("salesorder", "account"),
                ("salesorder", "contact"),
                ("account", "contact"),
                ("account", "opportunity")
            ]
        });
        sw.Stop();

        Assert.True(discovered.Success, discovered.Error);
        Assert.NotNull(discovered.Result);
        L($"LatencyMs={sw.ElapsedMilliseconds}");
        L($"ResponseCharacters={discovered.Result!.ResponseCharacters}");
        L($"FieldGroups={discovered.Result.Fields.Count}");
        L($"RelationshipGroups={discovered.Result.Relationships.Count}");
        L($"Unresolved={string.Join("; ", discovered.Result.UnresolvedConcepts)}");
        L("");
        L("Sample discovery JSON (truncated):");
        L(discovered.Json.Length <= 2500 ? discovered.Json : discovered.Json[..2500] + "…");

        L("");
        L("=== Field ranking hits ===");
        var misses = new List<string>();
        foreach (var expected in ExpectedFields)
        {
            var field = discovered.Result.Fields.FirstOrDefault(f =>
                f.Concept.Equals(expected.Concept, StringComparison.OrdinalIgnoreCase));
            var hit = field?.Candidates.Any(c =>
                c.LogicalName.Equals(expected.ExpectedLogical, StringComparison.OrdinalIgnoreCase)) == true;
            L($"Concept=\"{expected.Concept}\" Expected={expected.Entity}.{expected.ExpectedLogical} Hit={hit} " +
              $"Entity={field?.Entity ?? "(none)"} Top={field?.Candidates.FirstOrDefault()?.LogicalName ?? "(none)"} " +
              $"Candidates={field?.Candidates.Count ?? 0}");
            if (!hit)
            {
                misses.Add($"{expected.Concept} → {expected.Entity}.{expected.ExpectedLogical}");
            }
        }

        L("");
        L("=== Relationship discovery ===");
        foreach (var rel in discovered.Result.Relationships)
        {
            L($"Pair={rel.FromEntity}->{rel.ToEntity} Candidates={rel.Candidates.Count} " +
              $"Schemas=[{string.Join(", ", rel.Candidates.Select(c => c.SchemaName).Take(6))}]");
        }

        L("");
        L("=== Groq comparison (first 3 eval questions): discovery ON then OFF ===");
        await using (var scope = provider.CreateAsyncScope())
        {
            var chat = scope.ServiceProvider.GetRequiredService<ChatService>();

            async Task RunMode(string mode, int maxDiscovery)
            {
                aiOptions.MaxSchemaDiscoveryCallsPerRequest = maxDiscovery;
                L($"--- Mode={mode} MaxDiscovery={maxDiscovery} ---");
                foreach (var q in EvalQuestions.Take(3))
                {
                    var qSw = Stopwatch.StartNew();
                    try
                    {
                        var response = await chat.ChatAsync(
                            new ChatRequestDto
                            {
                                ConversationId = $"meta-disc-{mode}-{Guid.NewGuid():N}",
                                Message = q
                            },
                            CancellationToken.None);
                        qSw.Stop();
                        L($"Q=\"{q}\"");
                        L($"  LatencyMs={qSw.ElapsedMilliseconds} Tool={response.SelectedTool} " +
                          $"HasTable={response.StructuredData is not null} ReplyChars={(response.Reply?.Length ?? 0)}");
                    }
                    catch (Exception ex)
                    {
                        qSw.Stop();
                        L($"Q=\"{q}\" FAILED LatencyMs={qSw.ElapsedMilliseconds} Error={ex.Message}");
                    }
                }
            }

            await RunMode("discovery-on", 2);
            await RunMode("discovery-off", 0);
        }

        L("");
        L(misses.Count == 0
            ? "FIELD_MISSES: none (expected logical names appeared in candidates)"
            : "FIELD_MISSES: " + string.Join(" | ", misses));
        L($"REPORT: {ReportPath}");
        File.WriteAllText(ReportPath, sb.ToString());
        Assert.True(true);
    }

    private static async Task<(string? BaseUrl, string? GroqKey, string GroqModel, string GroqBase, CrmOptions Crm)>
        LoadDevConfigAsync()
    {
        string? FindDev()
        {
            var relative = Path.Combine("src", "CrmAiPoc.Api", "appsettings.Development.json");
            foreach (var start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
            {
                var dir = new DirectoryInfo(start);
                while (dir is not null)
                {
                    var candidate = Path.Combine(dir.FullName, relative);
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }

                    dir = dir.Parent;
                }
            }

            return null;
        }

        var path = FindDev();
        JsonElement? root = null;
        if (path is not null)
        {
            using var doc = JsonDocument.Parse(
                await File.ReadAllTextAsync(path),
                new JsonDocumentOptions
                {
                    CommentHandling = JsonCommentHandling.Skip,
                    AllowTrailingCommas = true
                });
            root = doc.RootElement.Clone();
        }

        static string? Get(JsonElement? r, string section, string key)
        {
            if (r is null || !r.Value.TryGetProperty(section, out var sec) || !sec.TryGetProperty(key, out var val))
            {
                return null;
            }

            return val.ValueKind == JsonValueKind.String ? val.GetString() : null;
        }

        var baseUrl = Environment.GetEnvironmentVariable("Crm__BaseUrl")
                      ?? Environment.GetEnvironmentVariable("CRM_BASE_URL")
                      ?? Get(root, "Crm", "BaseUrl");
        var groqKey = Environment.GetEnvironmentVariable("Groq__ApiKey")
                      ?? Get(root, "Groq", "ApiKey");
        var groqModel = Environment.GetEnvironmentVariable("Groq__Model")
                        ?? Get(root, "Groq", "Model")
                        ?? Get(root, "Groq", "ModelName")
                        ?? "openai/gpt-oss-120b";
        var groqBase = Environment.GetEnvironmentVariable("Groq__BaseUrl")
                       ?? Get(root, "Groq", "BaseUrl")
                       ?? "https://api.groq.com/openai/v1";

        var approved = new List<string>();
        var aliases = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var labels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (root is { } r && r.TryGetProperty("Crm", out var crmEl))
        {
            if (crmEl.TryGetProperty("ApprovedEntities", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                approved.AddRange(arr.EnumerateArray()
                    .Where(i => i.ValueKind == JsonValueKind.String)
                    .Select(i => i.GetString()!)
                    .Where(s => !string.IsNullOrWhiteSpace(s)));
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

        var crm = new CrmOptions
        {
            Provider = "Dynamics365",
            EnableLiveIntegration = true,
            BaseUrl = baseUrl ?? string.Empty,
            UseDefaultCredentials = true,
            AllowSendApprovedMetadataCatalogToAi = false,
            WarmApprovedMetadataOnStartup = false,
            RestrictToLoopback = false,
            ApprovedEntities = approved,
            ApprovedRelationships = [],
            MetadataCacheSeconds = 3600,
            BusinessTimeZoneId = "Africa/Cairo",
            EntityAliases = aliases,
            RelationshipLabels = labels
        };

        return (baseUrl, groqKey, groqModel, groqBase, crm);
    }
}
