# Risks, Gaps, and Next Steps

**Document:** 04-Risks-Gaps-and-Next-Steps  
**Baseline date:** 2026-11-05  
**Rule:** Recommendations only — no implementation in this documentation cycle.

Severity: **Critical** | **High** | **Medium** | **Low**

---

## 1. What Is Currently Strong

| Area | Why it is strong |
|------|------------------|
| Planner vs executor separation | Groq plans; backend validates/executes FetchXML |
| Privacy boundary for AI | No CRM-row round-trip; outbound auditor; local formatting |
| Metadata-driven design | Approved entities + semantic compact catalog |
| Clarification UX | Structured JSON → chips → independent re-plan |
| Offline developability | Mock CRM + large non-live test surface |
| Operational diagnostics | Correlation IDs, FailureKind logging, semantic index Built/Reused |

---

## 2. Prioritized Risk Register

| Issue | Severity | Evidence | Business Impact | Proposed Direction |
|-------|----------|----------|-----------------|--------------------|
| Groq GPT-OSS `tool_use_failed` / invalid tool `commentary` | **High** | Phase 3.4 audit; user-reported HTTP 400; Harmony channel semantics | Intermittent planning failures for valid questions | Log `failed_generation`; one bounded retry; optional prompt guard against invented tool/channel names; evaluate model stability |
| Polymorphic customer dual INNER joins return empty sets | **High** | Known Dynamics metadata (`order_customer_accounts` + `order_customer_contacts` share `customerid`); prior verification | Users see “no data” despite existing orders | Product rule or validator to forbid mutually exclusive INNER pairs; prompt examples; UX clarification |
| Ollama SPOF for planning | **High** | ChatService refuses Groq when semantic Error / no entity hits | Assistant unavailable if local embeddings down | Health checks; clearer ops runbooks; optional degraded mode policy (board decision) |
| Cold semantic index rebuild after restart | **Medium** | In-memory index; historical multi-minute cold builds ~2548 docs | Poor first-user experience after recycle | Pre-warm job; persistence evaluation; staged rollout windows |
| Groq TPM / concurrent request pressure | **Medium** | Historical 429/timeouts; frontend 60s cooldown (UX only) | Slow UX; still bypassable via multiple clients | Server-side rate limit; quota monitoring; keep cooldown |
| Unhelpful / frequent clarifications for “latest” | **Medium** | Prompt ambiguity rules + createdon/modifiedon both in catalog | Extra click friction | Product default (Created On) vs always clarify |
| Root README / older phase docs drift | **Medium** | README mentions obsolete tools/flows | Wrong board/engineering assumptions | Refresh README to match Document 02 |
| Loopback-only + AllowAnyOrigin POC posture | **High** (if treated as production) | `LoopbackOnlyMiddleware`, CORS policy | Unsafe exposure if deployed without auth | Production auth design before opening network |
| Incomplete capture of Groq error bodies | **Medium** | Auditor/classifier logs message but not structured `failed_generation` | Slow RCA on provider failures | Structured provider error logging (sanitized) |
| Live test results not refreshed in this baseline | **Medium** | Docs authored without live Groq/CRM runs | Unknown current reliability % | Scheduled gated live suite with published report |
| In-memory conversation store | **Low–Medium** | `ConversationStore` singleton | Lost sessions on recycle; not multi-instance safe | Sticky sessions or external store if scaling |
| Prompt dual-mode complexity (tool vs JSON) | **Medium** (hypothesis contributor) | SystemPrompt + Auto tool mode | May increase malformed tool emissions | Controlled prompt A/B (Document 02 experiment plan) |

---

## 3. Assessment by Dimension

### 3.1 Correctness and reliability

**Good:** Plan validation, entity approval, FetchXML construction covered by many non-live tests; clarification validation against catalog.

**Risky:** Provider tool-call parsing; polymorphic joins; relationship auto-resolution edge cases; dependence on semantic Top-K completeness (missed attributes → weaker plans).

**Next:** Provider RCA logging + retry; join mutual-exclusion policy; targeted live golden questions report.

### 3.2 Security and privacy

**Good:** CRM rows not returned to Groq on chat path; auditor; write refusal; approved entities; secrets via env/user-secrets.

**Risky:** No production inbound auth; permissive CORS; diagnostic dumps in Development temp; log content hygiene not fully audited.

**Next:** AuthN/Z design; tighten CORS; dump retention policy; security review before pilot.

### 3.3 Performance

**Good:** Warm semantic reuse; compact catalog vs full schema; single-flight index build.

**Risky:** Cold embed cost; Groq latency under 60s timeout; catalog/token growth as approved entities expand.

**Next:** Measure cold/warm/p95 e2e on target hardware; catalog budget alarms.

### 3.4 Scalability

**Good:** Stateless AI call per question (no Groq history).

**Risky:** In-memory index and conversation store; single API process assumptions; TPM shared across users.

**Next:** Define horizontal scale story (shared index cache, sticky vs redis sessions, per-user quotas).

### 3.5 Maintainability

**Good:** Clear folders (Ai / Crm / Security); options pattern; tests by concern.

**Risky:** Documentation drift; large `ChatService` / discovery classes; dual date APIs (legacy tokens + structured ranges).

**Next:** Keep architecture-review docs as baseline; incremental refactors only with board priority.

### 3.6 User experience

**Good:** Clarification chips; dynamic tables; English assistant messaging; cooldown feedback.

**Risky:** Forced 60s wait; clarification fatigue; opaque planner failures mapped to generic messages.

**Next:** UX copy for provider failures; optional shorter cooldown after board/quota review.

### 3.7 Testing and release readiness

**Good:** Broad mock/unit coverage including privacy, FetchXML, clarification, concurrency, timeout/retry.

**Risky:** Live suites env-gated and not part of default CI signal; Angular unit tests minimal relative to API; no published reliability KPI.

**Next:** Make non-live suite a release gate; define live smoke checklist for pilot environments.

---

## 4. Recommended Next-Step Roadmap (non-binding)

### Immediate (correctness / observability)

1. Structured logging of Groq `tool_use_failed` + sanitized `failed_generation`.
2. Re-run and publish current non-live test counts.
3. Update root README to match single-stage `query_crm` architecture.

### Short-term (reliability / UX)

4. Bounded retry for `tool_use_failed` within 60s budget.
5. Polymorphic join guard or clarification rule.
6. Product decision on “latest” default vs clarify.

### Pilot readiness

7. Inbound authentication and network exposure model.
8. Ops runbook: Ollama + API + Dynamics credentials + warmup.
9. Golden-path live evaluation set (Sales Order list/count/join/aggregate/clarify).

### Explicitly defer unless prioritized

- Provider/model replacement
- Persistent vector DB
- Multi-turn Groq memory
- Write operations

---

## 5. Separation: Bugs vs Optimizations

| Treat as correctness/security | Treat as optimization |
|-------------------------------|------------------------|
| tool_use_failed causing failed plans | Faster cold index |
| Empty results from illegal dual customer INNER joins | Smaller catalogs |
| Missing auth if exposed beyond loopback | Token cost tuning |
| CRM rows leaking to AI (regression) | UI polish |

---

## 6. Board-Oriented Bottom Line

The POC demonstrates a coherent, privacy-aware, metadata-driven CRM assistant architecture suitable as an **engineering baseline**.

It is **not** yet a pilot-ready production service primarily due to **provider tool-call reliability**, **join edge cases**, **operational dependencies (Ollama)**, and **missing production authentication**.

Use Documents 01–03 as the shared source of truth; use this document to prioritize the next phase without changing runtime behavior until decisions are approved.
