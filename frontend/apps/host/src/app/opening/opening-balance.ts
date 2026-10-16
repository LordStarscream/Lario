import { ChangeDetectionStrategy, Component, inject, OnInit, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Router } from '@angular/router';
import type { ApiErrorInfo } from '../../api/api-error';
import { ApiErrorHandler } from '../../api/error-handler';
import { CatalogService } from '../../api/catalogs';
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
      const random =
        typeof crypto !== 'undefined' && 'randomUUID' in crypto
          ? crypto.randomUUID()
          : Math.random().toString(36).slice(2);
      await JournalWriteEndpointsService.createOpeningBalance({
        id: `open-${random}`,
        date: this.date,
        total: parseEuroToCents(this.totalText) as number,
        accountId: this.accountId,
        distribution,
        note: this.note.trim() === '' ? null : this.note.trim(),
      });
      await this.router.navigate(['/']);
    } catch (err) {
      this.error.set(this.errors.handle(err));
      this.busy.set(false);
    }
  }

  formatCents = formatCents;
}
