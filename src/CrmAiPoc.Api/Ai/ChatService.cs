using System.Diagnostics;
using System.Globalization;
using CrmAiPoc.Api.Configuration;
using CrmAiPoc.Api.Crm.Dynamics;
using CrmAiPoc.Api.Crm.FetchXml;
using CrmAiPoc.Api.Crm.Generic;
using CrmAiPoc.Api.Crm.Metadata;
using CrmAiPoc.Api.Crm.Metadata.Discovery;
using CrmAiPoc.Api.Models;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace CrmAiPoc.Api.Ai;

public sealed partial class ChatService
{
    /// <summary>Fixed user-facing text for <c>unsupported_crm_request</c> (technical reason stays in logs).</summary>
    internal const string UnsupportedFeatureUserMessage =
        "This feature is not available in the current version.";

    private static readonly string SystemPrompt = """
        You are a read-only Microsoft Dynamics 365 On-Premises v9.1 CRM assistant for Sales and Marketing users.
        Users may ask in Arabic, English, or mixed Arabic and English. Understand the question in the user's language.
        Use your pretrained knowledge of standard Dynamics 365 CRM entities, attributes, relationships, and FetchXML.
        Always use Dynamics logical names (for example account, salesorder, createdon, statecode, name). Never invent schema identifiers.
        Never treat a business display label as an automatically valid logical name.
        All user-facing clarification and unsupported messages must be English.
        You have EXACTLY FOUR tools: discover_crm_schema, query_crm, clarify_crm, and unsupported_crm_request.
        For every CRM data request you MUST call exactly one tool per turn. Do not answer CRM data questions as plain assistant text.
        Non-CRM conversation (greetings) and structural follow-ups about a prior privacy-safe result schema may be answered as short plain text without inventing CRM values.

        Tool selection:
        - Direct query_crm: single-entity requests that use only standard fields you can name confidently
          (example: "Show the latest 10 Sales Orders.").
        - discover_crm_schema FIRST, then query_crm: any schema-dependent request, including multi-entity joins,
          custom attributes, unverified relationships/join fields, ambiguous field meanings, business concepts
          that could map to multiple CRM fields, or reports needing unverified date/money/grouping fields
          (example: "Show the top 10 Sales Orders with Customer Account and Billing Contact details.").
        - Do not invent custom attributes or relationship SchemaNames when discover_crm_schema is available.
        - For multi-entity questions, request the relevant relationshipPairs in one discovery call; do not discover
          only one field after a validation failure when relationships were needed up front.
        - After discover_crm_schema returns, call exactly one of: query_crm (preferred, using returned candidates),
          clarify_crm, or unsupported_crm_request. Do not reply with plain assistant text after discovery.
        - clarify_crm: genuinely ambiguous business intent (for example ranking "top customers" by sales value vs order count).
          Ask business-level choices only — never ask the user to pick SchemaNames, join attributes, GUIDs, or OptionSet numeric codes.
          Each suggestion label/question must be complete and directly executable as the next user message (no blanks, ___, or [enter value] placeholders).
          Prefer clear business alternatives the user can click; the UI also offers a custom free-text reply.
          Rejected suggestions are not proof that CRM lacks the feature — do not call unsupported_crm_request only because chips were filtered.
          If technical metadata is missing, call discover_crm_schema instead of asking the user for implementation details.
        - unsupported_crm_request: understood but no useful authorized query can be produced.

        The .NET backend validates FetchXML against live metadata and authorization, then executes it. It will not auto-repair invalid schema.
        If a query_crm tool result reports validation failure, revise the FetchXML using that feedback while preserving the user's original business intent.
        Do not substitute a different report. If you cannot safely correct the query, call clarify_crm or unsupported_crm_request.

        Conversation context:
        - Earlier user and assistant messages (if any) are conversational context only.
        - Prior assistant messages may include clarification questions, option meanings, and a privacy-safe summary of the previous CRM result schema (entity, operation, column keys/labels, filters, row count) — never record values.
        - Use that prior-result schema to answer structural follow-ups (which columns were returned, what was grouped) without calling query_crm again and without inventing CRM values.
        - When the user asks for new or changed CRM data, call query_crm (discover first when schema is missing).
        - The most recent user message is the current request.
        - Follow-ups may modify earlier constraints; the latest instruction wins.
        - Independent questions must not inherit unrelated filters or entities from earlier messages.
        - For CRM data requests emit one tool call for the current request only.
        - Never echo CRM records, tool payloads, or FetchXML from conversation context (none are provided).
        - Never invent CRM row values.

        discover_crm_schema contract:
        - entities: approved logical names to scope search (include every entity you plan to query or join).
        - concepts: business phrases to map to attributes (e.g. "order total value", "billing contact full name").
        - relationshipPairsJson: JSON array of [fromEntity,toEntity] pairs for every join you need
          (e.g. [["salesorder","account"],["salesorder","contact"]]).
        - Use returned candidates: logicalName/type for attributes; SchemaName, businessLabel, and
          referencing/referenced entity and attribute to choose link-entity from/to.
        - Returned entities[] include primaryIdAttribute and primaryNameAttribute for each scoped entity.
        - When multiple valid relationships exist between the same entities, choose using discovered
          businessLabel and endpoints so the join matches the user's business concept. Distinct labels
          are not interchangeable (for example a "Potential Customer" relationship is not automatically
          the same as "Customer Account"). If the business meaning cannot be determined confidently,
          call clarify_crm — do not guess.
        Returns compact metadata candidates only — never CRM records.

        query_crm contract:
        - fetchXml: a complete <fetch>...</fetch> document (Dynamics 365 On-Premises v9.1 FetchXML).
        - resultMode: complete | partial
        - limitation: required for partial; describe only the missing capability. Never claim CRM data was already retrieved.
        Use complete when native FetchXML can fulfill the request.
        Use partial when FetchXML can retrieve useful supporting CRM data but cannot complete the requested analysis.

        FetchXML guidance (must match backend validator):
        - Allowed elements only: fetch, entity, attribute, filter, condition, link-entity, order, value.
        - Prefer minimal root attributes: top for lists; aggregate="true" for aggregates. Optional: version, mapping, distinct.
        - Do not invent unsupported root attributes (including hyphenated forms such as no-lock or output-format).
        - List queries: set top (1–50). Do not combine top with count. Do not use page greater than 1.
          Default newest-first with createdon when appropriate.
        - Joins: <link-entity name="..." from="..." to="..." alias="..." link-type="inner|outer" relationship="SchemaName">.
          from = attribute on the linked entity (name=); to = attribute on the immediate parent entity.
          Choose relationship/from/to from discovery candidates using businessLabel + endpoints.
          Distinct business labels are not interchangeable; if unsure, call clarify_crm.
          For nested joins, evaluate from/to against the immediate parent entity.
          Choose inner vs outer by meaning; do not exclude parent rows merely because a related record may be missing
          unless the user requires a match.
        - Filters: filter/condition with supported operators; AND/OR via filter type; entityname for linked aliases / anti-joins.
          For Picklist/State/Status/Boolean attributes, discovery may return options:[{value,label}].
          Always use the numeric option value in FetchXML condition value= — never put display labels in choice filters.
          If options are missing, call discover_crm_schema for that concept before query_crm.
        - Aggregates: aggregate="true"; each aggregate/groupby attribute needs an alias; dategrouping when grouping dates
          (day|week|month|quarter|year|fiscal-period|fiscal-year). Aggregate ops: count, countcolumn, sum, avg, min, max.
          Sort aggregates with <order alias="declaredAlias"/> — never order attribute="aliasName" or attribute="physicalField".
          When grouping by individual CRM records (customer, account, contact, or any entity's display name),
          always groupby that entity's primaryIdAttribute and optionally also its primaryNameAttribute for labels.
          Do not group by display name alone — distinct records can share the same name.
          Categorical groupby (status, state, option sets, date buckets) does not need a primary ID.
          Prefer a tight date filter on large aggregates when the user implies a time window.
          Verify date and money fields via discovery when organization-specific.
        - Never emit SQL, OData business queries, C#, scripts, invented SchemaNames, or CRM records.

        Short examples (placeholders — replace with verified logical names):
        List:
        <fetch top="10"><entity name="salesorder"><attribute name="name"/><attribute name="createdon"/><order attribute="createdon" descending="true"/></entity></fetch>
        Join (outer keeps unmatched parents):
        <fetch top="10"><entity name="salesorder"><attribute name="name"/><link-entity name="account" from="accountid" to="customerid" alias="cust" link-type="outer" relationship="order_customer_accounts"><attribute name="name"/></link-entity></entity></fetch>
        Aggregate by month:
        <fetch aggregate="true"><entity name="salesorder"><attribute name="createdon" alias="month" groupby="true" dategrouping="month"/><attribute name="totalamount" alias="revenue" aggregate="sum"/><order alias="revenue" descending="true"/></entity></fetch>

        Write/create/update/delete requests are refused by the backend before tool selection.
        """;

    private readonly IChatClient _chatClient;
    private readonly CrmAiTools _tools;
    private readonly ConversationStore _conversations;
    private readonly DynamicsConnectivityService _connectivity;
    private readonly IFetchXmlQueryExecutor _fetchXmlExecutor;
    private readonly ICrmMetadataService _metadata;
    private readonly CrmSchemaDiscoveryService _schemaDiscovery;
    private readonly EntityApprovalPolicy _approvalPolicy;
    private readonly CrmOptions _crmOptions;
    private readonly AiOptions _aiOptions;
    private readonly ILogger<ChatService> _logger;

    public ChatService(
        IChatClient chatClient,
        CrmAiTools tools,
        ConversationStore conversations,
        DynamicsConnectivityService connectivity,
        IFetchXmlQueryExecutor fetchXmlExecutor,
        ICrmMetadataService metadata,
        CrmSchemaDiscoveryService schemaDiscovery,
        EntityApprovalPolicy approvalPolicy,
        IOptions<CrmOptions> crmOptions,
        IOptions<AiOptions> aiOptions,
        ILogger<ChatService> logger)
    {
        _chatClient = chatClient;
        _tools = tools;
        _conversations = conversations;
        _connectivity = connectivity;
        _fetchXmlExecutor = fetchXmlExecutor;
        _metadata = metadata;
        _schemaDiscovery = schemaDiscovery;
        _approvalPolicy = approvalPolicy;
        _crmOptions = crmOptions.Value;
        _aiOptions = aiOptions.Value;
        _logger = logger;
    }

    public async Task<ChatResponseDto> ChatAsync(ChatRequestDto request, CancellationToken cancellationToken)
    {
        var session = _conversations.GetOrCreate(request.ConversationId);
        session.Touch();
        return await ChatCoreAsync(request, session, cancellationToken);
    }

    private async Task<ChatResponseDto> ChatCoreAsync(
        ChatRequestDto request,
        ConversationSession session,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "User question: conversationId={ConversationId}, messageLength={Length}",
            session.ConversationId,
            request.Message.Trim().Length);

        if (LooksLikeWriteRequest(request.Message))
        {
            // Rejected input — do not append to question-only history.
            return CreateResponse(session, RefuseWrite(), outboundWasSafe: true);
        }

        var userMessage = request.Message.Trim();
        var correlationId = Guid.NewGuid().ToString("N")[..12];
        var questionLanguage = EnglishInputPolicy.ContainsArabicScript(userMessage)
            ? (userMessage.Any(c => c is >= 'A' and <= 'Z' or >= 'a' and <= 'z') ? "mixed" : "ar")
            : "en";

        // LLM-first: system prompt + privacy-safe prior turns + current question.
        // Never send tool outputs, CRM records, or metadata catalogs.
        await _metadata.EnsureApprovedEntitiesLoadedAsync(cancellationToken);

        var previousTurns = session.GetTurnsSnapshot();
        var maxMessages = Math.Clamp(_aiOptions.MaxUserQuestionsInHistory, 1, 50);
        var maxHistoryCharacters = Math.Clamp(_aiOptions.MaxUserHistoryCharacters, 500, 100_000);
        var selectedWindow = SelectConversationWindow(
            previousTurns,
            userMessage,
            maxMessages,
            maxHistoryCharacters);
        var outboundHistory = BuildOutboundHistory(selectedWindow.Turns);
        var audit = OutboundAiPayloadAuditor.Audit(outboundHistory);
        if (!audit.SafeForLiveCrm)
        {
            throw new InvalidOperationException(
                "Refusing to call the AI provider because the outbound payload appears to contain CRM tool results or record text.");
        }

        // Accepted user question — store only after validation, before the provider call.
        // Consecutive exact duplicates are ignored inside the session (retry-safe).
        session.TryAppendUserQuestion(userMessage);

        var discoverTool = AIFunctionFactory.Create(_tools.discover_crm_schema);
        var queryCrmTool = AIFunctionFactory.Create(_tools.query_crm);
        var clarifyCrmTool = AIFunctionFactory.Create(_tools.clarify_crm);
        var unsupportedTool = AIFunctionFactory.Create(_tools.unsupported_crm_request);
        var chatOptions = new ChatOptions
        {
            Tools = [discoverTool, queryCrmTool, clarifyCrmTool, unsupportedTool],
            // RequireAny → OpenAI/Groq tool_choice=required: model must call one of the tools.
            ToolMode = ChatToolMode.RequireAny
            // Temperature + reasoning_effort for Groq come from GroqOptions via LazyGroqChatClient.
        };

        var requestPayloadCharacters = outboundHistory.Sum(m => m.Text?.Length ?? 0);
        LogAiRequest(
            correlationId,
            session.ConversationId,
            userMessage,
            questionLanguage,
            selectedWindow,
            outboundHistory);

        ChatResponse modelResponse;
        try
        {
            modelResponse = await GetResponseWithRetryAsync(
                outboundHistory,
                chatOptions,
                correlationId,
                requestPayloadCharacters,
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _logger.LogInformation(
                "[AI ERROR] CorrelationId={CorrelationId} FailureKind=UserCancelled RequestPayloadCharacters={RequestPayloadCharacters}",
                correlationId,
                requestPayloadCharacters);
            throw;
        }
        catch (Exception ex) when (AiProviderFailureClassifier.Classify(ex) == AiProviderFailureKind.ProviderLimit
                                   || AiProviderFailureClassifier.IsProviderLimitMessage(ex.Message))
        {
            LogProviderFailure(
                correlationId,
                AiProviderFailureKind.ProviderLimit,
                attempt: 1,
                providerTimeoutSeconds: Math.Clamp(_aiOptions.ProviderTimeoutSeconds, 8, 90),
                elapsedMs: 0,
                requestPayloadCharacters,
                ex);
            return CreateResponse(
                session,
                $"The AI provider rejected or failed the request (payload chars={requestPayloadCharacters}): {ex.Message}",
                outboundWasSafe: audit.SafeForLiveCrm);
        }
        catch (TimeoutException ex)
        {
            LogProviderFailure(
                correlationId,
                AiProviderFailureKind.ClientTimeout,
                attempt: 1,
                providerTimeoutSeconds: Math.Clamp(_aiOptions.ProviderTimeoutSeconds, 8, 90),
                elapsedMs: 0,
                requestPayloadCharacters,
                ex);
            return CreateResponse(
                session,
                "The AI planner did not respond in time. Please retry your question.",
                outboundWasSafe: audit.SafeForLiveCrm);
        }
        catch (Exception ex) when (AiProviderFailureClassifier.IsToolUseFailedWithoutToolCall(ex))
        {
            // RequireAny can HTTP 400 when the model returns plain text (greetings / structural follow-ups).
            // One Auto retry keeps tools available for CRM without fabricating executed query results.
            _logger.LogWarning(
                ex,
                "[AI TOOL MODE] CorrelationId={CorrelationId} tool_use_failed under RequireAny; " +
                "retrying once with ToolMode=Auto. ToolModeWas=RequireAny",
                correlationId);

            try
            {
                var autoOptions = chatOptions.Clone();
                autoOptions.ToolMode = ChatToolMode.Auto;
                modelResponse = await GetResponseWithRetryAsync(
                    outboundHistory,
                    autoOptions,
                    correlationId,
                    requestPayloadCharacters,
                    cancellationToken);

                _logger.LogInformation(
                    "[AI TOOL MODE] CorrelationId={CorrelationId} AutoRetry=success ToolMode=Auto",
                    correlationId);

                return await DispatchToolCallsWithSelfCorrectionAsync(
                    session,
                    outboundHistory,
                    autoOptions,
                    modelResponse,
                    userMessage,
                    correlationId,
                    audit.SafeForLiveCrm,
                    requestPayloadCharacters,
                    cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception autoEx)
            {
                LogProviderFailure(
                    correlationId,
                    AiProviderFailureClassifier.Classify(autoEx),
                    attempt: 2,
                    providerTimeoutSeconds: Math.Clamp(_aiOptions.ProviderTimeoutSeconds, 8, 90),
                    elapsedMs: 0,
                    requestPayloadCharacters,
                    autoEx);
                return CreateResponse(
                    session,
                    "The AI planner could not complete this request. Please rephrase and try again.",
                    outboundWasSafe: audit.SafeForLiveCrm);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException
                                   || ex.GetType().Name.Contains("ClientResult", StringComparison.Ordinal))
        {
            var kind = AiProviderFailureClassifier.Classify(ex);
            LogProviderFailure(
                correlationId,
                kind,
                attempt: 1,
                providerTimeoutSeconds: Math.Clamp(_aiOptions.ProviderTimeoutSeconds, 8, 90),
                elapsedMs: 0,
                requestPayloadCharacters,
                ex);
            return CreateResponse(
                session,
                kind == AiProviderFailureKind.Http400
                    ? "The AI planner could not use the CRM planning tools for this question. Please rephrase and try again."
                    : "The AI planner did not respond in time. Please retry your question.",
                outboundWasSafe: audit.SafeForLiveCrm);
        }

        return await DispatchToolCallsWithSelfCorrectionAsync(
            session,
            outboundHistory,
            chatOptions,
            modelResponse,
            userMessage,
            correlationId,
            audit.SafeForLiveCrm,
            requestPayloadCharacters,
            cancellationToken);
    }

    /// <summary>
    /// Dispatches Groq tool calls. Supports bounded discover_crm_schema then query_crm,
    /// plus recoverable query_crm validation/Dynamics self-correction.
    /// Temporary assistant/tool messages stay request-local and are never stored on the session.
    /// </summary>
    private async Task<ChatResponseDto> DispatchToolCallsWithSelfCorrectionAsync(
        ConversationSession session,
        IReadOnlyList<ChatMessage> initialOutboundHistory,
        ChatOptions chatOptions,
        ChatResponse modelResponse,
        string userMessage,
        string correlationId,
        bool outboundWasSafe,
        int requestPayloadCharacters,
        CancellationToken cancellationToken)
    {
        var maxCorrectionRetries = Math.Clamp(_aiOptions.MaxQueryPlanCorrectionRetries, 0, 5);
        var maxAttempts = 1 + maxCorrectionRetries;
        var maxDiscoveryCalls = Math.Clamp(_aiOptions.MaxSchemaDiscoveryCallsPerRequest, 0, 3);
        var maxCandidates = Math.Clamp(_aiOptions.MaxSchemaDiscoveryCandidatesPerConcept, 1, 10);
        var workingMessages = initialOutboundHistory.ToList();
        long totalPromptTokens = 0;
        long totalCompletionTokens = 0;
        string? lastPlanFingerprint = null;
        var executedFetchXml = false;
        var discoveryUsed = 0;
        var queryAttempt = 0;
        var round = 0;
        var maxRounds = maxAttempts + maxDiscoveryCalls + 3;

        while (round < maxRounds)
        {
            round++;
            cancellationToken.ThrowIfCancellationRequested();

            AccumulateUsage(modelResponse, ref totalPromptTokens, ref totalCompletionTokens);

            var functionCalls = modelResponse.Messages
                .SelectMany(m => m.Contents.OfType<FunctionCallContent>())
                .ToList();

            var attempt = Math.Max(1, queryAttempt);
            LogAiResponse(correlationId, modelResponse, functionCalls, attempt, maxAttempts);

            if (functionCalls.Count == 0)
            {
                _logger.LogWarning(
                    "[AI RESPONSE] Provider={Provider} CorrelationId={CorrelationId} Attempt={Attempt}/{MaxAttempts} ResponseType=answer ToolCall=(none) " +
                    "Message=Expected a tool call under RequireAny",
                    _aiOptions.Provider ?? "Groq",
                    correlationId,
                    attempt,
                    maxAttempts);
                var text = LocalCrmReplyFormatter.FormatRefusalOrText(modelResponse.Text, "en");
                return CreateResponse(session, text, outboundWasSafe: outboundWasSafe, responseType: "answer");
            }

            if (functionCalls.Count > 1)
            {
                _logger.LogWarning(
                    "[GROQ RESPONSE] CorrelationId={CorrelationId} Attempt={Attempt}/{MaxAttempts} MultipleToolCalls={Count}",
                    correlationId,
                    attempt,
                    maxAttempts,
                    functionCalls.Count);
                return CreateResponse(
                    session,
                    "The AI planner returned multiple tool calls for one question. Please rephrase and try again.",
                    outboundWasSafe: outboundWasSafe,
                    responseType: "answer");
            }

            var call = EnsureCallId(functionCalls[0]);
            _logger.LogInformation(
                "[GROQ RESPONSE] CorrelationId={CorrelationId} Attempt={Attempt}/{MaxAttempts} ToolName={ToolName} CallId={CallId} " +
                "TotalPromptTokens={TotalPromptTokens} TotalCompletionTokens={TotalCompletionTokens}",
                correlationId,
                attempt,
                maxAttempts,
                call.Name,
                call.CallId,
                totalPromptTokens,
                totalCompletionTokens);

            if (string.Equals(call.Name, "clarify_crm", StringComparison.OrdinalIgnoreCase))
            {
                LogSelfCorrectionOutcome(correlationId, "Clarify", attempt, executedFetchXml, totalPromptTokens, totalCompletionTokens);
                return await HandleClarifyCrmAsync(
                    session, call, userMessage, correlationId, outboundWasSafe, cancellationToken);
            }

            if (string.Equals(call.Name, "unsupported_crm_request", StringComparison.OrdinalIgnoreCase))
            {
                LogSelfCorrectionOutcome(correlationId, "Unsupported", attempt, executedFetchXml, totalPromptTokens, totalCompletionTokens);
                return HandleUnsupportedCrmRequest(session, call, userMessage, correlationId, outboundWasSafe);
            }

            if (string.Equals(call.Name, "discover_crm_schema", StringComparison.OrdinalIgnoreCase))
            {
                if (discoveryUsed >= maxDiscoveryCalls)
                {
                    return CreateResponse(
                        session,
                        "Metadata discovery limit reached for this request. Please rephrase with clearer field names, or ask again.",
                        selectedTool: "discover_crm_schema",
                        outboundWasSafe: outboundWasSafe);
                }

                discoveryUsed++;
                var discoveryContinue = await ContinueAfterSchemaDiscoveryAsync(
                    workingMessages,
                    modelResponse,
                    call,
                    chatOptions,
                    correlationId,
                    maxCandidates,
                    outboundWasSafe,
                    cancellationToken);
                if (!discoveryContinue.Success)
                {
                    return CreateResponse(
                        session,
                        discoveryContinue.ErrorMessage ?? "Schema discovery failed.",
                        selectedTool: "discover_crm_schema",
                        outboundWasSafe: discoveryContinue.OutboundWasSafe);
                }

                modelResponse = discoveryContinue.NextResponse!;
                continue;
            }

            if (!string.Equals(call.Name, "query_crm", StringComparison.OrdinalIgnoreCase))
            {
                return CreateResponse(
                    session,
                    $"Tool '{call.Name}' is not exposed. Use discover_crm_schema, query_crm, clarify_crm, or unsupported_crm_request only.",
                    selectedTool: call.Name,
                    outboundWasSafe: outboundWasSafe);
            }

            queryAttempt++;
            attempt = queryAttempt;
            if (queryAttempt > maxAttempts)
            {
                return CreateResponse(
                    session,
                    "I could not produce a valid CRM FetchXML query after validation retries. Please rephrase and try again.",
                    selectedTool: "query_crm",
                    outboundWasSafe: outboundWasSafe);
            }

            _logger.LogInformation(
                "[QUERY PLAN] CorrelationId={CorrelationId} Attempt={Attempt}/{MaxAttempts} DiscoveryUsed={DiscoveryUsed}",
                correlationId,
                attempt,
                maxAttempts,
                discoveryUsed);

            var planAttempt = await TryValidateFetchXmlAsync(call, correlationId, cancellationToken);

            if (planAttempt.Success && planAttempt.Query is not null)
            {
                _logger.LogInformation(
                    "[FETCHXML VALIDATION] CorrelationId={CorrelationId} Attempt={Attempt}/{MaxAttempts} Passed=true",
                    correlationId,
                    attempt,
                    maxAttempts);

                _logger.LogInformation(
                    "[FETCHXML] CorrelationId={CorrelationId} Executing validated plan Entity={Entity} Aggregate={Aggregate}",
                    correlationId,
                    planAttempt.Query.EntityLogicalName,
                    planAttempt.Query.IsAggregate);

                executedFetchXml = true;
                var execution = await TryExecuteValidatedFetchXmlAsync(
                    session, planAttempt.Query, correlationId, outboundWasSafe, cancellationToken);

                if (execution.Success && execution.Response is not null)
                {
                    LogSelfCorrectionOutcome(
                        correlationId,
                        attempt > 1 || discoveryUsed > 0 ? "Recovered" : "FirstAttemptSuccess",
                        attempt,
                        executedFetchXml,
                        totalPromptTokens,
                        totalCompletionTokens);
                    return execution.Response;
                }

                if (!execution.IsRecoverable)
                {
                    LogSelfCorrectionOutcome(
                        correlationId,
                        "NonRetryable:Execution",
                        attempt,
                        executedFetchXml,
                        totalPromptTokens,
                        totalCompletionTokens);
                    return CreateResponse(
                        session,
                        execution.UserMessage ?? "The CRM FetchXML query could not be executed.",
                        selectedTool: "query_crm",
                        outboundWasSafe: outboundWasSafe);
                }

                // Recoverable Dynamics semantic rejection — reuse the shared correction budget.
                planAttempt = new QueryPlanAttemptResult
                {
                    Success = false,
                    FailureKind = QueryPlanFailureKind.RecoverablePlanning,
                    ErrorCode = execution.ErrorCode ?? "INVALID_FETCHXML",
                    ErrorMessage = execution.SanitizedFeedback ?? execution.UserMessage,
                    UserMessage = execution.UserMessage,
                    Field = execution.Field,
                    Entity = planAttempt.Query.EntityLogicalName
                };

                _logger.LogWarning(
                    "[FETCHXML EXECUTION] CorrelationId={CorrelationId} Attempt={Attempt}/{MaxAttempts} " +
                    "RecoverableDynamicsError=true ErrorCode={ErrorCode} DynamicsCode={DynamicsCode}",
                    correlationId,
                    attempt,
                    maxAttempts,
                    planAttempt.ErrorCode,
                    execution.DynamicsErrorCode);
            }
            else
            {
                _logger.LogWarning(
                    "[FETCHXML VALIDATION] CorrelationId={CorrelationId} Attempt={Attempt}/{MaxAttempts} Passed=false " +
                    "ErrorCode={ErrorCode} Kind={Kind} Error={Error}",
                    correlationId,
                    attempt,
                    maxAttempts,
                    planAttempt.ErrorCode,
                    planAttempt.FailureKind,
                    TruncateForLog(planAttempt.ErrorMessage, 400));
            }

            if (!QueryPlanFailureClassifier.IsRetryable(planAttempt.FailureKind))
            {
                LogSelfCorrectionOutcome(
                    correlationId,
                    "NonRetryable:" + planAttempt.FailureKind,
                    attempt,
                    executedFetchXml,
                    totalPromptTokens,
                    totalCompletionTokens);
                return CreateResponse(
                    session,
                    planAttempt.UserMessage ?? planAttempt.ErrorMessage ?? "The CRM FetchXML query was rejected.",
                    selectedTool: "query_crm",
                    outboundWasSafe: outboundWasSafe);
            }

            var fingerprint = FingerprintToolArguments(call.Arguments);
            if (!string.IsNullOrEmpty(lastPlanFingerprint) &&
                string.Equals(fingerprint, lastPlanFingerprint, StringComparison.Ordinal))
            {
                _logger.LogWarning(
                    "[AI SELF-CORRECTION] CorrelationId={CorrelationId} Outcome=RepeatedIdenticalPlan Attempt={Attempt} ErrorCode={ErrorCode}",
                    correlationId,
                    attempt,
                    planAttempt.ErrorCode);
                LogSelfCorrectionOutcome(
                    correlationId,
                    "RepeatedIdenticalPlan",
                    attempt,
                    executedFetchXml,
                    totalPromptTokens,
                    totalCompletionTokens);
                return CreateResponse(
                    session,
                    "I could not produce a valid CRM FetchXML query after repeated identical attempts. " +
                    "Please rephrase with the exact field or relationship you need, or ask for clarification options.",
                    selectedTool: "query_crm",
                    outboundWasSafe: outboundWasSafe);
            }

            lastPlanFingerprint = fingerprint;

            if (attempt >= maxAttempts)
            {
                LogSelfCorrectionOutcome(
                    correlationId,
                    "RetriesExhausted",
                    attempt,
                    executedFetchXml,
                    totalPromptTokens,
                    totalCompletionTokens);
                return CreateResponse(
                    session,
                    "I could not produce a valid CRM FetchXML query after validation retries. " +
                    (planAttempt.UserMessage ?? planAttempt.ErrorMessage ?? "Please rephrase and try again."),
                    selectedTool: "query_crm",
                    outboundWasSafe: outboundWasSafe);
            }

            var feedback = QueryPlanValidationFeedback.Fail(
                planAttempt.ErrorCode ?? "INVALID_QUERY_PLAN",
                planAttempt.ErrorMessage ?? "The FetchXML query failed validation.",
                planAttempt.Field,
                planAttempt.Entity);
            var feedbackJson = feedback.ToToolResultJson();

            _logger.LogInformation(
                "[AI SELF-CORRECTION] CorrelationId={CorrelationId} Retry={Retry} ErrorCode={ErrorCode} CallId={CallId}",
                correlationId,
                attempt,
                planAttempt.ErrorCode,
                call.CallId);

            // Request-local only: preserve assistant tool-call message + matching tool result CallId.
            AppendAssistantToolCall(workingMessages, modelResponse, call);
            workingMessages.Add(new ChatMessage(ChatRole.Tool, [new FunctionResultContent(call.CallId, feedbackJson)]));

            var retryAudit = OutboundAiPayloadAuditor.Audit(workingMessages);
            if (!retryAudit.SafeForLiveCrm)
            {
                _logger.LogError(
                    "[AI SELF-CORRECTION] CorrelationId={CorrelationId} Refusing retry: outbound payload failed privacy audit.",
                    correlationId);
                return CreateResponse(
                    session,
                    "I could not safely continue CRM planning for this request. Please rephrase and try again.",
                    selectedTool: "query_crm",
                    outboundWasSafe: false);
            }

            try
            {
                modelResponse = await GetResponseWithRetryAsync(
                    workingMessages,
                    chatOptions,
                    correlationId,
                    workingMessages.Sum(m => m.Text?.Length ?? 0) + feedbackJson.Length,
                    cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                LogSelfCorrectionOutcome(
                    correlationId,
                    "Cancelled",
                    attempt,
                    executedFetchXml,
                    totalPromptTokens,
                    totalCompletionTokens);
                throw;
            }
            catch (TimeoutException)
            {
                LogSelfCorrectionOutcome(
                    correlationId,
                    "ProviderTimeout",
                    attempt,
                    executedFetchXml,
                    totalPromptTokens,
                    totalCompletionTokens);
                return CreateResponse(
                    session,
                    "The AI planner did not respond in time while correcting the query plan. Please retry your question.",
                    outboundWasSafe: outboundWasSafe);
            }
            catch (Exception ex) when (AiProviderFailureClassifier.Classify(ex) == AiProviderFailureKind.ProviderLimit
                                       || AiProviderFailureClassifier.IsProviderLimitMessage(ex.Message))
            {
                return CreateResponse(
                    session,
                    $"The AI provider rejected or failed the correction request: {ex.Message}",
                    outboundWasSafe: outboundWasSafe);
            }
            catch (Exception ex) when (ex is HttpRequestException
                                       || ex.GetType().Name.Contains("ClientResult", StringComparison.Ordinal))
            {
                return CreateResponse(
                    session,
                    "The AI planner could not continue query-plan correction. Please rephrase and try again.",
                    outboundWasSafe: outboundWasSafe);
            }
        }

        return CreateResponse(
            session,
            "I could not produce a valid CRM query plan for this request.",
            selectedTool: "query_crm",
            outboundWasSafe: outboundWasSafe);
    }

    private sealed class SchemaDiscoveryContinueResult
    {
        public bool Success { get; init; }
        public ChatResponse? NextResponse { get; init; }
        public string? ErrorMessage { get; init; }
        public bool OutboundWasSafe { get; init; } = true;
    }

    private async Task<SchemaDiscoveryContinueResult> ContinueAfterSchemaDiscoveryAsync(
        List<ChatMessage> workingMessages,
        ChatResponse modelResponse,
        FunctionCallContent call,
        ChatOptions chatOptions,
        string correlationId,
        int maxCandidates,
        bool outboundWasSafe,
        CancellationToken cancellationToken)
    {
        var parsed = CrmSchemaDiscoveryArgumentParser.Parse(call.Arguments);
        if (!parsed.Success || parsed.Request is null)
        {
            var failJson = System.Text.Json.JsonSerializer.Serialize(new
            {
                fields = Array.Empty<object>(),
                relationships = Array.Empty<object>(),
                unresolvedConcepts = Array.Empty<string>(),
                notes = new[] { parsed.Error ?? "Invalid discover_crm_schema arguments." }
            });

            AppendAssistantToolCall(workingMessages, modelResponse, call);
            workingMessages.Add(new ChatMessage(ChatRole.Tool, [new FunctionResultContent(call.CallId, failJson)]));
        }
        else
        {
            var discovered = await _schemaDiscovery.DiscoverAsync(
                parsed.Request,
                maxCandidatesPerConcept: maxCandidates,
                cancellationToken: cancellationToken);

            var payload = discovered.Success
                ? discovered.Json
                : System.Text.Json.JsonSerializer.Serialize(new
                {
                    fields = Array.Empty<object>(),
                    relationships = Array.Empty<object>(),
                    unresolvedConcepts = Array.Empty<string>(),
                    notes = new[] { discovered.Error ?? "Schema discovery failed." }
                });

            _logger.LogInformation(
                "[METADATA DISCOVERY] CorrelationId={CorrelationId} CallId={CallId} ResponseCharacters={Chars} Success={Success}",
                correlationId,
                call.CallId,
                payload.Length,
                discovered.Success);

            AppendAssistantToolCall(workingMessages, modelResponse, call);
            workingMessages.Add(new ChatMessage(ChatRole.Tool, [new FunctionResultContent(call.CallId, payload)]));
        }

        var audit = OutboundAiPayloadAuditor.Audit(workingMessages);
        if (!audit.SafeForLiveCrm)
        {
            _logger.LogError(
                "[METADATA DISCOVERY] CorrelationId={CorrelationId} Refusing continue: outbound payload failed privacy audit.",
                correlationId);
            return new SchemaDiscoveryContinueResult
            {
                Success = false,
                ErrorMessage = "I could not safely continue CRM planning after schema discovery.",
                OutboundWasSafe = false
            };
        }

        var payloadChars = workingMessages.Sum(m => m.Text?.Length ?? 0);
        try
        {
            var next = await GetResponseWithRetryAsync(
                workingMessages,
                chatOptions,
                correlationId,
                payloadChars,
                cancellationToken);
            return new SchemaDiscoveryContinueResult { Success = true, NextResponse = next, OutboundWasSafe = outboundWasSafe };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (TimeoutException)
        {
            return new SchemaDiscoveryContinueResult
            {
                Success = false,
                ErrorMessage = "The AI planner did not respond in time after schema discovery. Please retry.",
                OutboundWasSafe = outboundWasSafe
            };
        }
        catch (Exception ex) when (AiProviderFailureClassifier.IsToolUseFailedWithoutToolCall(ex))
        {
            // Groq RequireAny (tool_choice=required) can HTTP 400 with tool_use_failed when the model
            // returns text after discovery instead of a tool call. One bounded continuation retry with
            // ToolMode=Auto keeps tools available without changing the global planning policy.
            _logger.LogWarning(
                ex,
                "[METADATA DISCOVERY] CorrelationId={CorrelationId} tool_use_failed under RequireAny; " +
                "retrying discovery continuation once with ToolMode=Auto.",
                correlationId);

            try
            {
                var autoOptions = chatOptions.Clone();
                autoOptions.ToolMode = ChatToolMode.Auto;
                var next = await GetResponseWithRetryAsync(
                    workingMessages,
                    autoOptions,
                    correlationId,
                    payloadChars,
                    cancellationToken);
                return new SchemaDiscoveryContinueResult
                {
                    Success = true,
                    NextResponse = next,
                    OutboundWasSafe = outboundWasSafe
                };
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (TimeoutException)
            {
                return new SchemaDiscoveryContinueResult
                {
                    Success = false,
                    ErrorMessage = "The AI planner did not respond in time after schema discovery. Please retry.",
                    OutboundWasSafe = outboundWasSafe
                };
            }
            catch (Exception autoEx)
            {
                _logger.LogWarning(
                    autoEx,
                    "[METADATA DISCOVERY] CorrelationId={CorrelationId} Auto ToolMode continuation also failed.",
                    correlationId);
                return new SchemaDiscoveryContinueResult
                {
                    Success = false,
                    ErrorMessage = "The AI planner could not continue after schema discovery. Please rephrase and try again.",
                    OutboundWasSafe = outboundWasSafe
                };
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[METADATA DISCOVERY] Follow-up Groq call failed. CorrelationId={CorrelationId}", correlationId);
            return new SchemaDiscoveryContinueResult
            {
                Success = false,
                ErrorMessage = "The AI planner could not continue after schema discovery. Please rephrase and try again.",
                OutboundWasSafe = outboundWasSafe
            };
        }
    }

    private ChatResponseDto HandleUnsupportedCrmRequest(
        ConversationSession session,
        FunctionCallContent call,
        string userMessage,
        string correlationId,
        bool outboundWasSafe)
    {
        var originalReason = GetArgValue(call.Arguments, "message")?.Trim();
        if (string.IsNullOrWhiteSpace(originalReason))
        {
            originalReason = GetArgValue(call.Arguments, "reason")?.Trim();
        }

        if (string.IsNullOrWhiteSpace(originalReason) || originalReason.Length < 12)
        {
            _logger.LogWarning(
                "[UNSUPPORTED] CorrelationId={CorrelationId} InvalidOrMissingReason OriginalQuestion={OriginalQuestion} " +
                "OriginalReason={OriginalReason}",
                correlationId,
                TruncateForLog(userMessage, 500),
                TruncateForLog(originalReason, 500));
            originalReason = "(missing or invalid unsupported reason from model)";
        }
        else if (originalReason.Length > 500)
        {
            originalReason = originalReason[..500];
        }

        // Keep the model's reason in logs; never surface it (or FetchXML/tool diagnostics) to the UI.
        _logger.LogInformation(
            "[UNSUPPORTED] CorrelationId={CorrelationId} OriginalQuestion={OriginalQuestion} " +
            "OriginalReason={OriginalReason} UserMessage={UserMessage} Tool={Tool}",
            correlationId,
            TruncateForLog(userMessage, 500),
            TruncateForLog(originalReason, 500),
            UnsupportedFeatureUserMessage,
            "unsupported_crm_request");

        return CreateResponse(
            session,
            UnsupportedFeatureUserMessage,
            outboundWasSafe: outboundWasSafe,
            selectedTool: "unsupported_crm_request",
            responseType: "unsupported");
    }

    private async Task<ChatResponseDto> HandleClarifyCrmAsync(
        ConversationSession session,
        FunctionCallContent call,
        string userMessage,
        string correlationId,
        bool outboundWasSafe,
        CancellationToken cancellationToken)
    {
        var parsed = ClarifyCrmArgumentParser.ParseFromToolArguments(call.Arguments);
        var approvedEntities = await _metadata.GetApprovedEntitiesAsync(cancellationToken);
        var validated = ClarificationSuggestionValidator.Validate(parsed, approvedEntities, _approvalPolicy);

        var originalSuggestionsForLog = parsed.Success
            ? string.Join(" || ", parsed.Suggestions.Select(s => $"{s.Label}: {s.Question}"))
            : parsed.Error ?? "parse-failed";

        if (!validated.Success)
        {
            _logger.LogWarning(
                "[CLARIFICATION] CorrelationId={CorrelationId} ValidationFailed={Error} " +
                "Received={Received} Accepted={Accepted} Rejected={Rejected} RejectionReasons=[{RejectionReasons}] " +
                "OriginalSuggestions={OriginalSuggestions} OriginalQuestion={OriginalQuestion}",
                correlationId,
                validated.Error ?? parsed.Error ?? "unknown",
                validated.ReceivedCount,
                validated.AcceptedCount,
                validated.RejectedCount,
                string.Join(" | ", validated.RejectionReasons),
                TruncateForLog(originalSuggestionsForLog, 800),
                TruncateForLog(userMessage, 500));

            // All chips invalid but the clarify message is still a business question → custom-only UI.
            if (parsed.Success &&
                validated.RejectedCount > 0 &&
                ClarificationSuggestionValidator.IsAnswerableClarificationMessage(parsed.Message))
            {
                _logger.LogInformation(
                    "[CLARIFICATION] CorrelationId={CorrelationId} CustomReplyOnly=true Message={Message}",
                    correlationId,
                    TruncateForLog(parsed.Message, 500));
                return CreateResponse(
                    session,
                    parsed.Message,
                    outboundWasSafe: outboundWasSafe,
                    selectedTool: "clarify_crm",
                    responseType: "clarification",
                    suggestions: Array.Empty<ClarificationSuggestionDto>());
            }

            // Incomplete/placeholder suggestions are a planning UX failure — not an unsupported CRM capability.
            return CreateResponse(
                session,
                "I could not prepare clarification options for this question. Please rephrase and try again.",
                outboundWasSafe: outboundWasSafe,
                selectedTool: "clarify_crm",
                responseType: "answer");
        }

        if (validated.RejectedCount > 0)
        {
            _logger.LogWarning(
                "[CLARIFICATION] CorrelationId={CorrelationId} PartialSuggestionFilter " +
                "Received={Received} Accepted={Accepted} Rejected={Rejected} RejectionReasons=[{RejectionReasons}] " +
                "OriginalSuggestions={OriginalSuggestions}",
                correlationId,
                validated.ReceivedCount,
                validated.AcceptedCount,
                validated.RejectedCount,
                string.Join(" | ", validated.RejectionReasons),
                TruncateForLog(originalSuggestionsForLog, 800));
        }

        _logger.LogInformation(
            "[CLARIFICATION] CorrelationId={CorrelationId} OriginalQuestion={OriginalQuestion} " +
            "Received={Received} Accepted={Accepted} Rejected={Rejected} SuggestedQuestions=[{SuggestedQuestions}]",
            correlationId,
            TruncateForLog(userMessage, 500),
            validated.ReceivedCount,
            validated.AcceptedCount,
            validated.RejectedCount,
            string.Join(" | ", validated.Suggestions.Select(s => s.Question)));

        return CreateResponse(
            session,
            parsed.Message,
            outboundWasSafe: outboundWasSafe,
            selectedTool: "clarify_crm",
            responseType: "clarification",
            suggestions: validated.Suggestions);
    }

    private sealed class QueryPlanAttemptResult
    {
        public bool Success { get; init; }
        public ValidatedFetchXmlQuery? Query { get; init; }
        public QueryPlanFailureKind FailureKind { get; init; }
        public string? ErrorCode { get; init; }
        public string? ErrorMessage { get; init; }
        public string? UserMessage { get; init; }
        public string? Field { get; init; }
        public string? Entity { get; init; }
    }

    /// <summary>
    /// Parses query_crm FetchXML arguments and validates them without CRM execution.
    /// </summary>
    private async Task<QueryPlanAttemptResult> TryValidateFetchXmlAsync(
        FunctionCallContent call,
        string correlationId,
        CancellationToken cancellationToken)
    {
        var parsed = FetchXmlArgumentParser.ParseFromToolArguments(call.Arguments);
        if (!parsed.Success || parsed.Request is null)
        {
            var msg = parsed.Error ?? "Invalid query_crm arguments.";
            var (kind, code, field) = QueryPlanFailureClassifier.Classify(
                QueryPlanValidationStage.Parse, msg);
            return new QueryPlanAttemptResult
            {
                Success = false,
                FailureKind = kind,
                ErrorCode = parsed.ErrorCode ?? code,
                ErrorMessage = msg,
                UserMessage = msg,
                Field = field
            };
        }

        _logger.LogInformation(
            "[QUERY PLAN] CorrelationId={CorrelationId} ResultMode={ResultMode} FetchLength={FetchLength} Arguments={Arguments}",
            correlationId,
            parsed.Request.ResultMode,
            parsed.Request.FetchXml.Length,
            FormatToolArguments(call.Arguments));

        var validated = await _fetchXmlExecutor.ValidateAsync(parsed.Request, cancellationToken);
        if (!validated.Success || validated.Query is null)
        {
            var msg = validated.ErrorMessage ?? "Invalid CRM FetchXML query.";
            var stage = validated.Stage switch
            {
                "EntityApproval" => QueryPlanValidationStage.EntityApproval,
                "Authorization" => QueryPlanValidationStage.Authorization,
                "MetadataLoad" => QueryPlanValidationStage.MetadataLoad,
                "Parse" => QueryPlanValidationStage.Parse,
                _ => QueryPlanValidationStage.FetchXmlBuild
            };
            var (kind, code, field) = QueryPlanFailureClassifier.Classify(
                stage, msg, validated.Entity, validated.Field);
            if (!string.IsNullOrWhiteSpace(validated.ErrorCode))
            {
                code = validated.ErrorCode!;
            }

            return new QueryPlanAttemptResult
            {
                Success = false,
                FailureKind = kind,
                ErrorCode = code,
                ErrorMessage = msg,
                UserMessage = msg,
                Field = field ?? validated.Field,
                Entity = validated.Entity
            };
        }

        return new QueryPlanAttemptResult
        {
            Success = true,
            Query = validated.Query
        };
    }

    private static FunctionCallContent EnsureCallId(FunctionCallContent call)
    {
        if (!string.IsNullOrWhiteSpace(call.CallId))
        {
            return call;
        }

        // Provider should supply CallId; if missing, assign a request-local id used for both
        // the echoed assistant tool-call and the matching tool result (never fabricate across turns).
        return new FunctionCallContent(
            Guid.NewGuid().ToString("N"),
            call.Name,
            call.Arguments is null
                ? null
                : new Dictionary<string, object?>(call.Arguments, StringComparer.OrdinalIgnoreCase))
        {
            Exception = call.Exception,
            InformationalOnly = call.InformationalOnly
        };
    }

    private static void AppendAssistantToolCall(
        List<ChatMessage> workingMessages,
        ChatResponse modelResponse,
        FunctionCallContent call)
    {
        // Prefer the provider's assistant message(s) so tool-call IDs stay aligned.
        var assistantMessages = modelResponse.Messages
            .Where(m => m.Role == ChatRole.Assistant)
            .ToList();

        if (assistantMessages.Count > 0)
        {
            // Replace any FunctionCallContent missing CallId with the ensured call.
            foreach (var message in assistantMessages)
            {
                var contents = message.Contents.Select(c =>
                {
                    if (c is FunctionCallContent fcc &&
                        string.Equals(fcc.Name, call.Name, StringComparison.OrdinalIgnoreCase) &&
                        string.IsNullOrWhiteSpace(fcc.CallId))
                    {
                        return (AIContent)call;
                    }

                    return c;
                }).ToList();

                workingMessages.Add(new ChatMessage(ChatRole.Assistant, contents));
            }

            return;
        }

        workingMessages.Add(new ChatMessage(ChatRole.Assistant, [call]));
    }

    internal static string FingerprintToolArguments(IDictionary<string, object?>? arguments)
    {
        if (arguments is null || arguments.Count == 0)
        {
            return "{}";
        }

        try
        {
            var ordered = arguments
                .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(kv => kv.Key, kv => NormalizeFingerprintValue(kv.Value), StringComparer.OrdinalIgnoreCase);
            return System.Text.Json.JsonSerializer.Serialize(ordered);
        }
        catch
        {
            return string.Join("|", arguments
                .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                .Select(kv => $"{kv.Key}={kv.Value}"));
        }
    }

    private static object? NormalizeFingerprintValue(object? value) =>
        value switch
        {
            null => null,
            string s => s.Trim(),
            System.Text.Json.JsonElement je => je.ToString(),
            _ => Convert.ToString(value, CultureInfo.InvariantCulture)
        };

    private static void AccumulateUsage(
        ChatResponse modelResponse,
        ref long totalPromptTokens,
        ref long totalCompletionTokens)
    {
        var usage = modelResponse.Usage;
        if (usage?.InputTokenCount is long input)
        {
            totalPromptTokens += input;
        }

        if (usage?.OutputTokenCount is long output)
        {
            totalCompletionTokens += output;
        }
    }

    private void LogSelfCorrectionOutcome(
        string correlationId,
        string outcome,
        int attempts,
        bool executedFetchXml,
        long totalPromptTokens,
        long totalCompletionTokens)
    {
        _logger.LogInformation(
            "[AI SELF-CORRECTION] CorrelationId={CorrelationId} Outcome={Outcome} Attempts={Attempts} " +
            "ExecutedFetchXml={ExecutedFetchXml} TotalPromptTokens={TotalPromptTokens} TotalCompletionTokens={TotalCompletionTokens}",
            correlationId,
            outcome,
            attempts,
            executedFetchXml,
            totalPromptTokens,
            totalCompletionTokens);
    }

    private sealed class FetchXmlExecutionAttemptResult
    {
        public bool Success { get; init; }
        public bool IsRecoverable { get; init; }
        public ChatResponseDto? Response { get; init; }
        public string? UserMessage { get; init; }
        public string? SanitizedFeedback { get; init; }
        public string? ErrorCode { get; init; }
        public string? DynamicsErrorCode { get; init; }
        public string? Field { get; init; }
    }

    private async Task<FetchXmlExecutionAttemptResult> TryExecuteValidatedFetchXmlAsync(
        ConversationSession session,
        ValidatedFetchXmlQuery query,
        string correlationId,
        bool outboundWasSafe,
        CancellationToken cancellationToken)
    {
        Guid? whoAmIUserId = null;
        if (_crmOptions.EnableLiveIntegration && !string.IsNullOrWhiteSpace(_crmOptions.BaseUrl))
        {
            var who = await _connectivity.WhoAmIAsync(cancellationToken);
            if (!who.Success || who.UserId is null)
            {
                return new FetchXmlExecutionAttemptResult
                {
                    Success = false,
                    IsRecoverable = false,
                    UserMessage = $"CRM identity verification failed before query_crm: {who.Message}",
                    ErrorCode = "AUTHORIZATION_DENIED"
                };
            }

            whoAmIUserId = who.UserId;
        }

        GenericCrmQueryResult result;
        try
        {
            result = await _fetchXmlExecutor.ExecuteAsync(query, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (TimeoutException ex)
        {
            _logger.LogWarning(ex, "query_crm FetchXML timed out. CorrelationId={CorrelationId}", correlationId);
            return new FetchXmlExecutionAttemptResult
            {
                Success = false,
                IsRecoverable = false,
                UserMessage = "The CRM request timed out. Please try again.",
                ErrorCode = "CRM_TIMEOUT"
            };
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "query_crm FetchXML network failure. CorrelationId={CorrelationId}", correlationId);
            return new FetchXmlExecutionAttemptResult
            {
                Success = false,
                IsRecoverable = false,
                UserMessage = "The CRM service could not be reached. Please try again later.",
                ErrorCode = "CRM_NETWORK_FAILURE"
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "query_crm FetchXML execution failed. CorrelationId={CorrelationId}", correlationId);
            return new FetchXmlExecutionAttemptResult
            {
                Success = false,
                IsRecoverable = false,
                UserMessage = "Failed to execute the CRM FetchXML query.",
                ErrorCode = "CRM_EXECUTION_FAILURE"
            };
        }

        if (!string.IsNullOrWhiteSpace(result.RelativeRequestUri))
        {
            _logger.LogInformation(
                "[FETCHXML] CorrelationId={CorrelationId} Entity={Entity} Operation={Operation} UriMarker={UriMarker}",
                correlationId,
                result.Entity,
                result.Operation,
                TruncateForLog(result.RelativeRequestUri, 500));
        }

        if (!string.IsNullOrWhiteSpace(result.Message) &&
            string.IsNullOrWhiteSpace(result.RelativeRequestUri) &&
            result.Items.Count == 0 &&
            result.Count is null)
        {
            _logger.LogWarning(
                "[FETCHXML] CorrelationId={CorrelationId} Entity={Entity} Operation={Operation} " +
                "ExecutionFailed=true Recoverable={Recoverable} DynamicsCode={DynamicsCode} Error={Error}",
                correlationId,
                query.EntityLogicalName,
                result.Operation,
                result.IsRecoverableFetchXmlError,
                result.DynamicsErrorCode,
                TruncateForLog(result.Message, 500));

            return new FetchXmlExecutionAttemptResult
            {
                Success = false,
                IsRecoverable = result.IsRecoverableFetchXmlError,
                UserMessage = result.Message,
                SanitizedFeedback = result.SanitizedCorrectionFeedback ?? result.Message,
                ErrorCode = result.CorrectionErrorCode
                    ?? result.DynamicsErrorCode
                    ?? "CRM_EXECUTION_FAILURE",
                DynamicsErrorCode = result.DynamicsErrorCode
            };
        }

        _logger.LogInformation(
            "[CRM RESPONSE] CorrelationId={CorrelationId} Entity={Entity} Operation={Operation} Rows={Rows} " +
            "ResultMode={ResultMode} Provider={Provider}",
            correlationId,
            result.Entity,
            result.Operation,
            result.Items.Count,
            result.ResultMode ?? "complete",
            result.ProviderName);

        return new FetchXmlExecutionAttemptResult
        {
            Success = true,
            Response = CreateResponse(
                session,
                reply: string.Empty,
                selectedTool: "query_crm",
                data: new
                {
                    entity = result.Entity,
                    operation = result.Operation.ToString().ToLowerInvariant(),
                    resultMode = result.ResultMode ?? query.ResultMode.ToString().ToLowerInvariant(),
                    limitation = result.Limitation ?? query.Limitation,
                    appliedFilters = result.AppliedFilterSummary,
                    sort = result.SortSummary,
                    limit = result.Limit,
                    count = result.Count,
                    countMayBeCapped = result.CountMayBeCapped,
                    joinedRowCount = result.Items.Count,
                    primaryIdAttribute = result.PrimaryIdAttribute,
                    primaryNameAttribute = result.PrimaryNameAttribute,
                    columns = result.Columns.Select(c => new
                    {
                        key = c.Key,
                        label = c.Label,
                        kind = c.Kind,
                        logicalName = c.LogicalName
                    }),
                    records = result.Items
                },
                whoAmI: whoAmIUserId,
                outboundWasSafe: outboundWasSafe,
                crmProvider: result.ProviderName)
        };
    }

    /// <summary>
    /// Builds outbound Groq messages: system prompt + privacy-safe dialogue turns (chronological).
    /// The last entry must be the current user request. No tool/CRM record content.
    /// </summary>
    internal static List<ChatMessage> BuildOutboundHistory(IReadOnlyList<ConversationTurn> turnsChronological)
    {
        if (turnsChronological is null || turnsChronological.Count == 0)
        {
            throw new ArgumentException("At least the current user question is required.", nameof(turnsChronological));
        }

        if (turnsChronological[^1].Role != ConversationTurnRole.User)
        {
            throw new ArgumentException("Outbound history must end with the current user question.", nameof(turnsChronological));
        }

        var messages = new List<ChatMessage>(1 + turnsChronological.Count)
        {
            new(ChatRole.System, SystemPrompt)
        };

        foreach (var turn in turnsChronological)
        {
            if (turn.Role == ConversationTurnRole.User)
            {
                messages.Add(new ChatMessage(ChatRole.User, turn.Text));
                continue;
            }

            // Defense in depth: never emit a stored assistant turn that fails the privacy filter.
            if (!AssistantHistoryPrivacyFilter.IsAllowed(turn.Kind, turn.Text, structuredData: null))
            {
                continue;
            }

            messages.Add(new ChatMessage(ChatRole.Assistant, turn.Text));
        }

        if (messages.Count < 2 || messages[^1].Role != ChatRole.User)
        {
            throw new InvalidOperationException("Outbound history must include the current user question.");
        }

        return messages;
    }

    /// <summary>Convenience overload: current user question only (tests / first turn).</summary>
    internal static List<ChatMessage> BuildOutboundHistory(string newUserMessage) =>
        BuildOutboundHistory(
        [
            new ConversationTurn
            {
                Role = ConversationTurnRole.User,
                Text = newUserMessage,
                Kind = "user"
            }
        ]);

    /// <summary>
    /// Convenience overload: user-question strings only (no prior assistant turns).
    /// </summary>
    internal static List<ChatMessage> BuildOutboundHistory(IReadOnlyList<string> userQuestionsChronological)
    {
        if (userQuestionsChronological is null || userQuestionsChronological.Count == 0)
        {
            throw new ArgumentException("At least the current user question is required.", nameof(userQuestionsChronological));
        }

        var turns = userQuestionsChronological
            .Select(q => new ConversationTurn
            {
                Role = ConversationTurnRole.User,
                Text = q,
                Kind = "user"
            })
            .ToArray();
        return BuildOutboundHistory(turns);
    }

    /// <summary>
    /// Selects prior + current turns within message-count and character budgets.
    /// Drops oldest first. Never drops or truncates the current user question.
    /// </summary>
    internal static ConversationHistoryWindow SelectConversationWindow(
        IReadOnlyList<ConversationTurn> previousTurns,
        string currentQuestion,
        int maxMessages,
        int maxHistoryCharacters)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(currentQuestion);
        maxMessages = Math.Max(1, maxMessages);
        maxHistoryCharacters = Math.Max(currentQuestion.Length, maxHistoryCharacters);

        var current = new ConversationTurn
        {
            Role = ConversationTurnRole.User,
            Text = currentQuestion.Trim(),
            Kind = "user"
        };

        var prior = (previousTurns ?? Array.Empty<ConversationTurn>())
            .Where(t =>
                t.Role == ConversationTurnRole.User ||
                AssistantHistoryPrivacyFilter.IsAllowed(t.Kind, t.Text, structuredData: null))
            .ToList();

        var maxPrior = maxMessages - 1;
        var start = maxPrior <= 0 || prior.Count == 0
            ? prior.Count
            : Math.Max(0, prior.Count - maxPrior);

        var window = prior.Skip(start).ToList();
        window.Add(current);

        var truncated = start > 0;
        while (window.Count > 1 &&
               window.Sum(t => t.Text.Length) > maxHistoryCharacters)
        {
            window.RemoveAt(0);
            truncated = true;
        }

        var previousUser = window.Count(t => t.Role == ConversationTurnRole.User) - 1;
        var previousAssistant = window.Count(t => t.Role == ConversationTurnRole.Assistant);
        var historyTokenEstimate = EstimateHistoryTokens(window);

        return new ConversationHistoryWindow
        {
            Turns = window,
            PreviousUserMessagesIncluded = Math.Max(0, previousUser),
            PreviousAssistantMessagesIncluded = previousAssistant,
            HistoryTokenEstimate = historyTokenEstimate,
            HistoryTruncated = truncated,
            AssistantHistoryPrivacyFilterApplied = true
        };
    }

    /// <summary>
    /// Selects prior + current user questions within count and character budgets (user-only helper).
    /// </summary>
    internal static IReadOnlyList<string> SelectUserQuestionWindow(
        IReadOnlyList<string> previousQuestions,
        string currentQuestion,
        int maxUserQuestions,
        int maxUserHistoryCharacters)
    {
        var priorTurns = (previousQuestions ?? Array.Empty<string>())
            .Select(q => new ConversationTurn
            {
                Role = ConversationTurnRole.User,
                Text = q,
                Kind = "user"
            })
            .ToArray();

        return SelectConversationWindow(
                priorTurns,
                currentQuestion,
                maxUserQuestions,
                maxUserHistoryCharacters)
            .Turns
            .Where(t => t.Role == ConversationTurnRole.User)
            .Select(t => t.Text)
            .ToArray();
    }

    internal static int EstimateHistoryTokens(IReadOnlyList<ConversationTurn> turns)
    {
        // Rough heuristic for diagnostics only (~4 chars/token); not billed usage.
        var chars = turns.Sum(t => t.Text.Length);
        return Math.Max(1, (chars + 3) / 4);
    }

    internal sealed class ConversationHistoryWindow
    {
        public required IReadOnlyList<ConversationTurn> Turns { get; init; }
        public int PreviousUserMessagesIncluded { get; init; }
        public int PreviousAssistantMessagesIncluded { get; init; }
        public int HistoryTokenEstimate { get; init; }
        public bool HistoryTruncated { get; init; }
        public bool AssistantHistoryPrivacyFilterApplied { get; init; }
    }

    private void LogAiRequest(
        string correlationId,
        string conversationId,
        string userMessage,
        string questionLanguage,
        ConversationHistoryWindow selectedWindow,
        IReadOnlyList<ChatMessage> outboundHistory)
    {
        var requestPayloadChars = outboundHistory.Sum(m => m.Text?.Length ?? 0);
        var userMessageCount = outboundHistory.Count(m => m.Role == ChatRole.User);
        var assistantMessageCount = outboundHistory.Count(m => m.Role == ChatRole.Assistant);
        var toolContentCount = outboundHistory.Count(m =>
            m.Contents.Any(c => c is FunctionCallContent or FunctionResultContent));

        _logger.LogInformation(
            "[AI REQUEST] Provider={Provider} CorrelationId={CorrelationId} ConversationId={ConversationId} " +
            "OriginalQuestion={OriginalQuestion} QuestionLanguage={QuestionLanguage} " +
            "PreviousUserMessagesIncluded={PreviousUserMessagesIncluded} " +
            "PreviousAssistantMessagesIncluded={PreviousAssistantMessagesIncluded} " +
            "HistoryTokenEstimate={HistoryTokenEstimate} HistoryTruncated={HistoryTruncated} " +
            "AssistantHistoryPrivacyFilterApplied={AssistantHistoryPrivacyFilterApplied} " +
            "UserQuestionsInOutbound={UserQuestionsInOutbound} AssistantMessagesInOutbound={AssistantMessagesInOutbound} " +
            "RequestPayloadCharacters={RequestPayloadCharacters} OutboundMessageCount={OutboundMessageCount} " +
            "ToolContentMessages={ToolContentMessages} HasCatalog=false Architecture=LlmFirstPrivacySafeHistory " +
            "HasQueryCrmTool=true HasClarifyCrmTool=true HasUnsupportedTool=true ToolMode=RequireAny",
            _aiOptions.Provider ?? "Groq",
            correlationId,
            conversationId,
            TruncateForLog(userMessage, 500),
            questionLanguage,
            selectedWindow.PreviousUserMessagesIncluded,
            selectedWindow.PreviousAssistantMessagesIncluded,
            selectedWindow.HistoryTokenEstimate,
            selectedWindow.HistoryTruncated,
            selectedWindow.AssistantHistoryPrivacyFilterApplied,
            userMessageCount,
            assistantMessageCount,
            requestPayloadChars,
            outboundHistory.Count,
            toolContentCount);
    }

    internal static string FormatClarificationHistoryText(
        string message,
        IReadOnlyList<ClarificationSuggestionDto>? suggestions)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append(message.Trim());
        if (suggestions is { Count: > 0 })
        {
            sb.AppendLine();
            sb.AppendLine();
            sb.Append("Clarification options:");
            foreach (var suggestion in suggestions)
            {
                sb.AppendLine();
                sb.Append("- ");
                sb.Append(suggestion.Label.Trim());
                sb.Append(": ");
                sb.Append(suggestion.Question.Trim());
            }
        }

        var text = sb.ToString().Trim();
        return text.Length <= 1500 ? text : text[..1500];
    }

    private void TryStoreSafeAssistantHistory(
        ConversationSession session,
        string reply,
        string? responseType,
        string? selectedTool,
        object? structuredData,
        IReadOnlyList<ClarificationSuggestionDto>? suggestions)
    {
        var type = (responseType ?? "answer").Trim();
        string? kind = null;
        string? text = null;

        // Privacy-safe schema synopsis for successful query_crm — never stores records/values.
        if (structuredData is not null &&
            string.Equals(selectedTool, "query_crm", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(type, "answer", StringComparison.OrdinalIgnoreCase))
        {
            text = TryFormatPrivacySafeResultSynopsis(structuredData);
            if (!string.IsNullOrWhiteSpace(text))
            {
                kind = "answer";
                _logger.LogInformation(
                    "[CONVERSATION HISTORY] Storing privacy-safe query_crm synopsis Length={Length} FollowUpContext=available",
                    text.Length);
            }
            else
            {
                _logger.LogInformation(
                    "[CONVERSATION HISTORY] query_crm structuredData present but synopsis unavailable; FollowUpContext=missing");
                return;
            }
        }
        else if (structuredData is not null)
        {
            return;
        }
        else if (string.Equals(type, "clarification", StringComparison.OrdinalIgnoreCase) &&
                 string.Equals(selectedTool, "clarify_crm", StringComparison.OrdinalIgnoreCase))
        {
            kind = "clarification";
            text = FormatClarificationHistoryText(reply, suggestions);
        }
        else if (string.Equals(type, "unsupported", StringComparison.OrdinalIgnoreCase) &&
                 string.Equals(selectedTool, "unsupported_crm_request", StringComparison.OrdinalIgnoreCase))
        {
            kind = "unsupported";
            text = UnsupportedFeatureUserMessage;
        }
        else if (string.Equals(type, "answer", StringComparison.OrdinalIgnoreCase) &&
                 string.Equals(selectedTool, "clarify_crm", StringComparison.OrdinalIgnoreCase) &&
                 !string.IsNullOrWhiteSpace(reply))
        {
            // Clarify fallback without suggestion chips — still record-free dialogue context.
            kind = "answer";
            text = reply.Trim();
            if (text.Length > 1500)
            {
                text = text[..1500];
            }
        }
        else if (string.Equals(type, "answer", StringComparison.OrdinalIgnoreCase) &&
                 string.IsNullOrWhiteSpace(selectedTool) &&
                 !string.IsNullOrWhiteSpace(reply))
        {
            // Plain Auto-mode conversational reply (greeting / structural follow-up) — record-free.
            kind = "answer";
            text = reply.Trim();
            if (text.Length > 1500)
            {
                text = text[..1500];
            }
        }

        if (kind is null || text is null)
        {
            return;
        }

        if (!AssistantHistoryPrivacyFilter.IsAllowed(kind, text, structuredData: null))
        {
            _logger.LogWarning(
                "[CONVERSATION HISTORY] Skipped unsafe assistant turn Kind={Kind} Length={Length}",
                kind,
                text.Length);
            return;
        }

        session.TryAppendSafeAssistantTurn(kind, text);
    }

    /// <summary>
    /// Builds a compact, record-free description of the last CRM result for Groq follow-ups.
    /// Includes entity/operation/columns/filters/counts only — never row values.
    /// </summary>
    internal static string? TryFormatPrivacySafeResultSynopsis(object structuredData)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(
                System.Text.Json.JsonSerializer.Serialize(structuredData));
            var root = doc.RootElement;
            if (root.ValueKind != System.Text.Json.JsonValueKind.Object)
            {
                return null;
            }

            static string? ReadString(System.Text.Json.JsonElement el, string name) =>
                el.TryGetProperty(name, out var p) && p.ValueKind == System.Text.Json.JsonValueKind.String
                    ? p.GetString()
                    : null;

            var entity = ReadString(root, "entity") ?? "(unknown)";
            var operation = ReadString(root, "operation") ?? "(unknown)";
            var resultMode = ReadString(root, "resultMode");
            var rowCount = root.TryGetProperty("joinedRowCount", out var jc) && jc.TryGetInt32(out var jn)
                ? jn
                : root.TryGetProperty("count", out var c) && c.TryGetInt32(out var cn) ? cn : (int?)null;

            var sb = new System.Text.StringBuilder();
            sb.Append("Previous CRM result (schema only; no record values): entity=");
            sb.Append(entity);
            sb.Append("; operation=");
            sb.Append(operation);
            if (!string.IsNullOrWhiteSpace(resultMode))
            {
                sb.Append("; resultMode=");
                sb.Append(resultMode);
            }

            if (rowCount is not null)
            {
                sb.Append("; rowCount=");
                sb.Append(rowCount.Value);
            }

            if (root.TryGetProperty("columns", out var cols) &&
                cols.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                sb.Append("; columns=[");
                var first = true;
                foreach (var col in cols.EnumerateArray().Take(24))
                {
                    var key = ReadString(col, "key");
                    if (string.IsNullOrWhiteSpace(key))
                    {
                        continue;
                    }

                    var label = ReadString(col, "label");
                    if (!first)
                    {
                        sb.Append(", ");
                    }

                    first = false;
                    sb.Append(key);
                    if (!string.IsNullOrWhiteSpace(label) &&
                        !label.Equals(key, StringComparison.OrdinalIgnoreCase))
                    {
                        sb.Append(":\"");
                        sb.Append(label.Replace('"', '\''));
                        sb.Append('"');
                    }
                }

                sb.Append(']');
            }

            if (root.TryGetProperty("appliedFilters", out var filters) &&
                filters.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                var filterTexts = filters.EnumerateArray()
                    .Where(f => f.ValueKind == System.Text.Json.JsonValueKind.String)
                    .Select(f => f.GetString())
                    .Where(s => !string.IsNullOrWhiteSpace(s))
                    .Take(8)
                    .ToArray();
                if (filterTexts.Length > 0)
                {
                    sb.Append("; filters=");
                    sb.Append(string.Join(" | ", filterTexts));
                }
            }

            var text = sb.ToString().Trim();
            if (text.Length > 1500)
            {
                text = text[..1500];
            }

            // Guard: never include a records array dump.
            if (text.Contains("\"records\"", StringComparison.OrdinalIgnoreCase) ||
                AssistantHistoryPrivacyFilter.ContainsDisallowedPayloadMarkers(text))
            {
                return null;
            }

            return text;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }


    private void LogAiResponse(
        string correlationId,
        ChatResponse modelResponse,
        IReadOnlyList<FunctionCallContent> functionCalls,
        int attempt = 1,
        int maxAttempts = 1)
    {
        var usage = modelResponse.Usage;
        var inputTokens = usage?.InputTokenCount;
        var outputTokens = usage?.OutputTokenCount;
        var totalTokens = usage?.TotalTokenCount;
        long? cachedTokens = null;
        if (usage?.AdditionalCounts is not null)
        {
            foreach (var kv in usage.AdditionalCounts)
            {
                if (kv.Key.Contains("cache", StringComparison.OrdinalIgnoreCase))
                {
                    cachedTokens = kv.Value;
                    break;
                }
            }
        }

        if (functionCalls.Count == 0)
        {
            _logger.LogInformation(
                "[GROQ RESPONSE] CorrelationId={CorrelationId} Attempt={Attempt}/{MaxAttempts} ProviderStatus=ok ToolCall=(none) Text={Text} " +
                "PromptTokens={PromptTokens} CompletionTokens={CompletionTokens} TotalTokens={TotalTokens} CachedTokens={CachedTokens}",
                correlationId,
                attempt,
                maxAttempts,
                TruncateForLog(modelResponse.Text, 800),
                inputTokens,
                outputTokens,
                totalTokens,
                cachedTokens);
            return;
        }

        var call = functionCalls[0];
        _logger.LogInformation(
            "[GROQ RESPONSE] CorrelationId={CorrelationId} Attempt={Attempt}/{MaxAttempts} ProviderStatus=ok ToolName={ToolName} Entity={Entity} Operation={Operation} " +
            "RawToolArguments={RawToolArguments} PromptTokens={PromptTokens} CompletionTokens={CompletionTokens} " +
            "TotalTokens={TotalTokens} CachedTokens={CachedTokens}",
            correlationId,
            attempt,
            maxAttempts,
            call.Name,
            GetArgValue(call.Arguments, "entity"),
            GetArgValue(call.Arguments, "operation"),
            FormatToolArguments(call.Arguments),
            inputTokens,
            outputTokens,
            totalTokens,
            cachedTokens);
    }

    private static bool IsDevelopmentEnvironment() =>
        string.Equals(
            Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT"),
            "Development",
            StringComparison.OrdinalIgnoreCase);

    private static string TruncateForLog(string? value, int max)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        return value.Length <= max ? value : value[..max] + "…";
    }

    private async Task<ChatResponse> GetResponseWithRetryAsync(
        IReadOnlyList<ChatMessage> messages,
        ChatOptions chatOptions,
        string correlationId,
        int requestPayloadCharacters,
        CancellationToken cancellationToken)
    {
        var timeoutSeconds = Math.Clamp(_aiOptions.ProviderTimeoutSeconds, 8, 90);
        // Transient HTTP 429/503 only — client timeouts do not consume a second full wait.
        const int maxAttempts = 2;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            attemptCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
            var sw = Stopwatch.StartNew();
            try
            {
                return await _chatClient.GetResponseAsync(messages, chatOptions, attemptCts.Token);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                sw.Stop();
                throw;
            }
            catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                sw.Stop();
                LogProviderFailure(
                    correlationId,
                    AiProviderFailureKind.ClientTimeout,
                    attempt,
                    timeoutSeconds,
                    sw.ElapsedMilliseconds,
                    requestPayloadCharacters,
                    ex);
                throw new TimeoutException(
                    $"AI provider did not respond within {timeoutSeconds}s.",
                    ex);
            }
            catch (Exception ex)
            {
                sw.Stop();
                var kind = AiProviderFailureClassifier.Classify(ex);
                LogProviderFailure(
                    correlationId,
                    kind,
                    attempt,
                    timeoutSeconds,
                    sw.ElapsedMilliseconds,
                    requestPayloadCharacters,
                    ex);

                if (attempt < maxAttempts && AiProviderFailureClassifier.IsTransient(kind))
                {
                    var delay = AiProviderFailureClassifier.GetTransientRetryDelay(ex, attempt);
                    _logger.LogWarning(
                        "[AI RETRY] CorrelationId={CorrelationId} FailureKind={FailureKind} Attempt={Attempt}/{Max} " +
                        "DelayMs={DelayMs} ProviderTimeoutSeconds={ProviderTimeoutSeconds}",
                        correlationId,
                        kind,
                        attempt,
                        maxAttempts,
                        (int)delay.TotalMilliseconds,
                        timeoutSeconds);
                    await Task.Delay(delay, cancellationToken);
                    continue;
                }

                throw;
            }
        }

        throw new InvalidOperationException("AI request failed after retries.");
    }

    private void LogProviderFailure(
        string correlationId,
        AiProviderFailureKind failureKind,
        int attempt,
        int providerTimeoutSeconds,
        long elapsedMs,
        int requestPayloadCharacters,
        Exception ex)
    {
        _logger.LogWarning(
            ex,
            "[AI ERROR] CorrelationId={CorrelationId} FailureKind={FailureKind} Attempt={Attempt} " +
            "ProviderTimeoutSeconds={ProviderTimeoutSeconds} ElapsedMs={ElapsedMs} " +
            "RequestPayloadCharacters={RequestPayloadCharacters}",
            correlationId,
            failureKind,
            attempt,
            providerTimeoutSeconds,
            elapsedMs,
            requestPayloadCharacters);
    }

    private static string FormatToolArguments(IDictionary<string, object?>? arguments)
    {
        if (arguments is null || arguments.Count == 0)
        {
            return "{}";
        }

        try
        {
            return System.Text.Json.JsonSerializer.Serialize(
                arguments.ToDictionary(kv => kv.Key, kv => kv.Value));
        }
        catch
        {
            return string.Join("; ", arguments.Select(kv => $"{kv.Key}={kv.Value}"));
        }
    }

    private static bool LooksLikeWriteRequest(string text) =>
        !string.IsNullOrWhiteSpace(text) && WriteIntentPattern().IsMatch(text);

    private static string RefuseWrite() =>
        "Refused. This assistant is read-only and will not create, update, or delete CRM records.";

    [System.Text.RegularExpressions.GeneratedRegex(
        @"\b(delete|remove|create|update|insert|upsert|assign|destroy)\b",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant)]
    private static partial System.Text.RegularExpressions.Regex WriteIntentPattern();

    private static string? GetArgValue(IDictionary<string, object?>? arguments, string key)
    {
        if (arguments is null || !arguments.TryGetValue(key, out var raw) || raw is null)
        {
            return null;
        }

        return Convert.ToString(raw, CultureInfo.InvariantCulture);
    }

    private ChatResponseDto CreateResponse(
        ConversationSession session,
        string reply,
        bool outboundWasSafe,
        string? selectedTool = null,
        object? data = null,
        Guid? whoAmI = null,
        string? crmProvider = null,
        string? responseType = "answer",
        IReadOnlyList<ClarificationSuggestionDto>? suggestions = null)
    {
        TryStoreSafeAssistantHistory(session, reply, responseType, selectedTool, data, suggestions);

        return new ChatResponseDto
        {
            ConversationId = session.ConversationId,
            Reply = reply,
            ProviderHint = ResolveProviderHint(),
            ResponseType = responseType,
            Suggestions = suggestions,
            PrivacySafeToolExecution = true,
            SelectedTool = selectedTool,
            StructuredData = data,
            CrmWhoAmIUserId = whoAmI,
            OutboundAiContainedCrmRecords = !outboundWasSafe,
            CrmDataProvider = crmProvider
        };
    }

    private string ResolveProviderHint()
    {
        var provider = (_aiOptions.Provider ?? "Groq").Trim();
        if (provider.Equals("OpenAI", StringComparison.OrdinalIgnoreCase))
        {
            return "OpenAI (tool selection only; CRM replies local)";
        }

        if (provider.Equals("Gemini", StringComparison.OrdinalIgnoreCase))
        {
            return "Gemini (tool selection only; CRM replies local)";
        }

        if (provider.Equals("OpenRouter", StringComparison.OrdinalIgnoreCase))
        {
            return "OpenRouter (tool selection only; CRM replies local)";
        }

        if (provider.Equals("Ollama", StringComparison.OrdinalIgnoreCase))
        {
            return "Ollama (tool selection only; CRM replies local)";
        }

        return "Groq (tool selection only; CRM replies local)";
    }
}
