# Dynamics 365 CRM AI POC

Minimal proof of concept: an AI model answers natural-language CRM questions (English/Arabic) by selecting **approved, read-only .NET functions**. CRM defaults to **synthetic mock**; live Dynamics 365 On-Premises (ECRMBSS) can be enabled separately.

**Three AI providers are built in** and selected by config:

| `Ai:Provider` | Backend | Default model |
|---------------|---------|---------------|
| `Groq` (**default**) | Groq OpenAI-compatible API | `openai/gpt-oss-120b` |
| `Gemini` | Google AI Studio via `Google.GenAI` | `gemini-3.8-flash` |
| `OpenAI` | OpenAI via `Microsoft.Extensions.AI.OpenAI` | `gpt-4o-mini` |

Function calling uses `Microsoft.Extensions.AI` (`UseFunctionInvocation` + `AIFunctionFactory`) for all providers.

## Flow

```
User Question
    → ASP.NET Core POST /api/chat (loopback-only POC)
    → Groq / Gemini / OpenAI (tool selection only; no UseFunctionInvocation round-trip of CRM rows)
    → Approved tools (… | get_sales_orders | query_sales_orders)
    → Backend validation + allowlisted OData builder
    → ICrmDataProvider (Mock | Dynamics365)
    → Local Arabic/English formatter (CRM rows never sent to the LLM)
```

Phase 4 dynamic salesorder queries: see `docs/PHASE4-DYNAMIC-SALESORDER-QUERIES.md`.

## Solution structure

```
CrmAiPoc.slnx
src/CrmAiPoc.Api/
  Ai/                 ChatService, CrmAiTools, LazyGemini / LazyOpenAi / LazyGroq clients
  Crm/                ICrmDataProvider, Mock + Dynamics adapters, validation
  Security/           Allowlist + server-side authorization
  Endpoints/          POST /api/chat
  wwwroot/            Minimal chat UI
tests/CrmAiPoc.Tests/ Validation, auth, tool execution tests
```

## Prerequisites

- .NET 8 SDK (or newer SDK targeting `net8.0`)
- Gemini (AI Studio) API key and/or OpenAI API key

## Configuration

Switch provider:

```json
"Ai": { "Provider": "Groq" }
```

Also `"Gemini"` or `"OpenAI"`. Local keys can live in `appsettings.Development.json`.

```bash
cd src/CrmAiPoc.Api
dotnet user-secrets set "Ai:Provider" "Groq"
dotnet user-secrets set "Groq:ApiKey" "<groq-key>"
dotnet user-secrets set "Gemini:ApiKey" "<ai-studio-key>"
dotnet user-secrets set "OpenAI:ApiKey" "<openai-key>"
```

| Key | Default | Meaning |
|-----|---------|---------|
| `Ai:Provider` | `Groq` | `Groq`, `Gemini`, or `OpenAI` |
| `Groq:ApiKey` / `GROQ_API_KEY` | _(secret)_ | Groq API key |
| `Groq:ModelName` | `openai/gpt-oss-120b` | Groq model |
| `Groq:BaseUrl` | `https://api.groq.com/openai/v1` | Groq OpenAI-compatible endpoint |
| `Gemini:ApiKey` / `GEMINI_API_KEY` | _(secret)_ | Google AI Studio key |
| `Gemini:ModelName` | `gemini-3.8-flash` | Gemini model |
| `OpenAI:ApiKey` / `OPENAI_API_KEY` | _(secret)_ | OpenAI API key |
| `OpenAI:ModelName` | `gpt-4o-mini` | OpenAI model |
| `Crm:Provider` | `Mock` | `Mock` or `Dynamics365` |
| `Crm:EnableLiveIntegration` | `false` | Required for any live Dynamics HTTP call |
| `Crm:BaseUrl` | ECRMBSS Web API URL | `https://ecrmdev9.te.eg/ECRMBSS/api/data/v9.1/` |
| `Crm:UseDefaultCredentials` | `true` | Windows/AD default credentials |
| `CRM_USERNAME` / `CRM_PASSWORD` / `CRM_DOMAIN` | _(env)_ | Optional explicit AD credentials (never hardcoded/logged) |
| `Crm:AllowSendLiveDataToAi` | `false` | Must be true before live rows are returned to Groq tools |

## Run

```bash
dotnet restore
dotnet build
dotnet test
dotnet run --project src/CrmAiPoc.Api
```

- Chat UI: `http://localhost:<port>/`
- Swagger: `http://localhost:<port>/swagger`
- Health: `GET /api/health`

### Example request

```http
POST /api/chat
Content-Type: application/json

{
  "message": "Show me accounts containing Ahmed"
}
```

Follow-up (reuse `conversationId`):

```json
{
  "conversationId": "<id-from-previous-response>",
  "message": "Show me contacts for this account."
}
```

## Approved AI tools (only)

| Function | Purpose |
|----------|---------|
| `search_accounts(name, limit)` | Partial name search |
| `get_contacts_for_account(accountId, limit)` | Contacts for an account |
| `count_accounts()` | Visible account count |
| `get_sales_orders(limit)` | Recent sales orders (Mock always; live Dynamics only if `AllowSendLiveDataToAi=true`) |

There are **no** create/update/delete tools. The model cannot run SQL, FetchXML, HTTP, or arbitrary C#.

## Phase 2 — Live Dynamics diagnostics

With `Crm:EnableLiveIntegration=true` and BaseUrl set (Development already points at ECRMBSS):

- `GET /api/crm/diagnostics/whoami`
- `GET /api/crm/diagnostics/salesorder-entity`
- `GET /api/crm/diagnostics/salesorder-schema` → writes `docs/CRM-SALESORDER-SCHEMA.md`
- `GET /api/crm/salesorders?limit=5` → **backend-only** retrieval (not sent to Groq)

Status notes: `docs/CRM-CONNECTIVITY-STATUS.md`

To use Dynamics as the data provider for tools:

```json
"Crm": {
  "Provider": "Dynamics365",
  "EnableLiveIntegration": true,
  "BaseUrl": "https://ecrmdev9.te.eg/ECRMBSS/api/data/v9.1/",
  "UseDefaultCredentials": true,
  "AllowSendLiveDataToAi": false
}
```

Do not set `AllowSendLiveDataToAi=true` — Phase 3 uses a **privacy-safe chat path**: Groq only selects `get_sales_orders(limit)`; the backend queries Dynamics and formats the Chat UI reply locally. CRM records are never returned to Groq.

### Phase 3 privacy-safe chat

1. User: `هاتلي آخر 5 Sales Orders`
2. Groq returns tool call `get_sales_orders` with `limit`
3. Backend validates + WhoAmI + Dynamics read
4. .NET builds Arabic/English reply + structured data for the UI
5. Outbound Groq payload audit asserts no CRM tool results / order payloads

## Security (backend-enforced)

- Read-only surface area
- Server-side argument validation and max page size
- Explicit entity/field allowlist (`account`, `contact`)
- Mock user permissions deny confidential account `aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee`
- Client-supplied role names are **not** used for authorization
- Empty tool results are returned as-is (no invented CRM data)
- Live CRM is not connected; no real customer data

## Example conversations

| User | Expected |
|------|----------|
| `Show me accounts containing Ahmed.` | Gemini calls `search_accounts`; answer lists Ahmed Trading Co / Ahmed Technologies from mock data |
| `كم عدد العملاء الموجودين؟` | Calls `count_accounts`; Arabic reply with count `5` |
| `Show me contacts for this account.` | Uses conversation context or asks for clarification |
| `Delete all accounts.` | Refuses — no delete tool; backend policy is read-only |
| `Show confidential CRM data I don't have access to.` | Backend denies confidential account |
| `What is the weather today?` | Explains CRM-only scope |

### Sample multi-turn demo

1. **User:** Show me accounts containing Ahmed.  
   **Assistant:** Lists matching mock accounts (and stores last account in session when unambiguous).
2. **User:** Show me contacts for this account.  
   **Assistant:** Calls `get_contacts_for_account` with the resolved GUID.
3. **User:** Delete all accounts.  
   **Assistant:** Refuses; no write tools exist.

## Diagnostic logs

Development logs include:

- User question (length / language hint)
- Selected AI function
- Parsed / validated arguments (non-sensitive)
- Authorization decision
- Function execution result summary
- Final AI response length

Never logs API keys or raw customer PII dumps.

## Dynamics On-Prem later

`ICrmDataProvider` + `DynamicsOnPremCrmDataProvider` support v9.1 Web API GET with Windows auth when explicitly enabled. **Not integrated or tested against live ECRMBSS.** Keep `Crm:Provider=Mock` for this POC.

## Tests

```bash
dotnet test
```

Covers validation, allowlist, authorization allow/deny, mock provider relationships, and tool execution.

## Honest status

| Area | Status |
|------|--------|
| `dotnet restore` / `build` / `test` | Run as part of delivery |
| Live Gemini / OpenAI chat | Requires the selected provider’s API key |
| Live Dynamics ECRMBSS | **Not connected / not tested** |

## References

- [Prompt a model (OpenAI + MEAI)](https://learn.microsoft.com/en-us/dotnet/ai/quickstarts/prompt-model?pivots=openai)
- [Microsoft.Extensions.AI tool calling](https://learn.microsoft.com/en-us/dotnet/ai/conceptual/calling-tools)
