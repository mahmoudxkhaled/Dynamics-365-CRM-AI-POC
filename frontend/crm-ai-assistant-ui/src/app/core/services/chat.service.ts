import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { Injectable } from '@angular/core';
import { Observable, catchError, throwError } from 'rxjs';
import { ChatRequest } from '../models/chat-request.model';
import { ChatErrorBody, ChatResponse } from '../models/chat-response.model';
import { ApiConfigService } from './api-config.service';

@Injectable({ providedIn: 'root' })
export class ChatService {
  constructor(
    private readonly http: HttpClient,
    private readonly apiConfig: ApiConfigService
  ) { }

  sendMessage(message: string, conversationId: string | null): Observable<ChatResponse> {
    const body: ChatRequest = {
      message,
      conversationId: conversationId || undefined
    };

    return this.http
      .post<ChatResponse>(this.apiConfig.url('/api/chat'), body)
      .pipe(catchError((err: HttpErrorResponse) => throwError(() => this.toError(err))));
  }

  private toError(err: HttpErrorResponse): Error {
    const body = err.error as ChatErrorBody | string | null;
    if (typeof body === 'string' && body.trim()) {
      return new Error(body);
    }
    if (body && typeof body === 'object') {
      const msg = body.error || body.detail || body.title;
      if (msg) {
        return new Error(msg);
      }
    }
    if (err.status === 0) {
      return new Error('Network error: unable to reach the CRM AI API.');
    }
    return new Error(err.message || 'Request failed.');
  }
}
