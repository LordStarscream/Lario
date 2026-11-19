// @vitest-environment jsdom
// Schritt 9: Kopplung, Herkunftsschutz und gemeinsame Netzwerksperre.
import type { MockInstance } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { OpenAPI } from '../../generated';
import {
  capacitorSqliteMockFactory,
  dbState,
  type CapacitorSqliteMock,
} from '../db/capacitor-sqlite-fake';
import { LocalDatabase } from '../db/local-database';
import { BookingsRepository } from '../db/bookings.repository';
import { SetupComponent } from './setup.component';
import { SyncService, SYNC_META_KEYS } from '../sync/sync.service';

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
  vi.restoreAllMocks();
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

/**
 * Authorization-Header eines fetch-Calls (der generierte Client übergibt
 * `Headers` im RequestInit) — für die Bearer-Prüfung der Logout-Calls.
 */
function authHeader(
  call: readonly (string | RequestInit | undefined)[],
): string | null {
  const init = call[1] as RequestInit | undefined;
  const headers = init?.headers;
  return headers instanceof Headers ? headers.get('Authorization') : null;
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


const HOST = 'https://lario.test';
const bootstrap = {
  dbGeneration: 1, protocolGeneration: 1, cursor: 0,
  accounts: [{ id: 'acc-1', name: 'Giro', type: 'bank', isInterestBearing: false, status: 'active', baseRevision: 1 }],
  pools: [{ id: 'pool-1', name: 'Flex', status: 'active', baseRevision: 1 }],
  categories: [{ id: 'cat-1', name: 'Essen', parentId: null, status: 'active', baseRevision: 1, taxPreset: null }],
  taxCategories: [],
};

describe('SetupComponent — Sync-Kopplung und Gerätebindung', () => {
  let component: SetupComponent;
  let mockFetch: MockInstance;
  let generation: number;
  let protocol: number;
  let pairs: number;

  beforeEach(async () => {
    dbState.reset();
    generation = 1;
    protocol = 1;
    pairs = 0;
    OpenAPI.BASE = HOST;
    OpenAPI.TOKEN = undefined;
    const lists = defaultRouter();
    mockFetch = vi.fn((url: string, init?: RequestInit) => {
      if (url.endsWith('/api/v1/sync/pair')) {
        pairs++;
        return Promise.resolve(jsonResponse(200, {
          id: `device-${pairs}`, name: 'Lario Android', token: `device-token-${pairs}`,
          createdAtUtc: '2026-11-19T00:00:00Z', revokedAtUtc: null, lastUsedAtUtc: null,
        }));
      }
      if (url.endsWith('/api/v1/sync/bootstrap')) {
        return Promise.resolve(jsonResponse(200, { ...bootstrap, dbGeneration: generation, protocolGeneration: protocol }));
      }
      if (url.includes('/api/v1/sync/changes')) {
        return Promise.resolve(jsonResponse(200, { cursor: 0, changes: [], complete: true, truncated: false }));
      }
      if (url.includes('/api/v1/sync/devices/') && init?.method === 'DELETE') {
        return Promise.resolve(jsonResponse(204, undefined));
      }
      if (url.endsWith('/api/v1/auth/logout')) return Promise.resolve(jsonResponse(204, undefined));
      return lists(url, init);
    });
    vi.stubGlobal('fetch', mockFetch);
    await TestBed.configureTestingModule({ imports: [SetupComponent] }).compileComponents();
    component = TestBed.createComponent(SetupComponent).componentInstance;
    await TestBed.inject(LocalDatabase).init();
    component.url.set(HOST);
    component.username.set('user');
    component.password.set('secret');
  });

  async function connect(): Promise<void> {
    await component.saveAndLoad();
    expect(component.status()).toBe('success');
  }

  async function localBooking(): Promise<void> {
    await TestBed.inject(BookingsRepository).save({
      date: '2026-11-19', totalAmountCents: 100, accountId: 'acc-1', note: null,
      splits: [{ id: 'split-1', amountCents: 100, poolId: 'pool-1', categoryId: 'cat-1', note: null, tax: null }],
    }, new Date('2026-11-19T00:00:00Z'));
  }

  it('koppelt mit Sitzungstoken und lädt Bootstrap mit Gerätetoken, persistiert beides zusammen', async () => {
    await connect();
    const calls = mockFetch.mock.calls;
    const pair = calls.find(call => String(call[0]).endsWith('/sync/pair'))!;
    const snapshot = calls.find(call => String(call[0]).endsWith('/sync/bootstrap'))!;
    expect(authHeader(pair)).toBe('Bearer tok-1');
    expect(authHeader(snapshot)).toBe('Bearer device-token-1');
    expect(dbState.meta.get(SYNC_META_KEYS.deviceToken)).toBe('device-token-1');
    expect(dbState.meta.get(SYNC_META_KEYS.bootstrapGeneration)).toBe('1');
    expect(dbState.meta.get('sync.host')).toBe(HOST);
    expect(OpenAPI.TOKEN).toBe('tok-1');
  });

  it('erneutes Koppeln am gleichen Host und gleicher Generation erhält eingefrorene Sendung', async () => {
    await connect();
    await localBooking();
    const bookings = TestBed.inject(BookingsRepository);
    const old = (await bookings.listSyncable())[0];
    await bookings.prepareSend(old.operationId, new Date());
    const frozen = (await bookings.listSyncable())[0];
    await connect();
    const retried = (await bookings.listSyncable())[0];
    expect(retried.operationId).toBe(frozen.operationId);
    expect(retried.payloadJson).toBe(frozen.payloadJson);
    expect(retried.payloadHash).toBe(frozen.payloadHash);
    expect((await bookings.get(retried.bookingId))?.status).toBe('sending');
    expect(dbState.meta.get(SYNC_META_KEYS.deviceToken)).toBe('device-token-2');
  });

  it('Hostwechsel mit lokalen Eingaben lässt bisherige Kopplung und Buchung unverändert', async () => {
    await connect();
    await localBooking();
    component.url.set('https://anderer-host.test');
    await component.saveAndLoad();
    expect(component.status()).toBe('error');
    expect(component.message()).toContain('Hostwechsel');
    expect(dbState.meta.get('auth.host')).toBe(HOST);
    expect(dbState.meta.get(SYNC_META_KEYS.deviceToken)).toBe('device-token-1');
    expect(dbState.bookings.size).toBe(1);
    expect(pairs).toBe(1);
    expect(OpenAPI.BASE).toBe(HOST);
  });

  /** Paarungs-Request anhalten; gibt die Resolver-Funktion zurück. */
  function gatePair(): (response: Response) => void {
    const previousRouter = mockFetch.getMockImplementation()!;
    let finishPair: ((response: Response) => void) | undefined;
    mockFetch.mockImplementation((url: string, init?: RequestInit) =>
      url.endsWith('/api/v1/sync/pair')
        ? new Promise<Response>(resolve => { finishPair = resolve; })
        : previousRouter(url, init));
    return (response: Response) => {
      if (finishPair === undefined) throw new Error('Paarung wurde nicht angefragt');
      finishPair(response);
    };
  }

  const pairResponse = (n: number): Response => jsonResponse(200, {
    id: `device-${n}`, name: 'Lario Android', token: `device-token-${n}`,
    createdAtUtc: '2026-11-19T00:00:00Z', revokedAtUtc: null, lastUsedAtUtc: null,
  });

  it('P1: Buchung nach leerer Herkunftsprüfung, vor B-Commit → Commit blockiert, Herkunft A bleibt', async () => {
    await connect();
    component.url.set('https://anderer-host.test');
    const releasePair = gatePair();
    const setup = component.saveAndLoad();
    // Bis die Paarung am Gate wartet — die (leere) Herkunftsprüfung
    // wurde bereits bestanden, Pairing/Bootstrap laufen.
    await vi.waitFor(() => {
      expect(mockFetch.mock.calls.some(call => String(call[0]).endsWith('/api/v1/sync/pair'))).toBe(true);
    });
    // Neue lokale Eingabe unter Host A während des B-Versuchs (Tabwechsel
    // in die Offline-Erfassung).
    await localBooking();
    expect(dbState.bookings.size).toBe(1);
    releasePair(pairResponse(2));
    await setup;
    expect(component.status()).toBe('error');
    expect(component.message()).toContain('Hostwechsel');
    expect(dbState.meta.get('sync.host')).toBe(HOST);
    expect(dbState.meta.get(SYNC_META_KEYS.deviceToken)).toBe('device-token-1');
    expect(dbState.bookings.size).toBe(1);
    expect(OpenAPI.BASE).toBe(HOST);
    // Fehlerpfad stellt die Laufzeit-Konfiguration auf die weiterhin
    // gültige gespeicherte Verbindung (Session-Token) zurück;
    // der Device-Token lebt in der sync-Meta (oben geprüft).
    expect(OpenAPI.TOKEN).toBe('tok-1');
    expect(mockFetch.mock.calls.some(call => String(call[0]).endsWith('/sync/devices/device-2') &&
      (call[1] as RequestInit)?.method === 'DELETE')).toBe(true);
  });

  it('P1: unter A laufende Anlage, deren Write auf den B-Commit trifft → B wartet und blockiert, Buchung bleibt an A', async () => {
    await connect();
    const bookings = TestBed.inject(BookingsRepository);
    const db = TestBed.inject(LocalDatabase);
    component.url.set('https://anderer-host.test');
    const releasePair = gatePair();
    // Finalen Schreib-Batch der Anlage anhalten: A-Validierung ist
    // bestanden, der Write ist noch offen.
    const originalExecuteSet = db.executeSet.bind(db);
    let writeGate: Promise<void> | null = null;
    let releaseWrite: (() => void) | undefined;
    db.executeSet = async (sets: Parameters<typeof originalExecuteSet>[0]) => {
      if (writeGate === null) {
        writeGate = new Promise<void>(resolve => { releaseWrite = resolve; });
      }
      await writeGate;
      return originalExecuteSet(sets);
    };
    const save = bookings.save({
      date: '2026-11-19', totalAmountCents: 100, accountId: 'acc-1', note: null,
      splits: [{ id: 'split-1', amountCents: 100, poolId: 'pool-1', categoryId: 'cat-1', note: null, tax: null }],
    }, new Date('2026-11-19T00:00:00Z'));
    const setup = component.saveAndLoad();
    // Anlage hält am Write-Gate (unter der Herkunftssperre), B-Versuch
    // hält am Pair-Gate; die Herkunftsprüfung von B sah noch keine
    // Eingabe.
    await vi.waitFor(() => expect(releaseWrite).toBeDefined());
    await vi.waitFor(() => {
      expect(mockFetch.mock.calls.some(call => String(call[0]).endsWith('/api/v1/sync/pair'))).toBe(true);
    });
    expect(dbState.bookings.size).toBe(0);
    // B läuft durch Pairing/Bootstrap bis zu seinem Commit — der wartet
    // hinter der noch laufenden Anlage auf die Sperre.
    releasePair(pairResponse(2));
    releaseWrite!();
    const saved = await save;
    expect(dbState.bookings.size).toBe(1);
    await setup;
    expect(component.status()).toBe('error');
    expect(component.message()).toContain('Hostwechsel');
    expect(saved.bookingId).toBeDefined();
    expect(dbState.meta.get('sync.host')).toBe(HOST);
    expect(dbState.meta.get(SYNC_META_KEYS.deviceToken)).toBe('device-token-1');
    expect(dbState.outbox.size).toBe(1);
    expect(OpenAPI.BASE).toBe(HOST);
    expect(OpenAPI.TOKEN).toBe('tok-1');
  });

  it.each(['database', 'protocol'] as const)('Abmelden erhält Generationbindung und blockiert neue %s-Generation', async (kind) => {
    await connect();
    await localBooking();
    await component.disconnect();
    expect(dbState.meta.get(SYNC_META_KEYS.deviceToken)).toBeUndefined();
    expect(dbState.meta.get('sync.host')).toBe(HOST);
    expect(dbState.meta.get(SYNC_META_KEYS.bootstrapGeneration)).toBe('1');
    expect(dbState.meta.get(SYNC_META_KEYS.protocolGeneration)).toBe('1');
    if (kind === 'database') generation = 2;
    else protocol = 2;
    await component.saveAndLoad();
    expect(component.status()).toBe('error');
    expect(component.message()).toContain('Geräteabstimmung');
    expect(dbState.meta.get(SYNC_META_KEYS.bootstrapGeneration)).toBe('1');
    expect(dbState.meta.get(SYNC_META_KEYS.protocolGeneration)).toBe('1');
    expect(dbState.meta.get(SYNC_META_KEYS.deviceToken)).toBeUndefined();
    expect(dbState.bookings.size).toBe(1);
    expect(mockFetch.mock.calls.some(call => String(call[0]).endsWith('/sync/devices/device-2') &&
      (call[1] as RequestInit)?.method === 'DELETE')).toBe(true);
  });

  it('Abmelden mit unbestätigter Sendung erhält Zugang zur sicheren Wiederholung', async () => {
    await connect();
    await localBooking();
    const bookings = TestBed.inject(BookingsRepository);
    await bookings.prepareSend((await bookings.listSyncable())[0].operationId, new Date());
    await component.disconnect();
    expect(component.status()).toBe('error');
    expect(component.message()).toContain('Unbestätigte');
    expect(dbState.meta.get(SYNC_META_KEYS.deviceToken)).toBe('device-token-1');
    expect(dbState.meta.get('auth.host')).toBe(HOST);
    expect((await bookings.get((await bookings.listSyncable())[0].bookingId))?.status).toBe('sending');
    expect(mockFetch.mock.calls.some(call => (call[1] as RequestInit)?.method === 'DELETE')).toBe(false);
  });

  it('Health, Einrichtung und Abmelden ändern während Sync keine globale Client-Verbindung', async () => {
    await connect();
    const service = TestBed.inject(SyncService);
    const previousRouter = mockFetch.getMockImplementation()!;
    let finishBootstrap!: (response: Response) => void;
    mockFetch.mockImplementation((url: string, init?: RequestInit) =>
      url.endsWith('/sync/bootstrap')
        ? new Promise<Response>(resolve => { finishBootstrap = resolve; })
        : previousRouter(url, init));
    const running = service.sync();
    // Bis der echte generierte Client den Bootstrap-Request gestartet hat.
    await new Promise(resolve => setTimeout(resolve, 0));
    expect(service.busy()).toBe(true);
    expect(finishBootstrap).toBeDefined();
    const requests = mockFetch.mock.calls.length;
    component.url.set('https://anderer-host.test');
    await component.checkHealth();
    await component.saveAndLoad();
    await component.disconnect();
    expect(mockFetch.mock.calls.length).toBe(requests);
    expect(OpenAPI.BASE).toBe(HOST);
    expect(OpenAPI.TOKEN).toBe('device-token-1');
    finishBootstrap(jsonResponse(200, bootstrap));
    await running;
    expect(service.busy()).toBe(false);
    expect(OpenAPI.BASE).toBe(HOST);
    expect(OpenAPI.TOKEN).toBe('tok-1');
  });
});
