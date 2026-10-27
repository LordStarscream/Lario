import { Component, inject, OnInit, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { BookingsRepository } from '../db/bookings.repository';
import { MetaService } from '../db/meta.service';
import { SnapshotRepository } from '../db/snapshot.repository';
import {
  AUTH_META_KEYS,
  HostClient,
  HostClientError,
} from './host.client';
import {
  ApiError,
  LarioHostService,
  OpenAPI,
} from '../../generated';

/**
 * Einrichtung-View (Schritt 8, Batch C, Design D3/D8).
 *
 * Verbindet das Gerät mit dem Host: Login, paralleles Laden der vier
 * Stammdaten-Listen, atomares Snapshot-Replace, Merken der
 * Zugangsdaten. Abmelden löscht die auth-Meta und (best-effort) das
 * Bearer-Status auf dem Host — der lokale Snapshot bleibt verfügbar
 * (Offline-Erfassung).
 *
 * Orchestrierung liegt hier (nicht im HostClient): `fetchMasterData` →
 * `SnapshotRepository.replace` → auth-Meta. Die auth-Meta wird NUR
 * nach erfolgreichem Replace gesetzt (Design: Login ok, aber
 * List/Replace fehlerhaft → keine auth-Meta, Snapshot unverändert).
 *
 * Zoneless: asynchrone Zustände sind Signals (Muster `app.ts`); die
 * Formularfelder bleiben normale Properties (CD wird über die
 * Signal-Updates in den Handler-Methoden getriggert).
 */
@Component({
  selector: 'app-setup',
  imports: [FormsModule],
  templateUrl: './setup.html',
})
export class SetupComponent implements OnInit {
  // inject() statt Constructor-Parameter (ESLint prefer-inject,
  // Batch-A-Konvention).
  // Hinweis: `LocalDatabase.init` wird hier NICHT aufgerufen (Design-)
  // Entscheidung: die App-Root initialisiert die DB; `loadLocalState`
  // nutzt Repositories, die init() selbst aufrufen (idempotent).
  private readonly hostClient = inject(HostClient);
  private readonly meta = inject(MetaService);
  private readonly snapshotRepository = inject(SnapshotRepository);
  private readonly bookingsRepository = inject(BookingsRepository);

  /**
   * Formularfelder als Signals (Spec: „Signals: url (Default ''),
   * username, password“): `url`/`username` werden asynchron in
   * `loadLocalState` vorbefüllt — zoneless braucht für solche Updates
   * ein Signal (Muster `app.ts` `hostUrl`).
   */
  readonly url = signal('');
  readonly username = signal('');
  readonly password = signal('');
  readonly showPassword = signal(false);

  /** Doppeltipp-Sperre (Design D7): true, solange ein Ladevorgang läuft. */
  readonly busy = signal(false);
  /** Ergebnisstatus der letzten Aktion. */
  readonly status = signal<'idle' | 'success' | 'error'>('idle');
  readonly message = signal<string | null>(null);
  /** Stabile Geräte-ID (Design D1), nach `loadLocalState`. */
  readonly deviceId = signal<string | null>(null);
  /** UTC-ISO-Zeitstempel des letzten Snapshot-Ladevorgangs. */
  readonly snapshotFetchedAt = signal<string | null>(null);
  /** Health-Prüfung (Zweitstufe — blockiert nichts). */
  readonly health = signal<string | null>(null);
  /** true, wenn Zugangsdaten lokal gemerkt sind (Passwort wird NICHT angezeigt). */
  readonly passwordRemembered = signal(false);

  /**
   * Start: lokal gemerkte Zustände laden (Design: `LocalDatabase.init`
   * wird hier NICHT aufgerufen — die App-Root initialisiert die DB;
   * die Repositories rufen init() selbst und idempotent auf).
   */
  ngOnInit(): void {
    void this.loadLocalState();
  }

  /**
   * Lokal gemerkte Zustände laden: Geräte-ID, gemerkte Zugangsdaten
   * (Vorbefüllung), Snapshot-Zeitstempel. Kein Health-Check bei Start
   * (der ist expliziter Button-Klick).
   */
  async loadLocalState(): Promise<void> {
    try {
      this.deviceId.set(await this.bookingsRepository.getDeviceId());
    } catch {
      // Speicherfehler hier nicht fatal — die View bleibt nutzbar.
    }
    const stored = await this.hostClient.loadAuthMeta();
    if (stored !== null) {
      this.url.set(stored.host);
      this.username.set(stored.username);
      this.passwordRemembered.set(true);
    }
    this.snapshotFetchedAt.set(await this.snapshotRepository.fetchedAt());
    // Health-Prüfung (Spec ngOnInit-Fluss) — nur wenn eine Host-Adresse
    // gemerkt ist; ohne Adresse bleibt `health` unverändert (null),
    // statt einen irreführenden Fehler-Status zu setzen.
    if (this.url().length > 0) {
      void this.checkHealth();
    }
  }

  /**
   * Verbinden + Stammdaten laden (Design-Entscheidung 26.10.):
   * `fetchMasterData` → atomares `replace` → auth-Meta (nur nach
   * Erfolg). Fehler nach Login: best-effort Logout, keine auth-Meta.
   */
  async saveAndLoad(): Promise<void> {
    if (this.busy()) {
      return; // Doppeltipp-Sperre (Design D7).
    }
    const url = this.url().trim();
    const username = this.username().trim();
    const password = this.password();
    if (url.length === 0) {
      this.showError('Host-Adresse fehlt');
      return;
    }
    if (username.length === 0) {
      this.showError('Benutzername fehlt');
      return;
    }
    if (password.length === 0) {
      this.showError('Passwort fehlt');
      return;
    }
    this.status.set('idle');
    this.message.set(null);
    this.busy.set(true);
    try {
      const now = new Date();
      const result = await this.hostClient.fetchMasterData(
        url,
        username,
        password,
        now,
      );
      // Atomares Replace (Design D3): erst danach ist der Snapshot
      // konsistent — und nur dann wird die auth-Meta gemerkt.
      await this.snapshotRepository.replace(result.snapshot, result.fetchedAtUtc);
      const expiresAt = new Date(
        now.getTime() + result.expiresInMinutes * 60_000,
      ).toISOString();
      await this.setAuthMeta(url, username, password, expiresAt);
      const { accounts, pools, categories } = result.snapshot;
      this.snapshotFetchedAt.set(result.fetchedAtUtc);
      this.passwordRemembered.set(true);
      this.status.set('success');
      this.message.set(
        `Verbunden — Stammdaten geladen (${accounts.size} Konten, ` +
          `${pools.size} Pools, ${categories.size} Kategorien)`,
      );
    } catch (error) {
      // Host-Fehler (HostClientError): Session best-effort beenden
      // (swallowt alle Fehler), damit kein veraltetes Bearer-Token
      // zurückbleibt. DB-Fehler (replace) lösen kein Logout aus.
      // auth-Meta bleibt unverändert (nur nach erfolgreichem replace).
      if (error instanceof HostClientError) {
        await this.hostClient.logout(url, username, password);
      }
      this.showError(describeError(error));
    } finally {
      this.busy.set(false);
    }
  }

  /**
   * Health prüfen (Zweitstufe; benötigt kein Token). Der Setup-
   * Vorgang blockiert NICHT an der Health-Prüfung.
   */
  async checkHealth(): Promise<void> {
    const url = this.url().trim();
    if (url.length === 0) {
      this.health.set('error');
      this.message.set('Host-Adresse fehlt');
      return;
    }
    OpenAPI.BASE = url;
    try {
      const result = await LarioHostService.getHealth();
      this.health.set('ok');
      this.message.set(`Host erreichbar: ${result.status}`);
    } catch (error) {
      const code = error instanceof ApiError && error.status > 0 ? 'error' : 'unreachable';
      this.health.set(code);
      this.message.set(
        code === 'error'
          ? `Host antwortet mit Fehlerstatus ${String((error as ApiError).status)}`
          : 'Host nicht erreichbar',
      );
    }
  }

  /**
   * Abmelden (Design D8): best-effort Logout auf dem Host, auth-Meta
   * entfernen, Bearer-Status löschen. Der lokale Snapshot bleibt
   * verfügbar (Offline-Erfassung).
   */
  async disconnect(): Promise<void> {
    const stored = await this.hostClient.loadAuthMeta();
    if (stored === null) {
      this.showError('Nicht verbunden');
      return;
    }
    await this.hostClient.logout(stored.host, stored.username, stored.password);
    await this.hostClient.clearAuthMeta();
    OpenAPI.TOKEN = undefined;
    this.passwordRemembered.set(false);
    this.status.set('success');
    this.message.set(
      'Abgemeldet — Snapshot bleibt lokal verfügbar (Offline-Erfassung)',
    );
  }

  /** Snapshot-Zeitstempel für die Anzeige formatieren. */
  formatFetchedAt(iso: string | null): string | null {
    if (iso === null) {
      return null;
    }
    const date = new Date(iso);
    return Number.isNaN(date.getTime()) ? iso : date.toLocaleString();
  }

  /**
   * auth-Meta schreiben (alle vier Keys; Design D3/D8). Wird NACH
   * erfolgreichem Snapshot-Replace aufgerufen.
   */
  private async setAuthMeta(
    host: string,
    username: string,
    password: string,
    expiresAt: string,
  ): Promise<void> {
    await this.meta.set(AUTH_META_KEYS.host, host);
    await this.meta.set(AUTH_META_KEYS.username, username);
    await this.meta.set(AUTH_META_KEYS.password, password);
    await this.meta.set(AUTH_META_KEYS.expiresAt, expiresAt);
  }

  private showError(message: string): void {
    this.status.set('error');
    this.message.set(message);
  }
}

function describeError(error: unknown): string {
  return error instanceof Error ? error.message : String(error);
}
