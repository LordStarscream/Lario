// @vitest-environment node
import "@angular/compiler";
import { mkdtempSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { DatabaseSync } from 'node:sqlite';
import { Injector } from '@angular/core';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { ApiError, OpenAPI, SyncEndpointsService, CancelablePromise, type SyncPushRequest, type SyncPushResponse, type BootstrapResponseDto } from '../../generated';
import { BookingsRepository } from '../db/bookings.repository';
import { LocalDatabase } from '../db/local-database';
import { MetaService } from '../db/meta.service';
import { SnapshotRepository } from '../db/snapshot.repository';
import type { LocalExpenseInput } from '../db/expense-payload';
import { SyncConnectionLock } from './sync-connection-lock';
import { SyncService, SYNC_META_KEYS } from './sync.service';

let sqlite: DatabaseSync;

/** Temp-Ordner der echten SQLite-Datei (Cleanup in afterEach). */
let sqliteDir: string;

/**
 * Test-Hook: erzwingt einen Fehler auf genau diesem Statement
 * (Atomaritäts-Test: zweite Statement der Quittungs-Transaktion
 * schlägt fehl → kompletter Rollback).
 */
const failBookingRevisionUpdate = false;

vi.mock('@capacitor/core', () => ({
  Capacitor: { isNativePlatform: () => true },
}));

// node:sqlite-Backed-Adapter: die Factory referenziert `sqlite`/
// `failBookingRevisionUpdate` nur in den Methodenköpfen (Lazy-Read) —
// beim Hoisting werden die Bindungen noch nicht gelesen (kein TDZ).
vi.mock('@capacitor-community/sqlite', () => {
  interface SetItem {
    statement: string;
    values?: unknown[];
  }
  return {
    CapacitorSQLite: {
      createConnection: async () => undefined,
      open: async () => undefined,
      executeSet: async (opts: {
        set?: SetItem[];
        transaction?: boolean;
      }) => {
        const sets = (opts.set ?? []).map((s) => ({
          statement: s.statement,
          values: (s.values ?? []) as (string | number | bigint | null)[],
        }));
        const runAll = () => {
          for (const s of sets) {
            if (failBookingRevisionUpdate &&
                s.statement === 'NEVER') {
              throw new Error('Simulierter Schreibfehler (Atomaritäts-Test)');
            }
            sqlite.prepare(s.statement).run(...s.values);
          }
        };
        if (opts.transaction !== false) {
          sqlite.exec('BEGIN');
          try {
            runAll();
            sqlite.exec('COMMIT');
          } catch (error) {
            sqlite.exec('ROLLBACK');
            throw error;
          }
        } else {
          runAll();
        }
      },
      query: async (opts: { statement: string; values?: unknown[] }) => {
        const values = (opts.values ?? []) as (string | number | bigint | null)[];
        const rows = sqlite.prepare(opts.statement).all(...values) as
          Record<string, unknown>[];
        return { values: rows };
      },
      beginTransaction: async () => {
        sqlite.exec('BEGIN IMMEDIATE');
      },
      commitTransaction: async () => {
        sqlite.exec('COMMIT');
      },
      rollbackTransaction: async () => {
        try {
          sqlite.exec('ROLLBACK');
        } catch {
          // Defensiv: ohne aktive Transaktion no-op (Plugin-Vertrag).
        }
      },
      isTransactionActive: async () => ({ result: false }),
      run: async (opts: { statement: string; values?: unknown[] }) => {
        sqlite
          .prepare(opts.statement)
          .run(...((opts.values ?? []) as (string | number | bigint | null)[]));
        return { lastInsertRowId: 0, changes: 0 };
      },
    },
  };
});

const now = new Date('2026-11-19T10:00:00Z');
const input: LocalExpenseInput = {
  date: '2026-11-19', totalAmountCents: 8500, accountId: 'acc', note: null,
  splits: [{ id: '', amountCents: 8500, poolId: 'pool', categoryId: 'cat', note: null, tax: null }],
};
function bootstrap(cursor = 5, dbGeneration = 1): BootstrapResponseDto {
  return {
    dbGeneration, protocolGeneration: 1, cursor,
    accounts: [{id:'acc',name:'Giro',type:'bank',isInterestBearing:false,status:'active',baseRevision:1}],
    pools: [{id:'pool',name:'Flex',status:'active',baseRevision:1}],
    categories: [{id:'cat',name:'Alltag',parentId:null,status:'active',baseRevision:1,taxPreset:null}],
    taxCategories: [],
  };
}
function ack(request: SyncPushRequest, status: string): CancelablePromise<SyncPushResponse> {
  return new CancelablePromise(resolve=>resolve({status,bookingId:(request.payload as {id:string}).id,protocolGeneration:1,revision:1,ack:true}));
}
function httpError(status: number): ApiError {
  return new ApiError({method:'POST',url:'/push'}, {url:'/push',ok:false,status,statusText:'Fehler',body:{error:{code:'invalid_reference',message:'Kategorie archiviert'}}}, 'Fehler');
}
let db: LocalDatabase;
let repo: BookingsRepository;
let meta: MetaService;
let service: SyncService;
let lock: SyncConnectionLock;
let injector: Injector;
async function save() { return repo.save(input, now); }
async function status(bookingId: string) { return (await repo.get(bookingId))?.status; }

describe('Manuelle Synchronisation mit echter SQLite', () => {
  beforeEach(async () => {
    sqliteDir=mkdtempSync(join(tmpdir(),'lario-sync-service-'));
    sqlite=new DatabaseSync(join(sqliteDir,'mobile.db'));
    injector=Injector.create({providers:[LocalDatabase,MetaService,SnapshotRepository,BookingsRepository,SyncConnectionLock,SyncService]});
    db=injector.get(LocalDatabase); meta=injector.get(MetaService); repo=injector.get(BookingsRepository);
    service=injector.get(SyncService); lock=injector.get(SyncConnectionLock);
    await db.init();
    const data=bootstrap();
    await injector.get(SnapshotRepository).replace({accounts:new Map(data.accounts.map(x=>[x.id,x])),pools:new Map(data.pools.map(x=>[x.id,x])),categories:new Map(data.categories.map(x=>[x.id,x])),taxCategories:new Map()},now.toISOString());
    await meta.set('auth.host','http://host');
    await meta.set(SYNC_META_KEYS.deviceId,'device');
    await meta.set(SYNC_META_KEYS.deviceToken,'device-token');
    vi.spyOn(SyncEndpointsService,'syncBootstrap').mockResolvedValue(bootstrap());
    vi.spyOn(SyncEndpointsService,'syncPush').mockImplementation(request=>ack(request!,'accepted'));
    vi.spyOn(SyncEndpointsService,'syncGetChanges').mockResolvedValue({cursor:5,complete:true,truncated:false,changes:[]});
  });
  afterEach(() => { vi.restoreAllMocks(); sqlite.close(); rmSync(sqliteDir,{recursive:true,force:true}); });

  it('überträgt 20 Eingaben in stabiler Reihenfolge und quittiert atomar', async () => {
    const ids=[];
    for(let i=0;i<20;i++) ids.push(await save());
    const order=(await repo.listSyncable()).map(x=>x.operationId);
    await service.sync();
    expect(service.result()).toEqual({accepted:20,confirmed:0,rejected:0});
    expect(vi.mocked(SyncEndpointsService.syncPush).mock.calls.map(c=>c[0]?.operationId)).toEqual(order);
    for(const id of ids) expect(await status(id.bookingId)).toBe('confirmed');
    expect(sqlite.prepare('SELECT DISTINCT base_revision FROM bookings').all()).toEqual([{base_revision:1}]);
  });

  it('wiederholt unklare Sendung nach Neustart mit identischer ID und Hash', async () => {
    const id=await save();
    vi.mocked(SyncEndpointsService.syncPush).mockRejectedValueOnce(new TypeError('Netz weg'));
    await service.sync();
    expect(await status(id.bookingId)).toBe('sending');
    const first=vi.mocked(SyncEndpointsService.syncPush).mock.calls[0][0];
    vi.mocked(SyncEndpointsService.syncPush).mockImplementation(request=>ack(request!,'already_processed'));
    service=Injector.create({providers:[{provide:LocalDatabase,useValue:db},MetaService,SnapshotRepository,BookingsRepository,SyncConnectionLock,SyncService]}).get(SyncService);
    await service.sync();
    expect(vi.mocked(SyncEndpointsService.syncPush).mock.calls[1][0]).toEqual(first);
    expect(await status(id.bookingId)).toBe('confirmed');
    expect(service.result()?.confirmed).toBe(1);
  });

  it('Ablehnung 400 speichert Grund und setzt Warteschlange fort', async () => {
    const first=await save(); const second=await save();
    vi.mocked(SyncEndpointsService.syncPush).mockRejectedValueOnce(httpError(400));
    await service.sync();
    expect(await status(first.bookingId)).toBe('rejected');
    expect((await repo.get(first.bookingId))?.statusReason).toBe('Kategorie archiviert');
    expect(await status(second.bookingId)).toBe('confirmed');
  });

  it.each(['payload_mismatch', 'domain_conflict'])(
    'Konflikt %s sperrt lokale Eingabe und stoppt den Lauf',
    async code => {
      const first = await save();
      const second = await save();
      const operation = (await repo.listSyncable())[0];
      const error = new ApiError({method:'POST',url:'/push'}, {
        url:'/push',ok:false,status:409,statusText:'Conflict',
        body:{error:{code,message:'Hostbuchung existiert mit anderem Inhalt'}},
      }, 'Conflict');
      vi.mocked(SyncEndpointsService.syncPush).mockRejectedValueOnce(error);
      await service.sync();
      expect(await status(first.bookingId)).toBe('sending');
      expect(await status(second.bookingId)).toBe('queued');
      expect(SyncEndpointsService.syncPush).toHaveBeenCalledTimes(1);
      expect(SyncEndpointsService.syncGetChanges).not.toHaveBeenCalled();
      expect(service.message()).toContain('Synchronisationskonflikt');
      expect(service.message()).toContain('Geräteabstimmung');
      expect(service.result()?.rejected).toBe(0);
      await expect(repo.discard(first.bookingId)).rejects.toHaveProperty('reason','not_queued');
      await expect(repo.update(first.bookingId,input,now)).rejects.toHaveProperty('reason','not_queued');
      const retained = (await repo.listSyncable())[0];
      expect(retained.operationId).toBe(operation.operationId);
      expect(retained.payloadHash).toBe(operation.payloadHash);
      expect(retained.payloadJson).toBe(operation.payloadJson);
    },
  );

  it('Widerruf stoppt Versand; neue Offline-Erfassung bleibt möglich', async () => {
    const id=await save();
    vi.mocked(SyncEndpointsService.syncPush).mockRejectedValueOnce(httpError(401));
    await service.sync();
    expect(await status(id.bookingId)).toBe('sending');
    expect(service.message()).toContain('Einrichtung erneut');
    expect(await save()).toHaveProperty('bookingId');
  });

  it('quittiert fehlendes ACK nicht', async () => {
    const id=await save();
    vi.mocked(SyncEndpointsService.syncPush).mockResolvedValueOnce({status:'accepted',bookingId:id.bookingId,protocolGeneration:1,revision:1,ack:false});
    await service.sync();
    expect(await status(id.bookingId)).toBe('sending');
    expect(SyncEndpointsService.syncGetChanges).not.toHaveBeenCalled();
  });

  it('Generationwechsel stoppt vor Push und bewahrt alten Stand', async () => {
    await meta.set(SYNC_META_KEYS.bootstrapGeneration,'1'); await meta.set(SYNC_META_KEYS.protocolGeneration,'1'); await meta.set(SYNC_META_KEYS.cursor,'4');
    const id=await save();
    vi.mocked(SyncEndpointsService.syncBootstrap).mockResolvedValueOnce(bootstrap(0,2));
    await service.sync();
    expect(SyncEndpointsService.syncPush).not.toHaveBeenCalled();
    expect(await status(id.bookingId)).toBe('queued');
    expect(await meta.get(SYNC_META_KEYS.bootstrapGeneration)).toBe('1');
    expect(await meta.get(SYNC_META_KEYS.cursor)).toBe('4');
    expect(service.message()).toContain('Geräteabstimmung');
  });

  it('paginiert mit letzter ID und speichert Cursor erst nach vollständigem Pull', async () => {
    vi.mocked(SyncEndpointsService.syncGetChanges)
      .mockResolvedValueOnce({cursor:20,complete:false,truncated:true,changes:[{id:8,entityType:'booking',entityId:'b',revision:1}]})
      .mockImplementationOnce(()=>new CancelablePromise(resolve=>{void meta.get(SYNC_META_KEYS.cursor).then(cursor=>{expect(cursor).toBe('5');resolve({cursor:20,complete:true,truncated:false,changes:[{id:20,entityType:'booking',entityId:'b2',revision:1}]});});}));
    await service.sync();
    expect(SyncEndpointsService.syncGetChanges).toHaveBeenNthCalledWith(2,8,200);
    expect(await meta.get(SYNC_META_KEYS.cursor)).toBe('20');
  });

  it('Pullfehler erhält bestätigte Eingaben und vorherigen Cursor', async () => {
    const id=await save();
    vi.mocked(SyncEndpointsService.syncGetChanges).mockRejectedValueOnce(new TypeError('Netz weg'));
    await service.sync();
    expect(await status(id.bookingId)).toBe('confirmed');
    expect(await meta.get(SYNC_META_KEYS.cursor)).toBe('5');
    expect(service.result()?.accepted).toBe(1);
  });

  it('ersetzt Stammdaten und Cursor gemeinsam; offene Eingaben bleiben erhalten', async () => {
    const id=await save();
    vi.mocked(SyncEndpointsService.syncPush).mockRejectedValueOnce(httpError(400));
    vi.mocked(SyncEndpointsService.syncGetChanges).mockResolvedValueOnce({cursor:8,complete:true,truncated:false,changes:[{id:8,entityType:'category',entityId:'cat',revision:2}]});
    const updated=bootstrap(9); updated.categories[0].name='Neu';
    vi.mocked(SyncEndpointsService.syncBootstrap).mockResolvedValueOnce(bootstrap()).mockResolvedValueOnce(updated);
    await service.sync();
    expect((await injector.get(SnapshotRepository).loadSnapshot())?.categories.get('cat')?.name).toBe('Neu');
    expect(await meta.get(SYNC_META_KEYS.cursor)).toBe('9');
    expect(await status(id.bookingId)).toBe('rejected');
  });

  it('Snapshot- und Cursor-Schreibfehler rollen gemeinsam zurück', async () => {
    await meta.set(SYNC_META_KEYS.cursor,'4');
    sqlite.exec("CREATE TRIGGER reject_cursor BEFORE UPDATE ON meta WHEN NEW.key='sync.cursor' BEGIN SELECT RAISE(ABORT,'Schreibfehler'); END");
    const data=bootstrap(); data.categories[0].name='Nicht übernehmen';
    vi.mocked(SyncEndpointsService.syncBootstrap).mockResolvedValueOnce(data);
    await service.sync();
    expect((await injector.get(SnapshotRepository).loadSnapshot())?.categories.get('cat')?.name).toBe('Alltag');
    expect(await meta.get(SYNC_META_KEYS.cursor)).toBe('4');
    expect(SyncEndpointsService.syncPush).not.toHaveBeenCalled();
  });

  it('respektiert Einrichtungssperre und restauriert Client-Konfiguration', async () => {
    OpenAPI.BASE='vorher'; OpenAPI.TOKEN='session'; lock.tryAcquire();
    await service.sync(); expect(SyncEndpointsService.syncBootstrap).not.toHaveBeenCalled();
    lock.release(); await service.sync();
    expect(OpenAPI.BASE).toBe('vorher'); expect(OpenAPI.TOKEN).toBe('session'); expect(lock.busy()).toBe(false);
  });
});
