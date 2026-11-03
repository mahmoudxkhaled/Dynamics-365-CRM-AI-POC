import { Component, Input, OnChanges, SimpleChanges } from '@angular/core';
import { ChatUiMessage } from '../../../../core/models/chat-response.model';
import {
  extractRows,
  hasArabic,
  isCountResult,
  shouldShowTable,
  summaryFromReply
} from '../../../../core/services/crm-table.util';

@Component({
  selector: 'app-chat-message',
  templateUrl: './chat-message.component.html',
  styleUrl: './chat-message.component.scss',
  standalone: false
})
export class ChatMessageComponent implements OnChanges {
  @Input({ required: true }) message!: ChatUiMessage;

  mode: 'plain' | 'count' | 'table' = 'plain';
  arabic = false;
  summary = '';
  summaryFirst = '';
  summaryRest = '';
  countLabel = 'Count';
  countCapped = '';

  ngOnChanges(changes: SimpleChanges): void {
    if (changes['message']) {
      this.rebuild();
    }
  }

  private rebuild(): void {
    const msg = this.message;
    if (!msg) {
      return;
    }

    if (msg.role === 'user' || msg.role === 'meta' || msg.error || !msg.structuredData) {
      this.mode = 'plain';
      this.arabic = hasArabic(msg.text || msg.reply || '');
      return;
    }

    const reply = msg.reply || '';
    const structured = msg.structuredData;
    const rows = extractRows(structured);
    this.arabic = hasArabic(reply);
    this.summary = summaryFromReply(reply);
    const lines = this.summary.split('\n');
    this.summaryFirst = lines[0] || this.summary;
    this.summaryRest = lines.slice(1).join('\n');

    if (isCountResult(structured, rows) && structured.count != null) {
      this.mode = 'count';
      this.countLabel = this.arabic ? 'العدد' : 'Count';
      this.countCapped = structured.countMayBeCapped
        ? this.arabic
          ? ' (على الأقل — قد يكون العدد أكبر)'
          : ' (at least — may be higher)'
        : '';
      return;
    }

    if (shouldShowTable(structured, rows)) {
      this.mode = 'table';
      return;
    }

    this.mode = 'plain';
  }
}
