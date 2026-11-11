import { NgModule } from '@angular/core';
import { RouterModule, Routes } from '@angular/router';
import { ChatWindowComponent } from './features/chat/components/chat-window/chat-window.component';

const routes: Routes = [
  { path: '', component: ChatWindowComponent },
  { path: '**', redirectTo: '' }
];

/**
 * HashLocationStrategy: required for CRM HTML web resources / iframe hosting.
 * PathLocationStrategy breaks under /WebResources/... URLs.
 *
 * Chat feature is eagerly loaded (no lazy chunks) so CRM web resource
 * filenames stay stable across builds (main.js / polyfills.js / styles.css).
 */
@NgModule({
  imports: [
    RouterModule.forRoot(routes, {
      useHash: true,
      anchorScrolling: 'disabled'
    })
  ],
  exports: [RouterModule]
})
export class AppRoutingModule {}
