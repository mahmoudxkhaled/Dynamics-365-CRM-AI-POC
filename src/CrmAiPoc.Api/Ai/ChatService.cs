using System.Globalization;
using System.Text.Json;
using CrmAiPoc.Api.Configuration;
using CrmAiPoc.Api.Crm;
using CrmAiPoc.Api.Crm.Dynamics;
using CrmAiPoc.Api.Crm.Models;
using CrmAiPoc.Api.Crm.Generic;
using CrmAiPoc.Api.Crm.Metadata;
using CrmAiPoc.Api.Crm.Query;
using CrmAiPoc.Api.Crm.Validation;
using System.Diagnostics;
using CrmAiPoc.Api.Diagnostics;
using CrmAiPoc.Api.Models;
using CrmAiPoc.Api.Security;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace CrmAiPoc.Api.Ai;

public sealed partial class ChatService
{
    private static readonly string SystemPrompt = """
        You are a read-only Dynamics 365 CRM routing assistant.
        Supported languages: English and Arabic.
        You have EXACTLY ONE CRM data tool: query_crm.
        Always call query_crm for CRM reads (list, count, filters, sort, dates, pagination, one-hop relationships).
        Do not invent CRM records. Never emit OData, SQL, FetchXML, or URLs.

        Approved entities (config-driven): account, contact, salesorder, lead (and any others enabled by backend policy).
        CRITICAL: Choose entity from the LATEST user message only. Do not reuse entity from earlier turns.

        query_crm examples:
        - latest 5 sales orders → entity=salesorder, operation=list, sortField=createdon, sortDirection=desc, limit=5
        - latest 5 accounts → entity=account, operation=list, sortField=createdon, sortDirection=desc, limit=5
        - latest 5 contacts → entity=contact, operation=list, sortField=createdon, sortDirection=desc, limit=5
        - latest leads → entity=lead, operation=list, sortField=createdon, sortDirection=desc, limit=5
        - count active sales orders → entity=salesorder, operation=count, filtersJson=[{"field":"statecode","operator":"eq","value":"Active"}]
        - عدد أوامر البيع → entity=salesorder, operation=count
        - contacts created before March 2026 → entity=contact, filtersJson with createdon lt 2026-03-01
        - accounts created after January 2026 → entity=account, filtersJson with createdon gt 2026-01-31 (or ge 2026-02-01)
        - highest-value sales orders → entity=salesorder, sortField=totalamount, sortDirection=desc, limit=5
        - contacts for an account GUID → entity=contact, relatedFromEntity=account, relatedFromId={guid}

        Relative dates: relativeDateField=createdon|modifiedon with relativeDateToken=
        last_calendar_month | this_calendar_month | this_week | last_7_days | last_30_days

        If the question needs an unapproved entity, unrestricted joins, customer-name lookup via relationship without a GUID, or write operations, explain the supported scope and ask for clarification.
        Refuse create/update/delete and unrelated topics.
        """;

    private readonly IChatClient _chatClient;
    private readonly CrmAiTools _tools;
    private readonly ConversationStore _conversations;
    private readonly ICrmDataProvider _crm;
    private readonly Dynamics365CrmProvider _dynamics;
    private readonly DynamicsConnectivityService _connectivity;
    private readonly ICrmAuthorizationService _auth;
    private readonly CrmArgumentValidator _validator;
    private readonly IGenericCrmRepository _genericCrm;
    private readonly ICrmMetadataService _metadata;
    private readonly CrmOptions _crmOptions;
    private readonly AiOptions _aiOptions;
    private readonly GroqOptions _groqOptions;
    private readonly GeminiOptions _geminiOptions;
    private readonly OpenAiOptions _openAiOptions;
    private readonly IExecutionTraceStore _traceStore;
    private readonly IExecutionTracer _tracer;
    private readonly ILogger<ChatService> _logger;

    public ChatService(
        IChatClient chatClient,
        CrmAiTools tools,
        ConversationStore conversations,
        ICrmDataProvider crm,
        Dynamics365CrmProvider dynamics,
        DynamicsConnectivityService connectivity,
        ICrmAuthorizationService auth,
        CrmArgumentValidator validator,
        IGenericCrmRepository genericCrm,
        ICrmMetadataService metadata,
        IOptions<CrmOptions> crmOptions,
        IOptions<AiOptions> aiOptions,
        IOptions<GroqOptions> groqOptions,
        IOptions<GeminiOptions> geminiOptions,
        IOptions<OpenAiOptions> openAiOptions,
        IExecutionTraceStore traceStore,
        IExecutionTracer tracer,
        ILogger<ChatService> logger)
    {
        _chatClient = chatClient;
        _tools = tools;
        _conversations = conversations;
        _crm = crm;
        _dynamics = dynamics;
        _connectivity = connectivity;
        _auth = auth;
        _validator = validator;
        _genericCrm = genericCrm;
        _metadata = metadata;
        _crmOptions = crmOptions.Value;
        _aiOptions = aiOptions.Value;
        _groqOptions = groqOptions.Value;
        _geminiOptions = geminiOptions.Value;
        _openAiOptions = openAiOptions.Value;
        _traceStore = traceStore;
        _tracer = tracer;
        _logger = logger;
    }

    public async Task<ChatResponseDto> ChatAsync(ChatRequestDto request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Message))
        {
            throw new ArgumentException("Message is required.", nameof(request));
        }

        var session = _conversations.GetOrCreate(request.ConversationId);
        _tools.Session = session;
        var language = DetectLanguageHint(request.Message);
        var trace = _traceStore.Start(session.ConversationId, request.Message, request.ExecutionId);
        _tracer.SetAi(ResolveAiProviderName(), ResolveAiModelName());
        _tracer.Instant(
            ExecutionSteps.HttpChatRequestReceived,
            ExecutionStepStatus.Success,
            summary: $"conversationId={session.ConversationId}; lang={language}",
            details: new
            {
                conversationId = session.ConversationId,
                messageLength = request.Message.Trim().Length,
                languageHint = language
            });

        try
        {
            return await ChatCoreAsync(request, session, language, trace, cancellationToken);
        }
        catch (Exception ex)
        {
            _tracer.Instant(
                ExecutionSteps.FinalChatResponse,
                ExecutionStepStatus.Failed,
                summary: "Unhandled chat failure",
                error: ex.Message);
            _traceStore.Complete(trace.ExecutionId, ExecutionTraceStatus.Failed);
            throw;
        }
    }

    private async Task<ChatResponseDto> ChatCoreAsync(
        ChatRequestDto request,
        ConversationSession session,
        string language,
        ExecutionTrace trace,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "User question: conversationId={ConversationId}, executionId={ExecutionId}, messageLength={Length}, languageHint={Hint}",
            session.ConversationId,
            trace.ExecutionId,
            request.Message.Trim().Length,
            language);

        // History sent to the LLM never includes CRM tool result payloads.
        var outboundHistory = BuildOutboundHistory(session, request.Message.Trim());
        var audit = OutboundAiPayloadAuditor.Audit(outboundHistory);
        _logger.LogInformation(
            "Outbound AI payload audit: messages={Count}, roles={Roles}, contentTypes={Types}, containsFunctionResults={HasResults}, suspiciousCrmText={Suspicious}, safe={Safe}",
            audit.MessageCount,
            audit.Roles,
            audit.ContentTypes,
            audit.ContainsFunctionResults,
            audit.SuspiciousCrmRecordText,
            audit.SafeForLiveCrm);

        _tracer.SetPrivacy(privacySafe: true, outboundContainedCrm: !audit.SafeForLiveCrm);
        _tracer.Instant(
            ExecutionSteps.PrivacyVerification,
            audit.SafeForLiveCrm ? ExecutionStepStatus.Success : ExecutionStepStatus.Blocked,
            summary: audit.SafeForLiveCrm
                ? "Outbound AI payload contains no CRM records"
                : "Outbound AI payload blocked — CRM text detected",
            details: new
            {
                audit.MessageCount,
                audit.ContainsFunctionResults,
                audit.SuspiciousCrmRecordText,
                audit.SafeForLiveCrm
            });

        if (!audit.SafeForLiveCrm)
        {
            _traceStore.Complete(trace.ExecutionId, ExecutionTraceStatus.Blocked);
            throw new InvalidOperationException(
                "Refusing to call the AI provider because the outbound payload appears to contain CRM tool results or record text.");
        }

        var tools = CreateToolDeclarations();
        var chatOptions = new ChatOptions
        {
            Tools = tools,
            Temperature = 0.1f
        };

        _tracer.Instant(
            ExecutionSteps.GroqApiRequestPrepared,
            ExecutionStepStatus.Success,
            summary: $"provider={ResolveAiProviderName()}; tools=[{string.Join(',', tools.OfType<AIFunction>().Select(t => t.Name))}]",
            details: new
            {
                provider = ResolveAiProviderName(),
                model = ResolveAiModelName(),
                toolCount = tools.Count,
                toolNames = tools.OfType<AIFunction>().Select(t => t.Name).ToArray(),
                outboundMessageCount = outboundHistory.Count
            });

        var groqStep = _tracer.Begin(ExecutionSteps.GroqApiResponseReceived, "Waiting for AI tool call…");
        var groqSw = Stopwatch.StartNew();
        ChatResponse modelResponse;
        try
        {
            modelResponse = await GetResponseWithRetryAsync(outboundHistory, chatOptions, cancellationToken);
            groqSw.Stop();
            _tracer.SetMetric("groqApiMs", groqSw.ElapsedMilliseconds);
            if (groqStep is not null)
            {
                _tracer.Complete(
                    groqStep.StepId,
                    ExecutionStepStatus.Success,
                    summary: $"AI responded in {groqSw.ElapsedMilliseconds}ms",
                    details: new { durationMs = groqSw.ElapsedMilliseconds });
            }
        }
        catch (Exception ex)
        {
            groqSw.Stop();
            _tracer.SetMetric("groqApiMs", groqSw.ElapsedMilliseconds);
            if (groqStep is not null)
            {
                _tracer.Complete(groqStep.StepId, ExecutionStepStatus.Failed, error: ex.Message);
            }

            _traceStore.Complete(trace.ExecutionId, ExecutionTraceStatus.Failed);
            throw;
        }

        var functionCalls = modelResponse.Messages
            .SelectMany(m => m.Contents.OfType<FunctionCallContent>())
            .ToList();

        session.Add(new ChatMessage(ChatRole.User, request.Message.Trim()));

        if (functionCalls.Count == 0)
        {
            if (LooksLikeWriteRequest(request.Message))
            {
                var refuse = language == "ar"
                    ? "هذه العملية مرفوضة. المساعد للقراءة فقط ولا ينفّذ إنشاء أو تعديل أو حذف."
                    : "Refused. This assistant is read-only and will not create, update, or delete CRM records.";
                _tracer.Instant(
                    ExecutionSteps.AiToolSelected,
                    ExecutionStepStatus.Blocked,
                    summary: "Write/delete intent — no CRM tool executed");
                _tracer.Instant(
                    ExecutionSteps.BackendValidation,
                    ExecutionStepStatus.Blocked,
                    summary: "Write/delete intent blocked before CRM query");
                _tracer.Instant(ExecutionSteps.FinalChatResponse, ExecutionStepStatus.Blocked, summary: refuse);
                session.Add(new ChatMessage(ChatRole.Assistant, refuse));
                var refused = CreateResponse(session, refuse, true, selectedTool: null, data: null, whoAmI: null, outboundWasSafe: audit.SafeForLiveCrm);
                _traceStore.Complete(trace.ExecutionId, ExecutionTraceStatus.Blocked);
                return refused;
            }

            // Model sometimes replies in text for Arabic count questions instead of calling a tool.
            var fallbackEntity = CrmQueryIntentHelper.DetectEntityLogicalName(request.Message);
            if (fallbackEntity is not null)
            {
                var args = new Dictionary<string, object?>
                {
                    ["entity"] = fallbackEntity,
                    ["operation"] = CrmQueryIntentHelper.LooksLikeCountQuestion(request.Message) ? "count" : "list",
                    ["limit"] = 10
                };
                var synthetic = CrmQueryIntentHelper.CorrectToolCall(
                    request.Message.Trim(),
                    new FunctionCallContent("local-entity-fallback", "query_crm", args));
                _logger.LogInformation(
                    "No tool selected by model; applying entity intent fallback for {Entity}.",
                    fallbackEntity);
                _tracer.Instant(
                    ExecutionSteps.AiToolSelected,
                    ExecutionStepStatus.Success,
                    summary: "Local entity intent fallback → query_crm",
                    details: TraceSanitizer.SanitizeToolArguments(args));
                _tracer.SetTool("query_crm", args);
                return await HandleQueryCrmPrivacySafeAsync(
                    session, synthetic, language, audit.SafeForLiveCrm, cancellationToken);
            }

            var text = LocalCrmReplyFormatter.FormatRefusalOrText(modelResponse.Text, language);
            var isBlocked = LooksLikeWriteRefusal(request.Message) || LooksLikeWriteRefusal(text);
            _tracer.Instant(
                ExecutionSteps.AiToolSelected,
                isBlocked ? ExecutionStepStatus.Blocked : ExecutionStepStatus.Success,
                summary: isBlocked ? "No CRM tool — request refused / out of scope" : "No tool call; text reply only",
                details: new { replyPreview = TraceSanitizer.PreviewMessage(text, 160) });
            _tracer.Instant(
                ExecutionSteps.FinalChatResponse,
                isBlocked ? ExecutionStepStatus.Blocked : ExecutionStepStatus.Success,
                summary: "Final assistant text (no CRM query)");
            session.Add(new ChatMessage(ChatRole.Assistant, text));
            var response = CreateResponse(session, text, privacySafe: true, selectedTool: null, data: null, whoAmI: null, audit.SafeForLiveCrm);
            _traceStore.Complete(trace.ExecutionId, isBlocked ? ExecutionTraceStatus.Blocked : ExecutionTraceStatus.Success);
            return response;
        }

        // Privacy-safe path: execute tools locally and NEVER send FunctionResultContent back to Groq.
        var originalCall = functionCalls[0];
        var call = CrmQueryIntentHelper.CorrectToolCall(request.Message.Trim(), originalCall);
        var originalEntity = GetArgValue(originalCall.Arguments, "entity");
        var effectiveEntity = GetArgValue(call.Arguments, "entity");
        if (!string.Equals(call.Name, originalCall.Name, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(originalEntity, effectiveEntity, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogInformation(
                "Tool routing after entity intent check: original={Original}/{OrigEntity}, effective={Effective}/{EffEntity}",
                originalCall.Name,
                originalEntity,
                call.Name,
                effectiveEntity);
        }

        _logger.LogInformation(
            "Selected AI function: {FunctionName} | Parsed arguments keys={Keys}",
            call.Name,
            call.Arguments is null ? "(none)" : string.Join(",", call.Arguments.Keys));

        _tracer.SetTool(call.Name, call.Arguments);
        _tracer.Instant(
            ExecutionSteps.AiToolSelected,
            ExecutionStepStatus.Success,
            summary: $"tool={call.Name}; entity={effectiveEntity ?? "(n/a)"}",
            details: new
            {
                originalTool = originalCall.Name,
                effectiveTool = call.Name,
                originalEntity,
                effectiveEntity,
                arguments = TraceSanitizer.SanitizeToolArguments(call.Arguments)
            });

        // Backend hard-stop for write/delete intents even if the model incorrectly picks query_crm.
        if (LooksLikeWriteRequest(request.Message))
        {
            var refuse = language == "ar"
                ? "هذه العملية مرفوضة. المساعد للقراءة فقط ولا ينفّذ إنشاء أو تعديل أو حذف."
                : "Refused. This assistant is read-only and will not create, update, or delete CRM records.";
            _tracer.Instant(
                ExecutionSteps.BackendValidation,
                ExecutionStepStatus.Blocked,
                summary: "Write/delete intent blocked before CRM query",
                details: new { reason = "read_only_policy" });
            _tracer.Instant(ExecutionSteps.FinalChatResponse, ExecutionStepStatus.Blocked, summary: refuse);
            session.Add(new ChatMessage(ChatRole.Assistant, refuse));
            var refused = CreateResponse(session, refuse, true, call.Name, null, null, audit.SafeForLiveCrm);
            _traceStore.Complete(trace.ExecutionId, ExecutionTraceStatus.Blocked);
            return refused;
        }

        // Phase 5.1: Groq is only offered query_crm. Intent helper remaps any legacy names to query_crm.
        if (string.Equals(call.Name, "query_crm", StringComparison.OrdinalIgnoreCase))
        {
            return await HandleQueryCrmPrivacySafeAsync(session, call, language, audit.SafeForLiveCrm, cancellationToken);
        }

        var unsupported = language == "ar"
            ? "هذه الأداة غير متاحة. استخدم استعلام CRM العام فقط."
            : $"Tool '{call.Name}' is not exposed. Use query_crm only.";
        session.Add(new ChatMessage(ChatRole.Assistant, unsupported));
        _tracer.Instant(ExecutionSteps.FinalChatResponse, ExecutionStepStatus.Blocked, summary: unsupported);
        var blocked = CreateResponse(session, unsupported, true, call.Name, null, null, audit.SafeForLiveCrm);
        _traceStore.Complete(trace.ExecutionId, ExecutionTraceStatus.Blocked);
        return blocked;
    }

    private static bool LooksLikeWriteRequest(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        return WriteIntentPattern().IsMatch(text);
    }

    private static bool LooksLikeWriteRefusal(string text) =>
        text.Contains("delete", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("حذف", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("create", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("update", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("refuse", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("read-only", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("read only", StringComparison.OrdinalIgnoreCase);

    [System.Text.RegularExpressions.GeneratedRegex(
        @"\b(delete|remove|create|update|insert|upsert|assign|destroy)\b|حذف|إنشاء|تعديل|امسح|امسحي",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant)]
    private static partial System.Text.RegularExpressions.Regex WriteIntentPattern();

    private async Task<ChatResponseDto> HandleQueryCrmPrivacySafeAsync(
        ConversationSession session,
        FunctionCallContent call,
        string language,
        bool outboundWasSafe,
        CancellationToken cancellationToken)
    {
        var parseSw = Stopwatch.StartNew();
        var parseStep = _tracer.Begin(ExecutionSteps.StructuredQueryPlanParsed, "Parsing query_crm arguments");
        var parsed = CrmQueryPlanParser.ParseFromToolArguments(call.Arguments);
        parseSw.Stop();
        _tracer.SetMetric("toolParseMs", parseSw.ElapsedMilliseconds);

        if (!parsed.Success || parsed.Plan is null)
        {
            var msg = parsed.Error ?? "Invalid query_crm arguments.";
            if (parseStep is not null)
            {
                _tracer.Complete(parseStep.StepId, ExecutionStepStatus.Failed, error: msg);
            }

            session.Add(new ChatMessage(ChatRole.Assistant, msg));
            _tracer.Instant(ExecutionSteps.FinalChatResponse, ExecutionStepStatus.Failed, summary: msg);
            CompleteTrace(ExecutionTraceStatus.Failed);
            return CreateResponse(session, msg, true, "query_crm", null, null, outboundWasSafe);
        }

        if (parseStep is not null)
        {
            _tracer.Complete(
                parseStep.StepId,
                ExecutionStepStatus.Success,
                summary: $"entity={parsed.Plan.Entity}; op={parsed.Plan.Operation}",
                details: new
                {
                    entity = parsed.Plan.Entity,
                    operation = parsed.Plan.Operation.ToString(),
                    filterCount = parsed.Plan.Filters.Count,
                    sortCount = parsed.Plan.Sort.Count,
                    limit = parsed.Plan.Limit,
                    relatedFromEntity = parsed.Plan.RelatedFromEntity,
                    selectCount = parsed.Plan.Select.Count
                });
        }

        var validationSw = Stopwatch.StartNew();
        var validationStep = _tracer.Begin(ExecutionSteps.BackendValidation, "Entity approval + read-only auth");
        var auth = _auth.AuthorizeRead(parsed.Plan.Entity, "query_crm");
        validationSw.Stop();
        _tracer.SetMetric("validationMs", validationSw.ElapsedMilliseconds);
        if (!auth.Allowed)
        {
            if (validationStep is not null)
            {
                _tracer.Complete(validationStep.StepId, ExecutionStepStatus.Blocked, error: auth.Reason);
            }

            session.Add(new ChatMessage(ChatRole.Assistant, auth.Reason));
            _tracer.Instant(ExecutionSteps.FinalChatResponse, ExecutionStepStatus.Blocked, summary: auth.Reason);
            CompleteTrace(ExecutionTraceStatus.Blocked);
            return CreateResponse(session, auth.Reason, true, "query_crm", null, null, outboundWasSafe);
        }

        if (validationStep is not null)
        {
            _tracer.Complete(validationStep.StepId, ExecutionStepStatus.Success, summary: "Read authorized");
        }

        var metaSw = Stopwatch.StartNew();
        var metaStep = _tracer.Begin(ExecutionSteps.MetadataResolution, "Loading approved entity metadata");
        await _metadata.EnsureApprovedEntitiesLoadedAsync(cancellationToken);
        metaSw.Stop();
        _tracer.SetMetric("metadataMs", metaSw.ElapsedMilliseconds);
        if (metaStep is not null)
        {
            _tracer.Complete(
                metaStep.StepId,
                ExecutionStepStatus.Success,
                summary: $"metadata ready for {parsed.Plan.Entity}",
                details: new
                {
                    entity = parsed.Plan.Entity,
                    relatedFromEntity = parsed.Plan.RelatedFromEntity,
                    durationMs = metaSw.ElapsedMilliseconds
                });
        }

        Guid? whoAmIUserId = null;
        if (_crmOptions.EnableLiveIntegration && !string.IsNullOrWhiteSpace(_crmOptions.BaseUrl))
        {
            if (_crmOptions.AllowSendLiveDataToAi)
            {
                throw new InvalidOperationException(
                    "Crm:AllowSendLiveDataToAi must remain false for the privacy-safe chat path.");
            }

            var who = await _connectivity.WhoAmIAsync(cancellationToken);
            if (!who.Success || who.UserId is null)
            {
                var fail = language == "ar"
                    ? "تعذر التحقق من هوية Windows/CRM قبل تنفيذ الاستعلام."
                    : $"CRM identity verification failed before query_crm: {who.Message}";
                session.Add(new ChatMessage(ChatRole.Assistant, fail));
                _tracer.Instant(ExecutionSteps.FinalChatResponse, ExecutionStepStatus.Failed, error: fail);
                CompleteTrace(ExecutionTraceStatus.Failed);
                return CreateResponse(session, fail, true, "query_crm", null, null, outboundWasSafe);
            }

            whoAmIUserId = who.UserId;
        }

        GenericCrmQueryResult result;
        try
        {
            result = await _genericCrm.QueryAsync(parsed.Plan, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "query_crm failed");
            var fail = language == "ar"
                ? "فشل تنفيذ استعلام CRM العام."
                : "Failed to execute the generic CRM query.";
            session.Add(new ChatMessage(ChatRole.Assistant, fail));
            _tracer.Instant(ExecutionSteps.FinalChatResponse, ExecutionStepStatus.Failed, error: ex.Message);
            CompleteTrace(ExecutionTraceStatus.Failed);
            return CreateResponse(session, fail, true, "query_crm", null, whoAmIUserId, outboundWasSafe);
        }

        if (!string.IsNullOrWhiteSpace(result.Message) &&
            string.IsNullOrWhiteSpace(result.RelativeRequestUri) &&
            result.Items.Count == 0 &&
            result.Count is null)
        {
            var blockedStatus = result.Message.Contains("not an approved", StringComparison.OrdinalIgnoreCase) ||
                                result.Message.Contains("denied", StringComparison.OrdinalIgnoreCase) ||
                                result.Message.Contains("Only read", StringComparison.OrdinalIgnoreCase)
                ? ExecutionTraceStatus.Blocked
                : ExecutionTraceStatus.Failed;
            session.Add(new ChatMessage(ChatRole.Assistant, result.Message));
            _tracer.Instant(
                ExecutionSteps.FinalChatResponse,
                blockedStatus == ExecutionTraceStatus.Blocked ? ExecutionStepStatus.Blocked : ExecutionStepStatus.Failed,
                summary: result.Message);
            CompleteTrace(blockedStatus);
            return CreateResponse(session, result.Message, true, "query_crm", null, whoAmIUserId, outboundWasSafe);
        }

        var formatSw = Stopwatch.StartNew();
        var formatStep = _tracer.Begin(ExecutionSteps.LocalResponseFormatting, "Formatting reply locally (no CRM rows to AI)");
        var reply = GenericCrmReplyFormatter.Format(result, language, whoAmIUserId);
        formatSw.Stop();
        _tracer.SetMetric("formatMs", formatSw.ElapsedMilliseconds);
        if (formatStep is not null)
        {
            _tracer.Complete(
                formatStep.StepId,
                ExecutionStepStatus.Success,
                summary: $"rows={result.Items.Count}; count={result.Count?.ToString() ?? "n/a"}",
                details: new
                {
                    rowCount = result.Items.Count,
                    result.Count,
                    result.CountMayBeCapped,
                    durationMs = formatSw.ElapsedMilliseconds
                });
        }

        session.Add(new ChatMessage(ChatRole.Assistant, reply));

        _logger.LogInformation(
            "Privacy-safe query_crm complete: entity={Entity}, operation={Operation}, rows={Rows}, sentToAi=false",
            result.Entity,
            result.Operation,
            result.Items.Count);

        _tracer.SetPrivacy(privacySafe: true, outboundContainedCrm: !outboundWasSafe);
        _tracer.Instant(
            ExecutionSteps.FinalChatResponse,
            ExecutionStepStatus.Success,
            summary: "Chat response ready; CRM records not sent to Groq",
            details: new
            {
                selectedTool = "query_crm",
                entity = result.Entity,
                operation = result.Operation.ToString(),
                rows = result.Items.Count,
                count = result.Count,
                privacySafe = true
            });
        CompleteTrace(ExecutionTraceStatus.Success);

        return CreateResponse(
            session,
            reply,
            privacySafe: true,
            selectedTool: "query_crm",
            data: new
            {
                entity = result.Entity,
                operation = result.Operation.ToString().ToLowerInvariant(),
                appliedFilters = result.AppliedFilterSummary,
                sort = result.SortSummary,
                limit = result.Limit,
                count = result.Count,
                countMayBeCapped = result.CountMayBeCapped,
                records = result.Items
            },
            whoAmI: whoAmIUserId,
            outboundWasSafe: outboundWasSafe,
            crmProvider: result.ProviderName);
    }

    private void CompleteTrace(ExecutionTraceStatus status)
    {
        if (_tracer.Current is not null)
        {
            _traceStore.Complete(_tracer.Current.ExecutionId, status);
        }
    }

    private async Task<ChatResponseDto> HandleQuerySalesOrdersPrivacySafeAsync(
        ConversationSession session,
        FunctionCallContent call,
        string language,
        bool outboundWasSafe,
        CancellationToken cancellationToken)
    {
        var auth = _auth.AuthorizeRead(CrmEntityAllowlist.SalesOrder, "query_sales_orders");
        if (!auth.Allowed)
        {
            session.Add(new ChatMessage(ChatRole.Assistant, auth.Reason));
            return CreateResponse(session, auth.Reason, true, "query_sales_orders", null, null, outboundWasSafe);
        }

        var parsed = SalesOrderQueryPlanParser.ParseFromToolArguments(call.Arguments);
        if (!parsed.Success || parsed.Plan is null)
        {
            var msg = parsed.Error ?? "Invalid query_sales_orders arguments.";
            session.Add(new ChatMessage(ChatRole.Assistant, msg));
            return CreateResponse(session, msg, true, "query_sales_orders", null, null, outboundWasSafe);
        }

        Guid? whoAmIUserId = null;
        var dataProvider = ResolveSalesOrderProvider();
        if (dataProvider.ProviderName.Equals("Dynamics365", StringComparison.OrdinalIgnoreCase))
        {
            if (_crmOptions.AllowSendLiveDataToAi)
            {
                throw new InvalidOperationException(
                    "Crm:AllowSendLiveDataToAi must remain false for the privacy-safe chat path.");
            }

            var who = await _connectivity.WhoAmIAsync(cancellationToken);
            if (!who.Success || who.UserId is null)
            {
                var fail = language == "ar"
                    ? "تعذر التحقق من هوية Windows/CRM قبل الاستعلام عن أوامر البيع."
                    : $"CRM identity verification failed before querying sales orders: {who.Message}";
                session.Add(new ChatMessage(ChatRole.Assistant, fail));
                return CreateResponse(session, fail, true, "query_sales_orders", null, null, outboundWasSafe);
            }

            whoAmIUserId = who.UserId;
        }

        SalesOrderQueryExecutionResult result;
        try
        {
            result = await dataProvider.QuerySalesOrdersAsync(parsed.Plan, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Dynamics query_sales_orders failed");
            var fail = language == "ar"
                ? "فشل تنفيذ استعلام أوامر البيع على Dynamics 365."
                : "Failed to execute the sales order query against Dynamics 365.";
            session.Add(new ChatMessage(ChatRole.Assistant, fail));
            return CreateResponse(session, fail, true, "query_sales_orders", null, whoAmIUserId, outboundWasSafe);
        }

        if (!string.IsNullOrWhiteSpace(result.Message) &&
            string.IsNullOrWhiteSpace(result.RelativeRequestUri) &&
            result.Items.Count == 0 &&
            result.Count is null)
        {
            session.Add(new ChatMessage(ChatRole.Assistant, result.Message));
            return CreateResponse(session, result.Message, true, "query_sales_orders", null, whoAmIUserId, outboundWasSafe);
        }

        var reply = LocalCrmReplyFormatter.FormatQueryResult(result, language, whoAmIUserId);
        session.Add(new ChatMessage(ChatRole.Assistant, reply));

        _logger.LogInformation(
            "Privacy-safe query_sales_orders complete: provider={Provider}, operation={Operation}, rows={Rows}, count={Count}, sentToAi={Sent}",
            dataProvider.ProviderName,
            result.Operation,
            result.Items.Count,
            result.Count,
            false);

        return CreateResponse(
            session,
            reply,
            privacySafe: true,
            selectedTool: "query_sales_orders",
            data: new
            {
                operation = result.Operation.ToString().ToLowerInvariant(),
                appliedFilters = result.AppliedFilterSummary,
                sort = result.SortSummary,
                limit = result.Limit,
                count = result.Count,
                countMayBeCapped = result.CountMayBeCapped,
                salesOrders = result.Items.Select(o => new
                {
                    o.SalesOrderId,
                    o.Name,
                    o.OrderNumber,
                    o.CustomerAccountId,
                    o.CreatedOn,
                    o.ModifiedOn,
                    o.TotalAmount,
                    o.StatusCode,
                    o.StatusLabel,
                    o.StateCode,
                    o.StateLabel
                })
            },
            whoAmI: whoAmIUserId,
            outboundWasSafe: outboundWasSafe,
            crmProvider: dataProvider.ProviderName);
    }

    private async Task<ChatResponseDto> HandleSalesOrdersPrivacySafeAsync(
        ConversationSession session,
        FunctionCallContent call,
        string language,
        bool outboundWasSafe,
        CancellationToken cancellationToken)
    {
        var auth = _auth.AuthorizeRead(CrmEntityAllowlist.SalesOrder, "get_sales_orders");
        if (!auth.Allowed)
        {
            var denied = auth.Reason;
            session.Add(new ChatMessage(ChatRole.Assistant, denied));
            return CreateResponse(session, denied, privacySafe: true, "get_sales_orders", null, null, outboundWasSafe);
        }

        var limit = ExtractLimit(call.Arguments);
        var safeLimit = _validator.ClampLimit(limit);

        Guid? whoAmIUserId = null;
        var dataProvider = ResolveSalesOrderProvider();
        if (dataProvider.ProviderName.Equals("Dynamics365", StringComparison.OrdinalIgnoreCase))
        {
            if (_crmOptions.AllowSendLiveDataToAi)
            {
                throw new InvalidOperationException(
                    "Crm:AllowSendLiveDataToAi must remain false for the privacy-safe chat path.");
            }

            var who = await _connectivity.WhoAmIAsync(cancellationToken);
            if (!who.Success || who.UserId is null)
            {
                var fail = language == "ar"
                    ? "تعذر التحقق من هوية Windows/CRM قبل قراءة أوامر البيع."
                    : $"CRM identity verification failed before reading sales orders: {who.Message}";
                _logger.LogWarning("WhoAmI failed before get_sales_orders: {Message}", who.Message);
                session.Add(new ChatMessage(ChatRole.Assistant, fail));
                return CreateResponse(session, fail, true, "get_sales_orders", null, null, outboundWasSafe);
            }

            whoAmIUserId = who.UserId;
            _logger.LogInformation(
                "CRM Windows identity for read: UserId={UserId}, AuthMode={AuthMode}. Process default credentials are not assumed to equal interactive end-user SSO.",
                who.UserId,
                who.AuthMode);
        }

        CrmQueryResult<CrmSalesOrder> result;
        try
        {
            result = await dataProvider.GetSalesOrdersAsync(safeLimit, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Dynamics get_sales_orders failed");
            var fail = language == "ar"
                ? "فشل جلب أوامر البيع من Dynamics 365."
                : "Failed to retrieve sales orders from Dynamics 365.";
            session.Add(new ChatMessage(ChatRole.Assistant, fail));
            return CreateResponse(session, fail, true, "get_sales_orders", null, whoAmIUserId, outboundWasSafe);
        }

        var structured = result.Items.Select(o => new
        {
            o.SalesOrderId,
            o.Name,
            o.OrderNumber,
            o.CustomerAccountId,
            o.CreatedOn,
            o.ModifiedOn,
            o.TotalAmount,
            o.StatusCode,
            o.StatusLabel,
            o.StateCode,
            o.StateLabel
        }).ToList();

        var reply = LocalCrmReplyFormatter.FormatSalesOrders(
            result.Items,
            safeLimit,
            language,
            dataProvider.ProviderName,
            whoAmIUserId);

        session.Add(new ChatMessage(ChatRole.Assistant, reply));

        _logger.LogInformation(
            "Privacy-safe get_sales_orders complete: provider={Provider}, count={Count}, sentToAi={Sent}",
            dataProvider.ProviderName,
            result.Items.Count,
            false);

        return CreateResponse(
            session,
            reply,
            privacySafe: true,
            selectedTool: "get_sales_orders",
            data: new { limit = safeLimit, totalMatched = result.TotalMatched, salesOrders = structured },
            whoAmI: whoAmIUserId,
            outboundWasSafe: outboundWasSafe,
            crmProvider: dataProvider.ProviderName);
    }

    private ICrmDataProvider ResolveSalesOrderProvider()
    {
        if (_crmOptions.EnableLiveIntegration && !string.IsNullOrWhiteSpace(_crmOptions.BaseUrl))
        {
            return _dynamics;
        }

        return _crm;
    }

    private List<AITool> CreateToolDeclarations() =>
    [
        // Single generic CRM tool for Groq. Legacy CrmAiTools methods remain for unit tests only.
        AIFunctionFactory.Create(_tools.query_crm)
    ];

    private static List<ChatMessage> BuildOutboundHistory(ConversationSession session, string newUserMessage)
    {
        // Privacy + anti-sticky-entity: send system + ONLY the latest user message to Groq.
        // Prior user questions (e.g. accounts) were causing the model to reuse entity=account.
        // Multi-turn account linkage still uses ConversationSession.LastReferencedAccountId server-side.
        _ = session;
        return
        [
            new ChatMessage(ChatRole.System, SystemPrompt),
            new ChatMessage(ChatRole.User, newUserMessage)
        ];
    }

    private async Task<string> ExecuteDeclaredToolLocallyAsync(
        FunctionCallContent call,
        IList<AITool> tools,
        CancellationToken cancellationToken)
    {
        var fn = tools.OfType<AIFunction>().FirstOrDefault(t =>
            string.Equals(t.Name, call.Name, StringComparison.OrdinalIgnoreCase));
        if (fn is null)
        {
            return JsonSerializer.Serialize(new { error = true, message = $"Unknown tool '{call.Name}'." });
        }

        var args = call.Arguments is null ? null : new AIFunctionArguments(call.Arguments);
        var raw = await fn.InvokeAsync(args, cancellationToken);
        return raw switch
        {
            string s => s,
            JsonElement je => je.GetRawText(),
            null => JsonSerializer.Serialize(new { error = true, message = "Empty tool result." }),
            _ => JsonSerializer.Serialize(raw)
        };
    }

    private static string? GetArgValue(IDictionary<string, object?>? arguments, string key)
    {
        if (arguments is null || !arguments.TryGetValue(key, out var raw) || raw is null)
        {
            return null;
        }

        return Convert.ToString(raw, CultureInfo.InvariantCulture);
    }

    private static int ExtractLimit(IDictionary<string, object?>? arguments)
    {
        if (arguments is null)
        {
            return 10;
        }

        if (!arguments.TryGetValue("limit", out var raw) || raw is null)
        {
            return 10;
        }

        return raw switch
        {
            int i => i,
            long l => (int)l,
            double d => (int)d,
            JsonElement je when je.TryGetInt32(out var ji) => ji,
            string s when int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => 10
        };
    }

    private async Task<ChatResponse> GetResponseWithRetryAsync(
        IReadOnlyList<ChatMessage> messages,
        ChatOptions chatOptions,
        CancellationToken cancellationToken)
    {
        const int maxAttempts = 3;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                return await _chatClient.GetResponseAsync(messages, chatOptions, cancellationToken);
            }
            catch (Exception ex) when (attempt < maxAttempts && IsTransientError(ex))
            {
                var delay = TimeSpan.FromSeconds(attempt * 2);
                _logger.LogWarning(
                    ex,
                    "AI provider transient error (attempt {Attempt}/{Max}). Retrying in {DelaySeconds}s.",
                    attempt,
                    maxAttempts,
                    delay.TotalSeconds);
                await Task.Delay(delay, cancellationToken);
            }
        }

        throw new InvalidOperationException("AI request failed after retries.");
    }

    private static bool IsTransientError(Exception ex)
    {
        var message = ex.Message ?? string.Empty;
        return message.Contains("high demand", StringComparison.OrdinalIgnoreCase)
               || message.Contains("rate limit", StringComparison.OrdinalIgnoreCase)
               || message.Contains("try again", StringComparison.OrdinalIgnoreCase)
               || message.Contains("temporarily", StringComparison.OrdinalIgnoreCase)
               || message.Contains("429", StringComparison.OrdinalIgnoreCase)
               || message.Contains("503", StringComparison.OrdinalIgnoreCase);
    }

    private ChatResponseDto CreateResponse(
        ConversationSession session,
        string reply,
        bool privacySafe,
        string? selectedTool,
        object? data,
        Guid? whoAmI,
        bool outboundWasSafe,
        string? crmProvider = null) =>
        new()
        {
            ConversationId = session.ConversationId,
            Reply = reply,
            ProviderHint = ResolveProviderHint(),
            LastReferencedAccountId = session.LastReferencedAccountId,
            LastReferencedAccountName = session.LastReferencedAccountName,
            PrivacySafeToolExecution = privacySafe,
            SelectedTool = selectedTool,
            StructuredData = data,
            CrmWhoAmIUserId = whoAmI,
            OutboundAiContainedCrmRecords = !outboundWasSafe,
            CrmDataProvider = crmProvider,
            ExecutionId = _tracer.Current?.ExecutionId
        };

    private string ResolveProviderHint()
    {
        var provider = ResolveAiProviderName();
        if (provider.Equals("OpenAI", StringComparison.OrdinalIgnoreCase))
        {
            return "OpenAI (tool selection only; CRM replies local)";
        }

        if (provider.Equals("Gemini", StringComparison.OrdinalIgnoreCase))
        {
            return "Gemini (tool selection only; CRM replies local)";
        }

        return "Groq (tool selection only; CRM replies local)";
    }

    private string ResolveAiProviderName() => (_aiOptions.Provider ?? "Groq").Trim();

    private string ResolveAiModelName()
    {
        var provider = ResolveAiProviderName();
        if (provider.Equals("OpenAI", StringComparison.OrdinalIgnoreCase))
        {
            return _openAiOptions.ModelName;
        }

        if (provider.Equals("Gemini", StringComparison.OrdinalIgnoreCase))
        {
            return _geminiOptions.ModelName;
        }

        return _groqOptions.ModelName;
    }

    private static object? SafeParseJson(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<JsonElement>(json);
        }
        catch
        {
            return null;
        }
    }

    private static string DetectLanguageHint(string message)
    {
        foreach (var ch in message)
        {
            if (ch is >= '\u0600' and <= '\u06FF')
            {
                return "ar";
            }
        }

        return "en";
    }
}
