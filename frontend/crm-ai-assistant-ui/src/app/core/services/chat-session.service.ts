import { Injectable } from '@angular/core';
import { Subject } from 'rxjs';

/**
 * Lightweight bridge so the page header can start a new chat
 * without coupling AppComponent to ChatWindowComponent.
 */
@Injectable({ providedIn: 'root' })
export class ChatSessionService {
  private readonly newChatSubject = new Subject<void>();
  readonly newChat$ = this.newChatSubject.asObservable();

  requestNewChat(): void {
    this.newChatSubject.next();
  }
}
