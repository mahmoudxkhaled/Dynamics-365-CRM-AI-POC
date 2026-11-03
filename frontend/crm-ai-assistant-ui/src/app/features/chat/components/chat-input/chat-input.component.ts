import { Component, EventEmitter, Input, Output } from '@angular/core';
import { hasArabic } from '../../../../core/services/crm-table.util';

@Component({
  selector: 'app-chat-input',
  templateUrl: './chat-input.component.html',
  styleUrl: './chat-input.component.scss',
  standalone: false
})
export class ChatInputComponent {
  @Input() disabled = false;
  @Output() send = new EventEmitter<string>();

  message = '';

  get inputRtl(): boolean {
    return hasArabic(this.message);
  }

  onSubmit(): void {
    const text = this.message.trim();
    if (!text || this.disabled) {
      return;
    }
    this.send.emit(text);
    this.message = '';
  }
}
