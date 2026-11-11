import { NgModule } from '@angular/core';
import { SharedModule } from '../../shared/shared.module';
import { ChatInputComponent } from './components/chat-input/chat-input.component';
import { ChatMessageComponent } from './components/chat-message/chat-message.component';
import { ChatWindowComponent } from './components/chat-window/chat-window.component';
import { CrmResultsTableComponent } from './components/crm-results-table/crm-results-table.component';

@NgModule({
  declarations: [
    ChatWindowComponent,
    ChatMessageComponent,
    ChatInputComponent,
    CrmResultsTableComponent
  ],
  imports: [SharedModule],
  exports: [ChatWindowComponent]
})
export class ChatModule {}
