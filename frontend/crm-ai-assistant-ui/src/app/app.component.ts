import { Component, OnInit } from '@angular/core';
import { CrmContextService } from './core/services/crm-context.service';
import { ThemeService, WeTheme } from './core/services/theme.service';

@Component({
  selector: 'app-root',
  templateUrl: './app.component.html',
  styleUrl: './app.component.scss',
  standalone: false
})
export class AppComponent implements OnInit {
  theme: WeTheme = 'light';

  constructor(
    private readonly themeService: ThemeService,
    private readonly crmContext: CrmContextService
  ) {}

  ngOnInit(): void {
    this.themeService.init();
    this.theme = this.themeService.theme;
    this.themeService.theme$.subscribe((t) => (this.theme = t));

    // Production-only: confirm CRM iframe context (no-op in local dev).
    if (this.crmContext.isCrmHosted) {
      console.info(
        '[CRM AI Assistant] CRM context OK',
        this.crmContext.context.orgUniqueName,
        this.crmContext.context.userName
      );
    }
  }

  toggleTheme(): void {
    this.themeService.toggle();
  }

  get themeToggleLabel(): string {
    return this.theme === 'dark' ? 'Light' : 'Dark';
  }

  get themeAriaLabel(): string {
    return this.theme === 'dark' ? 'Switch to light mode' : 'Switch to dark mode';
  }
}
