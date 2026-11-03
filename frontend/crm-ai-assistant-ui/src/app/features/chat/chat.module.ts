import { NgModule } from '@angular/core';
import { RouterModule, Routes } from '@angular/router';
import { SharedModule } from '../../shared/shared.module';
import { ChatInputComponent } from './components/chat-input/chat-input.component';
import { ChatMessageComponent } from './components/chat-message/chat-message.component';
import { ChatWindowComponent } from './components/chat-window/chat-window.component';
import { CrmResultsTableComponent } from './components/crm-results-table/crm-results-table.component';

const routes: Routes = [
  { path: '', component: ChatWindowComponent }
];

@NgModule({
  declarations: [
    ChatWindowComponent,
    ChatMessageComponent,
    ChatInputComponent,
    CrmResultsTableComponent
  ],
  imports: [SharedModule, RouterModule.forChild(routes)],
  exports: [ChatWindowComponent]
})
export class ChatModule {}
