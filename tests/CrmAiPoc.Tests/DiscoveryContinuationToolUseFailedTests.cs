using System.Net;
using CrmAiPoc.Api.Ai;
using CrmAiPoc.Api.Configuration;
using CrmAiPoc.Api.Crm.Dynamics;
using CrmAiPoc.Api.Crm.FetchXml;
using CrmAiPoc.Api.Crm.Generic;
using CrmAiPoc.Api.Crm.Metadata;
using CrmAiPoc.Api.Crm.Metadata.Discovery;
using CrmAiPoc.Api.Models;
using CrmAiPoc.Api.Security;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CrmAiPoc.Tests;

/// <summary>
/// Regression: after discover_crm_schema, Groq RequireAny can HTTP 400 with tool_use_failed.
/// Continuation may retry once with ToolMode=Auto; text-only Auto replies never execute CRM.
/// </summary>
public sealed class DiscoveryContinuationToolUseFailedTests
{
    private static readonly Dictionary<string, object?> DiscoverArgs = new()
    {
        ["entities"] = new[] { "salesorder" },
        ["concepts"] = new[] { "order total value" }
    };

    private static readonly Dictionary<string, object?> QueryArgs = new()
    {
        ["fetchXml"] =
            """
            <fetch top="5">
              <entity name="salesorder">
                <attribute name="name" />
                <attribute name="totalamount" />
                <order attribute="totalamount" descending="true" />
              </entity>
            </fetch>
            """,
        ["resultMode"] = "complete"
    };

    [Fact]
    public void Classifier_Detects_Tool_Use_Failed_Without_Tool_Call()
    {
        var ex = new HttpRequestException(
            "HTTP 400 invalid_request_error: tool_use_failed — Tool choice is required, but model did not call a tool",
            null,
            HttpStatusCode.BadRequest);

        Assert.Equal(AiProviderFailureKind.Http400, AiProviderFailureClassifier.Classify(ex));
        Assert.True(AiProviderFailureClassifier.IsToolUseFailedWithoutToolCall(ex));
    }

    [Fact]
    public void Classifier_Does_Not_Treat_Unrelated_Http400_As_Tool_Use_Failed()
    {
        var ex = new HttpRequestException(
            "HTTP 400 bad request: invalid model id",
            null,
            HttpStatusCode.BadRequest);

        Assert.Equal(AiProviderFailureKind.Http400, AiProviderFailureClassifier.Classify(ex));
        Assert.False(AiProviderFailureClassifier.IsToolUseFailedWithoutToolCall(ex));
    }

    [Fact]
    public async Task Discover_Then_ToolUseFailed_Then_Auto_QueryCrm_Succeeds()
    {
        var chat = new RecordingScriptedClient(
            DiscoverResponse(),
            new HttpRequestException(
                "400 invalid_request_error tool_use_failed: Tool choice is required, but model did not call a tool",
                null,
                HttpStatusCode.BadRequest),
            QueryResponse());

        var executor = new RecordingExecutor();
        var (service, store) = CreateService(chat, executor);

        var response = await service.ChatAsync(
            new ChatRequestDto { ConversationId = "disc-auto-1", Message = "Show biggest orders." },
            CancellationToken.None);

        Assert.Equal(3, chat.CallCount); // discover planning + RequireAny fail + Auto success
        Assert.Equal(1, executor.QueryCount);
        Assert.NotNull(response.StructuredData);
        Assert.True(string.IsNullOrWhiteSpace(response.Reply));
        Assert.Contains(ChatToolMode.RequireAny, chat.ObservedToolModes);
        Assert.Contains(ChatToolMode.Auto, chat.ObservedToolModes);
        Assert.True(chat.LastHadMatchingDiscoverToolPair);
        Assert.Equal(1, store.GetOrCreate("disc-auto-1").UserQuestionCount);
    }

    [Fact]
    public async Task Discover_Then_ToolUseFailed_Then_Auto_Text_Does_Not_Execute_Crm()
    {
        var chat = new RecordingScriptedClient(
            DiscoverResponse(),
            new HttpRequestException(
                "tool_use_failed: Tool choice is required, but model did not call a tool",
                null,
                HttpStatusCode.BadRequest),
            new ChatResponse([new ChatMessage(ChatRole.Assistant, "I think you want sales orders.")]));

        var executor = new RecordingExecutor();
        var (service, _) = CreateService(chat, executor);

        var response = await service.ChatAsync(
            new ChatRequestDto { ConversationId = "disc-auto-text", Message = "Show biggest orders." },
            CancellationToken.None);

        Assert.Equal(0, executor.QueryCount);
        Assert.Equal(0, executor.ValidateCount);
        Assert.Null(response.StructuredData);
        Assert.False(string.IsNullOrWhiteSpace(response.Reply));
        Assert.Contains(ChatToolMode.Auto, chat.ObservedToolModes);
    }

    [Fact]
    public async Task Discover_Then_Unrelated_Http400_Does_Not_Fallback_To_Auto()
    {
        var chat = new RecordingScriptedClient(
            DiscoverResponse(),
            new HttpRequestException(
                "HTTP 400 bad request: invalid_request — malformed JSON schema",
                null,
                HttpStatusCode.BadRequest));

        var executor = new RecordingExecutor();
        var (service, _) = CreateService(chat, executor);

        var response = await service.ChatAsync(
            new ChatRequestDto { ConversationId = "disc-400", Message = "Show biggest orders." },
            CancellationToken.None);

        Assert.Equal(2, chat.CallCount); // initial + one RequireAny continuation (no Auto)
        Assert.DoesNotContain(ChatToolMode.Auto, chat.ObservedToolModes);
        Assert.Equal(0, executor.QueryCount);
        Assert.Null(response.StructuredData);
        Assert.Contains("could not continue after schema discovery", response.Reply, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Discover_Continuation_Http429_Still_Retries_Then_Can_Query()
    {
        var chat = new RecordingScriptedClient(
            DiscoverResponse(),
            new HttpRequestException("429 rate limit high demand", null, HttpStatusCode.TooManyRequests),
            QueryResponse());

        var executor = new RecordingExecutor();
        var (service, _) = CreateService(chat, executor);

        var response = await service.ChatAsync(
            new ChatRequestDto { ConversationId = "disc-429", Message = "Show biggest orders." },
            CancellationToken.None);

        // GetResponseWithRetryAsync: attempt1 429 → retry attempt2 query success
        Assert.True(chat.CallCount >= 3);
        Assert.Equal(1, executor.QueryCount);
        Assert.NotNull(response.StructuredData);
        Assert.DoesNotContain(ChatToolMode.Auto, chat.ObservedToolModes);
    }

    [Fact]
    public async Task Discover_Then_Query_Still_Works_Without_Fallback()
    {
        var chat = new RecordingScriptedClient(DiscoverResponse(), QueryResponse());
        var executor = new RecordingExecutor();
        var (service, _) = CreateService(chat, executor);

        var response = await service.ChatAsync(
            new ChatRequestDto { ConversationId = "disc-ok", Message = "Show biggest orders." },
            CancellationToken.None);

        Assert.Equal(2, chat.CallCount);
        Assert.Equal(1, executor.QueryCount);
        Assert.NotNull(response.StructuredData);
        Assert.DoesNotContain(ChatToolMode.Auto, chat.ObservedToolModes);
    }

    [Fact]
    public async Task Tool_Use_Failed_Fallback_Is_Single_Attempt_Only()
    {
        var chat = new RecordingScriptedClient(
            DiscoverResponse(),
            new HttpRequestException(
                "tool_use_failed: Tool choice is required, but model did not call a tool",
                null,
                HttpStatusCode.BadRequest),
            new HttpRequestException(
                "tool_use_failed: Tool choice is required, but model did not call a tool",
                null,
                HttpStatusCode.BadRequest));

        var executor = new RecordingExecutor();
        var (service, _) = CreateService(chat, executor);

        var response = await service.ChatAsync(
            new ChatRequestDto { ConversationId = "disc-double-fail", Message = "Show biggest orders." },
            CancellationToken.None);

        Assert.Equal(3, chat.CallCount); // plan + RequireAny fail + one Auto fail
        Assert.Equal(1, chat.ObservedToolModes.Count(m => Equals(m, ChatToolMode.Auto)));
        Assert.Equal(0, executor.QueryCount);
        Assert.Null(response.StructuredData);
    }

    private static ChatResponse DiscoverResponse() =>
        new([
            new ChatMessage(ChatRole.Assistant, [
                new FunctionCallContent("d1", "discover_crm_schema", DiscoverArgs)
            ])
        ]);

    private static ChatResponse QueryResponse() =>
        new([
            new ChatMessage(ChatRole.Assistant, [
                new FunctionCallContent("q1", "query_crm", QueryArgs)
            ])
        ]);

    private static (ChatService Service, ConversationStore Store) CreateService(
        RecordingScriptedClient chat,
        RecordingExecutor executor)
    {
        var crmOptions = Options.Create(new CrmOptions
        {
            Provider = "Dynamics365",
            EnableLiveIntegration = false,
            ApprovedEntities = ["salesorder", "account", "contact"]
        });
        var aiOptions = Options.Create(new AiOptions
        {
            Provider = "Groq",
            MaxQueryPlanCorrectionRetries = 2,
            MaxSchemaDiscoveryCallsPerRequest = 2,
            ProviderTimeoutSeconds = 30
        });
        var approval = new EntityApprovalPolicy(crmOptions);
        var metadata = CreateSalesOrderMetadata();
        var discovery = new CrmSchemaDiscoveryService(
            metadata,
            approval,
            new CrmAuthorizationService(crmOptions, NullLogger<CrmAuthorizationService>.Instance, approval),
            NullLogger<CrmSchemaDiscoveryService>.Instance);
        var store = new ConversationStore();
        var http = new DynamicsCrmHttp(
            new StubHttp(),
            crmOptions,
            NullLogger<DynamicsCrmHttp>.Instance);
        var service = new ChatService(
            chat,
            new CrmAiTools(NullLogger<CrmAiTools>.Instance),
            store,
            new DynamicsConnectivityService(http, crmOptions, NullLogger<DynamicsConnectivityService>.Instance),
            executor,
            metadata,
            discovery,
            approval,
            crmOptions,
            aiOptions,
            NullLogger<ChatService>.Instance);
        return (service, store);
    }

    private static FakeMetadata CreateSalesOrderMetadata() =>
        new([
            new CrmEntityMetadata
            {
                LogicalName = "salesorder",
                EntitySetName = "salesorders",
                DisplayName = "Sales Order",
                PrimaryIdAttribute = "salesorderid",
                PrimaryNameAttribute = "name",
                IsApprovedForAi = true,
                Attributes =
                [
                    Attr("name", "Name", CrmAttributeKind.String),
                    Attr("totalamount", "Total Amount", CrmAttributeKind.Money, sortable: true),
                    Attr("createdon", "Created On", CrmAttributeKind.DateTime, sortable: true)
                ]
            }
        ]);

    private static CrmAttributeMetadata Attr(
        string name,
        string display,
        CrmAttributeKind kind,
        bool sortable = false) =>
        new()
        {
            LogicalName = name,
            ODataPropertyName = name,
            DisplayName = display,
            Kind = kind,
            IsValidForRead = true,
            IsFilterable = true,
            IsSortable = sortable,
            AllowedOperators = AttributeKindMapper.DefaultOperators(kind)
        };

    private sealed class RecordingScriptedClient : IChatClient
    {
        private readonly Queue<object> _script;
        public int CallCount { get; private set; }
        public List<ChatToolMode?> ObservedToolModes { get; } = [];
        public bool LastHadMatchingDiscoverToolPair { get; private set; }

        public RecordingScriptedClient(params object[] script) =>
            _script = new Queue<object>(script);

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            ObservedToolModes.Add(options?.ToolMode);

            var list = messages.ToList();
            var assistantCalls = list
                .Where(m => m.Role == ChatRole.Assistant)
                .SelectMany(m => m.Contents.OfType<FunctionCallContent>())
                .Where(c => c.Name.Equals("discover_crm_schema", StringComparison.OrdinalIgnoreCase))
                .ToList();
            var toolResults = list.SelectMany(m => m.Contents.OfType<FunctionResultContent>()).ToList();
            LastHadMatchingDiscoverToolPair = assistantCalls.Any(c =>
                toolResults.Any(r => string.Equals(r.CallId, c.CallId, StringComparison.Ordinal)));

            if (_script.Count == 0)
            {
                throw new InvalidOperationException("No scripted responses remaining.");
            }

            var next = _script.Dequeue();
            if (next is Exception ex)
            {
                throw ex;
            }

            return Task.FromResult((ChatResponse)next);
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class RecordingExecutor : IFetchXmlQueryExecutor
    {
        public int ValidateCount { get; private set; }
        public int QueryCount { get; private set; }

        public Task<FetchXmlValidationOutcome> ValidateAsync(
            FetchXmlToolRequest request,
            CancellationToken cancellationToken = default)
        {
            ValidateCount++;
            var entity = new CrmEntityMetadata
            {
                LogicalName = "salesorder",
                EntitySetName = "salesorders",
                DisplayName = "Sales Order",
                PrimaryIdAttribute = "salesorderid",
                PrimaryNameAttribute = "name",
                IsApprovedForAi = true
            };
            return Task.FromResult(FetchXmlValidationOutcome.Ok(new ValidatedFetchXmlQuery
            {
                FetchXml = request.FetchXml,
                EntityLogicalName = entity.LogicalName,
                EntitySetName = entity.EntitySetName,
                Entity = entity,
                RelativeRequestUri = "salesorders?fetchXml=x",
                Top = 5,
                ResultMode = request.ResultMode,
                Columns =
                [
                    new CrmTableColumn { Key = "name", Label = "Name", Kind = "string" },
                    new CrmTableColumn { Key = "totalamount", Label = "Total Amount", Kind = "money" }
                ]
            }));
        }

        public Task<GenericCrmQueryResult> ExecuteAsync(
            ValidatedFetchXmlQuery query,
            CancellationToken cancellationToken = default)
        {
            QueryCount++;
            return Task.FromResult(new GenericCrmQueryResult
            {
                Entity = query.EntityLogicalName,
                Operation = CrmQueryOperation.List,
                RelativeRequestUri = query.RelativeRequestUri,
                Items = [new Dictionary<string, object?> { ["name"] = "Demo", ["totalamount"] = 100m }],
                Columns = query.Columns,
                ProviderName = "Test",
                ResultMode = "complete"
            });
        }
    }

    private sealed class FakeMetadata(IReadOnlyList<CrmEntityMetadata> entities) : ICrmMetadataService
    {
        private readonly Dictionary<string, CrmEntityMetadata> _map =
            entities.ToDictionary(e => e.LogicalName, StringComparer.OrdinalIgnoreCase);

        public Task EnsureApprovedEntitiesLoadedAsync(CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<CrmEntityMetadata?> GetEntityAsync(string logicalName, CancellationToken cancellationToken = default) =>
            Task.FromResult(_map.TryGetValue(logicalName, out var e) ? e : null);

        public Task<IReadOnlyList<CrmEntityMetadata>> GetApprovedEntitiesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<CrmEntityMetadata>>(_map.Values.ToList());

        public Task RefreshAsync(string? logicalName = null, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

                public Task<AttributeOptionsResult> EnsureAttributeOptionsAsync(
            string entityLogicalName,
            string attributeLogicalName,
            CancellationToken cancellationToken = default)
        {
            _ = cancellationToken;
            if (GetEntityAsync(entityLogicalName).Result is { } entity &&
                entity.TryGetAttribute(attributeLogicalName, out var attr))
            {
                return Task.FromResult(new AttributeOptionsResult
                {
                    Attribute = attr,
                    OptionsAvailable = attr.OptionSetByValue.Count > 0 || attr.OptionSetByLabel.Count > 0,
                    AlreadyCached = true
                });
            }

            return Task.FromResult(new AttributeOptionsResult
            {
                Error = $"Attribute '{attributeLogicalName}' was not found on '{entityLogicalName}'."
            });
        }

        public string GetAiCatalogSummary() => string.Empty;
    }

    private sealed class StubHttp : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }
}
