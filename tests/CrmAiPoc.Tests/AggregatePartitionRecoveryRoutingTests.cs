using System.Net;
using System.Text;
using CrmAiPoc.Api.Configuration;
using CrmAiPoc.Api.Crm.Dynamics;
using CrmAiPoc.Api.Crm.FetchXml;
using CrmAiPoc.Api.Crm.Generic;
using CrmAiPoc.Api.Crm.Metadata;
using CrmAiPoc.Api.Security;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CrmAiPoc.Tests;

/// <summary>
/// Verifies AggregateQueryRecordLimit routes to partition recovery (not Groq / not annotation retry).
/// </summary>
public sealed class AggregatePartitionRecoveryRoutingTests
{
    [Fact]
    public async Task AggregateLimit_Skips_Annotation_Retry_And_Recovers_Via_Partitions()
    {
        var handler = new RecordingHandler();
        var crmOptions = Options.Create(new CrmOptions
        {
            Provider = "Dynamics365",
            EnableLiveIntegration = true,
            BaseUrl = "https://crm.test/api/data/v9.1/",
            UseDefaultCredentials = true,
            BusinessTimeZoneId = "Africa/Cairo",
            ApprovedEntities = ["salesorder"]
        });

        var http = new DynamicsCrmHttp(
            new HandlerFactory(handler),
            crmOptions,
            NullLogger<DynamicsCrmHttp>.Instance);

        var recovery = new FetchXmlAggregatePartitionRecovery(
            http,
            crmOptions,
            Options.Create(new AggregatePartitionOptions
            {
                MaxPartitions = 50,
                MaxDepth = 3,
                TimeoutSeconds = 30,
                MaxDegreeOfParallelism = 1,
                MaxSingleYearPartitions = 100
            }),
            NullLogger<FetchXmlAggregatePartitionRecovery>.Instance);

        var executor = new FetchXmlQueryExecutor(
            new FetchXmlSafetyValidator(
                new EmptyMetadata(),
                new EntityApprovalPolicy(crmOptions),
                new CrmAuthorizationService(crmOptions, NullLogger<CrmAuthorizationService>.Instance, new EntityApprovalPolicy(crmOptions))),
            http,
            recovery,
            NullLogger<FetchXmlQueryExecutor>.Instance);

        var entity = new CrmEntityMetadata
        {
            LogicalName = "salesorder",
            EntitySetName = "salesorders",
            PrimaryIdAttribute = "salesorderid",
            PrimaryNameAttribute = "name",
            IsApprovedForAi = true,
            Attributes =
            [
                new CrmAttributeMetadata
                {
                    LogicalName = "createdon",
                    ODataPropertyName = "createdon",
                    Kind = CrmAttributeKind.DateTime,
                    IsValidForRead = true
                },
                new CrmAttributeMetadata
                {
                    LogicalName = "totalamount",
                    ODataPropertyName = "totalamount",
                    Kind = CrmAttributeKind.Money,
                    IsValidForRead = true
                },
                new CrmAttributeMetadata
                {
                    LogicalName = "salesorderid",
                    ODataPropertyName = "salesorderid",
                    Kind = CrmAttributeKind.Uniqueidentifier,
                    IsValidForRead = true
                }
            ]
        };

        const string fetchXml = """
            <fetch aggregate="true">
              <entity name="salesorder">
                <attribute name="totalamount" alias="revenue" aggregate="sum" />
                <attribute name="customerid" alias="customer_id" groupby="true" />
                <order alias="revenue" descending="true" />
              </entity>
            </fetch>
            """;

        var query = new ValidatedFetchXmlQuery
        {
            FetchXml = fetchXml,
            EntityLogicalName = "salesorder",
            EntitySetName = "salesorders",
            Entity = entity,
            RelativeRequestUri = "salesorders?fetchXml=" + Uri.EscapeDataString(fetchXml),
            IsAggregate = true,
            Top = null,
            Columns = [],
            ProjectedAliases = ["revenue", "customer_id"],
            ResultMode = FetchXmlResultMode.Complete
        };

        var result = await executor.ExecuteAsync(query, CancellationToken.None);

        Assert.False(string.IsNullOrWhiteSpace(result.RelativeRequestUri));
        Assert.False(result.IsRecoverableFetchXmlError);
        Assert.Equal(2, result.Items.Count);

        var total = result.Items.Sum(r => Convert.ToDecimal(r["revenue"]));
        Assert.Equal(150m, total);

        // Original aggregate URI must be attempted once with annotations and never re-issued without Prefer.
        static string DecodeFetch(string uri)
        {
            var idx = uri.IndexOf("fetchXml=", StringComparison.OrdinalIgnoreCase);
            return idx < 0
                ? Uri.UnescapeDataString(uri)
                : Uri.UnescapeDataString(uri[(idx + "fetchXml=".Length)..]);
        }

        var originalCalls = handler.Calls
            .Where(c =>
            {
                var xml = DecodeFetch(c.Path);
                return xml.Contains("aggregate=\"true\"", StringComparison.OrdinalIgnoreCase) &&
                       !xml.Contains("operator=\"ge\"", StringComparison.OrdinalIgnoreCase) &&
                       !xml.Contains("top=\"1\"", StringComparison.OrdinalIgnoreCase);
            })
            .ToList();
        Assert.Single(originalCalls);
        Assert.True(originalCalls[0].IncludeAnnotations);
        Assert.DoesNotContain(handler.Calls, c =>
            c.Path == originalCalls[0].Path && !c.IncludeAnnotations);
    }

    [Fact]
    public async Task Avg_Aggregate_Returns_Limitation_Not_False_Success()
    {
        var handler = new RecordingHandler { FailAllAggregates = true };
        var crmOptions = Options.Create(new CrmOptions
        {
            EnableLiveIntegration = true,
            BaseUrl = "https://crm.test/api/data/v9.1/",
            BusinessTimeZoneId = "Africa/Cairo"
        });
        var http = new DynamicsCrmHttp(
            new HandlerFactory(handler),
            crmOptions,
            NullLogger<DynamicsCrmHttp>.Instance);
        var recovery = new FetchXmlAggregatePartitionRecovery(
            http,
            crmOptions,
            Options.Create(new AggregatePartitionOptions()),
            NullLogger<FetchXmlAggregatePartitionRecovery>.Instance);

        var entity = new CrmEntityMetadata
        {
            LogicalName = "salesorder",
            EntitySetName = "salesorders",
            PrimaryIdAttribute = "salesorderid",
            PrimaryNameAttribute = "name",
            Attributes =
            [
                new()
                {
                    LogicalName = "createdon",
                    ODataPropertyName = "createdon",
                    Kind = CrmAttributeKind.DateTime,
                    IsValidForRead = true
                }
            ]
        };

        const string fetchXml = """
            <fetch aggregate="true">
              <entity name="salesorder">
                <attribute name="totalamount" alias="avg_amt" aggregate="avg" />
              </entity>
            </fetch>
            """;

        var query = new ValidatedFetchXmlQuery
        {
            FetchXml = fetchXml,
            EntityLogicalName = "salesorder",
            EntitySetName = "salesorders",
            Entity = entity,
            RelativeRequestUri = "salesorders?fetchXml=" + Uri.EscapeDataString(fetchXml),
            IsAggregate = true,
            ResultMode = FetchXmlResultMode.Complete
        };

        var executor = new FetchXmlQueryExecutor(
            new FetchXmlSafetyValidator(
                new EmptyMetadata(),
                new EntityApprovalPolicy(crmOptions),
                new CrmAuthorizationService(crmOptions, NullLogger<CrmAuthorizationService>.Instance, new EntityApprovalPolicy(crmOptions))),
            http,
            recovery,
            NullLogger<FetchXmlQueryExecutor>.Instance);

        var result = await executor.ExecuteAsync(query, CancellationToken.None);

        Assert.True(string.IsNullOrWhiteSpace(result.RelativeRequestUri));
        Assert.False(result.IsRecoverableFetchXmlError);
        Assert.Equal("AGGREGATE_RECORD_LIMIT_EXCEEDED", result.CorrectionErrorCode);
        Assert.Contains("cannot be partition-merged", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class EmptyMetadata : ICrmMetadataService
    {
        public Task EnsureApprovedEntitiesLoadedAsync(CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<IReadOnlyList<CrmEntityMetadata>> GetApprovedEntitiesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<CrmEntityMetadata>>([]);

        public Task<CrmEntityMetadata?> GetEntityAsync(string logicalName, CancellationToken cancellationToken = default) =>
            Task.FromResult<CrmEntityMetadata?>(null);

        public Task RefreshAsync(string? logicalName = null, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<AttributeOptionsResult> EnsureAttributeOptionsAsync(
            string entityLogicalName,
            string attributeLogicalName,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new AttributeOptionsResult { Error = "not found" });

        public string GetAiCatalogSummary() => string.Empty;
    }

    private sealed class HandlerFactory(RecordingHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
        {
            var client = new HttpClient(handler, disposeHandler: false)
            {
                BaseAddress = new Uri("https://crm.test/api/data/v9.1/")
            };
            return client;
        }
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<(string Path, bool IncludeAnnotations)> Calls { get; } = [];
        public bool FailAllAggregates { get; set; }
        private int _gePartitionCalls;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.PathAndQuery ?? request.RequestUri?.ToString() ?? string.Empty;
            // DynamicsCrmHttp may use absolute URI with BaseUrl
            var full = request.RequestUri?.ToString() ?? string.Empty;
            var includeAnnotations = request.Headers.TryGetValues("Prefer", out var prefs) &&
                                     prefs.Any(p => p.Contains("include-annotations", StringComparison.OrdinalIgnoreCase));
            Calls.Add((full, includeAnnotations));

            var decoded = Decode(full);
            var isOriginalAggregate =
                decoded.Contains("aggregate=\"true\"", StringComparison.OrdinalIgnoreCase) &&
                !decoded.Contains("operator=\"ge\"", StringComparison.OrdinalIgnoreCase) &&
                !decoded.Contains("top=\"1\"", StringComparison.OrdinalIgnoreCase);

            if (FailAllAggregates || isOriginalAggregate)
            {
                return Task.FromResult(Json(
                    HttpStatusCode.BadRequest,
                    """{"error":{"code":"0x8004E023","message":"AggregateQueryRecordLimit exceeded."}}"""));
            }

            if (decoded.Contains("top=\"1\"", StringComparison.OrdinalIgnoreCase))
            {
                var ascending = !decoded.Contains("descending=\"true\"", StringComparison.OrdinalIgnoreCase);
                var date = ascending ? "2024-03-01T00:00:00Z" : "2025-08-15T00:00:00Z";
                return Task.FromResult(Json(
                    HttpStatusCode.OK,
                    $$"""{"value":[{"createdon":"{{date}}"}]}"""));
            }

            // Partitioned aggregate success (sequential stub: first ge → 100, later → 50).
            if (decoded.Contains("operator=\"ge\"", StringComparison.OrdinalIgnoreCase))
            {
                var partitionOrdinal = Interlocked.Increment(ref _gePartitionCalls);
                var revenue = partitionOrdinal <= 1 ? 100m : 50m;
                return Task.FromResult(Json(
                    HttpStatusCode.OK,
                    $$"""{"value":[{"customer_id":"cust-1","revenue":{{revenue.ToString(System.Globalization.CultureInfo.InvariantCulture)}}},{"customer_id":"cust-2","revenue":0}]}"""));
            }

            return Task.FromResult(Json(HttpStatusCode.OK, """{"value":[]}"""));
        }

        private static string Decode(string uri)
        {
            try
            {
                var idx = uri.IndexOf("fetchXml=", StringComparison.OrdinalIgnoreCase);
                if (idx < 0)
                {
                    return Uri.UnescapeDataString(uri);
                }

                return Uri.UnescapeDataString(uri[(idx + "fetchXml=".Length)..]);
            }
            catch
            {
                return uri;
            }
        }

        private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
            new(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
    }
}
