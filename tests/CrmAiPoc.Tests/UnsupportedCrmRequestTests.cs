using CrmAiPoc.Api.Ai;
using CrmAiPoc.Api.Configuration;
using CrmAiPoc.Api.Crm.Dynamics;
using CrmAiPoc.Api.Crm.FetchXml;
using CrmAiPoc.Api.Crm.Metadata;
using CrmAiPoc.Api.Crm.Metadata.Discovery;
using CrmAiPoc.Api.Models;
using CrmAiPoc.Api.Security;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CrmAiPoc.Tests;

public sealed class UnsupportedCrmRequestTests
{
    [Fact]
    public void ChatResponseDto_Supports_Unsupported_ResponseType()
    {
        var dto = new ChatResponseDto
        {
            ConversationId = "c1",
            Reply = ChatService.UnsupportedFeatureUserMessage,
            ResponseType = "unsupported",
            SelectedTool = "unsupported_crm_request",
            PrivacySafeToolExecution = true,
            OutboundAiContainedCrmRecords = false
        };

        Assert.Equal("unsupported", dto.ResponseType);
        Assert.Equal("unsupported_crm_request", dto.SelectedTool);
        Assert.Equal(
            "This feature is not available in the current version.",
            dto.Reply);
        Assert.Null(dto.Suggestions);
        Assert.Null(dto.StructuredData);
    }

    [Fact]
    public void Outbound_History_Still_User_Only_With_Three_Tool_Architecture()
    {
        var history = ChatService.BuildOutboundHistory(
        [
            "Show the top 5 accounts by percentage of total sales in 2025."
        ]);

        Assert.Contains("unsupported_crm_request", history[0].Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("discover_crm_schema", history[0].Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("fetchXml", history[0].Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(history, m => m.Role == ChatRole.Assistant);
    }

    [Fact]
    public async Task Unsupported_Tool_Returns_Standardized_User_Message()
    {
        const string modelReason = "Cross-currency conversion is not supported by this assistant.";
        var chat = new ScriptedChatClient(
            new ChatResponse([
                new ChatMessage(ChatRole.Assistant, [
                    new FunctionCallContent("u1", "unsupported_crm_request", new Dictionary<string, object?>
                    {
                        ["message"] = modelReason
                    })
                ])
            ]));
        var logger = new CollectingLogger();
        var service = CreateService(chat, logger);

        var response = await service.ChatAsync(
            new ChatRequestDto { ConversationId = "c-un-std", Message = "Convert amounts to EUR" },
            CancellationToken.None);

        Assert.Equal("unsupported", response.ResponseType);
        Assert.Equal("unsupported_crm_request", response.SelectedTool);
        Assert.Equal(ChatService.UnsupportedFeatureUserMessage, response.Reply);
        Assert.Equal("This feature is not available in the current version.", response.Reply);
        Assert.DoesNotContain("Cross-currency", response.Reply, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("FetchXML", response.Reply, StringComparison.OrdinalIgnoreCase);
        Assert.Null(response.StructuredData);

        Assert.Contains(
            logger.Entries,
            e => e.Contains("[UNSUPPORTED]", StringComparison.Ordinal) &&
                 e.Contains(modelReason, StringComparison.Ordinal) &&
                 e.Contains(ChatService.UnsupportedFeatureUserMessage, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Unsupported_Tool_With_Missing_Reason_Still_Returns_Standardized_Message()
    {
        var chat = new ScriptedChatClient(
            new ChatResponse([
                new ChatMessage(ChatRole.Assistant, [
                    new FunctionCallContent("u2", "unsupported_crm_request", new Dictionary<string, object?>
                    {
                        ["message"] = "short"
                    })
                ])
            ]));
        var logger = new CollectingLogger();
        var service = CreateService(chat, logger);

        var response = await service.ChatAsync(
            new ChatRequestDto { ConversationId = "c-un-short", Message = "Do something unsupported" },
            CancellationToken.None);

        Assert.Equal("unsupported", response.ResponseType);
        Assert.Equal(ChatService.UnsupportedFeatureUserMessage, response.Reply);
        Assert.Contains(
            logger.Entries,
            e => e.Contains("[UNSUPPORTED]", StringComparison.Ordinal) &&
                 e.Contains("InvalidOrMissingReason", StringComparison.Ordinal));
    }

    private static ChatService CreateService(IChatClient chat, ILogger<ChatService> logger)
    {
        var crmOptions = Options.Create(new CrmOptions
        {
            Provider = "Dynamics365",
            EnableLiveIntegration = false,
            ApprovedEntities = ["account", "contact", "salesorder"]
        });
        var aiOptions = Options.Create(new AiOptions
        {
            Provider = "Groq",
            MaxQueryPlanCorrectionRetries = 1
        });
        var approval = new EntityApprovalPolicy(crmOptions);
        var http = new DynamicsCrmHttp(
            new StubHttp(),
            crmOptions,
            NullLogger<DynamicsCrmHttp>.Instance);
        var metadata = new EmptyMetadata();
        return new ChatService(
            chat,
            new CrmAiTools(NullLogger<CrmAiTools>.Instance),
            new ConversationStore(),
            new DynamicsConnectivityService(http, crmOptions, NullLogger<DynamicsConnectivityService>.Instance),
            new NoOpFetchXmlExecutor(),
            metadata,
            new CrmSchemaDiscoveryService(
                metadata,
                approval,
                new CrmAuthorizationService(crmOptions, NullLogger<CrmAuthorizationService>.Instance, approval),
                NullLogger<CrmSchemaDiscoveryService>.Instance),
            approval,
            crmOptions,
            aiOptions,
            logger);
    }

    private sealed class CollectingLogger : ILogger<ChatService>
    {
        public List<string> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Entries.Add(formatter(state, exception));
        }
    }

    private sealed class ScriptedChatClient(params ChatResponse[] responses) : IChatClient
    {
        private int _index;

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> chatMessages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var response = responses[Math.Min(_index, responses.Length - 1)];
            _index++;
            return Task.FromResult(response);
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> chatMessages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    private sealed class StubHttp : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    private sealed class NoOpFetchXmlExecutor : IFetchXmlQueryExecutor
    {
        public Task<FetchXmlValidationOutcome> ValidateAsync(
            FetchXmlToolRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(FetchXmlValidationOutcome.Fail("Parse", "UNUSED", "not used"));

        public Task<CrmAiPoc.Api.Crm.Generic.GenericCrmQueryResult> ExecuteAsync(
            ValidatedFetchXmlQuery query,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class EmptyMetadata : ICrmMetadataService
    {
        public Task EnsureApprovedEntitiesLoadedAsync(CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<CrmEntityMetadata?> GetEntityAsync(string logicalName, CancellationToken cancellationToken = default) =>
            Task.FromResult<CrmEntityMetadata?>(null);

        public Task<IReadOnlyList<CrmEntityMetadata>> GetApprovedEntitiesAsync(
            CancellationToken cancellationToken = default) =>
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
