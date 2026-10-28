# Phase 5.1 — Final Generic CRM Engine Validation

Generated: 2026-10-28  
Environment: Dynamics 365 On-Premises v9.1 / ECRMBSS (Windows Default Credentials)  
Automated tests: **121 passed**, 0 failed

## Verdict

**Phase 5.1 complete.** Groq is offered **only** `query_crm`. All demo questions executed through `GenericCrmQueryBuilder` + `DynamicsGenericCrmRepository`. Fourth entity `lead` was enabled by **configuration only** (no entity-specific C# query builder). Capped Dynamics counts are never presented as exact totals. Outbound Groq payloads contained no CRM records.

## Supported entities (approved)

| Entity | Entity set | How enabled | Live status |
|--------|------------|-------------|-------------|
| `account` | `accounts` | `Crm:ApprovedEntities` + live EntityDefinitions | Verified |
| `contact` | `contacts` | `Crm:ApprovedEntities` + live EntityDefinitions | Verified |
| `salesorder` | `salesorders` | `Crm:ApprovedEntities` + live EntityDefinitions | Verified |
| `lead` | `leads` | **config only** (`Crm:ApprovedEntities` + aliases) | Verified list (5 rows); **no** `LeadQueryBuilder` / DTO / tool |

### Lead (fourth entity) — approved fields used by the generic engine

Live metadata discovery returned **203** readable attributes. Default `$select` (preferred safe subset) used:

`leadid`, `fullname`, `createdon`, `modifiedon`, `statuscode`, `statecode`, `emailaddress1`, `telephone1`, `firstname`, `lastname`, `jobtitle`, `address1_city`, `address1_country`, `subject` (plus primary-related lookups when present).

Primary: `leadid` / `fullname`. Entity set: `leads`.

## AI tool unification

| Before | After |
|--------|--------|
| Groq saw `get_sales_orders`, `query_sales_orders`, `query_crm`, account tools | Groq sees **`query_crm` only** (`CreateToolDeclarations`) |
| Legacy methods remain on `CrmAiTools` for unit tests | Chat remaps legacy names → `query_crm` via `CrmQueryIntentHelper` |

## End-to-end question matrix (live)

All requests selected **`query_crm`**, used **`GenericCrmQueryBuilder`**, executed via **`DynamicsGenericCrmRepository`**, `privacySafeToolExecution=true`, `outboundAiContainedCrmRecords=false`.

Customer PII in replies below is redacted.

| # | User question | Entity | Operation | AI tool | Builder | Result |
|---|---------------|--------|-----------|---------|---------|--------|
| 1 | Show me the latest 5 sales orders | salesorder | list + createdon desc | query_crm | Generic | **OK** — 5 rows |
| 2 | Show me the latest 5 accounts | account | list + createdon desc | query_crm | Generic | **OK** — 5 rows |
| 3 | Show me the latest 5 contacts | contact | list + createdon desc | query_crm | Generic | **OK** — 5 rows |
| 4 | Count active sales orders | salesorder | count + statecode=Active | query_crm | Generic | **OK** — “at least 5000 — not an exact total” |
| 5 | Find contacts created before March 2026 | contact | list + createdon lt | query_crm | Generic | **OK** — matched rows |
| 6 | Show accounts created after January 2026 | account | list + createdon ge | query_crm | Generic | **OK** — matched rows |
| 7 | Show the 5 highest-value sales orders | salesorder | list + totalamount desc | query_crm | Generic | **OK** — 5 rows |
| 8 | Show me the latest 5 leads | lead | list + createdon desc | query_crm | Generic | **OK** — 5 rows (config-only entity) |

### Direct Web API cross-check (diagnostic `/api/crm/diagnostics/query`)

Same generic engine; URIs (truncated):

| Probe | Relative URI (shape) | Rows / count |
|-------|----------------------|--------------|
| Latest SO | `salesorders?$select=…&$orderby=createdon desc&$top=5` | 5 |
| Latest accounts | `accounts?$select=…&$orderby=createdon desc&$top=5` | 5 |
| Latest contacts | `contacts?$select=…&$orderby=createdon desc&$top=5` | 5 |
| Count active SO | `salesorders?$filter=statecode eq 0&$count=true&$top=1` | count=5000, capped=true |
| Contacts before Mar 2026 | `contacts?…&$filter=createdon lt 2026-03-01…` | 5 |
| Accounts after Jan 2026 | `accounts?…&$filter=createdon ge 2026-02-01…` | 5 |
| Highest-value SO | `salesorders?…&$orderby=totalamount desc&$top=5` | 5 |
| Latest leads | `leads?$select=leadid,fullname,…&$orderby=createdon desc&$top=5` | 5 |

## Count limitations (v9.1)

Observed on ECRMBSS for active sales orders:

- `@odata.count` / total ≈ **5000**
- `@Microsoft.Dynamics.CRM.totalrecordcountlimitexceeded` = **true** (when Prefer annotations succeed)

Implementation:

- Count may send `Prefer: odata.include-annotations="*"` with fallback if Prefer fails.
- `CountMayBeCapped` / annotation drive copy: **“at least N — not an exact total”**.
- No unrestricted full-table scans for exact totals.

## Fourth-entity extensibility proof

1. Live EntityDefinitions confirmed `lead` / `leads` / `leadid` / `fullname` readable under AD credentials.
2. Enabled by adding `"lead"` to `Crm:ApprovedEntities` (+ aliases) in `appsettings*.json` only.
3. No `LeadQueryBuilder`, no `query_leads` tool, no lead DTO.
4. Live discovery + `GenericApprovedEntityStubFactory` fallback (no entity-specific builder).
5. Null `DisplayName` in Attribute metadata handled; option-set enrichment capped.
6. Automated test: `Fourth_Entity_Lead_Enabled_By_Config_Only`.

## Privacy audit

| Check | Result |
|-------|--------|
| `Crm:AllowSendLiveDataToAi` | `false` |
| Outbound Groq history | System + latest user message only (no CRM rows) |
| Tool results returned to Groq | No |
| Live chat audits | `safe=True`, `containsFunctionResults=False`, `suspiciousCrmText=False` |
| Response flags | `privacySafeToolExecution=true`, `outboundAiContainedCrmRecords=false` |
| Loopback restriction | `Crm:RestrictToLoopback=true` |
| CRM auth | Windows process default credentials |

## Security tests (automated + policy)

| Area | Result |
|------|--------|
| Unapproved entity | Rejected by `EntityApprovalPolicy` / auth |
| Injection in text filters | OData string escaping |
| Write-shaped operations | Denied by `CrmAuthorizationService` |
| Read-only HTTP | GET only via `DynamicsCrmHttp` |
| Legacy allowlist still denies random entities | `CrmEntityAllowlistTests` (static list); config approvals use `EntityApprovalPolicy` |

## Automated tests

```text
dotnet test
Passed!  - Failed: 0, Passed: 121, Skipped: 0
```

Includes Phase 5.1 unified routing, lead config-only enablement, count messaging, privacy auditor, and prior Phase 1–5 suites.

## Architecture

```
User → Groq (query_crm only)
    → CrmQueryIntentHelper (entity/operation correction; legacy remap)
    → CrmQueryPlanParser
    → EntityApprovalPolicy + ICrmMetadataService
    → GenericCrmQueryBuilder
    → IGenericCrmRepository (Dynamics | Mock)
    → GenericCrmReplyFormatter → Chat UI
```

## Hardening notes from this validation pass

- Fixed null `DisplayName` during Attribute metadata parse (blocked `lead` discovery).
- Default `$select` now prefers a safe column set (avoids virtual `*idname` / image fields that caused Web API 400).
- Option-label attach skips JSON nulls (`TryGetInt32` on Null threw for account/contact).
- Config-only entities fall back to `GenericApprovedEntityStubFactory` when live discovery fails.

## Residual limitations

- Exact org-wide counts above the Dynamics ceiling are unavailable without expensive scans (intentionally not done).
- One-hop relationships only; no unrestricted joins.
- Incoming HTTP callers remain unauthenticated — loopback POC only.
- Further entities: confirm readable under CRM AD identity, then add to `ApprovedEntities` (no new C# builders).
