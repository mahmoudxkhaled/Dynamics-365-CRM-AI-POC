using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using CrmAiPoc.Api.Ai;
using CrmAiPoc.Api.Configuration;
using CrmAiPoc.Api.Crm.Dynamics;
using CrmAiPoc.Api.Crm.Generic;
using CrmAiPoc.Api.Crm.Metadata;
using CrmAiPoc.Api.Crm.Query;
using CrmAiPoc.Api.Models;
using CrmAiPoc.Api.Security;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CrmAiPoc.Tests;

/// <summary>
/// Live Groq → query_crm → Parser → FetchXML → Dynamics verification for monthly Sales Order counts 2025.
/// Set CRM_LIVE_VERIFY=1. Report: %TEMP%\crm-poc-monthly-2025-verify.txt
/// </summary>
public sealed class LiveMonthlySalesOrder2025VerificationTests
{
    private static readonly string ReportPath =
        Path.Combine(Path.GetTempPath(), "crm-poc-monthly-2025-verify.txt");

    [Fact]
    public async Task Live_Monthly_SalesOrder_Counts_2025_Or_NotExecuted()
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

        var baseUrl = Environment.GetEnvironmentVariable("Crm__BaseUrl")
                      ?? Environment.GetEnvironmentVariable("CRM_BASE_URL");
        var groqKey = Environment.GetEnvironmentVariable("Groq__ApiKey")
                      ?? Environment.GetEnvironmentVariable("GROQ_API_KEY");

        // Fall back to Development appsettings values when env not set (local live verify).
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

        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            L("STATUS: Not Executed — Crm BaseUrl missing");
            File.WriteAllText(ReportPath, sb.ToString());
            Assert.True(true);
            return;
        }

        if (string.IsNullOrWhiteSpace(groqKey))
        {
            L("STATUS: Not Executed — Groq ApiKey missing");
            File.WriteAllText(ReportPath, sb.ToString());
            Assert.True(true);
            return;
        }

        var groqModel = Environment.GetEnvironmentVariable("Groq__ModelName")
                        ?? GetDevString(dev, "Groq", "ModelName")
                        ?? "openai/gpt-oss-120b";
        var groqBase = Environment.GetEnvironmentVariable("Groq__BaseUrl")
                       ?? GetDevString(dev, "Groq", "BaseUrl")
                       ?? "https://api.groq.com/openai/v1";

        var crmOptions = new CrmOptions
        {
            Provider = "Dynamics365",
            EnableLiveIntegration = true,
            BaseUrl = baseUrl,
            UseDefaultCredentials = true,
            AllowSendApprovedMetadataCatalogToAi = true,
            ApprovedEntities = ["account", "contact", "salesorder", "lead"],
            ApprovedRelationships = [],
            MetadataCacheSeconds = 3600,
            BusinessTimeZoneId = "Africa/Cairo",
            EntityAliases = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["account"] = ["accounts", "customers", "customer account", "customer accounts"],
                ["contact"] = ["contacts", "contact persons", "primary contact", "primary contacts"],
                ["salesorder"] = ["sales order", "sales orders", "orders"],
                ["lead"] = ["leads", "prospects"]
            },
            RelationshipLabels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["te_account_salesorder_customeraccountid"] = "Customer Account",
                ["order_customer_accounts"] = "Potential Customer (Account)",
                ["account_primary_contact"] = "Primary Contact"
            }
        };

        var services = new ServiceCollection();
        services.AddLogging(b => b.AddConsole().SetMinimumLevel(LogLevel.Information));
        services.AddSingleton(Options.Create(crmOptions));
        services.AddSingleton(Options.Create(new AiOptions { Provider = "Groq" }));
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
                client.Timeout = TimeSpan.FromSeconds(90);
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
        var connectivity = provider.GetRequiredService<DynamicsConnectivityService>();
        var who = await connectivity.WhoAmIAsync();
        if (!who.Success)
        {
            L($"STATUS: Not Executed — WhoAmI failed: {who.Message}");
            File.WriteAllText(ReportPath, sb.ToString());
            Assert.True(true);
            return;
        }

        L("STATUS: Executing live Groq → CRM pipeline");
        L($"WhoAmI: Success UserIdPresent={!string.IsNullOrWhiteSpace(who.UserId?.ToString())}");
        L($"GroqModel: {groqModel}");
        L($"BaseUrlHost: {new Uri(baseUrl).Host}");
        L($"Question: Show monthly Sales Order counts for 2025.");

        // Controlled Groq-style shorthand still parses before chat (regression of original failure).
        var shorthand = CrmQueryPlanParser.ParseFromToolArguments(new Dictionary<string, object?>
        {
            ["entity"] = "salesorder",
            ["operation"] = "aggregate",
            ["relativeDateField"] = "createdon",
            ["dateRangeJson"] = """{"calendar_year":2025}""",
            ["aggregatesJson"] = """[{"function":"count","field":"salesorderid","alias":"OrderCount"}]""",
            ["groupByJson"] = """[{"field":"createdon","alias":"Month","dateGrouping":"month"}]"""
        });
        L("");
        L("=== Controlled Groq-shorthand parse ===");
        L($"Success={shorthand.Success} Error={shorthand.Error}");
        if (shorthand.Success)
        {
            var expr = shorthand.Plan!.RelativeDate!.GetExpression();
            var tz = BusinessTimeZone.Resolve("Africa/Cairo");
            var resolved = DateRangeResolver.Resolve(expr, DateTimeOffset.UtcNow, tz);
            L($"ParsedKind={expr.Kind} Year={expr.Year}");
            L($"Resolved=[{resolved.StartUtc:u} .. {resolved.EndUtcExclusive:u})");
            L($"GroupByYearPresent={shorthand.Plan.GroupBy.Any(g => g.DateGrouping == CrmDateGrouping.Year)}");
        }

        Assert.True(shorthand.Success, shorthand.Error);

        using var scope = provider.CreateScope();
        var chat = scope.ServiceProvider.GetRequiredService<ChatService>();
        var response = await chat.ChatAsync(
            new ChatRequestDto { Message = "Show monthly Sales Order counts for 2025." },
            CancellationToken.None);

        L("");
        L("=== Live Chat Response ===");
        L($"SelectedTool={response.SelectedTool}");
        L($"PrivacySafeToolExecution={response.PrivacySafeToolExecution}");
        L($"CrmDataProvider={response.CrmDataProvider}");
        L($"ReplyLength={response.Reply?.Length ?? 0}");
        L($"ReplyPreview={Truncate(response.Reply, 1200)}");

        // Build FetchXML + execute validated plan against CRM.
        var meta = provider.GetRequiredService<ICrmMetadataService>();
        var soMeta = await meta.GetEntityAsync("salesorder", CancellationToken.None);
        Assert.NotNull(soMeta);
        var aggBuilder = provider.GetRequiredService<FetchXmlAggregateBuilder>();
        var built = aggBuilder.Build(shorthand.Plan!, soMeta!);
        L("");
        L("=== Built FetchXML (validated plan) ===");
        L($"BuildSuccess={built.Success} Error={built.Error}");
        if (built.Success && !string.IsNullOrWhiteSpace(built.RelativeRequestUri) &&
            built.RelativeRequestUri.Contains("fetchXml=", StringComparison.Ordinal))
        {
            var fetch = Uri.UnescapeDataString(built.RelativeRequestUri.Split("fetchXml=", 2)[1]);
            L("FetchXML:");
            L(Truncate(PrettyXml(fetch), 4000));
            var doc = XDocument.Parse(fetch);
            var ge = doc.Descendants("condition")
                .First(c => (string?)c.Attribute("operator") == "ge");
            var lt = doc.Descendants("condition")
                .First(c => (string?)c.Attribute("operator") == "lt");
            L($"createdon ge={ge.Attribute("value")?.Value}");
            L($"createdon lt={lt.Attribute("value")?.Value}");
            L($"FillMissingMonthBuckets={built.FillMissingMonthBuckets} BucketCount={built.CalendarMonthBuckets?.Count}");
        }

        var repo = provider.GetRequiredService<IGenericCrmRepository>();
        var crmResult = await repo.QueryAsync(shorthand.Plan!, CancellationToken.None);
        L("");
        L("=== Direct CRM Aggregate (validated plan) ===");
        L($"HttpOrProvider={crmResult.ProviderName}");
        L($"RelativeUriMarker={crmResult.RelativeRequestUri}");
        L($"Message={Truncate(crmResult.Message, 400)}");
        L($"RowCount={crmResult.Items.Count}");
        L("Monthly rows (sanitized keys only):");
        foreach (var row in crmResult.Items.Take(15))
        {
            var keys = string.Join(", ", row.Keys.OrderBy(k => k));
            L($"  keys=[{keys}]");
        }

        File.WriteAllText(ReportPath, sb.ToString());
        L($"Report written: {ReportPath}");

        Assert.True(shorthand.Success);
        Assert.False(string.IsNullOrWhiteSpace(response.Reply));
        Assert.True(
            crmResult.Items.Count > 0 ||
            !string.IsNullOrWhiteSpace(crmResult.RelativeRequestUri),
            $"CRM aggregate failed: {crmResult.Message}");
    }

    private static string? GetDevString(JsonElement? root, string section, string prop)
    {
        if (root is null)
        {
            return null;
        }

        if (!root.Value.TryGetProperty(section, out var s) || !s.TryGetProperty(prop, out var p))
        {
            return null;
        }

        return p.GetString();
    }

    private static string Truncate(string? s, int max) =>
        string.IsNullOrEmpty(s) ? "" : s.Length <= max ? s : s[..max] + "…";

    private static string PrettyXml(string xml)
    {
        try
        {
            return XDocument.Parse(xml).ToString();
        }
        catch
        {
            return xml;
        }
    }
}
