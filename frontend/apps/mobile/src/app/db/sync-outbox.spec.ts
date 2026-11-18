/**
 * Schritt 9 (Batch B) — Review-Befund P1: Outbox-Zustandsmaschine mit
 * echter SQLite (`node:sqlite`).
 *
 * Die übrigen Specs laufen im jsdom-Umfeld mit dem in-memory-Fake;
 * diese Datei prüft die CAS-/Quittungs-Logik gegen eine ECHTE
 * SQLite-Datei, weil das Review-Befund-Verhalten (Bindung der
 * Quittung an den gesendeten Payload-Hash, atomare
 * Outbox-/Buchungs-Updates, Transaktions-Rollback) von echtem
 * Transaktions- und Constraint-Verhalten abhängt.
 *
 * Der gemockte `@capacitor-community/sqlite`-Modul ist eine
 * `node:sqlite`-Backed-Implementierung des Plugin-Vertrags:
 * `beginTransaction`/`commitTransaction`/`rollbackTransaction`
 * steuern echte SQL-Transaktionen, `executeSet` (transaction: true)
 * läuft in einer eigenen Transaktion, Callback-Statements
 * (transaction: false) laufen in der offenen Transaktion.
 */
// @vitest-environment node
import { mkdtempSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { DatabaseSync } from 'node:sqlite';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';

import { Injector } from '@angular/core';

import { BookingStateError, BookingsRepository } from './bookings.repository';
import { LocalDatabase } from './local-database';
import { MetaService } from './meta.service';
import { SnapshotRepository } from './snapshot.repository';
import { type LocalExpenseInput } from './expense-payload';
import { UPDATE_BOOKING_BASE_REVISION } from './statements';
import { payloadHash } from './sha256';

/** Echte SQLite-Datei (pro Test neu). */
let sqlite: DatabaseSync;

/** Temp-Ordner der echten SQLite-Datei (Cleanup in afterEach). */
let sqliteDir: string;

/**
 * Test-Hook: erzwingt einen Fehler auf genau diesem Statement
 * (Atomaritäts-Test: zweite Statement der Quittungs-Transaktion
 * schlägt fehl → kompletter Rollback).
 */
let failBookingRevisionUpdate = false;

vi.mock('@capacitor/core', () => ({
  Capacitor: { isNativePlatform: () => true },
}));

// node:sqlite-Backed-Adapter: die Factory referenziert `sqlite`/
// `failBookingRevisionUpdate` nur in den Methodenköpfen (Lazy-Read) —
// beim Hoisting werden die Bindungen noch nicht gelesen (kein TDZ).
vi.mock('@capacitor-community/sqlite', () => {
  interface SetItem {
    statement: string;
    values?: unknown[];
  }
  return {
    CapacitorSQLite: {
      createConnection: async () => undefined,
      open: async () => undefined,
      executeSet: async (opts: {
        set?: SetItem[];
        transaction?: boolean;
      }) => {
        const sets = (opts.set ?? []).map((s) => ({
          statement: s.statement,
          values: (s.values ?? []) as (string | number | bigint | null)[],
        }));
        const runAll = () => {
          for (const s of sets) {
            if (failBookingRevisionUpdate &&
                s.statement === UPDATE_BOOKING_BASE_REVISION) {
              throw new Error('Simulierter Schreibfehler (Atomaritäts-Test)');
            }
            sqlite.prepare(s.statement).run(...s.values);
          }
        };
        if (opts.transaction !== false) {
          sqlite.exec('BEGIN');
          try {
            runAll();
            sqlite.exec('COMMIT');
          } catch (error) {
            sqlite.exec('ROLLBACK');
            throw error;
          }
        } else {
          runAll();
        }
      },
      query: async (opts: { statement: string; values?: unknown[] }) => {
        const values = (opts.values ?? []) as (string | number | bigint | null)[];
        const rows = sqlite.prepare(opts.statement).all(...values) as
          Record<string, unknown>[];
        return { values: rows };
      },
      beginTransaction: async () => {
        sqlite.exec('BEGIN IMMEDIATE');
      },
      commitTransaction: async () => {
        sqlite.exec('COMMIT');
      },
      rollbackTransaction: async () => {
        try {
          sqlite.exec('ROLLBACK');
        } catch {
          // Defensiv: ohne aktive Transaktion no-op (Plugin-Vertrag).
        }
      },
      isTransactionActive: async () => ({ result: false }),
      run: async (opts: { statement: string; values?: unknown[] }) => {
        sqlite
          .prepare(opts.statement)
          .run(...((opts.values ?? []) as (string | number | bigint | null)[]));
        return { lastInsertRowId: 0, changes: 0 };
      },
    },
  };
});

/** Feste Uhr: 2026-11-02 10:00:00 (lokal). */
const FIXED_NOW = new Date(2026, 10, 2, 10, 0, 0);

const SNAPSHOT_ISO = '2026-10-26T12:00:00.000Z';

/** Standard-Snapshot (gleiche Referenzen wie die übrigen Specs). */
async function loadStandardSnapshot(
  snapshotRepository: SnapshotRepository,
): Promise<void> {
  await snapshotRepository.replace(
    {
      accounts: new Map([
        [
          'acc-1',
          { id: 'acc-1', name: 'Giro', type: 'bank', isInterestBearing: false, status: 'active', baseRevision: 1 },
        ],
      ]),
      pools: new Map([
        ['pool-1', { id: 'pool-1', name: 'Flex', status: 'active', baseRevision: 1 }],
        ['pool-2', { id: 'pool-2', name: 'Raten', status: 'active', baseRevision: 1 }],
      ]),
      categories: new Map([
        ['cat-1', { id: 'cat-1', name: 'Essen', parentId: null, status: 'active', baseRevision: 1, taxPreset: null }],
        ['cat-2', { id: 'cat-2', name: 'Miete', parentId: null, status: 'active', baseRevision: 1, taxPreset: null }],
      ]),
      taxCategories: new Map([
        ['tax-1', { id: 'tax-1', name: 'USt', parentId: null, status: 'active', baseRevision: 1 }],
      ]),
    },
    SNAPSHOT_ISO,
  );
}

/** Beispiel-Ausgabe (10,00 EUR = 6,00 + 4,00). */
const SAMPLE_INPUT: LocalExpenseInput = {
  date: '2026-11-02',
  totalAmountCents: 1000,
  accountId: 'acc-1',
  note: null,
  splits: [
    {
      id: 'form-s1',
      amountCents: 600,
      poolId: 'pool-1',
      categoryId: 'cat-1',
      note: null,
      tax: null,
    },
    {
      id: 'form-s2',
      amountCents: 400,
      poolId: 'pool-2',
      categoryId: 'cat-2',
      note: null,
      tax: null,
    },
  ],
};

/** Variante (andere Beträge) für Update-Tests. */
const UPDATED_INPUT: LocalExpenseInput = {
  ...SAMPLE_INPUT,
  note: 'korrigiert',
  splits: [
    {
      id: 'form-s1',
      amountCents: 700,
      poolId: 'pool-1',
      categoryId: 'cat-1',
      note: null,
      tax: null,
    },
    {
      id: 'form-s2',
      amountCents: 300,
      poolId: 'pool-2',
      categoryId: 'cat-2',
      note: null,
      tax: null,
    },
  ],
};

let db: LocalDatabase;
let repo: BookingsRepository;

/** Outbox-Zeile direkt aus der echten Datenbank lesen. */
async function outboxRow(
  operationId: string,
): Promise<Record<string, unknown> | null> {
  const rows = await db.query<Record<string, unknown>>(
    'SELECT * FROM outbox WHERE operation_id = ?',
    [operationId],
  );
  return rows.length > 0 ? rows[0] : null;
}

/** `base_revision` der Buchung direkt aus der echten Datenbank lesen. */
async function baseRevision(bookingId: string): Promise<number | null> {
  const rows = await db.query<Record<string, unknown>>(
    'SELECT base_revision FROM bookings WHERE id = ?',
    [bookingId],
  );
  if (rows.length === 0) {
    return null;
  }
  const value = rows[0]['base_revision'];
  return value === null || value === undefined ? null : Number(value);
}

describe('P1: Outbox-Zustandsmaschine (echte SQLite)', () => {
  beforeEach(async () => {
    failBookingRevisionUpdate = false;
    sqliteDir = mkdtempSync(join(tmpdir(), 'lario-sync-outbox-'));
    sqlite = new DatabaseSync(join(sqliteDir, 'test.db'));
    // Reine DI (ohne TestBed): dieselben root-Provider wie die App;
    // LocalDatabase nutzt den node:sqlite-Backed-Plugin-Mock.
    const injector = Injector.create({
      providers: [
        LocalDatabase,
        MetaService,
        SnapshotRepository,
        BookingsRepository,
      ],
    });
    db = injector.get(LocalDatabase);
    await db.init();
    await loadStandardSnapshot(injector.get(SnapshotRepository));
    repo = injector.get(BookingsRepository);
  });

  afterEach(() => {
    sqlite.close();
    rmSync(sqliteDir, { recursive: true, force: true });
    failBookingRevisionUpdate = false;
  });

  describe('A2: Payload-Nachweis (payload_hash)', () => {
    it('save: exakt eine Outbox-Zeile; Hash = payloadHash(gespeichertes Payload)', async () => {
      const { operationId } = await repo.save(SAMPLE_INPUT, FIXED_NOW);

      const row = await outboxRow(operationId);
      expect(row).not.toBeNull();
      expect(String(row!['status'])).toBe('queued');
      // Der gespeicherte Hash ist aus dem gespeicherten Payload
      // ableitbar — Nachweis statt blindem Vertrauen.
      expect(String(row!['payload_hash'])).toBe(
        payloadHash(String(row!['payload'])),
      );
    });

    it('update: Payload und Hash werden ersetzt; Hash bleibt ableitbar', async () => {
      const { bookingId, operationId } = await repo.save(SAMPLE_INPUT, FIXED_NOW);
      await repo.update(bookingId, UPDATED_INPUT, new Date(2026, 10, 2, 11, 0, 0));

      const row = await outboxRow(operationId);
      const storedPayload = String(row!['payload']);
      // Neuer Inhalt (Korrektur sichtbar):
      expect(storedPayload).toContain('korrigiert');
      // Hash passt zum NEUEN Payload:
      expect(String(row!['payload_hash'])).toBe(payloadHash(storedPayload));
      expect(String(row!['status'])).toBe('queued');
    });

    it('discard: Outbox-Zeile wird mit der Buchung entfernt (FK-Cascade)', async () => {
      const { bookingId } = await repo.save(SAMPLE_INPUT, FIXED_NOW);
      await repo.discard(bookingId);

      const count = await db.query<Record<string, unknown>>(
        'SELECT COUNT(*) AS n FROM outbox WHERE booking_id = ?',
        [bookingId],
      );
      expect(Number(count[0]['n'])).toBe(0);
    });
  });

  describe('P1: prepareSend (atomare Send-Reservierung)', () => {
    it('queued → sending; eingefrorener Inhalt wird zurückgegeben', async () => {
      const { operationId } = await repo.save(SAMPLE_INPUT, FIXED_NOW);

      const frozen = await repo.prepareSend(operationId, FIXED_NOW);

      const row = await outboxRow(operationId);
      expect(String(row!['status'])).toBe('sending');
      expect(frozen.payloadHash).toBe(String(row!['payload_hash']));
      expect(frozen.payloadJson).toBe(String(row!['payload']));
      expect(frozen.operationId).toBe(operationId);
    });

    it('zweite prepareSend (sending) liefert identischen Inhalt (Retry)', async () => {
      const { operationId } = await repo.save(SAMPLE_INPUT, FIXED_NOW);
      const first = await repo.prepareSend(operationId, FIXED_NOW);
      const second = await repo.prepareSend(operationId, FIXED_NOW);

      expect(second).toEqual(first);
    });

    it('confirmed → BookingStateError not_queued', async () => {
      const { bookingId, operationId } = await repo.save(SAMPLE_INPUT, FIXED_NOW);
      const frozen = await repo.prepareSend(operationId, FIXED_NOW);
      await repo.markConfirmed(
        bookingId,
        operationId,
        5,
        frozen.payloadHash,
        FIXED_NOW,
      );

      await expect(repo.prepareSend(operationId, FIXED_NOW)).rejects.toThrowError(
        BookingStateError,
      );
      await expect(repo.prepareSend(operationId, FIXED_NOW)).rejects.toMatchObject({
        reason: 'not_queued',
      });
    });

    it('rejected → BookingStateError not_queued', async () => {
      const { operationId } = await repo.save(SAMPLE_INPUT, FIXED_NOW);
      const frozen = await repo.prepareSend(operationId, FIXED_NOW);
      await repo.markRejected(operationId, 'Konflikt', frozen.payloadHash, FIXED_NOW);

      await expect(repo.prepareSend(operationId, FIXED_NOW)).rejects.toMatchObject({
        reason: 'not_queued',
      });
    });

    it('fehlende Operation → not_found', async () => {
      await expect(
        repo.prepareSend('op-2026-1102-999', FIXED_NOW),
      ).rejects.toMatchObject({ reason: 'not_found' });
    });

    it('update während sending wird verweigert (eingefroren)', async () => {
      const { bookingId, operationId } = await repo.save(SAMPLE_INPUT, FIXED_NOW);
      await repo.prepareSend(operationId, FIXED_NOW);

      await expect(
        repo.update(bookingId, UPDATED_INPUT, FIXED_NOW),
      ).rejects.toMatchObject({ reason: 'not_queued' });
    });

    it('discard während sending wird verweigert (eingefroren)', async () => {
      const { bookingId, operationId } = await repo.save(SAMPLE_INPUT, FIXED_NOW);
      await repo.prepareSend(operationId, FIXED_NOW);

      await expect(repo.discard(bookingId)).rejects.toMatchObject({
        reason: 'not_queued',
      });
    });
  });

  describe('P1: markConfirmed (Quittung an gesendeten Inhalt gebunden)', () => {
    it('Erfolg: Outbox confirmed + base_revision — atomar', async () => {
      const { bookingId, operationId } = await repo.save(SAMPLE_INPUT, FIXED_NOW);
      const frozen = await repo.prepareSend(operationId, FIXED_NOW);

      await repo.markConfirmed(bookingId, operationId, 7, frozen.payloadHash, FIXED_NOW);

      const row = await outboxRow(operationId);
      expect(String(row!['status'])).toBe('confirmed');
      expect(Number(row!['expected_base_revision'])).toBe(7);
      expect(await baseRevision(bookingId)).toBe(7);
    });

    it('Doppel-Quittung: no-op, base_revision wird NICHT erneut erhöht', async () => {
      const { bookingId, operationId } = await repo.save(SAMPLE_INPUT, FIXED_NOW);
      const frozen = await repo.prepareSend(operationId, FIXED_NOW);
      await repo.markConfirmed(bookingId, operationId, 7, frozen.payloadHash, FIXED_NOW);

      // Späte/doppelte Antwort desselben Inhalts:
      await repo.markConfirmed(bookingId, operationId, 8, frozen.payloadHash, FIXED_NOW);

      expect(await baseRevision(bookingId)).toBe(7);
      const row = await outboxRow(operationId);
      expect(Number(row!['expected_base_revision'])).toBe(7);
    });

    it('Hash-Abweichung: payload_conflict, Zustand bleibt sending', async () => {
      const { bookingId, operationId } = await repo.save(SAMPLE_INPUT, FIXED_NOW);
      await repo.prepareSend(operationId, FIXED_NOW);

      const wrongHash = 'sha256:' + 'ab'.repeat(32);
      await expect(
        repo.markConfirmed(bookingId, operationId, 7, wrongHash, FIXED_NOW),
      ).rejects.toMatchObject({ reason: 'payload_conflict' });

      const row = await outboxRow(operationId);
      expect(String(row!['status'])).toBe('sending');
      expect(await baseRevision(bookingId)).toBe(null);
    });

    it('von rejected: unexpected_status, Zustand bleibt rejected', async () => {
      const { bookingId, operationId } = await repo.save(SAMPLE_INPUT, FIXED_NOW);
      const frozen = await repo.prepareSend(operationId, FIXED_NOW);
      await repo.markRejected(operationId, 'Konflikt', frozen.payloadHash, FIXED_NOW);

      await expect(
        repo.markConfirmed(bookingId, operationId, 7, frozen.payloadHash, FIXED_NOW),
      ).rejects.toMatchObject({ reason: 'unexpected_status' });

      const row = await outboxRow(operationId);
      expect(String(row!['status'])).toBe('rejected');
      expect(await baseRevision(bookingId)).toBe(null);
    });

    it('fremde bookingId: unexpected_status', async () => {
      const first = await repo.save(SAMPLE_INPUT, FIXED_NOW);
      const second = await repo.save(SAMPLE_INPUT, FIXED_NOW);
      const frozen = await repo.prepareSend(first.operationId, FIXED_NOW);

      await expect(
        repo.markConfirmed(
          second.bookingId,
          first.operationId,
          7,
          frozen.payloadHash,
          FIXED_NOW,
        ),
      ).rejects.toMatchObject({ reason: 'unexpected_status' });
    });

    it('Atomarität: zweites Statement schlägt fehl → kompletter Rollback', async () => {
      const { bookingId, operationId } = await repo.save(SAMPLE_INPUT, FIXED_NOW);
      const frozen = await repo.prepareSend(operationId, FIXED_NOW);
      // Die Buchungs-Update innerhalb der Quittungs-Transaktion
      // schlagen fehl — die Outbox-Übergabe muss mit zurückgerollt
      // werden (kein Teilzustand "confirmed ohne Revision").
      failBookingRevisionUpdate = true;

      await expect(
        repo.markConfirmed(bookingId, operationId, 7, frozen.payloadHash, FIXED_NOW),
      ).rejects.toThrow('Simulierter Schreibfehler (Atomaritäts-Test)');

      const row = await outboxRow(operationId);
      expect(String(row!['status'])).toBe('sending');
      expect(await baseRevision(bookingId)).toBe(null);
    });
  });

  describe('P1: markRejected (Ablehnung an gesendeten Inhalt gebunden)', () => {
    it('Erfolg: rejected + Grund', async () => {
      const { operationId } = await repo.save(SAMPLE_INPUT, FIXED_NOW);
      const frozen = await repo.prepareSend(operationId, FIXED_NOW);

      await repo.markRejected(operationId, 'Revision-Konflikt', frozen.payloadHash, FIXED_NOW);

      const row = await outboxRow(operationId);
      expect(String(row!['status'])).toBe('rejected');
      expect(String(row!['status_reason'])).toBe('Revision-Konflikt');
    });

    it('doppelte Ablehnung: no-op', async () => {
      const { operationId } = await repo.save(SAMPLE_INPUT, FIXED_NOW);
      const frozen = await repo.prepareSend(operationId, FIXED_NOW);
      await repo.markRejected(operationId, 'Konflikt', frozen.payloadHash, FIXED_NOW);

      await repo.markRejected(operationId, 'Anderer Text', frozen.payloadHash, FIXED_NOW);

      const row = await outboxRow(operationId);
      expect(String(row!['status'])).toBe('rejected');
      expect(String(row!['status_reason'])).toBe('Konflikt');
    });

    it('nach confirmed: confirmed bleibt (kein Downgrade)', async () => {
      const { bookingId, operationId } = await repo.save(SAMPLE_INPUT, FIXED_NOW);
      const frozen = await repo.prepareSend(operationId, FIXED_NOW);
      await repo.markConfirmed(bookingId, operationId, 7, frozen.payloadHash, FIXED_NOW);

      // Späte Ablehnungsantwort für denselben Inhalt:
      await repo.markRejected(operationId, 'Zu spät', frozen.payloadHash, FIXED_NOW);

      const row = await outboxRow(operationId);
      expect(String(row!['status'])).toBe('confirmed');
      expect(await baseRevision(bookingId)).toBe(7);
    });

    it('Hash-Abweichung: payload_conflict, Zustand bleibt sending', async () => {
      const { operationId } = await repo.save(SAMPLE_INPUT, FIXED_NOW);
      await repo.prepareSend(operationId, FIXED_NOW);

      const wrongHash = 'sha256:' + 'cd'.repeat(32);
      await expect(
        repo.markRejected(operationId, 'Konflikt', wrongHash, FIXED_NOW),
      ).rejects.toMatchObject({ reason: 'payload_conflict' });

      const row = await outboxRow(operationId);
      expect(String(row!['status'])).toBe('sending');
      expect(row!['status_reason'] ?? null).toBe(null);
    });

    it('von queued (ohne prepareSend): unexpected_status', async () => {
      const { operationId } = await repo.save(SAMPLE_INPUT, FIXED_NOW);

      const hash = 'sha256:' + 'ef'.repeat(32);
      await expect(
        repo.markRejected(operationId, 'Konflikt', hash, FIXED_NOW),
      ).rejects.toMatchObject({ reason: 'unexpected_status' });

      const row = await outboxRow(operationId);
      expect(String(row!['status'])).toBe('queued');
    });
  });

  describe('P1: listSyncable (sendbare Zeilen)', () => {
    it('liefert queued + sending in Sende-Reihenfolge; confirmed/rejected nicht', async () => {
      const b1 = await repo.save(SAMPLE_INPUT, new Date(2026, 10, 2, 9, 0, 0));
      const b2 = await repo.save(SAMPLE_INPUT, new Date(2026, 10, 2, 9, 5, 0));
      const b3 = await repo.save(SAMPLE_INPUT, new Date(2026, 10, 2, 9, 10, 0));

      // b1 → sending, b2 bleibt queued, b3 → confirmed.
      const frozen1 = await repo.prepareSend(b1.operationId, FIXED_NOW);
      const frozen3 = await repo.prepareSend(b3.operationId, FIXED_NOW);
      await repo.markConfirmed(
        b3.bookingId,
        b3.operationId,
        3,
        frozen3.payloadHash,
        FIXED_NOW,
      );
      void frozen1;

      const syncable = await repo.listSyncable();
      expect(syncable.map((o) => o.operationId)).toEqual([
        b1.operationId,
        b2.operationId,
      ]);
    });
  });
});
