// @vitest-environment jsdom
import { TestBed } from '@angular/core/testing';
import { CapacitorSQLite } from '@capacitor-community/sqlite';
import { capacitorSqliteMockFactory, dbState, type CapacitorSqliteMock } from './capacitor-sqlite-fake';
import { LocalDatabase } from './local-database';
import { MetaService } from './meta.service';
import { SCHEMA_STATEMENTS } from './statements';

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

describe('LocalDatabase (Batch A, simuliertes Native-Backend)', () => {
  beforeEach(() => {
    dbState.reset();
  });

  describe('init-Lifecycle', () => {
    it('legt eine RW-Connection an und fährt FK-Pragma (ohne Transaktion) + Verifikation + Schema-Batch', async () => {
      const db = new LocalDatabase();
      expect(db.isNative).toBe(true);
      expect(db.ready).toBe(false);
      // Vor init() anlegen: der PRAGMA-Query-Verifikationsaufruf muss
      // protokolliert sein (D2a).
      const querySpy = vi.spyOn(CapacitorSQLite, 'query');

      await db.init();

      // createConnection genau einmal (Guard), nur die RW-Connection:
      expect([...dbState.connections.keys()]).toEqual(['RW_lario']);
      expect(dbState.connections.get('RW_lario')?.opened).toBe(true);
      // ready erst am Ende:
      expect(db.ready).toBe(true);

      // Batch 1 (D2a): FK-Pragma AUSSERHALB einer Transaktion
      // (innerhalb wäre es wirkungslos) + values-Pflicht (P1).
      const pragmaBatch = dbState.executedSets[0];
      expect(pragmaBatch.database).toBe(LocalDatabase.DbName);
      expect(pragmaBatch.readonly).toBe(false);
      expect(pragmaBatch.transaction).toBe(false);
      expect(pragmaBatch.sets).toEqual([
        { statement: 'PRAGMA foreign_keys = ON', values: [] },
      ]);
      // Wirksamer FK-Zustand: aktiv (Fake spiegelt Android: open()
      // aktiviert FK; das Pragma bestätigt außerhalb der Transaktion).
      expect(dbState.foreignKeys).toBe(true);

      // Verifikation des wirksamen Zustands via PRAGMA-Query (D2a):
      expect(querySpy).toHaveBeenCalledWith({
        database: LocalDatabase.DbName,
        statement: 'PRAGMA foreign_keys',
        values: [],
        readonly: false,
      });

      // Batch 2: komplettes Schema in EINEM transaktionalen Batch;
      // JEDES Element trägt ein values-Array an der Plugin-Grenze (P1).
      const schemaBatch = dbState.executedSets[1];
      expect(dbState.executedSets).toHaveLength(2);
      expect(schemaBatch.database).toBe(LocalDatabase.DbName);
      expect(schemaBatch.readonly).toBe(false);
      expect(schemaBatch.transaction).toBe(true);
      for (const statement of SCHEMA_STATEMENTS) {
        expect(schemaBatch.sets.map((s) => s.statement)).toContain(statement);
      }
      expect(schemaBatch.sets).toHaveLength(SCHEMA_STATEMENTS.length);
      for (const set of schemaBatch.sets) {
        expect(Array.isArray(set.values)).toBe(true);
      }
      expect(dbState.schemaStatementsExecuted).toBe(true);
    });

    it('P1-Regression: executeSet ohne values liefert an der Plugin-Grenze values: []', async () => {
      const db = new LocalDatabase();
      await db.init();

      // Parameterloses Set (Review-Forderung): ohne Normalisierung
      // würde das Android-Plugin „No value for values“ werfen.
      await db.executeSet([{ statement: 'DELETE FROM meta' }]);

      const last = dbState.executedSets.at(-1)!;
      expect(last.sets).toEqual([
        { statement: 'DELETE FROM meta', values: [] },
      ]);
    });

    it('P2: parallele init()-Aufrufe teilen sich eine Initialisierungs-Kette', async () => {
      const db = new LocalDatabase();
      let release!: () => void;
      const gate = new Promise<void>((resolve) => (release = resolve));
      const originalCreate = CapacitorSQLite.createConnection;
      const createSpy = vi
        .spyOn(CapacitorSQLite, 'createConnection')
        .mockImplementation(async (options) => {
          // Gate: der erste createConnection-Aufruf hängt, bis
          // release() — der zweite init()-Aufrufer muss auf das
          // gemeinsame Promise warten statt neu zu starten.
          await gate;
          return originalCreate.call(CapacitorSQLite, options);
        });

      const p1 = db.init();
      const p2 = db.init();

      // Beide Aufrufe laufen; genau EIN createConnection ist in Fluge —
      // ohne das gemeinsame Promise würde der zweite Aufruf „already
      // exists“ werfen.
      await vi.waitFor(() => expect(createSpy).toHaveBeenCalledTimes(1));
      expect(createSpy).toHaveBeenCalledTimes(1);

      release();
      await expect(p1).resolves.toBeUndefined();
      await expect(p2).resolves.toBeUndefined();

      expect(createSpy).toHaveBeenCalledTimes(1);
      expect(db.ready).toBe(true);
      // FK-Batch + Schema-Batch je genau einmal (kein doppelter Lauf):
      expect(dbState.executedSets).toHaveLength(2);
    });

    it('P2: nach fehlgeschlagenem init() retryt der nächste Aufruf ohne doppelte Connection', async () => {
      const db = new LocalDatabase();
      const createSpy = vi.spyOn(CapacitorSQLite, 'createConnection');
      // open() schlägt beim ersten Versuch fehl (createConnection
      // war bereits erfolgreich).
      vi.spyOn(CapacitorSQLite, 'open').mockImplementationOnce(async () => {
        throw new Error('Simulierter Öffnungsfehler');
      });

      await expect(db.init()).rejects.toThrow('Simulierter Öffnungsfehler');
      expect(db.ready).toBe(false);

      // Retry: die bereits angelegte Connection wird WIEDERVERWENDET
      // (kein zweites createConnection → kein „already exists").
      await db.init();
      expect(db.ready).toBe(true);
      expect(createSpy).toHaveBeenCalledTimes(1);
      expect(dbState.executedSets).toHaveLength(2);
    });

    it('ist idempotent: zweites init() ruft createConnection nicht erneut auf', async () => {
      const db = new LocalDatabase();
      await db.init();
      // Ohne Guard würde createConnection() „already exists“ werfen.
      await db.init();
      expect(db.ready).toBe(true);
      expect([...dbState.connections.keys()]).toEqual(['RW_lario']);
      // Nur der erste init() hat die Batches ausgeführt:
      expect(dbState.executedSets).toHaveLength(2);
    });

    it('übersteht einen simulierten Neustart: neue Instanz, neues Register, Daten bleiben', async () => {
      const first = new LocalDatabase();
      await first.init();
      const metaBefore = TestBed.runInInjectionContext(() => new MetaService());
      await metaBefore.set('device_id', 'abc-123');

      // „App-Neustart“: neuer Prozess (leeres Register), dieselbe DB-Datei.
      dbState.simulateRestart();

      const second = new LocalDatabase();
      await second.init();
      expect(second.ready).toBe(true);
      expect([...dbState.connections.keys()]).toEqual(['RW_lario']);
      const metaAfter = TestBed.runInInjectionContext(() => new MetaService());
      expect(await metaAfter.get('device_id')).toBe('abc-123');
    });
  });

  describe('executeSet-Wrapper', () => {
    it('normalisiert jedes Element auf ein values-Array (P1) und übergibt readonly:false, transaction:true', async () => {
      const db = new LocalDatabase();
      await db.init();
      const sets: { statement: string; values?: unknown[] }[] = [
        { statement: 'DELETE FROM meta WHERE key = ?', values: ['a'] },
        { statement: 'DELETE FROM meta WHERE key = ?' }, // ohne values
      ];
      await db.executeSet(sets);

      const last = dbState.executedSets.at(-1)!;
      expect(last.database).toBe(LocalDatabase.DbName);
      expect(last.readonly).toBe(false);
      expect(last.transaction).toBe(true);
      // An der Plugin-Grenze trägt JEDES Element ein values-Array —
      // vorhandene Werte bleiben erhalten, fehlende werden zu [].
      expect(last.sets).toEqual([
        { statement: 'DELETE FROM meta WHERE key = ?', values: ['a'] },
        { statement: 'DELETE FROM meta WHERE key = ?', values: [] },
      ]);
      expect(last.sets).not.toBe(sets); // Normalisierung kopiert
    });
  });

  describe('query-Wrapper', () => {
    it('übergibt values als Pflichtfeld (Default []) und readonly:false', async () => {
      const db = new LocalDatabase();
      await db.init();
      const spy = vi.spyOn(CapacitorSQLite, 'query');

      await db.query('SELECT value FROM meta WHERE key = ?', ['x']);
      expect(spy).toHaveBeenLastCalledWith({
        database: LocalDatabase.DbName,
        statement: 'SELECT value FROM meta WHERE key = ?',
        values: ['x'],
        readonly: false,
      });

      await db.query('SELECT value FROM meta WHERE key = ?', []);
      expect(spy).toHaveBeenLastCalledWith({
        database: LocalDatabase.DbName,
        statement: 'SELECT value FROM meta WHERE key = ?',
        values: [],
        readonly: false,
      });

      await db.query('SELECT value FROM meta WHERE key = ?');
      expect(spy).toHaveBeenLastCalledWith({
        database: LocalDatabase.DbName,
        statement: 'SELECT value FROM meta WHERE key = ?',
        values: [],
        readonly: false,
      });
    });

    it('normalisiert iOS-Zeilen (erste Zeile = Spaltennamen) via mapRows', async () => {
      const db = new LocalDatabase();
      await db.init();
      const iosRows = [
        ['value'],
        { value: 'a' },
        { value: 'b' },
      ];
      vi.spyOn(CapacitorSQLite, 'query').mockResolvedValueOnce({ values: iosRows });

      await expect(db.query('SELECT value FROM meta')).resolves.toEqual([
        { value: 'a' },
        { value: 'b' },
      ]);
    });

    it('normalisiert Android-Zeilen (Zeilenobjekte ohne Kopfzeile) via mapRows', async () => {
      const db = new LocalDatabase();
      await db.init();
      const androidRows = [
        { value: 'a' },
        { value: 'b' },
      ];
      vi.spyOn(CapacitorSQLite, 'query').mockResolvedValueOnce({ values: androidRows });

      await expect(db.query('SELECT value FROM meta')).resolves.toEqual(androidRows);
    });
  });
});

describe('MetaService (Batch A, simuliertes Native-Backend)', () => {
  let db: LocalDatabase;
  let meta: MetaService;

  beforeEach(async () => {
    dbState.reset();
    db = new LocalDatabase();
    await db.init();
    // MetaService holt LocalDatabase über inject() → Konstruktion im
    // DI-Kontext. Die Instanz teilt sich den (geöffneten) Fake-State.
    meta = TestBed.runInInjectionContext(() => new MetaService());
  });

  it('setzt und liest einen Wert (Roundtrip)', async () => {
    await meta.set('device_id', 'abc-123');
    expect(await meta.get('device_id')).toBe('abc-123');
  });

  it('ersetzt einen bestehenden Wert (Upsert)', async () => {
    await meta.set('host_url', 'http://a:8080');
    await meta.set('host_url', 'http://b:8443');
    expect(await meta.get('host_url')).toBe('http://b:8443');
  });

  it('liefert null für nicht existente Keys und nach delete()', async () => {
    expect(await meta.get('fehlend')).toBeNull();

    await meta.set('fehlend', 'wert');
    expect(await meta.get('fehlend')).toBe('wert');

    await meta.delete('fehlend');
    expect(await meta.get('fehlend')).toBeNull();
  });
});
