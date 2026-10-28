// @vitest-environment jsdom
// Batch C — SnapshotRepository: atomares replace (Design D3), loadSnapshot,
// referenceCatalog, fetchedAt.
import {
  EnvironmentInjector,
  createEnvironmentInjector,
} from '@angular/core';
import { TestBed } from '@angular/core/testing';
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
} from './capacitor-sqlite-fake';
import { LocalDatabase } from './local-database';
import { MetaService } from './meta.service';
import { SnapshotRepository, type MasterDataSnapshot } from './snapshot.repository';

// Die Capacitor-Plugin-Module werden durch die Fake-Implementierung
// ersetzt (Muster: local-database.spec.ts).
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

beforeAll(() => {
  vi.useFakeTimers();
});

function snapshot(
  overrides: Partial<MasterDataSnapshot> = {},
): MasterDataSnapshot {
  const accounts: AccountDto[] = [
    {
      id: 'acc-1',
      name: 'Giro',
      type: 'bank',
      isInterestBearing: false,
      status: 'active',
      baseRevision: 5,
    },
  ];
  const pools: PoolDto[] = [
    { id: 'pool-1', name: 'Flex', status: 'active', baseRevision: 2 },
  ];
  const categories: CategoryDto[] = [
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
  const taxCategories: TaxCategoryDto[] = [
    { id: 'tax-1', name: 'USt', parentId: null, status: 'active', baseRevision: 1 },
  ];
  return {
    accounts: new Map(accounts.map((d) => [d.id, d])),
    pools: new Map(pools.map((d) => [d.id, d])),
    categories: new Map(categories.map((d) => [d.id, d])),
    taxCategories: new Map(taxCategories.map((d) => [d.id, d])),
    ...overrides,
  };
}

describe('SnapshotRepository (Batch C)', () => {
  let env: EnvironmentInjector;
  let db: LocalDatabase;
  let repo: SnapshotRepository;

  beforeEach(async () => {
    dbState.reset();
    env = createEnvironmentInjector(
      [LocalDatabase, MetaService, SnapshotRepository],
      TestBed.inject(EnvironmentInjector),
    );
    db = env.get(LocalDatabase);
    repo = env.get(SnapshotRepository);
    await db.init();
  });

  describe('replace + loadSnapshot', () => {
    it('vollständiger Snapshot wird identisch zurückgeliefert', async () => {
      const iso = '2026-10-26T12:00:00.000Z';
      await repo.replace(snapshot(), iso);

      const loaded = await repo.loadSnapshot();
      expect(loaded).not.toBeNull();
      expect(loaded!.accounts.get('acc-1')).toEqual({
        id: 'acc-1',
        name: 'Giro',
        type: 'bank',
        isInterestBearing: false,
        status: 'active',
        baseRevision: 5,
      });
      // baseRevision als number (nicht '5'), isInterestBearing als boolean.
      expect(typeof loaded!.accounts.get('acc-1')!.baseRevision).toBe('number');
      expect(typeof loaded!.accounts.get('acc-1')!.isInterestBearing).toBe('boolean');
      // taxPreset-Objekt rekonstruiert (nicht String).
      expect(loaded!.categories.get('cat-1')!.taxPreset).toEqual({
        taxRelevant: true,
        taxCategoryId: 'tax-1',
        taxCategoryName: 'USt',
        inheritToChildren: false,
        baseRevision: 4,
      });
      expect(loaded!.pools.get('pool-1')).toEqual({
        id: 'pool-1',
        name: 'Flex',
        status: 'active',
        baseRevision: 2,
      });
      expect(loaded!.taxCategories.get('tax-1')).toEqual({
        id: 'tax-1',
        name: 'USt',
        parentId: null,
        status: 'active',
        baseRevision: 1,
      });
      expect(await repo.fetchedAt()).toBe(iso);
    });

    it('replace ersetzt komplett: alte Zeilen weg, neue da', async () => {
      await repo.replace(snapshot(), '2026-10-26T12:00:00.000Z');
      const second = snapshot({
        accounts: new Map<string, AccountDto>([
          [
            'acc-9',
            {
              id: 'acc-9',
              name: 'Neues Konto',
              type: 'bank',
              isInterestBearing: false,
              status: 'active',
              baseRevision: 1,
            },
          ],
        ]),
        pools: new Map(),
        categories: new Map(),
        taxCategories: new Map(),
      });
      await repo.replace(second, '2026-10-27T12:00:00.000Z');

      const loaded = await repo.loadSnapshot();
      expect(loaded).not.toBeNull();
      expect([...loaded!.accounts.keys()]).toEqual(['acc-9']);
      expect(loaded!.accounts.has('acc-1')).toBe(false);
      expect(loaded!.pools.size).toBe(0);
      expect(loaded!.categories.size).toBe(0);
      expect(loaded!.taxCategories.size).toBe(0);
      expect(await repo.fetchedAt()).toBe('2026-10-27T12:00:00.000Z');
    });

    it('leere Maps: alle Tabellen leer, fetched_at gesetzt, leere (nicht null) Maps', async () => {
      const empty = snapshot({
        accounts: new Map(),
        pools: new Map(),
        categories: new Map(),
        taxCategories: new Map(),
      });
      await repo.replace(empty, '2026-10-26T12:00:00.000Z');

      expect(dbState.accounts.size).toBe(0);
      expect(dbState.pools.size).toBe(0);
      expect(dbState.categories.size).toBe(0);
      expect(dbState.taxCategories.size).toBe(0);

      const loaded = await repo.loadSnapshot();
      expect(loaded).not.toBeNull();
      expect(loaded!.accounts.size).toBe(0);
      expect(loaded!.pools.size).toBe(0);
      expect(loaded!.categories.size).toBe(0);
      expect(loaded!.taxCategories.size).toBe(0);

      const catalog = await repo.referenceCatalog();
      expect(catalog).not.toBeNull();
      expect(catalog!.accounts.size).toBe(0);
      expect(catalog!.pools.size).toBe(0);
      expect(catalog!.categories.size).toBe(0);
      expect(catalog!.taxCategories.size).toBe(0);
    });

    it('ohne replace: loadSnapshot null, referenceCatalog null', async () => {
      expect(await repo.loadSnapshot()).toBeNull();
      expect(await repo.referenceCatalog()).toBeNull();
      expect(await repo.fetchedAt()).toBeNull();
    });
  });

  describe('Atomarität (Design D3)', () => {
    it('Fehler beim 5. Statement (1. INSERT): komplettes Rollback, alter Zustand bleibt', async () => {
      const before = '2026-10-26T12:00:00.000Z';
      await repo.replace(snapshot(), before);

      // 4 DELETEs (Index 0–3) ok, erster INSERT (Index 4) schlägt fehl.
      dbState.failOnStatementIndex = 4;
      const after = '2026-10-27T12:00:00.000Z';
      const second = snapshot({
        accounts: new Map<string, AccountDto>([
          [
            'acc-2',
            {
              id: 'acc-2',
              name: 'Bar',
              type: 'cash',
              isInterestBearing: false,
              status: 'active',
              baseRevision: 1,
            },
          ],
        ]),
      });
      await expect(repo.replace(second, after)).rejects.toThrow(
        'simulated failure',
      );

      // Alter Snapshot vollständig intakt (keine Teiländerung).
      const loaded = await repo.loadSnapshot();
      expect(loaded).not.toBeNull();
      expect([...loaded!.accounts.keys()]).toEqual(['acc-1']);
      expect(loaded!.pools.get('pool-1')).toEqual({
        id: 'pool-1',
        name: 'Flex',
        status: 'active',
        baseRevision: 2,
      });
      expect(await repo.fetchedAt()).toBe(before);
    });

    it('Fehler beim allerersten replace: loadSnapshot bleibt null', async () => {
      dbState.failOnStatementIndex = 0;
      await expect(
        repo.replace(snapshot(), '2026-10-26T12:00:00.000Z'),
      ).rejects.toThrow('simulated failure');
      expect(await repo.loadSnapshot()).toBeNull();
      expect(await repo.fetchedAt()).toBeNull();
    });
  });

  describe('referenceCatalog', () => {
    it('gemischte Status korrekt gemappt (id → status)', async () => {
      await repo.replace(
        snapshot({
          accounts: new Map<string, AccountDto>([
            [
              'acc-1',
              {
                id: 'acc-1',
                name: 'Giro',
                type: 'bank',
                isInterestBearing: false,
                status: 'active',
                baseRevision: 1,
              },
            ],
            [
              'acc-2',
              {
                id: 'acc-2',
                name: 'Alt',
                type: 'bank',
                isInterestBearing: false,
                status: 'archived',
                baseRevision: 2,
              },
            ],
          ]),
          pools: new Map<string, PoolDto>([
            [
              'pool-1',
              { id: 'pool-1', name: 'Flex', status: 'active', baseRevision: 1 },
            ],
          ]),
        }),
        '2026-10-26T12:00:00.000Z',
      );

      const catalog = await repo.referenceCatalog();
      expect(catalog).not.toBeNull();
      expect(catalog!.accounts.get('acc-1')).toBe('active');
      expect(catalog!.accounts.get('acc-2')).toBe('archived');
      expect(catalog!.pools.get('pool-1')).toBe('active');
      expect(catalog!.categories.get('cat-1')).toBe('active');
      expect(catalog!.taxCategories.get('tax-1')).toBe('active');
    });
  });

  describe('fetchedAt', () => {
    it('null ohne Snapshot, ISO-String danach', async () => {
      expect(await repo.fetchedAt()).toBeNull();
      const iso = new Date().toISOString();
      await repo.replace(snapshot(), iso);
      expect(await repo.fetchedAt()).toBe(iso);
    });
  });

  describe('Serialisierung (P2, Batch C review)', () => {
    it('loadSnapshot unter parallelem replace: konsistenter Stand, kein Mischzustand', async () => {
      const t1 = '2026-10-26T12:00:00.000Z';
      await repo.replace(snapshot(), t1); // V1: acc-1/pool-1/cat-1/tax-1

      // Lese-Sektion und Replace werden „parallel“ auf derselben
      // Connection-Operations-Kette registriert: einer läuft komplett
      // vor dem anderen, niemals vermischt. (Alte Implementierung: 5
      // Einzel-Queries — ein replace hätte dazwischengreifen können.
      // )
      const load = repo.loadSnapshot();
      const t2 = '2026-10-27T12:00:00.000Z';
      const replace = repo.replace(
        snapshot({
          accounts: new Map([
            [
              'acc-9',
              {
                id: 'acc-9',
                name: 'Tresor',
                type: 'cash',
                isInterestBearing: false,
                status: 'active',
                baseRevision: 9,
              },
            ],
          ]),
          pools: new Map(),
          categories: new Map(),
          taxCategories: new Map(),
        }),
        t2,
      );

      const loaded = await load;
      await replace;

      expect(loaded).not.toBeNull();
      // Konsistenz: ALLE vier Tabellen aus derselben Version —
      // kompletter alter Stand (V1) oder kompletter neuer Stand (V2).
      const v1Konsistent =
        loaded!.accounts.has('acc-1') &&
        loaded!.pools.has('pool-1') &&
        loaded!.categories.has('cat-1') &&
        loaded!.taxCategories.has('tax-1') &&
        !loaded!.accounts.has('acc-9');
      const v2Konsistent =
        loaded!.accounts.has('acc-9') &&
        !loaded!.accounts.has('acc-1') &&
        loaded!.pools.size === 0 &&
        loaded!.categories.size === 0 &&
        loaded!.taxCategories.size === 0;
      expect(v1Konsistent || v2Konsistent).toBe(true);
      // Endzustand ist komplettes V2 (das Replace kam hinter der
      // Lese-Sektion in der Kette an).
      expect(await repo.fetchedAt()).toBe(t2);
      const final = await repo.loadSnapshot();
      expect(final!.accounts.has('acc-9')).toBe(true);
      expect(final!.accounts.has('acc-1')).toBe(false);
    });
  });
});
