// @vitest-environment jsdom
// Batch C — SetupComponent: connect-Flow (fetchMasterData → replace →
// auth-Meta), Health, disconnect, Busy-Sperre.
import type { MockInstance } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { OpenAPI } from '../../generated';
import {
  capacitorSqliteMockFactory,
  dbState,
  type CapacitorSqliteMock,
} from '../db/capacitor-sqlite-fake';
import { LocalDatabase } from '../db/local-database';
import { MetaService } from '../db/meta.service';
import { SnapshotRepository, type MasterDataSnapshot } from '../db/snapshot.repository';
import { BookingsRepository } from '../db/bookings.repository';
import { HostClient } from './host.client';
import { SetupComponent } from './setup.component';

vi.mock('@capacitor/core', () => ({
  Capacitor: { isNativePlatform: () => true },
}));

// Die Factory laeuft bei jedem Methodenaufruf (keine unmittelbare
// Import-Aufloesung — vi.mock-Hoisting, Muster: bookings.repository.spec.ts).
vi.mock('@capacitor-community/sqlite', () => {
  const mock = () => capacitorSqliteMockFactory().CapacitorSQLite;
  return {
    CapacitorSQLite: {
      createConnection: (
        opts: Parameters<CapacitorSqliteMock['createConnection']>[0],
      ) => mock().createConnection(opts),
      open: (opts: Parameters<CapacitorSqliteMock['open']>[0]) =>
        mock().open(opts),
      executeSet: (opts: Parameters<CapacitorSqliteMock['executeSet']>[0]) =>
        mock().executeSet(opts),
      query: (opts: Parameters<CapacitorSqliteMock['query']>[0]) =>
        mock().query(opts),
      run: (opts: Parameters<CapacitorSqliteMock['run']>[0]) =>
        mock().run(opts),
      beginTransaction: (
        opts: Parameters<CapacitorSqliteMock['beginTransaction']>[0],
      ) => mock().beginTransaction(opts),
      commitTransaction: (
        opts: Parameters<CapacitorSqliteMock['commitTransaction']>[0],
      ) => mock().commitTransaction(opts),
      rollbackTransaction: (
        opts: Parameters<CapacitorSqliteMock['rollbackTransaction']>[0],
      ) => mock().rollbackTransaction(opts),
      isTransactionActive: (
        opts: Parameters<CapacitorSqliteMock['isTransactionActive']>[0],
      ) => mock().isTransactionActive(opts),
    },
  };
});

afterEach(() => {
  // Vitest 5: `unstubGlobals` ist kein RuntimeConfig (setConfig);
  // statt automatischem Unstubbing nach jedem Test explizit entfernen
  // (Muster: app.spec.ts).
  vi.unstubAllGlobals();
});

function jsonResponse(status: number, body: unknown): Response {
  return {
    ok: status >= 200 && status < 300,
    status,
    statusText: status < 300 ? 'OK' : 'Error',
    headers: {
      get: (name: string) =>
        name.toLowerCase() === 'content-type' ? 'application/json' : null,
    },
    json: async () => body,
    text: async () =>
      typeof body === 'string' ? body : JSON.stringify(body),
  } as unknown as Response;
}

/** Login 200 + vier Stammdaten-Listen 200 (je 1 Element). */
function defaultRouter(): (url: string, init?: RequestInit) => Promise<Response> {
  return (url: string) => {
    const u = String(url);
    if (u.includes('/api/v1/auth/login')) {
      return Promise.resolve(jsonResponse(200, { token: 'tok-1', expiresInMinutes: 30 }));
    }
    if (u.includes('/api/v1/accounts')) {
      return Promise.resolve(jsonResponse(200, [
        { id: 'acc-1', name: 'Giro', type: 'bank', isInterestBearing: false, status: 'active', baseRevision: 1 },
      ]));
    }
    if (u.includes('/api/v1/pools')) {
      return Promise.resolve(jsonResponse(200, [
        { id: 'pool-1', name: 'Flex', status: 'active', baseRevision: 1 },
      ]));
    }
    if (u.includes('/api/v1/categories')) {
      return Promise.resolve(jsonResponse(200, [
        { id: 'cat-1', name: 'Essen', parentId: null, status: 'active', baseRevision: 1, taxPreset: null },
      ]));
    }
    if (u.includes('/api/v1/tax-categories')) {
      return Promise.resolve(jsonResponse(200, [
        { id: 'tax-1', name: 'USt', parentId: null, status: 'active', baseRevision: 1 },
      ]));
    }
    return Promise.resolve(jsonResponse(404, {}));
  };
}

const EMPTY_SNAPSHOT: MasterDataSnapshot = {
  accounts: new Map(),
  pools: new Map(),
  categories: new Map(),
  taxCategories: new Map(),
};

describe('SetupComponent (Batch C)', () => {
  let mockFetch: MockInstance;
  let comp: SetupComponent;
  let db: LocalDatabase;
  let snapshotRepository: SnapshotRepository;

  beforeEach(async () => {
    dbState.reset();
    OpenAPI.BASE = 'http://lario-test';
    OpenAPI.TOKEN = undefined;
    mockFetch = vi.fn(defaultRouter());
    vi.stubGlobal('fetch', mockFetch);
    await TestBed.configureTestingModule({
      imports: [SetupComponent],
      providers: [
        LocalDatabase,
        MetaService,
        SnapshotRepository,
        BookingsRepository,
        HostClient,
      ],
    }).compileComponents();
    // Review-Fix: standalone-Komponenten werden per createComponent
    // erzeugt (Muster app.spec.ts) — TestBed.inject findet keinen
    // Provider (NG0201); die Tests pruefen Signal-State, kein DOM.
    comp = TestBed.createComponent(SetupComponent).componentInstance;
    db = TestBed.inject(LocalDatabase);
    snapshotRepository = TestBed.inject(SnapshotRepository);
    await db.init();
  });

  function setFields(): void {
    comp.url.set('http://192.168.1.226:8080');
    comp.username.set('user');
    comp.password.set('secret');
  }

  it('saveAndLoad mit leerer URL: Fehler, kein fetch-Call', async () => {
    comp.username.set('user');
    comp.password.set('secret');

    await comp.saveAndLoad();

    expect(comp.status()).toBe('error');
    expect(comp.message()).toBe('Host-Adresse fehlt');
    expect(mockFetch).not.toHaveBeenCalled();
  });

  it('saveAndLoad mit leerem Passwort: Fehler, kein fetch-Call', async () => {
    comp.url.set('http://192.168.1.226:8080');
    comp.username.set('user');

    await comp.saveAndLoad();

    expect(comp.status()).toBe('error');
    expect(comp.message()).toBe('Passwort fehlt');
    expect(mockFetch).not.toHaveBeenCalled();
  });

  it('saveAndLoad Erfolg: Snapshot in DB, auth-Meta (4 Keys), snapshotFetchedAt gesetzt', async () => {
    setFields();

    await comp.saveAndLoad();

    expect(comp.status()).toBe('success');
    expect(comp.message()).toContain('1 Konten');
    expect(comp.message()).toContain('1 Pools');
    expect(comp.message()).toContain('1 Kategorien');
    // Snapshot persistent (Design D3).
    expect(dbState.accounts.size).toBe(1);
    expect(dbState.accounts.get('acc-1')?.name).toBe('Giro');
    expect(dbState.pools.size).toBe(1);
    // auth-Meta: alle 5 Keys (inkl. Sitzungstoken, P2 Batch-C-Review).
    expect(dbState.meta.get('auth.host')).toBe('http://192.168.1.226:8080');
    expect(dbState.meta.get('auth.username')).toBe('user');
    expect(dbState.meta.get('auth.password')).toBe('secret');
    expect(dbState.meta.get('auth.token')).toBe('tok-1');
    expect(dbState.meta.get('auth.expires_at')).toBeTruthy();
    expect(comp.snapshotFetchedAt()).not.toBeNull();
    // Bearer-Status für Folgeaufrufe (Design D8).
    expect(OpenAPI.TOKEN).toBe('tok-1');
    expect(comp.passwordRemembered()).toBe(true);
    // Genau ein Batch: Login + 4 Listen.
    expect(mockFetch).toHaveBeenCalledTimes(5);
  });

  it('saveAndLoad: Login ok, listPools 500 → Fehler, keine auth-Meta, Widerruf des ausgestellten Tokens (kein Re-Login)', async () => {
    setFields();
    const fallback = defaultRouter();
    mockFetch.mockImplementation((url: string) =>
      String(url).includes('/api/v1/pools')
        ? Promise.resolve(jsonResponse(500, {}))
        : fallback(url),
    );

    await comp.saveAndLoad();

    expect(comp.status()).toBe('error');
    expect(comp.message()).toContain('500');
    // Keine auth-Meta (Login war erfolgreich, Listen fehlgeschlagen).
    expect(dbState.meta.get('auth.host')).toBeUndefined();
    expect(dbState.meta.get('auth.username')).toBeUndefined();
    expect(dbState.meta.get('auth.password')).toBeUndefined();
    expect(dbState.meta.get('auth.expires_at')).toBeUndefined();
    // Snapshot-Tabelle leer (replace lief nicht).
    expect(dbState.accounts.size).toBe(0);
    // Genau 6 Calls: Login + 4 Listen + Logout — der widerrufende Logout
    // nutzt den ausgestellten Token (kein Re-Login, P2 Batch-C-Review).
    expect(mockFetch).toHaveBeenCalledTimes(6);
    expect(mockFetch.mock.calls.filter(([url]) =>
      String(url).includes('/api/v1/auth/login'),
    )).toHaveLength(1);
    expect(mockFetch.mock.calls.filter(([url]) =>
      String(url).includes('/api/v1/auth/logout'),
    )).toHaveLength(1);
    // Bearer-Status nach dem best-effort Widerruf gelöscht.
    expect(OpenAPI.TOKEN).toBeUndefined();
  });

  it('saveAndLoad bei replace-Fehler: Fehler, keine auth-Meta, kein Logout', async () => {
    setFields();
    // Erster Statement des replace (DELETE) schlägt fehl.
    dbState.failOnStatementIndex = 0;

    await comp.saveAndLoad();

    expect(comp.status()).toBe('error');
    expect(dbState.meta.get('auth.host')).toBeUndefined();
    expect(dbState.accounts.size).toBe(0);
    // DB-Fehler ist kein HostClientError → kein Logout (5 Calls, nicht 7).
    expect(mockFetch).toHaveBeenCalledTimes(5);
    expect(OpenAPI.TOKEN).toBe('tok-1');
  });

  it('disconnect ohne auth-Meta: „Nicht verbunden“, Snapshot bleibt', async () => {
    await snapshotRepository.replace(
      {
        ...EMPTY_SNAPSHOT,
        accounts: new Map([
          ['acc-1', { id: 'acc-1', name: 'Giro', type: 'bank', isInterestBearing: false, status: 'active', baseRevision: 1 }],
        ]),
      },
      '2026-10-26T12:00:00.000Z',
    );

    await comp.disconnect();

    expect(comp.message()).toBe('Nicht verbunden');
    // Snapshot bleibt lokal verfügbar (Design D8).
    expect(dbState.accounts.size).toBe(1);
    // Kein Logout-Versuch (keine auth-Meta).
    expect(mockFetch).not.toHaveBeenCalled();
  });

  it('disconnect mit auth-Meta: Widerruf des gemerkten Tokens, auth-Meta weg, Snapshot bleibt', async () => {
    setFields();
    await comp.saveAndLoad();
    expect(comp.snapshotFetchedAt()).not.toBeNull();

    await comp.disconnect();

    expect(comp.status()).toBe('success');
    expect(comp.message()).toContain('Abgemeldet');
    expect(dbState.meta.get('auth.host')).toBeUndefined();
    expect(dbState.meta.get('auth.username')).toBeUndefined();
    expect(dbState.meta.get('auth.password')).toBeUndefined();
    expect(dbState.meta.get('auth.token')).toBeUndefined();
    expect(dbState.meta.get('auth.expires_at')).toBeUndefined();
    expect(OpenAPI.TOKEN).toBeUndefined();
    // Snapshot bleibt lokal (Offline-Erfassung).
    expect(dbState.accounts.size).toBe(1);
    expect(comp.snapshotFetchedAt()).not.toBeNull();
    expect(comp.passwordRemembered()).toBe(false);
    // 5 Calls (connect) + 1 Call (Widerruf des gemerkten Tokens, kein
    // Re-Login, P2 Batch-C-Review).
    expect(mockFetch).toHaveBeenCalledTimes(6);
    expect(mockFetch.mock.calls.filter(([url]) =>
      String(url).includes('/api/v1/auth/login'),
    )).toHaveLength(1);
    expect(mockFetch.mock.calls.filter(([url]) =>
      String(url).includes('/api/v1/auth/logout'),
    )).toHaveLength(1);
  });

  it('busy-Sperre: zweiter saveAndLoad-Call während Lauf wird ignoriert (1 fetch-Batch)', async () => {
    setFields();
    let releaseLogin!: (value: Response) => void;
    const pendingLogin = new Promise<Response>((resolve) => {
      releaseLogin = resolve;
    });
    const fallback = defaultRouter();
    mockFetch.mockImplementation((url: string) =>
      String(url).includes('/api/v1/auth/login')
        ? pendingLogin
        : fallback(url),
    );

    const first = comp.saveAndLoad();
    expect(comp.busy()).toBe(true);
    const second = comp.saveAndLoad(); // wird ignoriert
    expect(comp.busy()).toBe(true);

    releaseLogin(
      jsonResponse(200, { token: 'tok-1', expiresInMinutes: 30 }),
    );
    await first;
    await second;

    expect(comp.busy()).toBe(false);
    expect(comp.status()).toBe('success');
    // Genau ein fetch-Batch: 1 Login + 4 Listen (kein zweiter Login).
    expect(mockFetch).toHaveBeenCalledTimes(5);
    expect(mockFetch.mock.calls.filter(([url]) =>
      String(url).includes('/api/v1/auth/login'),
    )).toHaveLength(1);
  });

  it('P2 Batch-C-Review: connect-Fehler nach erfolgreichem Login widerruft den ausgestellten Token ohne Re-Login', async () => {
    setFields();
    const fallback = defaultRouter();
    mockFetch.mockImplementation((url: string) =>
      String(url).includes('/api/v1/accounts')
        ? Promise.resolve(jsonResponse(401, { message: 'expired' }))
        : fallback(url),
    );

    await comp.saveAndLoad();

    expect(comp.status()).toBe('error');
    // 6 Calls: Login + 4 Stammdaten-Listen (Promise.all feuert alle,
    // auch wenn accounts 401 liefert) + Logout mit ausgestelltem Token.
    expect(mockFetch).toHaveBeenCalledTimes(6);
    expect(mockFetch.mock.calls.filter(([url]) =>
      String(url).includes('/api/v1/auth/login'),
    )).toHaveLength(1);
    expect(mockFetch.mock.calls.filter(([url]) =>
      String(url).includes('/api/v1/auth/logout'),
    )).toHaveLength(1);
    // Keine auth-Meta, Snapshot unverändert.
    expect(dbState.meta.get('auth.host')).toBeUndefined();
    expect(dbState.accounts.size).toBe(0);
  });

  it('P2 Batch-C-Review: disconnect während laufendem connect wird ignoriert (Busy-Sperre)', async () => {
    setFields();
    let releaseLogin!: (value: Response) => void;
    const pendingLogin = new Promise<Response>((resolve) => {
      releaseLogin = resolve;
    });
    const fallback = defaultRouter();
    mockFetch.mockImplementation((url: string) =>
      String(url).includes('/api/v1/auth/login')
        ? pendingLogin
        : fallback(url),
    );

    const connect = comp.saveAndLoad();
    // Parallel gestarteter disconnect sieht die Busy-Sperre und ignoriert —
    // er kann die (noch folgenden) gemerkten Zugangsdaten nicht löschen.
    await comp.disconnect();
    expect(comp.status()).toBe('idle'); // disconnect hat nichts geschrieben

    releaseLogin(jsonResponse(200, { token: 'tok-1', expiresInMinutes: 30 }));
    await connect;

    expect(comp.status()).toBe('success');
    // Neu gemerkte auth-Meta ist vollständig (disconnect lief nicht).
    expect(dbState.meta.get('auth.host')).toBe('http://192.168.1.226:8080');
    expect(dbState.meta.get('auth.token')).toBe('tok-1');
    // 1 Login + 4 Listen (kein Logout-Call).
    expect(mockFetch).toHaveBeenCalledTimes(5);
    expect(mockFetch.mock.calls.filter(([url]) =>
      String(url).includes('/api/v1/auth/logout'),
    )).toHaveLength(0);
  });

  it('P2 Batch-C-Review: connect während laufendem disconnect wird ignoriert (Busy-Sperre)', async () => {
    setFields();
    await comp.saveAndLoad(); // auth-Meta vorhanden
    let releaseLogout!: () => void;
    const pendingLogout = new Promise<void>((resolve) => {
      releaseLogout = resolve;
    });
    mockFetch.mockImplementation((url: string) => {
      if (String(url).includes('/api/v1/auth/logout')) {
        return pendingLogout.then(
          () => jsonResponse(204, {}),
        );
      }
      return defaultRouter()(url);
    });

    const disconnect = comp.disconnect();
    // Parallel gestarter connect wird von der Busy-Sperre abgewehrt —
    // er kann die auth-Meta nicht parallel neu merken, während sie
    // entfernt wird.
    await comp.saveAndLoad();
    expect(comp.status()).toBe('success'); // erster connect-Status, unverändert

    releaseLogout();
    await disconnect;

    expect(comp.status()).toBe('success'); // disconnect-Status
    expect(comp.message()).toContain('Abgemeldet');
    // auth-Meta vollständig entfernt (connect lief nicht).
    expect(dbState.meta.get('auth.host')).toBeUndefined();
    expect(dbState.meta.get('auth.token')).toBeUndefined();
    // 5 (connect) + 1 (Logout) Calls — kein zweiter connect-Batch.
    expect(mockFetch).toHaveBeenCalledTimes(6);
    expect(mockFetch.mock.calls.filter(([url]) =>
      String(url).includes('/api/v1/auth/login'),
    )).toHaveLength(1);
  });
});
