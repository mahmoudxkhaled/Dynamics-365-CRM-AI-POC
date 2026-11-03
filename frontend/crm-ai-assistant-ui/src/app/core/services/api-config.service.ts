import { Injectable } from '@angular/core';
import { environment } from '../../../environments/environment';

/**
 * Central API base URL resolution.
 * Dev: empty + proxy.conf.json → ASP.NET on :5138.
 * Prod / CRM embed: set environment.prod.ts → apiBaseUrl (absolute API origin).
 */
@Injectable({ providedIn: 'root' })
export class ApiConfigService {
  private readonly baseUrl = (environment.apiBaseUrl || '').replace(/\/+$/, '');

  get apiBaseUrl(): string {
    return this.baseUrl;
  }

  url(path: string): string {
    const normalized = path.startsWith('/') ? path : `/${path}`;
    return `${this.baseUrl}${normalized}`;
  }
}
