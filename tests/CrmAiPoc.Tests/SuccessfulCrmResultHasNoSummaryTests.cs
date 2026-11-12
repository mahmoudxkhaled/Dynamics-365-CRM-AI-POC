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
/// Successful structured CRM results must not carry generated summary text in Reply.
/// </summary>
public sealed class SuccessfulCrmResultHasNoSummaryTests
{
    [Fact]
    public async Task List_Result_Reply_Is_Empty_And_StructuredData_Present()
    {
        var response = await RunAsync(ListToolCall(), CrmQueryOperation.List);
        Assert.True(string.IsNullOrWhiteSpace(response.Reply));
        Assert.NotNull(response.StructuredData);
        var json = System.Text.Json.JsonSerializer.Serialize(response.StructuredData);
        Assert.Contains("\"resultMode\":\"complete\"", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Aggregate_Result_Reply_Is_Empty_And_StructuredData_Present()
    {
        var response = await RunAsync(AggregateToolCall(), CrmQueryOperation.Aggregate);
        Assert.True(string.IsNullOrWhiteSpace(response.Reply));
        Assert.NotNull(response.StructuredData);
        Assert.Equal("query_crm", response.SelectedTool);
    }

    [Fact]
    public async Task Count_Result_Reply_Is_Empty_With_Count_In_StructuredData()
    {
        var response = await RunAsync(CountToolCall(), CrmQueryOperation.Count, count: 42);
        Assert.True(string.IsNullOrWhiteSpace(response.Reply));
        Assert.NotNull(response.StructuredData);
    }

    [Fact]
    public async Task Clarification_Still_Returns_Message()
    {
        var chat = new ScriptedChatClient(
            new ChatResponse([
                new ChatMessage(ChatRole.Assistant, [
                    new FunctionCallContent("cl-1", "clarify_crm", new Dictionary<string, object?>
                    {
                        ["message"] = "Which months in 2025 should I include?",
                        ["suggestionsJson"] =
                            """[{"label":"All of 2025","question":"Show monthly sales order amounts for all of 2025."},{"label":"H1 2025","question":"Show monthly sales order amounts for January through June 2025."}]"""
                    })
                ])
            ]));
        var service = CreateService(chat, new StubExecutor(CrmQueryOperation.List));

        var response = await service.ChatAsync(
            new ChatRequestDto { ConversationId = "cl-keep", Message = "six months within 2025" },
            CancellationToken.None);

        Assert.Equal("clarification", response.ResponseType);
        Assert.False(string.IsNullOrWhiteSpace(response.Reply));
        Assert.NotEmpty(response.Suggestions ?? []);
    }

    private static async Task<ChatResponseDto> RunAsync(
        ChatResponse toolCall,
        CrmQueryOperation operation,
        int? count = null)
    {
        var chat = new ScriptedChatClient(toolCall);
        var service = CreateService(chat, new StubExecutor(operation, count));
        return await service.ChatAsync(
            new ChatRequestDto { ConversationId = "nosum", Message = "Show CRM data" },
            CancellationToken.None);
    }

    private static ChatService CreateService(IChatClient chat, IFetchXmlQueryExecutor executor)
    {
        var crmOptions = Options.Create(new CrmOptions
        {
            Provider = "Dynamics365",
            EnableLiveIntegration = false,
            ApprovedEntities = ["salesorder", "account", "contact", "lead"]
        });
        var aiOptions = Options.Create(new AiOptions { MaxQueryPlanCorrectionRetries = 0 });
        var approval = new EntityApprovalPolicy(crmOptions);
        var metadata = new StubMetadata();
        return new ChatService(
            chat,
            new CrmAiTools(NullLogger<CrmAiTools>.Instance),
            new ConversationStore(),
            new DynamicsConnectivityService(
                new DynamicsCrmHttp(new StubHttpClientFactory(), crmOptions, NullLogger<DynamicsCrmHttp>.Instance),
                crmOptions,
                NullLogger<DynamicsConnectivityService>.Instance),
            executor,
            metadata,
            new CrmSchemaDiscoveryService(
                metadata,
                approval,
                new CrmAuthorizationService(crmOptions, NullLogger<CrmAuthorizationService>.Instance, approval),
                NullLogger<CrmSchemaDiscoveryService>.Instance),
            approval,
            crmOptions,
            aiOptions,
            NullLogger<ChatService>.Instance);
    }

    private static ChatResponse ListToolCall() =>
        new([
            new ChatMessage(ChatRole.Assistant, [
                new FunctionCallContent("c1", "query_crm", new Dictionary<string, object?>
                {
                    ["fetchXml"] =
                        """
                        <fetch top="5">
                          <entity name="salesorder">
                            <attribute name="name" />
                            <attribute name="ordernumber" />
                            <attribute name="createdon" />
                          </entity>
                        </fetch>
                        """,
                    ["resultMode"] = "complete"
                })
            ])
        ]);

    private static ChatResponse CountToolCall() =>
        new([
            new ChatMessage(ChatRole.Assistant, [
                new FunctionCallContent("c2", "query_crm", new Dictionary<string, object?>
                {
                    ["fetchXml"] =
                        """
                        <fetch aggregate="true">
                          <entity name="salesorder">
                            <attribute name="salesorderid" alias="cnt" aggregate="count" />
                          </entity>
                        </fetch>
                        """,
                    ["resultMode"] = "complete"
                })
            ])
        ]);

    private static ChatResponse AggregateToolCall() =>
        new([
            new ChatMessage(ChatRole.Assistant, [
                new FunctionCallContent("c3", "query_crm", new Dictionary<string, object?>
                {
                    ["fetchXml"] =
                        """
                        <fetch aggregate="true">
                          <entity name="salesorder">
                            <attribute name="createdon" alias="year" groupby="true" dategrouping="year" />
                            <attribute name="createdon" alias="month" groupby="true" dategrouping="month" />
                            <attribute name="salesorderid" alias="ordercount" aggregate="count" />
                          </entity>
                        </fetch>
                        """,
                    ["resultMode"] = "complete"
                })
            ])
        ]);

    private sealed class ScriptedChatClient(params ChatResponse[] responses) : IChatClient
    {
        private readonly Queue<ChatResponse> _queue = new(responses);

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(_queue.Dequeue());

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class StubExecutor(CrmQueryOperation operation, int? count = null) : IFetchXmlQueryExecutor
    {
        public Task<FetchXmlValidationOutcome> ValidateAsync(
            FetchXmlToolRequest request,
            CancellationToken cancellationToken = default)
        {
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
                RelativeRequestUri = "salesorders?fetchXml=test",
                IsAggregate = operation is CrmQueryOperation.Aggregate or CrmQueryOperation.Count,
                IsCountOnly = operation == CrmQueryOperation.Count,
                Top = 5,
                Columns =
                [
                    new CrmTableColumn { Key = "name", Label = "Name", Kind = "string" },
                    new CrmTableColumn { Key = "ordernumber", Label = "Order #", Kind = "string" }
                ],
                ResultMode = request.ResultMode,
                Limitation = request.Limitation
            }));
        }

        public Task<GenericCrmQueryResult> ExecuteAsync(
            ValidatedFetchXmlQuery query,
            CancellationToken cancellationToken = default)
        {
            if (operation == CrmQueryOperation.Count)
            {
                return Task.FromResult(new GenericCrmQueryResult
                {
                    Entity = query.EntityLogicalName,
                    Operation = CrmQueryOperation.Count,
                    RelativeRequestUri = "(fetchXml count)",
                    Count = count ?? 0,
                    ProviderName = "Test",
                    ResultMode = "complete"
                });
            }

            return Task.FromResult(new GenericCrmQueryResult
            {
                Entity = query.EntityLogicalName,
                Operation = operation,
                RelativeRequestUri = "(fetchXml test)",
                Limit = query.Top ?? 5,
                Items =
                [
                    new Dictionary<string, object?>
                    {
                        ["name"] = "Demo",
                        ["ordernumber"] = "ORD-1",
                        ["createdon"] = DateTimeOffset.UtcNow
                    }
                ],
                Columns = query.Columns,
                ProviderName = "Test",
                ResultMode = query.ResultMode.ToString().ToLowerInvariant(),
                Limitation = query.Limitation
            });
        }
    }

    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    private sealed class StubMetadata : ICrmMetadataService
    {
        public Task EnsureApprovedEntitiesLoadedAsync(CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<CrmEntityMetadata?> GetEntityAsync(string logicalName, CancellationToken cancellationToken = default) =>
            Task.FromResult<CrmEntityMetadata?>(null);

        public Task<IReadOnlyList<CrmEntityMetadata>> GetApprovedEntitiesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<CrmEntityMetadata>>([]);

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
}
