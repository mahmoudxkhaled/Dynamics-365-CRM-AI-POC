# CRM AI Assistant UI — Dynamics 365 Web Resource Deployment

**Pattern:** Same approach as BossHelper (Angular build → CRM Web Resources → HTML iframe).  
**App folder:** `frontend/crm-ai-assistant-ui`

---

## What must change for CRM hosting (summary)

| Topic | Local (`npm start`) | CRM (production build) |
| ----- | ------------------- | ---------------------- |
| Host | Angular dev server `:4200` | CRM HTML web resource in iframe |
| Base href | `/` (dev config) | `./` (relative — required under `/WebResources/...`) |
| Routing | Hash (`#/...`) | Hash (`#/...`) — PathLocation breaks in CRM |
| Xrm / CRM context | Skipped (`environment.production === false`) | `window.parent.Xrm.Page.context` |
| API URL | Empty + `proxy.conf.json` → `:5138` | Set `environment.prod.ts` → `apiBaseUrl` |
| Static files | Served by `ng serve` | Uploaded as separate CRM web resources |

---

## Quick commands

| Goal | Command |
| ---- | ------- |
| Local development | `npm start` |
| Production build only | `npm run build` |
| **Build + CRM-ready package** | `npm run build:crm` |

Defaults in `angular.json`:

- `ng serve` / `npm start` → **development**
- `ng build` / `npm run build` → **production**

---

## Prerequisites

1. Node.js + dependencies:

   ```bash
   cd frontend/crm-ai-assistant-ui
   npm install
   ```

2. **Local API** (for `npm start`): ASP.NET CRM AI API on `http://localhost:5138`.

3. **Production API URL**: edit before CRM deploy:

   ```typescript
   // src/environments/environment.prod.ts
   apiBaseUrl: 'https://YOUR-CRM-AI-API-HOST'  // no trailing slash
   ```

4. CRM publisher prefix that can create web resources (recommended virtual folder below).

---

## Recommended CRM web resource naming

Use a **virtual folder** under your publisher prefix so relative ES module chunk imports resolve:

| Role | CRM logical name | Type |
| ---- | ---------------- | ---- |
| Entry HTML | `te_/CrmAiAssistant/index.html` | Webpage (HTML) |
| Main bundle | `te_/CrmAiAssistant/main.js` | Script (JScript) |
| Polyfills | `te_/CrmAiAssistant/polyfills.js` | Script (JScript) |
| Styles | `te_/CrmAiAssistant/styles.css` | Style Sheet (CSS) |
| Lazy / shared chunks | `te_/CrmAiAssistant/<chunk-file>.js` | Script (JScript) |
| Other dist files | `te_/CrmAiAssistant/<same filename>` | Match MIME |

**Prefix:** `te_/CrmAiAssistant` (change both in `environment*.ts` and `CRM_WR_PREFIX` if your publisher differs).

**Entry URL in CRM:**

```text
https://<crm-host>/<org>/WebResources/te_/CrmAiAssistant/index.html
```

---

## Build for CRM

```bash
cd frontend/crm-ai-assistant-ui
npm run build:crm
```

| Output | Purpose |
| ------ | ------- |
| `dist/crm-ai-assistant-ui/browser/` | Raw Angular production build |
| `dist/crm-webresources/` | Copy + rewritten `index.html` for CRM |
| `dist/crm-webresources/CRM-UPLOAD-MAPPING.json` | Exact logical name ↔ file map |

`prepare-crm-webresources.mjs` rewrites `index.html` so JS/CSS point to:

```html
<script src="/WebResources/te_/CrmAiAssistant/main.js" type="module"></script>
<link rel="stylesheet" href="/WebResources/te_/CrmAiAssistant/styles.css" />
<base href="./" />
```

Override prefix:

```bash
# PowerShell
$env:CRM_WR_PREFIX="te_/CrmAiAssistant"; npm run build:crm
```

---

## Upload mapping (exact steps)

1. Run `npm run build:crm`.
2. Open `dist/crm-webresources/CRM-UPLOAD-MAPPING.json`.
3. In CRM: **Settings → Customizations → Customize the System → Web Resources → New**.
4. For **each file** in `dist/crm-webresources/` (except the mapping JSON):

   | Field | Value |
   | ----- | ----- |
   | Name / URL | Exact `crmLogicalName` from mapping (e.g. `te_/CrmAiAssistant/main.js`) |
   | Display Name | Same or friendly label |
   | Type | HTML / JScript / CSS as in mapping |
   | Upload File | The matching file from `dist/crm-webresources/` |

5. Upload **`index.html` last** (or update it after all JS/CSS exist).
6. **Save** each web resource.
7. **Publish All Customizations**.

### Typical first upload set

After a production build with `outputHashing: none`, expect at least:

- `index.html`
- `main.js`
- `polyfills.js`
- `styles.css`
- one or more `chunk-*.js` (lazy `ChatModule` / shared)

Upload **every** `.js` / `.css` / `.html` listed in the mapping. Missing a chunk breaks the app in the CRM iframe.

---

## How `index.html` references CRM web resources

CRM does **not** serve a real disk folder. The virtual name `te_/CrmAiAssistant/...` makes the browser treat files as siblings:

- HTML URL: `/WebResources/te_/CrmAiAssistant/index.html`
- Script URL: `/WebResources/te_/CrmAiAssistant/main.js`
- Chunk import `./chunk-….js` resolves next to `main.js` under the same virtual folder

Do **not** use `<base href="/">` — it breaks asset resolution under `/WebResources/`.

---

## Open the app inside CRM

### Option A — Web resource preview

1. Open the `te_/CrmAiAssistant/index.html` web resource.
2. Use **Preview** (works only if scripts are published and API URL is reachable).

### Option B — Form iframe (recommended for users)

1. Add an **IFRAME** on a CRM form (or a dashboard HTML web resource).
2. URL:

   ```text
   /WebResources/te_/CrmAiAssistant/index.html
   ```

   or the full org URL.
3. Uncheck “Restrict cross-frame scripting” if your CRM version requires it for parent `Xrm` access (On-Prem patterns vary — match BossHelper form iframe settings).
4. Save + Publish the form.

### Option C — Sitemap / HTML web resource link

Point a sitemap area or button to the HTML web resource URL above.

---

## Dev vs production behavior

| Concern | Development | Production (CRM) |
| ------- | ----------- | ---------------- |
| `environment.production` | `false` | `true` |
| `CrmContextService` | Does not call Xrm | Reads `window.parent.Xrm.Page.context` |
| API | Relative `/api` via proxy | `environment.apiBaseUrl` |
| Routing | Hash | Hash |

---

## Local development workflow

```bash
# 1. Start CRM AI API on http://localhost:5138

# 2. Start Angular
cd frontend/crm-ai-assistant-ui
npm start

# 3. Open http://localhost:4200
```

No CRM, no Xrm, no web resource upload.

---

## Final checklist — open app inside CRM

- [ ] Set `apiBaseUrl` in `environment.prod.ts`
- [ ] `npm run build:crm` succeeded
- [ ] All files from `CRM-UPLOAD-MAPPING.json` uploaded with matching logical names
- [ ] HTML web resource uses rewritten `dist/crm-webresources/index.html`
- [ ] Save + **Publish All Customizations**
- [ ] Form iframe / sitemap points to `te_/CrmAiAssistant/index.html`
- [ ] App loads in CRM iframe (check browser F12 for 404 on missing chunks)
- [ ] Console shows `[CRM AI Assistant] CRM context OK` (Xrm available)
- [ ] Chat call reaches API (`POST .../api/chat`) — fix CORS / `apiBaseUrl` if not

---

## Troubleshooting

| Symptom | Likely cause |
| ------- | ------------ |
| Blank page / 404 on `.js` | Web resource name mismatch or chunk not uploaded |
| Routes break / reload 404 | Not using hash routing (should already be `useHash: true`) |
| `Xrm` warning in console | Page opened outside CRM iframe, or iframe blocks parent access |
| API network error | Empty/wrong `apiBaseUrl`, or API CORS blocking CRM origin |
| Works locally, fails in CRM | Deployed `environment.ts` instead of production build — always use `npm run build:crm` |

---

## Files involved in this setup

| File | Role |
| ---- | ---- |
| `angular.json` | `baseHref: ./`, prod `outputHashing: none`, env file replacements |
| `src/index.html` | `<base href="./">` |
| `src/app/app-routing.module.ts` | `useHash: true` |
| `src/environments/environment.ts` | Local / non-CRM |
| `src/environments/environment.prod.ts` | CRM / API URL |
| `src/app/core/services/crm-context.service.ts` | Guarded Xrm access |
| `src/typings/xrm.d.ts` | Minimal Xrm types |
| `scripts/prepare-crm-webresources.mjs` | CRM `index.html` + mapping |
| `CRM-DEPLOY.md` | This guide |
