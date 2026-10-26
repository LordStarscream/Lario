// @vitest-environment jsdom
import { EnvironmentInjector, createEnvironmentInjector } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import {
  capacitorSqliteMockFactory,
  dbState,
  type CapacitorSqliteMock,
} from './capacitor-sqlite-fake';
import { LocalDatabase } from './local-database';
import { MetaService } from './meta.service';
import { DEVICE_ID_META_KEY } from './bookings.repository';
import {
  BookingsRepository,
  BookingStateError,
  BookingValidationError,
} from './bookings.repository';
import { OP_SEQ_META_KEY, formatBookingId } from './operation-id';
import {
  buildExpensePayload,
  type LocalExpenseInput,
} from './expense-payload';
import { payloadHash } from './sha256';

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

const UUID_RE =
  /^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[0-9a-f]{4}-[0-9a-f]{12}$/;

/**
 * Beispiel-Ausgabe (45,00 EUR = 25,00 + 20,00). Ein Split trägt eine
 * Steuerkennzeichnung; Split-IDs sind FORM-IDs (ohne Booking-Prefix) —
 * das Repository setzt die kanonischen `<bookingId>-<n>`.
 */
const SAMPLE_INPUT: LocalExpenseInput = {
  date: '2026-11-01',
  totalAmountCents: 4500,
  accountId: 'acc-1',
  note: null,
  splits: [
    {
      id: 'form-s1',
      amountCents: 2500,
      poolId: 'pool-1',
      categoryId: 'cat-1',
      note: null,
      tax: null,
    },
    {
      id: 'form-s2',
      amountCents: 2000,
      poolId: 'pool-2',
      categoryId: 'cat-2',
      note: null,
      tax: {
        relevant: true,
        taxCategoryId: 'tax-1',
        taxCategoryName: 'Steuer A',
        markedAmountCents: 2000,
        note: null,
      },
    },
  ],
};

/**
 * `BookingsRepository.save` — die einzige Anlagepfad-Komposition
 * (Schritt 8, Batch B — Design D7):
 *
 * (a) Neue Eingabe nach App-Start:
 *     - `device.id` wird einmalig erzeugt und persistiert,
 *     - `operation_id` = `op-<JJJJ>-<MMDD>-<seq>` (Zähler persistiert,
 *       kein Tages-Reset),
 *     - Buchung, Splits, Outbox-Zeile und Zähler in EINEM atomaren
 *       `executeSet` (ein All-or-Nothing-Batch),
 *     - Splits erhalten kanonische IDs, die identisch in Payload und
 *       Datenbank vorliegen;
 *     - parallele Saves erhalten eindeutige Operation-IDs.
 * (b) Wiederholte Speicherversuche: keine Duplikate (Operation-IDs
 *     eindeutig, keine PK-Kollisionen).
 * (e) Fehlendes Plugin / fehlgeschlagener `executeSet`: kein Teilzustand —
 *     keine Buchung, keine Splits, keine Outbox-Zeile, Zähler unverändert.
 * (i) `save` verifiziert die Eingabe (Splitsummendefizit → Fehler,
 *     nichts wird geschrieben).
 * (k) Zustandsfehler: `update`/`discard` auf fehlender Buchung
 *     (`not_found`) bzw. nicht-queued Buchung (`not_queued`) werfen
 *     `BookingStateError`; `save` bleibt der einzige Anlegepfad.
 */
describe('BookingsRepository.save (Batch B, Design D7)', () => {
  let db: LocalDatabase;
  let meta: MetaService;
  let repo: BookingsRepository;

  /** Anzahl der executeSet-Batches nach App-Start (init + device.id). */
  let initBatchCount = 0;

  beforeEach(async () => {
    dbState.reset();
    // Eigener Environment-Injector pro Test => frische Singletons.
    const env = createEnvironmentInjector(
      [LocalDatabase, MetaService, BookingsRepository],
      TestBed.inject(EnvironmentInjector),
    );
    db = env.get(LocalDatabase);
    meta = env.get(MetaService);
    repo = env.get(BookingsRepository);
    // App-Start: Schema-Initialisierung.
    await db.init();
    initBatchCount = dbState.executedSets.length;
  });

  describe('(a) neue Eingabe nach App-Start', () => {
    it('save: Buchung, Splits und queued-Outbox-Zeile sind konsistent in der Datenbank', async () => {
      const result = await repo.save(SAMPLE_INPUT, FIXED_NOW);

      const bookingId = formatBookingId(1, FIXED_NOW.getTime());
      expect(result.bookingId).toBe(bookingId);
      expect(result.operationId).toBe('op-2026-1101-001');

      // Buchungszeile.
      expect(dbState.bookings.get(bookingId)).toMatchObject({
        date: '2026-11-01',
        type: 'expense',
        account_id: 'acc-1',
        total_amount_cents: 4500,
        note: null,
        base_revision: null,
        created_at_utc: FIXED_NOW.toISOString(),
      });

      // Split-Zeilen: kanonische IDs, Reihenfolge, Werte.
      const splitRows = [...dbState.splits.values()].filter(
        (s) => s.booking_id === bookingId,
      );
      expect(splitRows.map((s) => s.id)).toEqual([
        `${bookingId}-s1`,
        `${bookingId}-s2`,
      ]);
      expect(splitRows[0]).toMatchObject({
        amount_cents: 2500,
        pool_id: 'pool-1',
        category_id: 'cat-1',
        tax_relevant: null,
        tax_category_id: null,
        marked_amount_cents: null,
      });
      expect(splitRows[1]).toMatchObject({
        amount_cents: 2000,
        pool_id: 'pool-2',
        category_id: 'cat-2',
        tax_relevant: 1,
        tax_category_id: 'tax-1',
        marked_amount_cents: 2000,
      });

      // Outbox-Zeile: Payload/Hash korrekt, device.id = UUID.
      const row = dbState.outbox.get('op-2026-1101-001');
      const deviceId = row!.device_id;
      expect(deviceId).toMatch(UUID_RE);
      expect(row!.type).toBe('expense');
      expect(row!.booking_id).toBe(bookingId);
      const expectedInput: LocalExpenseInput = {
        ...SAMPLE_INPUT,
        splits: [
          { ...SAMPLE_INPUT.splits[0]!, id: `${bookingId}-s1` },
          { ...SAMPLE_INPUT.splits[1]!, id: `${bookingId}-s2` },
        ],
      };
      const expectedPayload = buildExpensePayload(bookingId, expectedInput);
      expect(row!.payload).toBe(expectedPayload);
      expect(row!.payload_hash).toBe(payloadHash(expectedPayload));
      expect(row!.expected_base_revision).toBeNull();
      expect(row!.status).toBe('queued');
      expect(row!.created_at_utc).toBe(FIXED_NOW.toISOString());
      expect(row!.updated_at_utc).toBe(FIXED_NOW.toISOString());

      // Meta: Zähler und device.id persistiert.
      expect(await meta.get(OP_SEQ_META_KEY)).toBe('2');
      expect(await meta.get(DEVICE_ID_META_KEY)).toBe(deviceId);
    });

    it('save: die kanonischen Split-IDs sind identisch in Payload und Datenbank (Design D7)', async () => {
      const result = await repo.save(SAMPLE_INPUT, FIXED_NOW);
      const row = dbState.outbox.get(result.operationId);
      const dbSplitIds = [...dbState.splits.values()]
        .filter((s) => s.booking_id === result.bookingId)
        .map((s) => s.id);
      const payloadSplitIds = (JSON.parse(row!.payload) as {
        splits: { id: string }[];
      }).splits.map((s) => s.id);
      expect(payloadSplitIds).toEqual(dbSplitIds);
    });

    it('save: Buchung, Splits, Outbox und Zähler sind EIN atomares executeSet (D7)', async () => {
      await repo.save(SAMPLE_INPUT, FIXED_NOW);
      const batches = dbState.executedSets.slice(initBatchCount);
      // Batch 1: device.id (Meta) — einmalig.
      // Batch 2: die Anlage-Transaktion.
      expect(batches).toHaveLength(2);
      expect(batches[0]!.sets).toHaveLength(1);
      expect(batches[0]!.sets[0]!.statement).toContain('INSERT INTO meta');
      expect(batches[0]!.sets[0]!.values).toEqual([
        DEVICE_ID_META_KEY,
        expect.any(String),
      ]);

      const main = batches[1]!;
      expect(main.transaction).toBe(true);
      expect(main.database).toBe(LocalDatabase.DbName);
      expect(main.sets).toHaveLength(5);
      expect(main.sets[0]!.statement).toContain('INSERT INTO bookings');
      expect(main.sets[1]!.statement).toContain('INSERT INTO splits');
      expect(main.sets[2]!.statement).toContain('INSERT INTO splits');
      expect(main.sets[3]!.statement).toContain('INSERT INTO outbox');
      expect(main.sets[4]!.statement).toContain('INSERT INTO meta');
      expect(main.sets[4]!.values).toEqual([OP_SEQ_META_KEY, '2']);

      // Zweite Anlage: device.id existiert bereits (kein Extra-Meta-Batch),
      // das Haupt-Batch hat dieselbe 5-Statement-Form, Zähler = 3.
      await repo.save(SAMPLE_INPUT, new Date(2026, 10, 1, 9, 31, 0));
      const after = dbState.executedSets.slice(initBatchCount);
      expect(after).toHaveLength(3);
      expect(after[2]!.sets).toHaveLength(5);
      expect(after[2]!.sets[0]!.statement).toContain('INSERT INTO bookings');
      expect(after[2]!.sets[4]!.values).toEqual([OP_SEQ_META_KEY, '3']);
    });

    it('zweite Eingabe (anderes Datum): deviceId stabil, seq wächst, kein Tages-Reset (D4)', async () => {
      const first = await repo.save(SAMPLE_INPUT, FIXED_NOW);
      const firstDevice = dbState.outbox.get(first.operationId)!.device_id;

      const second = await repo.save(
        SAMPLE_INPUT,
        new Date(2026, 10, 2, 8, 0, 0),
      );
      expect(second.operationId).toBe('op-2026-1102-002');
      expect(dbState.outbox.get(second.operationId)!.device_id).toBe(
        firstDevice,
      );
      expect(await meta.get(OP_SEQ_META_KEY)).toBe('3');
    });

    it('parallele Saves: eindeutige Operation-IDs, kein PK-Verstoß (D4)', async () => {
      const [r1, r2] = await Promise.all([
        repo.save(SAMPLE_INPUT, FIXED_NOW),
        repo.save(SAMPLE_INPUT, FIXED_NOW),
      ]);
      expect(r1.operationId).not.toBe(r2.operationId);
      expect([r1.operationId, r2.operationId].sort()).toEqual([
        'op-2026-1101-001',
        'op-2026-1101-002',
      ]);
      expect(await meta.get(OP_SEQ_META_KEY)).toBe('3');
      expect(dbState.bookings.size).toBe(2);
    });
  });

  describe('(b) wiederholte Speicherversuche', () => {
    it('wiederholter Save erzeugt keine Duplikate: neue Buchung, neue Operation-ID, keine Kollision', async () => {
      const first = await repo.save(SAMPLE_INPUT, FIXED_NOW);
      const second = await repo.save(
        SAMPLE_INPUT,
        new Date(2026, 10, 1, 9, 30, 5),
      );

      expect(second.bookingId).not.toBe(first.bookingId);
      expect(second.operationId).not.toBe(first.operationId);
      expect(dbState.bookings.size).toBe(2);
      expect([...dbState.outbox.keys()]).toEqual(
        [first.operationId, second.operationId].sort(),
      );
      // Die Doppeltipp-Sperre (UI) ist die primäre Duplikatsvermeidung
      // (Batch D); auf Repository-Ebene bleibt jede Anlage eindeutig.
    });
  });

  describe('(c) Verwerfen', () => {
    it('discard: queued Buchung + Splits + Outbox-Zeile sind in einer Transaktion weg, Zähler bleibt', async () => {
      const result = await repo.save(SAMPLE_INPUT, FIXED_NOW);
      const outboxSizeBefore = dbState.outbox.size;

      await repo.discard(result.bookingId);

      expect(dbState.bookings.size).toBe(0);
      expect(dbState.splits.size).toBe(0);
      expect(dbState.outbox.size).toBe(0);
      // Der Zähler wird NICHT zurückgesetzt (Operation-IDs eindeutig).
      expect(await meta.get(OP_SEQ_META_KEY)).toBe('2');
      expect(outboxSizeBefore).toBe(1);
    });
  });

  describe('(a) Neustart-Persistenz', () => {
    it('20 Ausgaben inkl. Splits überleben einen App-Neustart; Zähler bleibt monoton', async () => {
      for (let i = 1; i <= 20; i += 1) {
        await repo.save(
          {
            date: `2026-11-${String(i).padStart(2, '0')}`,
            totalAmountCents: 1000 * i,
            accountId: 'acc-1',
            note: null,
            splits: [
              {
                id: 'form-a',
                amountCents: 1000 * i,
                poolId: 'pool-1',
                categoryId: 'cat-1',
                note: null,
                tax: null,
              },
            ],
          },
          new Date(2026, 10, i, 9, 0, 0),
        );
      }
      expect(dbState.bookings.size).toBe(20);
      expect(dbState.splits.size).toBe(20);
      expect(dbState.outbox.size).toBe(20);
      expect(
        [...dbState.outbox.values()].every((r) => r.status === 'queued'),
      ).toBe(true);

      // App-Neustart: Prozessregister leer, DB-Datei bleibt erhalten.
      dbState.simulateRestart();
      const env2 = createEnvironmentInjector(
        [LocalDatabase, MetaService, BookingsRepository],
        TestBed.inject(EnvironmentInjector),
      );
      const db2 = env2.get(LocalDatabase);
      const repo2 = env2.get(BookingsRepository);
      await db2.init();

      const list = await repo2.list();
      expect(list).toHaveLength(20);
      expect(dbState.bookings.size).toBe(20);
      expect(dbState.splits.size).toBe(20);
      expect(dbState.outbox.size).toBe(20);

      // Persistierter Zähler: die nächste Operation ist seq 21.
      const next = await repo2.save(SAMPLE_INPUT, new Date(2026, 10, 21, 9, 0, 0));
      expect(next.operationId).toBe('op-2026-1121-021');
      expect(await env2.get(MetaService).get(OP_SEQ_META_KEY)).toBe('22');
    });
  });

  describe('(e) fehlendes Plugin / fehlgeschlagener executeSet', () => {
    it('executeSet schlägt bei der Split-Insert fehl: kein Teilzustand (All-or-Nothing, D7)', async () => {
      // Batch-Zusammensetzung: [0] Buchung, [1] Split 1, [2] Split 2,
      // [3] Outbox, [4] Zähler → Fehler bei Statement 2.
      dbState.failOnStatementIndex = 2;

      await expect(repo.save(SAMPLE_INPUT, FIXED_NOW)).rejects.toThrow(
        'simulated failure',
      );

      expect(dbState.bookings.size).toBe(0);
      expect(dbState.splits.size).toBe(0);
      expect(dbState.outbox.size).toBe(0);
      expect(await meta.get(OP_SEQ_META_KEY)).toBeNull();
    });

    it('executeSet schlägt bei der Outbox-Insert fehl: kein Teilzustand, Zähler bleibt 0', async () => {
      // Fehler bei Statement 3 (Outbox-Insert), nachdem Buchung + Splits
      // angewendet WAREN — das All-or-Nothing-Rollback des Plugins
      // macht sie rückgängig.
      dbState.failOnStatementIndex = 3;

      await expect(repo.save(SAMPLE_INPUT, FIXED_NOW)).rejects.toThrow(
        'simulated failure',
      );

      expect(dbState.bookings.size).toBe(0);
      expect(dbState.splits.size).toBe(0);
      expect(dbState.outbox.size).toBe(0);
      expect(await meta.get(OP_SEQ_META_KEY)).toBeNull();
    });

    it('fehlendes Plugin (createConnection schlägt fehl): init und save werfen, nichts wird geschrieben', async () => {
      dbState.failOnCreateConnection = true;
      // „Neuer Prozess“: das Verbindungsregister ist leer (die in
      // beforeEach initialisierte Connection existiert nicht mehr).
      dbState.simulateRestart();

      const env2 = createEnvironmentInjector(
        [LocalDatabase, MetaService, BookingsRepository],
        TestBed.inject(EnvironmentInjector),
      );
      const db2 = env2.get(LocalDatabase);
      const repo2 = env2.get(BookingsRepository);

      await expect(db2.init()).rejects.toThrow(
        'simulated connection failure',
      );
      // save sichert getOrCreateDeviceId → withWriteTransaction →
      // init() — der createConnection-Retry wirft denselben Fehler
      // (nichts wird geschrieben, keine Teilstate).
      await expect(repo2.save(SAMPLE_INPUT, FIXED_NOW)).rejects.toThrow(
        'simulated connection failure',
      );

      // Kein Teilzustand (direkt am Fake-State — ohne Connection sind
      // keine Queries möglich).
      expect(dbState.bookings.size).toBe(0);
      expect(dbState.outbox.size).toBe(0);
      expect(dbState.meta.has(OP_SEQ_META_KEY)).toBe(false);
      expect(dbState.meta.has(DEVICE_ID_META_KEY)).toBe(false);
    });
  });

  describe('(i) save verifiziert die Eingabe', () => {
    it('Splitsummendefizit: BookingValidationError, nichts wird geschrieben', async () => {
      const bad: LocalExpenseInput = {
        ...SAMPLE_INPUT,
        totalAmountCents: 4600, // Splits liefern nur 4500
      };

      await expect(repo.save(bad, FIXED_NOW)).rejects.toThrow(
        BookingValidationError,
      );

      expect(dbState.bookings.size).toBe(0);
      expect(dbState.splits.size).toBe(0);
      expect(dbState.outbox.size).toBe(0);
      expect(await meta.get(OP_SEQ_META_KEY)).toBeNull();
    });
  });

  describe('(k) Zustandsfehler', () => {
    it('update auf fehlender Buchung: BookingStateError (not_found)', async () => {
      await expect(
        repo.update('bk-missing', SAMPLE_INPUT, FIXED_NOW),
      ).rejects.toThrow(BookingStateError);
    });

    it('discard auf fehlender Buchung: BookingStateError (not_found)', async () => {
      await expect(repo.discard('bk-missing')).rejects.toThrow(
        BookingStateError,
      );
    });

    it('update/discard auf confirmed Buchung: BookingStateError (not_queued), Daten intakt', async () => {
      const result = await repo.save(SAMPLE_INPUT, FIXED_NOW);
      // Status direkt im (Fake-)State auf 'confirmed' setzen — ohne
      // OutboxService; testet das reale not_queued-Verhalten.
      const confirmedRow = dbState.outbox.get(result.operationId)!;
      confirmedRow.status = 'confirmed';
      const bookingBefore = { ...dbState.bookings.get(result.bookingId)! };

      await expect(
        repo.update(result.bookingId, SAMPLE_INPUT, FIXED_NOW),
      ).rejects.toThrow(BookingStateError);
      await expect(repo.discard(result.bookingId)).rejects.toThrow(
        BookingStateError,
      );

      expect(dbState.bookings.get(result.bookingId)).toEqual(bookingBefore);
      expect(dbState.outbox.size).toBe(1);
    });
  });

  describe('(l) Steuerkennzeichnung: Payload und DB (P1-Nachgang)', () => {
    it('save mit !relevant-Kennzeichnung: markedAmount null in Payload UND DB-Spalte (TaxMarking.ValidateSelf)', async () => {
      const input: LocalExpenseInput = {
        ...SAMPLE_INPUT,
        totalAmountCents: 2500,
        splits: [
          {
            ...SAMPLE_INPUT.splits[0]!,
            tax: {
              relevant: false,
              taxCategoryId: null,
              taxCategoryName: null,
              markedAmountCents: null,
              note: null,
            },
          },
        ],
      };
      const result = await repo.save(input, FIXED_NOW);

      // Host-Vertrag: !relevant → kein Teilbetrag (sonst wirft der Host
      // die Operation im Sync ab).
      const parsed = JSON.parse(
        dbState.outbox.get(result.operationId)!.payload,
      ) as {
        splits: {
          tax: {
            relevant: boolean;
            taxCategoryId: string | null;
            markedAmount: number | null;
            note: string | null;
          };
        }[];
      };
      expect(parsed.splits[0]!.tax).toEqual({
        relevant: false,
        taxCategoryId: null,
        markedAmount: null,
        note: null,
      });

      // Lokale DB: dieselbe Regel (Anzeigekonsistenz, Batch E).
      const splitRow = [...dbState.splits.values()].find(
        (s) => s.booking_id === result.bookingId,
      )!;
      expect(splitRow.tax_relevant).toBe(0);
      expect(splitRow.marked_amount_cents).toBeNull();
    });

    it('save mit relevant-Kennzeichnung und markedAmountCents null: Normalisierung auf Splitbetrag (Payload UND DB)', async () => {
      const input: LocalExpenseInput = {
        ...SAMPLE_INPUT,
        totalAmountCents: 2000,
        splits: [
          {
            ...SAMPLE_INPUT.splits[1]!,
            tax: {
              relevant: true,
              taxCategoryId: 'tax-1',
              taxCategoryName: 'Steuer A',
              markedAmountCents: null,
              note: null,
            },
          },
        ],
      };
      const result = await repo.save(input, FIXED_NOW);

      const parsed = JSON.parse(
        dbState.outbox.get(result.operationId)!.payload,
      ) as { splits: { tax: { markedAmount: number | null } }[] };
      expect(parsed.splits[0]!.tax.markedAmount).toBe(2000);

      const splitRow = [...dbState.splits.values()].find(
        (s) => s.booking_id === result.bookingId,
      )!;
      expect(splitRow.marked_amount_cents).toBe(2000);
    });
  });

  describe('(m) Status-Check innerhalb der Schreibtransaktion (P2-Reviewbefund)', () => {
    function statusSelectsSince(index: number) {
      return dbState.queryLog.slice(index).filter(
        (entry) =>
          entry.statement.startsWith('SELECT') &&
          (entry.statement.includes('FROM bookings') ||
            entry.statement.includes('FROM outbox')),
      );
    }

    it('update: Status-Lesungen laufen in offener Schreibtransaktion (manualTxActive)', async () => {
      const saved = await repo.save(SAMPLE_INPUT, FIXED_NOW);
      const before = dbState.queryLog.length;

      await repo.update(
        saved.bookingId,
        { ...SAMPLE_INPUT, note: 'Geändert' },
        FIXED_NOW,
      );

      const selects = statusSelectsSince(before);
      expect(selects.length).toBeGreaterThanOrEqual(2);
      for (const entry of selects) {
        expect(entry.manualTxActive).toBe(true);
      }
    });

    it('discard: Status-Lesungen laufen in offener Schreibtransaktion (manualTxActive)', async () => {
      const saved = await repo.save(SAMPLE_INPUT, FIXED_NOW);
      const before = dbState.queryLog.length;

      await repo.discard(saved.bookingId);

      const selects = statusSelectsSince(before);
      expect(selects.length).toBeGreaterThanOrEqual(2);
      for (const entry of selects) {
        expect(entry.manualTxActive).toBe(true);
      }
    });
  });
});
