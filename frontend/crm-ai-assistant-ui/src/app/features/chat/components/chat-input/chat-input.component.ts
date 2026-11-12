import { Component, ElementRef, EventEmitter, Input, Output, ViewChild } from '@angular/core';

export type ChatInputVariant = 'welcome' | 'conversation';

@Component({
  selector: 'app-chat-input',
  templateUrl: './chat-input.component.html',
  styleUrl: './chat-input.component.scss',
  standalone: false
})
export class ChatInputComponent {
  /** True while waiting for the model / API response. */
  @Input() loading = false;
  /** When > 0, send button shows remaining cooldown seconds. */
  @Input() cooldownSeconds = 0;
  /** Visual layout: centered welcome composer vs bottom conversation bar. */
  @Input() variant: ChatInputVariant = 'conversation';
  @Input() placeholder = 'Ask anything about your CRM...';
  @Input() showHints = true;
  @Output() send = new EventEmitter<string>();

  @ViewChild('inputArea') private inputArea?: ElementRef<HTMLTextAreaElement>;

  message = '';

  /** Auto direction for Arabic / mixed input; chrome stays LTR. */
  get inputDir(): 'auto' | 'ltr' {
    return /[\u0600-\u06FF]/.test(this.message) ? 'auto' : 'ltr';
  }

  /** User may always type; only sending is blocked. */
  get canSend(): boolean {
    return !this.loading && this.cooldownSeconds <= 0 && !!this.message.trim();
  }

  get buttonLabel(): string {
    if (this.cooldownSeconds > 0) {
      return `${this.cooldownSeconds}s`;
    }
    return 'Send';
  }

  onInput(): void {
    this.resizeTextarea();
  }

  onEnterKey(event: Event): void {
    const keyEvent = event as KeyboardEvent;
    if (keyEvent.shiftKey) {
      return;
    }
    keyEvent.preventDefault();
    this.onSubmit();
  }

  onSubmit(): void {
    const text = this.message.trim();
    if (!text || this.loading || this.cooldownSeconds > 0) {
      return;
    }
    this.send.emit(text);
    this.message = '';
    queueMicrotask(() => this.resizeTextarea());
  }

  private resizeTextarea(): void {
    const el = this.inputArea?.nativeElement;
    if (!el) {
      return;
    }
    el.style.height = 'auto';
    el.style.height = `${el.scrollHeight}px`;
  }
}
