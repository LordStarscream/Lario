import { Injectable, inject, signal } from '@angular/core';
import { ApiError, OpenAPI, SyncEndpointsService, type BootstrapResponseDto } from '../../generated';
import { BookingsRepository, BookingStateError } from '../db/bookings.repository';
import { LocalDatabase } from '../db/local-database';
import { MetaService } from '../db/meta.service';
import { SnapshotRepository } from '../db/snapshot.repository';
import { upsertMetaSet } from '../db/statements';
import { AUTH_META_KEYS } from '../setup/host.client';
import { SyncConnectionLock } from './sync-connection-lock';

export const SYNC_META_KEYS = {
  deviceId: 'sync.device_id',
  deviceToken: 'sync.device_token',
  cursor: 'sync.cursor',
  protocolGeneration: 'sync.protocol_generation',
  bootstrapGeneration: 'sync.bootstrap_generation',
} as const;

export interface SyncResult {
  accepted: number;
  confirmed: number;
  rejected: number;
}

/** Nur sichere Ganzzahlen akzeptieren; Cursor/Revisionswerte nie still runden. */
function integer(value: number | string, min = 0): number {
  const number = typeof value === 'string' && /^\d+$/.test(value) ? Number(value) : value;
  if (typeof number !== 'number' || !Number.isSafeInteger(number) || number < min) {
    throw new Error('Ungültiger Synchronisationsstand des Hosts');
  }
  return number;
}

@Injectable({ providedIn: 'root' })
export class SyncService {
  private readonly db = inject(LocalDatabase);
  private readonly meta = inject(MetaService);
  private readonly bookings = inject(BookingsRepository);
  private readonly snapshots = inject(SnapshotRepository);
  private readonly lock = inject(SyncConnectionLock);

  readonly busy = signal(false);
  readonly message = signal<string | null>(null);
  readonly result = signal<SyncResult | null>(null);
  readonly configured = signal(false);
  readonly pending = signal(0);

  async refreshState(): Promise<void> {
    const [host, deviceId, token, operations] = await Promise.all([
      this.meta.get(AUTH_META_KEYS.host),
      this.meta.get(SYNC_META_KEYS.deviceId),
      this.meta.get(SYNC_META_KEYS.deviceToken),
      this.bookings.listSyncable(),
    ]);
    this.configured.set(Boolean(host && deviceId && token));
    this.pending.set(operations.length);
  }

  /** Manueller Lauf; unklare Sendungen bleiben eingefroren und werden identisch wiederholt. */
  async sync(): Promise<void> {
    if (!this.lock.tryAcquire()) return;
    this.busy.set(true);
    this.message.set('Verbindung wird geprüft …');
    this.result.set(null);
    const result: SyncResult = { accepted: 0, confirmed: 0, rejected: 0 };
    const previousBase = OpenAPI.BASE;
    const previousToken = OpenAPI.TOKEN;
    try {
      const [host, token] = await Promise.all([
        this.meta.get(AUTH_META_KEYS.host),
        this.meta.get(SYNC_META_KEYS.deviceToken),
      ]);
      if (!host || !token) throw new Error('Gerät zuerst in der Einrichtung verbinden');
      OpenAPI.BASE = host;
      OpenAPI.TOKEN = token;

      // Pull trägt keine Generationen. Deshalb VOR jedem Push einen konsistenten
      // Bootstrap prüfen. Nach Restore weder alte Quittungen noch neue Eingaben
      // automatisch gegen einen anderen Datenbestand wiederholen (Schritt 17).
      const bootstrap = await SyncEndpointsService.syncBootstrap();
      const generation = integer(bootstrap.dbGeneration, 1);
      const protocol = integer(bootstrap.protocolGeneration, 1);
      const [oldGeneration, oldProtocol] = await Promise.all([
        this.meta.get(SYNC_META_KEYS.bootstrapGeneration),
        this.meta.get(SYNC_META_KEYS.protocolGeneration),
      ]);
      if ((oldGeneration !== null && integer(oldGeneration, 1) !== generation) ||
          (oldProtocol !== null && integer(oldProtocol, 1) !== protocol)) {
        throw new Error('Datenbank- oder Protokollgeneration geändert. Synchronisation angehalten; lokale Eingaben bleiben erhalten. Geräteabstimmung erforderlich.');
      }
      await this.applyBootstrap(bootstrap);

      const operations = await this.bookings.listSyncable();
      for (const [index, operation] of operations.entries()) {
        this.message.set(`Synchronisiere ${index + 1}/${operations.length} …`);
        let sent;
        try {
          sent = await this.bookings.prepareSend(operation.operationId, new Date());
        } catch (error) {
          // Eine inzwischen lokal verworfene/geänderte Operation wird beim
          // nächsten manuellen Lauf neu gelesen; niemals den alten Inhalt senden.
          if (error instanceof BookingStateError &&
              (error.reason === 'not_found' || error.reason === 'not_queued')) continue;
          throw error;
        }
        try {
          const response = await SyncEndpointsService.syncPush({
            operationId: sent.operationId,
            type: sent.type,
            payload: JSON.parse(sent.payloadJson),
            payloadHash: sent.payloadHash,
            createdAtUtc: sent.createdAtUtc,
          });
          if (response.ack !== true || response.bookingId !== sent.bookingId ||
              !['accepted', 'already_processed'].includes(response.status) ||
              integer(response.protocolGeneration, 1) !== protocol) {
            throw new Error('Host hat die gesendete Eingabe nicht eindeutig bestätigt');
          }
          const revision = integer(response.revision, 1);
          await this.bookings.markConfirmed(sent.bookingId, sent.operationId, revision, sent.payloadHash, new Date());
          if (response.status === 'accepted') result.accepted++;
          else result.confirmed++;
        } catch (error) {
          if (error instanceof ApiError && error.status === 409) {
            // Ein Konflikt kann eine bereits bestehende Hostbuchung betreffen.
            // Den lokalen Nachweis niemals als bearbeitbar/verwerfbar freigeben.
            const body = error.body as { error?: { message?: string } } | undefined;
            throw new Error(`Synchronisationskonflikt: ${body?.error?.message || 'Hostbuchung widerspricht der lokalen Eingabe'}. Geräteabstimmung erforderlich; Eingabe bleibt gesperrt und gespeichert.`, { cause: error });
          }
          if (error instanceof ApiError && error.status === 400) {
            const body = error.body as { error?: { code?: string; message?: string } } | undefined;
            const reason = body?.error?.message || body?.error?.code || `Host lehnt Eingabe ab (${error.status})`;
            await this.bookings.markRejected(sent.operationId, reason, sent.payloadHash, new Date());
            result.rejected++;
          } else throw error;
        }
      }
      await this.pull();
      this.result.set(result);
      this.message.set(`${result.accepted} übernommen, ${result.confirmed} bestätigt, ${result.rejected} abgelehnt`);
    } catch (error) {
      // Auch Teilerfolge bleiben sichtbar und lokal committet.
      this.result.set(result);
      this.message.set(error instanceof ApiError
        ? error.status === 401
          ? 'Gerät nicht mehr verbunden. Einrichtung erneut ausführen; lokale Erfassung bleibt möglich.'
          : `Synchronisation fehlgeschlagen (${error.status})`
        : error instanceof TypeError
          ? 'Host nicht erreichbar. Eingaben bleiben für den nächsten Versuch gespeichert.'
          : error instanceof Error ? error.message : 'Synchronisation fehlgeschlagen');
    } finally {
      OpenAPI.BASE = previousBase;
      OpenAPI.TOKEN = previousToken;
      this.busy.set(false);
      this.lock.release();
      await this.refreshState();
    }
  }

  private async applyBootstrap(response: BootstrapResponseDto): Promise<void> {
    const generation = integer(response.dbGeneration, 1);
    const protocol = integer(response.protocolGeneration, 1);
    const cursor = integer(response.cursor);
    if (![response.accounts, response.pools, response.categories, response.taxCategories].every(Array.isArray)) {
      throw new Error('Stammdaten-Antwort des Hosts ist unvollständig');
    }
    await this.db.executeSet([
      ...this.snapshots.buildReplaceSets({
        accounts: new Map(response.accounts.map(dto => [dto.id, dto])),
        pools: new Map(response.pools.map(dto => [dto.id, dto])),
        categories: new Map(response.categories.map(dto => [dto.id, dto])),
        taxCategories: new Map(response.taxCategories.map(dto => [dto.id, dto])),
      }, new Date().toISOString()),
      upsertMetaSet(SYNC_META_KEYS.cursor, String(cursor)),
      upsertMetaSet(SYNC_META_KEYS.bootstrapGeneration, String(generation)),
      upsertMetaSet(SYNC_META_KEYS.protocolGeneration, String(protocol)),
    ]);
  }

  private async pull(): Promise<void> {
    let cursor = integer((await this.meta.get(SYNC_META_KEYS.cursor)) ?? '0');
    for (;;) {
      const page = await SyncEndpointsService.syncGetChanges(cursor, 200);
      const end = integer(page.cursor);
      if (!Array.isArray(page.changes) || typeof page.complete !== 'boolean' || end < cursor) {
        throw new Error('Ungültige Änderungsantwort des Hosts');
      }
      let last = cursor;
      for (const change of page.changes) {
        const id = integer(change.id, 1);
        if (id <= last || id > end) throw new Error('Ungültige Änderungsreihenfolge des Hosts');
        last = id;
      }
      if (page.changes.some(change => ['account', 'pool', 'category', 'taxCategory'].includes(change.entityType))) {
        const bootstrap = await SyncEndpointsService.syncBootstrap();
        const oldGeneration = await this.meta.get(SYNC_META_KEYS.bootstrapGeneration);
        const oldProtocol = await this.meta.get(SYNC_META_KEYS.protocolGeneration);
        if (String(integer(bootstrap.dbGeneration, 1)) !== oldGeneration ||
            String(integer(bootstrap.protocolGeneration, 1)) !== oldProtocol) {
          throw new Error('Datenbank- oder Protokollgeneration geändert. Geräteabstimmung erforderlich.');
        }
        await this.applyBootstrap(bootstrap);
        return;
      }
      if (page.complete) {
        await this.meta.set(SYNC_META_KEYS.cursor, String(end));
        return;
      }
      // Der Antwortcursor ist die obere Grenze, NICHT das Seitenende.
      // Erst nach vollständigem Pull lokal fortschreiben.
      if (last === cursor) throw new Error('Host liefert keine vollständige Änderungsseite');
      cursor = last;
    }
  }
}
