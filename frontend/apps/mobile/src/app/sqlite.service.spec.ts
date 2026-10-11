import { CapacitorSQLite } from '@capacitor-community/sqlite';
import { SqliteService } from './sqlite.service';

/**
 * In-Memory-Fake der Plugin-Grenze, der den Verbindungslifecycle des
 * nativen Plugins modelliert (`CapacitorSQLite.java` 8.1.1):
 *
 * - `createConnection()` registriert pro Prozess eine Verbindung unter
 *   `RW_<Name>` bzw. `RO_<Name>`; ein zweiter Aufruf für dieselbe
 *   Connection wirft „Connection … already exists“.
 * - `open()` öffnet nur eine zuvor registrierte Connection, sonst
 *   „No available connection for database …“.
 * - `run()` läuft immer auf `RW_<Name>`; `readonly: true` wirft
 *   „not allowed in read-only mode“.
 * - `query()` läuft auf `RO_<Name>` bei `readonly: true`, sonst auf
 *   `RW_<Name>`; fehlt die respective Connection, „No available
 *   connection for database …“.
 *
 * Die echten SQL-Statements laufen auf dem Android-Gerät; hier wird die
 * Service-Logik (Initialisierung, Einträge, Geräte-ID-Stabilität) gegen
 * den erwarteten Verbindungslifecycle und die Statements geprüft.
 */
const db = vi.hoisted(() => {
  interface Entry {
    id: string;
    label: string;
    amountCents: number | null;
    createdAtUtc: string;
  }
  const state = {
    /** Prozessweites Verbindungsregister des Plugins (dbDict). */
    connections: new Map<string, { opened: boolean }>(),
    meta: new Map<string, string>(),
    entries: new Map<string, Entry>(),
    schemaCreated: false,
    /**
     * Simuliert einen App-Neustart: der Prozess (und damit das
     * Verbindungsregister) startet leer, die SQLite-Datei (Daten)
     * bleibt erhalten.
     */
    simulateRestart() {
      state.connections.clear();
    },
    reset() {
      state.connections.clear();
      state.meta.clear();
      state.entries.clear();
      state.schemaCreated = false;
    },
  };
  return state;
});

vi.mock('@capacitor/core', () => ({
  Capacitor: { isNativePlatform: () => true },
}));

vi.mock('@capacitor-community/sqlite', () => {
  const connKey = (database: string, readonly: boolean) =>
    (readonly ? 'RO_' : 'RW_') + database;

  const requireConnection = (database: string, readonly: boolean) => {
    const conn = db.connections.get(connKey(database, readonly));
    if (!conn) {
      throw new Error(`No available connection for database ${database}`);
    }
    return conn;
  };

  return {
    CapacitorSQLite: {
      async createConnection(options: { database: string; readonly?: boolean }) {
        const key = connKey(options.database, options.readonly ?? false);
        if (db.connections.has(key)) {
          throw new Error(`Connection ${options.database} already exists`);
        }
        db.connections.set(key, { opened: false });
      },
      async open(options: { database: string; readonly?: boolean }) {
        requireConnection(options.database, options.readonly ?? false).opened = true;
      },
      async execute(options: { database: string }) {
        const conn = requireConnection(options.database, false);
        if (!conn.opened) {
          throw new Error(`database ${options.database} not opened`);
        }
        db.schemaCreated = true;
      },
      async run(options: {
        database: string;
        statement: string;
        values?: unknown[];
        readonly?: boolean;
      }) {
        // run() adressiert im Plugin immer die RW_-Connection.
        const conn = db.connections.get('RW_' + options.database);
        if (!conn) {
          throw new Error(`No available connection for database ${options.database}`);
        }
        if (options.readonly === true) {
          throw new Error('not allowed in read-only mode');
        }
        if (!conn.opened) {
          throw new Error(`database ${options.database} not opened`);
        }
        const { statement, values = [] } = options;
        if (statement.includes('INSERT INTO prototype_entries')) {
          const [id, label, amountCents, createdAtUtc] = values as [
            string,
            string,
            number | null,
            string,
          ];
          db.entries.set(id, { id, label, amountCents, createdAtUtc });
        } else if (statement.includes('INSERT INTO meta')) {
          const [key, value] = values as [string, string];
          db.meta.set(key, value);
        }
      },
      async query(options: {
        database: string;
        statement: string;
        values?: unknown[];
        readonly?: boolean;
      }) {
        const readonly = options.readonly ?? false;
        const conn = requireConnection(options.database, readonly);
        if (!conn.opened) {
          throw new Error(`database ${options.database} not opened`);
        }
        const { statement, values } = options;
        if (statement.includes('FROM meta')) {
          const value = db.meta.get((values?.[0] as string) ?? '');
          return { values: value === undefined ? [] : [{ value }] };
        }
        if (statement.includes('FROM prototype_entries')) {
          const rows = [...db.entries.values()].sort(
            (a, b) => b.createdAtUtc.localeCompare(a.createdAtUtc) || b.id.localeCompare(a.id),
          );
          return { values: rows };
        }
        return { values: [] };
      },
    },
  };
});

function newService() {
  return new SqliteService();
}

describe('SqliteService (Prototyp, simuliertes Native-Backend)', () => {
  beforeEach(() => {
    db.reset();
  });

  it('initialisiert die Datenbank: RW-Connection anlegen, öffnen, Schema anlegen', async () => {
    const service = newService();
    expect(service.isNative).toBe(true);
    await service.init();
    expect(service.ready).toBe(true);
    expect(db.connections.get('RW_lario-prototype')?.opened).toBe(true);
    expect(db.schemaCreated).toBe(true);
  });

  it('ist idempotent: zweites init() in derselben Session schlägt nicht fehl', async () => {
    const service = newService();
    await service.init();
    // Ohne Guard würde createConnection() „already exists“ werfen.
    await service.init();
    expect(service.ready).toBe(true);
  });

  it('modelliert den Plugin-Vertrag: open() ohne createConnection() schlägt fehl', async () => {
    // Das native Register startet pro Prozess leer; open() allein
    // kann keine Verbindung erzeugen.
    await expect(
      CapacitorSQLite.open({ database: 'lario-prototype' }),
    ).rejects.toThrow('No available connection for database lario-prototype');
  });

  it('modelliert den Plugin-Vertrag: RO-Query ohne RO-Connection schlägt fehl', async () => {
    const service = newService();
    await service.init(); // legt nur die RW-Connection an
    // readonly: true würde RO_lario-prototype adressieren — existiert nicht.
    // Genau deshalb nutzt der Service readonly: false.
    await expect(
      CapacitorSQLite.query({
        database: 'lario-prototype',
        readonly: true,
        statement: 'SELECT value FROM meta WHERE key = ?',
      }),
    ).rejects.toThrow('No available connection for database lario-prototype');
  });

  it('speichert einen Eintrag und liest ihn wieder (Roundtrip)', async () => {
    const service = newService();
    await service.init();
    const saved = await service.saveEntry('Prototyp-Eintrag', 1250);
    expect(saved.label).toBe('Prototyp-Eintrag');
    expect(saved.amountCents).toBe(1250);
    expect(saved.id).toMatch(/^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[0-9a-f]{4}-[0-9a-f]{12}$/);
    expect(new Date(saved.createdAtUtc).toISOString()).toBe(saved.createdAtUtc);

    const listed = await service.listEntries();
    expect(listed).toEqual([saved]);
  });

  it('liefert Einträge neueste zuerst', async () => {
    const service = newService();
    await service.init();
    const first = await service.saveEntry('Erster', null);
    await new Promise((resolve) => setTimeout(resolve, 5));
    const second = await service.saveEntry('Zweiter', 100);
    const listed = await service.listEntries();
    expect(listed.map((e) => e.id)).toEqual([second.id, first.id]);
  });

  it('behält die Geräte-ID über einen simulierten Neustart (neuer Prozess) bei', async () => {
    const before = newService();
    await before.init();
    const id1 = await before.getDeviceId();
    expect(id1).toMatch(/^[0-9a-f-]{36}$/);

    // "App-Neustart": neuer Prozess (leeres Verbindungsregister),
    // aber dieselbe SQLite-Datei (Daten bleiben erhalten).
    db.simulateRestart();

    const after = newService();
    await after.init(); // legt die RW-Connection neu an und öffnet sie
    const id2 = await after.getDeviceId();
    expect(id2).toBe(id1);
  });

  it('ersetzt Meta-Werte bei erneuter Angabe (Upsert)', async () => {
    const service = newService();
    await service.init();
    await service.setMeta('host_url', 'http://a:8080');
    await service.setMeta('host_url', 'http://b:8443');
    expect(await service.getMeta('host_url')).toBe('http://b:8443');
    expect(await service.getMeta('nicht-angelegt')).toBeNull();
  });
});
