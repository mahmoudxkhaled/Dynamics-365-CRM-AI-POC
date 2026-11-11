import {
  AfterViewChecked,
  Component,
  ElementRef,
  OnDestroy,
  OnInit,
  ViewChild
} from '@angular/core';
import { Subscription } from 'rxjs';
import { ChatUiMessage } from '../../../../core/models/chat-response.model';
import { ChatService } from '../../../../core/services/chat.service';
import { ChatSessionService } from '../../../../core/services/chat-session.service';

@Component({
  selector: 'app-chat-window',
  templateUrl: './chat-window.component.html',
  styleUrl: './chat-window.component.scss',
  standalone: false
})
export class ChatWindowComponent implements OnInit, AfterViewChecked, OnDestroy {
  @ViewChild('chatScroll') chatScroll?: ElementRef<HTMLElement>;

  /** Minimum gap between /api/chat requests (rate-limit / concurrency guard). */
  private static readonly RequestCooldownMs = 60_000;

  /** English-only, read-only CRM suggestions for welcome mode. */
  readonly suggestedQuestions: string[] = [
    // 'Show me the latest 5 sales orders',
    // 'List the latest 5 accounts',
    // 'Show the newest contacts',
    // 'Count sales orders created this month',
    // 'Show the latest 5 leads'
  ];

  messages: ChatUiMessage[] = [];
  loading = false;
  conversationId: string | null = null;
  /** Seconds remaining before the next request is allowed; 0 means ready. */
  cooldownRemainingSeconds = 0;

  private shouldScroll = false;
  private nextId = 1;
  private cooldownUntilMs = 0;
  private cooldownTimerId: ReturnType<typeof setInterval> | null = null;
  private chatRequestSub: Subscription | null = null;
  private newChatSub: Subscription | null = null;

  constructor(
    private readonly chatService: ChatService,
    private readonly chatSession: ChatSessionService
  ) { }

  ngOnInit(): void {
    // Empty conversation starts in Welcome Mode (no seeded meta banner).
    this.newChatSub = this.chatSession.newChat$.subscribe(() => this.startNewChat());
  }

  ngOnDestroy(): void {
    this.clearCooldownTimer();
    this.stopProcessingIndicator();
    this.chatRequestSub?.unsubscribe();
    this.chatRequestSub = null;
    this.newChatSub?.unsubscribe();
    this.newChatSub = null;
  }

  /** Welcome until the user has started a real conversation turn. */
  get isWelcomeMode(): boolean {
    return !this.loading && !this.messages.some((m) => m.role === 'user' || m.role === 'assistant');
  }

  get inputBlocked(): boolean {
    return this.loading || this.cooldownRemainingSeconds > 0;
  }

  ngAfterViewChecked(): void {
    if (this.shouldScroll) {
      this.scrollToBottom();
      this.shouldScroll = false;
    }
  }

  onSuggestion(question: string): void {
    this.onSend(question);
  }

  /** Clicked AI clarification: submit the complete suggested question as a new independent /api/chat request. */
  onClarificationSuggestion(question: string): void {
    this.onSend(question);
  }

  startNewChat(): void {
    this.chatRequestSub?.unsubscribe();
    this.chatRequestSub = null;
    this.stopProcessingIndicator();
    this.clearCooldownTimer();
    this.cooldownRemainingSeconds = 0;
    this.cooldownUntilMs = 0;
    this.messages = [];
    this.conversationId = null;
    this.nextId = 1;
    this.shouldScroll = false;
  }

  onSend(message: string): void {
    if (this.inputBlocked) {
      return;
    }

    const trimmed = (message || '').trim();
    if (!trimmed) {
      return;
    }

    // Drop any in-flight request so we never show duplicate/stale indicators.
    this.chatRequestSub?.unsubscribe();
    this.chatRequestSub = null;

    // Immediate switch to Conversation Mode (before API returns).
    this.push({ role: 'user', text: trimmed });
    this.startProcessingIndicator();
    this.beginRequestCooldown();

    // Independent request: only the selected/complete question text is sent (no chat history to Groq).
    this.chatRequestSub = this.chatService.sendMessage(trimmed, this.conversationId).subscribe({
      next: (data) => {
        this.stopProcessingIndicator();
        this.conversationId = data.conversationId;
        const responseType = data.responseType || 'answer';
        this.push({
          role: 'assistant',
          reply: data.reply,
          text: data.reply,
          structuredData: data.structuredData ?? null,
          responseType,
          suggestions: data.suggestions ?? null
        });
        if (data.privacySafeToolExecution && responseType !== 'clarification') {
          this.push({
            role: 'meta',
            text: data.outboundAiContainedCrmRecords
              ? 'Warning: CRM records may have been included in the AI request.'
              : 'Your CRM data stayed private. Records were not sent to the AI model.'
          });
        }
        this.chatRequestSub = null;
      },
      error: (err: Error) => {
        this.stopProcessingIndicator();
        this.push({
          role: 'assistant',
          text: err.message || 'Request failed.',
          error: true
        });
        this.chatRequestSub = null;
      }
    });
  }

  trackById(_: number, msg: ChatUiMessage): string {
    return msg.id;
  }

  private startProcessingIndicator(): void {
    this.loading = true;
    this.shouldScroll = true;
  }

  private stopProcessingIndicator(): void {
    this.loading = false;
  }

  private push(partial: Omit<ChatUiMessage, 'id'>): void {
    this.messages = [
      ...this.messages,
      {
        ...partial,
        id: String(this.nextId++)
      }
    ];
    this.shouldScroll = true;
  }

  private beginRequestCooldown(): void {
    this.cooldownUntilMs = Date.now() + ChatWindowComponent.RequestCooldownMs;
    this.tickCooldown();
    this.clearCooldownTimer();
    this.cooldownTimerId = setInterval(() => this.tickCooldown(), 250);
  }

  private tickCooldown(): void {
    const remainingMs = Math.max(0, this.cooldownUntilMs - Date.now());
    this.cooldownRemainingSeconds = Math.ceil(remainingMs / 1000);
    if (remainingMs <= 0) {
      this.clearCooldownTimer();
      this.cooldownRemainingSeconds = 0;
    }
  }

  private clearCooldownTimer(): void {
    if (this.cooldownTimerId !== null) {
      clearInterval(this.cooldownTimerId);
      this.cooldownTimerId = null;
    }
  }

  private scrollToBottom(): void {
    const el = this.chatScroll?.nativeElement;
    if (el) {
      el.scrollTop = el.scrollHeight;
    }
  }
}
