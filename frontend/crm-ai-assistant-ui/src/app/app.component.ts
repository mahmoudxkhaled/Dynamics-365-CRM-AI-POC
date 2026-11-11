import { Component, OnInit } from '@angular/core';
import { ChatSessionService } from './core/services/chat-session.service';
import { CrmContextService } from './core/services/crm-context.service';

@Component({
  selector: 'app-root',
  templateUrl: './app.component.html',
  styleUrl: './app.component.scss',
  standalone: false
})
export class AppComponent implements OnInit {
  constructor(
    private readonly crmContext: CrmContextService,
    private readonly chatSession: ChatSessionService
  ) {}

  ngOnInit(): void {
    // Production-only: confirm CRM iframe context (no-op in local dev).
    if (this.crmContext.isCrmHosted) {
      console.info(
        '[CRM AI Assistant] CRM context OK',
        this.crmContext.context.orgUniqueName,
        this.crmContext.context.userName
      );
    }
  }

  onNewChat(): void {
    this.chatSession.requestNewChat();
  }
}
