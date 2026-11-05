import { Component, EventEmitter, Input, OnChanges, Output, SimpleChanges } from '@angular/core';
import { ChatUiMessage, ClarificationSuggestion } from '../../../../core/models/chat-response.model';
import {
  extractRows,
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
  @Input() suggestionsDisabled = false;
  @Output() readonly suggestionSelected = new EventEmitter<string>();

  mode: 'plain' | 'count' | 'table' | 'clarification' = 'plain';
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

  onSuggestionClick(suggestion: ClarificationSuggestion): void {
    const question = (suggestion.question || '').trim();
    if (!question || this.suggestionsDisabled) {
      return;
    }
    this.suggestionSelected.emit(question);
  }

  private rebuild(): void {
    const msg = this.message;
    if (!msg) {
      return;
    }

    if (msg.role === 'user' || msg.role === 'meta' || msg.error) {
      this.mode = 'plain';
      return;
    }

    if (
      msg.responseType === 'clarification' &&
      Array.isArray(msg.suggestions) &&
      msg.suggestions.length > 0
    ) {
      this.mode = 'clarification';
      return;
    }

    if (!msg.structuredData) {
      this.mode = 'plain';
      return;
    }

    const reply = msg.reply || '';
    const structured = msg.structuredData;
    const rows = extractRows(structured);
    this.summary = summaryFromReply(reply);
    const lines = this.summary.split('\n');
    this.summaryFirst = lines[0] || this.summary;
    this.summaryRest = lines.slice(1).join('\n');

    if (isCountResult(structured, rows) && structured.count != null) {
      this.mode = 'count';
      this.countLabel = 'Count';
      this.countCapped = structured.countMayBeCapped
        ? ' (at least — may be higher)'
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
