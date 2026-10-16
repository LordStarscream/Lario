import { ChangeDetectionStrategy, Component, inject, OnInit, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Router } from '@angular/router';
import { ApiError as ApiErrorClass } from '../../generated/core/ApiError';
import type { ApiErrorInfo } from '../../api/api-error';
import { ApiErrorHandler } from '../../api/error-handler';
import { CatalogService } from '../../api/catalogs';
import { PendingOperation } from '../../api/pending-operation';
import { formatCents, parseEuroToCents, todayIso } from '../../api/money';
import { JournalWriteEndpointsService } from '../../generated/services/JournalWriteEndpointsService';
import type { PoolAllocationCommand } from '../../generated/models/PoolAllocationCommand';

interface DistributionRow {
  key: number;
  poolId: string;
  amountText: string;
}

/**
 * Anfangsbestände erfassen (Kernfunktion 3): Kontostand mit optionaler
 * Verteilung auf Pools; nicht verteilte Beträge bleiben "unverteilt".
 */
@Component({
  selector: 'app-opening-balance',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [FormsModule],
  templateUrl: './opening-balance.html',
  styleUrl: './opening-balance.css',
})
export class OpeningBalance implements OnInit {
  private readonly router = inject(Router);
  private readonly catalog = inject(CatalogService);
  private readonly errors = inject(ApiErrorHandler);
  /**
   * Stabile Operation-Id (Review R3 P1): nach verlorener Antwort sendet
   * der nächste Klick mit unveränderter Daten dieselbe Operation (gleiche
   * Id + Payload) — idempotente Quittung statt doppeltem Anfangsbestand.
   */
  private readonly pendingOp = new PendingOperation();
  private keyBase = 1;

  readonly accounts = this.catalog.accounts;
  readonly pools = this.catalog.pools;

  date = todayIso();
  accountId = '';
  totalText = '';
  note = '';
  distribution: DistributionRow[] = [];

  readonly busy = signal(false);
  readonly error = signal<ApiErrorInfo | null>(null);
  readonly outcomeUncertain = this.pendingOp.outcomeUncertain;

  ngOnInit(): void {
    void this.catalog.load();
  }

  addRow(): void {
    this.distribution = [
      ...this.distribution,
      { key: this.keyBase++, poolId: '', amountText: '' },
    ];
  }

  removeRow(key: number): void {
    this.distribution = this.distribution.filter((row) => row.key !== key);
  }

  distributionSumCents(): number {
    return this.distribution.reduce(
      (sum, row) => sum + (parseEuroToCents(row.amountText) ?? 0),
      0,
    );
  }

  unallocatedCents(): number {
    const total = parseEuroToCents(this.totalText) ?? 0;
    return total - this.distributionSumCents();
  }

  issue(): string | null {
    if (!this.date) {
      return 'Datum fehlt.';
    }
    if (!this.accountId) {
      return 'Konto fehlt.';
    }
    const total = parseEuroToCents(this.totalText);
    if (total === null || total <= 0) {
      return 'Gesamtbetrag fehlt oder ungültig.';
    }
    const usedPoolIds = new Set<string>();
    for (const row of this.distribution) {
      if (!row.poolId) {
        return 'Jede Verteilung benötigt einen Pool.';
      }
      if (usedPoolIds.has(row.poolId)) {
        return 'Ein Pool kann nur einmal verteilt werden.';
      }
      usedPoolIds.add(row.poolId);
      const amount = parseEuroToCents(row.amountText);
      if (amount === null || amount <= 0) {
        return 'Jeder Verteilungsbetrag muss positiv und gültig sein.';
      }
    }
    if (this.distributionSumCents() > total) {
      return 'Die Verteilung darf den Gesamtbetrag nicht überschreiten.';
    }
    return null;
  }

  get issueText(): string | null {
    return this.issue();
  }

  async submit(): Promise<void> {
    if (this.busy() || this.issue() !== null) {
      return;
    }
    this.busy.set(true);
    this.error.set(null);
    try {
      const distribution: PoolAllocationCommand[] = this.distribution.map(
        (row) => ({
          poolId: row.poolId,
          amount: parseEuroToCents(row.amountText) as number,
        }),
      );
      const note = this.note.trim() === '' ? null : this.note.trim();
      const total = parseEuroToCents(this.totalText) as number;
      // Fingerprint ohne die Operation-Id: unveränderte Daten →
      // dieselbe Id (Idempotenz bei verlorener Antwort).
      const fingerprint = JSON.stringify({
        date: this.date,
        total,
        accountId: this.accountId,
        distribution,
        note,
      });
      await JournalWriteEndpointsService.createOpeningBalance({
        id: this.pendingOp.nextId('open', fingerprint),
        date: this.date,
        total,
        accountId: this.accountId,
        distribution,
        note,
      });
      this.pendingOp.release();
      await this.router.navigate(['/']);
    } catch (err) {
      this.error.set(this.errors.handle(err));
      // Definitive Ablehnung (4xx): die Operation wurde sicher nicht
      // committet → neue Id beim nächsten Versuch. Ansonsten (keine
      // Antwort, 5xx): Ausgang unklar → dieselbe Id + Payload wiedersenden.
      const definitive =
        err instanceof ApiErrorClass && err.status >= 400 && err.status < 500;
      if (definitive) {
        this.pendingOp.release();
      } else {
        this.pendingOp.markOutcomeUncertain();
      }
      this.busy.set(false);
    }
  }

  /** Rücksetzen: neue Operation-Id für die nächste Einreichung. */
  resetPendingOperation(): void {
    this.pendingOp.release();
  }

  formatCents = formatCents;
}
