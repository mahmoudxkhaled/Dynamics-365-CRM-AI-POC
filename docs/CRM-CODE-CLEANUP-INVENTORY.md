# Cleanup inventory (Phase A)

| Component | Classification | Notes |
|-----------|----------------|-------|
| `query_crm` + ChatService privacy path | KEEP | Production spine |
| Generic builder/repo/parser/metadata | KEEP | |
| `DynamicsCrmHttp`, WhoAmI, loopback, inspector | KEEP | |
| `EntityApprovalPolicy`, registry | KEEP | |
| `RelativeDateResolver`, `BusinessTimeZone` | KEEP | Used by generic builder |
| `CrmQueryIntentHelper` legacy remap | KEEP | Defensive if model invents old tool names |
| `StaticSeedCrmMetadata` / stub factory | KEEP | Fallback + unit-test metadata |
| `MockCrmMetadataService` / `MockGenericCrmRepository` / `MockCrmSeedData` | KEEP | Test doubles only (not chat→Groq path) |
| `CrmAiTools` legacy methods | REMOVE | Not exposed to Groq |
| `ICrmDataProvider` + Mock/Dynamics providers | REMOVE | Replaced by `IGenericCrmRepository` |
| `Crm/Query/SalesOrder*` (except RelativeDate/BusinessTz) | REMOVE | Entity-specific OData stack |
| `SalesOrderIntentHelper`, `SalesOrderFieldCatalogAiHint` | REMOVE | Façade / hint only |
| ChatService `HandleQuerySalesOrders*` / `get_sales_orders` | REMOVE | Dead (tool not exposed) |
| Diagnostics `/salesorders` + salesorder-schema via old provider | REFACTOR | Point at generic repo / entity metadata |
| `CrmEntityAllowlist` | KEEP | Auth constants + confidential demo; policy covers lead |
| `customers` in `ApprovedEntities` | REMOVE entry | Invalid entity; already an **alias** of `account` |
| `Crm:Provider=Mock` DI branch | KEEP optional | Offline/tests; live Dev uses Dynamics |

`customers`: not a Dynamics entity logical name; it is a business alias for `account` under `EntityAliases`. Having it in `ApprovedEntities` is incorrect and will fail EntityDefinitions discovery.
