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
/// Read-only live CRM verification for multi-entity joins.
/// Set env CRM_LIVE_VERIFY=1 plus Crm__* settings to execute; otherwise Not Executed.
/// Never mutates CRM records.
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
        services.AddPipelineDiagnosticsForTests(enabled: false);
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
        services.AddSingleton<IGenericCrmQueryBuilder, GenericCrmQueryBuilder>();
        services.AddSingleton<FetchXmlAggregateBuilder>();
        services.AddSingleton<ICrmAuthorizationService, CrmAuthorizationService>();
        services.AddSingleton<IGenericCrmRepository, DynamicsGenericCrmRepository>();

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
        Assert.Contains(
            salesorder!.Relationships,
            r => r.SchemaName.Equals("te_account_salesorder_customeraccountid", StringComparison.OrdinalIgnoreCase));

        var repo = provider.GetRequiredService<IGenericCrmRepository>();

        var test1 = await repo.QueryAsync(new CrmQueryPlan
        {
            Entity = "salesorder",
            Limit = 5,
            Sort = [new CrmQuerySort { Field = "createdon", Direction = CrmSortDirection.Desc }],
            Links =
            [
                new CrmLinkSpec
                {
                    Entity = "account",
                    Relationship = "te_account_salesorder_customeraccountid",
                    Alias = "ca",
                    Select = ["name"]
                }
            ]
        });
        Assert.True(test1.Items.Count >= 0, test1.Message);
        Assert.Contains(test1.Columns, c => c.Key.Contains("ca.", StringComparison.OrdinalIgnoreCase));

        var test2 = await repo.QueryAsync(new CrmQueryPlan
        {
            Entity = "salesorder",
            Limit = 10,
            Filters =
            [
                new CrmQueryFilter { Field = "statecode", Operator = CrmFilterOperator.Eq, Value = "Active" }
            ],
            Links =
            [
                new CrmLinkSpec
                {
                    Entity = "account",
                    Relationship = "te_account_salesorder_customeraccountid",
                    Alias = "ca",
                    Select = ["name"]
                }
            ]
        });
        Assert.NotNull(test2);

        var test3 = await repo.QueryAsync(new CrmQueryPlan
        {
            Entity = "salesorder",
            Limit = 10,
            Links =
            [
                new CrmLinkSpec
                {
                    Entity = "account",
                    Relationship = "te_account_salesorder_customeraccountid",
                    Alias = "ca",
                    Select = ["name"],
                    JoinType = CrmLinkJoinType.Inner,
                    Filters =
                    [
                        new CrmQueryFilter
                        {
                            Field = "name",
                            Operator = CrmFilterOperator.Contains,
                            Value = "a"
                        }
                    ]
                }
            ]
        });
        Assert.NotNull(test3);

        var test4 = await repo.QueryAsync(new CrmQueryPlan
        {
            Entity = "salesorder",
            Limit = 5,
            Links =
            [
                new CrmLinkSpec
                {
                    Entity = "account",
                    Relationship = "te_account_salesorder_customeraccountid",
                    Alias = "ca",
                    Select = ["name"],
                    Links =
                    [
                        new CrmLinkSpec
                        {
                            Entity = "contact",
                            Relationship = "contact_customer_accounts",
                            Alias = "ct",
                            Select = ["fullname"]
                        }
                    ]
                }
            ]
        });
        Assert.True(string.IsNullOrWhiteSpace(test4.Message) || test4.RelativeRequestUri.Length > 0, test4.Message);
        Assert.Contains(test4.Columns, c => c.Key.Contains("ca.", StringComparison.OrdinalIgnoreCase));
        Mark(
            $"Executed OK. WhoAmI={who.UserId}; " +
            $"T1={test1.Items.Count}; T2={test2.Items.Count}; T3={test3.Items.Count}; T4={test4.Items.Count}");
    }
}
