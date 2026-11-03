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

  readonly samples = [
    'Show me the latest 5 accounts',
    'هاتلي آخر 5 Sales Orders',
    'Delete all accounts',
    'Highest-value sales orders'
  ];

  message = '';
  copiedSample: string | null = null;
  private copiedTimer: ReturnType<typeof setTimeout> | null = null;

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

  async useSample(sample: string, event: Event): Promise<void> {
    event.preventDefault();
    event.stopPropagation();

    this.message = sample;

    let copied = false;
    try {
      if (navigator.clipboard?.writeText) {
        await navigator.clipboard.writeText(sample);
        copied = true;
      }
    } catch {
      copied = false;
    }

    if (!copied) {
      copied = this.copyFallback(sample);
    }

    if (this.copiedTimer) {
      clearTimeout(this.copiedTimer);
    }
    this.copiedSample = sample;
    this.copiedTimer = setTimeout(() => {
      this.copiedSample = null;
      this.copiedTimer = null;
    }, 900);
  }

  private copyFallback(text: string): boolean {
    const ta = document.createElement('textarea');
    ta.value = text;
    ta.style.cssText = 'position:fixed;left:-9999px;top:0;';
    document.body.appendChild(ta);
    ta.focus();
    ta.select();
    let ok = false;
    try {
      ok = document.execCommand('copy');
    } catch {
      ok = false;
    }
    document.body.removeChild(ta);
    return ok;
  }
}
