// Batch E (P5) — App-Shell: Tabs Erfassen / Eingaben / Einrichtung
// per Routing (RouterOutlet + routerLinkActive). Der Schritt-2-
// Prototyp (SQLite-Testdatensatz + Host-Check) ist ersetzt;
// `sqlite.service.ts` und `host.service.ts` sind gelöscht.
//
// Browser-Fallback (Design D8): ohne natives Plugin ist der lokale
// Speicher nicht verfügbar — die Shell zeigt eine klare Meldung
// statt der Tabs. Die DB-Initialisierung ist Aufgabe der
// Repositories/`LocalDatabase`; die App-Root ruft nichts auf.
import { Component, inject } from '@angular/core';
import { RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { LocalDatabase } from './db/local-database';

@Component({
  selector: 'app-root',
  templateUrl: './app.html',
  styleUrl: './app.css',
  imports: [RouterOutlet, RouterLink, RouterLinkActive],
})
export class App {
  // inject() statt Constructor-Parameter (ESLint prefer-inject).
  /** true, wenn die App im Android-Paket läuft (Capacitor-Plattform). */
  readonly isNative = inject(LocalDatabase).isNative;
}
