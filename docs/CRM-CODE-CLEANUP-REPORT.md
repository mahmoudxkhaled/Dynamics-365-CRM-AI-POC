# CRM AI POC — Code Cleanup Report

**Branch:** `cleanup/legacy-removal`  
**Date:** 2026-10-28  
**Scope:** Remove obsolete mock/legacy SalesOrder-specific runtime paths while preserving the generic metadata-driven CRM query engine. No new features.

---

## Baseline (Phase B)

| Check | Result |
|-------|--------|
| Branch | `cleanup/legacy-removal` |
| `dotnet build` | Succeeded (0 errors) |
| `dotnet test` | **126 passed**, 0 failed |

---

## Inventory decisions (Phase A summary)

| Classification | Examples |
|----------------|----------|
| **REMOVE** | `ICrmDataProvider`, `MockCrmDataProvider`, `Dynamics365CrmProvider`, SalesOrder OData stack, entity-specific `CrmAiTools` methods, dead ChatService sales-order handlers, `SalesOrderIntentHelper`, invalid `customers` ApprovedEntities entry |
| **KEEP** | `query_crm`, ChatService privacy path, generic builder/repo/parser/metadata, `DynamicsCrmHttp`, WhoAmI, inspector, `EntityApprovalPolicy`, `CrmEntityAllowlist`, `RelativeDateResolver` / `BusinessTimeZone`, `CrmQueryIntentHelper` (legacy name remap), `StaticSeedCrmMetadata` / stub factory, Mock metadata/repo/seed for tests & `Crm:Provider=Mock` |
| **REFACTOR** | Diagnostics `/salesorders` and salesorder-schema (no longer patch legacy provider) |
| **REVIEW → resolved** | `customers` — not a Dynamics logical name; already an **account alias** |

### `customers` finding

`customers` is **not** a valid Dynamics entity logical name. It is an intentional **business alias** for `account` under `EntityAliases`. Listing it in `ApprovedEntities` caused invalid EntityDefinitions discovery attempts. Removed from `ApprovedEntities` in `appsettings.json`; alias retained.

---

## Files deleted

### Runtime

- `src/CrmAiPoc.Api/Crm/ICrmDataProvider.cs`
- `src/CrmAiPoc.Api/Crm/Mock/MockCrmDataProvider.cs`
- `src/CrmAiPoc.Api/Crm/Dynamics/Dynamics365CrmProvider.cs`
- `src/CrmAiPoc.Api/Crm/Query/SalesOrderQueryPlanParser.cs`
- `src/CrmAiPoc.Api/Crm/Query/SalesOrderODataQueryBuilder.cs`
- `src/CrmAiPoc.Api/Crm/Query/SalesOrderQueryExecutionResult.cs`
- `src/CrmAiPoc.Api/Crm/Query/SalesOrderFieldCatalog.cs`
- `src/CrmAiPoc.Api/Crm/Query/SalesOrderQueryPlan.cs`
- `src/CrmAiPoc.Api/Crm/Query/SalesOrderQueryEnums.cs`
- `src/CrmAiPoc.Api/Ai/SalesOrderIntentHelper.cs`
- `src/CrmAiPoc.Api/Ai/SalesOrderFieldCatalogAiHint.cs`

### Obsolete tests

- `tests/CrmAiPoc.Tests/SalesOrderIntentHelperTests.cs`
- `tests/CrmAiPoc.Tests/QuerySalesOrdersToolTests.cs`
- `tests/CrmAiPoc.Tests/SalesOrderQueryPlanParserTests.cs`
- `tests/CrmAiPoc.Tests/SalesOrderODataQueryBuilderTests.cs`
- `tests/CrmAiPoc.Tests/SalesOrderToolTests.cs`
- `tests/CrmAiPoc.Tests/MockCrmDataProviderTests.cs`
- `tests/CrmAiPoc.Tests/CrmAiToolsTests.cs` (entity-specific tool methods)

---

## Files modified

| File | Change |
|------|--------|
| `Ai/CrmAiTools.cs` | Only `query_crm` remains; ctor takes `IGenericCrmRepository` |
| `Ai/ChatService.cs` | Removed `ICrmDataProvider` / `Dynamics365CrmProvider` deps and dead sales-order handlers |
| `Ai/LocalCrmReplyFormatter.cs` | Slimmed to text/refusal helpers; row formatting via `GenericCrmReplyFormatter` |
| `Program.cs` | Removed `ICrmDataProvider` / `Dynamics365CrmProvider` DI |
| `Endpoints/CrmDiagnosticsEndpoints.cs` | `/salesorders` uses `IGenericCrmRepository`; schema discovery no longer mutates a legacy provider |
| `appsettings.json` | Removed `customers` from `ApprovedEntities` |
| Phase 4/5/5.1 & privacy tests | Updated to generic engine / `query_crm` |
| `docs/CRM-CODE-CLEANUP-INVENTORY.md` | Phase A inventory (pre-existing) |

### Files added

| File | Purpose |
|------|---------|
| `Crm/Query/RelativeDateToken.cs` | Enum previously colocated with deleted SalesOrder enums; required by generic engine |
| `tests/CrmAiPoc.Tests/QueryCrmToolTests.cs` | Regression coverage for `query_crm` |
| `docs/CRM-CODE-CLEANUP-REPORT.md` | This report |

---

## Legacy components removed

- Entity-specific AI tools: `search_accounts`, `get_contacts_for_account`, `count_accounts`, `get_sales_orders`, `query_sales_orders`
- Parallel SalesOrder query plan / OData builder / field catalog
- Dual CRM data provider interface (`ICrmDataProvider`) and both Mock/Dynamics implementations used by that interface
- Dead ChatService privacy handlers that could never be reached (Groq only receives `query_crm`)

---

## Components deliberately retained (and why)

| Component | Why |
|-----------|-----|
| `CrmAiTools.query_crm` | Required for Groq tool schema / `AIFunctionFactory` |
| `IGenericCrmRepository` + Dynamics/Mock generics | Production query path |
| `MockCrmSeedData` + `MockGenericCrmRepository` / `MockCrmMetadataService` | Offline `Crm:Provider=Mock` + unit tests (synthetic demo data only) |
| `StaticSeedCrmMetadata` / `GenericApprovedEntityStubFactory` | Metadata fallback + test seeds |
| `CrmEntityAllowlist` | Auth constants + confidential-account demo; works alongside `EntityApprovalPolicy` |
| `CrmQueryIntentHelper` legacy tool-name remap | Defensive if the model invents old tool names |
| `DynamicsSchemaDiscoveryService` + salesorder diagnostic GETs | Useful read-only CRM diagnostics |
| `RelativeDateResolver` / `BusinessTimeZone` / `RelativeDateToken` | Used by generic query builder |
| Model types `CrmAccount` / `CrmContact` / `CrmSalesOrder` | Still used by mock seed + authorization helpers |

---

## DI registrations removed or modified

**Removed**

- `AddSingleton<Dynamics365CrmProvider>()`
- `AddSingleton<ICrmDataProvider>(…)` / `AddSingleton<ICrmDataProvider, MockCrmDataProvider>()`

**Unchanged (still registered)**

- `ICrmMetadataService` → Dynamics or Mock
- `IGenericCrmRepository` → Dynamics or Mock
- `DynamicsCrmHttp`, connectivity, schema discovery
- `CrmAiTools`, `ChatService`, authorization, metadata registry, execution inspector

---

## Configuration changes

- `Crm:ApprovedEntities`: removed invalid `"customers"` entry (aliases unchanged).
- Live Development settings already listed approved entities correctly (`account`, `contact`, `salesorder`, `lead`).
- No change to `AllowSendLiveDataToAi`, loopback, auth mode, or approved-entity policy semantics.

---

## Final production execution flow

```
Browser
  → POST /api/chat
  → ChatService
  → Groq (tool schema: query_crm only)
  → query_crm structured arguments
  → CrmQueryPlanParser
  → CrmAuthorizationService + EntityApprovalPolicy
  → Live/Mock CRM Metadata
  → GenericCrmQueryBuilder
  → DynamicsGenericCrmRepository | MockGenericCrmRepository
  → DynamicsCrmHttp (live) → Dynamics 365 On-Prem v9.1
  → GenericCrmReplyFormatter (local; CRM rows never sent to Groq)
  → Browser (+ Execution Inspector stages)
```

---

## Post-cleanup verification (Phase I)

| Check | Status |
|-------|--------|
| Groq exposes only `query_crm` | Pass (`CreateToolDeclarations`) |
| ChatService routes through generic repository | Pass |
| No active SalesOrder-specific runtime OData builders | Pass (deleted) |
| No runtime `ICrmDataProvider` mock/Dynamics fallback in DI | Pass |
| Live metadata discovery intact | Pass (`DynamicsCrmMetadataService`) |
| No real CRM customer records in source | Pass (synthetic mock seed only) |
| Inspector / diagnostics endpoints compile | Pass |
| Remaining source refs to legacy tools | Only intentional remap lists in `CrmQueryIntentHelper` |

---

## Build & test results (after cleanup)

| Check | Result |
|-------|--------|
| `dotnet build` | Succeeded (pre-existing CA1416 / nullable warnings only) |
| `dotnet test` | **71 passed**, 0 failed |

Test count drop (126 → 71) is expected: obsolete SalesOrder-specific and entity-tool tests were removed and replaced with generic/`query_crm` coverage where needed.

### Regression coverage retained (mocked)

- Generic account / contact / salesorder / lead queries
- Filters, sort, count, limits, relationship one-hop
- Invalid entity / OData-injection-shaped field rejection
- Privacy outbound audit / `AllowSendLiveDataToAi` refuse on Dynamics provider
- Intent remap of legacy tool names → `query_crm`
- Execution inspector unit tests

**Live CRM integration was not re-run** as part of this cleanup (read-only live verification was out of automated scope).

---

## Remaining technical debt

1. Historical docs (`README.md`, Phase 3/4 docs) still describe removed tools/providers — update in a docs pass if desired.
2. `CrmEntityAllowlist` partially overlaps `EntityApprovalPolicy`; keep until auth is fully policy-driven.
3. `CrmQueryIntentHelper` still knows legacy tool names for remapping — intentional safety net.
4. Diagnostics `/salesorders` remains a convenience GET; prefer `/diagnostics/query` for new work.
5. Mock seed data uses synthetic demo company names for offline tests — not live CRM data.

---

## Final rule compliance

- No multi-entity joins feature work
- No new AI tools / provider change
- Generic engine behavior preserved
- Approved entities unchanged except invalid `customers` entry
- No deploy/push performed by this cleanup
