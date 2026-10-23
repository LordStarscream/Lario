/**
 * SQL-Statements der lokalen SQLite-Speicherschicht (Schritt 8, Design D2).
 *
 * Nur SQL — keine Logik und keine Validierung. Das Batch-A-Schema
 * (Masterdaten + Buchungen + Outbox + Entwürfe + Meta) wird von
 * `LocalDatabase.init()` in EINEM `executeSet`-Batch ausgeführt, der
 * explizit transaktional läuft (transaction: true) — atomar oder gar
 * nichts. Das FK-Pragma (D2a) läuft in einem eigenen, NICHT
 * transaktionalen Batch davor: `PRAGMA foreign_keys = ON` wäre in der
 * Schema-Transaktion wirkungslos (SQLite-Semantik).
 *
 * Alle CREATE-Statements sind `IF NOT EXISTS` — die Initialisierung ist
 * dadurch idempotent und Neustart-sicher.
 *
 * An der Plugin-Grenze trägt JEDES Set-Element ein `values`-Array
 * (Review-Befund P1: Android 8.1.1 liest `values` zwingend) —
 * `LocalDatabase` normalisiert das; interne `SqlSet`-Elemente dürfen
 * `values` weglassen.
 */

/** Einzelnes Set-Element für `executeSet` (Plugin-Typ: `values` optional). */
export interface SqlSet {
  statement: string;
  values?: unknown[];
}

/**
 * Komplettes Batch-A-Schema: alle Tabellen exakt mit den Spalten aus
 * Design D2. `splits.booking_id` referenziert `bookings(id)` mit
 * ON DELETE CASCADE — wirksam nur, weil `init()` zusätzlich
 * `PRAGMA foreign_keys = ON` setzt (Design D2a).
 */
export const SCHEMA_STATEMENTS: string[] = [
  `CREATE TABLE IF NOT EXISTS meta (
    key TEXT PRIMARY KEY,
    value TEXT NOT NULL
  );`,
  `CREATE TABLE IF NOT EXISTS accounts (
    id TEXT PRIMARY KEY,
    name TEXT NOT NULL,
    type TEXT NOT NULL,
    is_interest_bearing INTEGER NOT NULL,
    status TEXT NOT NULL,
    base_revision INTEGER NOT NULL
  );`,
  `CREATE TABLE IF NOT EXISTS pools (
    id TEXT PRIMARY KEY,
    name TEXT NOT NULL,
    status TEXT NOT NULL,
    base_revision INTEGER NOT NULL
  );`,
  `CREATE TABLE IF NOT EXISTS categories (
    id TEXT PRIMARY KEY,
    name TEXT NOT NULL,
    parent_id TEXT,
    status TEXT NOT NULL,
    base_revision INTEGER NOT NULL,
    tax_preset_json TEXT
  );`,
  `CREATE TABLE IF NOT EXISTS tax_categories (
    id TEXT PRIMARY KEY,
    name TEXT NOT NULL,
    parent_id TEXT,
    status TEXT NOT NULL,
    base_revision INTEGER NOT NULL
  );`,
  `CREATE TABLE IF NOT EXISTS bookings (
    id TEXT PRIMARY KEY,
    date TEXT NOT NULL,
    type TEXT NOT NULL,
    account_id TEXT NOT NULL,
    total_amount_cents INTEGER NOT NULL,
    note TEXT,
    base_revision INTEGER,
    created_at_utc TEXT NOT NULL
  );`,
  `CREATE TABLE IF NOT EXISTS splits (
    id TEXT PRIMARY KEY,
    booking_id TEXT NOT NULL REFERENCES bookings(id) ON DELETE CASCADE,
    amount_cents INTEGER NOT NULL,
    pool_id TEXT NOT NULL,
    category_id TEXT NOT NULL,
    note TEXT,
    tax_relevant INTEGER,
    tax_category_id TEXT,
    tax_category_name TEXT,
    marked_amount_cents INTEGER,
    tax_note TEXT
  );`,
  `CREATE TABLE IF NOT EXISTS outbox (
    operation_id TEXT PRIMARY KEY,
    device_id TEXT NOT NULL,
    type TEXT NOT NULL,
    booking_id TEXT NOT NULL,
    payload TEXT NOT NULL,
    payload_hash TEXT NOT NULL,
    expected_base_revision INTEGER,
    status TEXT NOT NULL,
    created_at_utc TEXT NOT NULL,
    updated_at_utc TEXT NOT NULL
  );`,
  `CREATE TABLE IF NOT EXISTS drafts (
    id TEXT PRIMARY KEY,
    name TEXT NOT NULL,
    data TEXT NOT NULL,
    created_at_utc TEXT NOT NULL,
    updated_at_utc TEXT NOT NULL
  );`,
];

/** Meta-Wert setzen; bei bestehendem Key wird der Wert ersetzt (Upsert). */
export function upsertMetaSet(key: string, value: string): SqlSet {
  return {
    statement:
      'INSERT INTO meta (key, value) VALUES (?, ?) ON CONFLICT(key) DO UPDATE SET value = excluded.value',
    values: [key, value],
  };
}

/** Meta-Wert löschen (wirkt nichts, wenn der Key fehlt). */
export function deleteMetaSet(key: string): SqlSet {
  return {
    statement: 'DELETE FROM meta WHERE key = ?',
    values: [key],
  };
}

/** Meta-Wert auswählen (eine Zeile mit Spalte `value`, oder leeres Ergebnis). */
export function selectMetaSet(key: string): SqlSet {
  return {
    statement: 'SELECT value FROM meta WHERE key = ?',
    values: [key],
  };
}
