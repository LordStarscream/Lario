import { SqliteService } from './sqlite.service';

/**
 * In-Memory-Fake der Plugin-Grenze (open/execute/run/query).
 * Die echten SQL-Statements laufen auf dem Android-Gerät; hier wird
 * die Service-Logik (Initialisierung, Einträge, Geräte-ID-Stabilität)
 * gegen die erwarteten Statements geprüft.
 */
const db = vi.hoisted(() => {
  const meta = new Map<string, string>();
  const entries = new Map<
    string,
    { id: string; label: string; amountCents: number | null; createdAtUtc: string }
  >();
  return { meta, entries, opened: false, schemaCreated: false };
});

vi.mock('@capacitor/core', () => ({
  Capacitor: { isNativePlatform: () => true },
}));

vi.mock('@capacitor-community/sqlite', () => ({
  CapacitorSQLite: {
    async open() {
      db.opened = true;
    },
    async execute() {
      db.schemaCreated = true;
    },
    async run(options: { statement: string; values?: unknown[] }) {
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
    async query(options: { statement: string; values?: unknown[] }) {
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
}));

function newService() {
  const service = new SqliteService();
  return service;
}

describe('SqliteService (Prototyp, simuliertes Native-Backend)', () => {
  beforeEach(async () => {
    db.meta.clear();
    db.entries.clear();
    db.opened = false;
    db.schemaCreated = false;
  });

  it('initialisiert die Datenbank und legt das Schema an', async () => {
    const service = newService();
    expect(service.isNative).toBe(true);
    await service.init();
    expect(service.ready).toBe(true);
    expect(db.opened).toBe(true);
    expect(db.schemaCreated).toBe(true);
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

  it('behält die Geräte-ID über einen simulierten Neustart (neue Instanz) bei', async () => {
    const before = newService();
    await before.init();
    const id1 = await before.getDeviceId();
    expect(id1).toMatch(/^[0-9a-f-]{36}$/);

    // "App-Neustart": neue Service-Instanz, aber dieselbe Datenbank.
    const after = newService();
    await after.init();
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
