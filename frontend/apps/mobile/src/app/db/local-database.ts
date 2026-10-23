import { Injectable } from '@angular/core';
import { Capacitor } from '@capacitor/core';
import { CapacitorSQLite } from '@capacitor-community/sqlite';
import { SCHEMA_STATEMENTS, type SqlSet } from './statements';

/**
 * Lokale SQLite-Speicherschicht (Schritt 8, Batch A: Speicherfundament).
 *
 * Verbindungslifecycle (Plugin-Vertrag, `CapacitorSQLite.java` 8.1.1):
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
 * `PRAGMA foreign_keys = ON` muss die App selbst setzen (Design D2a):
 * Das Plugin setzt das Pragma nicht; ohne es bleiben FOREIGN-KEY- und
 * ON DELETE CASCADE-Definitionen inaktiv.
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
   * true, sobald die RW-Verbindung im (prozessweiten) Plugin-Register
   * angelegt wurde. Siehe Klassen-Doku: `createConnection()` darf pro
   * Prozess genau einmal aufgerufen werden.
   */
  private connectionCreated = false;

  /**
   * DB öffnen und Schema idempotent anlegen (Neustart-sicher).
   * Pro App-Start: erst RW-`createConnection()`, dann `open()`.
   *
   * `ready` wird nur am Ende gesetzt: Schlägt ein Schritt fehl,
   * propagiert der Fehler und `ready` bleibt false — ein erneuter
   * Aufruf setzt an der ersten fehlgeschlagenen Stelle ein, ohne die
   * Verbindung doppelt anzulegen.
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
        database: LocalDatabase.DbName,
        // Nur die RW-Connection wird je angelegt (siehe Klassen-Doku).
        readonly: false,
      });
      this.connectionCreated = true;
    }
    await CapacitorSQLite.open({
      database: LocalDatabase.DbName,
      readonly: false,
    });
    // Pragma + komplettes Schema in EINEM executeSet-Batch: Das Plugin
    // führt executeSet standardmäßig in einer Transaction aus
    // (transaction: true — das Feld wird bewusst NICHT gesetzt). Das
    // Schema steht dadurch atomar oder gar nicht (Design D2a).
    await CapacitorSQLite.executeSet({
      database: LocalDatabase.DbName,
      readonly: false,
      set: [
        { statement: 'PRAGMA foreign_keys = ON' },
        ...SCHEMA_STATEMENTS.map((statement): SqlSet => ({ statement })),
      ],
    });
    this.ready = true;
  }

  /**
   * Satz von SQL-Sets atomar ausführen (executeSet). `transaction` ist
   * im Plugin standardmäßig true und wird hier bewusst nie gesetzt.
   */
  async executeSet(sets: SqlSet[]): Promise<void> {
    await CapacitorSQLite.executeSet({
      database: LocalDatabase.DbName,
      set: sets,
      readonly: false, // nur die RW-Connection ist angelegt
    });
  }

  /**
   * Query ausführen und die Ergebniszeilen normalisieren (mapRows).
   * `values` ist im Plugin-Vertrag Pflichtfeld — auch ohne Parameter
   * (Android verwirft Aufrufe ohne `values`-Feld; daher Default `[]`).
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
