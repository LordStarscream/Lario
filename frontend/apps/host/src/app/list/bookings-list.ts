import { ChangeDetectionStrategy, Component, inject, OnInit, signal } from '@angular/core';
import { Router } from '@angular/router';
import { FormsModule } from '@angular/forms';
import type { ApiErrorInfo } from '../../api/api-error';
import { ApiErrorHandler } from '../../api/error-handler';
import { CatalogService } from '../../api/catalogs';
import { formatCents, formatDate, parseEuroToCents, todayIso } from '../../api/money';
import { BalancesPanel } from '../shared/balances-panel/balances-panel';
import { JournalEndpointsService } from '../../generated/services/JournalEndpointsService';
import { JournalWriteEndpointsService } from '../../generated/services/JournalWriteEndpointsService';
import type { BalancesDto } from '../../generated/models/BalancesDto';
import type { BookingDetailDto } from '../../generated/models/BookingDetailDto';
import type { BookingListItemDto } from '../../generated/models/BookingListItemDto';

export interface ReversalDialogState {
  booking: BookingListItemDto;
  /** Stabile Identität des Stornos (Idempotenz), einmal pro Dialog erzeugt. */
  operationId: string;
  date: string;
  note: string;
  busy: boolean;
  error: ApiErrorInfo | null;
}

export interface RefundLineState {
  poolId: string;
  poolName: string | null;
  /** Verbleibender Deckel in Zenten: Split-Position des Pools minus bereits erstattet. */
  maxCents: number;
  /** Bereits gewährte Erstattungen an diesen Pool in Zenten (Info-Anzeige). */
  refundedCents: number;
  amountText: string;
}

export interface RefundDialogState {
  booking: BookingListItemDto;
  operationId: string;
  loading: boolean;
  busy: boolean;
  detail: BookingDetailDto | null;
  accountId: string;
  date: string;
  lines: RefundLineState[];
  /** true, wenn alle Ursprungspools vollständig erstattet sind. */
  fullyRefunded: boolean;
  error: ApiErrorInfo | null;
}

const TYPE_LABELS: Record<string, string> = {
  Income: 'Einnahme',
  Expense: 'Ausgabe',
  AccountTransfer: 'Kontotransfer',
  Reassignment: 'Umwidmung',
  TransferReassignment: 'Transfer + Umwidmung',
  Reservation: 'Reservierung',
  Closeout: 'Abschluss',
  Reversal: 'Storno',
  Refund: 'Erstattung',
  BalanceCorrection: 'Korrektur Kontostand',
  OpeningBalance: 'Anfangsbestand',
};

const FILTER_TYPES = [
  { value: 'all', label: 'Alle Typen' },
  { value: 'Expense', label: 'Ausgaben' },
  { value: 'Income', label: 'Einnahmen' },
  { value: 'AccountTransfer', label: 'Kontotransfers' },
  { value: 'Reassignment', label: 'Umwidmungen' },
  { value: 'TransferReassignment', label: 'Transfer + Umwidmung' },
  { value: 'Reversal', label: 'Stornos' },
  { value: 'Refund', label: 'Erstattungen' },
  { value: 'OpeningBalance', label: 'Anfangsbestände' },
  { value: 'BalanceCorrection', label: 'Korrektur Kontostand' },
];

function newOperationId(prefix: string): string {
  const random =
    typeof crypto !== 'undefined' && 'randomUUID' in crypto
      ? crypto.randomUUID()
      : Math.random().toString(36).slice(2);
  return `${prefix}-${random}`;
}

/**
 * Erstattungszeilen aus dem Buchungsdetail ableiten (Review R3 P2):
 *
 * - Aggregation pro Pool: eine Erstattung fasst einen Pool in einer
 *   Zeile auf (serverseitige Regel); mehrere Splits desselben Pools
 *   zählen zusammen zur Ursprungs-Position.
 * - Verbleibender Deckel pro Pool = Split-Position minus bereits
 *   gewährter Erstattungen ({@link BookingDetailDto.refundedByPool}) —
 *   nach einer Teilerstattung ist nur noch der Rest erstattungsfähig.
 * - Vollständig erstattete Pools fallen aus den Zeilen heraus;
 *   sind alle Pools vollständig erstattet, ist die Ausgabe
 *   {@link fullyRefunded}.
 */
export function refundLinesFromDetail(detail: BookingDetailDto): {
  lines: RefundLineState[];
  fullyRefunded: boolean;
} {
  const positionByPool = new Map<string, number>();
  const nameByPool = new Map<string, string | null>();
  for (const split of detail.splits) {
    if (!split.poolId) {
      continue;
    }
    positionByPool.set(
      split.poolId,
      (positionByPool.get(split.poolId) ?? 0) + Number(split.amount),
    );
    if (!nameByPool.has(split.poolId)) {
      nameByPool.set(split.poolId, split.poolName);
    }
  }

  const refundedByPool = new Map<string, number>();
  for (const entry of detail.refundedByPool) {
    refundedByPool.set(
      entry.poolId,
      (refundedByPool.get(entry.poolId) ?? 0) + Number(entry.amount),
    );
  }

  const lines: RefundLineState[] = [];
  for (const [poolId, position] of positionByPool) {
    const refunded = Math.min(refundedByPool.get(poolId) ?? 0, position);
    const remaining = position - refunded;
    if (remaining > 0) {
      lines.push({
        poolId,
        poolName: nameByPool.get(poolId) ?? null,
        maxCents: remaining,
        refundedCents: refunded,
        amountText: '',
      });
    }
  }

  return { lines, fullyRefunded: positionByPool.size > 0 && lines.length === 0 };
}

@Component({
  selector: 'app-bookings-list',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [FormsModule, BalancesPanel],
  templateUrl: './bookings-list.html',
  styleUrl: './bookings-list.css',
})
export class BookingsList implements OnInit {
  readonly typeLabels = TYPE_LABELS;
  readonly filterTypes = FILTER_TYPES;
  readonly todayIso = todayIso;

  typeFilter = 'Expense';
  fromFilter = '';
  toFilter = '';

  readonly items = signal<BookingListItemDto[]>([]);
  readonly loading = signal(false);
  readonly loadError = signal<ApiErrorInfo | null>(null);
  readonly success = signal<string | null>(null);
  /**
   * Aktuelle Salden (Zwischenstep 7.5, A1): Konten- und Pool-Salden
   * über der Buchungsliste. Ergänzende Ansicht — ein Fehler beim Laden
   * der Salden blockiert die Liste nicht (dann ohne Karte).
   */
  readonly balances = signal<BalancesDto | null>(null);

  readonly reversal = signal<ReversalDialogState | null>(null);
  readonly refund = signal<RefundDialogState | null>(null);

  private readonly router = inject(Router);
  readonly catalog = inject(CatalogService);
  readonly errors = inject(ApiErrorHandler);

  async ngOnInit(): Promise<void> {
    await this.catalog.load();
    await Promise.all([this.load(), this.loadBalances()]);
  }

  async load(): Promise<void> {
    if (this.loading()) {
      return;
    }
    this.loading.set(true);
    this.loadError.set(null);
    this.success.set(null);
    try {
      const items = await JournalEndpointsService.listBookings(
        this.typeFilter === 'all' ? undefined : this.typeFilter,
        this.fromFilter || undefined,
        this.toFilter || undefined,
        500,
      );
      this.items.set(items);
    } catch (error) {
      this.loadError.set(this.errors.handle(error));
    } finally {
      this.loading.set(false);
    }
  }

  /** Salden laden (A1); Fehler bleiben lokal — die Liste funktioniert ohne. */
  private async loadBalances(): Promise<void> {
    try {
      this.balances.set(await JournalEndpointsService.getBalances());
    } catch {
      this.balances.set(null);
    }
  }

  /** Salden aktualisieren (nach Schreibaktionen, z. B. Storno). */
  async refreshBalances(): Promise<void> {
    await this.loadBalances();
  }

  typeLabel(type: string): string {
    return TYPE_LABELS[type] ?? type;
  }

  /**
   * Buchungen, für die der UI-Korrektur-Button sichtbar ist: nur Typen mit
   * vorhandenem Korrekturformular — Ausgabe (ausgaben/:id/korrektur) und
   * Anfangsbestand (anfangsbestaende/:id/korrektur). Die Domain definiert
   * Korrektur zusätzlich für Einnahmen (CorrectionService), dafür existiert
   * in der UI aber noch kein Einnahmenformular — bis dahin ändern
   * Einnahmen über Storno + Neuausgabe (der Storno-Button bleibt für sie
   * verfügbar). Gleiches gilt für alle übrigen Typen (Storno + Neu).
   */
  isCorrectable(item: BookingListItemDto): boolean {
    return item.type === 'Expense' || item.type === 'OpeningBalance';
  }

  /**
   * Typgerechte Korrektur-Route öffnen (7.5, offener Punkt): ein
   * Anfangsbestand öffnet das Anfangsbestände-Korrekturformular
   * (Verteilung auf Pools, in-place), Ausgaben das
   * Ausgaben-Korrekturformular. isCorrectable() begrenzt den Button auf
   * genau diese beiden Typen — die Ausgaben-Branch erreicht nur Ausgaben.
   * Beide Pfade sind absolut (relative Navigation löst gegen die Wurzel
   * auf und trifft die falsche Route).
   */
  goCorrection(item: BookingListItemDto): void {
    const base =
      item.type === 'OpeningBalance' ? '/anfangsbestaende' : '/ausgaben';
    void this.router.navigate([base, item.id, 'korrektur']);
  }

  // ---------- Storno ----------

  openReversal(booking: BookingListItemDto): void {
    this.reversal.set({
      booking,
      operationId: newOperationId('rev'),
      date: todayIso(),
      note: '',
      busy: false,
      error: null,
    });
  }

  closeReversal(): void {
    this.reversal.set(null);
  }

  async submitReversal(): Promise<void> {
    const dialog = this.reversal();
    if (!dialog || dialog.busy || !dialog.date) {
      return;
    }
    this.patchReversal({ busy: true, error: null });
    try {
      await JournalWriteEndpointsService.createReversal(dialog.booking.id, {
        id: dialog.operationId,
        expectedBaseRevision: Number(dialog.booking.baseRevision),
        date: dialog.date,
        note: dialog.note.trim() === '' ? null : dialog.note.trim(),
      });
      this.reversal.set(null);
      await this.load();
      await this.refreshBalances();
      // load() setzt success auf null — daher erst danach die Meldung setzen.
      this.success.set(`Storno zu ${formatDate(dialog.booking.date)} gebucht.`);
    } catch (error) {
      this.patchReversal({ busy: false, error: this.errors.handle(error) });
    }
  }

  private patchReversal(patch: Partial<ReversalDialogState>): void {
    this.reversal.update((d) => (d ? { ...d, ...patch } : d));
  }

  // ---------- Erstattung ----------

  openRefund(booking: BookingListItemDto): void {
    const state: RefundDialogState = {
      booking,
      operationId: newOperationId('refund'),
      loading: true,
      busy: false,
      detail: null,
      accountId: booking.accountId ?? '',
      date: todayIso(),
      lines: [],
      fullyRefunded: false,
      error: null,
    };
    this.refund.set(state);
    void this.loadRefundDetail(state);
  }

  private async loadRefundDetail(state: RefundDialogState): Promise<void> {
    try {
      const detail = await JournalEndpointsService.getBooking(state.booking.id);
      const { lines, fullyRefunded } = refundLinesFromDetail(detail);
      this.patchRefund(state, {
        loading: false,
        detail,
        accountId: state.accountId || detail.accountPostings[0]?.accountId || '',
        lines,
        fullyRefunded,
      });
    } catch (error) {
      this.patchRefund(state, { loading: false, error: this.errors.handle(error) });
    }
  }

  closeRefund(): void {
    this.refund.set(null);
  }

  refundSumCents(): number {
    return (
      this.refund()?.lines.reduce(
        (sum, line) => sum + (parseEuroToCents(line.amountText) ?? 0),
        0,
      ) ?? 0
    );
  }

  refundValid(): boolean {
    const dialog = this.refund();
    if (!dialog || !dialog.detail || !dialog.accountId || !dialog.date) {
      return false;
    }
    // Review R4 F3: leer/0-Zeilen sind nicht ausgewählt (keine
    // Teil-Erstattung blockieren); mindestens eine Zeile mit positivem
    // Betrag erforderlich; ungültige nicht-leere Einträge und
    // Überschreitung des Deckels bleiben abgelehnt.
    let hasPositive = false;
    for (const line of dialog.lines) {
      const amount = parseEuroToCents(line.amountText);
      if (amount === null) {
        if (line.amountText.trim() !== '') {
          return false; // ungültiger Eintrag
        }
        continue; // leer = nicht ausgewählt
      }
      if (amount === 0) {
        continue; // 0 = nicht ausgewählt
      }
      if (amount > line.maxCents) {
        return false;
      }
      hasPositive = true;
    }
    return hasPositive;
  }

  async submitRefund(): Promise<void> {
    const dialog = this.refund();
    if (!dialog || dialog.busy || !dialog.detail || !this.refundValid()) {
      return;
    }
    this.patchRefund(dialog, { busy: true, error: null });
    try {
      await JournalWriteEndpointsService.createRefund(dialog.booking.id, {
        id: dialog.operationId,
        accountId: dialog.accountId,
        date: dialog.date,
        lines: dialog.lines
          .map((line) => ({
            poolId: line.poolId,
            // Review R4 F3: leer/0 = nicht ausgewählt → nicht übertragen.
            amount: parseEuroToCents(line.amountText) ?? 0,
          }))
          .filter((line) => line.amount > 0),
        note: null,
      });
      this.refund.set(null);
      await this.load();
      await this.refreshBalances();
      // load() setzt success auf null — daher erst danach die Meldung setzen.
      this.success.set(`Erstattung zu ${formatDate(dialog.booking.date)} gebucht.`);
    } catch (error) {
      this.patchRefund(dialog, { busy: false, error: this.errors.handle(error) });
    }
  }

  private patchRefund(state: RefundDialogState, patch: Partial<RefundDialogState>): void {
    this.refund.update((d) => (d && d.booking.id === state.booking.id ? { ...d, ...patch } : d));
  }

  // ---------- Anzeige ----------

  formatCents = formatCents;
  formatDate = formatDate;
}
