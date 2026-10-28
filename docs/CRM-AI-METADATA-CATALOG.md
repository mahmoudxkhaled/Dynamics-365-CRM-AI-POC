# Approved CRM Metadata Catalog for AI Context

## Root cause (before)

Privacy-safe chat sent Groq only:

1. Fixed system prompt  
2. Latest user message  
3. `query_crm` tool schema  

Backend metadata discovery existed, but a controlled catalog was **not** attached to outbound AI messages. The model could guess field names; the backend rejected invalid ones after the fact.

## Architecture

```
User question
 → ChatService
 → ICrmAiMetadataCatalogService.BuildApprovedCatalogAsync()
      (all ApprovedEntities, compact attributes; cached via existing metadata services)
 → Outbound: System + Catalog block + User + query_crm schema
 → Groq tool call
 → CrmQueryPlanParser → Auth → Generic engine → Dynamics
 → Local formatter (records never returned to Groq)
```

Config gate: `Crm:AllowSendApprovedMetadataCatalogToAi`  
- Default **false** (`appsettings.json`)  
- Enabled in Development example / local Development settings for PoC  

Related limits: `AiMetadataCatalogMaxEntities`, `AiMetadataCatalogMaxAttributesPerEntity`, `AiMetadataCatalogMaxChars`.

## Security

- No CRM **records** in the catalog (schema/labels only).  
- Labels sanitized (instruction-injection redaction).  
- Unapproved entities/relationships excluded.  
- Backend validation remains authoritative.  
- Live CRM rows are never sent to the AI (privacy-safe by design).  
- Outbound auditor ignores delimited catalog blocks when scanning for record patterns.

## Example outbound catalog (synthetic)

```text
<<<APPROVED_CRM_METADATA_CATALOG>>>
REFERENCE DATA ONLY — ...
{"entities":[{"logicalName":"account","entitySetName":"accounts","displayName":"Account","attributes":[{"logicalName":"name","type":"String","filterable":true,"sortable":true,"selectable":true},{"logicalName":"createdon","type":"DateTime","filterable":true,"sortable":true,"selectable":true}]}]}
<<<END_APPROVED_CRM_METADATA_CATALOG>>>
```

## Latency note

Catalog build is registry/seed only (no Dynamics HTTP on the chat path). Live metadata is loaded by `CrmMetadataWarmupHostedService` at startup (`Crm:WarmApprovedMetadataOnStartup`) and overlays the registry in the background.

## Limitation

Catalog includes **all** approved entities each request (compact), not per-question entity narrowing. Metadata disclosure to the external model is intentional when the flag is enabled.
