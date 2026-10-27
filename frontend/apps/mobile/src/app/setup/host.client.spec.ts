// @vitest-environment jsdom
// Batch C — HostClient: Login, Stammdaten-Download, Logout, auth-Meta.
//
// Der generierte OpenAPI-Client nutzt `fetch` global — Mock per
// vi.stubGlobal (Muster: local-database.spec.ts, vi.mock-Blöcke für die
// Capacitor-Module, damit MetaService/LocalDatabase lauffähig sind).
import {
  EnvironmentInjector,
  createEnvironmentInjector,
} from '@angular/core';
import { TestBed } from '@angular/core/testing';
import type { MockInstance } from 'vitest';
import { OpenAPI } from '../../generated';
import type {
  AccountDto,
  CategoryDto,
  PoolDto,
  TaxCategoryDto,
} from '../../generated';
import {
  capacitorSqliteMockFactory,
  dbState,
  type CapacitorSqliteMock,
} from '../db/capacitor-sqlite-fake';
import { LocalDatabase } from '../db/local-database';
import { MetaService } from '../db/meta.service';
import {
  AUTH_META_KEYS,
  HostClient,
  HostClientError,
} from './host.client';

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

/** Response-Mock, der dem generierten Client-Contract genügt. */
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

const LOGIN_BODY = { token: 'tok-1', expiresInMinutes: 30 };

const ACCOUNTS: AccountDto[] = [
  {
    id: 'acc-1',
    name: 'Giro',
    type: 'bank',
    isInterestBearing: false,
    status: 'active',
    baseRevision: 3,
  },
  {
    id: 'acc-2',
    name: 'Bar',
    type: 'cash',
    isInterestBearing: false,
    status: 'active',
    baseRevision: 1,
  },
];

const POOLS: PoolDto[] = [
  { id: 'pool-1', name: 'Flex', status: 'active', baseRevision: 2 },
];

const CATEGORIES: CategoryDto[] = [
  {
    id: 'cat-1',
    name: 'Essen',
    parentId: null,
    status: 'active',
    baseRevision: 1,
    taxPreset: {
      taxRelevant: true,
      taxCategoryId: 'tax-1',
      taxCategoryName: 'USt',
      inheritToChildren: false,
      baseRevision: 4,
    },
  },
];

const TAX_CATEGORIES: TaxCategoryDto[] = [
  { id: 'tax-1', name: 'USt', parentId: null, status: 'active', baseRevision: 1 },
];

/**
 * Standard-Router: Login 200 + die vier Stammdaten-Listen 200 (Pfade aus
 * dem generierten Client). Tests überschreiben einzelne Routen.
 */
function defaultRouter(): (url: string, init?: RequestInit) => Promise<Response> {
  return (url: string) => {
    const u = String(url);
    if (u.includes('/api/v1/auth/login')) {
      return Promise.resolve(jsonResponse(200, LOGIN_BODY));
    }
    if (u.includes('/api/v1/accounts')) {
      return Promise.resolve(jsonResponse(200, ACCOUNTS));
    }
    if (u.includes('/api/v1/pools')) {
      return Promise.resolve(jsonResponse(200, POOLS));
    }
    if (u.includes('/api/v1/categories')) {
      return Promise.resolve(jsonResponse(200, CATEGORIES));
    }
    if (u.includes('/api/v1/tax-categories')) {
      return Promise.resolve(jsonResponse(200, TAX_CATEGORIES));
    }
    return Promise.resolve(jsonResponse(404, {}));
  };
}

describe('HostClient (Batch C)', () => {
  let mockFetch: MockInstance;
  let env: EnvironmentInjector;
  let hostClient: HostClient;
  let meta: MetaService;

  beforeEach(async () => {
    dbState.reset();
    OpenAPI.BASE = 'http://lario-test';
    OpenAPI.TOKEN = undefined;
    mockFetch = vi.fn(defaultRouter());
    vi.stubGlobal('fetch', mockFetch);
    env = createEnvironmentInjector(
      [LocalDatabase, MetaService, HostClient],
      TestBed.inject(EnvironmentInjector),
    );
    hostClient = env.get(HostClient);
    meta = env.get(MetaService);
    await env.get(LocalDatabase).init();
  });

  describe('fetchMasterData', () => {
    const NOW = new Date('2026-10-26T12:00:00.000Z');

    it('Erfolg: Login + 4 Listen, Maps korrekt, TOKEN/BASE gesetzt', async () => {
      const result = await hostClient.fetchMasterData(
        'http://lario-test',
        'user',
        'pw',
        NOW,
      );

      // Ein Login + vier Listen (keine weiteren Aufrufe).
      expect(mockFetch).toHaveBeenCalledTimes(5);
      const loginCalls = mockFetch.mock.calls.filter(([url]) =>
        String(url).includes('/api/v1/auth/login'),
      );
      expect(loginCalls).toHaveLength(1);
      expect(loginCalls[0][1]?.method).toBe('POST');
      expect(loginCalls[0][1]?.body).toBe(
        JSON.stringify({ username: 'user', password: 'pw' }),
      );

      expect(result.fetchedAtUtc).toBe(NOW.toISOString());
      expect(result.expiresInMinutes).toBe(30);
      expect([...result.snapshot.accounts.keys()]).toEqual(['acc-1', 'acc-2']);
      expect(result.snapshot.accounts.get('acc-1')).toEqual(ACCOUNTS[0]);
      expect(result.snapshot.pools.get('pool-1')).toEqual(POOLS[0]);
      expect(result.snapshot.categories.get('cat-1')).toEqual(CATEGORIES[0]);
      expect(result.snapshot.taxCategories.get('tax-1')).toEqual(TAX_CATEGORIES[0]);

      expect(OpenAPI.BASE).toBe('http://lario-test');
      expect(OpenAPI.TOKEN).toBe('tok-1');
    });

    it('Login 401: HostClientError http_error, TOKEN bleibt undefined, keine Listen', async () => {
      mockFetch.mockImplementation((url: string) =>
        String(url).includes('/api/v1/auth/login')
          ? Promise.resolve(jsonResponse(401, { message: 'no' }))
          : Promise.resolve(jsonResponse(200, [])),
      );

      let caught: unknown;
      try {
        await hostClient.fetchMasterData(
          'http://lario-test',
          'user',
          'wrong',
          NOW,
        );
      } catch (error) {
        caught = error;
      }
      expect(caught).toBeInstanceOf(HostClientError);
      expect((caught as HostClientError).code).toBe('http_error');
      expect(OpenAPI.TOKEN).toBeUndefined();
      // Nur der eine Login-Versuch, keine Listen-Aufrufe.
      expect(mockFetch).toHaveBeenCalledTimes(1);
      expect(mockFetch.mock.calls.filter(([url]) =>
        String(url).includes('/api/v1/accounts'),
      )).toHaveLength(0);
    });

    it('Login-Netzwerkfehler: HostClientError http_unreachable', async () => {
      mockFetch.mockRejectedValue(new TypeError('fetch failed'));
      await expect(
        hostClient.fetchMasterData('http://lario-test', 'user', 'pw', NOW),
      ).rejects.toMatchObject({ code: 'http_unreachable' });
      expect(OpenAPI.TOKEN).toBeUndefined();
    });

    it('Login ok, listAccounts 500: HostClientError http_error', async () => {
      const fallback = defaultRouter();
      mockFetch.mockImplementation((url: string) =>
        String(url).includes('/api/v1/accounts')
          ? Promise.resolve(jsonResponse(500, {}))
          : fallback(url),
      );
      await expect(
        hostClient.fetchMasterData('http://lario-test', 'user', 'pw', NOW),
      ).rejects.toMatchObject({ code: 'http_error' });
      // TOKEN nach fehlgeschlagener List-Phase zurückgesetzt (kein
      // veraltetes Bearer-Token in der OpenAPI-Config hängen lassen);
      // der volle best-effort-Logout läuft in der Komponente.
      expect(OpenAPI.TOKEN).toBeUndefined();
    });

    it('Liste liefert kein Array: HostClientError bad_response', async () => {
      const fallback = defaultRouter();
      mockFetch.mockImplementation((url: string) =>
        String(url).includes('/api/v1/pools')
          ? Promise.resolve(jsonResponse(200, { not: 'an array' }))
          : fallback(url),
      );
      await expect(
        hostClient.fetchMasterData('http://lario-test', 'user', 'pw', NOW),
      ).rejects.toMatchObject({ code: 'bad_response' });
    });

    it('schreibt NICHTS in die DB (Trennscharfe Regel)', async () => {
      await hostClient.fetchMasterData('http://lario-test', 'user', 'pw', NOW);
      expect(dbState.accounts.size).toBe(0);
      expect(dbState.pools.size).toBe(0);
      expect(dbState.categories.size).toBe(0);
      expect(dbState.taxCategories.size).toBe(0);
      expect(dbState.meta.get('snapshot.fetched_at')).toBeUndefined();
      for (const key of [
        AUTH_META_KEYS.host,
        AUTH_META_KEYS.username,
        AUTH_META_KEYS.password,
        AUTH_META_KEYS.expiresAt,
      ]) {
        expect(dbState.meta.get(key)).toBeUndefined();
      }
    });
  });

  describe('logout (best-effort)', () => {
    it('Login + Logout-Call; Logout-Fehler werden geschluckt; TOKEN am Ende undefined', async () => {
      mockFetch
        .mockResolvedValueOnce(jsonResponse(200, LOGIN_BODY)) // Re-Login
        .mockResolvedValueOnce(jsonResponse(500, {})); // authLogout → Fehler
      await expect(
        hostClient.logout('http://lario-test', 'user', 'pw'),
      ).resolves.toBeUndefined();
      expect(mockFetch).toHaveBeenCalledTimes(2);
      expect(
        mockFetch.mock.calls[1][0] && String(mockFetch.mock.calls[1][0]),
      ).toContain('/api/v1/auth/logout');
      expect(OpenAPI.TOKEN).toBeUndefined();
    });

    it('Re-Login schlägt fehl: kein throw, TOKEN bleibt undefined', async () => {
      mockFetch.mockRejectedValue(new TypeError('fetch failed'));
      await expect(
        hostClient.logout('http://lario-test', 'user', 'pw'),
      ).resolves.toBeUndefined();
      expect(mockFetch).toHaveBeenCalledTimes(1);
      expect(OpenAPI.TOKEN).toBeUndefined();
    });
  });

  describe('auth-Meta', () => {
    it('loadAuthMeta: null wenn Key fehlt, sonst Werte; clearAuthMeta löscht alle 4', async () => {
      expect(await hostClient.loadAuthMeta()).toBeNull();

      // Drei von vier Keys → weiterhin null.
      await meta.set(AUTH_META_KEYS.host, 'http://h');
      await meta.set(AUTH_META_KEYS.username, 'u');
      await meta.set(AUTH_META_KEYS.password, 'p');
      expect(await hostClient.loadAuthMeta()).toBeNull();

      await meta.set(AUTH_META_KEYS.expiresAt, '2026-11-26T00:00:00.000Z');
      expect(await hostClient.loadAuthMeta()).toEqual({
        host: 'http://h',
        username: 'u',
        password: 'p',
        expiresAt: '2026-11-26T00:00:00.000Z',
      });

      await hostClient.clearAuthMeta();
      expect(await hostClient.loadAuthMeta()).toBeNull();
      for (const key of [
        AUTH_META_KEYS.host,
        AUTH_META_KEYS.username,
        AUTH_META_KEYS.password,
        AUTH_META_KEYS.expiresAt,
      ]) {
        expect(dbState.meta.get(key)).toBeUndefined();
      }
    });
  });

  // HostClientError ist eine einfache Error-Klasse — Verhalten wird über
  // die Codes oben abgedeckt; hier nur die Klassenzugehörigkeit prüfen.
  it('HostClientError instanceof Error', () => {
    const error = new HostClientError('http_error', 'x');
    expect(error).toBeInstanceOf(Error);
    expect(error.name).toBe('HostClientError');
  });
});

