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

public sealed class AiSelfCorrectionTests
{
    [Fact]
    public async Task Valid_First_Attempt_Does_Not_Retry()
    {
        var chat = new ScriptedChatClient(QueryCrmResponse("call-1", ValidListFetchXml()));
        var executor = new RecordingFetchXmlExecutor { ValidatePass = true, QueryPass = true };
        var (service, store) = CreateService(chat, executor);

        var response = await service.ChatAsync(
            new ChatRequestDto { ConversationId = "c1", Message = "Show the latest 5 sales orders" },
            CancellationToken.None);

        Assert.Equal(1, chat.CallCount);
        Assert.Equal(1, executor.ValidateCount);
        Assert.Equal(1, executor.QueryCount);
        Assert.True(string.IsNullOrWhiteSpace(response.Reply));
        Assert.NotNull(response.StructuredData);
        Assert.Equal(1, store.GetOrCreate("c1").UserQuestionCount);
    }

    [Fact]
    public async Task Invalid_Field_Then_Valid_FetchXml_Recovers()
    {
        var chat = new ScriptedChatClient(
            QueryCrmResponse("call-1", AggregateFetchXml(groupField: "orderdate")),
            QueryCrmResponse("call-2", ValidAggregateFetchXml()));
        var executor = new RecordingFetchXmlExecutor
        {
            ValidatePass = false,
            FailMessage = "groupBy field 'orderdate' is not approved/readable on 'salesorder'.",
            FailStage = "FetchXmlBuild",
            FailCode = "INVALID_GROUP_BY_FIELD",
            PassAfterValidateFailures = 1
        };
        var (service, store) = CreateService(chat, executor);

        var response = await service.ChatAsync(
            new ChatRequestDto
            {
                ConversationId = "c2",
                Message = "Show each month's sales order amount in 2025."
            },
            CancellationToken.None);

        Assert.Equal(2, chat.CallCount);
        Assert.Equal(2, executor.ValidateCount);
        Assert.Equal(1, executor.QueryCount);
        Assert.True(string.IsNullOrWhiteSpace(response.Reply));
        Assert.NotNull(response.StructuredData);

        var toolResult = chat.CapturedToolResults.Single(fr => fr.CallId == "call-1");
        Assert.True(OutboundAiPayloadAuditor.IsQueryPlanValidationFeedback(toolResult.Result));
        Assert.Contains("INVALID_GROUP_BY_FIELD", Convert.ToString(toolResult.Result)!, StringComparison.Ordinal);
        Assert.DoesNotContain("records", Convert.ToString(toolResult.Result)!, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, store.GetOrCreate("c2").UserQuestionCount);
    }

    [Fact]
    public async Task Invalid_Relationship_Then_Valid_Recovers()
    {
        var chat = new ScriptedChatClient(
            QueryCrmResponse("rel-1", ListWithBogusLinkFetchXml()),
            QueryCrmResponse("rel-2", ValidListFetchXml()));
        var executor = new RecordingFetchXmlExecutor
        {
            ValidatePass = false,
            FailMessage = "relationship 'invented_relationship_schema' is not valid between 'salesorder' and 'account'.",
            FailStage = "FetchXmlBuild",
            FailCode = "INVALID_RELATIONSHIP",
            PassAfterValidateFailures = 1
        };
        var (service, _) = CreateService(chat, executor);

        var response = await service.ChatAsync(
            new ChatRequestDto
            {
                ConversationId = "c-rel",
                Message = "Show latest sales orders with the customer account name."
            },
            CancellationToken.None);

        Assert.Equal(2, chat.CallCount);
        Assert.Equal(1, executor.QueryCount);
        Assert.True(string.IsNullOrWhiteSpace(response.Reply));
        Assert.NotNull(response.StructuredData);
    }

    [Fact]
    public async Task Invalid_Aggregate_Field_Then_Valid_Recovers()
    {
        var chat = new ScriptedChatClient(
            QueryCrmResponse("agg-1", AggregateFetchXml(sumField: "revenue_amount")),
            QueryCrmResponse("agg-2", ValidAggregateFetchXml()));
        var executor = new RecordingFetchXmlExecutor
        {
            ValidatePass = false,
            FailMessage = "Aggregate field 'revenue_amount' is not approved/readable on 'salesorder'.",
            FailStage = "FetchXmlBuild",
            FailCode = "INVALID_AGGREGATE_FIELD",
            PassAfterValidateFailures = 1
        };
        var (service, _) = CreateService(chat, executor);

        var response = await service.ChatAsync(
            new ChatRequestDto { ConversationId = "c-agg", Message = "Sum sales order amounts by month in 2025" },
            CancellationToken.None);

        Assert.Equal(2, chat.CallCount);
        Assert.Equal(1, executor.QueryCount);
        Assert.Contains("INVALID_AGGREGATE_FIELD", Convert.ToString(chat.CapturedToolResults[0].Result)!, StringComparison.Ordinal);
        Assert.True(string.IsNullOrWhiteSpace(response.Reply));
        Assert.NotNull(response.StructuredData);
    }

    [Fact]
    public async Task Invalid_Sort_Alias_Then_Valid_Recovers()
    {
        var chat = new ScriptedChatClient(
            QueryCrmResponse("sort-1", AggregateFetchXml(sortAlias: "missing_alias")),
            QueryCrmResponse("sort-2", ValidAggregateFetchXml()));
        var executor = new RecordingFetchXmlExecutor
        {
            ValidatePass = false,
            FailMessage = "sort alias 'missing_alias' is not sortable for this FetchXML.",
            FailStage = "FetchXmlBuild",
            FailCode = "INVALID_SORT",
            PassAfterValidateFailures = 1
        };
        var (service, _) = CreateService(chat, executor);

        var response = await service.ChatAsync(
            new ChatRequestDto { ConversationId = "c-sort", Message = "Top months by sales order amount in 2025" },
            CancellationToken.None);

        Assert.Equal(2, chat.CallCount);
        Assert.Equal(1, executor.QueryCount);
        Assert.Contains("INVALID_SORT", Convert.ToString(chat.CapturedToolResults[0].Result)!, StringComparison.Ordinal);
        Assert.True(string.IsNullOrWhiteSpace(response.Reply));
        Assert.NotNull(response.StructuredData);
    }

    [Fact]
    public async Task Repeated_Identical_Invalid_FetchXml_Stops_Early()
    {
        var bad = AggregateFetchXml(groupField: "orderdate");
        var chat = new ScriptedChatClient(
            QueryCrmResponse("same-1", bad),
            QueryCrmResponse("same-2", bad),
            QueryCrmResponse("same-3", bad));
        var executor = new RecordingFetchXmlExecutor
        {
            ValidatePass = false,
            FailMessage = "groupBy field 'orderdate' is not approved/readable on 'salesorder'.",
            FailStage = "FetchXmlBuild"
        };
        var (service, _) = CreateService(chat, executor, maxRetries: 2);

        var response = await service.ChatAsync(
            new ChatRequestDto { ConversationId = "c-same", Message = "Monthly sales totals 2025" },
            CancellationToken.None);

        Assert.Equal(2, chat.CallCount);
        Assert.Equal(0, executor.QueryCount);
        Assert.Contains("identical", response.Reply, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task All_Attempts_Invalid_Returns_Clear_Error_Without_Execution()
    {
        var chat = new ScriptedChatClient(
            QueryCrmResponse("a1", AggregateFetchXml(groupField: "orderdate")),
            QueryCrmResponse("a2", AggregateFetchXml(groupField: "order_date")),
            QueryCrmResponse("a3", AggregateFetchXml(groupField: "OrderDate")));
        var executor = new RecordingFetchXmlExecutor
        {
            ValidatePass = false,
            FailMessage = "groupBy field 'orderdate' is not approved/readable on 'salesorder'.",
            FailStage = "FetchXmlBuild"
        };
        var (service, _) = CreateService(chat, executor, maxRetries: 2);

        var response = await service.ChatAsync(
            new ChatRequestDto { ConversationId = "c-all", Message = "Monthly sales totals 2025" },
            CancellationToken.None);

        Assert.Equal(3, chat.CallCount);
        Assert.Equal(0, executor.QueryCount);
        Assert.Contains("valid CRM FetchXML", response.Reply, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Authorization_Denial_Does_Not_Retry()
    {
        var chat = new ScriptedChatClient(
            QueryCrmResponse("auth-1", ListFetchXml(entity: "email")));
        var executor = new RecordingFetchXmlExecutor
        {
            ValidatePass = false,
            FailStage = "EntityApproval",
            FailCode = "ENTITY_NOT_APPROVED",
            FailMessage = "Entity 'email' is not an approved AI-accessible CRM entity."
        };
        var (service, _) = CreateService(chat, executor);

        var response = await service.ChatAsync(
            new ChatRequestDto { ConversationId = "c-auth", Message = "Show emails" },
            CancellationToken.None);

        Assert.Equal(1, chat.CallCount);
        Assert.Equal(0, executor.QueryCount);
        Assert.Contains("not an approved", response.Reply, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Crm_Validate_Infrastructure_Failure_Does_Not_Ai_Retry()
    {
        var chat = new ScriptedChatClient(QueryCrmResponse("meta-1", ValidAggregateFetchXml()));
        var executor = new RecordingFetchXmlExecutor
        {
            ValidatePass = false,
            FailStage = "MetadataLoad",
            FailCode = "METADATA_UNAVAILABLE",
            FailMessage = "CRM metadata for 'salesorder' could not be loaded from Dynamics 365. Please try again later."
        };
        var (service, _) = CreateService(chat, executor);

        var response = await service.ChatAsync(
            new ChatRequestDto { ConversationId = "c-meta", Message = "Monthly sales 2025" },
            CancellationToken.None);

        Assert.Equal(1, chat.CallCount);
        Assert.Equal(0, executor.QueryCount);
        Assert.Contains("could not be loaded", response.Reply, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Unsupported_Tool_Does_Not_Retry()
    {
        var chat = new ScriptedChatClient(
            new ChatResponse([
                new ChatMessage(ChatRole.Assistant, [
                    new FunctionCallContent("u1", "unsupported_crm_request", new Dictionary<string, object?>
                    {
                        ["message"] = "Cross-currency conversion is not supported by this assistant."
                    })
                ])
            ]));
        var (service, _) = CreateService(chat, new RecordingFetchXmlExecutor());

        var response = await service.ChatAsync(
            new ChatRequestDto { ConversationId = "c-un", Message = "Convert amounts to EUR" },
            CancellationToken.None);

        Assert.Equal(1, chat.CallCount);
        Assert.Equal("unsupported", response.ResponseType);
        Assert.Equal(ChatService.UnsupportedFeatureUserMessage, response.Reply);
        Assert.DoesNotContain("Cross-currency", response.Reply, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Clarify_Tool_Does_Not_Retry()
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
        var (service, _) = CreateService(chat, new RecordingFetchXmlExecutor());

        var response = await service.ChatAsync(
            new ChatRequestDto { ConversationId = "c-cl", Message = "six months within 2025" },
            CancellationToken.None);

        Assert.Equal(1, chat.CallCount);
        Assert.Equal("clarification", response.ResponseType);
    }

    [Fact]
    public async Task Question_Only_History_Unchanged_After_Correction()
    {
        var chat = new ScriptedChatClient(
            QueryCrmResponse("h1", AggregateFetchXml(groupField: "orderdate")),
            QueryCrmResponse("h2", ValidAggregateFetchXml()));
        var executor = new RecordingFetchXmlExecutor
        {
            ValidatePass = false,
            FailMessage = "groupBy field 'orderdate' is not approved/readable on 'salesorder'.",
            FailStage = "FetchXmlBuild",
            PassAfterValidateFailures = 1
        };
        var (service, store) = CreateService(chat, executor);
        const string q = "Monthly contribution of sales order amount in 2025";

        await service.ChatAsync(new ChatRequestDto { ConversationId = "c-hist", Message = q }, CancellationToken.None);

        Assert.Equal([q], store.GetOrCreate("c-hist").GetUserQuestionsSnapshot());
    }

    [Fact]
    public async Task Temporary_Tool_Messages_Maintain_Matching_Call_Ids()
    {
        var chat = new ScriptedChatClient(
            QueryCrmResponse("exact-id-42", AggregateFetchXml(groupField: "orderdate")),
            QueryCrmResponse("exact-id-43", ValidAggregateFetchXml()));
        var executor = new RecordingFetchXmlExecutor
        {
            ValidatePass = false,
            FailMessage = "groupBy field 'orderdate' is not approved/readable on 'salesorder'.",
            FailStage = "FetchXmlBuild",
            PassAfterValidateFailures = 1
        };
        var (service, _) = CreateService(chat, executor);

        await service.ChatAsync(
            new ChatRequestDto { ConversationId = "c-id", Message = "Monthly sales 2025" },
            CancellationToken.None);

        var fr = Assert.Single(chat.CapturedToolResults);
        Assert.Equal("exact-id-42", fr.CallId);
        Assert.Contains(
            chat.LastRequestMessages!,
            m => m.Contents.OfType<FunctionCallContent>().Any(c => c.CallId == "exact-id-42"));
    }

    [Fact]
    public async Task Invalid_FetchXml_Never_Executes()
    {
        var chat = new ScriptedChatClient(
            QueryCrmResponse("x1", AggregateFetchXml(groupField: "orderdate")),
            QueryCrmResponse("x2", AggregateFetchXml(groupField: "order_date")));
        var executor = new RecordingFetchXmlExecutor
        {
            ValidatePass = false,
            FailMessage = "groupBy field 'orderdate' is not approved/readable on 'salesorder'.",
            FailStage = "FetchXmlBuild"
        };
        var (service, _) = CreateService(chat, executor, maxRetries: 1);

        await service.ChatAsync(
            new ChatRequestDto { ConversationId = "c-noexec", Message = "Monthly sales 2025" },
            CancellationToken.None);

        Assert.Equal(0, executor.QueryCount);
    }

    [Fact]
    public async Task Query_Results_Never_Reach_Groq_On_Retry()
    {
        var chat = new ScriptedChatClient(
            QueryCrmResponse("p1", AggregateFetchXml(groupField: "orderdate")),
            QueryCrmResponse("p2", ValidAggregateFetchXml()));
        var executor = new RecordingFetchXmlExecutor
        {
            ValidatePass = false,
            FailMessage = "groupBy field 'orderdate' is not approved/readable on 'salesorder'.",
            FailStage = "FetchXmlBuild",
            PassAfterValidateFailures = 1,
            QueryRows =
            [
                new Dictionary<string, object?>
                {
                    ["year"] = 2025,
                    ["month"] = 4,
                    ["revenue"] = 100m,
                    ["ordernumber"] = "ORD-999"
                }
            ]
        };
        var (service, _) = CreateService(chat, executor);

        await service.ChatAsync(
            new ChatRequestDto { ConversationId = "c-priv", Message = "Monthly sales 2025" },
            CancellationToken.None);

        var blob = string.Join("\n", chat.AllRequestMessageTexts);
        Assert.DoesNotContain("ORD-999", blob, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"records\"", blob, StringComparison.OrdinalIgnoreCase);
        Assert.All(chat.CapturedToolResults, fr =>
            Assert.True(OutboundAiPayloadAuditor.IsQueryPlanValidationFeedback(fr.Result)));
    }

    [Fact]
    public async Task Partial_Result_Includes_Limitation_In_StructuredData()
    {
        var chat = new ScriptedChatClient(
            QueryCrmResponse("partial-1", ValidAggregateFetchXml(
                resultMode: "partial",
                limitation: "Comparing monthly totals against the annual monthly average is not currently supported.")));
        var executor = new RecordingFetchXmlExecutor { ValidatePass = true, QueryPass = true };
        var (service, _) = CreateService(chat, executor);

        var response = await service.ChatAsync(
            new ChatRequestDto { ConversationId = "c-partial", Message = "Monthly vs annual average" },
            CancellationToken.None);

        Assert.True(string.IsNullOrWhiteSpace(response.Reply));
        Assert.NotNull(response.StructuredData);
        var json = System.Text.Json.JsonSerializer.Serialize(response.StructuredData);
        Assert.Contains("partial", json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("annual monthly average", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Recoverable_Dynamics_Error_Then_Corrected_FetchXml_Succeeds()
    {
        var chat = new ScriptedChatClient(
            QueryCrmResponse("dyn-1", AggregateOrderByPhysicalAttribute()),
            QueryCrmResponse("dyn-2", ValidAggregateFetchXml()));
        var executor = new RecordingFetchXmlExecutor
        {
            ValidatePass = true,
            FailFirstExecutions = 1,
            ExecutionRecoverable = true,
            ExecutionFailCode = "INVALID_SORT",
            ExecutionFailMessage =
                "Aggregate column 'totalamount' must be sorted with <order alias=\"revenue\"/>, not attribute=\"totalamount\".",
            DynamicsErrorCode = "80041131"
        };
        var (service, store) = CreateService(chat, executor);

        var response = await service.ChatAsync(
            new ChatRequestDto
            {
                ConversationId = "c-dyn",
                Message = "Show the total sales order amount for each month in 2025, sorted from highest to lowest."
            },
            CancellationToken.None);

        Assert.Equal(2, chat.CallCount);
        Assert.Equal(2, executor.ValidateCount);
        Assert.Equal(2, executor.QueryCount);
        Assert.True(string.IsNullOrWhiteSpace(response.Reply));
        Assert.NotNull(response.StructuredData);
        Assert.Contains("INVALID_SORT", Convert.ToString(chat.CapturedToolResults[0].Result)!, StringComparison.Ordinal);
        Assert.DoesNotContain("ORD-", Convert.ToString(chat.CapturedToolResults[0].Result)!, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, store.GetOrCreate("c-dyn").UserQuestionCount);
    }

    [Fact]
    public async Task Unknown_Dynamics_Http400_Does_Not_Retry()
    {
        var chat = new ScriptedChatClient(QueryCrmResponse("u400", ValidAggregateFetchXml()));
        var executor = new RecordingFetchXmlExecutor
        {
            ValidatePass = true,
            FailFirstExecutions = 99,
            ExecutionRecoverable = false,
            ExecutionFailCode = "DYNAMICS_80040203",
            ExecutionFailMessage = "Dynamics Web API FetchXML GET failed. Status=400, Code=80040203.",
            DynamicsErrorCode = "80040203"
        };
        var (service, _) = CreateService(chat, executor);

        var response = await service.ChatAsync(
            new ChatRequestDto { ConversationId = "c-u400", Message = "Show me our biggest orders this year." },
            CancellationToken.None);

        Assert.Equal(1, chat.CallCount);
        Assert.Equal(1, executor.QueryCount);
        Assert.Contains("80040203", response.Reply, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Auth_Failure_During_Execution_Does_Not_Retry()
    {
        var chat = new ScriptedChatClient(QueryCrmResponse("auth-exec", ValidListFetchXml()));
        var executor = new RecordingFetchXmlExecutor
        {
            ValidatePass = true,
            FailFirstExecutions = 99,
            ExecutionRecoverable = false,
            ExecutionFailCode = "AUTHORIZATION_DENIED",
            ExecutionFailMessage = "CRM authorization failed for this query."
        };
        var (service, _) = CreateService(chat, executor);

        var response = await service.ChatAsync(
            new ChatRequestDto { ConversationId = "c-auth-exec", Message = "Show orders" },
            CancellationToken.None);

        Assert.Equal(1, chat.CallCount);
        Assert.Equal(1, executor.QueryCount);
        Assert.Contains("authorization", response.Reply, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Cancellation_Stops_Retries()
    {
        using var cts = new CancellationTokenSource();
        var chat = new ScriptedChatClient(
            QueryCrmResponse("cancel-1", AggregateFetchXml(groupField: "orderdate")),
            new Func<ChatResponse>(() =>
            {
                cts.Cancel();
                throw new OperationCanceledException(cts.Token);
            }));
        var executor = new RecordingFetchXmlExecutor
        {
            ValidatePass = false,
            FailMessage = "groupBy field 'orderdate' is not approved/readable on 'salesorder'.",
            FailStage = "FetchXmlBuild"
        };
        var (service, _) = CreateService(chat, executor);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.ChatAsync(
                new ChatRequestDto { ConversationId = "c-cancel", Message = "Monthly sales 2025" },
                cts.Token));

        Assert.Equal(0, executor.QueryCount);
        Assert.Equal(2, chat.CallCount);
    }

    [Fact]
    public void Auditor_Allows_Validation_Feedback_But_Blocks_Crm_Records()
    {
        var feedback = QueryPlanValidationFeedback.Fail(
            "INVALID_GROUP_BY_FIELD",
            "The selected field cannot be used for grouping on this entity.",
            field: "orderdate",
            entity: "salesorder").ToToolResultJson();

        var ok = OutboundAiPayloadAuditor.Audit([
            new(ChatRole.System, "sys"),
            new(ChatRole.User, "q"),
            new(ChatRole.Assistant, [new FunctionCallContent("1", "query_crm")]),
            new(ChatRole.Tool, [new FunctionResultContent("1", feedback)])
        ]);
        Assert.True(ok.SafeForLiveCrm);
        Assert.True(ok.ContainsFunctionResults);

        var bad = OutboundAiPayloadAuditor.Audit([
            new(ChatRole.Tool, [new FunctionResultContent("1", "{\"salesOrders\":[{\"ordernumber\":\"ORD-1\"}]}")])
        ]);
        Assert.False(bad.SafeForLiveCrm);
    }

    [Fact]
    public void Classifier_Maps_GroupBy_And_Authorization()
    {
        var group = QueryPlanFailureClassifier.Classify(
            QueryPlanValidationStage.FetchXmlBuild,
            "groupBy field 'orderdate' is not approved/readable on 'salesorder'.");
        Assert.Equal(QueryPlanFailureKind.RecoverablePlanning, group.Kind);
        Assert.Equal("INVALID_GROUP_BY_FIELD", group.Code);

        var auth = QueryPlanFailureClassifier.Classify(
            QueryPlanValidationStage.EntityApproval,
            "Entity 'email' is not an approved AI CRM entity.");
        Assert.Equal(QueryPlanFailureKind.NonRetryableAuthorization, auth.Kind);
    }

    private static (ChatService Service, ConversationStore Store) CreateService(
        ScriptedChatClient chat,
        RecordingFetchXmlExecutor executor,
        int maxRetries = 2)
    {
        var crmOptions = Options.Create(new CrmOptions
        {
            Provider = "Dynamics365",
            EnableLiveIntegration = false,
            BaseUrl = null,
            ApprovedEntities = ["account", "contact", "lead", "salesorder"],
            EntityAliases = new Dictionary<string, List<string>>
            {
                ["salesorder"] = ["sales order", "sales orders", "orders"]
            }
        });
        var aiOptions = Options.Create(new AiOptions
        {
            Provider = "Groq",
            MaxQueryPlanCorrectionRetries = maxRetries,
            ProviderTimeoutSeconds = 30
        });
        var store = new ConversationStore();
        var http = new DynamicsCrmHttp(
            new StubHttpClientFactory(),
            crmOptions,
            NullLogger<DynamicsCrmHttp>.Instance);
        var connectivity = new DynamicsConnectivityService(
            http, crmOptions, NullLogger<DynamicsConnectivityService>.Instance);
        var approval = new EntityApprovalPolicy(crmOptions);
        var metadata = new StubMetadata();
        var discovery = new CrmSchemaDiscoveryService(
            metadata,
            approval,
            new CrmAuthorizationService(crmOptions, NullLogger<CrmAuthorizationService>.Instance, approval),
            NullLogger<CrmSchemaDiscoveryService>.Instance);
        var service = new ChatService(
            chat,
            new CrmAiTools(NullLogger<CrmAiTools>.Instance),
            store,
            connectivity,
            executor,
            metadata,
            discovery,
            approval,
            crmOptions,
            aiOptions,
            NullLogger<ChatService>.Instance);
        return (service, store);
    }

    private static ChatResponse QueryCrmResponse(string callId, IDictionary<string, object?> args) =>
        new([
            new ChatMessage(ChatRole.Assistant, [
                new FunctionCallContent(callId, "query_crm", args)
            ])
        ]);

    private static IDictionary<string, object?> ValidListFetchXml(string entity = "salesorder") =>
        ListFetchXml(entity);

    private static IDictionary<string, object?> ListFetchXml(string entity = "salesorder") =>
        new Dictionary<string, object?>
        {
            ["fetchXml"] =
                $"""
                <fetch top="5">
                  <entity name="{entity}">
                    <attribute name="name" />
                    <attribute name="ordernumber" />
                    <attribute name="createdon" />
                    <order attribute="createdon" descending="true" />
                  </entity>
                </fetch>
                """,
            ["resultMode"] = "complete",
            ["limitation"] = null
        };

    private static IDictionary<string, object?> ListWithBogusLinkFetchXml() =>
        new Dictionary<string, object?>
        {
            ["fetchXml"] =
                """
                <fetch top="5">
                  <entity name="salesorder">
                    <attribute name="name" />
                    <link-entity name="account" from="accountid" to="customerid" alias="customer" link-type="inner" relationship="invented_relationship_schema">
                      <attribute name="name" />
                    </link-entity>
                  </entity>
                </fetch>
                """,
            ["resultMode"] = "complete"
        };

    private static IDictionary<string, object?> AggregateOrderByPhysicalAttribute() =>
        new Dictionary<string, object?>
        {
            ["fetchXml"] =
                """
                <fetch aggregate="true">
                  <entity name="salesorder">
                    <attribute name="createdon" alias="year" groupby="true" dategrouping="year" />
                    <attribute name="createdon" alias="month" groupby="true" dategrouping="month" />
                    <attribute name="totalamount" alias="revenue" aggregate="sum" />
                    <order attribute="totalamount" descending="true" />
                  </entity>
                </fetch>
                """,
            ["resultMode"] = "complete"
        };

    private static IDictionary<string, object?> ValidAggregateFetchXml(
        string resultMode = "complete",
        string? limitation = null) =>
        AggregateFetchXml(resultMode: resultMode, limitation: limitation);

    private static IDictionary<string, object?> AggregateFetchXml(
        string entity = "salesorder",
        string groupField = "createdon",
        string sumField = "totalamount",
        string sortAlias = "revenue",
        string resultMode = "complete",
        string? limitation = null) =>
        new Dictionary<string, object?>
        {
            ["fetchXml"] =
                $"""
                <fetch aggregate="true">
                  <entity name="{entity}">
                    <attribute name="{groupField}" alias="year" groupby="true" dategrouping="year" />
                    <attribute name="{groupField}" alias="month" groupby="true" dategrouping="month" />
                    <attribute name="{sumField}" alias="revenue" aggregate="sum" />
                    <order alias="{sortAlias}" descending="true" />
                    <filter type="and">
                      <condition attribute="createdon" operator="on-or-after" value="2025-01-01" />
                      <condition attribute="createdon" operator="on-or-before" value="2025-12-31" />
                    </filter>
                  </entity>
                </fetch>
                """,
            ["resultMode"] = resultMode,
            ["limitation"] = limitation
        };

    private sealed class ScriptedChatClient : IChatClient
    {
        private readonly Queue<object> _script;
        public int CallCount { get; private set; }
        public IList<ChatMessage>? LastRequestMessages { get; private set; }
        public List<string> AllRequestMessageTexts { get; } = [];
        public List<FunctionResultContent> CapturedToolResults { get; } = [];

        public ScriptedChatClient(params object[] script) =>
            _script = new Queue<object>(script);

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            var list = messages.ToList();
            LastRequestMessages = list;
            foreach (var m in list)
            {
                if (!string.IsNullOrWhiteSpace(m.Text))
                {
                    AllRequestMessageTexts.Add(m.Text!);
                }

                foreach (var fr in m.Contents.OfType<FunctionResultContent>())
                {
                    CapturedToolResults.Add(fr);
                    AllRequestMessageTexts.Add(Convert.ToString(fr.Result) ?? string.Empty);
                }
            }

            if (_script.Count == 0)
            {
                throw new InvalidOperationException("No scripted Groq responses remaining.");
            }

            var next = _script.Dequeue();
            if (next is Func<ChatResponse> factory)
            {
                return Task.FromResult(factory());
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

    private sealed class RecordingFetchXmlExecutor : IFetchXmlQueryExecutor
    {
        private int _validateFailures;
        private int _executionFailures;

        public bool ValidatePass { get; set; } = true;
        public bool QueryPass { get; set; } = true;
        public int PassAfterValidateFailures { get; set; }
        public int FailFirstExecutions { get; set; }
        public bool ExecutionRecoverable { get; set; }
        public string ExecutionFailCode { get; set; } = "CRM_EXECUTION_FAILURE";
        public string ExecutionFailMessage { get; set; } = "Dynamics execution failed.";
        public string? DynamicsErrorCode { get; set; }
        public string FailMessage { get; set; } = "Invalid FetchXML.";
        public string FailStage { get; set; } = "FetchXmlBuild";
        public string FailCode { get; set; } = "INVALID_QUERY_PLAN";
        public int ValidateCount { get; private set; }
        public int QueryCount { get; private set; }
        public IReadOnlyList<IReadOnlyDictionary<string, object?>> QueryRows { get; set; } =
        [
            new Dictionary<string, object?>
            {
                ["year"] = 2025,
                ["month"] = 4,
                ["revenue"] = 100m
            }
        ];

        public Task<FetchXmlValidationOutcome> ValidateAsync(
            FetchXmlToolRequest request,
            CancellationToken cancellationToken = default)
        {
            ValidateCount++;
            if (ValidatePass ||
                (PassAfterValidateFailures > 0 && _validateFailures >= PassAfterValidateFailures))
            {
                return Task.FromResult(FetchXmlValidationOutcome.Ok(BuildValidated(request)));
            }

            _validateFailures++;
            return Task.FromResult(FetchXmlValidationOutcome.Fail(FailStage, FailCode, FailMessage));
        }

        public Task<GenericCrmQueryResult> ExecuteAsync(
            ValidatedFetchXmlQuery query,
            CancellationToken cancellationToken = default)
        {
            QueryCount++;
            if (!QueryPass)
            {
                throw new TimeoutException("CRM timeout");
            }

            if (_executionFailures < FailFirstExecutions)
            {
                _executionFailures++;
                return Task.FromResult(new GenericCrmQueryResult
                {
                    Entity = query.EntityLogicalName,
                    Operation = query.IsAggregate ? CrmQueryOperation.Aggregate : CrmQueryOperation.List,
                    RelativeRequestUri = string.Empty,
                    Message = ExecutionFailMessage,
                    DynamicsErrorCode = DynamicsErrorCode,
                    DynamicsHttpStatus = 400,
                    IsRecoverableFetchXmlError = ExecutionRecoverable,
                    SanitizedCorrectionFeedback = ExecutionFailMessage,
                    CorrectionErrorCode = ExecutionFailCode,
                    ProviderName = "Test",
                    ResultMode = query.ResultMode.ToString().ToLowerInvariant(),
                    Limitation = query.Limitation
                });
            }

            return Task.FromResult(new GenericCrmQueryResult
            {
                Entity = query.EntityLogicalName,
                Operation = query.IsAggregate ? CrmQueryOperation.Aggregate : CrmQueryOperation.List,
                RelativeRequestUri = query.RelativeRequestUri,
                Limit = query.Top ?? QueryRows.Count,
                Items = QueryRows,
                ProviderName = "Test",
                Columns = query.Columns,
                ResultMode = query.ResultMode.ToString().ToLowerInvariant(),
                Limitation = query.Limitation
            });
        }

        private static ValidatedFetchXmlQuery BuildValidated(FetchXmlToolRequest request)
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

            return new ValidatedFetchXmlQuery
            {
                FetchXml = request.FetchXml,
                EntityLogicalName = entity.LogicalName,
                EntitySetName = entity.EntitySetName,
                Entity = entity,
                RelativeRequestUri = "salesorders?fetchXml=%3Cfetch%3E",
                IsAggregate = request.FetchXml.Contains("aggregate=\"true\"", StringComparison.OrdinalIgnoreCase),
                Top = 5,
                Columns =
                [
                    new CrmTableColumn { Key = "year", Label = "Year", Kind = "year" },
                    new CrmTableColumn { Key = "month", Label = "Month", Kind = "month" },
                    new CrmTableColumn { Key = "revenue", Label = "Total amount", Kind = "money" }
                ],
                ResultMode = request.ResultMode,
                Limitation = request.Limitation
            };
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
