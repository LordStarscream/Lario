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
 * Stammdaten-Listen parallel (Promise.all). Der Client ist bewusst
 * DB-schreibfrei: Das atomare Snapshot-Replace macht die
 * Setup-Component (über `SnapshotRepository`), die auth-Meta ebenfalls.
 * Trennschärfe: HostClient = Netzwerk + Fehlerklassifikation;
 * Komponente = Orchestrierung (Replace + Meta).
 *
 * Die Bearer-Zugangsdaten liegen in `OpenAPI.TOKEN` (generierter
 * Client) — ein prozessweiter Zustand, den `fetchMasterData` setzt und
 * `logout` wieder löscht (`undefined`).
 *
 * auth-Meta (Design D8, „lokal gemerkt"): host/username/password/
 * expires_at in der Meta-Tabelle der lokalen Geräte-DB. Das Passwort
 * liegt gemäß Design-Entscheidung als Klartext in der lokalen, nicht
 * synchronisierten Geräte-Datenbank (bewusste Entscheidung,
 * Schritt-8-Scope — die DB verlässt das Gerät nicht).
 */

/** Meta-Keys der gemerkten Host-Zugangsdaten (Design D3/D8). */
export const AUTH_META_KEYS = {
  host: 'auth.host',
  username: 'auth.username',
  password: 'auth.password',
  expiresAt: 'auth.expires_at',
} as const;

/** Gemerkte Host-Zugangsdaten (aus der Meta-Tabelle). */
export interface StoredAuth {
  host: string;
  username: string;
  password: string;
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
  constructor(
    readonly code: HostClientErrorCode,
    message: string,
  ) {
    super(message);
    this.name = 'HostClientError';
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
      // lassen (hier nur TOKEN-Reset; der volle best-effort-Logout
      // läuft in der Komponente).
      OpenAPI.TOKEN = undefined;
      throw this.classify(error, 'lists');
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
    };
  }

  /**
   * Best-effort-Abmeldung (Design D8): Re-Login (frischer Token) +
   * `authLogout` — ALLE Fehler werden geschluckt (Netzwerk weg, Token
   * abgelaufen, Host nicht erreichbar): `logout` ist ein Komfortpfad,
   * kein kritischer Vorgang. Am Ende ist `OpenAPI.TOKEN` undefined.
   */
  async logout(host: string, username: string, password: string): Promise<void> {
    try {
      OpenAPI.BASE = host.trim();
      const login = await LarioHostService.authLogin({ username, password });
      if (typeof login.token !== 'string' || login.token.length === 0) {
        return;
      }
      OpenAPI.TOKEN = login.token;
      await LarioHostService.authLogout();
    } catch (error) {
      console.warn('HostClient.logout: Abmeldung fehlgeschlagen (ignoriert)', error);
    } finally {
      OpenAPI.TOKEN = undefined;
    }
  }

  /**
   * Gemerkte Zugangsdaten laden; `null`, wenn auch nur einer der vier
   * Keys fehlt (unvollständige Merkung = nicht verbunden).
   */
  async loadAuthMeta(): Promise<StoredAuth | null> {
    const [host, username, password, expiresAt] = await Promise.all([
      this.meta.get(AUTH_META_KEYS.host),
      this.meta.get(AUTH_META_KEYS.username),
      this.meta.get(AUTH_META_KEYS.password),
      this.meta.get(AUTH_META_KEYS.expiresAt),
    ]);
    if (host === null || username === null || password === null || expiresAt === null) {
      return null;
    }
    return { host, username, password, expiresAt };
  }

  /** Gemerkte Zugangsdaten entfernen (alle vier Keys). */
  async clearAuthMeta(): Promise<void> {
    await this.meta.delete(AUTH_META_KEYS.host);
    await this.meta.delete(AUTH_META_KEYS.username);
    await this.meta.delete(AUTH_META_KEYS.password);
    await this.meta.delete(AUTH_META_KEYS.expiresAt);
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
  private classify(error: unknown, context: 'login' | 'lists'): HostClientError {
    if (error instanceof HostClientError) {
      return error;
    }
    if (error instanceof ApiError && error.status > 0) {
      const message =
        context === 'login'
          ? `Anmeldung fehlgeschlagen (${error.status})`
          : `Stammdaten konnten nicht geladen werden (${error.status})`;
      return new HostClientError('http_error', message);
    }
    return new HostClientError('http_unreachable', 'Host nicht erreichbar');
  }
}
