import { Injectable } from '@angular/core';
import { Capacitor } from '@capacitor/core';
import { CapacitorSQLite } from '@capacitor-community/sqlite';

/** Technischer Prototyp-Eintrag (Schritt 2) — keine Finanzbuchung. */
export interface PrototypeEntry {
  /** Gerätegenerierte stabile ID (UUID). */
  id: string;
  label: string;
  /** Optionaler technischer Betrag in Cent (ganzzahlig). */
  amountCents: number | null;
  /** ISO-8601-Zeitpunkt (UTC) der lokalen Speicherung. */
  createdAtUtc: string;
}

/**
 * Lokaler SQLite-Speicher des Prototyps (Android, native Plattform).
 *
 * Bewusst einfache, flache Ablage: Testdatensatz + Meta-Daten
 * (u. a. stabile Geräte-ID als Kopplungsgrundlage für Schritt 8/9).
 * Keinerlei Salden-, Deckungs- oder Sync-Logik.
 *
 * Im Browser (ng serve, Tests) ist das Native-Plugin nicht verfügbar —
 * `isNative` ist dann false und die Methodeen werfen nicht, sondern die
 * UI zeigt eine klare Meldung.
 *
 * Verbindungslifecycle (Plugin-Vertrag, `CapacitorSQLite.java` 8.1.1):
 * Das Plugin hält pro Prozess ein Verbindungsregister (`dbDict`) mit den
 * Schlüsseln `RW_<Name>` / `RO_<Name>`. `open()` wirft ohne vorherige
 * `createConnection()` („No available connection for database …“). Der
 * Prototyp nutzt ausschließlich die RW-Verbindung: Es wird keine
 * `RO_`-Verbindung angelegt, daher dürfen `run()`/`query()`/`execute()`
 * hier nicht mit `readonly: true` aufgerufen werden (ein `run()` mit
 * `readonly: true` wirft sogar explizit).
 */
@Injectable({ providedIn: 'root' })
export class SqliteService {
  private static readonly DbName = 'lario-prototype';

  private static readonly Schema = `
    CREATE TABLE IF NOT EXISTS meta (
      key TEXT PRIMARY KEY,
      value TEXT NOT NULL
    );
    CREATE TABLE IF NOT EXISTS prototype_entries (
      id TEXT PRIMARY KEY,
      label TEXT NOT NULL,
      amount_cents INTEGER,
      created_at_utc TEXT NOT NULL
    );
  `;

  /** true nur im Android-WebView (native Plattform). */
  readonly isNative = Capacitor.isNativePlatform();

  /** true nach erfolgreicher Initialisierung (DB geöffnet, Schema angelegt). */
  ready = false;

  /**
   * true, sobald die RW-Verbindung im (prozessweiten) Plugin-Register
   * angelegt wurde. Das Register existiert pro App-Start neu; bei einem
   * zweiten `init()` in derselben Session darf `createConnection()`
   * nicht erneut aufgerufen werden (würde „already exists“ werfen).
   */
  private connectionCreated = false;

  /**
   * DB öffnen und Schema idempotent anlegen (Neustart-sicher).
   * Pro App-Start: erst RW-`createConnection()`, dann `open()` —
   * das Plugin-Register (`dbDict`) ist prozessweit und startet leer.
   */
  async init(): Promise<void> {
    if (!this.isNative) {
      return;
    }
    if (this.ready) {
      return;
    }
    if (!this.connectionCreated) {
      await CapacitorSQLite.createConnection({
        database: SqliteService.DbName,
        readonly: false,
      });
      this.connectionCreated = true;
    }
    await CapacitorSQLite.open({
      database: SqliteService.DbName,
      readonly: false,
    });
    await CapacitorSQLite.execute({
      database: SqliteService.DbName,
      statements: SqliteService.Schema,
    });
    this.ready = true;
  }

  /** Prototyp-Eintrag lokal speichern (funktioniert offline, ohne Host). */
  async saveEntry(label: string, amountCents: number | null): Promise<PrototypeEntry> {
    const entry: PrototypeEntry = {
      id: newId(),
      label,
      amountCents,
      createdAtUtc: new Date().toISOString(),
    };
    await CapacitorSQLite.run({
      database: SqliteService.DbName,
      statement:
        'INSERT INTO prototype_entries (id, label, amount_cents, created_at_utc) VALUES (?, ?, ?, ?)',
      values: [entry.id, entry.label, entry.amountCents, entry.createdAtUtc],
      readonly: false,
    });
    return entry;
  }

  /** Gespeicherte Einträge laden (neueste zuerst). */
  async listEntries(): Promise<PrototypeEntry[]> {
    const result = await CapacitorSQLite.query({
      database: SqliteService.DbName,
      // readonly: false — der Prototyp legt nur die RW-Verbindung an;
      // readonly: true würde die (nicht existierende) RO_-Connection
      // ansprechen und damit „No available connection“ werfen.
      readonly: false,
      statement:
        'SELECT id, label, amount_cents AS amountCents, created_at_utc AS createdAtUtc FROM prototype_entries ORDER BY created_at_utc DESC, id DESC',
      // values ist im Plugin-Vertrag Pflichtfeld, auch ohne Parameter:
      // Android prüft `has("values")` und verwirft den Aufruf sonst mit
      // „Query: Must provide an Array of Strings“ (Gerätebefund).
      values: [],
    });
    return mapRows<PrototypeEntry>(result.values ?? []);
  }

  /**
   * Stabile Geräte-ID liefern (Kopplungsgrundlage): einmalig erzeugt
   * und in der Meta-Tabelle persistiert; bei jedem Neustart dieselbe.
   */
  async getDeviceId(): Promise<string> {
    const existing = await this.getMeta('device_id');
    if (existing) {
      return existing;
    }
    const id = newId();
    await this.setMeta('device_id', id);
    return id;
  }

  async getMeta(key: string): Promise<string | null> {
    const result = await CapacitorSQLite.query({
      database: SqliteService.DbName,
      readonly: false, // siehe listEntries(): nur RW-Verbindung vorhanden
      statement: 'SELECT value FROM meta WHERE key = ?',
      values: [key],
    });
    const rows = mapRows<Record<string, unknown>>(result.values ?? []);
    const value = rows[0]?.['value'];
    return typeof value === 'string' ? value : null;
  }

  async setMeta(key: string, value: string): Promise<void> {
    await CapacitorSQLite.run({
      database: SqliteService.DbName,
      statement: 'INSERT INTO meta (key, value) VALUES (?, ?) ON CONFLICT(key) DO UPDATE SET value = excluded.value',
      values: [key, value],
      readonly: false,
    });
  }
}

/**
 * UUIDv4 erzeugen. `crypto.randomUUID()` steht im Android-WebView
 * (HTTPS-Kontext) zur Verfügung; der Fallback nutzt
 * `crypto.getRandomValues` und ist dadurch auf jedem Pfad verfügbar.
 */
function newId(): string {
  const cryptoObj = globalThis.crypto;
  if (cryptoObj?.randomUUID) {
    return cryptoObj.randomUUID();
  }
  const bytes = new Uint8Array(16);
  cryptoObj.getRandomValues(bytes);
  bytes[6] = (bytes[6] & 0x0f) | 0x40; // Version 4
  bytes[8] = (bytes[8] & 0x3f) | 0x80; // Variant 10
  const hex = Array.from(bytes, (b) => b.toString(16).padStart(2, '0')).join('');
  return `${hex.slice(0, 8)}-${hex.slice(8, 12)}-${hex.slice(12, 16)}-${hex.slice(16, 20)}-${hex.slice(20)}`;
}

/**
 * Ergebniszeilen des Plugins normalisieren: Auf iOS ist die erste
 * Zeile die Spaltennamen-Liste; auf Android sind es Zeilenobjekte.
 */
function mapRows<T>(values: unknown[]): T[] {
  let rows = values;
  if (rows.length > 0 && Array.isArray(rows[0])) {
    rows = rows.slice(1);
  }
  return rows as T[];
}
