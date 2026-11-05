import {
  AfterViewChecked,
  Component,
  ElementRef,
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
export class ChatWindowComponent implements OnInit, AfterViewChecked {
  @ViewChild('chatScroll') chatScroll?: ElementRef<HTMLElement>;

  /** Supported suggested questions (read-only CRM scenarios only). */
  readonly suggestedQuestions: string[] = [
    'Show the latest 5 Sales Orders.',
    'How many Sales Orders are there by status?',
    'Show monthly Sales Order counts for the last 6 months.',
    'Show total Sales Order amount grouped by status.',
    'Show the latest 5 Accounts.',
    'How many Sales Orders were created in the last 30 days?'
  ];

  messages: ChatUiMessage[] = [];
  loading = false;
  conversationId: string | null = null;

  private shouldScroll = false;
  private nextId = 1;

  constructor(private readonly chatService: ChatService) {}

  ngOnInit(): void {
    this.push({
      role: 'meta',
      text: 'Conversation ready. Ask about accounts, contacts, leads, or sales orders.'
    });
  }

  get showSuggestions(): boolean {
    return !this.loading && this.messages.every((m) => m.role === 'meta');
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

  onSend(message: string): void {
    if (this.loading) {
      return;
    }

    this.push({ role: 'user', text: message });
    this.loading = true;

    this.chatService.sendMessage(message, this.conversationId).subscribe({
      next: (data) => {
        this.conversationId = data.conversationId;
        this.push({
          role: 'assistant',
          reply: data.reply,
          text: data.reply,
          structuredData: data.structuredData ?? null
        });
        if (data.privacySafeToolExecution) {
          this.push({
            role: 'meta',
            text:
              'Privacy-safe: tool=' +
              (data.selectedTool || '?') +
              '; CRM→AI records=' +
              (data.outboundAiContainedCrmRecords ? 'YES' : 'NO') +
              (data.crmWhoAmIUserId ? '; WhoAmI=' + data.crmWhoAmIUserId : '')
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

  private scrollToBottom(): void {
    const el = this.chatScroll?.nativeElement;
    if (el) {
      el.scrollTop = el.scrollHeight;
    }
  }
}
