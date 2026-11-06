import { Component, EventEmitter, Input, Output } from '@angular/core';

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
  @Output() send = new EventEmitter<string>();

  message = '';

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

  onSubmit(): void {
    const text = this.message.trim();
    if (!text || this.loading || this.cooldownSeconds > 0) {
      return;
    }
    this.send.emit(text);
    this.message = '';
  }
}
