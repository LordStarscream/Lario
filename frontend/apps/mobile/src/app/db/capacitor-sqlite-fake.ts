import type { SqlSet } from './statements';
import {
  DELETE_BOOKING,
  DELETE_DRAFT,
  DELETE_OUTBOX,
  DELETE_SNAPSHOT_ACCOUNTS,
  DELETE_SNAPSHOT_CATEGORIES,
  DELETE_SNAPSHOT_POOLS,
  DELETE_SNAPSHOT_TAX_CATEGORIES,
  DELETE_SPLITS,
  INSERT_BOOKING,
  INSERT_OUTBOX,
  INSERT_SNAPSHOT_ACCOUNT,
  INSERT_SNAPSHOT_CATEGORY,
  INSERT_SNAPSHOT_POOL,
  INSERT_SNAPSHOT_TAX_CATEGORY,
  INSERT_SPLIT,
  SELECT_ACCOUNT_STATUSES,
  SELECT_BOOKING_BY_ID,
  SELECT_CATEGORY_STATUSES,
  SELECT_POOL_STATUSES,
  SELECT_SNAPSHOT_ACCOUNTS,
  SELECT_SNAPSHOT_CATEGORIES,
  SELECT_SNAPSHOT_POOLS,
  SELECT_SNAPSHOT_TAX_CATEGORIES,
  SELECT_TAX_CATEGORY_STATUSES,
  SELECT_BOOKING_LIST,
  SELECT_DRAFTS,
  SELECT_DRAFT_BY_ID,
  SELECT_OUTBOX_BY_BOOKING,
  SELECT_SPLITS_BY_BOOKING,
  UPSERT_DRAFT,
  UPDATE_BOOKING,
  UPDATE_OUTBOX_PAYLOAD,
} from './statements';

/**
 * Teilbare In-Memory-Fake der Plugin-Grenze `@capacitor-community/sqlite`
 * (8.1.1) für alle Specs unter `app/db/` (Schritt 8, Batch A/B).
 *
 * Der State liegt in `vi.hoisted`: Die `vi.mock`-Hoisting-Regel erlaubt
 * nur `vi.hoisted`-Variablen im Mock-Factory-Closure. Die Factory wird
 * daher zentral hier definiert und von den Specs als
 * `vi.mock('@capacitor-community/sqlite', capacitorSqliteMockFactory)`
 * referenziert — so bleibt der Hoisting-Vertrag in jeder Spec gültig.
 *
 * Modelliert werden die relevanten Plugin-Verträge:
 *
 * - Verbindungsregister (`dbDict`) pro Prozess mit den Schlüsseln
 *   `RW_<Name>` / `RO_<Name>`; ein zweiter `createConnection()`-Aufruf
 *   für dieselbe Connection wirft „already exists“.
 * - `open()` öffnet nur eine zuvor registrierte Connection, sonst
 *   „No available connection for database …“. Contract Android 8.1.1:
 *   `open()` aktiviert FK-Constraints (`Database.java`:
 *   `setForeignKeyConstraintsEnabled(true)`) — der Fake spiegelt das
 *   im State (`foreignKeys`).
 * - `executeSet()` läuft auf der RW_-Connection. Contract Android
 *   8.1.1 (Review-Befund P1): `values` ist bei JEDEM Element
 *   Pflichtfeld — ohne Array wirft der Fake wie das echte Plugin.
 *   Standardmäßig ist der Batch ALLES-ODER-NICHTS (transaction: true):
 *   Snapshot, dann alle Sets anwenden; bei einem Fehler Snapshot
 *   wiederherstellen und werfen (kein Teilzustand). Mit
 *   `transaction: false` läuft jedes Statement sofort — darunter auch
 *   `PRAGMA foreign_keys = ON`, das (wie bei echtem SQLite) nur
 *   außerhalb einer Transaktion wirksam ist.
 * - `query()`/`run()` werfen ohne das Pflichtfeld `values` (auch wenn
 *   es ein leeres Array wäre), wie das echte Plugin auf Android.
 * - `run()`/`executeSet()` modellieren die `transaction`-Option des
 *   Vertrags 8.1.1 (Default TRUE — verifiziert an der Android-Quelle
 *   v8.1.1, `CapacitorSQLitePlugin.java`: `call.getBoolean(
 *   "transaction", true)`, `Database.java` `execute()`/`executeSet()`):
 *   Das Plugin startet EIGENS eine Transaktion vor den Statements
 *   (`beginTransaction()`) und committet nachher (`commitTransaction()`);
 *   bei Fehler rollt es in `finally` zurück (`rollbackTransaction()`
 *   bei `_db.inTransaction()`). Der Fake spiegelt das per Snapshot
 *   (All-oder-Nichts). Steuer-Statements per `run()` OHNE
 *   `transaction: false` fehlschlagen daher (Review-Befund P1,
 *   3. Runde): `BEGIN …` verschachtelt (SQLite: „cannot start a
 *   transaction within a transaction“); `COMMIT`/`ROLLBACK` beenden
 *   die Plugin-Transaktion, worauf die Plugin-eigene
 *   `commitTransaction()`-Prüfung „No transaction active“ wirft.
 *   Mit `transaction: false` läuft das Statement direkt; `BEGIN`/
 *   `COMMIT`/`ROLLBACK` steuern die manuelle Transaktion. `executeSet`
 *   mit `transaction: false` wendet die Statements sofort an — bei
 *   Fehler kann ein Teilzustand in der offenen (manuellen)
 *   Transaktion bleiben (Plugin-Contract, `executeSet` ohne
 *   begin/rollback); `LocalDatabase` rollt explizit zurück.
 * - Native Transaktions-Methoden `beginTransaction()`/
 *   `commitTransaction()`/`rollbackTransaction()`/`isTransactionActive()`
 *   (Contract 8.1.1, verifiziert an `Database.java` v8.1.1 —
 *   `beginTransaction` ist eine deferred Write-Transaktion):
 *   `beginTransaction` mit offener Transaktion wirft „Already in
 *   transaction“; `commitTransaction` ohne Transaktion wirft „No
 *   transaction active“; `rollbackTransaction` ohne Transaktion ist
 *   ein no-op. `LocalDatabase.withWriteTransaction` nutzt diese
 *   (Review-Befund P1 — keine Steuer-Statements per `run()`).
 *
 * Tabellenspeicher (Batch B, Design D2/D4): `meta`, `bookings`,
 * `splits`, `outbox`, `drafts` plus eine Liste der erfolgreich
 * ausgeführten executeSet-Batches (für Assertions).
 *
 * Constraint-Semantik (Design D2a, `foreignKeys` = true):
 * - PRIMARY KEY/UNIQUE: INSERT mit bestehendem Key wirft
 *   `SQLITE_CONSTRAINT_PRIMARYKEY`.
 * - FOREIGN KEY: INSERT_SPLIT/INSERT_OUTBOX mit fehlender
 *   Referenzbuchung wirft. DELETE_BOOKING cascade-t immer: Splits
 *   und Outbox-Zeilen fallen mit der Buchung (Option A, Review B10).
 * - NOT NULL: NULL-Wert in einer NOT NULL-Spalte wirft.
 * - UPDATE/DELETE ohne Treffer ist ein no-op (0 Zeilen), wie SQLite.
 *
 * Batch-B-Statements werden per normalisiertem Vollstring erkannt
 * (Whitespace kollabiert) — kein Substring-Matching, damit
 * zusammengehörige Statements nicht verwechselt werden.
 */

/** Buchung (spiegelt die Batch-A-Spalten 1:1, snake_case). */
export interface FakeBookingRow {
  id: string;
  date: string;
  type: string;
  account_id: string;
  total_amount_cents: number;
  note: string | null;
  base_revision: number | null;
  created_at_utc: string;
}

/** Split (spiegelt die Batch-A-Spalten 1:1, snake_case). */
export interface FakeSplitRow {
  id: string;
  booking_id: string;
  amount_cents: number;
  pool_id: string;
  category_id: string;
  note: string | null;
  /** 1/0 = Kennzeichnung relevant/nicht relevant; null = keine Kennzeichnung. */
  tax_relevant: number | null;
  tax_category_id: string | null;
  tax_category_name: string | null;
  marked_amount_cents: number | null;
  tax_note: string | null;
}

/** Konto (spiegelt die Batch-A-Spalten 1:1, snake_case; Batch C). */
export interface FakeAccountRow {
  id: string;
  name: string;
  type: string;
  is_interest_bearing: number;
  status: string;
  base_revision: number;
}

/** Pool (spiegelt die Batch-A-Spalten 1:1, snake_case; Batch C). */
export interface FakePoolRow {
  id: string;
  name: string;
  status: string;
  base_revision: number;
}

/** Kategorie (spiegelt die Batch-A-Spalten 1:1, snake_case; Batch C). */
export interface FakeCategoryRow {
  id: string;
  name: string;
  parent_id: string | null;
  status: string;
  base_revision: number;
  tax_preset_json: string | null;
}

/** Steuerkategorie (spiegelt die Batch-A-Spalten 1:1, snake_case; Batch C). */
export interface FakeTaxCategoryRow {
  id: string;
  name: string;
  parent_id: string | null;
  status: string;
  base_revision: number;
}

/** Outbox-Zeile (Design D4) — spiegelt die Batch-A-Spalten 1:1. */
export interface FakeOutboxRow {
  operation_id: string;
  device_id: string;
  type: string;
  booking_id: string;
  payload: string;
  payload_hash: string;
  expected_base_revision: number | null;
  status: string;
  created_at_utc: string;
  updated_at_utc: string;
}

/**
 * Tabellensnapshot für manuelle Transaktionen (BEGIN/ROLLBACK, Review-
 * Befund P2, Batch B) und die All-or-Nothing-Garantie von executeSet.
 */
export interface FakeTableSnapshot {
  meta: Map<string, string>;
  accounts: Map<string, FakeAccountRow>;
  pools: Map<string, FakePoolRow>;
  categories: Map<string, FakeCategoryRow>;
  taxCategories: Map<string, FakeTaxCategoryRow>;
  bookings: Map<string, FakeBookingRow>;
  splits: Map<string, FakeSplitRow>;
  outbox: Map<string, FakeOutboxRow>;
  drafts: Map<string, FakeDraftRow>;
  schemaStatementsExecuted: boolean;
}

/** Entwurf (spiegelt die Batch-A-Spalten 1:1, snake_case). */
export interface FakeDraftRow {
  id: string;
  name: string;
  data: string;
  created_at_utc: string;
  updated_at_utc: string;
}

/** Erfolgreich ausgeführter executeSet-Batch (für Assertions in den Specs). */
export interface FakeExecuteSetBatch {
  database: string;
  readonly?: boolean;
  /** true = transaktional (Plugin-Default), false = transaction: false. */
  transaction: boolean;
  sets: SqlSet[];
}

/** Gemeinsamer Fake-State (über `vi.hoisted` auch im Mock verfügbar). */
export interface CapacitorSqliteFakeState {
  /** Prozessweites Verbindungsregister des Plugins (dbDict). */
  connections: Map<string, { opened: boolean }>;
  /** Batch-A-Tabellenspeicher: Meta-Keys/Werte. */
  meta: Map<string, string>;
  /** Batch-C-Tabellenspeicher: Stammdaten-Snapshot, Key = id. */
  accounts: Map<string, FakeAccountRow>;
  /** Batch-C-Tabellenspeicher: Stammdaten-Snapshot, Key = id. */
  pools: Map<string, FakePoolRow>;
  /** Batch-C-Tabellenspeicher: Stammdaten-Snapshot, Key = id. */
  categories: Map<string, FakeCategoryRow>;
  /** Batch-C-Tabellenspeicher: Stammdaten-Snapshot, Key = id. */
  taxCategories: Map<string, FakeTaxCategoryRow>;
  /** Batch-B-Tabellenspeicher: Buchungen, Key = id. */
  bookings: Map<string, FakeBookingRow>;
  /** Batch-B-Tabellenspeicher: Splits, Key = id. */
  splits: Map<string, FakeSplitRow>;
  /** Batch-B-Tabellenspeicher: Outbox-Zeilen, Key = operation_id. */
  outbox: Map<string, FakeOutboxRow>;
  /** Batch-B-Tabellenspeicher: Entwürfe, Key = id. */
  drafts: Map<string, FakeDraftRow>;
  /** Alle erfolgreich ausgeführten executeSet-Batches, in Reihenfolge. */
  executedSets: FakeExecuteSetBatch[];
  /**
   * Query-Ausführungstage (Review-Befund P2, Runde 4): Jede Query wird
   * in Reihenfolge mit dem Transaktionszustand zum Ausführungszeitpunkt
   * (`manualTxActive`) aufgezeichnet. Tests beweisen damit, dass
   * Status-Lesungen innerhalb einer offenen Schreibtransaktion laufen.
   * `reset()` leert das Log.
   */
  queryLog: { statement: string; manualTxActive: boolean }[];
  /** true, nachdem mindestens ein CREATE TABLE … IF NOT EXISTS lief. */
  schemaStatementsExecuted: boolean;
  /**
   * Wirksamer FK-Zustand der geöffneten Connection. Contract Android
   * 8.1.1: `open()` setzt ihn auf true. `PRAGMA foreign_keys = ON`
   * (nur außerhalb einer Transaktion) ebenso — siehe applySet().
   */
  foreignKeys: boolean;
  /**
   * Test-Option (Batch B, Abnahme (e)): executeSet wirft beim Set mit
   * diesem Index `Error('simulated failure')`. null = aus. `reset()`
   * setzt die Option zurück.
   */
  failOnStatementIndex: number | null;
  /**
   * Test-Option (Batch B, Abnahme (e)): createConnection wirft
   * `Error('simulated connection failure')` — simuliert ein fehlendes
   * oder fehlgeschlagenes Plugin. false = aus. `reset()` setzt die
   * Option zurück.
   */
  failOnCreateConnection: boolean;
  /**
   * Simuliert einen App-Neustart: der Prozess (und damit das
   * Verbindungsregister) startet leer, die SQLite-Datei (Daten in
   * meta/bookings/splits/outbox/drafts) bleibt erhalten.
   */
  simulateRestart(): void;
  /** Leert Verbindungsregister, alle Tabellen und die Batch-Historie. */
  reset(): void;
  /**
   * Manuelle Schreibtransaktion (run: BEGIN/COMMIT/ROLLBACK, Review-
   * Befund P2, Batch B): true, solange ein BEGIN ohne korrespondierendes
   * COMMIT/ROLLBACK in Fluge ist.
   */
  manualTxActive: boolean;
  /**
   * Tabellensnapshot zum BEGIN (für ROLLBACK bzw. Prozess-Abbruch in
   * simulateRestart). null = keine offene manuelle Transaktion.
   */
  manualTxSnapshot: FakeTableSnapshot | null;
}

export const dbState: CapacitorSqliteFakeState = vi.hoisted(() => {
  const state = {
    connections: new Map<string, { opened: boolean }>(),
    meta: new Map<string, string>(),
    accounts: new Map<string, FakeAccountRow>(),
    pools: new Map<string, FakePoolRow>(),
    categories: new Map<string, FakeCategoryRow>(),
    taxCategories: new Map<string, FakeTaxCategoryRow>(),
    bookings: new Map<string, FakeBookingRow>(),
    splits: new Map<string, FakeSplitRow>(),
    outbox: new Map<string, FakeOutboxRow>(),
    drafts: new Map<string, FakeDraftRow>(),
    // Nur Typ-Referenzen (werden zur Laufzeit eliminiert) — kein
    // Hoisting-Problem mit vi.hoisted.
    executedSets: [] as FakeExecuteSetBatch[],
    queryLog: [] as { statement: string; manualTxActive: boolean }[],
    schemaStatementsExecuted: false,
    foreignKeys: false,
    failOnStatementIndex: null as number | null,
    failOnCreateConnection: false,
    manualTxActive: false,
    manualTxSnapshot: null as FakeTableSnapshot | null,
    simulateRestart() {
      // Prozess-Abbruch: eine uncommittede manuelle Transaktion wird
      // zurückgerollt (SQLite rollt uncommittede Transaktionen beim
      // Prozessende zurück).
      if (state.manualTxActive && state.manualTxSnapshot) {
        restoreSnapshot(state, state.manualTxSnapshot);
      }
      state.manualTxActive = false;
      state.manualTxSnapshot = null;
      state.connections.clear();
    },
    reset() {
      state.connections.clear();
      state.meta.clear();
      state.accounts.clear();
      state.pools.clear();
      state.categories.clear();
      state.taxCategories.clear();
      state.bookings.clear();
      state.splits.clear();
      state.outbox.clear();
      state.drafts.clear();
      state.executedSets.length = 0;
      state.queryLog.length = 0;
      state.schemaStatementsExecuted = false;
      state.foreignKeys = false;
      state.failOnStatementIndex = null;
      state.failOnCreateConnection = false;
      state.manualTxActive = false;
      state.manualTxSnapshot = null;
    },
  };
  return state;
});

/** Whitespace normalisieren (Vollstring-Abgleich statt Substring). */
function normalize(statement: string): string {
  return statement.replace(/\s+/g, ' ').trim();
}

/** Normalisierte Meta-Statements (entsprechen den statements.ts-Hilfen). */
const META_STATEMENTS = {
  upsert:
    'INSERT INTO meta (key, value) VALUES (?, ?) ON CONFLICT(key) DO UPDATE SET value = excluded.value',
  delete: 'DELETE FROM meta WHERE key = ?',
  /** Parameterlos: löscht ALLE Meta-Zeilen (SQLite-Semantik). */
  deleteAll: 'DELETE FROM meta',
  select: 'SELECT value FROM meta WHERE key = ?',
} as const;

type StateTables = Pick<
  CapacitorSqliteFakeState,
  | 'meta'
  | 'accounts'
  | 'pools'
  | 'categories'
  | 'taxCategories'
  | 'bookings'
  | 'splits'
  | 'outbox'
  | 'drafts'
  | 'schemaStatementsExecuted'
  | 'foreignKeys'
>;

function constraintError(table: string, column: string, value: string): Error {
  return new Error(
    `SQLITE_CONSTRAINT_PRIMARYKEY: ${table}.${column} ${value} bereits vorhanden`,
  );
}

/**
 * Tabellensnapshot erstellen (All-or-Nothing-Garantie von executeSet
 * und ROLLBACK/Prozess-Abbruch von manuellen Transaktionen).
 */
function takeSnapshot(state: StateTables): FakeTableSnapshot {
  return {
    meta: new Map(state.meta),
    accounts: new Map([...state.accounts].map(([key, row]) => [key, { ...row }])),
    pools: new Map([...state.pools].map(([key, row]) => [key, { ...row }])),
    categories: new Map([...state.categories].map(([key, row]) => [key, { ...row }])),
    taxCategories: new Map(
      [...state.taxCategories].map(([key, row]) => [key, { ...row }]),
    ),
    bookings: new Map([...state.bookings].map(([key, row]) => [key, { ...row }])),
    splits: new Map([...state.splits].map(([key, row]) => [key, { ...row }])),
    outbox: new Map([...state.outbox].map(([key, row]) => [key, { ...row }])),
    drafts: new Map([...state.drafts].map(([key, row]) => [key, { ...row }])),
    schemaStatementsExecuted: state.schemaStatementsExecuted,
  };
}

/** Snapshot aus takeSnapshot() wiederherstellen. */
function restoreSnapshot(
  state: StateTables,
  snapshot: FakeTableSnapshot,
): void {
  state.meta = snapshot.meta;
  state.accounts = snapshot.accounts;
  state.pools = snapshot.pools;
  state.categories = snapshot.categories;
  state.taxCategories = snapshot.taxCategories;
  state.bookings = snapshot.bookings;
  state.splits = snapshot.splits;
  state.outbox = snapshot.outbox;
  state.drafts = snapshot.drafts;
  state.schemaStatementsExecuted = snapshot.schemaStatementsExecuted;
}

/**
 * Einzelnes Set evaluieren und in den Fake-State anwenden. Wirft bei
 * nicht unterstützten oder constraintverletzenden Statements —
 * executeSet nutzt das für die Prüf- und Anwendungsphase.
 *
 * `inTransaction` spiegelt die echte SQLite-Semantik für
 * `PRAGMA foreign_keys = ON`: innerhalb einer Transaktion wirkungslos,
 * außerhalb wirksam.
 */
function applySet(
  state: StateTables,
  set: SqlSet,
  inTransaction: boolean,
): void {
  // `values` wird an der executeSet-Grenze geprüft (Android-Contract);
  // defensive Wiederholung, damit applySet auch isoliert sicher bleibt.
  const { statement, values } = set;
  if (!Array.isArray(values)) {
    throw new Error('ExecuteSet: No value for values');
  }
  const stmt = normalize(statement);
  if (stmt.startsWith('PRAGMA foreign_keys')) {
    if (!inTransaction) {
      state.foreignKeys = true;
    }
    return;
  }
  if (stmt.startsWith('CREATE TABLE IF NOT EXISTS')) {
    state.schemaStatementsExecuted = true;
    return;
  }
  if (stmt === META_STATEMENTS.upsert) {
    const [key, value] = values as [string, string];
    state.meta.set(key, String(value));
    return;
  }
  if (stmt === META_STATEMENTS.delete) {
    state.meta.delete(String(values[0]));
    return;
  }
  if (stmt === META_STATEMENTS.deleteAll) {
    state.meta.clear();
    return;
  }
  // ── Batch B: Buchungen ────────────────────────────────────────────────
  if (stmt === normalize(INSERT_BOOKING)) {
    const [
      id,
      date,
      type,
      accountId,
      totalAmountCents,
      note,
      baseRevision,
      createdAtUtc,
    ] = values as (string | number | null)[];
    for (const [column, value] of [
      ['date', date],
      ['type', type],
      ['account_id', accountId],
      ['total_amount_cents', totalAmountCents],
      ['created_at_utc', createdAtUtc],
    ] as const) {
      if (value === null || value === undefined) {
        throw new Error(
          `SQLITE_CONSTRAINT_NOTNULL: NOT NULL constraint failed: bookings.${column}`,
        );
      }
    }
    if (state.bookings.has(String(id))) {
      throw constraintError('bookings', 'id', String(id));
    }
    state.bookings.set(String(id), {
      id: String(id),
      date: String(date),
      type: String(type),
      account_id: String(accountId),
      total_amount_cents: Number(totalAmountCents),
      note: note === null || note === undefined ? null : String(note),
      base_revision:
        baseRevision === null || baseRevision === undefined
          ? null
          : Number(baseRevision),
      created_at_utc: String(createdAtUtc),
    });
    return;
  }
  if (stmt === normalize(UPDATE_BOOKING)) {
    const [date, totalAmountCents, accountId, note, id] = values as (
      | string
      | number
      | null
    )[];
    const row = state.bookings.get(String(id));
    if (!row) {
      return; // SQLite: 0 Zeilen, kein Fehler
    }
    row.date = String(date);
    row.total_amount_cents = Number(totalAmountCents);
    row.account_id = String(accountId);
    row.note = note === null || note === undefined ? null : String(note);
    return;
  }
  if (stmt === normalize(DELETE_BOOKING)) {
    const id = String(values[0]);
    if (!state.bookings.has(id)) {
      return; // SQLite: 0 Zeilen, kein Fehler
    }
    state.bookings.delete(id);
    // ON DELETE CASCADE (Design D2): Splits fallen immer mit der Buchung.
    for (const [splitId, split] of [...state.splits]) {
      if (split.booking_id === id) {
        state.splits.delete(splitId);
      }
    }
    // ON DELETE CASCADE (outbox.booking_id → bookings.id): Outbox-
    // Zeilen fallen immer mit der Buchung (Option A, Review B10).
    for (const [operationId, outboxRow] of [...state.outbox]) {
      if (outboxRow.booking_id === id) {
        state.outbox.delete(operationId);
      }
    }
    return;
  }
  // ── Batch B: Splits ───────────────────────────────────────────────────
  if (stmt === normalize(INSERT_SPLIT)) {
    const [
      id,
      bookingId,
      amountCents,
      poolId,
      categoryId,
      note,
      taxRelevant,
      taxCategoryId,
      taxCategoryName,
      markedAmountCents,
      taxNote,
    ] = values as (string | number | null)[];
    for (const [column, value] of [
      ['booking_id', bookingId],
      ['amount_cents', amountCents],
      ['pool_id', poolId],
      ['category_id', categoryId],
    ] as const) {
      if (value === null || value === undefined) {
        throw new Error(
          `SQLITE_CONSTRAINT_NOTNULL: NOT NULL constraint failed: splits.${column}`,
        );
      }
    }
    if (state.foreignKeys && !state.bookings.has(String(bookingId))) {
      throw new Error(
        `SQLITE_CONSTRAINT_FOREIGNKEY: FOREIGN KEY constraint failed: splits.booking_id → bookings.id (${String(bookingId)})`,
      );
    }
    if (state.splits.has(String(id))) {
      throw constraintError('splits', 'id', String(id));
    }
    state.splits.set(String(id), {
      id: String(id),
      booking_id: String(bookingId),
      amount_cents: Number(amountCents),
      pool_id: String(poolId),
      category_id: String(categoryId),
      note: note === null || note === undefined ? null : String(note),
      tax_relevant:
        taxRelevant === null || taxRelevant === undefined
          ? null
          : Number(taxRelevant),
      tax_category_id:
        taxCategoryId === null || taxCategoryId === undefined
          ? null
          : String(taxCategoryId),
      tax_category_name:
        taxCategoryName === null || taxCategoryName === undefined
          ? null
          : String(taxCategoryName),
      marked_amount_cents:
        markedAmountCents === null || markedAmountCents === undefined
          ? null
          : Number(markedAmountCents),
      tax_note: taxNote === null || taxNote === undefined ? null : String(taxNote),
    });
    return;
  }
  if (stmt === normalize(DELETE_SPLITS)) {
    const bookingId = String(values[0]);
    for (const [id, row] of [...state.splits]) {
      if (row.booking_id === bookingId) {
        state.splits.delete(id);
      }
    }
    return;
  }
  // ── Batch B: Outbox (Design D4) ───────────────────────────────────────
  if (stmt === normalize(INSERT_OUTBOX)) {
    const [
      operationId,
      deviceId,
      type,
      bookingId,
      payload,
      payloadHash,
      expectedBaseRevision,
      status,
      createdAtUtc,
      updatedAtUtc,
    ] = values as (string | number | null)[];
    if (state.outbox.has(String(operationId))) {
      throw constraintError('outbox', 'operation_id', String(operationId));
    }
    if (state.foreignKeys && !state.bookings.has(String(bookingId))) {
      throw new Error(
        `SQLITE_CONSTRAINT_FOREIGNKEY: FOREIGN KEY constraint failed: outbox.booking_id → bookings.id (${String(bookingId)})`,
      );
    }
    state.outbox.set(String(operationId), {
      operation_id: String(operationId),
      device_id: String(deviceId),
      type: String(type),
      booking_id: String(bookingId),
      payload: String(payload),
      payload_hash: String(payloadHash),
      expected_base_revision:
        expectedBaseRevision === null || expectedBaseRevision === undefined
          ? null
          : Number(expectedBaseRevision),
      status: String(status),
      created_at_utc: String(createdAtUtc),
      updated_at_utc: String(updatedAtUtc),
    });
    return;
  }
  if (stmt === normalize(UPDATE_OUTBOX_PAYLOAD)) {
    const [payload, payloadHash, updatedAtUtc, operationId, status] =
      values as (string | number)[];
    const row = state.outbox.get(String(operationId));
    // WHERE operation_id = ? AND status = ? → sonst 0 Zeilen (kein Fehler).
    if (!row || row.status !== String(status)) {
      return;
    }
    row.payload = String(payload);
    row.payload_hash = String(payloadHash);
    row.updated_at_utc = String(updatedAtUtc);
    return;
  }
  if (stmt === normalize(DELETE_OUTBOX)) {
    state.outbox.delete(String(values[0]));
    return;
  }
  // ── Batch B: Entwürfe ─────────────────────────────────────────────────
  if (stmt === normalize(UPSERT_DRAFT)) {
    const [id, name, data, createdAtUtc, updatedAtUtc] =
      values as (string | null)[];
    const existing = state.drafts.get(String(id));
    if (existing) {
      // Upsert: created_at_utc bleibt erhalten.
      existing.name = String(name);
      existing.data = String(data);
      existing.updated_at_utc = String(updatedAtUtc);
    } else {
      state.drafts.set(String(id), {
        id: String(id),
        name: String(name),
        data: String(data),
        created_at_utc: String(createdAtUtc),
        updated_at_utc: String(updatedAtUtc),
      });
    }
    return;
  }
  if (stmt === normalize(DELETE_DRAFT)) {
    state.drafts.delete(String(values[0]));
    return;
  }
  // ── Batch C: Stammdaten-Snapshot (Design D3) ────────────────────────────
  if (stmt === normalize(DELETE_SNAPSHOT_ACCOUNTS)) {
    state.accounts.clear();
    return;
  }
  if (stmt === normalize(DELETE_SNAPSHOT_POOLS)) {
    state.pools.clear();
    return;
  }
  if (stmt === normalize(DELETE_SNAPSHOT_CATEGORIES)) {
    state.categories.clear();
    return;
  }
  if (stmt === normalize(DELETE_SNAPSHOT_TAX_CATEGORIES)) {
    state.taxCategories.clear();
    return;
  }
  if (stmt === normalize(INSERT_SNAPSHOT_ACCOUNT)) {
    const [id, name, type, isInterestBearing, status, baseRevision] =
      values as (string | number)[];
    for (const [column, value] of [
      ['name', name],
      ['type', type],
      ['is_interest_bearing', isInterestBearing],
      ['status', status],
      ['base_revision', baseRevision],
    ] as const) {
      if (value === null || value === undefined) {
        throw new Error(
          `SQLITE_CONSTRAINT_NOTNULL: NOT NULL constraint failed: accounts.${column}`,
        );
      }
    }
    if (state.accounts.has(String(id))) {
      throw constraintError('accounts', 'id', String(id));
    }
    state.accounts.set(String(id), {
      id: String(id),
      name: String(name),
      type: String(type),
      is_interest_bearing: Number(isInterestBearing),
      status: String(status),
      base_revision: Number(baseRevision),
    });
    return;
  }
  if (stmt === normalize(INSERT_SNAPSHOT_POOL)) {
    const [id, name, status, baseRevision] = values as (string | number)[];
    for (const [column, value] of [
      ['name', name],
      ['status', status],
      ['base_revision', baseRevision],
    ] as const) {
      if (value === null || value === undefined) {
        throw new Error(
          `SQLITE_CONSTRAINT_NOTNULL: NOT NULL constraint failed: pools.${column}`,
        );
      }
    }
    if (state.pools.has(String(id))) {
      throw constraintError('pools', 'id', String(id));
    }
    state.pools.set(String(id), {
      id: String(id),
      name: String(name),
      status: String(status),
      base_revision: Number(baseRevision),
    });
    return;
  }
  if (stmt === normalize(INSERT_SNAPSHOT_CATEGORY)) {
    const [id, name, parentId, status, baseRevision, taxPresetJson] =
      values as (string | null)[];
    for (const [column, value] of [
      ['name', name],
      ['status', status],
      ['base_revision', baseRevision],
    ] as const) {
      if (value === null || value === undefined) {
        throw new Error(
          `SQLITE_CONSTRAINT_NOTNULL: NOT NULL constraint failed: categories.${column}`,
        );
      }
    }
    if (state.categories.has(String(id))) {
      throw constraintError('categories', 'id', String(id));
    }
    state.categories.set(String(id), {
      id: String(id),
      name: String(name),
      parent_id: parentId === null || parentId === undefined ? null : String(parentId),
      status: String(status),
      base_revision: Number(baseRevision),
      tax_preset_json:
        taxPresetJson === null || taxPresetJson === undefined
          ? null
          : String(taxPresetJson),
    });
    return;
  }
  if (stmt === normalize(INSERT_SNAPSHOT_TAX_CATEGORY)) {
    const [id, name, parentId, status, baseRevision] =
      values as (string | null)[];
    for (const [column, value] of [
      ['name', name],
      ['status', status],
      ['base_revision', baseRevision],
    ] as const) {
      if (value === null || value === undefined) {
        throw new Error(
          `SQLITE_CONSTRAINT_NOTNULL: NOT NULL constraint failed: tax_categories.${column}`,
        );
      }
    }
    if (state.taxCategories.has(String(id))) {
      throw constraintError('tax_categories', 'id', String(id));
    }
    state.taxCategories.set(String(id), {
      id: String(id),
      name: String(name),
      parent_id: parentId === null || parentId === undefined ? null : String(parentId),
      status: String(status),
      base_revision: Number(baseRevision),
    });
    return;
  }
  throw new Error(
    `capacitor-sqlite-fake: executeSet unterstützt das Statement nicht: ${statement}`,
  );
}

/**
 * Query auswerten. Unbekannte Statements werfen (Batch B: lautes
 * Scheitern statt leeres Ergebnis), damit Specs nie still leer laufen.
 */
function runQuery(state: StateTables, statement: string, values: unknown[]): unknown[] {
  const stmt = normalize(statement);
  const v = (index: number): string => String(values[index]);
  // Batch A: FK-Status.
  if (stmt.startsWith('PRAGMA foreign_keys')) {
    return [{ foreign_keys: state.foreignKeys ? 1 : 0 }];
  }
  if (stmt === META_STATEMENTS.select) {
    const value = state.meta.get(v(0));
    return value === undefined ? [] : [{ value }];
  }
  // ── Batch B: Buchungen/Splits ─────────────────────────────────────────
  if (stmt === normalize(SELECT_BOOKING_LIST)) {
    // ORDER BY date DESC, created_at_utc DESC — created_at_utc stammt
    // aus der Quellzeile (im JOIN-Ziel nicht selektiert).
    const sourceRows = [...state.bookings.values()].sort(
      (a, b) =>
        b.date.localeCompare(a.date) ||
        b.created_at_utc.localeCompare(a.created_at_utc) ||
        b.id.localeCompare(a.id),
    );
    return sourceRows.map((b) => {
      let outboxStatus: string | null = null;
      for (const o of state.outbox.values()) {
        if (o.booking_id === b.id) {
          outboxStatus = o.status;
          break;
        }
      }
      return {
        id: b.id,
        date: b.date,
        total_amount_cents: b.total_amount_cents,
        account_id: b.account_id,
        note: b.note,
        outbox_status: outboxStatus,
      };
    });
  }
  if (stmt === normalize(SELECT_BOOKING_BY_ID)) {
    const row = state.bookings.get(v(0));
    return row === undefined ? [] : [row];
  }
  if (stmt === normalize(SELECT_SPLITS_BY_BOOKING)) {
    const rows = [...state.splits.values()]
      .filter((s) => s.booking_id === v(0))
      .sort((a, b) => a.id.localeCompare(b.id));
    return rows;
  }
  // ── Batch B: Outbox ───────────────────────────────────────────────────
  if (stmt === normalize(SELECT_OUTBOX_BY_BOOKING)) {
    const rows: { operation_id: string; status: string }[] = [];
    for (const row of state.outbox.values()) {
      if (row.booking_id === v(0)) {
        rows.push({ operation_id: row.operation_id, status: row.status });
      }
    }
    return rows;
  }
  // ── Batch B: Entwürfe ─────────────────────────────────────────────────
  if (stmt === normalize(SELECT_DRAFT_BY_ID)) {
    const row = state.drafts.get(v(0));
    return row === undefined ? [] : [row];
  }
  if (stmt === normalize(SELECT_DRAFTS)) {
    const rows = [...state.drafts.values()].sort(
      (a, b) =>
        b.updated_at_utc.localeCompare(a.updated_at_utc) ||
        b.id.localeCompare(a.id),
    );
    return rows;
  }
  // ── Batch C: Stammdaten-Snapshot (Design D3) ────────────────────────────
  if (stmt === normalize(SELECT_SNAPSHOT_ACCOUNTS)) {
    return [...state.accounts.values()];
  }
  if (stmt === normalize(SELECT_SNAPSHOT_POOLS)) {
    return [...state.pools.values()];
  }
  if (stmt === normalize(SELECT_SNAPSHOT_CATEGORIES)) {
    return [...state.categories.values()];
  }
  if (stmt === normalize(SELECT_SNAPSHOT_TAX_CATEGORIES)) {
    return [...state.taxCategories.values()];
  }
  if (stmt === normalize(SELECT_ACCOUNT_STATUSES)) {
    return [...state.accounts.values()].map((row) => ({
      id: row.id,
      status: row.status,
    }));
  }
  if (stmt === normalize(SELECT_POOL_STATUSES)) {
    return [...state.pools.values()].map((row) => ({
      id: row.id,
      status: row.status,
    }));
  }
  if (stmt === normalize(SELECT_CATEGORY_STATUSES)) {
    return [...state.categories.values()].map((row) => ({
      id: row.id,
      status: row.status,
    }));
  }
  if (stmt === normalize(SELECT_TAX_CATEGORY_STATUSES)) {
    return [...state.taxCategories.values()].map((row) => ({
      id: row.id,
      status: row.status,
    }));
  }
  throw new Error(
    `capacitor-sqlite-fake: query unterstützt das Statement nicht: ${statement}`,
  );
}

/**
 * Die von der Fake-Factory bereitgestellten Plugin-Methoden.
 * Konkrete Parameter-Typen (statt `...args: unknown[]`), damit die
 * Implementierungen typkorrekt zuweisen sind.
 */
export interface CapacitorSqliteMock {
  createConnection(options: { database: string; readonly?: boolean }): Promise<void>;
  open(options: { database: string; readonly?: boolean }): Promise<void>;
  executeSet(options: {
    database: string;
    set: SqlSet[];
    readonly?: boolean;
    transaction?: boolean;
  }): Promise<void>;
  query(options: { database: string; statement: string; values?: unknown[]; readonly?: boolean }): Promise<{ values: unknown[] }>;
  run(options: {
    database: string;
    statement: string;
    values?: unknown[];
    readonly?: boolean;
    /** Contract 8.1.1: Default true (automatische Plugin-Transaktion). */
    transaction?: boolean;
  }): Promise<void>;
  /** Native Transaktions-Methoden (Contract 8.1.1) — siehe Datei-Doku. */
  beginTransaction(options: { database: string; readonly?: boolean }): Promise<void>;
  commitTransaction(options: { database: string; readonly?: boolean }): Promise<void>;
  rollbackTransaction(options: { database: string; readonly?: boolean }): Promise<void>;
  isTransactionActive(options: { database: string; readonly?: boolean }): Promise<{ result: boolean }>;
}

/**
 * Mock-Factory für `vi.mock('@capacitor-community/sqlite', …)`.
 * Von den Specs importiert und direkt an `vi.mock` übergeben.
 */
export function capacitorSqliteMockFactory(): { CapacitorSQLite: CapacitorSqliteMock } {
  return {
    CapacitorSQLite: {
      async createConnection(options: { database: string; readonly?: boolean }) {
        if (dbState.failOnCreateConnection) {
          throw new Error('simulated connection failure');
        }
        const key = (options.readonly ? 'RO_' : 'RW_') + options.database;
        if (dbState.connections.has(key)) {
          throw new Error(`Connection ${options.database} already exists`);
        }
        dbState.connections.set(key, { opened: false });
      },
      async open(options: { database: string; readonly?: boolean }) {
        const key = (options.readonly ? 'RO_' : 'RW_') + options.database;
        if (!dbState.connections.has(key)) {
          throw new Error(`No available connection for database ${options.database}`);
        }
        dbState.connections.get(key)!.opened = true;
        // Contract Android 8.1.1: open() aktiviert FK-Constraints
        // (Database.java: setForeignKeyConstraintsEnabled(true)).
        dbState.foreignKeys = true;
      },
      /**
       * Plugin-Vertrag 8.1.1: `values` ist bei JEDEM Set-Element
       * Pflichtfeld (Android: getJSONArray wirft ohne es). Standard:
       * ALLES-ODER-NICHTS (transaction: true) — Snapshot, dann alle
       * Sets anwenden; bei einem Fehler Snapshot wiederherstellen und
       * werfen (kein Teilzustand). Mit `transaction: false` läuft
       * jedes Statement sofort (keine All-or-Nothing-Garantie).
       */
      async executeSet(options: {
        database: string;
        set: SqlSet[];
        readonly?: boolean;
        transaction?: boolean;
      }) {
        const key = (options.readonly ? 'RO_' : 'RW_') + options.database;
        const conn = dbState.connections.get(key);
        if (!conn) {
          throw new Error(`No available connection for database ${options.database}`);
        }
        if (!conn.opened) {
          throw new Error(`database ${options.database} not opened`);
        }
        // Prüfphase: alle Sets müssen gültig sein, bevor etwas wirkt.
        for (const set of options.set) {
          if (!set || typeof set.statement !== 'string') {
            throw new Error('executeSet: Set ohne gültiges Statement');
          }
          if (!Array.isArray(set.values)) {
            throw new Error('ExecuteSet: No value for values');
          }
        }
        const inTransaction = options.transaction !== false;
        // Verschachtelungs-Schutz (Review-Befund P2): während eine
        // manuelle Transaktion in Fluge ist, ist keine automatische
        // Transaktion (transaction: true) erlaubt — SQLite: „cannot
        // start a transaction within a transaction“.
        if (inTransaction && dbState.manualTxActive) {
          throw new Error(
            'cannot start a transaction within a transaction',
          );
        }
        // PRAGMA foreign_keys wirkt nur außerhalb EINES Transaktions-
        // Kontexts (SQLite-Semantik): auch eine manuell offene
        // Transaktion zählt (Konsistenz mit run()).
        const effectiveTx = inTransaction || dbState.manualTxActive;
        const snapshot = inTransaction ? takeSnapshot(dbState) : null;
        try {
          for (let index = 0; index < options.set.length; index += 1) {
            if (dbState.failOnStatementIndex === index) {
              throw new Error('simulated failure');
            }
            applySet(dbState, options.set[index], effectiveTx);
          }
        } catch (error) {
          if (snapshot) {
            restoreSnapshot(dbState, snapshot);
          }
          // transaction: false (z. B. in einer manuellen Transaktion):
          // die Statements wirken sofort — bei Fehler kann ein
          // Teilzustand in der offenen Transaktion bleiben (Plugin-
          // Contract 8.1.1: executeSet ohne begin/rollback). Der
          // Aufrufer (LocalDatabase.withWriteTransaction) rollt
          // explizit zurück.
          throw error;
        }
        dbState.executedSets.push({
          database: options.database,
          readonly: options.readonly,
          transaction: inTransaction,
          sets: options.set,
        });
      },
      async query(options: {
        database: string;
        statement: string;
        values?: unknown[];
        readonly?: boolean;
      }) {
        // Echter Plugin-Vertrag: `values` ist Pflichtfeld (auch []).
        if (options.values === undefined || !Array.isArray(options.values)) {
          throw new Error('Query: Must provide an Array of Strings');
        }
        const key = (options.readonly ? 'RO_' : 'RW_') + options.database;
        const conn = dbState.connections.get(key);
        if (!conn) {
          throw new Error(`No available connection for database ${options.database}`);
        }
        if (!conn.opened) {
          throw new Error(`database ${options.database} not opened`);
        }
        dbState.queryLog.push({
          statement: options.statement,
          manualTxActive: dbState.manualTxActive,
        });
        return { values: runQuery(dbState, options.statement, options.values) };
      },
      async run(options: {
        database: string;
        statement: string;
        values?: unknown[];
        readonly?: boolean;
        transaction?: boolean;
      }) {
        if (options.values === undefined || !Array.isArray(options.values)) {
          throw new Error('Run: Must provide an Array of values');
        }
        // run() adressiert im Plugin immer die RW_-Connection.
        const conn = dbState.connections.get('RW_' + options.database);
        if (!conn) {
          throw new Error(`No available connection for database ${options.database}`);
        }
        if (options.readonly === true) {
          throw new Error('not allowed in read-only mode');
        }
        if (!conn.opened) {
          throw new Error(`database ${options.database} not opened`);
        }
        // Plugin-Vertrag 8.1.1 (verifiziert an Database.java v8.1.1):
        // `transaction` default TRUE — das Plugin startet EIGENS eine
        // Transaktion um das Statement (beginTransaction → Statement →
        // commitTransaction; bei Fehler finally: rollbackTransaction).
        // Steuer-Statements per run() OHNE `transaction: false`
        // fehlschlagen daher (Review-Befund P1, 3. Runde):
        // - BEGIN: verschachtelt → SQLite-Fehler
        // - COMMIT/ROLLBACK: das Statement beendet die Plugin-
        //   Transaktion, dann wirft die Plugin-eigene
        //   commitTransaction()-Prüfung „No transaction active“
        const autoTx = options.transaction !== false;
        const txStatement = normalize(options.statement).replace(/;$/, '');
        const isBegin = /^begin(\s+(immediate|deferred|exclusive))?$/i.test(
          txStatement,
        );
        const isCommit = /^commit(\s+transaction)?$/i.test(txStatement);
        const isRollback = /^rollback(\s+transaction)?$/i.test(txStatement);
        if (autoTx) {
          if (isBegin) {
            throw new Error(
              'cannot start a transaction within a transaction',
            );
          }
          if (isCommit || isRollback) {
            // Net-Effekt: keine State-Änderung (die Plugin-Transaktion
            // ist leer), dann Fehler — wie im echten Plugin.
            throw new Error('No transaction active');
          }
          // Daten-Statement: All-oder-Nichts via Snapshot (das echte
          // Plugin rollt seine Automatik-Transaktion bei Fehler in
          // finally zurück).
          const snapshot = takeSnapshot(dbState);
          try {
            applySet(
              dbState,
              { statement: options.statement, values: options.values },
              true,
            );
          } catch (error) {
            restoreSnapshot(dbState, snapshot);
            throw error;
          }
          return;
        }
        // Manuelle Transaktion (transaction: false): SQLite-Semantik —
        // ein fehlerhaftes Daten-Statement LÄSST die Transaktion
        // offen; der Aufrufer (LocalDatabase) rollt explizit zurück.
        if (isBegin) {
          if (dbState.manualTxActive) {
            throw new Error(
              'cannot start a transaction within a transaction',
            );
          }
          dbState.manualTxSnapshot = takeSnapshot(dbState);
          dbState.manualTxActive = true;
          return;
        }
        if (isCommit || isRollback) {
          if (!dbState.manualTxActive) {
            throw new Error(
              isCommit
                ? 'cannot commit - no transaction is active'
                : 'cannot rollback - no transaction is active',
            );
          }
          if (isRollback) {
            restoreSnapshot(dbState, dbState.manualTxSnapshot!);
          }
          dbState.manualTxActive = false;
          dbState.manualTxSnapshot = null;
          return;
        }
        // Daten-Statement: sofort angewandt (PRAGMA foreign_keys
        // no-op-Regel innerhalb offener Transaktion).
        applySet(
          dbState,
          { statement: options.statement, values: options.values },
          dbState.manualTxActive,
        );
      },
      /**
       * Native Transaktions-Methoden (Contract 8.1.1; Android:
       * `Database.beginTransaction()` — deferred Write-Transaktion).
       * `LocalDatabase.withWriteTransaction` nutzt diese (Review-Befund
       * P1 — keine Steuer-Statements per `run()`).
       */
      async beginTransaction(options: {
        database: string;
        readonly?: boolean;
      }) {
        const conn = dbState.connections.get('RW_' + options.database);
        if (!conn || !conn.opened) {
          throw new Error(
            `No available connection for database ${options.database}`,
          );
        }
        if (dbState.manualTxActive) {
          // Android-Contract (Database.beginTransaction()).
          throw new Error('Already in transaction');
        }
        dbState.manualTxSnapshot = takeSnapshot(dbState);
        dbState.manualTxActive = true;
      },
      async commitTransaction(options: {
        database: string;
        readonly?: boolean;
      }) {
        const conn = dbState.connections.get('RW_' + options.database);
        if (!conn || !conn.opened) {
          throw new Error(
            `No available connection for database ${options.database}`,
          );
        }
        if (!dbState.manualTxActive) {
          // Android-Contract (Database.commitTransaction()).
          throw new Error('No transaction active');
        }
        // Commit: BEGIN-Snapshot verwerfen (aktueller State gilt).
        dbState.manualTxActive = false;
        dbState.manualTxSnapshot = null;
      },
      async rollbackTransaction(options: {
        database: string;
        readonly?: boolean;
      }) {
        const conn = dbState.connections.get('RW_' + options.database);
        if (!conn || !conn.opened) {
          throw new Error(
            `No available connection for database ${options.database}`,
          );
        }
        if (!dbState.manualTxActive) {
          return; // Android-Contract: no-op ohne aktive Transaktion.
        }
        restoreSnapshot(dbState, dbState.manualTxSnapshot!);
        dbState.manualTxActive = false;
        dbState.manualTxSnapshot = null;
      },
      async isTransactionActive(options: {
        database: string;
        readonly?: boolean;
      }) {
        const conn = dbState.connections.get('RW_' + options.database);
        if (!conn || !conn.opened) {
          throw new Error(
            `No available connection for database ${options.database}`,
          );
        }
        return { result: dbState.manualTxActive };
      },
    },
  };
}
