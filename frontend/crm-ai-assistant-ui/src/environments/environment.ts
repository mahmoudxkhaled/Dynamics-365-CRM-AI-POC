/**
 * Local development (ng serve / npm start).
 * No CRM / Xrm — API via Angular proxy (proxy.conf.json → localhost:5138).
 */
export const environment = {
  production: false,
  /** Empty = same-origin relative /api (proxied in dev). */
  apiBaseUrl: '',
  /**
   * CRM web resource virtual folder (unused in local dev).
   * Production uploads use this path under /WebResources/.
   */
  crmWebResourcePrefix: 'te_/CrmAiAssistant'
};
