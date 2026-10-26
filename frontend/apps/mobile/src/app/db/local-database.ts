import { Injectable } from '@angular/core';
import { Capacitor } from '@capacitor/core';
import { CapacitorSQLite } from '@capacitor-community/sqlite';
import { SCHEMA_STATEMENTS, type SqlSet } from './statements';

/**
 * Lokale SQLite-Speicherschicht (Schritt 8, Batch A: Speicherfundament).
 *
 * Verbindungslifecycle (Plugin-Vertrag, `@capacitor-community/sqlite` 8.1.1):
 * Das Plugin hält pro Prozess ein Verbindungsregister (`dbDict`) mit den
 * Schlüsseln `RW_<Name>` / `RO_<Name>`. `open()` wirft ohne vorherige
 * `createConnection()` („No available connection for database …“). Die
 * App nutzt ausschließlich die RW-Verbindung: Es wird keine
 * `RO_`-Verbindung angelegt, daher laufen alle Aufrufe
 * (`executeSet`/`query`/`run`) mit `readonly: false` — `readonly: true`
 * würde die (nicht existierende) RO_-Connection adressieren und damit
 * „No available connection“ werfen.
 *
 * `createConnection()` wird pro Prozess genau EINMAL ausgeführt
 * (Guard `connectionCreated`): Das Register existiert pro App-Start
 * neu; ein zweiter Aufruf in derselben Session würde „already exists“
 * werfen. Nach einem Neustart startet das Register leer — der Guard
 * (eine Instanz-Variable) ist dann ebenfalls false, alles passt.
 *
 * Parallele Initialisierung (Review-Befund P2): `init()` serialisiert
 * gleichzeitige Aufrufer über ein gemeinsames Promise
 * (`initializing`) — die Kette create/open/Pragma/Schema läuft auch bei
 * mehreren parallelen `init()`-Aufrufen genau einmal. Nach einem
 * Fehlschlag wird das laufende Promise wieder freigegeben, damit ein
 * Folgeaufruf neu ansetzen kann; `connectionCreated` bleibt nur nach
 * tatsächlich erfolgreichem Create erhalten.
 *
 * Foreign Keys (Design D2a, review-korrigiert): Android 8.1.1 aktiviert
 * FK bereits beim `open()` (`Database.java`:
 * `setForeignKeyConstraintsEnabled(true)`). `PRAGMA foreign_keys = ON`
 * INNEHALB einer Transaktion ist wirkungslos (SQLite-Semantik) — das
 * frühere Pragma im transaktionalen Schema-Batch hätte auf Android
 * nichts zusätzlich bewirkt. `init()` setzt das Pragma daher explizit
 * außerhalb einer Transaktion (`transaction: false`) und verifiziert
 * den wirksamen Zustand per `PRAGMA foreign_keys`: Die Garantie wird
 * plattformunabhängig (Web/Browser-Fallback) und schlägt laut fehl,
 * falls ein Pluginwechsel die Aktivierung beim Öffnen entfernt.
 *
 * Plugin-Grenze (Review-Befund P1): Jedes `executeSet`-Element muss an
 * der Plugin-Grenze ein `values`-Array tragen — Android 8.1.1
 * (`Database.executeSet()`) liest `values` für jedes Element
 * zwingend und wirft ohne es. `toPluginSets()` normalisiert jedes
 * Element, auch parameterlose Sets (`values: []`).
 *
 * Schreib-Serialisierung (Review-Befund P2, Batch B): Alle
 * Schreiboperationen (`executeSet`, `withWriteTransaction`) werden auf
 * einer Connection-Operations-Kette (`opChain`) serialisiert. Eine
 * offene Schreibtransaktion hält die Connection über die gesamte
 * Callback-Dauer; andere Schreiboperationen warten bis nach
 * COMMIT/ROLLBACK. Leseoperationen (`query`) bleiben außerhalb der
 * Kette: Auf derselben Connection sehen sie den Zustand der offenen
 * Transaktion ohnehin.
 */
@Injectable({ providedIn: 'root' })
export class LocalDatabase {
  /** Datenbankname (Dateiname der SQLite-Datei auf dem Gerät). */
  static readonly DbName = 'lario';

  /** true nur im Android-WebView (native Plattform). */
  readonly isNative = Capacitor.isNativePlatform();

  /** true nach erfolgreicher Initialisierung (DB geöffnet, Schema angelegt). */
  ready = false;

  /**
   * true, sobald die RW-Connection im (prozessweiten) Plugin-Register
   * angelegt wurde. Siehe Klassen-Doku: `createConnection()` darf pro
   * Prozess genau einmal aufgerufen werden.
   */
  private connectionCreated = false;

  /** true, sobald die RW-Connection erfolgreich geöffnet wurde. */
  private opened = false;

  /**
   * Gemeinsames Promise der laufenden Initialisierung (Review-Befund
   * P2): Parallele `init()`-Aufrufe warten auf dasselbe Promise; die
   * Kette create/open/Pragma/Schema serialisiert darüber. Nach einem
   * Fehlschlag wird es auf null gesetzt, damit ein Retry startet.
   */
  private initializing: Promise<void> | null = null;

  /**
   * Connection-Operations-Kette (Review-Befund P2, Batch B): Alle
   * Schreiboperationen (executeSet, withWriteTransaction) werden auf
   * derselben Connection nacheinander ausgeführt — eine offene
   * Schreibtransaktion hält die Connection über die gesamte
   * Callback-Dauer; andere Schreiboperationen warten bis nach
   * COMMIT/ROLLBACK.
   */
  private opChain: Promise<unknown> = Promise.resolve();

  /**
   * DB öffnen und Schema idempotent anlegen (Neustart-sicher).
   * Pro App-Start: erst RW-`createConnection()`, dann `open()`, dann
   * FK-Pragma außerhalb einer Transaktion mit Verifikation, zuletzt
   * das Schema.
   *
   * Parallele `init()`-Aufrufe teilen sich eine Initialisierungs-Kette
   * (P2). `ready` wird nur am Ende gesetzt: Schlägt ein Schritt fehl,
   * propagiert der Fehler, `ready` bleibt false — ein erneuter Aufruf
   * setzt an der ersten fehlgeschlagenen Stelle ein, ohne die
   * Verbindung doppelt anzulegen.
   */
  init(): Promise<void> {
    if (!this.isNative || this.ready) {
      return Promise.resolve();
    }
    if (this.initializing === null) {
      this.initializing = this.doInit().catch((error: unknown) => {
        // Laufendes Promise freigeben: der nächste init()-Aufruf startet
        // eine frische Kette (connectionCreated/opened bleiben bis auf
        // die fehlgeschlagene Stelle erhalten).
        this.initializing = null;
        throw error;
      });
    }
    return this.initializing;
  }

  private async doInit(): Promise<void> {
    if (!this.connectionCreated) {
      await CapacitorSQLite.createConnection({
        database: LocalDatabase.DbName,
        // Nur die RW-Connection wird je angelegt (siehe Klassen-Doku).
        readonly: false,
      });
      this.connectionCreated = true;
    }
    if (!this.opened) {
      await CapacitorSQLite.open({
        database: LocalDatabase.DbName,
        readonly: false,
      });
      this.opened = true;
    }
    // D2a: FK außerhalb einer Transaktion aktivieren (das Pragma ist in
    // einer Transaktion wirkungslos) und den wirksamen Zustand
    // verifizieren — siehe Klassen-Doku.
    await CapacitorSQLite.executeSet({
      database: LocalDatabase.DbName,
      readonly: false,
      transaction: false,
      set: [{ statement: 'PRAGMA foreign_keys = ON', values: [] }],
    });
    const fkRows = await this.query<Record<string, unknown>>(
      'PRAGMA foreign_keys',
    );
    const fkEnabled = fkRows[0]?.['foreign_keys'];
    if (Number(fkEnabled) !== 1) {
      throw new Error(
        `LocalDatabase: FOREIGN KEY-Constraints nicht aktiv ` +
          `(PRAGMA foreign_keys = ${String(fkEnabled)})`,
      );
    }
    // Komplettes Schema in EINEM transaktionalen executeSet-Batch:
    // atomar oder gar nicht (Design D2). Jedes Element trägt ein
    // values-Array (P1).
    await CapacitorSQLite.executeSet({
      database: LocalDatabase.DbName,
      readonly: false,
      transaction: true,
      set: SCHEMA_STATEMENTS.map(
        (statement): SqlSet => ({ statement, values: [] }),
      ),
    });
    this.ready = true;
  }

  /**
   * Satz von SQL-Sets atomar ausführen (executeSet, transaction: true).
   * Jedes Element wird an der Plugin-Grenze auf ein `values`-Array
   * normalisiert (P1: Android wirft ohne es).
   *
   * Serialisiert auf der Connection-Operations-Kette (P2, Batch B):
   * Eine laufende withWriteTransaction-Transaktion hält die Connection;
   * dieser Aufruf wartet bis nach COMMIT/ROLLBACK.
   */
  async executeSet(sets: SqlSet[]): Promise<void> {
    await this.serialize(async () => {
      await CapacitorSQLite.executeSet({
        database: LocalDatabase.DbName,
        set: toPluginSets(sets),
        readonly: false, // nur die RW-Connection ist angelegt
        transaction: true,
      });
    });
  }

  /**
   * Query ausführen und die Ergebniszeilen normalisieren (mapRows).
   * `values` ist im Plugin-Vertrag Pflichtfeld — auch ohne Parameter
   * (Android verwirft Aufrufe ohne `values`-Feld; daher Default `[]`).
   *
   * Leseoperationen laufen OHNE Operations-Kette (P2, Batch B): Auf
   * derselben Connection sind die Schreibzugriffe einer offenen
   * Transaktion derselben Connection sichtbar.
   */
  async query<T>(statement: string, values: unknown[] = []): Promise<T[]> {
    const result = await CapacitorSQLite.query({
      database: LocalDatabase.DbName,
      statement,
      values,
      readonly: false, // nur die RW-Connection ist angelegt
    });
    return mapRows<T>(result.values ?? []);
  }

  /**
   * Native Schreibtransaktion (Review-Befund P2, Batch B):
   * BEGIN IMMEDIATE → Callback → COMMIT; bei Fehler ROLLBACK
   * (kein Teilzustand).
   *
   * - Die gesamte Callback-Dauer hält die Connection (Operations-Kette);
   *   andere Schreiboperationen warten bis nach COMMIT/ROLLBACK.
   * - Der Callback erhält Query-/Write-Methoden auf derselben Connection;
   *   deren Statements laufen OHNE eigene automatische Transaktion
   *   (`transaction: false`) — keine verschachtelte Transaktion.
   *   Der Callback soll die Handle-Methoden verwenden, nicht die
   *   öffentlichen executeSet/query (die würden auf ihre eigene
   *   Sperre warten → Deadlock).
   * - Init wird idempotent sichergestellt (bereits initialisiert:
   *   sofort no-op).
   */
  withWriteTransaction<T>(
    callback: (tx: LocalDatabaseTransaction) => Promise<T>,
  ): Promise<T> {
    return this.serialize(async () => {
      await this.init();
      await CapacitorSQLite.run({
        database: LocalDatabase.DbName,
        statement: 'BEGIN IMMEDIATE',
        values: [],
        readonly: false,
      });
      try {
        const result = await callback({
          query: <R>(statement: string, values?: unknown[]) =>
            this.queryInternal<R>(statement, values ?? []),
          executeSet: (sets: SqlSet[]) => this.executeSetInternal(sets),
        });
        await CapacitorSQLite.run({
          database: LocalDatabase.DbName,
          statement: 'COMMIT',
          values: [],
          readonly: false,
        });
        return result;
      } catch (error) {
        try {
          await CapacitorSQLite.run({
            database: LocalDatabase.DbName,
            statement: 'ROLLBACK',
            values: [],
            readonly: false,
          });
        } catch {
          // ROLLBACK-Scheitern verdeckt den Originalfehler nicht.
        }
        throw error;
      }
    });
  }

  /**
   * Query ohne Operations-Kette (nur innerhalb des Transaktions-
   * Callbacks verwenden).
   */
  private async queryInternal<T>(
    statement: string,
    values: unknown[],
  ): Promise<T[]> {
    const result = await CapacitorSQLite.query({
      database: LocalDatabase.DbName,
      statement,
      values,
      readonly: false,
    });
    return mapRows<T>(result.values ?? []);
  }

  /**
   * SQL-Sets ohne automatische Transaktion (transaction: false) und
   * ohne Operations-Kette ausführen (nur innerhalb des Transaktions-
   * Callbacks verwenden).
   */
  private async executeSetInternal(sets: SqlSet[]): Promise<void> {
    await CapacitorSQLite.executeSet({
      database: LocalDatabase.DbName,
      set: toPluginSets(sets),
      readonly: false,
      transaction: false,
    });
  }

  /**
   * Operation auf der Connection-Operations-Kette serialisieren
   * (P2, Batch B). Fehler brechen die Kette nicht: die nächste
   * Operation wird weiterhin gestartet (`.then(next, next)`-Form).
   */
  private serialize<T>(operation: () => Promise<T>): Promise<T> {
    const promise = this.opChain.then(operation, operation);
    // Kette weiterführen, egal ob Erfolg oder Fehler:
    this.opChain = promise.then(
      () => undefined,
      () => undefined,
    );
    return promise;
  }
}

/**
 * Transaktions-Handle (Review-Befund P2, Batch B): Query-/Write-
 * Methoden auf derselben Connection innerhalb einer offenen
 * withWriteTransaction-Transaktion. Statements laufen ohne eigene
 * automatische Transaktion (transaction: false) — keine verschachtelte
 * Transaktion.
 */
export interface LocalDatabaseTransaction {
  /** Query innerhalb der Transaktion (gleiche Connection). */
  query<T>(statement: string, values?: unknown[]): Promise<T[]>;
  /** SQL-Sets innerhalb der Transaktion ausführen (transaction: false). */
  executeSet(sets: SqlSet[]): Promise<void>;
}

/**
 * Normalisierung an der Plugin-Grenze (Review-Befund P1): Android 8.1.1
 * liest `values` bei jedem executeSet-Element zwingend —
 * parameterlose Sets erhalten ein explizites `[]`.
 */
function toPluginSets(sets: SqlSet[]): SqlSet[] {
  return sets.map((set) => ({
    statement: set.statement,
    values: set.values ?? [],
  }));
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
