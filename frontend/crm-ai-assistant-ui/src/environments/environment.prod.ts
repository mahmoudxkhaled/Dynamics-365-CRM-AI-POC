/**
 * Production / CRM web resource hosting (ng build).
 * Set apiBaseUrl to the absolute CRM AI API origin before deploying.
 */
export const environment = {
  production: true,
  /**
   * Absolute API origin when the UI runs inside CRM (different host from the API).
   * Example: 'https://crm-ai-api.te.eg' or 'http://localhost:5138' for CRM+local API tests.
   * Leave '' only if a reverse proxy serves /api on the same origin as the iframe page.
   */
  apiBaseUrl: 'http://localhost:5138',
  /** Publisher virtual folder for CRM web resources (must match CRM-DEPLOY.md). */
  crmWebResourcePrefix: 'te_/CrmAiAssistant'
};
