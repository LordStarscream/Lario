import { Injectable, inject } from '@angular/core';
import { LocalDatabase } from './local-database';
import { MetaService } from './meta.service';
import {
  OP_SEQ_META_KEY,
  formatBookingId,
  formatOperationId,
  formatSplitId,
  parseOpSeq,
} from './operation-id';
import {
  buildExpensePayload,
  type LocalExpenseInput,
  type LocalSplitInput,
} from './expense-payload';
import { payloadHash } from './sha256';
import {
  DELETE_BOOKING,
  DELETE_OUTBOX,
  DELETE_SPLITS,
  INSERT_BOOKING,
  INSERT_OUTBOX,
  INSERT_SPLIT,
  SELECT_BOOKING_BY_ID,
  SELECT_BOOKING_LIST,
  SELECT_OUTBOX_BY_BOOKING,
  SELECT_SPLITS_BY_BOOKING,
  UPDATE_BOOKING,
  UPDATE_OUTBOX_PAYLOAD,
  upsertMetaSet,
} from './statements';

/** Meta-Key der stabilen Geräte-ID (Design D1; UUIDv4). */
export const DEVICE_ID_META_KEY = 'device.id';

/**
 * UUIDv4 (Muster `app/sqlite.service.ts`): `crypto.randomUUID()` im
 * Android-WebView (HTTPS-Kontext); Fallback über
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

/**
 * Lokales Buchungs-Repository (Schritt 8, Batch B, Design D2/D5/D7).
 *
 * Eine Ausgabe ist EINE atomare `executeSet`-Transaktion:
 * Buchung + Splits + Outbox-Zeile + Zähler (Design D7). Die lokale
 * Buchung hält die Fachdaten für die Eingaben-View (Batch E) und das
 * Synchronisationslesen (Schritt 9); die Outbox-Zeile trägt die
 * operationId + Payload + Hash für den späteren Versand.
 *
 * `save` ist über `saveChain` serialisiert: Zähler und `device.id`
 * werden innerhalb derselben Kette gelesen/geschrieben, dadurch
 * erzeugen parallele Aufrufe keine doppelten Operation-IDs oder
 * abweichenden Geräte-IDs. Die Anlage der `device.id` (einmalig,
 * stabil) läuft in der lokalen Methode `getOrCreateDeviceId` — ein eigener
 * atomarer Meta-Batch VOR dem Haupt-Batch; sie gehört nicht zum
 * D7-Batch (Buchung + Splits + Outbox + Zähler).
 *
 * Nur `queued`-Buchungen dürfen geändert oder verworfen werden — der
 * Host hat die Operation dann nie gesehen (Design D4).
 */

/** Validierungsfehler: `reason` maschinell unterscheidbar. */
export class BookingValidationError extends Error {
  constructor(
    readonly reason:
      | 'invalid_date'
      | 'invalid_amount'
      | 'invalid_account'
      | 'no_splits'
      | 'invalid_split_amount'
      | 'split_sum_mismatch'
      | 'tax_inconsistent'
      | 'invalid_marked_amount',
    message: string,
  ) {
    super(message);
    this.name = 'BookingValidationError';
  }
}

/** Zustandsfehler: `reason` maschinell unterscheidbar. */
export class BookingStateError extends Error {
  constructor(
    readonly reason: 'not_found' | 'not_queued' | 'duplicate_operation',
    message: string,
  ) {
    super(message);
    this.name = 'BookingStateError';
  }
}

/** Eintrag der Eingaben-Liste (Design D2, Outbox-Status mitgeführt). */
export interface LocalBookingListItem {
  bookingId: string;
  date: string;
  totalAmountCents: number;
  accountId: string;
  note: string | null;
  /** `queued`/`confirmed` aus der Outbox; `unknown` ohne Outbox-Zeile. */
  status: 'queued' | 'confirmed' | 'unknown';
}

/** Steuerkennzeichnung eines Split (Design D2; null = keine Kennzeichnung). */
export interface LocalSplitTax {
  relevant: boolean;
  taxCategoryId: string | null;
  taxCategoryName: string | null;
  markedAmountCents: number | null;
  note: string | null;
}

/** Split-Detail (snake_case-Spalten → camelCase, tax normalisiert). */
export interface LocalBookingSplit {
  id: string;
  amountCents: number;
  poolId: string;
  categoryId: string;
  note: string | null;
  /** null = keine explizite Kennzeichnung (Host materialisiert Voreinstellung). */
  tax: LocalSplitTax | null;
}

/** Buchungsdetail inkl. Splits (Eingaben-View, Batch E). */
export interface LocalBookingDetail {
  bookingId: string;
  date: string;
  type: string;
  accountId: string;
  totalAmountCents: number;
  note: string | null;
  createdAtUtc: string;
  status: 'queued' | 'confirmed' | 'unknown';
  splits: LocalBookingSplit[];
}

/** `yyyy-MM-dd` und echtes Kalendertag (z. B. `2026-02-30` ungültig). */
function isValidDate(value: string): boolean {
  if (!/^\d{4}-\d{2}-\d{2}$/.test(value)) {
    return false;
  }
  const [year, month, day] = value.split('-').map(Number);
  const date = new Date(Date.UTC(year, month - 1, day));
  return (
    date.getUTCFullYear() === year &&
    date.getUTCMonth() === month - 1 &&
    date.getUTCDate() === day
  );
}

/**
 * Invarianten einer lokalen Ausgabe (Batch B, Vorbedingung vor jeder
 * Transaktion). Wirft `BookingValidationError` mit exaktem `reason`.
 * Exportiert für die Tests.
 */
export function validateExpense(input: LocalExpenseInput): void {
  if (!isValidDate(input.date)) {
    throw new BookingValidationError(
      'invalid_date',
      `Ungültiges Fachdatum: ${input.date} (erwartet yyyy-MM-dd, echtes Kalendertag)`,
    );
  }
  if (
    !Number.isInteger(input.totalAmountCents) ||
    input.totalAmountCents < 1 ||
    input.totalAmountCents > 9_999_999_999_999
  ) {
    throw new BookingValidationError(
      'invalid_amount',
      `Gesamtbetrag ungültig: ${input.totalAmountCents} (erwartet ganze Cent-Zahl 1…9999999999999)`,
    );
  }
  if (!input.accountId || input.accountId.trim().length === 0) {
    throw new BookingValidationError('invalid_account', 'Konto fehlt');
  }
  if (input.splits.length === 0) {
    throw new BookingValidationError('no_splits', 'Mindestens ein Split erforderlich');
  }
  let sum = 0;
  for (const split of input.splits) {
    if (!Number.isInteger(split.amountCents) || split.amountCents < 1) {
      throw new BookingValidationError(
        'invalid_split_amount',
        `Splitbetrag ungültig: ${split.amountCents} (erwartet ganze Cent-Zahl > 0)`,
      );
    }
    sum += split.amountCents;
    validateSplitTax(split);
  }
  if (sum !== input.totalAmountCents) {
    throw new BookingValidationError(
      'split_sum_mismatch',
      `Splitsumme ${sum} entspricht nicht dem Gesamtbetrag ${input.totalAmountCents}`,
    );
  }
}

/** Steuer-Regeln (Domain `Lario.Domain/Journal/TaxMarking.cs`, maßgeblich). */
function validateSplitTax(split: LocalSplitInput): void {
  const tax = split.tax;
  if (tax === null) {
    return;
  }
  if (!tax.relevant) {
    if (tax.taxCategoryId !== null || tax.markedAmountCents !== null) {
      throw new BookingValidationError(
        'tax_inconsistent',
        'Nicht relevante Steuerkennzeichnung darf keine Kategorie/Markierung tragen',
      );
    }
    return;
  }
  if (tax.markedAmountCents !== null) {
    if (
      !Number.isInteger(tax.markedAmountCents) ||
      tax.markedAmountCents < 0 ||
      tax.markedAmountCents > split.amountCents
    ) {
      throw new BookingValidationError(
        'invalid_marked_amount',
        `Markierter Teilbetrag ungültig: ${tax.markedAmountCents} (erwartet 0…Splitbetrag ${split.amountCents})`,
      );
    }
  }
}

/** Outbox-Status aus der Join-Zeile (null → unknown). */
function mapOutboxStatus(status: unknown): 'queued' | 'confirmed' | 'unknown' {
  if (status === 'queued' || status === 'confirmed') {
    return status;
  }
  return 'unknown';
}

@Injectable({ providedIn: 'root' })
export class BookingsRepository {
  // inject() statt Konstruktor-Injection (ESLint prefer-inject,
  // Batch-A-Konvention).
  private readonly db = inject(LocalDatabase);
  private readonly meta = inject(MetaService);

  /**
   * Serialisiert den Read-Modify-Write-Zyklus von `save` (Zähler lesen
   * → atomarer Batch). Parallele Aufrufe ohne Serialisierung würden
   * denselben Zählerwert lesen und damit doppelte Operation-IDs
   * erzeugen (Primary-Key-Verstoß).
   */
  private saveChain: Promise<unknown> = Promise.resolve();

  /**
   * Neue Ausgabe speichern (Design D7: Buchung + Splits + Outbox +
   * Zähler in EINER atomaren Transaktion). Die stabile Geräte-ID wird
   * vorher (falls nötig) in einem eigenen Meta-Batch angelegt.
   *
   * Wirft `BookingValidationError` vor jeder Schreiboperation (dann
   * ist garantiert nichts gespeichert). Bei einem `executeSet`-Fehler
   * (z. B. UNIQUE-Verstoß auf `outbox.operation_id`) rollt die
   * Transaktion komplett zurück — kein Teilzustand.
   */
  async save(
    input: LocalExpenseInput,
    now: Date,
  ): Promise<{ bookingId: string; operationId: string }> {
    const operation = this.saveChain.then(() => this.doSave(input, now));
    // Die Kette läuft auch bei Fehlern weiter, sonst würden spätere
    // Saves dauerhaft hängen.
    this.saveChain = operation.then(
      () => undefined,
      () => undefined,
    );
    return operation;
  }

  /**
   * Stabile Geräte-ID lesen bzw. einmalig anlegen (Design D1; `device.id`,
   * UUIDv4). `save` ruft diese innerhalb der `saveChain` auf — die Anlage
   * (ein `meta`-Upsert) ist dadurch gegen parallele erste Saves serialisiert.
   * Der Upsert ist idempotent.
   */
  private async getOrCreateDeviceId(): Promise<string> {
    const existing = await this.meta.get(DEVICE_ID_META_KEY);
    if (existing !== null) {
      return existing;
    }
    const deviceId = newId();
    await this.meta.set(DEVICE_ID_META_KEY, deviceId);
    return deviceId;
  }

  private async doSave(
    input: LocalExpenseInput,
    now: Date,
  ): Promise<{ bookingId: string; operationId: string }> {
    validateExpense(input);
    // Gerät-ID sicherstellen (einmalig, stabil; innerhalb der
    // saveChain serialisiert — kein Race beim ersten parallelen Save).
    const deviceId = await this.getOrCreateDeviceId();
    const seq = parseOpSeq(await this.meta.get(OP_SEQ_META_KEY));
    const bookingId = formatBookingId(seq, now.getTime());
    const operationId = formatOperationId(now, seq);
    // Kanonische Split-IDs: identisch im Payload und in der Datenbank.
    const normalized = normalizeSplitIds(bookingId, input);
    const payload = buildExpensePayload(bookingId, normalized);
    const hash = payloadHash(payload);
    const nowIso = now.toISOString();

    // EIN executeSet-Batch (atomar, Design D5/D7):
    const sets = [
      {
        statement: INSERT_BOOKING,
        values: [
          bookingId,
          input.date,
          'expense',
          input.accountId,
          input.totalAmountCents,
          input.note,
          null,
          nowIso,
        ],
      },
      ...normalized.splits.map((split) => ({
        statement: INSERT_SPLIT,
        values: splitInsertValues(split, bookingId),
      })),
      {
        statement: INSERT_OUTBOX,
        values: [operationId, deviceId, 'expense', bookingId, payload, hash, null, 'queued', nowIso, nowIso],
      },
      upsertMetaSet(OP_SEQ_META_KEY, String(seq + 1)),
    ];
    await this.db.executeSet(sets);
    return { bookingId, operationId };
  }

  /**
   * Queued-Ausgabe ändern (Design D4): Fachdaten + Splits +
   * Payload/Hash ersetzen; die `operationId` bleibt erhalten (der Host
   * hat die Operation nie gesehen). Nicht queued → `not_queued`.
   */
  async update(
    bookingId: string,
    input: LocalExpenseInput,
    now: Date,
  ): Promise<void> {
    const { operationId } = await this.loadQueued(bookingId);
    validateExpense(input);
    // Kanonische Split-IDs (gleiche Buchungs-ID + Position →
    // deterministisch, Payload und Datenbank bleiben identisch).
    const normalized = normalizeSplitIds(bookingId, input);
    const payload = buildExpensePayload(bookingId, normalized);
    const hash = payloadHash(payload);
    const nowIso = now.toISOString();

    // EIN executeSet-Batch (atomar): Buchung ersetzen, Splits
    // löschen/neu anlegen (Anzahl kann sich ändern), Outbox-Payload
    // aktualisieren. Keine Zähler-Änderung — operationId bleibt.
    const sets = [
      {
        statement: UPDATE_BOOKING,
        values: [input.date, input.totalAmountCents, input.accountId, input.note, bookingId],
      },
      { statement: DELETE_SPLITS, values: [bookingId] },
      ...normalized.splits.map((split) => ({
        statement: INSERT_SPLIT,
        values: splitInsertValues(split, bookingId),
      })),
      {
        statement: UPDATE_OUTBOX_PAYLOAD,
        values: [payload, hash, nowIso, operationId, 'queued'],
      },
    ];
    await this.db.executeSet(sets);
  }

  /**
   * Queued-Ausgabe verwerfen (Design D4): Buchung + Splits + Outbox in
   * EINER Transaktion löschen. Der Zähler wird NICHT zurückgesetzt
   * (Operation-IDs bleiben eindeutig). Nicht queued → `not_queued`.
   */
  async discard(bookingId: string): Promise<void> {
    const { operationId } = await this.loadQueued(bookingId);
    // Explizite Lösch-Reihenfolge Splits → Buchung → Outbox
    // (Verteidigungstiefe, Design D2a).
    await this.db.executeSet([
      { statement: DELETE_SPLITS, values: [bookingId] },
      { statement: DELETE_BOOKING, values: [bookingId] },
      { statement: DELETE_OUTBOX, values: [operationId] },
    ]);
  }

  /**
   * Eingaben-Liste, neuestes Fachdatum zuerst (mit Outbox-Status;
   * keine Outbox-Zeile → `unknown`).
   */
  async list(): Promise<LocalBookingListItem[]> {
    const rows = await this.db.query<Record<string, unknown>>(SELECT_BOOKING_LIST, []);
    return rows.map((row) => ({
      bookingId: String(row['id']),
      date: String(row['date']),
      totalAmountCents: Number(row['total_amount_cents']),
      accountId: String(row['account_id']),
      note: (row['note'] as string | null) ?? null,
      status: mapOutboxStatus(row['outbox_status']),
    }));
  }

  /** Buchungsdetail mit Splits; `null`, wenn die Buchung fehlt. */
  async get(bookingId: string): Promise<LocalBookingDetail | null> {
    const bookingRows = await this.db.query<Record<string, unknown>>(
      SELECT_BOOKING_BY_ID,
      [bookingId],
    );
    if (bookingRows.length === 0) {
      return null;
    }
    const booking = bookingRows[0];
    const splitRows = await this.db.query<Record<string, unknown>>(
      SELECT_SPLITS_BY_BOOKING,
      [bookingId],
    );
    const outboxRows = await this.db.query<Record<string, unknown>>(
      SELECT_OUTBOX_BY_BOOKING,
      [bookingId],
    );
    return {
      bookingId: String(booking['id']),
      date: String(booking['date']),
      type: String(booking['type']),
      accountId: String(booking['account_id']),
      totalAmountCents: Number(booking['total_amount_cents']),
      note: (booking['note'] as string | null) ?? null,
      createdAtUtc: String(booking['created_at_utc']),
      status:
        outboxRows.length === 0
          ? 'unknown'
          : mapOutboxStatus(outboxRows[0]['status']),
      splits: splitRows.map((row) => mapSplitRow(row)),
    };
  }

  /**
   * Buchung + Outbox-Zeile laden und prüfen, dass sie `queued` ist.
   * Fehlende Buchung/Outbox → `not_found`; nicht queued → `not_queued`.
   */
  private async loadQueued(
    bookingId: string,
  ): Promise<{ operationId: string }> {
    const bookingRows = await this.db.query<Record<string, unknown>>(
      SELECT_BOOKING_BY_ID,
      [bookingId],
    );
    if (bookingRows.length === 0) {
      throw new BookingStateError('not_found', `Buchung ${bookingId} nicht gefunden`);
    }
    const outboxRows = await this.db.query<Record<string, unknown>>(
      SELECT_OUTBOX_BY_BOOKING,
      [bookingId],
    );
    if (outboxRows.length === 0) {
      throw new BookingStateError(
        'not_found',
        `Outbox-Zeile für Buchung ${bookingId} nicht gefunden`,
      );
    }
    const operationId = String(outboxRows[0]['operation_id']);
    const status = outboxRows[0]['status'];
    if (status !== 'queued') {
      throw new BookingStateError(
        'not_queued',
        `Buchung ${bookingId} ist ${String(status)} — nur queued darf geändert/verworfen werden`,
      );
    }
    return { operationId };
  }
}

/**
 * Kanonische Split-IDs zuweisen (Position 1-basiert): die IDs gelten
 * sowohl im Payload als auch in der Datenbank. Die `id`-Werte der
 * Eingabe sind Formular-IDs und werden hier ersetzt.
 */
function normalizeSplitIds(
  bookingId: string,
  input: LocalExpenseInput,
): LocalExpenseInput {
  return {
    ...input,
    splits: input.splits.map((split, index) => ({
      ...split,
      id: formatSplitId(bookingId, index + 1),
    })),
  };
}

/**
 * Split-Values für INSERT_SPLIT (Spaltenreihenfolge aus statements.ts).
 * `tax: null` bleibt NULL in den Kennzeichnungs-Spalten (Host
 * materialisiert die Voreinstellung); `markedAmountCents: null` wird
 * auf den Splitbetrag normalisiert (deterministisch, Design D2).
 */
function splitInsertValues(
  split: LocalSplitInput,
  bookingId: string,
): (string | number | null)[] {
  const tax = split.tax;
  return [
    split.id,
    bookingId,
    split.amountCents,
    split.poolId,
    split.categoryId,
    split.note,
    tax ? (tax.relevant ? 1 : 0) : null,
    tax?.taxCategoryId ?? null,
    tax?.taxCategoryName ?? null,
    tax ? (tax.markedAmountCents ?? split.amountCents) : null,
    tax?.note ?? null,
  ];
}

/** Split-Zeile (snake_case) → LocalBookingSplit (tax normalisiert). */
function mapSplitRow(row: Record<string, unknown>): LocalBookingSplit {
  const taxRelevant = row['tax_relevant'];
  const tax: LocalSplitTax | null =
    taxRelevant === null || taxRelevant === undefined
      ? null
      : {
          relevant: taxRelevant === 1,
          taxCategoryId: (row['tax_category_id'] as string | null) ?? null,
          taxCategoryName: (row['tax_category_name'] as string | null) ?? null,
          markedAmountCents:
            row['marked_amount_cents'] === null || row['marked_amount_cents'] === undefined
              ? null
              : Number(row['marked_amount_cents']),
          note: (row['tax_note'] as string | null) ?? null,
        };
  return {
    id: String(row['id']),
    amountCents: Number(row['amount_cents']),
    poolId: String(row['pool_id']),
    categoryId: String(row['category_id']),
    note: (row['note'] as string | null) ?? null,
    tax,
  };
}
