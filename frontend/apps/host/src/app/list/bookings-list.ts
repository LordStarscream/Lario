import { ChangeDetectionStrategy, Component, inject, OnInit, signal } from '@angular/core';
import { Router } from '@angular/router';
import { FormsModule } from '@angular/forms';
import type { ApiErrorInfo } from '../../api/api-error';
import { ApiErrorHandler } from '../../api/error-handler';
import { CatalogService } from '../../api/catalogs';
import { formatCents, formatDate, parseEuroToCents, todayIso } from '../../api/money';
import { JournalEndpointsService } from '../../generated/services/JournalEndpointsService';
import { JournalWriteEndpointsService } from '../../generated/services/JournalWriteEndpointsService';
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
  /** Deckel: Aufteilung des Ursprungspools in Zenten. */
  maxCents: number;
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

@Component({
  selector: 'app-bookings-list',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [FormsModule],
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

  readonly reversal = signal<ReversalDialogState | null>(null);
  readonly refund = signal<RefundDialogState | null>(null);

  private readonly router = inject(Router);
  readonly catalog = inject(CatalogService);
  readonly errors = inject(ApiErrorHandler);

  async ngOnInit(): Promise<void> {
    await this.catalog.load();
    await this.load();
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

  typeLabel(type: string): string {
    return TYPE_LABELS[type] ?? type;
  }

  goCorrection(id: string): void {
    void this.router.navigate(['/ausgaben', id, 'korrektur']);
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
      error: null,
    };
    this.refund.set(state);
    void this.loadRefundDetail(state);
  }

  private async loadRefundDetail(state: RefundDialogState): Promise<void> {
    try {
      const detail = await JournalEndpointsService.getBooking(state.booking.id);
      this.patchRefund(state, {
        loading: false,
        detail,
        accountId: state.accountId || detail.accountPostings[0]?.accountId || '',
        lines: detail.splits
          .filter((s) => s.poolId)
          .map((s) => ({
            poolId: s.poolId,
            poolName: s.poolName,
            maxCents: Number(s.amount),
            amountText: '',
          })),
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
    if (dialog.lines.length === 0) {
      return false;
    }
    return dialog.lines.every((line) => {
      const amount = parseEuroToCents(line.amountText);
      return amount !== null && amount > 0 && amount <= line.maxCents;
    });
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
        lines: dialog.lines.map((line) => ({
          poolId: line.poolId,
          // isRefundComplete() garantiert gültige Zeilen; 0 wäre ein Client-Bug
          // und wird vom Server mit 400 abgelehnt.
          amount: parseEuroToCents(line.amountText) ?? 0,
        })),
        note: null,
      });
      this.refund.set(null);
      await this.load();
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
