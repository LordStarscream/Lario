import { ApplicationConfig, provideBrowserGlobalErrorListeners } from '@angular/core';
import { provideRouter, withInMemoryScrolling, type Routes } from '@angular/router';

/**
 * Routen der App (Batch E, P5): drei Views als Tabs — Erfassen
 * (primäre Startansicht), Eingaben, Einrichtung. `''` und unbekannte
 * Pfade leiten auf `/erfassen` weiter. `erfassen/:bookingId` ist der
 * Ändern-Modus derselben Erfassungs-Komponente (Routen-Parameter).
 * Exportiert für die Tests (RouterTestingHarness).
 */
export const routes: Routes = [
  { path: '', pathMatch: 'full', redirectTo: 'erfassen' },
  {
    path: 'erfassen',
    loadComponent: () =>
      import('./entry/entry.component').then((m) => m.EntryComponent),
  },
  {
    path: 'erfassen/:bookingId',
    loadComponent: () =>
      import('./entry/entry.component').then((m) => m.EntryComponent),
  },
  {
    path: 'eingaben',
    loadComponent: () =>
      import('./entries/entries.component').then((m) => m.EntriesComponent),
  },
  {
    path: 'eingaben/:bookingId',
    loadComponent: () =>
      import('./entries/entries-detail.component').then((m) => m.EntriesDetailComponent),
  },
  {
    path: 'einrichtung',
    loadComponent: () =>
      import('./setup/setup.component').then((m) => m.SetupComponent),
  },
  { path: '**', redirectTo: 'erfassen' },
];

export const appConfig: ApplicationConfig = {
  providers: [
    provideBrowserGlobalErrorListeners(),
    provideRouter(routes, withInMemoryScrolling({ anchorScrolling: 'disabled' })),
  ],
};
