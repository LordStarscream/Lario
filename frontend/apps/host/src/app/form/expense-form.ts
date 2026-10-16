import {
  ChangeDetectionStrategy,
  Component,
  inject,
  OnInit,
  signal,
} from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router } from '@angular/router';
import type { ApiErrorInfo } from '../../api/api-error';
import { ApiErrorHandler } from '../../api/error-handler';
import { CatalogService } from '../../api/catalogs';
import { cents, formatCents, parseEuroToCents, todayIso } from '../../api/money';
import { JournalEndpointsService } from '../../generated/services/JournalEndpointsService';
import { JournalWriteEndpointsService } from '../../generated/services/JournalWriteEndpointsService';
import type { BookingDetailDto } from '../../generated/models/BookingDetailDto';
import type { CategoryDto } from '../../generated/models/CategoryDto';
import type { ExpenseCommand } from '../../generated/models/ExpenseCommand';
import type { ExpenseSplitCommand } from '../../generated/models/ExpenseSplitCommand';
import type { TaxPresetDto } from '../../generated/models/TaxPresetDto';

export type TaxMode = 'inherit' | 'relevant' | 'irrelevant';

export interface SplitRowState {
  key: number;
  /** Stabile Split-Identität (Client-seitig, Idempotenz). */
  id: string;
  amountText: string;
  poolId: string;
  categoryId: string;
  taxMode: TaxMode;
  taxCategoryId: string;
  partialCapture: boolean;
  markedText: string;
  note: string;
}

function toEuroText(centsValue: number | string | null): string {
  if (centsValue === null) {
    return '';
  }
  return (cents(centsValue) / 100).toFixed(2).replace('.', ',');
}

function newSplitRow(index: number, keyBase: number): SplitRowState {
  const random =
    typeof crypto !== 'undefined' && 'randomUUID' in crypto
      ? crypto.randomUUID()
      : Math.random().toString(36).slice(2);
  return {
    key: keyBase + index,
    id: `split-${random}`,
    amountText: '',
    poolId: '',
    categoryId: '',
    taxMode: 'inherit',
    taxCategoryId: '',
    partialCapture: false,
    markedText: '',
    note: '',
  };
}

@Component({
  selector: 'app-expense-form',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [FormsModule],
  templateUrl: './expense-form.html',
  styleUrl: './expense-form.css',
})
export class ExpenseForm implements OnInit {
  private readonly router = inject(Router);
  private readonly catalog = inject(CatalogService);
  private readonly route = inject(ActivatedRoute);
  private readonly errors = inject(ApiErrorHandler);

  readonly correctionId: string | null;
  readonly isCorrection: boolean;
  private keyBase = 1;

  date = todayIso();
  accountId = '';
  note = '';
  totalText = '';
  splits: SplitRowState[] = [newSplitRow(0, 1)];
  expectedBaseRevision: number | null = null;

  readonly loading = signal(false);
  readonly loadError = signal<ApiErrorInfo | null>(null);
  readonly submitError = signal<ApiErrorInfo | null>(null);
  readonly busy = signal(false);
  readonly success = signal<string | null>(null);

  constructor() {
    const id = this.route.snapshot.paramMap.get('id');
    this.correctionId = id;
    this.isCorrection = id !== null;
    if (id) {
      void this.loadCorrection(id);
    }
  }

  // ---------- Stammdaten für das Template ----------

  readonly accounts = this.catalog.accounts;
  readonly pools = this.catalog.pools;
  readonly categories = this.catalog.categories;
  readonly taxCategories = this.catalog.taxCategories;

  ngOnInit(): void {
    void this.catalog.load();
  }

  // ---------- Korrektur vorladen ----------

  private async loadCorrection(id: string): Promise<void> {
    this.loading.set(true);
    this.loadError.set(null);
    try {
      const detail = await JournalEndpointsService.getBooking(id);
      this.prefillFromDetail(detail);
    } catch (error) {
      this.loadError.set(this.errors.handle(error));
    } finally {
      this.loading.set(false);
    }
  }

  private prefillFromDetail(detail: BookingDetailDto): void {
    this.date = detail.date;
    this.note = detail.note ?? '';
    this.accountId = detail.accountPostings[0]?.accountId ?? '';
    this.totalText = toEuroText(detail.totalAmount);
    this.expectedBaseRevision = Number(detail.baseRevision);
    this.splits = detail.splits.map((split, index) => {
      const row = newSplitRow(index, this.nextKey());
      row.amountText = toEuroText(split.amount);
      row.poolId = split.poolId;
      row.categoryId = split.categoryId;
      if (split.tax) {
        row.taxMode = split.tax.relevant ? 'relevant' : 'irrelevant';
        row.taxCategoryId = split.tax.taxCategoryId ?? '';
        row.partialCapture = split.tax.markedAmount !== null;
        row.markedText = toEuroText(split.tax.markedAmount);
      }
      row.note = split.note ?? '';
      return row;
    });
    if (this.splits.length === 0) {
      this.splits = [newSplitRow(0, this.nextKey())];
    }
  }

  private nextKey(): number {
    return this.keyBase++;
  }

  // ---------- Kategorie-Voreinstellungen ----------

  categoryById(id: string): CategoryDto | undefined {
    return this.catalog.categories().find((c) => c.id === id);
  }

  effectivePreset(row: SplitRowState): TaxPresetDto | null {
    return this.categoryById(row.categoryId)?.taxPreset ?? null;
  }

  presetLabel(row: SplitRowState): string {
    const preset = this.effectivePreset(row);
    if (preset === null) {
      return 'keine Voreinstellung';
    }
    if (preset.taxRelevant === true) {
      return `relevant${preset.taxCategoryName ? ` (${preset.taxCategoryName})` : ''}`;
    }
    if (preset.taxRelevant === false) {
      return 'nicht relevant';
    }
    return 'offen';
  }

  /** Wirksame Relevanz einer Zeile (Voreinstellung oder explizit). */
  effectiveTaxRelevant(row: SplitRowState): boolean | null {
    if (row.taxMode === 'relevant') {
      return true;
    }
    if (row.taxMode === 'irrelevant') {
      return false;
    }
    return this.effectivePreset(row)?.taxRelevant ?? null;
  }

  // ---------- Zeilen-Verwaltung ----------

  addSplit(): void {
    this.splits = [...this.splits, newSplitRow(this.splits.length, this.nextKey())];
    this.submitError.set(null);
  }

  removeSplit(key: number): void {
    this.splits = this.splits.filter((row) => row.key !== key);
    this.submitError.set(null);
  }

  onTaxModeChange(row: SplitRowState, mode: TaxMode): void {
    row.taxMode = mode;
    if (mode === 'relevant') {
      const preset = this.effectivePreset(row);
      if (
        row.taxCategoryId === '' &&
        preset?.taxRelevant === true &&
        preset.taxCategoryId
      ) {
        row.taxCategoryId = preset.taxCategoryId;
      }
    }
    this.submitError.set(null);
  }

  // ---------- Validierung ----------

  splitsSumCents(): number {
    return this.splits.reduce(
      (sum, row) => sum + (parseEuroToCents(row.amountText) ?? 0),
      0,
    );
  }

  totalCents(): number | null {
    return parseEuroToCents(this.totalText);
  }

  /** Summe der Zeilen weicht vom Gesamtbetrag ab (nur bei gültiger Gesamtsumme). */
  sumMismatch(): boolean {
    const total = this.totalCents();
    return total !== null && total > 0 && this.splitsSumCents() !== total;
  }

  splitIssue(row: SplitRowState): string | null {
    const amount = parseEuroToCents(row.amountText);
    if (amount === null || amount <= 0) {
      return 'Betrag fehlt oder ungültig';
    }
    if (!row.poolId) {
      return 'Pool fehlt';
    }
    if (!row.categoryId) {
      return 'Kategorie fehlt';
    }
    if (row.taxMode === 'relevant' && row.taxCategoryId === '') {
      return 'Steuerkategorie fehlt';
    }
    if (row.taxMode === 'relevant' && row.partialCapture) {
      const marked = parseEuroToCents(row.markedText);
      if (marked === null || marked <= 0) {
        return 'Teilmenge fehlt oder ungültig';
      }
      if (marked > amount) {
        return 'Teilmenge größer als Betrag';
      }
    }
    return null;
  }

  formIssue(): string | null {
    if (!this.date) {
      return 'Datum fehlt.';
    }
    if (!this.accountId) {
      return 'Konto fehlt.';
    }
    const total = this.totalCents();
    if (total === null || total <= 0) {
      return 'Gesamtbetrag fehlt oder ungültig.';
    }
    if (this.splits.length === 0) {
      return 'Mindestens eine Aufteilung ist erforderlich.';
    }
    for (const row of this.splits) {
      const issue = this.splitIssue(row);
      if (issue) {
        return `Zeile ${this.splits.indexOf(row) + 1}: ${issue}.`;
      }
    }
    const sum = this.splitsSumCents();
    if (sum !== total) {
      return `Die Aufteilung (${formatCents(sum)}) muss den Gesamtbetrag (${formatCents(total)}) exakt abdecken.`;
    }
    if (this.isCorrection && this.expectedBaseRevision === null) {
      return 'Revision der Ursprungsbuchung unbekannt — bitte neu laden.';
    }
    return null;
  }

  get issue(): string | null {
    return this.formIssue();
  }

  // ---------- Absenden ----------

  private operationId: string | null = null;

  /** Stabile Identität der Operation: einmal erzeugt, bei Retries wiederverwendet. */
  private stableOperationId(): string {
    if (this.operationId === null) {
      const random =
        typeof crypto !== 'undefined' && 'randomUUID' in crypto
          ? crypto.randomUUID()
          : Math.random().toString(36).slice(2);
      this.operationId = this.isCorrection ? `corr-${random}` : `exp-${random}`;
    }
    return this.operationId;
  }

  private buildCommand(): ExpenseCommand {
    const splits: ExpenseSplitCommand[] = this.splits.map((row) => {
      let tax: ExpenseSplitCommand['tax'] = null;
      if (row.taxMode === 'relevant') {
        tax = {
          relevant: true,
          taxCategoryId: row.taxCategoryId,
          markedAmount: row.partialCapture
            ? (parseEuroToCents(row.markedText) as number)
            : null,
          note: null,
        };
      } else if (row.taxMode === 'irrelevant') {
        tax = {
          relevant: false,
          taxCategoryId: null,
          markedAmount: null,
          note: null,
        };
      }
      // Beträge wurden zuvor in splitIssue() validiert.
      const amount = parseEuroToCents(row.amountText) as number;
      return {
        id: row.id,
        amount,
        poolId: row.poolId,
        categoryId: row.categoryId,
        note: row.note.trim() === '' ? null : row.note.trim(),
        tax,
      };
    });
    return {
      id: this.stableOperationId(),
      date: this.date,
      totalAmount: this.totalCents() as number,
      accountId: this.accountId,
      note: this.note.trim() === '' ? null : this.note.trim(),
      splits,
      expectedBaseRevision: this.isCorrection
        ? this.expectedBaseRevision
        : null,
    };
  }

  async submit(): Promise<void> {
    if (this.busy() || this.formIssue() !== null) {
      return;
    }
    this.busy.set(true);
    this.submitError.set(null);
    try {
      const command = this.buildCommand();
      const created = this.isCorrection
        ? await JournalWriteEndpointsService.correctBooking(
            this.correctionId as string,
            {
              operationId: command.id,
              expectedBaseRevision: command.expectedBaseRevision as number,
              date: command.date,
              note: command.note,
              accountId: command.accountId,
              totalAmount: command.totalAmount,
              splits: command.splits,
              allocations: null,
            },
          )
        : await JournalWriteEndpointsService.createExpense(command);
      this.success.set(
        created.idempotent
          ? 'Die Buchung war bereits gebucht (identisch erneut eingereicht).'
          : this.isCorrection
            ? 'Korrektur wurde gebucht.'
            : 'Ausgabe wurde gebucht.',
      );
      await this.router.navigate(['/']);
    } catch (error) {
      this.submitError.set(this.errors.handle(error));
      this.busy.set(false);
    }
  }

  async goBack(): Promise<void> {
    await this.router.navigate(['/']);
  }

  // ---------- Anzeige für das Template ----------

  formatCents = formatCents;
}
