import {
  AfterViewChecked,
  Component,
  ElementRef,
  OnDestroy,
  OnInit,
  ViewChild
} from '@angular/core';
import { ChatUiMessage } from '../../../../core/models/chat-response.model';
import { ChatService } from '../../../../core/services/chat.service';

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

  /** Supported suggested questions (read-only CRM scenarios only, English). */
  readonly suggestedQuestions: string[] = [
    'Show the latest 5 accounts',
    'Show the latest 5 contacts',
    'Show the latest 5 leads',
    'Show the latest 5 sales orders',
    'How many sales orders are there by status?',
    'Show total sales order amount grouped by status'
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

  constructor(private readonly chatService: ChatService) {}

  ngOnInit(): void {
    this.push({
      role: 'meta',
      text: 'Conversation ready. Select a suggested question or type your own.'
    });
  }

  ngOnDestroy(): void {
    this.clearCooldownTimer();
  }

  get inputBlocked(): boolean {
    return this.loading || this.cooldownRemainingSeconds > 0;
  }

  get showSuggestions(): boolean {
    return !this.inputBlocked && this.messages.every((m) => m.role === 'meta');
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

  onSend(message: string): void {
    if (this.inputBlocked) {
      return;
    }

    const trimmed = (message || '').trim();
    if (!trimmed) {
      return;
    }

    this.push({ role: 'user', text: trimmed });
    this.loading = true;
    this.shouldScroll = true;
    this.beginRequestCooldown();

    // Independent request: only the selected/complete question text is sent (no chat history to Groq).
    this.chatService.sendMessage(trimmed, this.conversationId).subscribe({
      next: (data) => {
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
        this.loading = false;
      },
      error: (err: Error) => {
        this.push({
          role: 'assistant',
          text: err.message || 'Request failed.',
          error: true
        });
        this.loading = false;
      }
    });
  }

  trackById(_: number, msg: ChatUiMessage): string {
    return msg.id;
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
