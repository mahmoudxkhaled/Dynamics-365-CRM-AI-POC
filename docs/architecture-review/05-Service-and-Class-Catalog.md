# Service and Class Catalog

**Document:** 05-Service-and-Class-Catalog  
**Purpose:** Onboarding map for engineers new to the repository  
**Baseline date:** 2026-11-05  

Ownership legend:

- **Decides** — chooses business/planning outcome  
- **Validates** — accepts/rejects proposals  
- **Executes** — performs I/O or transformation  
- **Presents** — UI / DTO shaping  

---

## 1. Layer Overview

```text
Angular UI (Presents)
    -> Endpoints (HTTP boundary)
        -> ChatService (Decides orchestration)
            -> Semantic retrieval (Executes local AI embeddings)
            -> Metadata catalog (Executes assembly)
            -> IChatClient / Groq (Decides query plan or clarification)
            -> Clarification parser/validator (Validates)
            -> Query plan parser + relationship discovery (Validates / resolves)
            -> IGenericCrmRepository (Executes CRM read)
            -> Formatters / table builders (Presents)
```

---

## 2. Inventory Table (Backend)

| Component | File Path | Responsibility | Important Methods | Dependencies | Called By |
|-----------|-----------|----------------|-------------------|--------------|-----------|
| `ChatEndpoints` | `Endpoints/ChatEndpoints.cs` | HTTP chat/health | `MapChatEndpoints` | `ChatService` | `Program` |
| `SemanticMetadataEndpoints` | `Endpoints/SemanticMetadataEndpoints.cs` | Dev diagnostics search | `MapSemanticMetadataEndpoints` | retriever | `Program` (Development) |
| `ChatService` | `Ai/ChatService.cs` | Single-stage orchestration | `ChatAsync`, `ChatCoreAsync`, `HandleQueryCrmAsync`, `BuildOutboundHistory` | tools, catalog, semantic, chat client, metadata, authz helpers | Endpoints |
| `CrmAiTools` | `Ai/CrmAiTools.cs` | AI tool surface (`query_crm`) | `query_crm` | `IGenericCrmRepository` | `AIFunctionFactory` / ChatService registration |
| `LazyGroqChatClient` | `Ai/LazyGroqChatClient.cs` | Groq IChatClient | `GetResponseAsync`, `GetInner` | `GroqOptions` | DI `IChatClient` |
| `LazyOpenAiChatClient` | `Ai/LazyOpenAiChatClient.cs` | OpenAI provider | same pattern | OpenAI options | DI |
| `LazyGeminiChatClient` | `Ai/LazyGeminiChatClient.cs` | Gemini provider | same pattern | Gemini options | DI |
| `ClarificationResponseParser` | `Ai/ClarificationResponseParser.cs` | Parse clarification JSON | `TryParse` | — | ChatService |
| `ClarificationSuggestionValidator` | `Ai/ClarificationSuggestionValidator.cs` | Catalog-ground suggestions | `Validate` | approval policy, catalog | ChatService |
| `OutboundAiPayloadAuditor` | `Ai/OutboundAiPayloadAuditor.cs` | Block CRM rows outbound | `Audit` | catalog markers | ChatService |
| `AiProviderFailureClassifier` | `Ai/AiProviderFailureClassifier.cs` | Classify provider errors | `Classify`, `IsTransient` | — | ChatService |
| `ConversationStore` | `Ai/ConversationStore.cs` | In-memory sessions | `GetOrCreate` | — | ChatService |
| `ConversationSession` | `Ai/ConversationSession.cs` | Session state / pending rel | pending clarification fields | — | Store |
| `EnglishInputPolicy` | `Ai/EnglishInputPolicy.cs` | Reject Arabic script input | helpers | — | ChatService |
| `LocalCrmReplyFormatter` | `Ai/LocalCrmReplyFormatter.cs` | Non-CRM text fallbacks | `FormatRefusalOrText` | — | ChatService |
| `GenericCrmReplyFormatter` | `Ai/GenericCrmReplyFormatter.cs` | Format query results text | format helpers | — | ChatService path |
| `CrmQueryIntentHelper` | `Ai/CrmQueryIntentHelper.cs` | Diagnostics helpers | intent helpers | metadata | tests / diagnostics |
| `QueryCrmDateRangeArg` | `Ai/QueryCrmDateRangeArg.cs` | Typed tool date arg | properties | — | CrmAiTools schema |
| `PendingRelationshipClarification` | `Ai/PendingRelationshipClarification.cs` | Pending join choice model | — | — | discovery / session |
| `CrmAiMetadataCatalogService` | `Crm/Metadata/CrmAiMetadataCatalogService.cs` | Build AI catalog block | `BuildCatalogFromSemanticRetrievalAsync`, `BuildDelimitedBlock` | metadata, labeler | ChatService |
| `DynamicsCrmMetadataService` | `Crm/Metadata/DynamicsCrmMetadataService.cs` | Live metadata load | ensure/get approved | Dynamics HTTP | DI when Dynamics |
| `MockCrmMetadataService` | `Crm/Metadata/MockCrmMetadataService.cs` | Mock metadata | ensure/get | registry, seed | DI when Mock |
| `CrmMetadataRegistry` | `Crm/Metadata/CrmMetadataRegistry.cs` | In-process metadata cache | Upsert/TryGet | — | metadata services |
| `EntityApprovalPolicy` | `Crm/Metadata/EntityApprovalPolicy.cs` | Approved entity gate | `IsApproved`, aliases | `CrmOptions` | many |
| `CrmRelationshipDiscovery` | `Crm/Metadata/CrmRelationshipDiscovery.cs` | Resolve/clarify links | `ResolveProposedLinks`, match selection | metadata | ChatService |
| `CrmRelationshipLabeler` | `Crm/Metadata/CrmRelationshipLabeler.cs` | Friendly relationship labels | label APIs | `CrmOptions` | catalog / discovery |
| `CrmEntityResolver` | `Crm/Metadata/CrmEntityResolver.cs` | Entity name resolution | resolve | policy | parsers / tools path |
| `CrmMetadataWarmupHostedService` | `Crm/Metadata/CrmMetadataWarmupHostedService.cs` | Startup warmup | `ExecuteAsync` | metadata service | Host |
| `StaticSeedCrmMetadata` | `Crm/Metadata/StaticSeedCrmMetadata.cs` | Mock schema seed | seed builders | — | Mock metadata |
| `SemanticCrmMetadataRetriever` | `Crm/Metadata/Semantic/SemanticCrmMetadataRetriever.cs` | Question→metadata hits | `RetrieveAsync` | Ollama, index, metadata | ChatService |
| `InMemorySemanticMetadataIndex` | `Crm/Metadata/Semantic/InMemorySemanticMetadataIndex.cs` | Vector index single-flight | `EnsureBuiltAsync` | — | Retriever |
| `OllamaEmbeddingClient` | `Crm/Metadata/Semantic/OllamaEmbeddingClient.cs` | HTTP embeddings | `EmbedAsync`, batch | HttpClient | Retriever |
| `MetadataDocumentBuilder` | `Crm/Metadata/Semantic/MetadataDocumentBuilder.cs` | Docs + hash | `Build`, `ComputeContentHash` | — | Retriever |
| `CosineSimilarity` | `Crm/Metadata/Semantic/CosineSimilarity.cs` | Vector score | `Compute` | — | Retriever |
| `DynamicsGenericCrmRepository` | `Crm/Generic/DynamicsGenericCrmRepository.cs` | Live FetchXML execute | `QueryAsync` | HTTP, builder, auth | ChatService / tools |
| `MockGenericCrmRepository` | `Crm/Generic/MockGenericCrmRepository.cs` | Mock execute | `QueryAsync` | seed | DI Mock |
| `GenericCrmQueryBuilder` | `Crm/Generic/GenericCrmQueryBuilder.cs` | Plan→FetchXML | build methods | condition/aggregate builders | repositories |
| `FetchXmlAggregateBuilder` | `Crm/Generic/FetchXmlAggregateBuilder.cs` | Aggregate FetchXML | build | — | query builder |
| `FetchXmlConditionBuilder` | `Crm/Generic/FetchXmlConditionBuilder.cs` | Filter XML | build | — | query builder |
| `CrmQueryPlanParser` | `Crm/Generic/CrmQueryPlanParser.cs` | Tool args→plan | `Parse`, `ParseFromToolArguments` | — | ChatService / tools |
| `CrmRelationshipResolver` | `Crm/Generic/CrmRelationshipResolver.cs` | Spec→resolved links | resolve | metadata | query builder path |
| `AggregateResultNormalizer` | `Crm/Generic/AggregateResultNormalizer.cs` | Normalize aggregate rows | normalize | — | repository/format |
| `LinkResultNormalizer` | `Crm/Generic/LinkResultNormalizer.cs` | Flatten linked results | normalize | — | repository/format |
| `CrmTableColumnBuilder` | `Crm/Generic/CrmTableColumnBuilder.cs` | Dynamic columns | build | metadata | ChatService response |
| `CrmFieldDisplayFormatter` | `Crm/Generic/CrmFieldDisplayFormatter.cs` | Display values | format | — | formatters |
| `ClientSideResultSorter` | `Crm/Generic/ClientSideResultSorter.cs` | Re-sort page | sort | — | list path |
| `DynamicsCrmErrorParser` | `Crm/Generic/DynamicsCrmErrorParser.cs` | Parse CRM errors | parse | — | Dynamics repo |
| `DynamicsCrmHttp` | `Crm/Dynamics/DynamicsCrmHttp.cs` | CRM HTTP + handler | Get/Send helpers | `CrmOptions` | metadata/repo |
| `DynamicsConnectivityService` | `Crm/Dynamics/DynamicsConnectivityService.cs` | WhoAmI / connectivity | probe | HTTP | ChatService pre-query |
| `DateRangeResolver` / `RelativeDateResolver` | `Crm/Query/*` | Resolve relative dates | resolve | business TZ | plan execution |
| `CrmArgumentValidator` | `Crm/Validation/CrmArgumentValidator.cs` | Arg validation helpers | validate | — | various |
| `CrmAuthorizationService` | `Security/CrmAuthorizationService.cs` | Authorize reads | `AuthorizeRead` | allowlist/options | repositories |
| `CrmEntityAllowlist` | `Security/CrmEntityAllowlist.cs` | Allowlist helper | — | options | authz |
| `LoopbackOnlyMiddleware` | `Security/LoopbackOnlyMiddleware.cs` | Restrict API to loopback | `InvokeAsync` | `CrmOptions` | pipeline |
| `ChatRequestDto` / `ChatResponseDto` | `Models/*` | API contracts | properties | — | endpoints/UI |
| Options (`Ai`, `Groq`, `Crm`, `Ollama`, `SemanticMetadata`, …) | `Configuration/*` | Config binding | properties | `Program` | everywhere |

---

## 3. Inventory Table (Frontend)

| Component | File Path | Responsibility | Important Methods | Dependencies | Called By |
|-----------|-----------|----------------|-------------------|--------------|-----------|
| `ChatWindowComponent` | `features/chat/components/chat-window/*` | Chat orchestration UI | `onSend`, cooldown, suggestions | `ChatService` | App shell |
| `ChatInputComponent` | `features/chat/components/chat-input/*` | Composer | `onSubmit` | parent inputs | ChatWindow |
| `ChatMessageComponent` | `features/chat/components/chat-message/*` | Message modes | `onSuggestionClick` | parent | ChatWindow |
| `CrmResultsTableComponent` | `features/chat/components/crm-results-table/*` | Dynamic table | render columns | structuredData | ChatMessage |
| `ChatService` | `core/services/chat.service.ts` | HTTP `/api/chat` | `sendMessage` | `ApiConfigService`, HttpClient | ChatWindow |
| `ApiConfigService` | `core/services/api-config.service.ts` | Resolve API base URL | `url` | environment | ChatService |
| `CrmContextService` | `core/services/crm-context.service.ts` | Xrm context (CRM host) | context helpers | `window.parent.Xrm` | app when prod |
| `crm-table.util.ts` | `core/services/crm-table.util.ts` | Table shaping helpers | utils | — | table component |
| Models | `core/models/*` | TS DTOs | — | — | services/components |

---

## 4. Call Relationships (Chat Path)

```text
Angular ChatWindow.onSend
  -> Angular ChatService.sendMessage
    -> POST /api/chat
      -> ChatEndpoints
        -> ChatService.ChatAsync
          -> ConversationStore.GetOrCreate
          -> [optional] PendingRelationshipClarification resume
          -> SemanticCrmMetadataRetriever.RetrieveAsync
               -> ICrmMetadataService.Ensure/GetApproved
               -> MetadataDocumentBuilder.Build/Hash
               -> InMemorySemanticMetadataIndex.EnsureBuiltAsync
                    -> OllamaEmbeddingClient (batch)
               -> OllamaEmbeddingClient (query)
               -> CosineSimilarity ranking / TopK
          -> CrmAiMetadataCatalogService.BuildCatalogFromSemanticRetrievalAsync
          -> OutboundAiPayloadAuditor.Audit
          -> IChatClient.GetResponseAsync (LazyGroqChatClient)
          -> branch:
               A) ClarificationResponseParser + ClarificationSuggestionValidator
               B) CrmQueryPlanParser + EntityApproval + CrmRelationshipDiscovery
                    -> IGenericCrmRepository.QueryAsync
                         -> GenericCrmQueryBuilder / FetchXml*
                         -> DynamicsCrmHttp OR Mock
                    -> formatters + CrmTableColumnBuilder
          -> ChatResponseDto
```

---

## 5. Who Decides What

| Decision | Owner |
|----------|-------|
| Which CRM operation/fields/filters/links | Groq via `query_crm` args (**Decides**) |
| Whether to clarify vs execute | Groq under `ChatToolMode.Auto` (**Decides**) |
| Whether suggestion is catalog-valid | `ClarificationSuggestionValidator` (**Validates**) |
| Whether entity/fields/links are approved | `EntityApprovalPolicy` + discovery/resolver (**Validates**) |
| Exact FetchXML text | `GenericCrmQueryBuilder` / aggregate builder (**Executes**) |
| Whether rows return to Groq | ChatService architecture — **never on chat path** |
| UI chip click question text | Angular suggestion `question` field (**Presents** / user **Decides**) |

---

## 6. Interfaces and Implementations

| Interface | Implementation(s) | Selected by |
|-----------|-------------------|-------------|
| `IChatClient` | `LazyGroqChatClient` / OpenAI / Gemini | `Ai:Provider` |
| `ICrmMetadataService` | `DynamicsCrmMetadataService` / `MockCrmMetadataService` | `Crm:Provider` + live flags |
| `IGenericCrmRepository` | `DynamicsGenericCrmRepository` / `MockGenericCrmRepository` | same |
| `IGenericCrmQueryBuilder` | `GenericCrmQueryBuilder` | DI |
| `ICrmAiMetadataCatalogService` | `CrmAiMetadataCatalogService` | DI |
| `ISemanticCrmMetadataRetriever` | `SemanticCrmMetadataRetriever` | DI |
| `IOllamaEmbeddingClient` | `OllamaEmbeddingClient` | DI |
| `ICrmAuthorizationService` | `CrmAuthorizationService` | DI |

---

## 7. Test Catalog (by concern)

| Concern | Primary test files |
|---------|-------------------|
| Chat / single-stage planning | `SingleStageAiPlanningTests.cs`, `SemanticChatIntegrationTests.cs` |
| Clarification | `ClarificationResponseTests.cs` |
| Provider timeout/retry | `AiProviderTimeoutRetryTests.cs` |
| Diagnostic dump concurrency | `DiagnosticDumpConcurrencyTests.cs` |
| Semantic index/retrieval | `SemanticMetadataRetrievalTests.cs` |
| Catalog budget/relationships | `CrmAiMetadataCatalogTests.cs`, `MetadataCatalogBudgetAndRelationshipTests.cs` |
| FetchXML / multi-entity | `FetchXmlBusinessQueryTests.cs`, `MultiEntityLinkQueryTests.cs` |
| Aggregates | `AggregateQueryCrmTests.cs`, `Phase5GenericCrmEngineTests.cs` |
| Privacy / security | `PrivacySafeChatTests.cs`, `Phase4PrivacyAndSecurityTests.cs`, auth tests |
| Live Dynamics/Groq/Ollama | `Live*.cs` (env-gated) |

---

## 8. Configuration Classes Quick Index

| Class | Section | Purpose |
|-------|---------|---------|
| `AiOptions` | `Ai` | Provider + timeout |
| `GroqOptions` | `Groq` | Key, model, base URL |
| `OpenAiOptions` / `GeminiOptions` | respective | Alternate providers |
| `CrmOptions` | `Crm` | Connectivity, approval, privacy flags |
| `OllamaOptions` | `Ollama` | Local embeddings |
| `SemanticMetadataOptions` | `SemanticMetadata` | TopK and diagnostic endpoint |

---

## 9. Onboarding Reading Order

1. This catalog (orientation)  
2. `01-Executive-Board-Report.md` (why)  
3. `02-Detailed-Technical-Architecture.md` §2 lifecycle  
4. `ChatService.cs` top-to-bottom once  
5. `CrmAiTools.query_crm` + `CrmQueryPlan`  
6. `SemanticCrmMetadataRetriever` + `InMemorySemanticMetadataIndex`  
7. `GenericCrmQueryBuilder` + one FetchXML test  
8. Angular `chat-window` + `chat.service`  
9. `04-Risks-Gaps-and-Next-Steps.md` before changing behavior  

---

## 10. Related Documents

- `01-Executive-Board-Report.md`
- `02-Detailed-Technical-Architecture.md`
- `03-Implementation-Evidence-Matrix.md`
- `04-Risks-Gaps-and-Next-Steps.md`
