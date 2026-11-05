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
/// Live: salesorder → Customer Account → Primary Contact. Set CRM_LIVE_VERIFY=1.
/// </summary>
public sealed class LivePrimaryContactVerificationTests
{
    private static readonly string ReportPath =
        Path.Combine(Path.GetTempPath(), "crm-poc-primary-contact-verify.txt");

    [Fact]
    public async Task Live_TwoHop_PrimaryContact_Or_NotExecuted()
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
        services.AddSingleton<CrmRelationshipLabeler>();
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
        services.AddSingleton<IGenericCrmQueryBuilder, GenericCrmQueryBuilder>();
        services.AddSingleton<FetchXmlAggregateBuilder>();
        services.AddSingleton<ICrmAuthorizationService, CrmAuthorizationService>();
        services.AddSingleton<IGenericCrmRepository, DynamicsGenericCrmRepository>();

        await using var sp = services.BuildServiceProvider();
        var who = await sp.GetRequiredService<DynamicsConnectivityService>().WhoAmIAsync();
        if (!who.Success)
        {
            L($"Not Executed: WhoAmI failed");
            File.WriteAllText(ReportPath, sb.ToString());
            Assert.True(true);
            return;
        }

        var metadata = sp.GetRequiredService<ICrmMetadataService>();
        await metadata.EnsureApprovedEntitiesLoadedAsync();
        var account = await metadata.GetEntityAsync("account");
        Assert.NotNull(account);

        var hasPrimaryLookup = account!.TryGetAttribute("primarycontactid", out _);
        var primaryRels = account.Relationships
            .Where(r => r.IsApproved &&
                        (r.ReferencingAttribute?.Equals("primarycontactid", StringComparison.OrdinalIgnoreCase) == true ||
                         r.SchemaName.Equals("account_primary_contact", StringComparison.OrdinalIgnoreCase)))
            .ToList();

        L($"STATUS=Executed Host={new Uri(baseUrl).Host}");
        L($"primarycontactid_attribute={hasPrimaryLookup}");
        L($"primary_contact_relationships={string.Join(",", primaryRels.Select(r => r.SchemaName))}");

        if (!hasPrimaryLookup || primaryRels.Count == 0)
        {
            L("LIMITATION=Primary Contact relationship not available/approved in live metadata.");
            File.WriteAllText(ReportPath, sb.ToString());
            Assert.True(true); // not a hard fail — report limitation
            return;
        }

        var labeler = sp.GetRequiredService<CrmRelationshipLabeler>();
        var policy = sp.GetRequiredService<EntityApprovalPolicy>();
        var approved = await metadata.GetApprovedEntitiesAsync();
        const string msg =
            "Show the latest 5 Sales Orders with Customer Account and Primary Contact names.";

        var resolution = CrmEntityResolver.Resolve(msg, null, approved, policy, "en");
        L($"resolution={resolution.Status} entity={resolution.LogicalName}");
        Assert.Equal(CrmEntityResolutionStatus.Resolved, resolution.Status);
        Assert.Equal("salesorder", resolution.LogicalName);

        var plan = new CrmQueryPlan
        {
            Entity = "salesorder",
            Limit = 5,
            Sort = [new CrmQuerySort { Field = "createdon", Direction = CrmSortDirection.Desc }],
            Links =
            [
                new CrmLinkSpec
                {
                    Entity = "account",
                    Alias = "ca",
                    Select = ["name"],
                    Links = [new CrmLinkSpec { Entity = "contact", Alias = "pc", Select = ["fullname"] }]
                }
            ]
        };
        var attach = CrmRelationshipDiscovery.ResolveProposedLinks(
            plan, msg, approved, policy, labeler, "en");
        L($"attachClarification={attach.ClarificationMessage}");
        Assert.Null(attach.Pending);
        Assert.True(string.IsNullOrWhiteSpace(attach.ClarificationMessage), attach.ClarificationMessage);

        var so = await metadata.GetEntityAsync("salesorder");
        var builder = sp.GetRequiredService<IGenericCrmQueryBuilder>();
        CrmEntityMetadata? Get(string n) =>
            metadata.GetEntityAsync(n).GetAwaiter().GetResult();
        var built = builder.Build(attach.Plan, so!, getEntity: Get);
        L($"BuildSuccess={built.Success}");
        L($"FetchXml={SanitizeFetch(built.FetchXml)}");
        Assert.True(built.Success, built.Error);
        Assert.Contains("primarycontactid", built.FetchXml, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("parentcustomerid", built.FetchXml, StringComparison.OrdinalIgnoreCase);

        var repo = sp.GetRequiredService<IGenericCrmRepository>();
        var result = await repo.QueryAsync(attach.Plan);
        L($"RowCount={result.Items.Count}");
        L($"Columns=[{string.Join(", ", result.Columns.Select(c => c.Key))}]");
        L($"Message={Sanitize(result.Message)}");
        Assert.True(result.Items.Count <= 5);
        Assert.Contains(result.Columns, c => c.Key.Contains("name", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Columns, c => c.Key.Contains("fullname", StringComparison.OrdinalIgnoreCase) ||
                                              c.Key.Contains("pc.", StringComparison.OrdinalIgnoreCase));

        File.WriteAllText(ReportPath, sb.ToString());
    }

    private static string SanitizeFetch(string? xml)
    {
        if (string.IsNullOrWhiteSpace(xml)) return "(empty)";
        return Regex.Replace(xml, @"value=""[^""]*""", "value=\"***\"");
    }

    private static string Sanitize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        var s = Regex.Replace(text, @"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}", "{guid}");
        return s.Length > 300 ? s[..300] + "…" : s;
    }
}
