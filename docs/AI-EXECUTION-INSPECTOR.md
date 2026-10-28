# AI Execution Inspector

Development-only side panel that shows **real** backend execution events for each chat message.

## What you see

| Area | Content |
|------|---------|
| Execution ID | Correlation id shared by chat request + inspector poll |
| Status | Running / Success / Failed / Blocked |
| AI tool | Provider, model, tool name, **sanitized** Groq arguments |
| CRM query | Entity, entity set, filters, sort, OData path (redacted), HTTP status, record **count** |
| Timeline | Only stages that actually ran, with measured durations |
| Privacy | Confirms outbound Groq payload had no CRM rows |

CRM **row payloads are never stored** in the diagnostic trace.

## Layout

- Desktop: Chat ~55% · Inspector ~45% (drag the splitter; Toggle inspector collapses it)
- Mobile: Chat / Inspector tabs

Click a prior user bubble to reopen that message’s trace.

## Live updates

1. UI generates an `executionId` and starts polling `GET /api/diagnostics/executions/{id}` every ~400ms.
2. Chat `POST /api/chat` includes the same `executionId`.
3. Backend appends steps as ChatService / GenericCrmRepository run.
4. Polling stops when status ≠ `Running`. SSE is also available at `.../events`.

## Security

| Control | Behavior |
|---------|----------|
| `Crm:EnableExecutionInspector` | `false` in `appsettings.json`; `true` only in Development |
| Host environment | Endpoints return 404 unless `IsDevelopment()` **and** flag enabled |
| Loopback | Existing `RestrictToLoopback` still applies to `/api/*` |
| Redaction | GUIDs, emails, phones, sensitive argument keys, quoted OData filter values |
| Groq | `AllowSendLiveDataToAi` remains `false`; inspector does not change that |

Frontend visibility is **not** a security boundary.

## Instrumented stages (when executed)

1. HTTP Chat Request Received  
2. Privacy Verification (outbound)  
3. Groq API Request Prepared  
4. Groq API Response Received  
5. AI Tool Selected  
6. Structured Query Plan Parsed  
7. Backend Validation  
8. Metadata / Relationship Resolution  
9. Generic OData Query Construction  
10. Dynamics 365 HTTP Request / Response  
11. Local Response Formatting  
12. Final Chat Response  

A refused delete (e.g. “Delete all accounts”) shows **Blocked** without Dynamics query success steps.

## API (Development + flag)

- `GET /api/diagnostics/executions` — recent traces  
- `GET /api/diagnostics/executions/{id}` — snapshot JSON  
- `GET /api/diagnostics/executions/{id}/safe-text` — copy-friendly sanitized text  
- `GET /api/diagnostics/executions/{id}/events` — SSE stream while Running  

## Tests

`ExecutionInspectorTests` covers correlation isolation, redaction, failed-step status, and Development-only enablement.

## Limits

- In-memory, bounded (~100 traces); process restart clears history  
- No external APM / database  
- Token usage shown only if the AI client exposes it (not fabricated)  
