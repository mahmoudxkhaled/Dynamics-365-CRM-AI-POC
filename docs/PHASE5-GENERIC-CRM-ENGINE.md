# Phase 5 — Generic Metadata-Driven Dynamics 365 AI Query Engine

Generated: 2026-10-28

## Goal

Replace per-entity query stacks with **one reusable engine** driven by CRM metadata and configuration. Approving a new readable entity should not require a new C# query builder, AI function, or DTO.

## Architecture

```
User question
  → Groq tool selection (query_crm preferred; legacy tools kept)
  → Backend CrmQueryPlan parse + EntityApprovalPolicy
  → ICrmMetadataService (cached EntityDefinitions)
  → GenericCrmQueryBuilder (safe OData)
  → IGenericCrmRepository (Dynamics | Mock)
  → GenericCrmReplyFormatter (local AR/EN)
  → Chat UI
```

CRM rows never return to Groq (`AllowSendLiveDataToAi=false`). Loopback restriction remains.

## Components

| Component | Role |
|-----------|------|
| `EntityApprovalPolicy` | Config-driven AI entity allowlist + aliases |
| `CrmMetadataRegistry` | In-memory metadata cache |
| `ICrmMetadataService` | Discover/refresh approved entities only |
| `DynamicsCrmMetadataService` | Live v9.1 EntityDefinitions + OptionSets + relationships |
| `MockCrmMetadataService` | Seed metadata for tests/offline |
| `CrmQueryPlan` | Structured non-executable plan |
| `GenericCrmQueryBuilder` | Validate + build OData |
| `CrmRelationshipResolver` | One-hop approved relationships → GUID filter |
| `DynamicsGenericCrmRepository` / `MockGenericCrmRepository` | Execute read-only queries |
| `query_crm` | Single generic AI tool |

## Configuration

```json
"Crm": {
  "ApprovedEntities": [ "account", "contact", "salesorder" ],
  "EntityAliases": {
    "salesorder": [ "sales order", "أوامر البيع" ]
  },
  "MetadataCacheSeconds": 3600
}
```

Discovered-but-unapproved entities are **not** exposed to Groq.

## Generic query plan example

```json
{
  "entity": "salesorder",
  "operation": "list",
  "filters": [{ "field": "totalamount", "operator": "gt", "value": "10000" }],
  "sort": [{ "field": "totalamount", "direction": "desc" }],
  "limit": 5
}
```

Relationship (one hop):

```json
{
  "entity": "contact",
  "relatedFromEntity": "account",
  "relatedFromId": "11111111-1111-1111-1111-111111111111",
  "limit": 10
}
```

→ filter `_parentcustomerid_value eq {guid}` using verified relationship metadata (no invented navigation joins).

## Diagnostics (loopback)

- `GET /api/crm/diagnostics/entities`
- `GET /api/crm/diagnostics/entities/{logicalName}`
- `POST /api/crm/diagnostics/metadata/refresh`
- `POST /api/crm/diagnostics/query` (returns URI/summaries; not full row dumps by default)

## Backward compatibility

Kept working:

- `search_accounts`, `get_contacts_for_account`, `count_accounts`
- `get_sales_orders`, `query_sales_orders`
- Phase 4 salesorder field catalog / builder
- Privacy-safe chat path
- Existing automated tests

## Security notes

- Read-only GET only
- Approved entities/fields/operators only
- OptionSet labels resolved from metadata
- Lookup filters require GUIDs
- Count ceiling (5000) never reported as exact when capped
- Process Windows identity; no remote multi-user AD auth claimed

## Adding a new entity later

1. Add logical name to `Crm:ApprovedEntities` (+ optional aliases).
2. Refresh metadata (`POST .../metadata/refresh`).
3. Use `query_crm` — **no new builder/tool/DTO required**.

(Ensure the entity is readable under the current CRM identity.)
