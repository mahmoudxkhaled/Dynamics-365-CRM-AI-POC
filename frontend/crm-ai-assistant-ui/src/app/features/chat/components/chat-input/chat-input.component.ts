import { Component, EventEmitter, Input, Output } from '@angular/core';

@Component({
  selector: 'app-chat-input',
  templateUrl: './chat-input.component.html',
  styleUrl: './chat-input.component.scss',
  standalone: false
})
export class ChatInputComponent {
  @Input() disabled = false;
  /** When > 0, shows a wait message after the previous request. */
  @Input() cooldownSeconds = 0;
  @Output() send = new EventEmitter<string>();

  message = '';

  onSubmit(): void {
    const text = this.message.trim();
    if (!text || this.disabled) {
      return;
    }
    this.send.emit(text);
    this.message = '';
  }
}
