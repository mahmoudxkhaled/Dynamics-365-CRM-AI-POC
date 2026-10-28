# Dynamics 365 Connectivity Status

Organization: `ECRMBSS`  
Base URL: `https://ecrmdev9.te.eg/ECRMBSS/api/data/v9.1/`  
Target entity: `salesorder`  
Auth mode (configured): Windows default credentials (`Crm:UseDefaultCredentials=true`)  
Optional explicit credentials: environment variables `CRM_USERNAME`, `CRM_PASSWORD`, `CRM_DOMAIN` (never hardcoded, never logged)

## Live test result (2026-10-28)

| Check | Result |
|-------|--------|
| DNS / network reachability | OK |
| SSL validation | Enabled (no bypass) |
| `GET WhoAmI()` | **200 OK** (Windows default credentials) |
| `GET EntityDefinitions(LogicalName='salesorder')` | **200 OK** — EntitySetName=`salesorders` |
| Attributes metadata | **200 OK** — 218 readable, 82 custom |
| `GET salesorders` (allowlisted fields, top 5) | **200 OK** |

Authenticated identity from WhoAmI:

- UserId: `b1335b28-556d-f111-afc3-005056b70b06`
- BusinessUnitId: `91a7cc62-3258-e711-8196-000d3a21a525`
- OrganizationId: `2521fc49-3b68-ea11-8265-005056a2aa00`

## Diagnostic endpoints

- `GET /api/crm/diagnostics/whoami`
- `GET /api/crm/diagnostics/salesorder-entity`
- `GET /api/crm/diagnostics/salesorder-schema` → writes `docs/CRM-SALESORDER-SCHEMA.md`
- `GET /api/crm/salesorders?limit=5` → backend-only read (does **not** send data to Groq)

## AI boundary

- `Crm:AllowSendLiveDataToAi` defaults to **false**
- `get_sales_orders` AI tool works against Mock data
- Against Dynamics365 provider, the AI tool refuses until `AllowSendLiveDataToAi=true` is explicitly approved

## Provider switch

Keep Groq AI unchanged. For live CRM data provider:

```json
"Crm": {
  "Provider": "Dynamics365",
  "EnableLiveIntegration": true,
  "BaseUrl": "https://ecrmdev9.te.eg/ECRMBSS/api/data/v9.1/",
  "UseDefaultCredentials": true,
  "AllowSendLiveDataToAi": false
}
```
