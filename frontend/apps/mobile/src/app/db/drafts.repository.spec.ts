// @vitest-environment jsdom
import { EnvironmentInjector, createEnvironmentInjector } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import {
  capacitorSqliteMockFactory,
  dbState,
  type CapacitorSqliteMock,
} from './capacitor-sqlite-fake';
import { LocalDatabase } from './local-database';
import { DraftsRepository } from './drafts.repository';

vi.mock('@capacitor/core', () => ({
  Capacitor: { isNativePlatform: () => true },
}));

// Das Mocker-Hoisting setzt diesen Aufruf ueber alle Import-Konstanten der
// Spec; die Factory laeuft, wenn das gemockte Modul zuerst importiert wird —
// das passiert innerhalb des geteilten esbuild-Chunks (Fake + LocalDatabase)
// VOR der Initialisierung der Import-Konstanten der Spec. Die Factory darf
// daher KEINE Import-Referenzen unmittelbar loesen: Sie erzeugt nur das
// Objekt, die Fake-Referenz wird erst bei tatsaechlichem Methodenaufruf
// (in den Tests) geloest.
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
    } satisfies CapacitorSqliteMock,
  };
});

/** Feste Uhr: 2026-11-01 09:30:00 (lokal). */
const FIXED_NOW = new Date(2026, 10, 1, 9, 30, 0);
const LATER = new Date(2026, 10, 1, 10, 0, 0);

/**
 * Entwurfs-Repository (Schritt 8, Batch B, B7): Entwürfe sind
 * Formular-Zustand, unabhängig von Buchung/Outbox.
 */
describe('DraftsRepository (Batch B, B7)', () => {
  let db: LocalDatabase;
  let drafts: DraftsRepository;

  beforeEach(async () => {
    dbState.reset();
    const env = createEnvironmentInjector(
      [LocalDatabase, DraftsRepository],
      TestBed.inject(EnvironmentInjector),
    );
    db = env.get(LocalDatabase);
    drafts = env.get(DraftsRepository);
    await db.init();
  });

  it('save: neuer Entwurf mit createdAt = updatedAt', async () => {
    await drafts.save('draft-1', 'Mittagessen', '{"amount":4500}', FIXED_NOW);

    const draft = await drafts.get('draft-1');
    expect(draft).toEqual({
      id: 'draft-1',
      name: 'Mittagessen',
      data: '{"amount":4500}',
      createdAtUtc: FIXED_NOW.toISOString(),
      updatedAtUtc: FIXED_NOW.toISOString(),
    });
  });

  it('save: bestehender Entwurf wird ersetzt, createdAt bleibt erhalten (Upsert)', async () => {
    await drafts.save('draft-1', 'Mittagessen', '{"amount":4500}', FIXED_NOW);
    await drafts.save('draft-1', 'Mittagessen (geändert)', '{"amount":5000}', LATER);

    const draft = await drafts.get('draft-1');
    expect(draft?.name).toBe('Mittagessen (geändert)');
    expect(draft?.data).toBe('{"amount":5000}');
    expect(draft?.createdAtUtc).toBe(FIXED_NOW.toISOString());
    expect(draft?.updatedAtUtc).toBe(LATER.toISOString());
    expect(dbState.drafts.size).toBe(1);
  });

  it('get: null, wenn der Entwurf fehlt', async () => {
    expect(await drafts.get('draft-missing')).toBeNull();
  });

  it('list: zuletzt geänderter Entwurf zuerst', async () => {
    await drafts.save('draft-a', 'A', '{}', FIXED_NOW);
    await drafts.save('draft-b', 'B', '{}', LATER);
    // draft-a erneut ändern → wird der Neueste.
    await drafts.save('draft-a', 'A (neu)', '{}', new Date(2026, 10, 1, 11, 0, 0));

    const list = await drafts.list();
    expect(list.map((d) => d.id)).toEqual(['draft-a', 'draft-b']);
  });

  it('delete: Entwurf wird entfernt; get liefert danach null', async () => {
    await drafts.save('draft-1', 'Mittagessen', '{}', FIXED_NOW);

    await drafts.delete('draft-1');

    expect(await drafts.get('draft-1')).toBeNull();
    expect(dbState.drafts.size).toBe(0);
    // Doppeltes Löschen: No-Op (SQLite DELETE ohne Treffer).
    await drafts.delete('draft-1');
    expect(dbState.drafts.size).toBe(0);
  });
});
