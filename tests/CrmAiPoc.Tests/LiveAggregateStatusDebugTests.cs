using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using CrmAiPoc.Api.Ai;
using CrmAiPoc.Api.Configuration;
using CrmAiPoc.Api.Crm.Dynamics;
using CrmAiPoc.Api.Crm.Generic;
using CrmAiPoc.Api.Crm.Metadata;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CrmAiPoc.Tests;

/// <summary>
/// Isolates live Dynamics aggregate failures. Set CRM_LIVE_VERIFY=1.
/// Writes sanitized diagnostics to %TEMP%\crm-poc-aggregate-debug.txt — no customer rows.
/// </summary>
public sealed class LiveAggregateStatusDebugTests
{
    private static readonly string ReportPath =
        Path.Combine(Path.GetTempPath(), "crm-poc-aggregate-debug.txt");

    [Fact]
    public async Task Capture_Crm_Error_For_Status_GroupBy()
    {
        var sb = new StringBuilder();
        void L(string s) => sb.AppendLine(s);

        if (!string.Equals(Environment.GetEnvironmentVariable("CRM_LIVE_VERIFY"), "1", StringComparison.Ordinal))
        {
            L("Not Executed: CRM_LIVE_VERIFY!=1");
            File.WriteAllText(ReportPath, sb.ToString());
            Assert.True(true);
            return;
        }

        var baseUrl = Environment.GetEnvironmentVariable("Crm__BaseUrl")
                      ?? Environment.GetEnvironmentVariable("CRM_BASE_URL");
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            L("Not Executed: BaseUrl missing");
            File.WriteAllText(ReportPath, sb.ToString());
            Assert.True(true);
            return;
        }

        // --- Step 1: intent / plan trace (no Groq) ---
        const string en = "How many sales orders do we have by status?";
        var crmOptions = new CrmOptions
        {
            Provider = "Dynamics365",
            EnableLiveIntegration = true,
            BaseUrl = baseUrl,
            UseDefaultCredentials = true,
            ApprovedEntities = ["account", "contact", "salesorder", "lead"],
            ApprovedRelationships = [],
            EntityAliases = new Dictionary<string, List<string>>
            {
                ["salesorder"] = ["sales order", "sales orders", "orders"]
            },
            BusinessTimeZoneId = "Africa/Cairo",
            MetadataCacheSeconds = 3600
        };
        var options = Options.Create(crmOptions);
        var policy = new EntityApprovalPolicy(options);

        foreach (var msg in new[] { en })
        {
            L($"=== Intent: {msg} ===");
            L($"DetectEntity={CrmQueryIntentHelper.DetectEntityLogicalName(msg, policy)}");
            // Stubbed AI aggregate plan — backend no longer invents this from keywords.
            var stubArgs = new Dictionary<string, object?>
            {
                ["entity"] = "salesorder",
                ["operation"] = "aggregate",
                ["aggregatesJson"] = """[{"function":"count","field":"salesorderid","alias":"record_count"}]""",
                ["groupByJson"] = """[{"field":"statuscode","alias":"statuscode"}]"""
            };
            foreach (var kv in stubArgs)
            {
                L($"  ARG {kv.Key}={kv.Value}");
            }

            var parsed = CrmQueryPlanParser.ParseFromToolArguments(stubArgs);
            L($"ParseSuccess={parsed.Success} Err={parsed.Error}");
            if (parsed.Plan is not null)
            {
                L($"Plan Op={parsed.Plan.Operation} Entity={parsed.Plan.Entity}");
                L($"Aggs={string.Join(';', parsed.Plan.Aggregates.Select(a => $"{a.Function}:{a.Field}:{a.Alias}"))}");
                L($"GroupBy={string.Join(';', parsed.Plan.GroupBy.Select(g => $"{g.Field}:{g.Alias}"))}");
            }
        }

        var services = new ServiceCollection();
        services.AddLogging(b => b.AddDebug());
        services.AddSingleton(options);
        services.AddSingleton<EntityApprovalPolicy>();
        services.AddSingleton<CrmMetadataRegistry>();
        services.AddHttpClient(DynamicsCrmHttp.HttpClientName, client =>
            {
                client.BaseAddress = new Uri(baseUrl.EndsWith('/') ? baseUrl : baseUrl + "/");
                client.Timeout = TimeSpan.FromSeconds(90);
            })
            .ConfigurePrimaryHttpMessageHandler(() => DynamicsCrmHttp.CreateHandler(crmOptions));
        services.AddSingleton<DynamicsCrmHttp>();
        services.AddSingleton<DynamicsConnectivityService>();
        services.AddSingleton<ICrmMetadataService, DynamicsCrmMetadataService>();
        services.AddSingleton<FetchXmlAggregateBuilder>();
        services.AddSingleton<IGenericCrmQueryBuilder, GenericCrmQueryBuilder>();

        await using var sp = services.BuildServiceProvider();
        var who = await sp.GetRequiredService<DynamicsConnectivityService>().WhoAmIAsync();
        if (!who.Success)
        {
            L($"Not Executed: WhoAmI failed {Sanitize(who.Message)}");
            File.WriteAllText(ReportPath, sb.ToString());
            Assert.True(true);
            return;
        }

        L($"WhoAmI=OK Host={new Uri(baseUrl).Host}");
        var metadata = sp.GetRequiredService<ICrmMetadataService>();
        await metadata.EnsureApprovedEntitiesLoadedAsync();
        var so = await metadata.GetEntityAsync("salesorder");
        Assert.NotNull(so);
        L($"salesorder EntitySet={so!.EntitySetName} PrimaryId={so.PrimaryIdAttribute}");
        L($"statuscode present={so.TryGetAttribute("statuscode", out var sc)} kind={sc?.Kind}");
        L($"statecode present={so.TryGetAttribute("statecode", out var st)} kind={st?.Kind}");

        var http = sp.GetRequiredService<DynamicsCrmHttp>();
        var listBuilder = sp.GetRequiredService<IGenericCrmQueryBuilder>();
        var aggBuilder = sp.GetRequiredService<FetchXmlAggregateBuilder>();

        // A) List top 1
        var listBuilt = listBuilder.Build(
            new CrmQueryPlan
            {
                Entity = "salesorder",
                Limit = 1,
                Sort = [new CrmQuerySort { Field = "createdon", Direction = CrmSortDirection.Desc }]
            },
            so);
        await Probe(L, http, "A_ListTop1", listBuilt.RelativeRequestUri!, listBuilt.FetchXml);

        // B) Simple count (operation=count path via GenericCrmQueryBuilder)
        var countBuilt = listBuilder.Build(
            new CrmQueryPlan { Entity = "salesorder", Operation = CrmQueryOperation.Count },
            so);
        await Probe(L, http, "B_SimpleCount", countBuilt.RelativeRequestUri!, countBuilt.FetchXml);

        // C) Aggregate countcolumn no groupby
        var cPlan = new CrmQueryPlan
        {
            Entity = "salesorder",
            Operation = CrmQueryOperation.Aggregate,
            Aggregates =
            [
                new CrmAggregateSpec
                {
                    Function = CrmAggregateFunction.Count,
                    Field = "salesorderid",
                    Alias = "record_count"
                }
            ]
        };
        var cBuilt = aggBuilder.Build(cPlan, so);
        await Probe(L, http, "C_AggCountNoGroup", cBuilt.RelativeRequestUri!, cBuilt.FetchXml);

        // D) COUNT groupby statuscode (failing case — builder output)
        var dPlan = new CrmQueryPlan
        {
            Entity = "salesorder",
            Operation = CrmQueryOperation.Aggregate,
            Aggregates =
            [
                new CrmAggregateSpec
                {
                    Function = CrmAggregateFunction.Count,
                    Field = "salesorderid",
                    Alias = "record_count"
                }
            ],
            GroupBy = [new CrmGroupBySpec { Field = "statuscode", Alias = "statuscode" }]
        };
        var dBuilt = aggBuilder.Build(dPlan, so);
        await Probe(L, http, "D_CountByStatuscode_Builder", dBuilt.RelativeRequestUri!, dBuilt.FetchXml);

        // E) Same with aggregate="count" instead of countcolumn
        var eXml =
            """<fetch mapping="logical" aggregate="true" distinct="false"><entity name="salesorder"><attribute name="salesorderid" alias="record_count" aggregate="count" /><attribute name="statuscode" groupby="true" alias="statuscode" /></entity></fetch>""";
        await Probe(L, http, "E_CountByStatus_aggregate_count", Encode(so.EntitySetName, eXml), eXml);

        // F) groupby statecode with countcolumn
        var fXml =
            """<fetch mapping="logical" aggregate="true" distinct="false"><entity name="salesorder"><attribute name="salesorderid" alias="record_count" aggregate="countcolumn" /><attribute name="statecode" groupby="true" alias="statecode" /></entity></fetch>""";
        await Probe(L, http, "F_CountByStatecode", Encode(so.EntitySetName, fXml), fXml);

        // G) groupby statuscode with count (not countcolumn) + distinct true
        var gXml =
            """<fetch mapping="logical" aggregate="true" distinct="true"><entity name="salesorder"><attribute name="salesorderid" alias="record_count" aggregate="count" /><attribute name="statuscode" groupby="true" alias="statuscode" /></entity></fetch>""";
        await Probe(L, http, "G_CountByStatus_distinct", Encode(so.EntitySetName, gXml), gXml);

        // H) Minimal status groupby without primary id — count statuscode itself
        var hXml =
            """<fetch mapping="logical" aggregate="true"><entity name="salesorder"><attribute name="statuscode" alias="record_count" aggregate="countcolumn" /><attribute name="statuscode" groupby="true" alias="statuscode" /></entity></fetch>""";
        await Probe(L, http, "H_CountStatuscode_Self", Encode(so.EntitySetName, hXml), hXml);

        File.WriteAllText(ReportPath, sb.ToString());
        Assert.True(File.Exists(ReportPath));
    }

    private static string Encode(string entitySet, string fetchXml) =>
        $"{entitySet}?fetchXml={Uri.EscapeDataString(fetchXml)}";

    private static async Task Probe(
        Action<string> line,
        DynamicsCrmHttp http,
        string name,
        string relativeUri,
        string? fetchXml)
    {
        line("");
        line($"=== Probe {name} ===");
        line($"FetchXml={SanitizeFetch(fetchXml)}");
        var result = await http.GetAsync(relativeUri, includeAnnotations: false);
        line($"HTTP={result.StatusCode} Success={result.IsSuccess} Category={result.ErrorCategory}");
        if (result.IsSuccess)
        {
            line($"BodySummary={SummarizeSuccessBody(result.Body)}");
        }
        else
        {
            line($"ErrorSanitized={SanitizeCrmError(result.Body)}");
        }
    }

    private static string SummarizeSuccessBody(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("value", out var values))
            {
                return $"len={body.Length} no-value";
            }

            var n = values.GetArrayLength();
            var keys = n > 0
                ? string.Join(',', values[0].EnumerateObject().Select(p => p.Name).Where(n => !n.Contains('@')))
                : "";
            return $"rows={n} keys=[{keys}]";
        }
        catch
        {
            return $"len={body.Length} parse-fail";
        }
    }

    private static string SanitizeCrmError(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return "(empty)";
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            string? code = null, message = null, type = null;
            if (root.TryGetProperty("error", out var err))
            {
                code = err.TryGetProperty("code", out var c) ? c.GetString() : null;
                message = err.TryGetProperty("message", out var m) ? m.GetString() : null;
                if (err.TryGetProperty("innererror", out var inner))
                {
                    type = inner.TryGetProperty("type", out var t) ? t.GetString() : null;
                    if (inner.TryGetProperty("message", out var im))
                    {
                        message ??= im.GetString();
                    }
                }
            }

            return $"code={code}; type={type}; message={Sanitize(message)}";
        }
        catch
        {
            return Sanitize(body);
        }
    }

    private static string SanitizeFetch(string? fetchXml)
    {
        if (string.IsNullOrWhiteSpace(fetchXml))
        {
            return "(empty)";
        }

        try
        {
            var doc = XDocument.Parse(fetchXml);
            foreach (var a in doc.Descendants().Attributes().Where(a => a.Name.LocalName == "value"))
            {
                a.Value = "***";
            }

            return doc.ToString(SaveOptions.DisableFormatting);
        }
        catch
        {
            return Regex.Replace(fetchXml, @"value=""[^""]*""", "value=\"***\"");
        }
    }

    private static string Sanitize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var s = Regex.Replace(text, @"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}", "{guid}");
        s = Regex.Replace(s, @"https?://[^\s""']+", "{url}");
        return s.Length > 500 ? s[..500] + "…" : s;
    }
}
