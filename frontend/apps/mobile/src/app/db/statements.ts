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
 * Design D2. `splits.booking_id` und `outbox.booking_id` referenzieren
 * `bookings(id)` mit ON DELETE CASCADE — wirksam nur, weil `init()`
 * zusätzlich `PRAGMA foreign_keys = ON` setzt (Design D2a).
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
    booking_id TEXT NOT NULL REFERENCES bookings(id) ON DELETE CASCADE,
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

// Batch B — Buchungen, Splits, Outbox, Entwürfe (Design D2/D4/D7)

/** Neue lokale Buchung (Design D2: `base_revision` lokal null). */
export const INSERT_BOOKING =
  'INSERT INTO bookings (id, date, type, account_id, total_amount_cents, note, base_revision, created_at_utc) VALUES (?, ?, ?, ?, ?, ?, ?, ?)';

/** Fachdaten einer Buchung ersetzen (Splits getrennt, Outbox getrennt). */
export const UPDATE_BOOKING =
  'UPDATE bookings SET date = ?, total_amount_cents = ?, account_id = ?, note = ? WHERE id = ?';

/** Buchung löschen (Splits und Outbox-Zeilen fallen immer mit der Buchung — ON DELETE CASCADE; der Fake modelliert beide Cascades immer, unabhängig vom FK-Status). */
export const DELETE_BOOKING = 'DELETE FROM bookings WHERE id = ?';

/** Split an eine Buchung anhängen (`ON DELETE CASCADE` zu bookings). */
export const INSERT_SPLIT =
  'INSERT INTO splits (id, booking_id, amount_cents, pool_id, category_id, note, tax_relevant, tax_category_id, tax_category_name, marked_amount_cents, tax_note) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)';

/** Alle Splits einer Buchung löschen (vor UPDATE/DELETE der Buchung). */
export const DELETE_SPLITS = 'DELETE FROM splits WHERE booking_id = ?';

/** Eingaben-Liste mit Outbox-Status (kein Outbox-Zeile → null/unknown). */
export const SELECT_BOOKING_LIST =
  'SELECT b.id, b.date, b.total_amount_cents, b.account_id, b.note, o.status AS outbox_status FROM bookings b LEFT JOIN outbox o ON o.booking_id = b.id ORDER BY b.date DESC, b.created_at_utc DESC';

/** Buchung laden (alle Spalten). */
export const SELECT_BOOKING_BY_ID =
  'SELECT id, date, type, account_id, total_amount_cents, note, base_revision, created_at_utc FROM bookings WHERE id = ?';

/** Splits einer Buchung in Positionsreihenfolge (id-Präfix codiert die Position). */
export const SELECT_SPLITS_BY_BOOKING =
  'SELECT id, amount_cents, pool_id, category_id, note, tax_relevant, tax_category_id, tax_category_name, marked_amount_cents, tax_note FROM splits WHERE booking_id = ? ORDER BY id';

/** Neue Outbox-Zeile (Design D4: status `queued`, `expected_base_revision` null). */
export const INSERT_OUTBOX =
  'INSERT INTO outbox (operation_id, device_id, type, booking_id, payload, payload_hash, expected_base_revision, status, created_at_utc, updated_at_utc) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?)';

/** Outbox-Zeile einer Buchung (operation_id + status). */
export const SELECT_OUTBOX_BY_BOOKING =
  'SELECT operation_id, status FROM outbox WHERE booking_id = ?';

/** Payload+Hash einer noch queued Outbox-Zeile ersetzen (sonst 0 Zeilen). */
export const UPDATE_OUTBOX_PAYLOAD =
  'UPDATE outbox SET payload = ?, payload_hash = ?, updated_at_utc = ? WHERE operation_id = ? AND status = ?';

/** Outbox-Zeile löschen (wirkt nichts, wenn sie fehlt). */
export const DELETE_OUTBOX = 'DELETE FROM outbox WHERE operation_id = ?';

/** Entwurf anlegen oder ersetzen (created_at_utc bleibt bei bestehendem Entwurf erhalten). */
export const UPSERT_DRAFT =
  'INSERT INTO drafts (id, name, data, created_at_utc, updated_at_utc) VALUES (?, ?, ?, ?, ?) ON CONFLICT(id) DO UPDATE SET name = excluded.name, data = excluded.data, updated_at_utc = excluded.updated_at_utc';

/** Entwurf laden. */
export const SELECT_DRAFT_BY_ID =
  'SELECT id, name, data, created_at_utc, updated_at_utc FROM drafts WHERE id = ?';

/** Alle Entwürfe, zuletzt aktualisierte zuerst. */
export const SELECT_DRAFTS =
  'SELECT id, name, data, created_at_utc, updated_at_utc FROM drafts ORDER BY updated_at_utc DESC, id DESC';

/** Entwurf löschen (wirkt nichts, wenn er fehlt). */
export const DELETE_DRAFT = 'DELETE FROM drafts WHERE id = ?';
