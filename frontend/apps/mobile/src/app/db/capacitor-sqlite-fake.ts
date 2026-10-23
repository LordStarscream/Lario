import type { SqlSet } from './statements';

/**
 * Teilbare In-Memory-Fake der Plugin-Grenze `@capacitor-community/sqlite`
 * (8.1.1) für alle Specs unter `app/db/` (Schritt 8, Batch A).
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
 *   „No available connection for database …“.
 * - `executeSet()` läuft auf der RW_-Connection und ist
 *   ALLES-ODER-NICHTS (Designpunkt c): Alle Sets werden erst geprüft,
 *   dann angewendet — ein Fehler verändert KEINEN Teil des Batches und
 *   wirft (Plugin-Vertrag: executeSet läuft standardmäßig in einer
 *   Transaction, transaction: true).
 * - `query()`/`run()` werfen ohne das Pflichtfeld `values` (auch wenn
 *   es ein leeres Array wäre), wie das echte Plugin auf Android.
 *
 * Batch-A-Tabellenspeicher: nur `meta` (Map) plus eine Liste der
 * erfolgreich ausgeführten executeSet-Batches (für Assertions). Die
 * Buchungs-/Outbox-Tabellen kommen in Batch B.
 */

/** Erfolgreich ausgeführter executeSet-Batch (für Assertions in den Specs). */
export interface FakeExecuteSetBatch {
  database: string;
  readonly?: boolean;
  sets: SqlSet[];
}

/** Gemeinsamer Fake-State (über `vi.hoisted` auch im Mock verfügbar). */
export interface CapacitorSqliteFakeState {
  /** Prozessweites Verbindungsregister des Plugins (dbDict). */
  connections: Map<string, { opened: boolean }>;
  /** Batch-A-Tabellenspeicher: Meta-Keys/Werte. */
  meta: Map<string, string>;
  /** Alle erfolgreich ausgeführten executeSet-Batches, in Reihenfolge. */
  executedSets: FakeExecuteSetBatch[];
  /** true, nachdem mindestens ein CREATE TABLE … IF NOT EXISTS lief. */
  schemaStatementsExecuted: boolean;
  /**
   * Simuliert einen App-Neustart: der Prozess (und damit das
   * Verbindungsregister) startet leer, die SQLite-Datei (Daten) bleibt
   * erhalten.
   */
  simulateRestart(): void;
  /** Leert Verbindungsregister, Meta-Daten und Batch-Historie. */
  reset(): void;
}

export const dbState: CapacitorSqliteFakeState = vi.hoisted(() => {
  const state = {
    connections: new Map<string, { opened: boolean }>(),
    meta: new Map<string, string>(),
    // Nur Typ-Referenz (wird zur Laufzeit eliminiert) — kein
    // Hoisting-Problem mit vi.hoisted.
    executedSets: [] as FakeExecuteSetBatch[],
    schemaStatementsExecuted: false,
    simulateRestart() {
      state.connections.clear();
    },
    reset() {
      state.connections.clear();
      state.meta.clear();
      state.executedSets.length = 0;
      state.schemaStatementsExecuted = false;
    },
  };
  return state;
});

/**
 * Einzelnes Set evaluieren und in den Fake-State anwenden. Wirft bei
 * nicht unterstützten (Batch A kennt nur Meta + Schema + Pragma) oder
 * ungültigen Statements — executeSet nutzt das für die Prüfphase.
 */
function applySet(
  state: Pick<CapacitorSqliteFakeState, 'meta' | 'schemaStatementsExecuted'>,
  set: SqlSet,
): void {
  const { statement, values = [] } = set;
  if (statement.includes('PRAGMA foreign_keys')) {
    // Das echte Plugin setzt das Pragma nicht — Batch A führt es aus.
    // Kein State nötig; die Specs asserten das Statement im Batch.
    return;
  }
  if (statement.includes('CREATE TABLE IF NOT EXISTS')) {
    state.schemaStatementsExecuted = true;
    return;
  }
  if (statement.includes('INSERT INTO meta') && statement.includes('ON CONFLICT')) {
    const [key, value] = values as [string, string];
    state.meta.set(key, String(value));
    return;
  }
  if (statement.includes('DELETE FROM meta')) {
    state.meta.delete(String(values[0]));
    return;
  }
  throw new Error(
    `capacitor-sqlite-fake: executeSet unterstützt das Statement nicht (Batch A): ${statement}`,
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
  executeSet(options: { database: string; set: SqlSet[]; readonly?: boolean }): Promise<void>;
  query(options: { database: string; statement: string; values?: unknown[]; readonly?: boolean }): Promise<{ values: unknown[] }>;
  run(options: { database: string; statement: string; values?: unknown[]; readonly?: boolean }): Promise<void>;
}

/**
 * Mock-Factory für `vi.mock('@capacitor-community/sqlite', …)`.
 * Von den Specs importiert und direkt an `vi.mock` übergeben.
 */
export function capacitorSqliteMockFactory(): { CapacitorSQLite: CapacitorSqliteMock } {
  return {
    CapacitorSQLite: {
      async createConnection(options: { database: string; readonly?: boolean }) {
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
      },
      /**
       * ALLES-ODER-NICHTS (Designpunkt c): Snapshot, dann alle Sets
       * anwenden; bei einem Fehler Snapshot wiederherstellen und
       * werfen — der Batch hat nichts verändert.
       */
      async executeSet(options: {
        database: string;
        set: SqlSet[];
        readonly?: boolean;
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
        }
        // Anwendungsphase mit Snapshot für die Rückabwicklung.
        const metaBefore = new Map(dbState.meta);
        const schemaBefore = dbState.schemaStatementsExecuted;
        try {
          for (const set of options.set) {
            applySet(dbState, set);
          }
        } catch (error) {
          dbState.meta = metaBefore;
          dbState.schemaStatementsExecuted = schemaBefore;
          throw error;
        }
        dbState.executedSets.push({
          database: options.database,
          readonly: options.readonly,
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
        // Batch-A-Stützfläche: nur die Meta-Abfragen.
        if (options.statement.includes('SELECT value FROM meta')) {
          const value = dbState.meta.get(String(options.values[0]));
          return { values: value === undefined ? [] : [{ value }] };
        }
        return { values: [] };
      },
      async run(options: {
        database: string;
        statement: string;
        values?: unknown[];
        readonly?: boolean;
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
        // Batch A: keine weiteren run()-Statements erwartet.
      },
    },
  };
}
