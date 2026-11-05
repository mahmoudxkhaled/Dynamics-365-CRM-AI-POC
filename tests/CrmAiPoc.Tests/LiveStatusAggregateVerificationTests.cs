using System.Text;
using System.Text.RegularExpressions;
using CrmAiPoc.Api.Configuration;
using CrmAiPoc.Api.Crm.Dynamics;
using CrmAiPoc.Api.Crm.Generic;
using CrmAiPoc.Api.Crm.Metadata;
using CrmAiPoc.Api.Security;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CrmAiPoc.Tests;

/// <summary>
/// Live verification that statuscode COUNT aggregate recovers from AggregateQueryRecordLimit.
/// Set CRM_LIVE_VERIFY=1. Report: %TEMP%\crm-poc-status-aggregate-verify.txt
/// </summary>
public sealed class LiveStatusAggregateVerificationTests
{
    private static readonly string ReportPath =
        Path.Combine(Path.GetTempPath(), "crm-poc-status-aggregate-verify.txt");

    [Fact]
    public async Task Live_Count_By_Statuscode_Succeeds_Or_NotExecuted()
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

        var crmOptions = new CrmOptions
        {
            Provider = "Dynamics365",
            EnableLiveIntegration = true,
            BaseUrl = baseUrl,
            UseDefaultCredentials = true,
            ApprovedEntities = ["account", "contact", "salesorder", "lead"],
            BusinessTimeZoneId = "Africa/Cairo",
            MetadataCacheSeconds = 3600
        };

        var services = new ServiceCollection();
        services.AddLogging(b => b.AddDebug());
        services.AddSingleton(Options.Create(crmOptions));
        services.AddPipelineDiagnosticsForTests(enabled: false);
        services.AddSingleton<EntityApprovalPolicy>();
        services.AddSingleton<CrmMetadataRegistry>();
        services.AddHttpClient(DynamicsCrmHttp.HttpClientName, client =>
            {
                client.BaseAddress = new Uri(baseUrl.EndsWith('/') ? baseUrl : baseUrl + "/");
                client.Timeout = TimeSpan.FromMinutes(5);
            })
            .ConfigurePrimaryHttpMessageHandler(() => DynamicsCrmHttp.CreateHandler(crmOptions));
        services.AddSingleton<DynamicsCrmHttp>();
        services.AddSingleton<DynamicsConnectivityService>();
        services.AddSingleton<ICrmMetadataService, DynamicsCrmMetadataService>();
        services.AddSingleton<IGenericCrmQueryBuilder, GenericCrmQueryBuilder>();
        services.AddSingleton<FetchXmlAggregateBuilder>();
        services.AddSingleton<ICrmAuthorizationService, CrmAuthorizationService>();
        services.AddSingleton<IGenericCrmRepository, DynamicsGenericCrmRepository>();

        await using var sp = services.BuildServiceProvider();
        var who = await sp.GetRequiredService<DynamicsConnectivityService>().WhoAmIAsync();
        if (!who.Success)
        {
            L($"Not Executed: WhoAmI failed {Sanitize(who.Message)}");
            File.WriteAllText(ReportPath, sb.ToString());
            Assert.True(true);
            return;
        }

        L("STATUS=Executed");
        L($"Host={new Uri(baseUrl).Host}");

        var repo = sp.GetRequiredService<IGenericCrmRepository>();
        var plan = new CrmQueryPlan
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

        var result = await repo.QueryAsync(plan);
        L($"Provider={result.ProviderName} Op={result.Operation}");
        L($"Message={Sanitize(result.Message)}");
        L($"RowCount={result.Items.Count}");
        L($"Columns=[{string.Join(", ", result.Columns.Select(c => c.Key))}]");
        L($"Summaries={string.Join(" | ", result.AppliedFilterSummary.Select(Sanitize))}");

        var total = 0L;
        foreach (var row in result.Items)
        {
            var status = row.TryGetValue("statuscode", out var s) ? Convert.ToString(s) : "?";
            var label = row.TryGetValue("statuscode_label", out var lb) ? Convert.ToString(lb) : "";
            var count = row.TryGetValue("record_count", out var c) ? Convert.ToInt64(c) : 0;
            total += count;
            L($"Group statuscode={status} label={label} count={count}");
        }

        L($"ExactTotalAcrossGroups={total}");
        File.WriteAllText(ReportPath, sb.ToString());

        Assert.True(string.IsNullOrWhiteSpace(result.Message) ||
                    !result.Message.Contains("0x8004E023", StringComparison.OrdinalIgnoreCase),
            result.Message);
        Assert.True(result.Items.Count >= 1, "Expected at least one status group");
        Assert.True(total > 0, "Expected positive total count across groups");
        Assert.Contains(result.Columns, c => c.Key.Equals("statuscode", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Columns, c => c.Key.Equals("record_count", StringComparison.OrdinalIgnoreCase));
    }

    private static string Sanitize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var s = Regex.Replace(text, @"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}", "{guid}");
        return s.Length > 300 ? s[..300] + "…" : s;
    }
}
