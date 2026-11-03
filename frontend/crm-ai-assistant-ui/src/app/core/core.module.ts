import { NgModule, Optional, SkipSelf } from '@angular/core';
import { CommonModule } from '@angular/common';
import { HttpClientModule } from '@angular/common/http';

/**
 * Singleton app-wide services live in providedIn: 'root'.
 * CoreModule imports HttpClient once for the app shell.
 */
@NgModule({
  imports: [CommonModule, HttpClientModule],
  exports: [HttpClientModule]
})
export class CoreModule {
  constructor(@Optional() @SkipSelf() parent: CoreModule) {
    if (parent) {
      throw new Error('CoreModule is already loaded. Import it only in AppModule.');
    }
  }
}
