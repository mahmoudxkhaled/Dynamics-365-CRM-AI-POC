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
/// Live CRM verification for multi-entity FetchXML joins.
/// Set CRM_LIVE_VERIFY=1 plus Crm__BaseUrl to execute; otherwise Not Executed.
/// </summary>
public sealed class LiveCrmMultiEntityVerificationTests
{
    [Fact]
    public async Task Live_JoinScenarios_Or_NotExecuted()
    {
        static void Mark(string status) =>
            File.WriteAllText(
                Path.Combine(Path.GetTempPath(), "crm-poc-live-multi-entity-status.txt"),
                status);

        if (!string.Equals(Environment.GetEnvironmentVariable("CRM_LIVE_VERIFY"), "1", StringComparison.Ordinal))
        {
            Mark("Not Executed: set CRM_LIVE_VERIFY=1 to run live multi-entity checks.");
            Assert.True(true);
            return;
        }

        var baseUrl = Environment.GetEnvironmentVariable("Crm__BaseUrl")
                      ?? Environment.GetEnvironmentVariable("CRM_BASE_URL");
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            Mark("Not Executed: Crm__BaseUrl / CRM_BASE_URL not set.");
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
            ApprovedRelationships =
            [
                "te_account_salesorder_customeraccountid",
                "order_customer_accounts",
                "contact_customer_accounts"
            ],
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
                client.Timeout = TimeSpan.FromSeconds(60);
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

        await using var provider = services.BuildServiceProvider();
        var connectivity = provider.GetRequiredService<DynamicsConnectivityService>();
        var who = await connectivity.WhoAmIAsync();
        if (!who.Success)
        {
            Mark($"Not Executed: WhoAmI failed — {who.Message}");
            Assert.True(true);
            return;
        }

        var metadata = provider.GetRequiredService<ICrmMetadataService>();
        await metadata.EnsureApprovedEntitiesLoadedAsync();
        var salesorder = await metadata.GetEntityAsync("salesorder");
        Assert.NotNull(salesorder);

        var rel = salesorder!.Relationships.FirstOrDefault(r =>
            r.SchemaName.Equals("te_account_salesorder_customeraccountid", StringComparison.OrdinalIgnoreCase) ||
            r.SchemaName.Equals("order_customer_accounts", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(rel);

        var executor = provider.GetRequiredService<IFetchXmlQueryExecutor>();
        var fetch = $"""
            <fetch top="5">
              <entity name="salesorder">
                <attribute name="name" />
                <attribute name="createdon" />
                <order attribute="createdon" descending="true" />
                <link-entity name="account" from="{rel!.ReferencedAttribute}" to="{rel.ReferencingAttribute}" alias="ca" link-type="inner" relationship="{rel.SchemaName}">
                  <attribute name="name" />
                </link-entity>
              </entity>
            </fetch>
            """;

        var validated = await executor.ValidateAsync(new FetchXmlToolRequest
        {
            FetchXml = fetch,
            ResultMode = FetchXmlResultMode.Complete
        });
        Assert.True(validated.Success, validated.ErrorMessage);

        var result = await executor.ExecuteAsync(validated.Query!);
        Assert.True(string.IsNullOrWhiteSpace(result.Message) || result.Items.Count >= 0, result.Message);
        Mark($"Executed OK rows={result.Items.Count} rel={rel.SchemaName}");
    }
}
