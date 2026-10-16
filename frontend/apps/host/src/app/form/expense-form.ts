import {
  ChangeDetectionStrategy,
  Component,
  inject,
  OnInit,
  signal,
  type Signal,
} from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router } from '@angular/router';
import type { ApiErrorInfo } from '../../api/api-error';
import { ApiErrorHandler } from '../../api/error-handler';
import { CatalogService } from '../../api/catalogs';
import { PendingOperation } from '../../api/pending-operation';
import { cents, formatCents, parseEuroToCents, todayIso } from '../../api/money';
import { ApiError as ApiErrorClass } from '../../generated/core/ApiError';
import { JournalEndpointsService } from '../../generated/services/JournalEndpointsService';
import { JournalWriteEndpointsService } from '../../generated/services/JournalWriteEndpointsService';
import type { BookingDetailDto } from '../../generated/models/BookingDetailDto';
import type { CategoryDto } from '../../generated/models/CategoryDto';
import type { ExpenseCommand } from '../../generated/models/ExpenseCommand';
import type { ExpenseSplitCommand } from '../../generated/models/ExpenseSplitCommand';
import type { TaxPresetDto } from '../../generated/models/TaxPresetDto';

export type TaxMode = 'inherit' | 'relevant' | 'irrelevant';

/** Max. Länge der Steuerkennzeichnungs-Notiz (API-Vertrag: 500 Zeichen). */
export const MAX_TAX_NOTE_LENGTH = 500;

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
  /** Steuerkennzeichnungs-Notiz (Review R4 F5). */
  taxNote: string;
  /**
   * Zeile wurde aus dem Ursprungs-Detail vorgefüllt (Korrektur);
   * nur solche Zeilen tragen die originale Split-Id (Review R5-2).
   */
  prefilled: boolean;
}

function toEuroText(centsValue: number | string | null): string {
  if (centsValue === null) {
    return '';
  }
  return (cents(centsValue) / 100).toFixed(2).replace('.', ',');
}

/**
 * Neue Split-Zeile mit eindeutigem Formular-`key` (Review R4 F6): der
 * Key kommt zentral aus {@link ExpenseForm.nextKey} — eine Index-
 * basierte Zuordnung würde nach Löschen von Zeilen Kollisionen erzeugen
 * (doppelte [id] im Template).
 */
function newSplitRow(key: number): SplitRowState {
  const random =
    typeof crypto !== 'undefined' && 'randomUUID' in crypto
      ? crypto.randomUUID()
      : Math.random().toString(36).slice(2);
  return {
    key,
    id: `split-${random}`,
    amountText: '',
    poolId: '',
    categoryId: '',
    taxMode: 'inherit',
    taxCategoryId: '',
    partialCapture: false,
    markedText: '',
    note: '',
    taxNote: '',
    prefilled: false,
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
  /**
   * Aufteilungs-Zustand beim Laden der Ursprungsbuchung (Review R3 P2):
   * bleibt er unverändert, sendet die Korrektur `splits: null` — der
   * Server hält die Original-Splits (keine stillen Id-/Notizen-Wechsel).
   */
  private allocationSnapshot: string | null = null;

  date = todayIso();
  accountId = '';
  note = '';
  totalText = '';
  splits: SplitRowState[] = [newSplitRow(this.nextKey())];
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
    this.pendingOperation = inject(PendingOperation);
    this.opPrefix = this.isCorrection ? 'corr' : 'exp';
    this.outcomeUncertain = this.pendingOperation.outcomeUncertain(
      this.opPrefix,
    );
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
      // Erst Katalog, dann Detail: die wirksame Steuerkennzeichnung der
      // Vorbelegung (und damit der Aufteilungs-Fingerprint) hängt vom
      // Kategorie-Katalog ab (Review R3 P2).
      await this.catalog.load();
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
    this.splits = detail.splits.map((split) => {
      const row = newSplitRow(this.nextKey());
      // Stabile Split-Identität über Revisionen hinweg (Review R3 P2):
      // die Original-Id bleibt erhalten, damit eine Korrektur, die die
      // Aufteilung nicht ändert, die Splits (inkl. Steuerkennzeichnung
      // und -Notizen) nicht still ersetzt.
      row.id = split.id;
      row.prefilled = true;
      row.amountText = toEuroText(split.amount);
      row.poolId = split.poolId;
      row.categoryId = split.categoryId;
      if (split.tax) {
        row.taxMode = split.tax.relevant ? 'relevant' : 'irrelevant';
        row.taxCategoryId = split.tax.taxCategoryId ?? '';
        row.partialCapture = split.tax.markedAmount !== null;
        row.markedText = toEuroText(split.tax.markedAmount);
        row.taxNote = split.tax.note ?? '';
      }
      row.note = split.note ?? '';
      return row;
    });
    if (this.splits.length === 0) {
      this.splits = [newSplitRow(this.nextKey())];
    }
    // Referenz-Zustand der Aufteilung für den Skalar-Korrektur-Pfad:
    // ändert sich nur die Buchungsnote (oder Datum/Konto), sendet die
    // Korrektur splits: null — der Server hält die Original-Splits.
    this.allocationSnapshot = this.allocationFingerprint();
  }

  /**
   * Fingerprint der aktuellen Aufteilung (Gesamtbetrag + Splits) —
   * identisch, wenn der Nutzer die Aufteilung nicht geändert hat.
   */
  private allocationFingerprint(): string {
    return allocationFingerprint({
      totalCents: this.totalCents(),
      splits: this.splits.map((row) => ({
        id: row.id,
        amountCents: parseEuroToCents(row.amountText),
        poolId: row.poolId,
        categoryId: row.categoryId,
        note: row.note.trim(),
        tax: {
          relevant: this.effectiveTaxRelevant(row),
          taxCategoryId:
            this.effectiveTaxRelevant(row) === true
              ? row.taxCategoryId
              : null,
          markedAmountCents:
            this.effectiveTaxRelevant(row) === true && row.partialCapture
              ? parseEuroToCents(row.markedText)
              : null,
          note: row.taxNote.trim() === '' ? null : row.taxNote.trim(),
        },
      })),
    });
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
    this.splits = [...this.splits, newSplitRow(this.nextKey())];
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
    } else if (mode === 'inherit') {
      // Review R4 F4: zurück auf die Kategorie-Voreinstellung — die
      // explizit gewählte Steuerkategorie/Teilmenge ist wieder inaktiv.
      row.taxCategoryId = '';
      row.partialCapture = false;
      row.markedText = '';
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
    if (row.taxNote.length > MAX_TAX_NOTE_LENGTH) {
      return `Steuer-Notiz zu lang (max. ${MAX_TAX_NOTE_LENGTH} Zeichen)`;
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

  /**
   * Stabile Operation-Id (Review R3 P1 / R4 F1 / R5-1): nach verlorener
   * Antwort sendet der nächste Klick mit unveränderten Daten dieselbe
   * Operation (gleiche Id + Payload) — idempotente Quittung statt
   * doppelte Buchung. Geänderte Daten bei unklarem Ausgang blockieren
   * die Einreichung, bis die hängende Operation abgeglichen und manuell
   * freigegeben ist.
   *
   * Der Zustand liegt im Root-Service (nicht in der Komponenteninstanz):
   * der 401-Login-Redirect zerstört das Formular, behält aber die
   * gehaltene Operation (Review R5-1).
   *
   * Initialisierung im Konstruktor: hängt von `isCorrection` ab, das
   * erst im Konstruktor gesetzt ist (Feld-Initialisierer laufen vorher).
   */
  private readonly pendingOperation: PendingOperation;
  private readonly opPrefix: string;
  readonly outcomeUncertain: Signal<boolean>;

  /**
   * Fingerprint der Payload ohne Operation-Id (Review R4 F1):
   * unveränderte Daten → dieselbe Id (idempotentes Wiedersenden).
   */
  private payloadFingerprint(command: ExpenseCommand): string {
    return JSON.stringify({
      isCorrection: this.isCorrection,
      correctionId: this.correctionId,
      date: command.date,
      totalAmount: command.totalAmount,
      accountId: command.accountId,
      note: command.note,
      // Review R5-1: bei Neuanlagen sind die Split-Ids
      // Formularinstanz-spezifisch; nach einem 401-Redirect erzeugt
      // die erneute Erfassung derselben Daten neue Ids. Ohne die Ids
      // im Fingerprint bleibt die Operation als identisch erkennbar —
      // und buildCommand() leitet die Ids deterministisch aus dem
      // Zeileninhalt ab, sodass das Wiedersenden wirklich dieselbe
      // Payload trägt (gleiche Operation-Id mit anderem Inhalt
      // würde der Server ablehnen).
      splits: this.isCorrection
        ? command.splits
        : command.splits?.map((split) => ({
            amount: split.amount,
            poolId: split.poolId,
            categoryId: split.categoryId,
            note: split.note,
            tax: split.tax,
          })),
      expectedBaseRevision: command.expectedBaseRevision,
    });
  }

  private buildCommand(): ExpenseCommand {
    const splits: ExpenseSplitCommand[] = this.splits.map(
      (row, index) => {
        let tax: ExpenseSplitCommand['tax'] = null;
        if (row.taxMode === 'relevant') {
          tax = {
            relevant: true,
            taxCategoryId: row.taxCategoryId,
            markedAmount: row.partialCapture
              ? (parseEuroToCents(row.markedText) as number)
              : null,
            note: row.taxNote.trim() === '' ? null : row.taxNote.trim(),
          };
        } else if (row.taxMode === 'irrelevant') {
          tax = {
            relevant: false,
            taxCategoryId: null,
            markedAmount: null,
            // Review R5-3: die Notiz bleibt auch bei „nicht
            // relevant“ erhalten (Domain-Regel: Notiz unabhängig
            // vom Relevanz-Flag).
            note: row.taxNote.trim() === '' ? null : row.taxNote.trim(),
          };
        } else if (row.prefilled) {
          // Review R5-2: eine bestehende Zeile, die auf „Wie
          // Voreinstellung“ belassen wurde, darf in der Korrektur
          // nicht tax: null senden — null heißt dort „Original-
          // Kennzeichnung behalten“ und würde z. B. nach einer
          // Kategorienänderung die Kennzeichnung der alten Kategorie
          // still halten. Statt dessen wird die wirksame Voreinstellung
          // der aktuellen Kategorie explizit materialisiert (gleiche
          // Semantik wie bei der Neuanlage).
          tax = this.materializePresetTax(row);
        }
        // Beträge wurden zuvor in splitIssue() validiert.
        const amount = parseEuroToCents(row.amountText) as number;
        return {
          id: this.splitIdFor(row, index),
          amount,
          poolId: row.poolId,
          categoryId: row.categoryId,
          note: row.note.trim() === '' ? null : row.note.trim(),
          tax,
        };
      },
    );
    return {
      // Review R4 F1: wird in submit() per PendingOperation vergeben.
      id: '',
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

  /**
   * Split-Id für die Payload (Review R5-1):
   * - vorgefüllte Zeilen (Korrektur): die originale Split-Id — stabile
   *   Identität über Revisionen hinweg;
   * - alle anderen (Neuanlage, neu hinzugefügte Zeilen): deterministisch
   *   aus dem Zeileninhalt abgeleitet. Nach einem 401-Redirect erzeugt
   *   die erneute Erfassung derselben Daten in einer neuen Formular-
   *   instanz dieselben Ids — die wiedergesendete Payload ist damit
   *   identisch, statt „gleiche Operation-Id mit anderem Inhalt“ zu
   *   sein, was der Server ablehnt.
   */
  private splitIdFor(row: SplitRowState, index: number): string {
    if (this.isCorrection && row.prefilled) {
      return row.id;
    }
    return `split-${index}-${stableSplitHash(row)}`;
  }

  /**
   * Explizite Materialisierung der wirksamen Kategorie-Voreinstellung
   * als Steuerkennzeichnung (Review R5-2) — gleiche Werte wie die
   * Server-Materialisierung (MaterializeEffectivePreset) für neue
   * Teilposten: keine Voreinstellung oder „nicht festgelegt“ →
   * explizit nicht relevant (die einzige explizit darstellbare
   * „keine Kennzeichnung“ in der Korrektur — tax: null würde die
   * ursprüngliche Kennzeichnung behalten); sonst Relevanz +
   * Steuerkategorie der Voreinstellung, ohne Teilbetrag und ohne Notiz.
   */
  private materializePresetTax(row: SplitRowState): ExpenseSplitCommand['tax'] {
    const preset = this.effectivePreset(row);
    if (preset !== null && preset.taxRelevant === true) {
      return {
        relevant: true,
        taxCategoryId: preset.taxCategoryId,
        markedAmount: null,
        note: null,
      };
    }
    return { relevant: false, taxCategoryId: null, markedAmount: null, note: null };
  }

  async submit(): Promise<void> {
    if (this.busy() || this.formIssue() !== null) {
      return;
    }
    this.busy.set(true);
    this.submitError.set(null);
    const command = this.buildCommand();
    // Review R4 F1: unklarer Ausgang + geänderte Daten → Blocken.
    // Eine neue Id würde eine zweite Buchung erzeugen können, wenn die
    // alte Operation bereits committet ist.
    const id = this.pendingOperation.nextId(
      this.opPrefix,
      this.payloadFingerprint(command),
    );
    if (id === null) {
      this.submitError.set({
        code: 'uncertain_operation_blocked',
        message:
          'Einreichung blockiert: Der Ausgang des letzten Versuchs ist unklar und die Daten wurden seitdem geändert. Prüfe die Buchungsliste, ob der vorherige Eintrag gebucht wurde (ggf. korrigieren oder stornieren), und setze die Operation unten zurück, bevor du neue Daten einreichst.',
        expectedBaseRevision: null,
        actualBaseRevision: null,
      });
      this.busy.set(false);
      return;
    }
    command.id = id;
    try {
      const created = this.isCorrection
        ? await this.submitCorrection(command)
        : await JournalWriteEndpointsService.createExpense(command);
      this.pendingOperation.release(this.opPrefix);
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
      // Definitive Ablehnung (4xx): neue Id beim nächsten Versuch —
      // aber nur, wenn dieser Versuch nicht bereits unklaren Ausgang
      // hatte: ein unklarer Vorversuch kann bereits committet sein
      // (Commit, dann Antwortverlust, dann z. B. 401 wegen
      // abgelaufener Session). In dem Fall bleibt die Operation
      // gehalten; der nächste Klick mit unveränderter Payload sendet
      // dieselbe Operation und erhält die idempotente Quittung
      // (Review R5-1). Ansonsten (keine Antwort, 5xx): Ausgang unklar
      // → dieselbe Id + Payload wiedersenden.
      const definitive =
        error instanceof ApiErrorClass &&
        error.status >= 400 &&
        error.status < 500;
      if (definitive) {
        if (!this.outcomeUncertain()) {
          this.pendingOperation.release(this.opPrefix);
        }
      } else {
        this.pendingOperation.markOutcomeUncertain(this.opPrefix);
      }
      this.busy.set(false);
    }
  }

  /** Rücksetzen: neue Operation-Id für die nächste Einreichung (Review R4 F1). */
  resetPendingOperation(): void {
    this.pendingOperation.release(this.opPrefix);
  }

  /**
   * Korrektur absenden (Review R3 P2): bleibt die Aufteilung (Gesamt-
   * betrag + Splits) unverändert, werden `splits` und `totalAmount`
   * als null gesendet — der Server hält die Original-Splits mit ihren
   * Ids, Steuerkennzeichnungen und -Notizen; Datum, Notiz und Konto
   * sind unabhängig und werden immer gesendet. (Domain-Regel: Splits
   * nur zusammen mit neuem Gesamtbetrag, Gesamtbetrag ohne Splits nur
   * wenn er dem Original entspricht — beides gilt hier exakt.)
   */
  private async submitCorrection(
    command: ExpenseCommand,
  ): Promise<{ idempotent: boolean }> {
    const allocationUnchanged =
      this.allocationSnapshot !== null &&
      this.allocationFingerprint() === this.allocationSnapshot;
    return JournalWriteEndpointsService.correctBooking(
      this.correctionId as string,
      {
        operationId: command.id,
        expectedBaseRevision: command.expectedBaseRevision as number,
        date: command.date,
        note: command.note,
        accountId: command.accountId,
        totalAmount: allocationUnchanged ? null : command.totalAmount,
        splits: allocationUnchanged ? null : command.splits,
        allocations: null,
      },
    );
  }

  async goBack(): Promise<void> {
    await this.router.navigate(['/']);
  }

  // ---------- Anzeige für das Template ----------

  formatCents = formatCents;
}

export interface AllocationSplitState {
  id: string;
  amountCents: number | null;
  poolId: string;
  categoryId: string;
  note: string;
  /** Wirksame Steuerkennzeichnung (nach Kategorie-Voreinstellung aufgelöst). */
  tax: {
    relevant: boolean | null;
    taxCategoryId: string | null;
    markedAmountCents: number | null;
    /** Steuer-Notiz (Review R4 F5) — Teil des Fingerprint: wird sie
     * geändert, darf die Korrektur nicht den Skalar-Pfad nehmen. */
    note: string | null;
  };
}

export interface AllocationState {
  totalCents: number | null;
  splits: AllocationSplitState[];
}

/**
 * Fingerprint der Aufteilung (Review R3 P2 / R4 F5): Gesamtbetrag +
 * Splits mit Id, Betrag, Pool, Kategorie, Notiz und wirksamer
 * Steuerkennzeichnung (inkl. Steuer-Notiz). Bleibt er vom Vorladen
 * bis zum Absenden unverändert,
 * sendet die Korrektur `splits: null` / `totalAmount: null` und der
 * Server hält die Original-Splits.
 *
 * Bewusst enthalten ist die Split-Notiz: wird sie geändert, darf die
 * Korrektur nicht den Skalar-Pfad nehmen, sonst ginge die Änderung
 * still verloren.
 */
export function allocationFingerprint(state: AllocationState): string {
  return JSON.stringify(state);
}

/**
 * Deterministische Kurzsignatur des Zeileninhalts (Review R5-1): bei
 * Neuanlagen wird die Split-Id daraus abgeleitet, sodass die erneute
 * Erfassung derselben Daten (z. B. nach einem 401-Login-Redirect in
 * einer neuen Formularinstanz) dieselbe Payload erzeugt. Beträge sind
 * normalisiert (Cent), damit „100,00“ und „100.00“ dieselbe Signatur
 * liefern. FNV-1a genügt hier (Unterscheidbarkeit, kein Security-Aspekt);
 * der Zeilenindex trennt zwei identische Zeilen.
 */
export function stableSplitHash(row: SplitRowState): string {
  const content = JSON.stringify({
    amountCents: parseEuroToCents(row.amountText),
    poolId: row.poolId,
    categoryId: row.categoryId,
    note: row.note.trim(),
    taxMode: row.taxMode,
    taxCategoryId: row.taxCategoryId,
    partialCapture: row.partialCapture,
    markedCents: row.partialCapture ? parseEuroToCents(row.markedText) : null,
    taxNote: row.taxNote.trim(),
  });
  let hash = 0x811c9dc5;
  for (let i = 0; i < content.length; i++) {
    hash ^= content.charCodeAt(i);
    hash = Math.imul(hash, 0x01000193);
  }
  return (hash >>> 0).toString(36);
}
