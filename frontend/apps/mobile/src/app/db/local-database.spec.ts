// @vitest-environment jsdom
import { TestBed } from '@angular/core/testing';
import { CapacitorSQLite } from '@capacitor-community/sqlite';
import { capacitorSqliteMockFactory, dbState } from './capacitor-sqlite-fake';
import { LocalDatabase } from './local-database';
import { MetaService } from './meta.service';
import { SCHEMA_STATEMENTS } from './statements';

vi.mock('@capacitor/core', () => ({
  Capacitor: { isNativePlatform: () => true },
}));

vi.mock('@capacitor-community/sqlite', capacitorSqliteMockFactory);

describe('LocalDatabase (Batch A, simuliertes Native-Backend)', () => {
  beforeEach(() => {
    dbState.reset();
  });

  describe('init-Lifecycle', () => {
    it('legt genau eine RW-Connection an, öffnet sie und fährt Pragma + Schema in einem executeSet-Batch', async () => {
      const db = new LocalDatabase();
      expect(db.isNative).toBe(true);
      expect(db.ready).toBe(false);

      await db.init();

      // createConnection genau einmal (Guard), nur die RW-Connection:
      expect([...dbState.connections.keys()]).toEqual(['RW_lario']);
      expect(dbState.connections.get('RW_lario')?.opened).toBe(true);
      // ready erst am Ende:
      expect(db.ready).toBe(true);

      // Pragma (D2a) + komplettes Schema in EINEM executeSet-Batch:
      expect(dbState.executedSets).toHaveLength(1);
      const batch = dbState.executedSets[0];
      expect(batch.database).toBe(LocalDatabase.DbName);
      expect(batch.readonly).toBe(false);
      expect(batch.sets[0].statement).toBe('PRAGMA foreign_keys = ON');
      for (const statement of SCHEMA_STATEMENTS) {
        expect(batch.sets.map((s) => s.statement)).toContain(statement);
      }
      expect(batch.sets).toHaveLength(1 + SCHEMA_STATEMENTS.length);
      expect(dbState.schemaStatementsExecuted).toBe(true);
    });

    it('ist idempotent: zweites init() ruft createConnection nicht erneut auf', async () => {
      const db = new LocalDatabase();
      await db.init();
      // Ohne Guard würde createConnection() „already exists“ werfen.
      await db.init();
      expect(db.ready).toBe(true);
      expect([...dbState.connections.keys()]).toEqual(['RW_lario']);
      // Nur der erste init() hat den Schema-Batch ausgeführt:
      expect(dbState.executedSets).toHaveLength(1);
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
    it('übergibt die Sets unverändert und mit readonly:false', async () => {
      const db = new LocalDatabase();
      await db.init();
      const sets = [
        { statement: 'DELETE FROM meta WHERE key = ?', values: ['a'] },
        { statement: 'PRAGMA foreign_keys = ON' },
      ];
      await db.executeSet(sets);

      const last = dbState.executedSets.at(-1)!;
      expect(last.database).toBe(LocalDatabase.DbName);
      expect(last.readonly).toBe(false);
      expect(last.sets).toEqual(sets);
      expect(last.sets).toBe(sets); // Wrapper kopiert nicht — Identität prüfen
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
