import { NgModule } from '@angular/core';
import { RouterModule, Routes } from '@angular/router';

const routes: Routes = [
  {
    path: '',
    loadChildren: () =>
      import('./features/chat/chat.module').then((m) => m.ChatModule)
  },
  { path: '**', redirectTo: '' }
];

/**
 * HashLocationStrategy: required for CRM HTML web resources / iframe hosting.
 * PathLocationStrategy breaks under /WebResources/... URLs.
 */
@NgModule({
  imports: [
    RouterModule.forRoot(routes, {
      useHash: true,
      // Keep scroll/anchor behavior simple inside CRM iframe
      anchorScrolling: 'disabled'
    })
  ],
  exports: [RouterModule]
})
export class AppRoutingModule {}
