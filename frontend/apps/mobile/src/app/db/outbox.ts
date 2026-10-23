import { Injectable, inject } from '@angular/core';
import { DELETE_OUTBOX } from './statements';
import { LocalDatabase } from './local-database';
import { MetaService } from './meta.service';
import { payloadHash } from './sha256';

/**
 * Outbox-Dienst (Schritt 8, Design D4/D5).
 *
 * Die Outbox-Tabelle wird von `LocalDatabase.init()` aus dem
 * Batch-A-Schema (`statements.ts`, Design D2) angelegt:
 *
 * ```sql
 * CREATE TABLE IF NOT EXISTS outbox (
 *   operation_id TEXT PRIMARY KEY,
 *   device_id TEXT NOT NULL,
 *   type TEXT NOT NULL,
 *   booking_id TEXT NOT NULL,
 *   payload TEXT NOT NULL,
 *   payload_hash TEXT NOT NULL,
 *   expected_base_revision INTEGER,
 *   status TEXT NOT NULL,
 *   created_at_utc TEXT NOT NULL,
 *   updated_at_utc TEXT NOT NULL
 * );
 * ```
 *
 * **Status (Entscheidung, Abweichung von der Batch-B-Entwurfsvorgabe):**
 * Die finalisierten Design-D1-D12 und das Abnahmekriterium (a)
 * (`.tasks/step-8-mobile-offline.md`) schreiben
 * „Schritt 8 schreibt nur `status='queued'`“ (UI-Label „ausstehend“).
 * Die Batch-B-Vorgabe benutzte `pending` als Platzhalter — maßgeblich
 * ist das finalisierte Design: Anlegen = `queued`, Bestätigung
 * (Schritt 9, Sync) = `confirmed`.
 *
 * **Zeitstempel:** `created_at_utc`/`updated_at_utc` sind UTC-ISO-8601
 * (Z-Suffix), z. B. `2026-11-01T12:00:00.000Z` — die Batch-A-Spalten
 * heißen `*_utc`; eine separate `confirmed_at`-Spalte existiert nicht,
 * die Bestätigungszeit ist `updated_at_utc`.
 *
 * **Transaktionalität (D5):** Jede Mutation ist genau EIN
 * `executeSet`-Aufruf (`LocalDatabase.executeSet` läuft immer mit
 * `transaction: true` — Plugin-Default; Android: begin/commit, bei
 * Fehler rollback):
 *
 * - Anlegen (Buchung + Splits + Outbox-Zeile + Zähler in EINEM Batch)
 *   liegt in `BookingsRepository.save` (Design D7) — der Dienst hier
 *   hält die Status-Operationen auf existierenden Zeilen.
 * - `modifyOutbox` (nur `queued`): Payload/Hash/Zeitstempel in EINEM
 *   Batch ersetzen (gleiche `operation_id` — der Host hat sie nie
 *   gesehen).
 * - `confirmOutbox` (nur `queued`): Status `confirmed` + Zeitstempel in
 *   EINEM Batch; idempotent (nicht vorhanden oder bereits bestätigt →
 *   No-Op).
 * - `deleteOutbox` (nur `queued`): Zeile löschen; idempotent (nicht
 *   vorhanden → No-Op).
 *
 * **Einzigartigkeit:** `operation_id` ist PRIMARY KEY (DB-Constraint).
 * `booking_id` trägt im finalisierten D2-Schema KEIN UNIQUE-Constraint
 * (Retry-Semantik Schritt 9). UI-Doppeltipp wird auf Formular-Ebene
 * per `saving`-Sperre verhindert (D6); die `operationId` ist zudem aus
 * dem serialisierten Geräte-Zähler gebildet (Batch B, Repository).
 */

/** Outbox-Status (Design D4: Schritt 8 schreibt nur `queued`). */
export type OutboxStatus = 'queued' | 'confirmed';

/** Outbox-Zeile (abgebildet aus den Batch-A-Spalten). */
export interface OutboxEntry {
  operationId: string;
  deviceId: string;
  /** Operationstyp; Schritt 8: immer `expense`. */
  type: string;
  bookingId: string;
  /** Kompaktes ExpenseCommand-JSON (B3). */
  payload: string;
  /** `sha256:<hex>` (B1). */
  payloadHash: string;
  /** NULL bei Neuanlage (D4). */
  expectedBaseRevision: number | null;
  status: OutboxStatus;
  /** UTC-ISO-8601. */
  createdAtUtc: string;
  /** UTC-ISO-8601; bei Bestätigung/Änderung aktualisiert. */
  updatedAtUtc: string;
}

/** Auswertung von `countOutbox`. */
export interface OutboxCounts {
  queued: number;
  confirmed: number;
  total: number;
}

/** Meta-Key der stabilen Geräte-ID (Design: `device.id`). */
export const DEVICE_ID_META_KEY = 'device.id';

/** Operationstyp für Ausgaben (Schritt 8). */
export const OUTBOX_TYPE_EXPENSE = 'expense';

// Outbox-Statements (Batch B). Die Tabelle kommt aus statements.ts
// (Batch A, Design D2) — hier nur die CRUD-Statements des Dienstes.
const SELECT_OUTBOX_BY_OPERATION = `SELECT operation_id, device_id, type,
  booking_id, payload, payload_hash, expected_base_revision, status,
  created_at_utc, updated_at_utc
FROM outbox WHERE operation_id = ?`;

const UPDATE_OUTBOX_MODIFY = `UPDATE outbox SET payload = ?,
  payload_hash = ?, updated_at_utc = ? WHERE operation_id = ?`;

const UPDATE_OUTBOX_CONFIRM = `UPDATE outbox SET status = 'confirmed',
  updated_at_utc = ? WHERE operation_id = ?`;

const SELECT_OUTBOX_ALL = `SELECT operation_id, device_id, type,
  booking_id, payload, payload_hash, expected_base_revision, status,
  created_at_utc, updated_at_utc
FROM outbox ORDER BY created_at_utc DESC, operation_id DESC`;

const SELECT_OUTBOX_COUNTS = `SELECT
  SUM(CASE WHEN status = 'queued' THEN 1 ELSE 0 END) AS queued,
  SUM(CASE WHEN status = 'confirmed' THEN 1 ELSE 0 END) AS confirmed,
  COUNT(*) AS total
FROM outbox`;

/** UTC-ISO-8601 (Z-Suffix), z. B. `2026-11-01T12:00:00.000Z`. */
function toUtcIso(date: Date): string {
  return date.toISOString();
}

/**
 * UUIDv4 (Muster `app/sqlite.service.ts:170-180`): `crypto.randomUUID()`
 * im Android-WebView (HTTPS-Kontext); Fallback über
 * `crypto.getRandomValues`, dadurch auf jedem Pfad verfügbar.
 */
function newId(): string {
  const cryptoObj = globalThis.crypto;
  if (cryptoObj?.randomUUID) {
    return cryptoObj.randomUUID();
  }
  const bytes = new Uint8Array(16);
  cryptoObj.getRandomValues(bytes);
  bytes[6] = (bytes[6] & 0x0f) | 0x40; // Version 4
  bytes[8] = (bytes[8] & 0x3f) | 0x80; // Variant 10
  const hex = Array.from(bytes, (b) => b.toString(16).padStart(2, '0')).join('');
  return `${hex.slice(0, 8)}-${hex.slice(8, 12)}-${hex.slice(12, 16)}-${hex.slice(16, 20)}-${hex.slice(20)}`;
}

@Injectable({ providedIn: 'root' })
export class OutboxService {
  // inject() statt Konstruktor-Injection (ESLint prefer-inject,
  // Batch-A-Konvention); die App holt OutboxService aus dem
  // DI-Container (providedIn: 'root').
  private readonly db = inject(LocalDatabase);
  private readonly meta = inject(MetaService);

  /**
   * Uhr (injizierbar für deterministische Tests); Default: Systemuhr.
   */
  private now = (): Date => new Date();

  /** Uhr ersetzen (Test-Naht). */
  setClock(clock: () => Date): void {
    this.now = clock;
  }

  /**
   * Stabile Geräte-ID lesen bzw. einmalig anlegen (Design:
   * `device.id`, UUIDv4). `BookingsRepository.save` ruft diese Methode
   * innerhalb der `saveChain` auf — die Anlage (ein `meta`-Upsert,
   * eigener atomarer Batch) ist dadurch gegen parallele erste Saves
   * serialisiert. Außerhalb der Kette (z. B. Einrichtung, Batch C)
   * ist die Methode ebenfalls sicher: der Upsert ist idempotent.
   */
  async getOrCreateDeviceId(): Promise<string> {
    const existing = await this.meta.get(DEVICE_ID_META_KEY);
    if (existing !== null) {
      return existing;
    }
    const deviceId = newId();
    await this.meta.set(DEVICE_ID_META_KEY, deviceId);
    return deviceId;
  }

  /**
   * Payload/Hash einer QUEUED-Operation ersetzen (Design D4: „Ändern
   * (nur queued)“). Gleiche `operation_id` — der Host hat sie nie
   * gesehen. `confirmed`-Operationen sind unveränderlich → werfen;
   * nicht vorhandene → werfen.
   */
  async modifyOutbox(operationId: string, payload: string): Promise<void> {
    const entry = await this.getOutboxEntry(operationId);
    if (entry === null) {
      throw new Error(`Outbox-Operation ${operationId} nicht gefunden`);
    }
    if (entry.status !== 'queued') {
      throw new Error(
        `Outbox-Operation ${operationId} ist ${entry.status} — nur queued darf geändert werden`,
      );
    }
    await this.db.executeSet([
      {
        statement: UPDATE_OUTBOX_MODIFY,
        values: [payload, payloadHash(payload), toUtcIso(this.now()), operationId],
      },
    ]);
  }

  /**
   * Operation auf `confirmed` setzen (Schritt 9, Sync — hier nur die
   * Speicher-Hälfte). Idempotent: nicht vorhanden oder bereits
   * `confirmed` → No-Op (keine Änderung, kein Fehler).
   */
  async confirmOutbox(operationId: string): Promise<void> {
    const entry = await this.getOutboxEntry(operationId);
    if (entry === null || entry.status === 'confirmed') {
      return;
    }
    await this.db.executeSet([
      {
        statement: UPDATE_OUTBOX_CONFIRM,
        values: [toUtcIso(this.now()), operationId],
      },
    ]);
  }

  /**
   * Operation löschen (Design D4: „Verwerfen (nur queued)“ — die
   * Buchung/Splits werden vom Aufrufer in derselben Batch-D-Transaktion
   * entfernt). Idempotent: nicht vorhanden → No-Op; `confirmed` →
   * werfen (bestätigte Eingaben sind nicht verwerfbar).
   */
  async deleteOutbox(operationId: string): Promise<void> {
    const entry = await this.getOutboxEntry(operationId);
    if (entry === null) {
      return;
    }
    if (entry.status !== 'queued') {
      throw new Error(
        `Outbox-Operation ${operationId} ist ${entry.status} — nur queued darf verworfen werden`,
      );
    }
    await this.db.executeSet([{ statement: DELETE_OUTBOX, values: [operationId] }]);
  }

  /** Alle Outbox-Zeilen, neueste zuerst (Sortierung: created_at, operation_id). */
  async listOutbox(): Promise<OutboxEntry[]> {
    const rows = await this.db.query<Record<string, unknown>>(SELECT_OUTBOX_ALL, []);
    return rows.map(mapOutboxRow);
  }

  /** Anzahl nach Status + gesamt (NULL-Sichere SUM via COALESCE-freier Auswertung). */
  async countOutbox(): Promise<OutboxCounts> {
    const rows = await this.db.query<Record<string, unknown>>(
      SELECT_OUTBOX_COUNTS,
      [],
    );
    const row = rows[0] ?? {};
    return {
      queued: Number(row['queued'] ?? 0),
      confirmed: Number(row['confirmed'] ?? 0),
      total: Number(row['total'] ?? 0),
    };
  }

  /** Einzelne Zeile nach `operation_id`; `null`, wenn nicht vorhanden. */
  async getOutboxEntry(operationId: string): Promise<OutboxEntry | null> {
    const rows = await this.db.query<Record<string, unknown>>(
      SELECT_OUTBOX_BY_OPERATION,
      [operationId],
    );
    if (rows.length === 0) {
      return null;
    }
    return mapOutboxRow(rows[0]);
  }
}

/** Zeilenobjekt (snake_case-Spalten) → OutboxEntry (camelCase). */
function mapOutboxRow(row: Record<string, unknown>): OutboxEntry {
  const revision = row['expected_base_revision'];
  return {
    operationId: String(row['operation_id']),
    deviceId: String(row['device_id']),
    type: String(row['type']),
    bookingId: String(row['booking_id']),
    payload: String(row['payload']),
    payloadHash: String(row['payload_hash']),
    expectedBaseRevision:
      revision === null || revision === undefined ? null : Number(revision),
    status: String(row['status']) as OutboxStatus,
    createdAtUtc: String(row['created_at_utc']),
    updatedAtUtc: String(row['updated_at_utc']),
  };
}
