import { Injectable, inject } from '@angular/core';
import { MetaService } from '../db/meta.service';
import type { MasterDataSnapshot } from '../db/snapshot.repository';
import {
  ApiError,
  LarioHostService,
  MasterDataEndpointsService,
  OpenAPI,
} from '../../generated';

/**
 * Host-Verbindung für die Einrichtung (Schritt 8, Batch C, Design D3/D8).
 *
 * `fetchMasterData` führt den Login aus und lädt die vier
 * Stammdaten-Listen parallel (Promise.all). `fetchMasterData` ist
 * bewusst DB-schreibfrei: Das atomare Snapshot-Replace macht die
 * Setup-Component (über `SnapshotRepository`), die auth-Meta ebenfalls
 * (einschließlich des Sitzungstokens, P2 Batch-C-Review). Trennschärfe:
 * HostClient = Netzwerk + Fehlerklassifikation + auth-Meta-Zugriff;
 * Komponente = Orchestrierung (ein gemeinsamer Persistenz-Batch).
 *
 * Sitzungstoken (P2, Batch-C-Review): Der Host stellt pro Login einen
 * unabhängigen Token aus und widerruft beim `authLogout` ausschließlich
 * den mitgesendeten Bearer (AuthEndpoints/AuthTokenStore). `logout`
 * führt daher KEINEN Re-Login aus, sondern widerruft den tatsächlich
 * ausgestellten Token, der zusammen mit den Zugangsdaten in der
 * auth-Meta gemerkt ist (`auth.token`). `OpenAPI.TOKEN` (prozessweiter
 * Zustand des generierten Clients) hält den Token für die laufende
 * Session; `logout` setzt ihn am Ende zurück auf `undefined`.
 *
 * auth-Meta (Design D8, „lokal gemerkt"): host/username/password/
 * token/expires_at in der Meta-Tabelle der lokalen Geräte-DB. Passwort
 * und Token liegen gemäß Design-Entscheidung als Klartext in der
 * lokalen, nicht synchronisierten Geräte-Datenbank (bewusste
 * Entscheidung, Schritt-8-Scope — die DB verlässt das Gerät nicht).
 */

/** Meta-Keys der gemerkten Host-Zugangsdaten (Design D3/D8). */
export const AUTH_META_KEYS = {
  host: 'auth.host',
  username: 'auth.username',
  password: 'auth.password',
  /**
   * Letzt ausgestellter Sitzungstoken (P2, Batch-C-Review): wird beim
   * `authLogout` widerrufen — kein Re-Login-Ersatzwiderruf.
   */
  token: 'auth.token',
  expiresAt: 'auth.expires_at',
} as const;

/** Gemerkte Host-Zugangsdaten (aus der Meta-Tabelle). */
export interface StoredAuth {
  host: string;
  username: string;
  password: string;
  /** Letzt ausgestellter Sitzungstoken (für `logout`). */
  token: string;
  /** UTC-ISO; überfällig = Token abgelaufen (Re-Login erforderlich). */
  expiresAt: string;
}

/** Ergebnis von `fetchMasterData` (vor dem Snapshot-Replace). */
export interface MasterDataFetchResult {
  snapshot: MasterDataSnapshot;
  /** UTC-ISO-Zeitpunkt des Ladevorgangs. */
  fetchedAtUtc: string;
  /** Token-Gültigkeit in Minuten (LoginResponse). */
  expiresInMinutes: number;
  /**
   * Ausgestellter Sitzungstoken (P2, Batch-C-Review): gehört zusammen
   * mit den übrigen Verbindungsdaten in die auth-Meta und wird beim
   * Abmelden widerrufen.
   */
  token: string;
}

export type HostClientErrorCode =
  /** Netzwerkfehler / Host nicht erreichbar. */
  | 'http_unreachable'
  /** Host antwortet mit Fehlerstatus (401, 500, …). */
  | 'http_error'
  /** Antwort unlesbar / unerwartete Struktur. */
  | 'bad_response';

/** Host-Verbindungsfehler mit maschinell unterscheidbarem Code. */
export class HostClientError extends Error {
  /**
   * Ausgestellter Sitzungstoken, wenn der Fehler NACH einem
   * erfolgreichen Login auftrat (P2, Batch-C-Review): Die
   * Setup-Component widerruft genau diesen Token best-effort, bevor er
   * verworfen wird (`undefined` = Login nie erfolgreich).
   */
  readonly token?: string;

  constructor(
    readonly code: HostClientErrorCode,
    message: string,
    token?: string,
  ) {
    super(message);
    this.name = 'HostClientError';
    this.token = token;
  }
}

@Injectable({ providedIn: 'root' })
export class HostClient {
  // inject() statt Constructor-Parameter (ESLint prefer-inject).
  private readonly meta = inject(MetaService);

  /**
   * Login + paralleles Laden der vier Stammdaten-Listen (Design-
   * Entscheidung 26.10. „Fluss connect“). Setzt `OpenAPI.BASE` und
   * `OpenAPI.TOKEN`. Kein DB-Schrieb, keine auth-Meta.
   *
   * Fehlerklassifikation: `ApiError` mit Status > 0 → `http_error`
   * (Host antwortet); sonst → `http_unreachable` (Netzwerk).
   * Unerwartete Antwortstruktur → `bad_response`.
   */
  async fetchMasterData(
    host: string,
    username: string,
    password: string,
    now: Date,
  ): Promise<MasterDataFetchResult> {
    OpenAPI.BASE = host.trim();
    const login = await this.login(username, password);
    let accounts;
    let pools;
    let categories;
    let taxCategories;
    try {
      [accounts, pools, categories, taxCategories] = await Promise.all([
        MasterDataEndpointsService.listAccounts(),
        MasterDataEndpointsService.listPools(),
        MasterDataEndpointsService.listCategories(),
        MasterDataEndpointsService.listTaxCategories(),
      ]);
    } catch (error) {
      // Fehler NACH erfolgreichem Login: Bearer-Status nicht hängen
      // lassen (TOKEN-Reset; der best-effort-Widerruf des ausgestellten
      // Tokens läuft in der Komponente, P2 Batch-C-Review). Der Token
      // reist im Fehler mit, damit er widerrufen werden kann.
      OpenAPI.TOKEN = undefined;
      throw this.classify(error, 'lists', login.token);
    }
    if (
      !Array.isArray(accounts) ||
      !Array.isArray(pools) ||
      !Array.isArray(categories) ||
      !Array.isArray(taxCategories)
    ) {
      OpenAPI.TOKEN = undefined;
      throw new HostClientError(
        'bad_response',
        'Antwort des Hosts unlesbar: Stammdaten-Liste fehlt',
        login.token,
      );
    }
    return {
      snapshot: {
        accounts: new Map(accounts.map((dto) => [dto.id, dto])),
        pools: new Map(pools.map((dto) => [dto.id, dto])),
        categories: new Map(categories.map((dto) => [dto.id, dto])),
        taxCategories: new Map(taxCategories.map((dto) => [dto.id, dto])),
      },
      fetchedAtUtc: now.toISOString(),
      expiresInMinutes: Number(login.expiresInMinutes),
      token: login.token,
    };
  }

  /**
   * Best-effort-Abmeldung (Design D8; P2, Batch-C-Review): widerruft
   * den tatsächlich ausgestellten Sitzungstoken `token` per
   * `authLogout` — KEIN Re-Login. Der Host stellt pro Login einen
   * unabhängigen Token aus und widerruft nur den mitgesendeten Bearer;
   * ein Re-Login würde einen NEUEN Token widerrufen, während die echte
   * Session bis zum Ablauf gültig bliebe. `token` leer = Login nie
   * erfolgreich → No-op (nichts zu widerrufen). ALLE Fehler werden
   * geschluckt (Netzwerk weg, Token abgelaufen, Host nicht erreichbar):
   * `logout` ist ein Komfortpfad, kein kritischer Vorgang. Am Ende ist
   * `OpenAPI.TOKEN` undefined.
   */
  async logout(host: string, token: string): Promise<void> {
    if (token.length === 0) {
      OpenAPI.TOKEN = undefined;
      return;
    }
    try {
      OpenAPI.BASE = host.trim();
      OpenAPI.TOKEN = token;
      await LarioHostService.authLogout();
    } catch (error) {
      console.warn('HostClient.logout: Abmeldung fehlgeschlagen (ignoriert)', error);
    } finally {
      OpenAPI.TOKEN = undefined;
    }
  }

  /**
   * Gemerkte Zugangsdaten laden; `null`, wenn auch nur einer der fünf
   * Keys fehlt (unvollständige Merkung = nicht verbunden).
   */
  async loadAuthMeta(): Promise<StoredAuth | null> {
    const [host, username, password, token, expiresAt] = await Promise.all([
      this.meta.get(AUTH_META_KEYS.host),
      this.meta.get(AUTH_META_KEYS.username),
      this.meta.get(AUTH_META_KEYS.password),
      this.meta.get(AUTH_META_KEYS.token),
      this.meta.get(AUTH_META_KEYS.expiresAt),
    ]);
    if (
      host === null ||
      username === null ||
      password === null ||
      token === null ||
      expiresAt === null
    ) {
      return null;
    }
    return { host, username, password, token, expiresAt };
  }

  /**
   * Gemerkte Zugangsdaten entfernen (alle fünf Keys in EINER atomaren
   * Transaktion — P2, Batch-C-Review: keine teileweise Löschung).
   */
  async clearAuthMeta(): Promise<void> {
    await this.meta.deleteMany(Object.values(AUTH_META_KEYS));
  }

  /**
   * Login ausführen; `OpenAPI.TOKEN` wird auf den Token gesetzt.
   * Fehlerklassifikation: `ApiError` mit Status > 0 = Host antwortete
   * (http_error, z. B. 401 → „Anmeldung fehlgeschlagen (401)“); alles
   * andere (Netzwerkfehler, ApiError ohne Status) = http_unreachable.
   */
  private async login(
    username: string,
    password: string,
  ): Promise<{ token: string; expiresInMinutes: number | string }> {
    let login;
    try {
      login = await LarioHostService.authLogin({ username, password });
    } catch (error) {
      throw this.classify(error, 'login');
    }
    if (typeof login.token !== 'string' || login.token.length === 0) {
      throw new HostClientError(
        'bad_response',
        'Antwort des Hosts unlesbar: Login-Token fehlt',
      );
    }
    OpenAPI.TOKEN = login.token;
    return login;
  }

  /**
   * Fehler → HostClientError: ApiError mit Status > 0 = Host antwortete
   * (http_error); alles andere (Netzwerkfehler, ApiError ohne Status) =
   * http_unreachable. Kontext ('login' | 'lists') bestimmt die
   * deutsch-sprachige Meldung (UI-tauglich, die Setup-View zeigt
   * `error.message` an).
   */
  private classify(
    error: unknown,
    context: 'login' | 'lists',
    token?: string,
  ): HostClientError {
    if (error instanceof HostClientError) {
      return error;
    }
    if (error instanceof ApiError && error.status > 0) {
      const message =
        context === 'login'
          ? `Anmeldung fehlgeschlagen (${error.status})`
          : `Stammdaten konnten nicht geladen werden (${error.status})`;
      return new HostClientError('http_error', message, token);
    }
    return new HostClientError('http_unreachable', 'Host nicht erreichbar', token);
  }
}
