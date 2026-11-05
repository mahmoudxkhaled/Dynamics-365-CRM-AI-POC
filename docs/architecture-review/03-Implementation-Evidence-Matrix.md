# Implementation Evidence Matrix

**Document:** 03-Implementation-Evidence-Matrix  
**Baseline date:** 2026-11-05  
**Verification status legend:**

- **Confirmed** — directly observed in current source
- **Confirmed (config)** — present in committed configuration
- **Historical** — observed in prior runs/logs/chat sessions; not re-executed here
- **Insufficient evidence** — claimed elsewhere but not verified in this pass

Line ranges are approximate anchors; prefer searching the named symbol if files shift.

---

## Architecture and request flow

| Claim | Source File | Class/Method | Line Range | Verification Status |
|-------|-------------|--------------|------------|---------------------|
| Chat API is `POST /api/chat` | `src/CrmAiPoc.Api/Endpoints/ChatEndpoints.cs` | `MapChatEndpoints` | 12–51 | Confirmed |
| Health endpoint `GET /api/health` | `ChatEndpoints.cs` | `MapGet("/health")` | 53–61 | Confirmed |
| Chat orchestration entry | `src/CrmAiPoc.Api/Ai/ChatService.cs` | `ChatAsync` / `ChatCoreAsync` | 116–352 | Confirmed |
| No chat history sent to Groq | `ChatService.cs` | `BuildOutboundHistory` | 621–637 | Confirmed |
| Outbound messages = system + catalog + user | `ChatService.cs` | `BuildOutboundHistory` | 621–637 | Confirmed |
| Semantic retrieval before Groq | `ChatService.cs` | `ChatCoreAsync` | 180–205 | Confirmed |
| Semantic failure blocks Groq | `ChatService.cs` | `ChatCoreAsync` | 187–201 | Confirmed |
| Single tool `query_crm` registered | `ChatService.cs` | `ChatCoreAsync` | 239–245 | Confirmed |
| `ChatToolMode.Auto` | `ChatService.cs` | `ChatOptions` | 240–245 | Confirmed |
| Temperature 0.1 | `ChatService.cs` | `ChatOptions` | 245 | Confirmed |
| Local tool execution (no UseFunctionInvocation) | `LazyGroqChatClient.cs` | `GetInner` | 105–109 | Confirmed |
| Clarification without tool call | `ChatService.cs` | `HandleTextOrClarificationResponse` | 326–403 | Confirmed |
| Clarification click = new chat request | `frontend/.../chat-window.component.ts` | `onClarificationSuggestion` | ~61–64 | Confirmed |
| Frontend 60s cooldown | `chat-window.component.ts` | `RequestCooldownMs` / `beginRequestCooldown` | ~22–23, cooldown methods | Confirmed |
| Conversation store local only | `ConversationStore.cs` / `ChatService.ChatAsync` | `GetOrCreate` | Program DI + ChatService | Confirmed |

---

## AI provider configuration

| Claim | Source File | Class/Method | Line Range | Verification Status |
|-------|-------------|--------------|------------|---------------------|
| Default AI provider Groq | `appsettings.json` | `Ai:Provider` | 10–12 | Confirmed (config) |
| Model `openai/gpt-oss-120b` | `appsettings.json` / `GroqOptions` / `LazyGroqChatClient` | `ModelName` | appsettings 20–22; GroqOptions 9–10; client 76–78 | Confirmed |
| SDK retries disabled (0) | `LazyGroqChatClient.cs` | `GetInner` | 84–96 | Confirmed |
| Provider timeout 60s | `AiOptions` / `appsettings.json` / `GetResponseWithRetryAsync` | `ProviderTimeoutSeconds` | AiOptions 18; ChatService ~831 | Confirmed |
| Retry only 429/503 | `AiProviderFailureClassifier.cs` / `GetResponseWithRetryAsync` | `IsTransient` | 107–108; retry loop 876 | Confirmed |
| MEAI / OpenAI package versions | `CrmAiPoc.Api.csproj` | PackageReference | 13–15 | Confirmed |
| Provider selection DI | `Program.cs` | `IChatClient` factory | 180–206 | Confirmed |

---

## Semantic retrieval

| Claim | Source File | Class/Method | Line Range | Verification Status |
|-------|-------------|--------------|------------|---------------------|
| Ollama nomic-embed-text 768-d | `OllamaOptions.cs` / `appsettings.json` | options | OllamaOptions 11–15; appsettings 24–28 | Confirmed |
| Document kinds entity/attr/rel | `MetadataDocumentBuilder.cs` | `Build` | 14–37 | Confirmed |
| Content hash invalidation | `MetadataDocumentBuilder.ComputeContentHash` + `EnsureBuiltAsync` | hash compare | Builder 40–75; Index 32–48 | Confirmed |
| Single-flight index build | `InMemorySemanticMetadataIndex.cs` | `EnsureBuiltAsync` | 26–84 | Confirmed |
| `IndexRebuilt` only for embedder | `InMemorySemanticMetadataIndex.cs` | return tuple | 46–47, 73 | Confirmed |
| Cosine similarity ranking | `SemanticCrmMetadataRetriever.cs` / `CosineSimilarity.cs` | `RetrieveAsync` | Retriever 83–91 | Confirmed |
| TopK 3 / 20 / 10 | `SemanticMetadataOptions.cs` / appsettings | properties | Options 8–17; appsettings 30–35 | Confirmed |
| Index volatile (restart) | `InMemorySemanticMetadataIndex` | in-memory `_snapshot` | class fields | Confirmed |
| ~2548 documents cold build minutes | prior live logs / Phase notes | — | — | Historical |
| Warm retrieval milliseconds | prior live tests | — | — | Historical |

---

## CRM metadata and approval

| Claim | Source File | Class/Method | Line Range | Verification Status |
|-------|-------------|--------------|------------|---------------------|
| Approved entity list (11) | `appsettings.json` | `Crm:ApprovedEntities` | 49–61 | Confirmed (config) |
| Mock vs Dynamics DI switch | `Program.cs` | provider branch | 156–175 | Confirmed |
| Live requires EnableLiveIntegration + BaseUrl | `Program.cs` | Dynamics branch guard | 159–166 | Confirmed |
| Metadata warmup hosted service | `Program.cs` / `CrmMetadataWarmupHostedService.cs` | `AddHostedService` | Program 178 | Confirmed |
| Entity approval policy | `EntityApprovalPolicy.cs` | `IsApproved` | used by ChatService/retriever | Confirmed |
| Catalog delimiters | `CrmAiMetadataCatalogService.cs` | `BeginMarker`/`EndMarker`/`BuildDelimitedBlock` | 17–18, 723–732 | Confirmed |

---

## Tool calling and validation

| Claim | Source File | Class/Method | Line Range | Verification Status |
|-------|-------------|--------------|------------|---------------------|
| Tool method `query_crm` | `CrmAiTools.cs` | `query_crm` | 39–152 | Confirmed |
| Required arg `entity` | MEAI schema from method | AIFunctionFactory | probe + method signature | Confirmed |
| Plan is non-executable | `CrmQueryPlan.cs` | class summary | 20–58 | Confirmed |
| No silent entity substitution | `ChatService.cs` | `TryCanonicalizeApprovedEntity` comment | 423–431 | Confirmed |
| Link resolution / clarification attach | `CrmRelationshipDiscovery.cs` | `ResolveProposedLinks` | called from ChatService ~436 | Confirmed |
| Unknown tool name rejected | `ChatService.cs` | after functionCalls | 347–351 | Confirmed |
| `commentary` not an app tool | tools registration + prompt | — | — | Confirmed |
| `tool_use_failed` / commentary errors | Phase 3.4 audit + user reports | — | — | Historical / strongly supported externally |

---

## Clarification

| Claim | Source File | Class/Method | Line Range | Verification Status |
|-------|-------------|--------------|------------|---------------------|
| Parser expects type=clarification, 2–4 suggestions | `ClarificationResponseParser.cs` | `TryParse` | 32–151 | Confirmed |
| Validator uses AI catalog | `ClarificationSuggestionValidator.cs` | `Validate` | class | Confirmed |
| Response DTO fields | `ChatResponseDto.cs` / `ClarificationSuggestionDto.cs` | properties | ChatResponseDto 9–16 | Confirmed |
| Angular chip rendering | `chat-message.component.html` | clarification mode | template | Confirmed |
| Independent follow-up request | `chat-window.component.ts` | `onClarificationSuggestion` → `onSend` | — | Confirmed |

---

## FetchXML and results

| Claim | Source File | Class/Method | Line Range | Verification Status |
|-------|-------------|--------------|------------|---------------------|
| FetchXML list/count generation | `GenericCrmQueryBuilder.cs` / tests | builders | `FetchXmlBusinessQueryTests` | Confirmed |
| Aggregate builder | `FetchXmlAggregateBuilder.cs` | — | — | Confirmed |
| Condition builder | `FetchXmlConditionBuilder.cs` | — | — | Confirmed |
| Dynamics repository executes FetchXML | `DynamicsGenericCrmRepository.cs` | `QueryAsync` | — | Confirmed |
| Mock repository for offline | `MockGenericCrmRepository.cs` | `QueryAsync` | — | Confirmed |
| Table column builder | `CrmTableColumnBuilder.cs` | — | — | Confirmed |
| Aggregate normalization | `AggregateResultNormalizer.cs` | — | — | Confirmed |
| Polymorphic dual INNER join empty results | prior verification notes + metadata | `order_customer_*` | StaticSeed / labels | Historical behavior; no dedicated validator Confirmed absent |

---

## Security and privacy

| Claim | Source File | Class/Method | Line Range | Verification Status |
|-------|-------------|--------------|------------|---------------------|
| Outbound auditor blocks CRM-like payloads | `OutboundAiPayloadAuditor.cs` | `Audit` | 22–68 | Confirmed |
| Catalog blocks excluded from record scan | `OutboundAiPayloadAuditor.cs` | `IsApprovedMetadataCatalogBlock` | 40–44, 71–74 | Confirmed |
| Loopback-only middleware | `LoopbackOnlyMiddleware.cs` | `InvokeAsync` | 26–50 | Confirmed |
| RestrictToLoopback default true | `appsettings.json` | `Crm:RestrictToLoopback` | 44 | Confirmed (config) |
| Write request refusal | `ChatService.cs` | `LooksLikeWriteRequest` | ~141–144 | Confirmed |
| English script policy | `EnglishInputPolicy.cs` / ChatService | Arabic script check | ChatService ~133–138 | Confirmed |
| Diagnostic dump unique by CorrelationId | `ChatService.cs` | `TryWriteDevelopmentCatalogDump` | 725–753 | Confirmed |
| Diagnostic dump failure non-fatal | `TryWriteDevelopmentCatalogDump` catch | — | 746–753 | Confirmed |
| CORS AllowAnyOrigin | `Program.cs` | `AddCors` | 208–212 | Confirmed |
| Production-secure claim | — | — | — | Insufficient evidence (explicitly not claimed) |

---

## Frontend / deployment

| Claim | Source File | Class/Method | Line Range | Verification Status |
|-------|-------------|--------------|------------|---------------------|
| Angular 19 | `frontend/.../package.json` | dependencies | 17–23 | Confirmed |
| Web resource prefix `te_/CrmAiAssistant` | `environment.prod.ts` / `CRM-DEPLOY.md` | `crmWebResourcePrefix` | env prod 14; deploy doc | Confirmed |
| Prefix is not `ted_weai/` | comparison | — | — | Confirmed difference vs older naming |
| `build:crm` packaging script | `package.json` | scripts | 9 | Confirmed |
| Chat HTTP client | `chat.service.ts` | `sendMessage` | 15–23 | Confirmed |

---

## Testing

| Claim | Source File | Class/Method | Line Range | Verification Status |
|-------|-------------|--------------|------------|---------------------|
| 44 test files under CrmAiPoc.Tests | filesystem scan | — | — | Confirmed |
| 242 Fact/Theory attributes | filesystem scan | — | — | Confirmed |
| 10 Fact/Theory in Live*.cs | filesystem scan | — | — | Confirmed |
| 269 non-live tests passed | prior Phase 3.2 agent session | — | — | Historical |
| Suite re-run for this doc set | — | — | — | Not executed |

---

## Capability matrix (summary)

| Capability | Status | Evidence pointer |
|------------|--------|------------------|
| CRM connectivity | Implemented but Not Fully Verified (env-dependent) | Dynamics* + Live tests |
| Metadata discovery | Implemented and Verified (code + mock/live paths) | DynamicsCrmMetadataService / Mock |
| Semantic metadata retrieval | Implemented and Verified (non-live unit) | Semantic* tests |
| Local embeddings | Implemented and Verified (client + options) | OllamaEmbeddingClient |
| Read-only records | Implemented and Verified | tools + auth + write refuse |
| Sorting / filtering / dates | Implemented and Verified | FetchXML + date tests |
| Count / aggregates / GroupBy | Implemented and Verified | Aggregate* tests |
| Joins / multi-hop | Implemented but Not Fully Verified (polymorphic edge) | MultiEntity* + live notes |
| Clarification + chips | Implemented and Verified | Clarification* + Angular |
| Single-stage AI planning | Implemented and Verified | ChatService + SingleStage* tests |
| Privacy enforcement | Implemented and Verified (POC) | Privacy* + auditor |
| Retry behavior | Implemented and Verified (unit) | AiProviderTimeoutRetryTests |
| CRM web-resource deploy | Implemented but Not Fully Verified | CRM-DEPLOY.md packaging |
| Automated testing | Implemented and Verified (suite exists); latest full green run Historical | tests project |
