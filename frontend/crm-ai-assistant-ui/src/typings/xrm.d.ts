/**
 * Minimal Dynamics 365 Xrm typings for CRM iframe hosting.
 * Matches BossHelper usage: window.parent.Xrm.Page.context
 */
interface XrmPageContext {
  getClientUrl(): string;
  getUserId(): string;
  getUserName(): string;
  getOrgUniqueName(): string;
}

interface XrmPage {
  context: XrmPageContext;
}

interface XrmStatic {
  Page: XrmPage;
}

interface Window {
  Xrm?: XrmStatic;
}
