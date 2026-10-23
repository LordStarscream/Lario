/**
 * Batch A — Speichertest mit ECHTER SQLite über `node:sqlite`.
 *
 * Die anderen Specs der App laufen im jsdom-Umfeld; diese Datei braucht
 * stattdessen den Node-Kontext, weil sie eine echte SQLite-Datei im
 * Temp-Ordner anlegt und das Batch-A-Schema (Design D2) gegen echte
 * Constraints prüft: Idempotenz, FK-Cascade, Unique-Constraints,
 * FK-Verletzungen, Batch-Atomarität und die Meta-Helfer.
 */
// @vitest-environment node
import { mkdtempSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { DatabaseSync } from 'node:sqlite';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';

import { deleteMetaSet, SCHEMA_STATEMENTS, selectMetaSet, upsertMetaSet } from './statements';

/** Ein Set-Element (entspricht `SqlSet` aus statements.ts). */
interface TestSqlSet {
  statement: string;
  values?: unknown[];
}

/**
 * Führt eine Liste von Set-Elementen in EINER Transaktion aus
 * (`BEGIN` … `COMMIT`) — das Pendant zu `executeSet` (transaction: true)
 * auf einer echten SQLite-Verbindung.
 */
function runScript(db: DatabaseSync, statements: TestSqlSet[]): void {
  db.exec('BEGIN');
  for (const s of statements) {
    const values = (s.values ?? []) as (string | number | bigint | null)[];
    db.prepare(s.statement).run(...values);
  }
  db.exec('COMMIT');
}

/**
 * init-Äquivalent zu `LocalDatabase.init()`: Foreign-Key-Prüfung pro
 * Verbindung aktivieren und das komplette Batch-A-Schema anlegen.
 * Zweiter Aufruf muss fehlerfrei idempotent sein (`IF NOT EXISTS`).
 */
function initSchema(db: DatabaseSync): void {
  db.exec('PRAGMA foreign_keys = ON');
  runScript(db, SCHEMA_STATEMENTS.map((statement) => ({ statement })));
}

/** Führt ein einzelnes Set-Element (z. B. aus den Meta-Helfern) aus. */
function executeSet(db: DatabaseSync, set: TestSqlSet): void {
  const values = (set.values ?? []) as (string | number | bigint | null)[];
  db.prepare(set.statement).run(...values);
}

/** Liest die `value`-Spalte aller Zeilen eines Set-Elements aus. */
function querySet(db: DatabaseSync, set: TestSqlSet): string[] {
  const values = (set.values ?? []) as (string | number | bigint | null)[];
  const rows = db.prepare(set.statement).all(...values) as { value: string }[];
  return rows.map((row) => row.value);
}

describe('Batch-A-Schema (echte SQLite via node:sqlite)', () => {
  let db: DatabaseSync;
  let tmpDir: string;

  beforeEach(() => {
    // Pro Test: frischer Temp-Ordner + frische SQLite-Datei.
    tmpDir = mkdtempSync(join(tmpdir(), 'lario-schema-'));
    db = new DatabaseSync(join(tmpDir, 'test.db'));
  });

  afterEach(() => {
    // Teardown: Verbindung schließen und Temp-Ordner entfernen.
    db.close();
    rmSync(tmpDir, { recursive: true, force: true });
  });

  it('erzeugt das Schema fehlerfrei; zweiter Aufruf ist idempotent', () => {
    initSchema(db);
    // Zweite Initialisierung (z. B. App-Neustart) darf nichts ändern
    // und darf nicht werfen.
    initSchema(db);

    const rows = db
      .prepare('SELECT name FROM sqlite_master WHERE type = ? ORDER BY name')
      .all('table') as { name: string }[];
    const tableNames = rows.map((row) => row.name);

    for (const expected of [
      'accounts',
      'bookings',
      'categories',
      'drafts',
      'meta',
      'outbox',
      'pools',
      'splits',
      'tax_categories',
    ]) {
      expect(tableNames).toContain(expected);
    }
  });

  it('FK-Cascade: Löschen der Buchung löscht ihre Splits', () => {
    initSchema(db);

    // Buchung mit allen NOT NULL-Feldern; optionale Felder bleiben NULL.
    db.prepare(
      `INSERT INTO bookings
        (id, date, type, account_id, total_amount_cents, note, base_revision, created_at_utc)
       VALUES (?, ?, ?, ?, ?, NULL, NULL, ?)`,
    ).run('b1', '2024-05-01', 'expense', 'a1', 1000, '2024-05-01T00:00:00Z');

    // Zwei Splits hängen an der Buchung.
    db.prepare(
      `INSERT INTO splits
        (id, booking_id, amount_cents, pool_id, category_id, note, tax_relevant, tax_category_id, tax_category_name, marked_amount_cents, tax_note)
       VALUES (?, ?, ?, ?, ?, NULL, NULL, NULL, NULL, NULL, NULL)`,
    ).run('s1', 'b1', 600, 'p1', 'c1');
    db.prepare(
      `INSERT INTO splits
        (id, booking_id, amount_cents, pool_id, category_id, note, tax_relevant, tax_category_id, tax_category_name, marked_amount_cents, tax_note)
       VALUES (?, ?, ?, ?, ?, NULL, NULL, NULL, NULL, NULL, NULL)`,
    ).run('s2', 'b1', 400, 'p1', 'c2');

    // Buchung löschen — die Splits müssen per ON DELETE CASCADE folgen.
    db.prepare('DELETE FROM bookings WHERE id = ?').run('b1');

    const rows = db
      .prepare('SELECT COUNT(*) AS count FROM splits')
      .all() as { count: number }[];
    expect(rows[0]?.count).toBe(0);
  });

  it('Unique-Constraints: doppelte bookings.id und doppelte outbox.operation_id werfen', () => {
    initSchema(db);

    const insertBooking = (id: string): void => {
      db.prepare(
        `INSERT INTO bookings
          (id, date, type, account_id, total_amount_cents, note, base_revision, created_at_utc)
         VALUES (?, ?, ?, ?, ?, NULL, NULL, ?)`,
      ).run(id, '2024-05-01', 'expense', 'a1', 1000, '2024-05-01T00:00:00Z');
    };

    insertBooking('b1');
    // Zweite Buchung mit derselben Primärschlüssel-Wert wirft.
    expect(() => insertBooking('b1')).toThrow();

    const insertOutbox = (operationId: string): void => {
      db.prepare(
        `INSERT INTO outbox
          (operation_id, device_id, type, booking_id, payload, payload_hash, expected_base_revision, status, created_at_utc, updated_at_utc)
         VALUES (?, ?, ?, ?, ?, ?, NULL, ?, ?, ?)`,
      ).run(
        operationId,
        'device-1',
        'BOOKING_CREATED',
        'b1',
        '{}',
        'hash-1',
        'pending',
        '2024-05-01T00:00:00Z',
        '2024-05-01T00:00:00Z',
      );
    };

    insertOutbox('op1');
    // Zweite Outbox-Zeile mit derselben operation_id (PRIMARY KEY) wirft.
    expect(() => insertOutbox('op1')).toThrow();
  });

  it('FK-Verletzung: Split auf nicht existierende Buchung wirft', () => {
    initSchema(db);

    expect(() => {
      db.prepare(
        `INSERT INTO splits
          (id, booking_id, amount_cents, pool_id, category_id, note, tax_relevant, tax_category_id, tax_category_name, marked_amount_cents, tax_note)
         VALUES (?, ?, ?, ?, ?, NULL, NULL, NULL, NULL, NULL, NULL)`,
      ).run('s1', 'fehlend', 100, 'p1', 'c1');
    }).toThrow();
  });

  it('Atomarität (BEGIN...COMMIT): Constraint-Verstoß rollt den ganzen Batch zurück', () => {
    initSchema(db);

    // Vorheriger, bereits committer Zustand: 1 Buchung + 1 Split.
    db.prepare(
      `INSERT INTO bookings
        (id, date, type, account_id, total_amount_cents, note, base_revision, created_at_utc)
       VALUES (?, ?, ?, ?, ?, NULL, NULL, ?)`,
    ).run('b1', '2024-05-01', 'expense', 'a1', 1000, '2024-05-01T00:00:00Z');
    db.prepare(
      `INSERT INTO splits
        (id, booking_id, amount_cents, pool_id, category_id, note, tax_relevant, tax_category_id, tax_category_name, marked_amount_cents, tax_note)
       VALUES (?, ?, ?, ?, ?, NULL, NULL, NULL, NULL, NULL, NULL)`,
    ).run('s1', 'b1', 1000, 'p1', 'c1');

    // Ein Batch in einer Transaktion: die zweite Buchung wird angelegt,
    // der folgende Split verletzt die FK — das Ganze muss zurückrollen.
    db.exec('BEGIN');
    db.prepare(
      `INSERT INTO bookings
        (id, date, type, account_id, total_amount_cents, note, base_revision, created_at_utc)
       VALUES (?, ?, ?, ?, ?, NULL, NULL, ?)`,
    ).run('b2', '2024-05-02', 'income', 'a1', 500, '2024-05-02T00:00:00Z');
    expect(() => {
      db.prepare(
        `INSERT INTO splits
          (id, booking_id, amount_cents, pool_id, category_id, note, tax_relevant, tax_category_id, tax_category_name, marked_amount_cents, tax_note)
         VALUES (?, ?, ?, ?, ?, NULL, NULL, NULL, NULL, NULL, NULL)`,
      ).run('s2', 'fehlend', 100, 'p1', 'c1');
    }).toThrow();
    db.exec('ROLLBACK');

    // Nach dem Rollback: exakt der vorherige Zustand.
    const bookingRows = db
      .prepare('SELECT COUNT(*) AS count FROM bookings')
      .all() as { count: number }[];
    expect(bookingRows[0]?.count).toBe(1);
    const splitRows = db
      .prepare('SELECT COUNT(*) AS count FROM splits')
      .all() as { count: number }[];
    expect(splitRows[0]?.count).toBe(1);
  });

  it('D2a-Semantik: PRAGMA foreign_keys = ON ist in einer Transaktion wirkungslos', () => {
    // Echtes SQLite-Verhalten, auf dem die D2a-Korrektur beruht: Das
    // Pragma lässt sich nur außerhalb einer Transaktion ändern.
    // LocalDatabase.init() führt es daher mit transaction: false aus.
    // (node:sqlite aktiviert FKs standardmäßig — hier explizit aus,
    // damit der No-Op-Effekt messbar ist.)
    db.exec('PRAGMA foreign_keys = OFF');
    const initial = db.prepare('PRAGMA foreign_keys').get() as {
      foreign_keys: number;
    };
    expect(initial.foreign_keys).toBe(0); // Ausgangszustand: aus

    db.exec('BEGIN');
    db.exec('PRAGMA foreign_keys = ON');
    db.exec('COMMIT');
    const afterTransactional = db
      .prepare('PRAGMA foreign_keys')
      .get() as { foreign_keys: number };
    expect(afterTransactional.foreign_keys).toBe(0); // wirkungslos geblieben

    db.exec('PRAGMA foreign_keys = ON');
    const afterStandalone = db
      .prepare('PRAGMA foreign_keys')
      .get() as { foreign_keys: number };
    expect(afterStandalone.foreign_keys).toBe(1); // außerhalb wirksam
  });

  it('Meta-Helfer auf echter SQLite', () => {
    initSchema(db);

    // Upsert: Wert setzen und wieder lesen.
    executeSet(db, upsertMetaSet('sync.cursor', 'cursor-1'));
    expect(querySet(db, selectMetaSet('sync.cursor'))).toEqual(['cursor-1']);

    // Zweiter Upsert mit neuem Wert: ersetzt statt duplizieren.
    executeSet(db, upsertMetaSet('sync.cursor', 'cursor-2'));
    expect(querySet(db, selectMetaSet('sync.cursor'))).toEqual(['cursor-2']);

    // Fehlender Key liefert ein leeres Ergebnis.
    expect(querySet(db, selectMetaSet('fehlend.key'))).toEqual([]);

    // Delete: danach wieder leer.
    executeSet(db, deleteMetaSet('sync.cursor'));
    expect(querySet(db, selectMetaSet('sync.cursor'))).toEqual([]);
  });
});
