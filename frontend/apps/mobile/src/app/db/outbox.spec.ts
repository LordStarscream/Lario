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
import { BookingsRepository } from './bookings.repository';
import { DEVICE_ID_META_KEY, OutboxService } from './outbox';
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
    } satisfies CapacitorSqliteMock,
  };
});

/** Feste Uhr: 2026-11-01 09:30:00 (lokal). */
const FIXED_NOW = new Date(2026, 10, 1, 9, 30, 0);

const UUID_RE =
  /^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[0-9a-f]{4}-[0-9a-f]{12}$/;

/** Beispiel-Ausgabe (45,00 EUR, zwei Splits). */
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
      tax: null,
    },
  ],
};

/**
 * Outbox-Dienst (Schritt 8, Batch B — Design D4/D5).
 *
 * Das Anlegen (Buchung + Splits + Outbox-Zeile + Zähler in EINEM
 * atomaren Batch) liegt seit Design D7 in `BookingsRepository.save` —
 * siehe `bookings.repository.spec.ts`. Diese Spec deckt die übrige
 * OutboxService-Oberfläche ab:
 *
 * - `getOrCreateDeviceId`: stabile `device.id` (einmalig, UUIDv4).
 * - (d) modify/confirm/delete nur für `queued`; `confirmed`
 *   unveränderlich, Bestätigung/Löschen idempotent.
 * - list/count/get.
 *
 * Setup-Einträge entstehen über das Repository (einziger Anlegepfad).
 */
describe('OutboxService (Batch B, Design D4/D5)', () => {
  let db: LocalDatabase;
  let meta: MetaService;
  let outbox: OutboxService;
  let repo: BookingsRepository;

  beforeEach(async () => {
    dbState.reset();
    // Eigener Environment-Injector pro Test => frische Singletons
    // (LocalDatabase, MetaService, OutboxService, BookingsRepository).
    const env = createEnvironmentInjector(
      [LocalDatabase, MetaService, OutboxService, BookingsRepository],
      TestBed.inject(EnvironmentInjector),
    );
    db = env.get(LocalDatabase);
    meta = env.get(MetaService);
    outbox = env.get(OutboxService);
    repo = env.get(BookingsRepository);
    outbox.setClock(() => FIXED_NOW);
    // App-Start: Schema-Initialisierung.
    await db.init();
  });

  /** Beispiel-Ausgabe über das Repository anlegen → operationId. */
  async function createSample(now: Date = FIXED_NOW): Promise<string> {
    const result = await repo.save(SAMPLE_INPUT, now);
    return result.operationId;
  }

  describe('getOrCreateDeviceId', () => {
    it('legt device.id einmalig an (UUIDv4), persistiert sie, stabil im Wiederholungsaufruf', async () => {
      const first = await outbox.getOrCreateDeviceId();
      expect(first).toMatch(UUID_RE);
      expect(await meta.get(DEVICE_ID_META_KEY)).toBe(first);
      // Zweiter Aufruf: dieselbe ID (stabil über Neustarts).
      expect(await outbox.getOrCreateDeviceId()).toBe(first);
    });

    it('stellt eine bestehende device.id wieder her (keine neue UUID)', async () => {
      const fixed = 'a0a0a0a0-a0a0-a0a0-a0a0-a0a0a0a0a0a0';
      await meta.set(DEVICE_ID_META_KEY, fixed);
      expect(await outbox.getOrCreateDeviceId()).toBe(fixed);
    });
  });

  describe('(d) modify/confirm/delete', () => {
    it('modify: queued-Eintrag — Payload/Hash/updated_at ersetzt, gleiche operation_id', async () => {
      const opId = await createSample();
      const row = dbState.outbox.get(opId);
      const bookingId = row!.booking_id;
      const oldPayload = row!.payload;
      const oldCreatedAt = row!.created_at_utc;

      const changed: LocalExpenseInput = { ...SAMPLE_INPUT, note: 'korrigiert' };
      const newPayload = buildExpensePayload(bookingId, changed);
      outbox.setClock(() => new Date(2026, 10, 1, 10, 0, 0));
      await outbox.modifyOutbox(opId, newPayload);

      const after = dbState.outbox.get(opId);
      expect(after!.payload).toBe(newPayload);
      expect(after!.payload).not.toBe(oldPayload);
      expect(after!.payload_hash).toBe(payloadHash(newPayload));
      expect(after!.status).toBe('queued');
      expect(after!.created_at_utc).toBe(oldCreatedAt);
      expect(after!.updated_at_utc).toBe(
        new Date(2026, 10, 1, 10, 0, 0).toISOString(),
      );
    });

    it('modify: confirmed-Eintrag wird abgelehnt (unveränderlich)', async () => {
      const opId = await createSample();
      await outbox.confirmOutbox(opId);
      const before = dbState.outbox.get(opId)!.payload;

      await expect(outbox.modifyOutbox(opId, '{}')).rejects.toThrow(
        'nur queued',
      );
      expect(dbState.outbox.get(opId)!.payload).toBe(before);
    });

    it('modify: nicht vorhandene Operation wird abgelehnt', async () => {
      await expect(
        outbox.modifyOutbox('op-2026-1101-999', '{}'),
      ).rejects.toThrow('nicht gefunden');
    });

    it('confirm: queued → confirmed, updated_at wird gesetzt', async () => {
      const opId = await createSample();
      outbox.setClock(() => new Date(2026, 10, 1, 12, 0, 0));
      await outbox.confirmOutbox(opId);

      const row = dbState.outbox.get(opId);
      expect(row!.status).toBe('confirmed');
      expect(row!.updated_at_utc).toBe(
        new Date(2026, 10, 1, 12, 0, 0).toISOString(),
      );
    });

    it('confirm: idempotent — zweiter Aufruf und fehlende Operation sind No-Ops', async () => {
      const opId = await createSample();
      await outbox.confirmOutbox(opId);
      const confirmedTs = dbState.outbox.get(opId)!.updated_at_utc;

      // Zweite Bestätigung: keine Änderung, kein Fehler.
      outbox.setClock(() => new Date(2026, 10, 5, 12, 0, 0));
      await outbox.confirmOutbox(opId);
      expect(dbState.outbox.get(opId)!.updated_at_utc).toBe(confirmedTs);
      // Fehlende Operation: No-Op.
      await outbox.confirmOutbox('op-2026-1101-999');
    });

    it('delete: queued-Eintrag wird entfernt; idempotent bei fehlender Operation', async () => {
      const opId = await createSample();
      await outbox.deleteOutbox(opId);
      expect(dbState.outbox.size).toBe(0);
      // Zweites Löschen: No-Op.
      await outbox.deleteOutbox(opId);
      expect(dbState.outbox.size).toBe(0);
    });

    it('delete: confirmed-Eintrag wird abgelehnt', async () => {
      const opId = await createSample();
      await outbox.confirmOutbox(opId);

      await expect(outbox.deleteOutbox(opId)).rejects.toThrow('nur queued');
      expect(dbState.outbox.size).toBe(1);
    });

    it('list/count: neueste zuerst, Zählung nach Status', async () => {
      const op1 = await createSample(new Date(2026, 10, 1, 9, 30, 0));
      const op2 = await createSample(new Date(2026, 10, 1, 9, 31, 0));
      await outbox.confirmOutbox(op1);

      const list = await outbox.listOutbox();
      expect(list.map((e) => e.operationId)).toEqual([op2, op1]);
      expect(await outbox.countOutbox()).toEqual({
        queued: 1,
        confirmed: 1,
        total: 2,
      });
    });

    it('getOutboxEntry: Zeile nach operation_id; null, wenn nicht vorhanden', async () => {
      const opId = await createSample();
      const entry = await outbox.getOutboxEntry(opId);
      expect(entry?.operationId).toBe(opId);
      expect(entry?.status).toBe('queued');
      expect(await outbox.getOutboxEntry('op-2026-1101-999')).toBeNull();
    });
  });
});
