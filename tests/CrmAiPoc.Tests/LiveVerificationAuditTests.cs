using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using CrmAiPoc.Api.Configuration;
using CrmAiPoc.Api.Crm.Dynamics;
using CrmAiPoc.Api.Crm.Generic;
using CrmAiPoc.Api.Crm.Metadata;
using CrmAiPoc.Api.Crm.Query;
using CrmAiPoc.Api.Security;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CrmAiPoc.Tests;

/// <summary>
/// End-to-end live CRM verification for the hybrid-resolution / dynamic-joins audit.
/// Set CRM_LIVE_VERIFY=1. Writes sanitized report to %TEMP%\crm-poc-live-audit-report.txt.
/// Never logs customer field values.
/// </summary>
public sealed class LiveVerificationAuditTests
{
    private static readonly string ReportPath =
        Path.Combine(Path.GetTempPath(), "crm-poc-live-audit-report.txt");

    [Fact]
    public async Task Live_Audit_Scenarios_Or_NotExecuted()
    {
        var sb = new StringBuilder();
        void Line(string s) => sb.AppendLine(s);

        if (!string.Equals(Environment.GetEnvironmentVariable("CRM_LIVE_VERIFY"), "1", StringComparison.Ordinal))
        {
            Line("STATUS: Not Executed — CRM_LIVE_VERIFY!=1");
            File.WriteAllText(ReportPath, sb.ToString());
            Assert.True(true);
            return;
        }

        var baseUrl = Environment.GetEnvironmentVariable("Crm__BaseUrl")
                      ?? Environment.GetEnvironmentVariable("CRM_BASE_URL");
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            Line("STATUS: Not Executed — Crm__BaseUrl / CRM_BASE_URL not set");
            File.WriteAllText(ReportPath, sb.ToString());
            Assert.True(true);
            return;
        }

        // Mirror Development config: empty ApprovedRelationships = open within approved entities.
        var crmOptions = new CrmOptions
        {
            Provider = "Dynamics365",
            EnableLiveIntegration = true,
            BaseUrl = baseUrl,
            UseDefaultCredentials = true,
            ApprovedEntities = ["account", "contact", "salesorder", "lead"],
            ApprovedRelationships = [],
            MetadataCacheSeconds = 3600,
            BusinessTimeZoneId = "Africa/Cairo"
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
        services.AddSingleton<IGenericCrmQueryBuilder, GenericCrmQueryBuilder>();
        services.AddSingleton<FetchXmlAggregateBuilder>();
        services.AddSingleton<ICrmAuthorizationService, CrmAuthorizationService>();
        services.AddSingleton<IGenericCrmRepository, DynamicsGenericCrmRepository>();

        await using var provider = services.BuildServiceProvider();
        var connectivity = provider.GetRequiredService<DynamicsConnectivityService>();
        var who = await connectivity.WhoAmIAsync();
        if (!who.Success)
        {
            Line($"STATUS: Not Executed — WhoAmI failed: {Sanitize(who.Message)}");
            File.WriteAllText(ReportPath, sb.ToString());
            Assert.True(true);
            return;
        }

        Line("STATUS: Executed");
        Line($"WhoAmI: Success=true UserIdPresent={!string.IsNullOrWhiteSpace(who.UserId?.ToString())}");
        Line($"BaseUrlHost: {new Uri(baseUrl).Host}");
        Line($"ApprovedRelationshipsAllowlist: empty (open within approved entities)");

        var metadata = provider.GetRequiredService<ICrmMetadataService>();
        await metadata.EnsureApprovedEntitiesLoadedAsync();
        var soMeta = await metadata.GetEntityAsync("salesorder");
        var accountMeta = await metadata.GetEntityAsync("account");
        Assert.NotNull(soMeta);
        Assert.NotNull(accountMeta);

        Line("");
        Line("=== Relationship discovery (salesorder) ===");
        var soRels = soMeta!.Relationships.Where(r => r.IsApproved).ToList();
        Line($"ApprovedRelationshipCount={soRels.Count}");
        foreach (var r in soRels.OrderBy(r => r.SchemaName))
        {
            Line(
                $"  {r.SchemaName} kind={r.Kind} " +
                $"{r.ReferencingEntity}.{r.ReferencingAttribute} -> {r.ReferencedEntity}.{r.ReferencedAttribute}");
        }

        var path = CrmRelationshipDiscovery.FindUniquePath(soMeta, accountMeta!);
        Line($"SO->Account unique path: Success={path.Success} Ambiguous={path.Ambiguous}");
        if (path.Ambiguous)
        {
            Line($"  Candidates(SchemaNames): {string.Join(", ", path.Candidates.Select(c => c.SchemaName))}");
        }

        var builder = provider.GetRequiredService<IGenericCrmQueryBuilder>();
        CrmEntityMetadata? Get(string n) =>
            metadata.GetEntityAsync(n).GetAwaiter().GetResult();

        var repo = provider.GetRequiredService<IGenericCrmRepository>();

        // 1) Latest 5 sales orders
        await RunScenario(
            Line, builder, repo, Get, soMeta,
            "1_Latest5SalesOrders",
            new CrmQueryPlan
            {
                Entity = "salesorder",
                Limit = 5,
                Sort = [new CrmQuerySort { Field = "createdon", Direction = CrmSortDirection.Desc }]
            });

        // 2) Sales orders grouped by status (statuscode)
        await RunScenario(
            Line, builder, repo, Get, soMeta,
            "2_OrdersGroupedByStatus",
            new CrmQueryPlan
            {
                Entity = "salesorder",
                Operation = CrmQueryOperation.Aggregate,
                Aggregates =
                [
                    new CrmAggregateSpec { Field = "salesorderid", Function = CrmAggregateFunction.Count, Alias = "ordercount" }
                ],
                GroupBy = [new CrmGroupBySpec { Field = "statuscode", Alias = "statuscode" }],
                Limit = 20
            },
            isAggregate: true,
            aggregateBuilder: provider.GetRequiredService<FetchXmlAggregateBuilder>());

        // 3) Last six months order counts (year + month group-by)
        await RunScenario(
            Line, builder, repo, Get, soMeta,
            "3_LastSixMonthsOrderCounts",
            new CrmQueryPlan
            {
                Entity = "salesorder",
                Operation = CrmQueryOperation.Aggregate,
                Aggregates =
                [
                    new CrmAggregateSpec { Field = "salesorderid", Function = CrmAggregateFunction.Count, Alias = "ordercount" }
                ],
                RelativeDate = new CrmRelativeDateFilter
                {
                    Field = "createdon",
                    Token = RelativeDateToken.Last6CalendarMonths
                },
                GroupBy =
                [
                    new CrmGroupBySpec { Field = "createdon", Alias = "yr", DateGrouping = CrmDateGrouping.Year },
                    new CrmGroupBySpec { Field = "createdon", Alias = "mo", DateGrouping = CrmDateGrouping.Month }
                ],
                Limit = 12
            },
            isAggregate: true,
            aggregateBuilder: provider.GetRequiredService<FetchXmlAggregateBuilder>());

        // 4) Sales orders with customer account names (explicit SchemaName — ambiguous otherwise)
        await RunScenario(
            Line, builder, repo, Get, soMeta,
            "4_OrdersWithCustomerAccountNames",
            new CrmQueryPlan
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
                        Select = ["name"],
                        JoinType = CrmLinkJoinType.Outer
                    }
                ]
            });

        // 5) Related account filtering (inner join + name contains — value is non-PII probe letter)
        await RunScenario(
            Line, builder, repo, Get, soMeta,
            "5_RelatedAccountFilter",
            new CrmQueryPlan
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

        // 6) Multi-hop SO → Account → Contact
        await RunScenario(
            Line, builder, repo, Get, soMeta,
            "6_MultiHop_SO_Account_Contact",
            new CrmQueryPlan
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

        // 7) Account → Contact single hop
        await RunScenario(
            Line, builder, repo, Get, accountMeta!,
            "7_Account_To_Contact",
            new CrmQueryPlan
            {
                Entity = "account",
                Limit = 5,
                Links =
                [
                    new CrmLinkSpec
                    {
                        Entity = "contact",
                        Relationship = "contact_customer_accounts",
                        Alias = "ct",
                        Select = ["fullname"],
                        JoinType = CrmLinkJoinType.Outer
                    }
                ]
            });

        var aggBuilder = provider.GetRequiredService<FetchXmlAggregateBuilder>();
        var tz = BusinessTimeZone.Resolve(crmOptions.BusinessTimeZoneId);
        var utcNow = DateTimeOffset.UtcNow;

        // 8A) Monthly counts Jan–Jun 2025
        var rangeA = DateRangeResolver.Resolve(
            new CrmDateRangeExpression
            {
                Kind = CrmDateRangeKind.CalendarMonthRange,
                Year = 2025,
                MonthStart = 1,
                MonthEnd = 6
            },
            utcNow,
            tz);
        Line("");
        Line($"=== Date expression 8A === kind=calendar_month_range resolved=[{rangeA.StartUtc:u} .. {rangeA.EndUtcExclusive:u}) buckets={rangeA.CalendarMonthBuckets?.Count}");
        await RunScenario(
            Line, builder, repo, Get, soMeta,
            "8A_MonthlyCounts_JanJun2025",
            new CrmQueryPlan
            {
                Entity = "salesorder",
                Operation = CrmQueryOperation.Aggregate,
                Aggregates =
                [
                    new CrmAggregateSpec { Field = "salesorderid", Function = CrmAggregateFunction.Count, Alias = "ordercount" }
                ],
                RelativeDate = new CrmRelativeDateFilter
                {
                    Field = "createdon",
                    Expression = new CrmDateRangeExpression
                    {
                        Kind = CrmDateRangeKind.CalendarMonthRange,
                        Year = 2025,
                        MonthStart = 1,
                        MonthEnd = 6
                    }
                },
                GroupBy =
                [
                    new CrmGroupBySpec { Field = "createdon", Alias = "yr", DateGrouping = CrmDateGrouping.Year },
                    new CrmGroupBySpec { Field = "createdon", Alias = "mo", DateGrouping = CrmDateGrouping.Month }
                ],
                Limit = 12
            },
            isAggregate: true,
            aggregateBuilder: aggBuilder);

        // 8C) Last 8 calendar months
        var rangeC = DateRangeResolver.Resolve(
            new CrmDateRangeExpression { Kind = CrmDateRangeKind.LastNCalendarMonths, N = 8 },
            utcNow,
            tz);
        Line("");
        Line($"=== Date expression 8C === kind=last_n_calendar_months n=8 resolved=[{rangeC.StartUtc:u} .. {rangeC.EndUtcExclusive:u}) buckets={rangeC.CalendarMonthBuckets?.Count}");
        await RunScenario(
            Line, builder, repo, Get, soMeta,
            "8C_MonthlyCounts_Last8Months",
            new CrmQueryPlan
            {
                Entity = "salesorder",
                Operation = CrmQueryOperation.Aggregate,
                Aggregates =
                [
                    new CrmAggregateSpec { Field = "salesorderid", Function = CrmAggregateFunction.Count, Alias = "ordercount" }
                ],
                RelativeDate = new CrmRelativeDateFilter
                {
                    Field = "createdon",
                    Expression = new CrmDateRangeExpression { Kind = CrmDateRangeKind.LastNCalendarMonths, N = 8 }
                },
                GroupBy =
                [
                    new CrmGroupBySpec { Field = "createdon", Alias = "yr", DateGrouping = CrmDateGrouping.Year },
                    new CrmGroupBySpec { Field = "createdon", Alias = "mo", DateGrouping = CrmDateGrouping.Month }
                ],
                Limit = 12
            },
            isAggregate: true,
            aggregateBuilder: aggBuilder);

        // 8D) Top 10 this year + customer account
        var rangeD = DateRangeResolver.Resolve(
            new CrmDateRangeExpression { Kind = CrmDateRangeKind.ThisCalendarYear },
            utcNow,
            tz);
        Line("");
        Line($"=== Date expression 8D === kind=this_calendar_year (YTD) resolved=[{rangeD.StartUtc:u} .. {rangeD.EndUtcExclusive:u})");
        await RunScenario(
            Line, builder, repo, Get, soMeta,
            "8D_Top10_ThisYear_WithCustomerAccount",
            new CrmQueryPlan
            {
                Entity = "salesorder",
                Operation = CrmQueryOperation.List,
                Limit = 10,
                Sort = [new CrmQuerySort { Field = "totalamount", Direction = CrmSortDirection.Desc }],
                RelativeDate = new CrmRelativeDateFilter
                {
                    Field = "createdon",
                    Expression = new CrmDateRangeExpression { Kind = CrmDateRangeKind.ThisCalendarYear }
                },
                Links =
                [
                    new CrmLinkSpec
                    {
                        Entity = "account",
                        Relationship = "te_account_salesorder_customeraccountid",
                        Alias = "ca",
                        Select = ["name"],
                        JoinType = CrmLinkJoinType.Outer
                    }
                ]
            });

        // 8E) Absolute historical range
        var rangeE = DateRangeResolver.Resolve(
            new CrmDateRangeExpression
            {
                Kind = CrmDateRangeKind.Absolute,
                AbsoluteStartUtc = new DateTimeOffset(2024, 3, 15, 0, 0, 0, TimeSpan.FromHours(2)).ToUniversalTime(),
                AbsoluteEndExclusiveUtc = new DateTimeOffset(2024, 9, 21, 0, 0, 0, TimeSpan.FromHours(2)).ToUniversalTime()
            },
            utcNow,
            tz);
        Line("");
        Line($"=== Date expression 8E === kind=absolute resolved=[{rangeE.StartUtc:u} .. {rangeE.EndUtcExclusive:u})");
        await RunScenario(
            Line, builder, repo, Get, soMeta,
            "8E_List_Absolute_Mar15_Sep20_2024",
            new CrmQueryPlan
            {
                Entity = "salesorder",
                Operation = CrmQueryOperation.List,
                Limit = 25,
                RelativeDate = new CrmRelativeDateFilter
                {
                    Field = "createdon",
                    Expression = new CrmDateRangeExpression
                    {
                        Kind = CrmDateRangeKind.Absolute,
                        AbsoluteStartUtc = rangeE.StartUtc,
                        AbsoluteEndExclusiveUtc = rangeE.EndUtcExclusive
                    }
                }
            });

        // 8F) Q2 2025 count
        var rangeF = DateRangeResolver.Resolve(
            new CrmDateRangeExpression { Kind = CrmDateRangeKind.CalendarQuarter, Year = 2025, Quarter = 2 },
            utcNow,
            tz);
        Line("");
        Line($"=== Date expression 8F === kind=calendar_quarter Q2 2025 resolved=[{rangeF.StartUtc:u} .. {rangeF.EndUtcExclusive:u})");
        await RunScenario(
            Line, builder, repo, Get, soMeta,
            "8F_Count_Q2_2025",
            new CrmQueryPlan
            {
                Entity = "salesorder",
                Operation = CrmQueryOperation.Count,
                RelativeDate = new CrmRelativeDateFilter
                {
                    Field = "createdon",
                    Expression = new CrmDateRangeExpression
                    {
                        Kind = CrmDateRangeKind.CalendarQuarter,
                        Year = 2025,
                        Quarter = 2
                    }
                }
            });

        // 8G) Last 45 days
        var rangeG = DateRangeResolver.Resolve(
            new CrmDateRangeExpression { Kind = CrmDateRangeKind.LastNDays, N = 45 },
            utcNow,
            tz);
        Line("");
        Line($"=== Date expression 8G === kind=last_n_days n=45 resolved=[{rangeG.StartUtc:u} .. {rangeG.EndUtcExclusive:u})");
        await RunScenario(
            Line, builder, repo, Get, soMeta,
            "8G_List_Last45Days",
            new CrmQueryPlan
            {
                Entity = "salesorder",
                Operation = CrmQueryOperation.List,
                Limit = 20,
                RelativeDate = new CrmRelativeDateFilter
                {
                    Field = "createdon",
                    Expression = new CrmDateRangeExpression { Kind = CrmDateRangeKind.LastNDays, N = 45 }
                }
            });

        File.WriteAllText(ReportPath, sb.ToString());
        Assert.True(File.Exists(ReportPath));
    }

    private static async Task RunScenario(
        Action<string> line,
        IGenericCrmQueryBuilder builder,
        IGenericCrmRepository repo,
        Func<string, CrmEntityMetadata?> getEntity,
        CrmEntityMetadata root,
        string name,
        CrmQueryPlan plan,
        bool isAggregate = false,
        FetchXmlAggregateBuilder? aggregateBuilder = null)
    {
        line("");
        line($"=== Scenario {name} ===");

        if (!isAggregate)
        {
            var built = builder.Build(plan, root, getEntity: getEntity);
            line($"BuildSuccess={built.Success}");
            if (!built.Success)
            {
                line($"BuildError={Sanitize(built.Error)}");
                return;
            }

            line($"FetchXmlSanitized:\n{SanitizeFetchXml(built.FetchXml)}");
            line($"LinkColumns=[{string.Join(", ", built.LinkColumns.Select(c => c.Key))}]");
        }
        else if (aggregateBuilder is not null)
        {
            var built = aggregateBuilder.Build(plan, root);
            line($"BuildSuccess={built.Success}");
            if (!built.Success)
            {
                line($"BuildError={Sanitize(built.Error)}");
                return;
            }

            line($"FetchXmlSanitized:\n{SanitizeFetchXml(built.FetchXml)}");
        }
        else
        {
            line("BuildNote=aggregate routed via FetchXmlAggregateBuilder inside repository");
        }

        try
        {
            var result = await repo.QueryAsync(plan);
            line($"ExecStatus={(string.IsNullOrWhiteSpace(result.Message) || result.Items.Count >= 0 ? "OK" : "WARN")}");
            line($"Provider={result.ProviderName} Entity={result.Entity} Op={result.Operation}");
            line($"RowCount={result.Items.Count} Count={result.Count} CountMayBeCapped={result.CountMayBeCapped}");
            line($"Columns=[{string.Join(", ", result.Columns.Select(c => c.Key))}]");
            line($"RelativeUriSanitized={SanitizeUri(result.RelativeRequestUri)}");
            if (!string.IsNullOrWhiteSpace(result.Message))
            {
                line($"Message={Sanitize(result.Message)}");
            }
        }
        catch (Exception ex)
        {
            line($"ExecStatus=FAILED ExceptionType={ex.GetType().Name} Message={Sanitize(ex.Message)}");
        }
    }

    private static string SanitizeFetchXml(string? fetchXml)
    {
        if (string.IsNullOrWhiteSpace(fetchXml))
        {
            return "(empty)";
        }

        try
        {
            var doc = XDocument.Parse(fetchXml);
            foreach (var attr in doc.Descendants().Attributes().Where(a => a.Name.LocalName == "value"))
            {
                // Keep structure; redact literal values that could be customer data.
                if (!string.IsNullOrEmpty(attr.Value) &&
                    !Regex.IsMatch(
                        attr.Value,
                        @"^(true|false|\d{4}-\d{2}-\d{2}(T[\d:\.]+Z)?|Active|Inactive|[0-6]|a)$",
                        RegexOptions.IgnoreCase))
                {
                    attr.Value = "***";
                }
            }

            return doc.ToString(SaveOptions.DisableFormatting);
        }
        catch
        {
            return Regex.Replace(fetchXml, @"value=""[^""]*""", "value=\"***\"");
        }
    }

    private static string SanitizeUri(string? uri)
    {
        if (string.IsNullOrWhiteSpace(uri))
        {
            return "(empty)";
        }

        // Keep entity set + fetchXml presence; strip encoded payload body length only.
        var fetchIdx = uri.IndexOf("fetchXml=", StringComparison.OrdinalIgnoreCase);
        if (fetchIdx < 0)
        {
            return uri.Length > 120 ? uri[..120] + "…" : uri;
        }

        return uri[..(fetchIdx + "fetchXml=".Length)] + $"<encoded len={uri.Length - fetchIdx - 9}>";
    }

    private static string Sanitize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        // Strip GUID-looking tokens and long numeric runs.
        var s = Regex.Replace(text, @"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}", "{guid}");
        s = Regex.Replace(s, @"\b\d{6,}\b", "{n}");
        return s.Length > 400 ? s[..400] + "…" : s;
    }
}
