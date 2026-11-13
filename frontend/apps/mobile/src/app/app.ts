import { Component, inject, OnInit, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { SqliteService, PrototypeEntry } from './sqlite.service';
import { parseAmount } from './amount';
import { HostService, HostCheckResult } from './host.service';

@Component({
  selector: 'app-root',
  imports: [FormsModule],
  templateUrl: './app.html',
  styleUrl: './app.css',
})
export class App implements OnInit {
  // inject() statt Constructor-Parameter: Feld-Initialisierer (isNative,
  // Signals) dürfen Parameter-Properties nicht vor ihrer „Zuweisung“
  // referenzieren (TS2729), wohl aber per inject() deklarierte Felder.
  private readonly sqlite = inject(SqliteService);
  private readonly host = inject(HostService);

  /**
   * Asynchrone UI-Zustände sind Signals: Angular läuft in diesem Projekt
   * zoneless (Angular 22, kein zone.js), und nur Signal-Updates (oder
   * markForCheck) melden asynchrone Änderungen nach `await` an die
   * Change-Detection. Ohne Signals blieben Geräte-ID, Initialisierungsfehler
   * und die Ergebnisse von save()/testHost() unsichtbar, bis ein User-Event
   * (z. B. Tippen in ein Eingabefeld) einen CD-Pass auslöst (Gerätebefund
   * OnePlus 9).
   */
  readonly deviceId = signal<string | null>(null);
  readonly initError = signal<string | null>(null);
  readonly entries = signal<PrototypeEntry[]>([]);
  readonly saving = signal(false);
  readonly saveError = signal<string | null>(null);
  readonly hostUrl = signal('http://192.168.1.226:8080');
  readonly checking = signal(false);
  readonly hostCheck = signal<HostCheckResult | null>(null);

  /** Eingabefelder bleiben normale Properties: sie ändern sich nur bei
   * User-Events (ausgelöstes CD) oder synchron in save(), und der dortige
   * Signal-Update (entries) triggert den CD-Pass, der die geleerten
   * Felder mit rendert.
   */
  label = '';
  amountInput = '';

  readonly isNative = this.sqlite.isNative;

  async ngOnInit(): Promise<void> {
    if (!this.isNative) {
      return;
    }
    try {
      await this.sqlite.init();
      this.deviceId.set(await this.sqlite.getDeviceId());
      this.entries.set(await this.sqlite.listEntries());
      const storedUrl = await this.sqlite.getMeta('host_url');
      if (storedUrl) {
        this.hostUrl.set(storedUrl);
      }
    } catch (err) {
      this.initError.set(describeError(err));
    }
  }

  async save(): Promise<void> {
    const trimmedLabel = this.label.trim();
    if (!trimmedLabel) {
      this.saveError.set('Bitte eine Bezeichnung angeben.');
      return;
    }
    const amountCents = parseAmount(this.amountInput);
    if (amountCents === undefined) {
      this.saveError.set(
        'Ungültiger Betrag — Format z. B. „12,50“ (max. 13 Stellen vor dem Komma, 2 Nachkommastellen).',
      );
      return;
    }
    if (!this.isNative || !this.sqlite.ready) {
      this.saveError.set(
        'Der lokale Speicher ist nur in der installierten Android-App verfügbar (nicht in der Browser-Vorschau).',
      );
      return;
    }
    this.saveError.set(null);
    this.saving.set(true);
    try {
      const entry = await this.sqlite.saveEntry(trimmedLabel, amountCents);
      this.entries.update((current) => [entry, ...current]);
      this.label = '';
      this.amountInput = '';
    } catch (err) {
      this.saveError.set(`Speichern fehlgeschlagen: ${describeError(err)}`);
    } finally {
      this.saving.set(false);
    }
  }

  /** Centwert für die Anzeige formatieren (Template-Zugriff; die
   *  reine Modul-Funktion `parseAmount` wird in den Tests direkt
   *  geprüft). */
  formatCents(cents: number): string {
    const sign = cents < 0 ? '-' : '';
    const abs = Math.abs(cents);
    return `${sign}${Math.floor(abs / 100)},${String(abs % 100).padStart(2, '0')} €`;
  }

  async testHost(): Promise<void> {
    const url = this.hostUrl().trim();
    if (!url) {
      this.hostCheck.set({
        ok: false,
        message: 'Bitte eine Host-URL angeben (z. B. http://192.168.1.226:8080).',
      });
      return;
    }
    this.checking.set(true);
    this.hostCheck.set(null);
    const result = await this.host.checkHealth(url);
    this.hostCheck.set(result);
    // Merken, damit der nächste Appstart bei derselben Adresse beginnt.
    if (this.sqlite.ready) {
      try {
        await this.sqlite.setMeta('host_url', url);
      } catch {
        // Nicht kritisch für den Verbindungstest selbst.
      }
    }
    this.checking.set(false);
  }
}

function describeError(err: unknown): string {
  if (err instanceof Error) {
    return err.message;
  }
  return String(err);
}

// Batch D (P4): `parseAmount` lebt in `amount.ts` (1:1-Extraktion für das
// Erfassen-Formular); der Re-Export erhält `app.html`/`app.spec.ts`.
export { parseAmount } from './amount';
