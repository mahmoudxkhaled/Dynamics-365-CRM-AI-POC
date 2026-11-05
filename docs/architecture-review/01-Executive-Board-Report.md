# WE CRM AI Assistant POC — Executive Board Report

**Document:** 01-Executive-Board-Report  
**Audience:** Technical Board, Solution Architects, Engineering Leads, Project Stakeholders  
**Classification:** Proof of Concept — Implementation Baseline  
**Date:** 2026-11-05  
**Scope:** Documentation and discovery only (no code changes in this review cycle)

---

## 1. Executive Summary

The WE CRM AI Assistant POC enables Sales and Marketing users to ask English natural-language questions against Dynamics 365 On-Premises CRM without writing FetchXML or memorizing entity schemas.

The **actual implemented architecture** is a single-stage AI planning pipeline:

1. Angular chat UI (local or Dynamics web resource) posts to ASP.NET Core `POST /api/chat`.
2. Local **Ollama** embeddings select relevant **approved CRM metadata** (not business records).
3. A compact metadata catalog plus system instructions and the current question are sent to **Groq** (`openai/gpt-oss-120b`).
4. Groq either calls the single tool **`query_crm`** or returns structured **clarification** suggestions.
5. The backend validates the plan, builds **FetchXML**, executes a **read-only** CRM query, and formats results locally.
6. CRM business rows are **not** sent back to Groq.

This report establishes the **official implementation baseline** for the next development phase. It reflects the repository as inspected on 2026-11-05. Historical README and phase notes partially lag the code; where they conflict, **the code wins**.

**Maturity:** Advanced POC with substantial automated non-live test coverage. **Not** production-certified.

---

## 2. Business Problem and Objectives

| Problem | Objective |
|---------|-----------|
| Users cannot easily query CRM without technical FetchXML/schema knowledge | Natural-language Q&A for approved Sales/Marketing entities |
| Hardcoded query templates do not scale | Metadata-driven, dynamic planning |
| Sending CRM records to external AI raises privacy risk | External AI receives schema + question only; records stay on-prem/backend |
| Ambiguous questions cause wrong queries | Clarification chips with complete follow-up questions |

Example supported intents: latest N sales orders, top N by amount, counts, monthly aggregates, joins to related accounts/contacts (within approved metadata).

---

## 3. Current POC Scope

**In scope (implemented):**

- Read-only CRM query assistant (list / count / aggregate)
- Approved-entity allowlist
- Semantic metadata retrieval (local embeddings)
- Groq as default AI planner (Gemini/OpenAI selectable)
- Clarification JSON → clickable Angular suggestions
- FetchXML engine with filters, sort, dates, joins, aggregates
- Mock CRM mode for offline development
- Dynamics live mode behind feature flags
- Privacy-oriented outbound payload auditing
- CRM web-resource packaging for Angular

**Out of scope / not claimed:**

- Create / update / delete CRM operations
- Full conversational multi-turn memory to the AI provider
- Production-grade inbound API authentication (loopback restriction is a POC control)
- Guaranteed polymorphic join correctness for all customer-lookup combinations
- Guaranteed Groq tool-call success rate for all prompts

---

## 4. Solution Overview

```text
Microsoft Dynamics 365 On-Premises
        |
        | Hosts Angular Web Resource (te_/CrmAiAssistant)
        v
 Angular Chat UI  ----POST /api/chat---->  ASP.NET Core API
                                              |
                    +-------------------------+-------------------------+
                    |                         |                         |
             Metadata Cache            Semantic Retrieval          ChatService
             (EntityDefinitions)       (Ollama nomic-embed-text)         |
                    |                         |                         |
                    +-------- compact catalog --------+                 |
                                                      |                 |
                                                      v                 |
                                                   Groq Planner         |
                                                      |                 |
                                    +-----------------+-----------------+
                                    |                                   |
                            Clarification JSON                    query_crm tool
                                    |                                   |
                              Angular chips                      Plan validation
                                    |                                   |
                         New independent /api/chat                   FetchXML
                                                                        |
                                                                  CRM read-only
                                                                        |
                                                                 Local formatting
                                                                        |
                                                                     Angular UI
```

**Key design principle:** Groq remains the authoritative **query planner**. The backend **validates and executes**; it must not silently rewrite business intent.

---

## 5. Architecture Diagram (Verified)

See also Mermaid diagrams in `02-Detailed-Technical-Architecture.md`.

High-level trust boundaries:

- **Browser / CRM iframe** → API (HTTP)
- **API** → **Ollama localhost** (embeddings only)
- **API** → **Groq cloud** (schema catalog + question + tool schema)
- **API** → **Dynamics Web API** (metadata + FetchXML reads; Windows/AD credentials)

---

## 6. Key Technologies

| Layer | Technology |
|-------|------------|
| Frontend | Angular 19, hash routing, CRM web resources |
| Backend | ASP.NET Core (.NET 8), minimal APIs |
| AI abstraction | Microsoft.Extensions.AI 10.11.0 |
| Default AI provider | Groq OpenAI-compatible API, model `openai/gpt-oss-120b` |
| Embeddings | Local Ollama, `nomic-embed-text` (768-d) |
| CRM | Dynamics 365 On-Premises Web API v9.1 (FetchXML for business queries) |
| Tests | xUnit (`CrmAiPoc.Tests`) |

---

## 7. End-to-End User Journey

1. User opens chat (local Angular or CRM iframe).
2. User asks: e.g. “Show the latest 5 Sales Orders.”
3. UI may enforce a **60-second** gap between requests (frontend cooldown).
4. Backend retrieves relevant metadata via embeddings.
5. Groq either:
   - Returns clarification options (e.g. Created On vs Modified On), or
   - Emits a `query_crm` tool call.
6. If clarification: user clicks a chip; UI sends a **new independent** chat request with the full suggested question.
7. If tool call: backend validates entity/fields/links, builds FetchXML, runs CRM read, returns table/text.
8. User sees English assistant text and, when applicable, a dynamic results table.

---

## 8. Capabilities Delivered

| Capability | Board-level status |
|------------|--------------------|
| Natural-language CRM reads (approved entities) | Delivered (POC) |
| Dynamic metadata-driven planning | Delivered |
| Local semantic metadata selection | Delivered |
| Clarification suggestions | Delivered |
| Aggregations / GroupBy / date ranges | Delivered |
| Related-entity joins (bounded) | Delivered with known polymorphic edge cases |
| Privacy: CRM rows not sent to Groq | Delivered with automated auditor |
| Hosting inside Dynamics as web resource | Packaging documented and supported |
| Production hardening (auth, HA, SRE) | Not delivered |

---

## 9. Verified Outcomes

Evidence-backed outcomes (see Document C for file/line mapping):

- Single tool `query_crm` registered for planning; ChatService executes locally.
- Outbound Groq messages are system prompt + metadata catalog + current user question only.
- Approved entity list configured in `appsettings.json` (11 entities including `te_serviceorder`).
- Semantic Top-K defaults: 3 entities, 20 attributes/entity, 10 relationships.
- Non-live automated tests exist at scale (**242** `[Fact]`/`[Theory]` attributes across **44** test files; **10** of those attributes are under `Live*.cs` files and typically require external systems).
- A prior Phase 3.2 session run reported **269** non-live tests passed; that result is **historical** and was not re-executed as part of writing this document set.

---

## 10. Security and Privacy Approach

**Confirmed controls:**

- External AI receives schema/instructions/question — not CRM business rows (auditor + no function-invocation round-trip).
- Embeddings generated via local Ollama.
- Read-oriented tool surface; write intents refused.
- Entity approval policy for AI-accessible entities.
- Optional loopback-only API middleware for unauthenticated POC hosting.
- Secrets via configuration / environment / user-secrets (not hardcoded in source).

**Explicit POC limitations:**

- Inbound API authentication for non-loopback callers is not implemented.
- CORS is permissive (`AllowAnyOrigin`) for POC convenience.
- Treat as **not production-secure** without further hardening.

---

## 11. Performance and Operational Considerations

| Concern | Observation | Impact |
|---------|-------------|--------|
| Cold semantic index build | Historical live logs: minutes for ~2,548 documents | First questions after restart can be slow |
| Warm retrieval | Historical: milliseconds | Acceptable interactive latency after warm-up |
| Groq latency / TPM | 60s provider timeout; frontend 60s spacing | Concurrent clicks previously caused rate-limit and dump collisions (mitigated in Phase 3.2) |
| Catalog size | Historical ~20k characters in some runs | Token cost and rate-limit pressure |
| Index persistence | In-memory only | Lost on process restart |

Monetary cost figures are **not** established in-repo and are omitted.

---

## 12. Current Limitations and Risks (Top)

1. **Groq / GPT-OSS tool-calling reliability** — intermittent HTTP 400 `tool_use_failed` with invalid tool name `commentary` (Harmony channel confusion). Strongly supported by Phase 3.4 audit.
2. **Polymorphic customer joins** — simultaneous INNER joins on `order_customer_accounts` and `order_customer_contacts` can return zero rows; no dedicated mutual-exclusion validator.
3. **Ollama as planning dependency** — embedding failure blocks Groq.
4. **Documentation drift** — root `README.md` still describes older tool names and flows.
5. **Production auth / multi-instance scale** — not designed.

---

## 13. POC Maturity Assessment

| Dimension | Rating | Notes |
|-----------|--------|-------|
| Functional breadth | High for a POC | List/count/aggregate/joins/clarification |
| Architectural clarity | High | Clear planner vs executor separation |
| Privacy posture | Strong for POC | Auditor + local formatting |
| Reliability under provider quirks | Medium | Tool-call failures intermittent |
| Operability | Medium | Warm-up, in-memory index, config flags |
| Production readiness | Low | Auth, HA, SRE, SLOs missing |

**Overall:** Suitable baseline for Technical Board architectural decisions; not a go-live candidate without a hardening phase.

---

## 14. Recommended Next Priorities

1. Capture and log Groq `failed_generation` on `tool_use_failed`; consider one bounded retry.
2. Re-run full non-live suite and publish current pass counts.
3. Align root README with current single-stage `query_crm` architecture.
4. Decide product policy for “latest” ambiguity (always clarify vs default Created On).
5. Design production inbound auth and remove/relax loopback-only carefully.
6. Evaluate index warm persistence or pre-build to cut cold-start impact.
7. Controlled experiment on polymorphic customer-link combinations.

---

## 15. Decisions Required from the Board

1. Is the **external Groq planner + local FetchXML executor** model accepted as the target architecture?
2. Is **clarification-first** for ambiguous “latest/top” questions acceptable UX, or should defaults be fixed?
3. What is the **production hosting** model for the API (on-prem only, identity model, TLS, auth)?
4. Are **TPM / cooldown** constraints acceptable for field rollout, or is a model/provider change required?
5. What is the **exit criteria** from POC to pilot (reliability %, approved entity set, security sign-off)?

---

## Related Documents

| Doc | Purpose |
|-----|---------|
| `02-Detailed-Technical-Architecture.md` | Deep technical reference |
| `03-Implementation-Evidence-Matrix.md` | Claim → source evidence |
| `04-Risks-Gaps-and-Next-Steps.md` | Prioritized risks |
| `05-Service-and-Class-Catalog.md` | Onboarding catalog |
