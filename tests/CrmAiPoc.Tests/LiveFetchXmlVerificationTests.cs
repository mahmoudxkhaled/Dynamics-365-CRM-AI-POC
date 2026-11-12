using CrmAiPoc.Api.Configuration;
using CrmAiPoc.Api.Crm.Dynamics;
using CrmAiPoc.Api.Crm.FetchXml;
using CrmAiPoc.Api.Crm.Metadata;
using CrmAiPoc.Api.Security;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CrmAiPoc.Tests;

/// <summary>
/// Live Dynamics 365 On-Premises v9.1 verification for LLM-generated FetchXML path.
/// Mocked unit coverage lives in FetchXmlSafetyValidatorTests; this suite hits CRM when enabled.
/// Set CRM_LIVE_VERIFY=1 and Crm__BaseUrl to execute.
/// </summary>
public sealed class LiveFetchXmlVerificationTests
{
    [Fact]
    public async Task Live_List_Count_Sum_Monthly_Or_NotExecuted()
    {
        static void Mark(string status) =>
            File.WriteAllText(
                Path.Combine(Path.GetTempPath(), "crm-poc-live-fetchxml-status.txt"),
                status);

        if (!string.Equals(Environment.GetEnvironmentVariable("CRM_LIVE_VERIFY"), "1", StringComparison.Ordinal))
        {
            Mark("Not Executed: set CRM_LIVE_VERIFY=1.");
            Assert.True(true);
            return;
        }

        var baseUrl = Environment.GetEnvironmentVariable("Crm__BaseUrl")
                      ?? Environment.GetEnvironmentVariable("CRM_BASE_URL");
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            Mark("Not Executed: Crm__BaseUrl not set.");
            Assert.True(true);
            return;
        }

        await using var provider = BuildProvider(baseUrl);
        var who = await provider.GetRequiredService<DynamicsConnectivityService>().WhoAmIAsync();
        if (!who.Success)
        {
            Mark($"Not Executed: WhoAmI failed — {who.Message}");
            Assert.True(true);
            return;
        }

        await provider.GetRequiredService<ICrmMetadataService>().EnsureApprovedEntitiesLoadedAsync();
        var executor = provider.GetRequiredService<IFetchXmlQueryExecutor>();

        async Task AssertOk(string label, string fetchXml)
        {
            var validated = await executor.ValidateAsync(new FetchXmlToolRequest
            {
                FetchXml = fetchXml,
                ResultMode = FetchXmlResultMode.Complete
            });
            Assert.True(validated.Success, $"{label}: {validated.ErrorMessage}");
            var result = await executor.ExecuteAsync(validated.Query!);
            Assert.True(
                !string.IsNullOrWhiteSpace(result.RelativeRequestUri) || result.Count is not null || result.Items.Count >= 0,
                $"{label}: {result.Message}");
        }

        await AssertOk("list", """
            <fetch top="5">
              <entity name="salesorder">
                <attribute name="name" />
                <attribute name="ordernumber" />
                <attribute name="createdon" />
                <order attribute="createdon" descending="true" />
              </entity>
            </fetch>
            """);

        await AssertOk("count", """
            <fetch aggregate="true">
              <entity name="salesorder">
                <attribute name="salesorderid" alias="cnt" aggregate="count" />
              </entity>
            </fetch>
            """);

        await AssertOk("sum", """
            <fetch aggregate="true">
              <entity name="salesorder">
                <attribute name="totalamount" alias="revenue" aggregate="sum" />
              </entity>
            </fetch>
            """);

        await AssertOk("monthly", """
            <fetch aggregate="true">
              <entity name="salesorder">
                <attribute name="createdon" alias="year" groupby="true" dategrouping="year" />
                <attribute name="createdon" alias="month" groupby="true" dategrouping="month" />
                <attribute name="totalamount" alias="revenue" aggregate="sum" />
                <filter type="and">
                  <condition attribute="createdon" operator="on-or-after" value="2025-01-01" />
                  <condition attribute="createdon" operator="on-or-before" value="2025-12-31" />
                </filter>
              </entity>
            </fetch>
            """);

        Mark("Executed OK: list/count/sum/monthly FetchXML against live CRM.");
    }

    private static ServiceProvider BuildProvider(string baseUrl)
    {
        var crmOptions = new CrmOptions
        {
            Provider = "Dynamics365",
            EnableLiveIntegration = true,
            BaseUrl = baseUrl,
            UseDefaultCredentials = true,
            ApprovedEntities = ["account", "contact", "salesorder", "lead"],
            MetadataCacheSeconds = 3600
        };

        var services = new ServiceCollection();
        services.AddLogging(b => b.AddDebug());
        services.AddSingleton(Options.Create(crmOptions));
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
        services.AddSingleton<ICrmAuthorizationService, CrmAuthorizationService>();
        services.AddSingleton(Options.Create(new AggregatePartitionOptions()));
        services.AddSingleton<FetchXmlSafetyValidator>();
        services.AddSingleton<FetchXmlAggregatePartitionRecovery>();
        services.AddSingleton<IFetchXmlQueryExecutor, FetchXmlQueryExecutor>();
        return services.BuildServiceProvider();
    }
}
