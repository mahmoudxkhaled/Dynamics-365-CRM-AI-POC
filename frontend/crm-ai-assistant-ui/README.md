# WE CRM AI Assistant UI

NgModule-based Angular frontend for the CRM AI Assistant PoC. Talks to the existing ASP.NET Core API (`POST /api/chat`) without changing backend business logic.

## Stack

- Angular 19 (NgModules, `--standalone=false`)
- TypeScript + SCSS
- `HttpClient` + dev proxy to `http://localhost:5138`

## Install

```bash
cd frontend/crm-ai-assistant-ui
npm install
```

If your corporate TLS intercept breaks npm, temporarily set `NODE_TLS_REJECT_UNAUTHORIZED=0` for install only.

## Run (development)

1. Start the API (from repo root / Visual Studio): listens on **http://localhost:5138**
2. Start Angular:

```bash
npm start
```

Open **http://localhost:4200**. `/api/*` is proxied to the API (see `proxy.conf.json`).

## Build

```bash
npm run build
```

Output: `dist/crm-ai-assistant-ui/browser/`

### Deploy as Dynamics 365 CRM web resources

```bash
npm run build:crm
```

Follow **[CRM-DEPLOY.md](./CRM-DEPLOY.md)** (BossHelper-style: HTML + JS/CSS web resources, CRM iframe, `window.parent.Xrm`).

Set `environment.prod.ts` → `apiBaseUrl` to the absolute API origin before CRM upload.

## API configuration

| Mode | `apiBaseUrl` | Notes |
|------|--------------|--------|
| Dev | `''` | Relative `/api` via Angular proxy |
| Prod (same reverse proxy) | `''` | Relative `/api` |
| Prod / CRM different origin | `https://your-api-host` | No secrets in Angular config |

CORS: the API already allows any origin for the PoC. Prefer proxy / same-origin reverse proxy in production instead of widening CORS further.

## Architecture

```text
src/app/
  core/          models, ChatService, ApiConfigService, ThemeService, table utils
  shared/        SharedModule
  features/chat/ ChatWindow, ChatMessage, ChatInput, CrmResultsTable
  app.module.ts  NgModule bootstrap (not standalone)
```

## Reference UI

The original static UI remains at `src/CrmAiPoc.Api/wwwroot/index.html` until this app is verified in your environment. Safe to retire after acceptance.
