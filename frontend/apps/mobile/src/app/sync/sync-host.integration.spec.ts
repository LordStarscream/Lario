/** Optionaler Batch-C-Smoke: scripts/test-step9-sync.sh; echter Host + echte mobile SQLite. */
// @vitest-environment node
import { spawn, type ChildProcess } from 'node:child_process';
import { mkdtempSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { env } from 'node:process';
import { DatabaseSync } from 'node:sqlite';
import { Injector } from '@angular/core';
import { afterEach, describe, expect, it, vi } from 'vitest';
import {
  JournalEndpointsService, JournalWriteEndpointsService, LarioHostService,
  MasterDataEndpointsService, OpenAPI, SyncEndpointsService,
} from '../../generated';
import { BookingsRepository } from '../db/bookings.repository';
import type { LocalExpenseInput } from '../db/expense-payload';
import { LocalDatabase } from '../db/local-database';
import { MetaService } from '../db/meta.service';
import { SnapshotRepository } from '../db/snapshot.repository';
import { AUTH_META_KEYS, HostClient } from '../setup/host.client';
import { SyncConnectionLock } from './sync-connection-lock';
import { SYNC_META_KEYS, SyncService } from './sync.service';

let sqlite: DatabaseSync;
let directory: string | undefined;
let host: ChildProcess | undefined;
const realFetch = globalThis.fetch;

vi.mock('@capacitor/core', () => ({ Capacitor: { isNativePlatform: () => true } }));
vi.mock('@capacitor-community/sqlite', () => ({
  CapacitorSQLite: {
    createConnection: async () => undefined,
    open: async () => undefined,
    executeSet: async (options: {
      set: { statement: string; values: (string | number | null)[] }[];
      transaction?: boolean;
    }) => {
      if (options.transaction !== false) sqlite.exec('BEGIN');
      try {
        for (const item of options.set) sqlite.prepare(item.statement).run(...item.values);
        if (options.transaction !== false) sqlite.exec('COMMIT');
      } catch (error) {
        if (options.transaction !== false) sqlite.exec('ROLLBACK');
        throw error;
      }
    },
    query: async (options: { statement: string; values: (string | number | null)[] }) => ({
      values: sqlite.prepare(options.statement).all(...options.values),
    }),
    beginTransaction: async () => { sqlite.exec('BEGIN IMMEDIATE'); },
    commitTransaction: async () => { sqlite.exec('COMMIT'); },
    rollbackTransaction: async () => { sqlite.exec('ROLLBACK'); },
    isTransactionActive: async () => ({ result: false }),
    run: async (options: { statement: string; values: (string | number | null)[] }) => {
      sqlite.prepare(options.statement).run(...options.values);
      return { lastInsertRowId: 0, changes: 0 };
    },
  },
}));

async function stopHost(): Promise<void> {
  const running = host;
  host = undefined;
  if (!running || running.exitCode !== null || running.signalCode !== null) return;
  await new Promise<void>((resolve, reject) => {
    const force = setTimeout(() => running.kill('SIGKILL'), 2000);
    running.once('exit', () => { clearTimeout(force); resolve(); });
    running.once('error', reject);
    running.kill('SIGTERM');
  });
}

async function startHost(): Promise<void> {
  host = spawn(env['LARIO_SYNC_HOST_BINARY']!, [], {
    env: {
      ...env,
      Lario__Http__Address: '127.0.0.1',
      Lario__Http__Port: new URL(env['LARIO_SYNC_HOST']!).port,
      Lario__DataDirectory: join(directory!, 'host'),
      Lario__Auth__Username: 'sync-smoke',
      Lario__Auth__Password: 'synthetic-local-smoke-password',
    },
    stdio: 'ignore',
  });
  let spawnError: Error | undefined;
  host.once('error', (error) => { spawnError = error; });
  for (let attempt = 0; attempt < 100; attempt++) {
    if (spawnError) throw spawnError;
    if (host.exitCode !== null) throw new Error(`Testhost beendet: ${host.exitCode}`);
    try {
      if ((await realFetch(`${env['LARIO_SYNC_HOST']}/api/health`)).ok) return;
    } catch { /* Host startet noch. */ }
    await new Promise((resolve) => setTimeout(resolve, 100));
  }
  throw new Error('Testhost startet nicht innerhalb von zehn Sekunden');
}

function mobile(): Injector {
  return Injector.create({ providers: [
    LocalDatabase, MetaService, BookingsRepository, SnapshotRepository, HostClient, SyncConnectionLock, SyncService,
  ] });
}

afterEach(async () => {
  globalThis.fetch = realFetch;
  await stopHost();
  if (sqlite) sqlite.close();
  if (directory) rmSync(directory, { recursive: true, force: true });
  OpenAPI.TOKEN = undefined;
  OpenAPI.BASE = '';
});

describe.skipIf(!env['LARIO_SYNC_HOST'])('Batch C: generierter Client → echter Host', () => {
  it('überträgt 20 Offline-Ausgaben nach Neustart genau einmal und bewahrt Eingaben nach Widerruf', async () => {
    directory = mkdtempSync(join(tmpdir(), 'lario-sync-host-'));
    sqlite = new DatabaseSync(join(directory, 'mobile.db'));
    let injector = mobile();
    let repo = injector.get(BookingsRepository);
    let meta = injector.get(MetaService);
    await injector.get(LocalDatabase).init();
    await startHost();
    OpenAPI.BASE = env['LARIO_SYNC_HOST']!;
    const login = async () => {
      const response = await LarioHostService.authLogin({
        username: 'sync-smoke', password: 'synthetic-local-smoke-password',
      });
      OpenAPI.TOKEN = response.token;
      return response.token;
    };
    await login();
    const account = await MasterDataEndpointsService.createAccount({
      name: 'Smoke Giro', type: 'Bank', isInterestBearing: false,
    });
    const flex = await MasterDataEndpointsService.createPool({ name: 'Smoke Flex' });
    const leisure = await MasterDataEndpointsService.createPool({ name: 'Smoke Freizeit' });
    const category = await MasterDataEndpointsService.createCategory({ name: 'Smoke Einkauf', parentId: null });
    await JournalWriteEndpointsService.createOpeningBalance({
      id: crypto.randomUUID(), date: '2026-11-01', total: 100000, accountId: account.id,
      distribution: [{ poolId: flex.id, amount: 70000 }, { poolId: leisure.id, amount: 30000 }], note: null,
    });
    let hostClient = injector.get(HostClient);
    const setup = await hostClient.fetchMasterData(OpenAPI.BASE, 'sync-smoke',
      'synthetic-local-smoke-password', new Date());
    let device = await hostClient.pair();
    const initialBootstrap = await hostClient.bootstrap(device, setup.token);
    expect(initialBootstrap.accounts.some(item => item.id === account.id)).toBe(true);
    await meta.set(AUTH_META_KEYS.host, OpenAPI.BASE);
    await meta.set(SYNC_META_KEYS.deviceId, device.id);
    await meta.set(SYNC_META_KEYS.deviceToken, device.token);
    OpenAPI.TOKEN = device.token;
    let sync = injector.get(SyncService);
    await sync.sync();
    expect(sync.result()).toEqual({ accepted: 0, confirmed: 0, rejected: 0 });

    // Tatsächlich beendeter Host; die Erfassung verwendet ausschließlich lokale Daten.
    await stopHost();
    await expect(realFetch(`${OpenAPI.BASE}/api/health`)).rejects.toThrow();
    const input: LocalExpenseInput = {
      date: '2026-11-02', totalAmountCents: 850, accountId: account.id, note: 'Synthetischer Smoke',
      splits: [
        { id: 'flex', amountCents: 650, poolId: flex.id, categoryId: category.id, note: null, tax: null },
        { id: 'leisure', amountCents: 200, poolId: leisure.id, categoryId: category.id, note: null, tax: null },
      ],
    };
    for (let index = 0; index < 20; index++) {
      await repo.save(input, new Date(Date.UTC(2026, 10, 2, 10, 0, index)));
    }
    expect(await repo.listSyncable()).toHaveLength(20);
    const first = (await repo.listSyncable())[0];

    // Echte SQLite-Datei schließen und neue App-Services über denselben Speicher starten.
    sqlite.close();
    sqlite = new DatabaseSync(join(directory, 'mobile.db'));
    injector = mobile();
    repo = injector.get(BookingsRepository);
    meta = injector.get(MetaService);
    sync = injector.get(SyncService);
    hostClient = injector.get(HostClient);
    expect(await repo.listSyncable()).toHaveLength(20);
    await startHost();

    // Antwort erst NACH echtem HTTP-Erfolg/Servercommit verwerfen.
    let lostResponse = false;
    globalThis.fetch = async (...args: Parameters<typeof fetch>) => {
      const response = await realFetch(...args);
      const url = args[0] instanceof Request ? args[0].url : String(args[0]);
      if (url.endsWith('/api/v1/sync/push') && !lostResponse) {
        expect(response.ok).toBe(true);
        await response.text();
        lostResponse = true;
        throw new TypeError('Simulierter Antwortverlust nach Servercommit');
      }
      return response;
    };
    await sync.sync();
    expect(lostResponse).toBe(true);
    expect((await repo.listSyncable())[0]).toMatchObject({
      operationId: first.operationId, payloadHash: first.payloadHash,
    });
    expect(sqlite.prepare('SELECT status FROM outbox WHERE operation_id = ?').get(first.operationId))
      .toMatchObject({ status: 'sending' });
    globalThis.fetch = realFetch;

    // Auch nach Widerruf und neuer Kopplung greift Stufe 2 über die stabile Buchungs-ID.
    await login();
    await SyncEndpointsService.syncRevokeDevice(device.id);
    await sync.sync();
    expect(sync.message()).toContain('Gerät nicht mehr verbunden');
    expect(await repo.listSyncable()).toHaveLength(20);
    await hostClient.fetchMasterData(OpenAPI.BASE, 'sync-smoke',
      'synthetic-local-smoke-password', new Date());
    device = await hostClient.pair();
    await meta.set(SYNC_META_KEYS.deviceId, device.id);
    await meta.set(SYNC_META_KEYS.deviceToken, device.token);
    await sync.sync();
    expect(sync.result()).toEqual({ accepted: 19, confirmed: 1, rejected: 0 });
    expect(await repo.listSyncable()).toHaveLength(0);
    expect(sqlite.prepare("SELECT count(*) AS count FROM outbox WHERE status = 'confirmed'").get()).toMatchObject({ count: 20 });
    expect(sqlite.prepare('SELECT count(*) AS count FROM bookings WHERE base_revision = 1').get()).toMatchObject({ count: 20 });

    // Derselbe gefrorene Request wird zwei weitere Male über den generierten Client bestätigt.
    OpenAPI.TOKEN = device.token;
    for (let repeat = 0; repeat < 2; repeat++) {
      const response = await SyncEndpointsService.syncPush({
        operationId: first.operationId, type: first.type, payload: JSON.parse(first.payloadJson),
        payloadHash: first.payloadHash, createdAtUtc: first.createdAtUtc,
      });
      expect(response.status).toBe('already_processed');
      expect(response.ack).toBe(true);
    }
    await login();
    const bookings = await JournalEndpointsService.listBookings();
    expect(bookings.filter(booking => booking.type.toLowerCase() === 'expense')).toHaveLength(20);
    const balances = await JournalEndpointsService.getBalances();
    expect(Number(balances.totalAccountBalance)).toBe(83000);
    expect(Number(balances.totalPoolBalance)).toBe(83000);
    expect(Number(balances.pools.find(pool => pool.poolId === flex.id)!.balance)).toBe(57000);
    expect(Number(balances.pools.find(pool => pool.poolId === leisure.id)!.balance)).toBe(26000);
    await SyncEndpointsService.syncRevokeDevice(device.id);
    await repo.save(input, new Date());
    await sync.sync();
    expect(sync.message()).toContain('Gerät nicht mehr verbunden');
    expect(await repo.listSyncable()).toHaveLength(1);
    await repo.save(input, new Date());
    expect(await repo.listSyncable()).toHaveLength(2);
    expect(await meta.get(SYNC_META_KEYS.deviceToken)).toBe(device.token);
  }, 30000);
});
