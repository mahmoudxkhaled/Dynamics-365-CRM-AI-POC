import { Injectable } from '@angular/core';
import { BehaviorSubject } from 'rxjs';

export type WeTheme = 'light' | 'dark';

const THEME_KEY = 'we-crm-theme';

@Injectable({ providedIn: 'root' })
export class ThemeService {
  private readonly themeSubject = new BehaviorSubject<WeTheme>(this.readInitial());

  readonly theme$ = this.themeSubject.asObservable();

  get theme(): WeTheme {
    return this.themeSubject.value;
  }

  init(): void {
    this.apply(this.themeSubject.value);
  }

  toggle(): void {
    this.apply(this.theme === 'dark' ? 'light' : 'dark');
  }

  apply(theme: WeTheme): void {
    const next: WeTheme = theme === 'dark' ? 'dark' : 'light';
    document.documentElement.setAttribute('data-theme', next);
    try {
      localStorage.setItem(THEME_KEY, next);
    } catch {
      /* ignore */
    }
    this.themeSubject.next(next);
  }

  private readInitial(): WeTheme {
    try {
      const saved = localStorage.getItem(THEME_KEY);
      if (saved === 'light' || saved === 'dark') {
        return saved;
      }
      return window.matchMedia('(prefers-color-scheme: dark)').matches ? 'dark' : 'light';
    } catch {
      return 'light';
    }
  }
}
