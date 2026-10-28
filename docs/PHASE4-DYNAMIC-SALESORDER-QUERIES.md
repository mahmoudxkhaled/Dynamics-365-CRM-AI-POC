# Phase 4 — Dynamic Sales Order Query Builder

Generated: 2026-10-28

## Summary

Phase 4 extends the privacy-safe CRM AI assistant with a structured `query_sales_orders` tool. Groq selects an approved tool and structured arguments only. The ASP.NET Core backend validates a query plan against a verified `salesorder` allowlist, builds safe OData, executes it with Windows Default Credentials against ECRMBSS, and formats Arabic/English replies locally. Live CRM rows are never returned to Groq (`Crm:AllowSendLiveDataToAi = false`).

## Flow

```
User question
  → Groq tool selection (query_sales_orders | get_sales_orders | …)
  → Backend plan parse + allowlist validation
  → SalesOrderODataQueryBuilder (safe OData URI)
  → Dynamics 365 Web API (read-only, AD identity)
  → LocalCrmReplyFormatter
  → Chat UI
```

Outbound Groq payload contains: system routing prompt, prior **user** questions only, current question, and approved tool schemas. It does **not** contain CRM rows, tool results, or locally formatted order lists.

## Verified salesorder field allowlist

Source: live `EntityDefinitions` + empirical OData probes on ECRMBSS v9.1.

| Field | Type | Filter | Sort | Notes |
|-------|------|--------|------|-------|
| `salesorderid` | Guid | eq/ne | no | Primary key |
| `name` | String | eq/ne/contains | no | Primary name; searchable |
| `ordernumber` | String | eq/ne/contains | no | Searchable |
| `createdon` | DateTime | comparisons + relative | yes | Default sort |
| `modifiedon` | DateTime | comparisons + relative | no | |
| `totalamount` | Money | comparisons | yes | Verified `gt 10000` |
| `statuscode` | Status | eq/ne | no | Labels below |
| `statecode` | State | eq/ne | no | **Active = 0** |
| `_te_customeraccountid_value` | Lookup GUID | eq/ne | no | Alias `te_customeraccountid`; GUID only |
| `_customerid_value` | Lookup GUID | eq/ne | no | Alias `customerid`; GUID only |

### statuscode (verified OptionSet)

`1=New`, `2=Pending`, `3=In Progress`, `4=No Money`, `100001=Complete`, `100002=Partial`, `100003=Invoiced`

### statecode (verified OptionSet)

`0=Active`, `1=Submitted`, `2=Canceled`, `3=Fulfilled`, `4=Invoiced`

Customer **name** search via lookup is **not** supported in this phase (would require a verified related-entity query).

## Structured query plan (DTO)

- `Operation`: `list` | `count`
- `Filters[]` (max 3): `Field`, `Operator` (`eq|ne|gt|ge|lt|le|contains`), `Value`
- `RelativeDate`: `{ Field, Token }` where token ∈ `last_calendar_month`, `this_calendar_month`, `this_week`, `last_7_days`, `last_30_days`
- `SortField`: `createdon` | `totalamount`
- `SortDirection`: `asc` | `desc`
- `Limit`: default 10, max 50 (list only)

Relative dates are resolved on the backend using `Crm:BusinessTimeZoneId` (default `Africa/Cairo`). Calendar months use business-local midnights converted to UTC half-open ranges `[start, end)`. Rolling windows (`last_7_days`, `last_30_days`) use the server UTC clock — not the model’s notion of “today”.

## Example query plans → OData (synthetic)

### 1. Latest 10

Plan: `{ operation: list, limit: 10 }`

```
salesorders?$select=salesorderid,name,ordernumber,createdon,modifiedon,totalamount,statuscode,statecode,_te_customeraccountid_value,_customerid_value&$orderby=createdon desc&$top=10
```

### 2. Last calendar month

Plan: `{ relativeDate: { field: createdon, token: last_calendar_month }, limit: 10 }`

```
…&$filter=createdon ge 2026-08-31T21:00:00Z and createdon lt 2026-09-30T21:00:00Z&$orderby=createdon desc&$top=10
```

(Exact UTC literals depend on Egypt offset for that date.)

### 3. Amount greater than 10000

Plan: `{ filters: [{ field: totalamount, operator: gt, value: "10000" }] }`

```
…&$filter=totalamount gt 10000&$orderby=createdon desc&$top=10
```

### 4. Active state

Plan: `{ filters: [{ field: statecode, operator: eq, value: "Active" }] }`

```
…&$filter=statecode eq 0&…
```

### 5. Name contains ABC

```
…&$filter=contains(name,'ABC')&…
```

Injection attempt `ABC' or 1 eq 1` becomes `contains(name,'ABC'' or 1 eq 1')`.

### 6. Count this week

Plan: `{ operation: count, relativeDate: { field: createdon, token: this_week } }`

```
…&$filter=createdon ge … and createdon lt …&$count=true&$top=1
```

### Count ceiling

Dynamics On-Prem v9.1 returned `@odata.count = 5000` for broad queries. When count equals `5000`, the UI reports **at least 5000** and states the true total may be higher. Incomplete counts are never presented as exact totals.

## Groq tools

| Tool | Purpose |
|------|---------|
| `get_sales_orders` | Backward-compatible latest N |
| `query_sales_orders` | Structured filters / relative dates / sort / count |

`query_sales_orders` parameters: `operation`, `filtersJson`, `relativeDateField`, `relativeDateToken`, `sortField`, `sortDirection`, `limit`.

## Privacy & loopback

- `Crm:AllowSendLiveDataToAi = false` (required)
- CRM results stay in the backend / Chat UI structured data
- Conversation history sent to Groq excludes assistant CRM replies
- `Crm:RestrictToLoopback = true` — non-loopback `/api/*` callers receive HTTP 403 until incoming auth exists
- All CRM operations remain GET/read-only

## Project structure (Phase 4 additions)

```
src/CrmAiPoc.Api/Crm/Query/
  SalesOrderQueryEnums.cs
  SalesOrderQueryPlan.cs
  SalesOrderQueryPlanParser.cs
  SalesOrderFieldCatalog.cs
  SalesOrderODataQueryBuilder.cs
  RelativeDateResolver.cs
  BusinessTimeZone.cs
  SalesOrderQueryExecutionResult.cs
src/CrmAiPoc.Api/Security/LoopbackOnlyMiddleware.cs
docs/PHASE4-DYNAMIC-SALESORDER-QUERIES.md
tests: SalesOrderODataQueryBuilderTests, SalesOrderQueryPlanParserTests,
       QuerySalesOrdersToolTests, Phase4PrivacyAndSecurityTests
```

## Live ECRMBSS demos (2026-10-28, read-only, AD default credentials)

| Demo | OData intent | Result |
|------|--------------|--------|
| 1 | `totalamount gt 10000` orderby amount desc top 3 | 3 rows (amounts present; customer names not logged) |
| 2 | `statecode eq 0` (Active) orderby createdon desc top 3 | 3 rows, recent createdon values |
| 3 | `createdon ge 2026-10-21` + `$count=true` | `@odata.count=8` (not capped) |

## Demo questions (AR/EN)

1. هات آخر 10 Sales Orders → `get_sales_orders` / `query_sales_orders` limit 10  
2. هات الـ Sales Orders اللي اتعملت الشهر اللي فات → `last_calendar_month`  
3. Show orders created during the last 7 days → `last_7_days`  
4. Show sales orders with an amount greater than 10000 → `totalamount gt 10000`  
5. هات الـ Orders اللي حالتها Active → `statecode eq Active`  
6. Find sales orders containing ABC in their name → `contains(name,…)`  
7. Count sales orders created this week → `operation=count` + `this_week`  
8. Show the 5 highest-value sales orders → `sortField=totalamount`, `limit=5`

## Boundary note

The user’s original natural-language question is still sent to Groq for tool selection. Do not paste customer-sensitive free text into the chat if that question itself must not leave the machine.
