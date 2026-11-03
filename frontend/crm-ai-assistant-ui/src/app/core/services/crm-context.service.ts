import { Injectable } from '@angular/core';
import { environment } from '../../../environments/environment';

export interface CrmContextInfo {
  available: boolean;
  clientUrl: string | null;
  userId: string | null;
  userName: string | null;
  orgUniqueName: string | null;
}

/**
 * Reads Dynamics CRM context only in production (CRM iframe).
 * Local/dev never touches Xrm — same pattern as BossHelper.
 */
@Injectable({ providedIn: 'root' })
export class CrmContextService {
  private readonly info: CrmContextInfo;

  constructor() {
    this.info = this.resolve();
  }

  get context(): CrmContextInfo {
    return this.info;
  }

  get isCrmHosted(): boolean {
    return environment.production && this.info.available;
  }

  private resolve(): CrmContextInfo {
    const empty: CrmContextInfo = {
      available: false,
      clientUrl: null,
      userId: null,
      userName: null,
      orgUniqueName: null
    };

    if (!environment.production) {
      return empty;
    }

    try {
      const xrm = window.parent?.Xrm ?? window.Xrm;
      const ctx = xrm?.Page?.context;
      if (!ctx) {
        console.warn(
          '[CrmContextService] Production build but Xrm context was not found. Open the HTML web resource inside CRM.'
        );
        return empty;
      }

      return {
        available: true,
        clientUrl: ctx.getClientUrl(),
        userId: ctx.getUserId(),
        userName: ctx.getUserName(),
        orgUniqueName: ctx.getOrgUniqueName()
      };
    } catch (err) {
      console.warn('[CrmContextService] Failed to read CRM context.', err);
      return empty;
    }
  }
}
