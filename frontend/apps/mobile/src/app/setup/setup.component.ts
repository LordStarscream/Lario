import { Component, inject, OnInit, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { BookingsRepository } from '../db/bookings.repository';
import { LocalDatabase } from '../db/local-database';
import { SnapshotRepository } from '../db/snapshot.repository';
import { upsertMetaSet } from '../db/statements';
import {
  AUTH_META_KEYS,
  HostClient,
  HostClientError,
  type StoredAuth,
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
 * Zugangsdaten. Abmelden widerruft (best-effort) den gemerkten
 * Sitzungstoken und löscht die auth-Meta — der lokale Snapshot bleibt
 * verfügbar (Offline-Erfassung).
 *
 * Orchestrierung liegt hier (nicht im HostClient): `fetchMasterData` →
 * EINE lokale Transaktion mit Snapshot-Replace UND auth-Meta (inkl.
 * des ausgestellten Sitzungstokens; P2, Batch-C-Review). Die
 * auth-Meta wird NUR zusammen mit dem Replace gesetzt — ein Schreib-
 * fehler hinterlässt keinen Mischzustand (Design: Login ok, aber
 * List fehlerhaft → keine auth-Meta, Snapshot unverändert).
 *
 * Fehlerpfad connect (P2-Regression, Batch-C-Review): Jeder Fehler
 * VOR dem lokalen Commit (Host-Fehler nach Login, DB-Fehler im
 * Persist-Batch) widerruft best-effort den für diesen Versuch
 * ausgestellten Token (kein Re-Login) und setzt die Laufzeit-
 * Konfiguration (BASE/TOKEN) auf die weiterhin gültige gespeicherte
 * Verbindung zurück — ohne vorherige Verbindung wird der Bearer
 * entfernt. Sonst bliebe der Token des fehlgeschlagenen Versuchs im
 * prozessweiten Client hängen und auf dem Host aktiv bis Ablauf.
 *
 * Busy-Sperre (P2, Batch-C-Review): connect und disconnect respektieren
 * dieselbe `busy`-Sperre — die Sperre wird VOR dem ersten await gesetzt
 * und im `finally` freigegeben, damit ein paralleles connect+disconnect
 * neu gemerkte Zugangsdaten nicht löschen kann. Der Verbindungstest
 * ist bewusst kein gesperrter Vorgang (Zweitstufe, ändert keinen
 * Zustand).
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
  private readonly db = inject(LocalDatabase);
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
   * `fetchMasterData` → EINE lokale Transaktion mit Snapshot-Replace
   * UND auth-Meta (inkl. Sitzungstoken, P2 Batch-C-Review).
   *
   * Fehler VOR dem lokalen Commit (P2-Regression, Batch-C-Review):
   * best-effort-Widerruf des ausgestellten Tokens dieses Versuchs
   * (auch bei DB-Fehler im Persist-Batch — der Token wäre sonst
   * weder widerrufen noch in der auth-Meta abgelegt) +
   * Wiederherstellung der Laufzeit-Konfiguration (BASE/TOKEN) aus
   * der weiterhin gültigen gespeicherten auth-Meta; ohne vorherige
   * Verbindung wird der Bearer entfernt. Die auth-Meta bleibt
   * unverändert (nur ein erfolgreicher Batch ersetzt sie).
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
    // Sperre vor dem ersten await (P2, Batch-C-Review): kein Fenster,
    // in dem disconnect (oder ein zweiter connect) starten kann.
    this.busy.set(true);
    // VOR dem Versuch lesen (P2-Regression, Batch-C-Review): der
    // Fehlerpfad muss die weiterhin gültige gespeicherte Verbindung
    // kennen — auch wenn der Persist-Batch selbst fehlschlägt
    // (Rollback → auth-Meta unverändert).
    let previous: StoredAuth | null = null;
    let issuedToken: string | undefined;
    try {
      previous = await this.hostClient.loadAuthMeta();
      const now = new Date();
      const result = await this.hostClient.fetchMasterData(
        url,
        username,
        password,
        now,
      );
      issuedToken = result.token;
      // EINE atomare lokale Transaktion: komplettes Snapshot-Replace
      // (4×DELETE + alle INSERT + Zeitstempel) UND die gemerkten
      // Verbindungsdaten inkl. des ausgestellten Sitzungstokens
      // (P2, Batch-C-Review) — all-or-nothing, kein Mischzustand
      // (z. B. neuer Host + altes Passwort).
      const expiresAt = new Date(
        now.getTime() + result.expiresInMinutes * 60_000,
      ).toISOString();
      await this.db.executeSet([
        ...this.snapshotRepository.buildReplaceSets(result.snapshot, result.fetchedAtUtc),
        upsertMetaSet(AUTH_META_KEYS.host, url),
        upsertMetaSet(AUTH_META_KEYS.username, username),
        upsertMetaSet(AUTH_META_KEYS.password, password),
        upsertMetaSet(AUTH_META_KEYS.token, result.token),
        upsertMetaSet(AUTH_META_KEYS.expiresAt, expiresAt),
      ]);
      const { accounts, pools, categories } = result.snapshot;
      this.snapshotFetchedAt.set(result.fetchedAtUtc);
      this.passwordRemembered.set(true);
      this.status.set('success');
      this.message.set(
        `Verbunden — Stammdaten geladen (${accounts.size} Konten, ` +
          `${pools.size} Pools, ${categories.size} Kategorien)`,
      );
    } catch (error) {
      // Jeder Fehler VOR dem lokalen Commit (Host-Fehler nach Login,
      // DB-Fehler im Persist-Batch) löst dasselbe Aufräumen aus
      // (P2-Regression, Batch-C-Review):
      // 1. Der für diesen Versuch ausgestellte Token wird
      //    best-effort widerrufen (swallowt alle Fehler) — kein
      //    Re-Login, der Host widerruft nur den mitgesendeten
      //    Bearer. `issuedToken` (Fehler im Persist-Batch) bzw.
      //    `error.token` (HostClientError nach Login); leer = Login
      //    nie erfolgreich → No-op.
      const tokenToRevoke =
        issuedToken ??
        (error instanceof HostClientError ? error.token : undefined);
      if (tokenToRevoke !== undefined && tokenToRevoke.length > 0) {
        await this.hostClient.logout(url, tokenToRevoke);
      }
      // 2. Laufzeit-Konfiguration konsistent zur (durch den Rollback
      //    unveränderten) gespeicherten Verbindung — ohne vorherige
      //    Verbindung wird der Bearer entfernt. Sonst bliebe
      //    Host/Token des fehlgeschlagenen Versuchs im prozessweiten
      //    Client hängen.
      if (previous !== null) {
        OpenAPI.BASE = previous.host;
        OpenAPI.TOKEN = previous.token;
      } else {
        OpenAPI.TOKEN = undefined;
      }
      // auth-Meta bleibt unverändert (nur ein erfolgreicher Batch
      // ersetzt sie).
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
   * Abmelden (Design D8; P2, Batch-C-Review): best-effort-Widerruf des
   * gemerkten Sitzungstokens (kein Re-Login — der Host widerruft nur
   * den mitgesendeten Bearer) + atomare Entfernung der auth-Meta, unter
   * derselben Busy-Sperre wie connect: ein paralleles connect+disconnect
   * kann neu gemerkte Zugangsdaten nicht mehr löschen. Der lokale
   * Snapshot bleibt verfügbar (Offline-Erfassung).
   */
  async disconnect(): Promise<void> {
    if (this.busy()) {
      return; // paralleler Vorgang läuft (P2, Batch-C-Review)
    }
    // Sperre vor dem ersten await (P2, Batch-C-Review).
    this.busy.set(true);
    try {
      const stored = await this.hostClient.loadAuthMeta();
      if (stored === null) {
        this.showError('Nicht verbunden');
        return;
      }
      await this.hostClient.logout(stored.host, stored.token);
      await this.hostClient.clearAuthMeta();
      this.passwordRemembered.set(false);
      this.status.set('success');
      this.message.set(
        'Abgemeldet — Snapshot bleibt lokal verfügbar (Offline-Erfassung)',
      );
    } finally {
      this.busy.set(false);
    }
  }

  /** Snapshot-Zeitstempel für die Anzeige formatieren. */
  formatFetchedAt(iso: string | null): string | null {
    if (iso === null) {
      return null;
    }
    const date = new Date(iso);
    return Number.isNaN(date.getTime()) ? iso : date.toLocaleString();
  }

  private showError(message: string): void {
    this.status.set('error');
    this.message.set(message);
  }
}

function describeError(error: unknown): string {
  return error instanceof Error ? error.message : String(error);
}
