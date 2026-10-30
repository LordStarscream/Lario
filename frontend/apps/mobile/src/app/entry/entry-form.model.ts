/**
 * Batch D (P4) — Reines Formularmodell der mobilen Ausgabe-Erfassung.
 *
 * Keine Angular-Abhängigkeit (keine injizierbaren Services, keine
 * Templates): die Funktionen sind direkt unit-testbar und teilen die
 * Fachregeln mit dem Repository (`db/bookings.repository.ts`,
 * Batch B). Der Speicherpfad ist bewusst zweistufig:
 *
 *  1. `validateEntryForm` prüft Formular + Stammdaten-Snapshot
 *     (Begriffe, die das Repository nicht kennt: Archiv-Status,
 *     unbekannte Steuerkategorie, leere Splits).
 *  2. `toLocalInput` baut das `LocalExpenseInput`; beim Speichern
 *     validiert `BookingsRepository.save` erneut (Doppelte Sicherheit
 *     — die Repository-Validierung ist die maßgebliche).
 *
 * EUR-Beträge sind durchweg ganzzahlige Centwerte (`parseAmount`),
 * kein Float.
 */
import { formatCents, parseAmount } from '../amount';
import type { MasterDataSnapshot } from '../db/snapshot.repository';
import type {
  LocalExpenseInput,
  LocalSplitInput,
  LocalTaxInput,
} from '../db/expense-payload';
import type { TaxPresetDto } from '../../generated';

/**
 * Formular-Feldfehler (Design D7): jede Meldung ist eindeutig und
 * benennt das betroffene Feld/Split.
 */
export type EntryFormError =
  | { code: 'invalid_date'; message: string }
  | { code: 'missing_amount'; message: string }
  | { code: 'invalid_amount'; message: string }
  | { code: 'missing_account'; message: string }
  | { code: 'missing_split'; message: string }
  | { code: 'invalid_split_amount'; message: string }
  | { code: 'missing_pool'; message: string }
  | { code: 'missing_category'; message: string }
  | { code: 'split_sum_mismatch'; message: string }
  | { code: 'unknown_account'; message: string }
  | { code: 'unknown_pool'; message: string }
  | { code: 'unknown_category'; message: string }
  | { code: 'unknown_tax_category'; message: string }
  | { code: 'archived_account'; message: string }
  | { code: 'archived_pool'; message: string }
  | { code: 'archived_category'; message: string }
  | { code: 'archived_tax_category'; message: string }
  | { code: 'missing_tax_category'; message: string }
  | { code: 'marked_amount_range'; message: string };

/** Steuerkennzeichnung eines Splits (Formularstand). */
export interface EntryTaxModel {
  relevant: boolean;
  taxCategoryId: string | null;
  taxCategoryName: string | null;
  /** Eingabetext für die markierte Summe (Standard = Splitbetrag). */
  markedAmountInput: string;
  note: string;
}

/** Ein Split im Formular (Formular-ID `id`, kein Fach-UUID). */
export interface EntrySplitModel {
  /** Formular-interne ID (z. B. "s1"); wird beim Speichern ersetzt. */
  id: string;
  /** Stabile Position im Split-Array (Sortierung, Zuordnung). */
  key: string;
  amountInput: string;
  poolId: string;
  categoryId: string;
  note: string;
  tax: EntryTaxModel;
}

/** Formularzustand des Erfassen-Formulars. */
export interface EntryFormModel {
  date: string;
  amountInput: string;
  accountId: string;
  note: string;
  splits: EntrySplitModel[];
}

/**
 * Baut ein neues, leeres Split-Modell. `key` ist die stabile Position
 * im Split-Array (Sortierung, Zuordnung); `id` ist die
 * formular-interne ID (beim Speichern durch `normalizeSplitIds`
 * ersetzt).
 */
export function createDefaultSplit(key: string): EntrySplitModel {
  return {
    id: key,
    key,
    amountInput: '',
    poolId: '',
    categoryId: '',
    note: '',
    tax: {
      relevant: false,
      taxCategoryId: null,
      taxCategoryName: null,
      markedAmountInput: '',
      note: '',
    },
  };
}

/** Heutiges Datum (Europe/Berlin) als `YYYY-MM-DD`. */
export function todayInBerlin(): string {
  const parts = new Intl.DateTimeFormat('en-CA', {
    timeZone: 'Europe/Berlin',
    year: 'numeric',
    month: '2-digit',
    day: '2-digit',
  }).formatToParts(new Date());
  const year = parts.find((p) => p.type === 'year')?.value ?? '';
  const month = parts.find((p) => p.type === 'month')?.value ?? '';
  const day = parts.find((p) => p.type === 'day')?.value ?? '';
  return `${year}-${month}-${day}`;
}

/** Neues, leeres Formular (Standard: heute, 1 Split). */
export function createDefaultForm(date: string): EntryFormModel {
  return {
    date,
    amountInput: '',
    accountId: '',
    note: '',
    splits: [createDefaultSplit('s1')],
  };
}

/**
 * Gültiges Fachdatum `YYYY-MM-DD` (wie `isValidDate` im
 * Repository — gleiche Regex, gleiche Kalenderregeln).
 */
function isValidDate(s: string): boolean {
  if (!/^\d{4}-\d{2}-\d{2}$/.test(s)) {
    return false;
  }
  const [y, m, d] = s.split('-').map(Number);
  const dt = new Date(Date.UTC(y, m - 1, d));
  return (
    dt.getUTCFullYear() === y && dt.getUTCMonth() === m - 1 && dt.getUTCDate() === d
  );
}

/**
 * Vollständige Formularvalidierung (Design D7). Die Reihenfolge der
 * Meldungen ist stabil: Datum, Gesamt, Konto, Splits (pro Split:
 * Betrag, Pool, Kategorie, Steuer), Summen-Abgleich, Archiv-Status.
 *
 * Die Snapshot-Prüfungen (unbekannt/archiviert) sind UI-Ebene: das
 * Repository kennt die Stammdaten nicht und validiert nur Struktur
 * und Summen.
 */
export function validateEntryForm(
  model: EntryFormModel,
  snapshot: MasterDataSnapshot | null,
): EntryFormError[] {
  const errors: EntryFormError[] = [];

  if (!isValidDate(model.date)) {
    errors.push({ code: 'invalid_date', message: 'Datum ist ungültig oder fehlt' });
  }

  const totalCents = parseAmount(model.amountInput);
  if (totalCents === null || totalCents === undefined) {
    errors.push({ code: 'missing_amount', message: 'Gesamtbetrag fehlt' });
  } else if (totalCents <= 0) {
    errors.push({ code: 'invalid_amount', message: 'Gesamtbetrag muss größer als 0 sein' });
  }

  if (model.accountId === '') {
    errors.push({ code: 'missing_account', message: 'Konto fehlt' });
  } else if (snapshot !== null && !snapshot.accounts.has(model.accountId)) {
    errors.push({ code: 'unknown_account', message: 'Konto ist nicht im Snapshot' });
  }

  if (model.splits.length === 0) {
    errors.push({ code: 'missing_split', message: 'Mindestens ein Split ist erforderlich' });
  }

  let splitSum = 0;
  for (const split of model.splits) {
    const label = `Split ${split.key}: `;

    const cents = parseAmount(split.amountInput);
    const amountValid = cents !== null && cents !== undefined && cents > 0;
    if (!amountValid) {
      errors.push({ code: 'invalid_split_amount', message: `${label}Betrag fehlt oder ist ungültig` });
    } else {
      splitSum += cents;
    }

    if (split.poolId === '') {
      errors.push({ code: 'missing_pool', message: `${label}Pool fehlt` });
    } else if (snapshot !== null && !snapshot.pools.has(split.poolId)) {
      errors.push({ code: 'unknown_pool', message: `${label}Pool ist nicht im Snapshot` });
    }

    if (split.categoryId === '') {
      errors.push({ code: 'missing_category', message: `${label}Kategorie fehlt` });
    } else if (snapshot !== null && !snapshot.categories.has(split.categoryId)) {
      errors.push({ code: 'unknown_category', message: `${label}Kategorie ist nicht im Snapshot` });
    }

    if (split.tax.relevant) {
      if (split.tax.taxCategoryId === null) {
        errors.push({
          code: 'missing_tax_category',
          message: `${label}Steuerkategorie fehlt (Steuer relevant markiert)`,
        });
      } else if (snapshot !== null && !snapshot.taxCategories.has(split.tax.taxCategoryId)) {
        errors.push({
          code: 'unknown_tax_category',
          message: `${label}Steuerkategorie ist nicht im Snapshot`,
        });
      }
      if (split.tax.markedAmountInput !== '') {
        const marked = parseAmount(split.tax.markedAmountInput);
        if (marked === null || marked === undefined) {
          errors.push({
            code: 'marked_amount_range',
            message: `${label}Markierter Betrag ist ungültig`,
          });
        } else if (amountValid && (marked < 0 || marked > cents!)) {
          errors.push({
            code: 'marked_amount_range',
            message: `${label}Markierter Betrag muss zwischen 0 und Splitbetrag (${formatCents(cents!)} €) liegen`,
          });
        }
      }
    }
  }

  if (totalCents !== null && totalCents !== undefined && splitSum !== totalCents) {
    errors.push({
      code: 'split_sum_mismatch',
      message: `Split-Summe ${formatCents(splitSum)} € ≠ Gesamtbetrag ${formatCents(totalCents)} €`,
    });
  }

  // Archiv-Status (nur mit Snapshot; "aktiv" ist der einzige
  // verwertbare Status — Design D7).
  if (snapshot !== null) {
    if (model.accountId !== '') {
      const account = snapshot.accounts.get(model.accountId);
      if (account !== undefined && account.status !== 'active') {
        errors.push({
          code: 'archived_account',
          message: `Konto "${account.name}" ist archiviert`,
        });
      }
    }
    for (const split of model.splits) {
      if (split.poolId !== '') {
        const pool = snapshot.pools.get(split.poolId);
        if (pool !== undefined && pool.status !== 'active') {
          errors.push({
            code: 'archived_pool',
            message: `Pool "${pool.name}" ist archiviert`,
          });
        }
      }
      if (split.categoryId !== '') {
        const category = snapshot.categories.get(split.categoryId);
        if (category !== undefined && category.status !== 'active') {
          errors.push({
            code: 'archived_category',
            message: `Kategorie "${category.name}" ist archiviert`,
          });
        }
      }
      if (split.tax.relevant && split.tax.taxCategoryId !== null) {
        const taxCategory = snapshot.taxCategories.get(split.tax.taxCategoryId);
        if (taxCategory !== undefined && taxCategory.status !== 'active') {
          errors.push({
            code: 'archived_tax_category',
            message: `Steuerkategorie "${taxCategory.name}" ist archiviert`,
          });
        }
      }
    }
  }

  return errors;
}

/**
 * Baut das `LocalExpenseInput` aus einem gültigen Formular.
 *
 * - `normalizeSplitIds`-konform: die Split-IDs sind
 *   formular-intere Form-IDs und werden beim Speichern ersetzt
 *   (`BookingsRepository.save` macht das intern).
 * - Steuer: `relevant` → vollständiger Tax-Input; neutral →
 *   `{ relevant: false, ... }` (nicht `null` — das Repository
 *   speichert dann `tax_relevant = 0`).
 * - `markedAmountInput`: leer → `null`; sonst `parseAmount`.
 *
 * @returns `null`, wenn das Formular oder der Snapshot ungültig sind
 *   (der Aufrufer prüft vorab per `validateEntryForm`).
 */
export function toLocalInput(
  model: EntryFormModel,
  snapshot: MasterDataSnapshot | null,
): LocalExpenseInput | null {
  if (snapshot === null) {
    return null;
  }
  if (validateEntryForm(model, snapshot).length > 0) {
    return null;
  }
  const totalCents = parseAmount(model.amountInput);
  if (totalCents === null || totalCents === undefined) {
    return null;
  }
  const splits: LocalSplitInput[] = [];
  for (const s of model.splits) {
    const cents = parseAmount(s.amountInput);
    if (cents === null || cents === undefined) {
      return null;
    }
    const tax: LocalTaxInput = s.tax.relevant
      ? {
          relevant: true,
          taxCategoryId: s.tax.taxCategoryId,
          taxCategoryName: s.tax.taxCategoryName,
          markedAmountCents:
            s.tax.markedAmountInput === ''
              ? null
              : parseAmount(s.tax.markedAmountInput) ?? null,
          note: s.tax.note.trim() === '' ? null : s.tax.note.trim(),
        }
      : {
          relevant: false,
          taxCategoryId: null,
          taxCategoryName: null,
          markedAmountCents: null,
          note: s.tax.note.trim() === '' ? null : s.tax.note.trim(),
        };
    splits.push({
      // Formular-interne ID (z. B. "s1"); `normalizeSplitIds` im
      // Repository ersetzt sie durch fachliche UUIDs.
      id: s.id,
      amountCents: cents,
      poolId: s.poolId,
      categoryId: s.categoryId,
      note: s.note.trim() === '' ? null : s.note.trim(),
      tax,
    });
  }
  return {
    date: model.date,
    totalAmountCents: totalCents,
    accountId: model.accountId,
    note: model.note.trim() === '' ? null : model.note.trim(),
    splits,
  };
}

/**
 * Quick-Action "Halbe" (Design D4): setzt den Gesamtbetrag auf die
 * Hälfte (gerundet), teilt die Summe auf die Splits auf (Rest auf den
 * ersten). Die Splits müssen gültige Beträge haben, sonst ist die
 * Aktion nicht möglich.
 *
 * Rundungsregel: `Math.floor(total/2)`; jeder Split erhält
 * `Math.floor(split/2)`, der Rest (0 oder 1 Cent) geht auf den
 * ersten Split.
 *
 * Zusätzlich wird `sum(splits) === total` gefordert: ohne diese
 * Präcondition könnte der Rest negativ werden und einen negativen
 * Split-Betrag erzeugen (`formatCents` wirft dann eine Exception).
 */
export function applyHalfSplit(model: EntryFormModel): EntryFormModel | null {
  const totalCents = parseAmount(model.amountInput);
  if (totalCents === null || totalCents === undefined || totalCents <= 0) {
    return null;
  }
  if (model.splits.length === 0) {
    return null;
  }
  const amounts = model.splits.map((s) => {
    const c = parseAmount(s.amountInput);
    return c !== null && c !== undefined && c > 0 ? c : -1;
  });
  if (amounts.some((c) => c < 0)) {
    return null;
  }
  if (amounts.reduce((a, b) => a + b, 0) !== totalCents) {
    return null;
  }
  const half = Math.floor(totalCents / 2);
  const halfed = amounts.map((c) => Math.floor(c / 2));
  // Rest (0 oder 1 Cent, s. o.) auf den ersten Split.
  halfed[0] += half - halfed.reduce((a, b) => a + b, 0);
  return {
    ...model,
    amountInput: formatCents(half),
    splits: model.splits.map((s, i) => clampMarkedAmount(s, halfed[i])),
  };
}

/**
 * Quick-Action "Gleich aufteilen" (Design D4): teilt den
 * Gesamtbetrag gleichmäßig auf die Splits auf (Rest-Cents auf die
 * ersten Splits).
 *
 * Rundungsregel: `base = Math.floor(total / n)`; die ersten
 * `total % n` Splits erhalten `base + 1` Cent.
 */
export function applyEqualSplit(model: EntryFormModel): EntryFormModel | null {
  const totalCents = parseAmount(model.amountInput);
  if (totalCents === null || totalCents === undefined || totalCents <= 0) {
    return null;
  }
  const n = model.splits.length;
  if (n === 0) {
    return null;
  }
  const base = Math.floor(totalCents / n);
  const remainder = totalCents % n;
  return {
    ...model,
    splits: model.splits.map((s, i) =>
      clampMarkedAmount(s, base + (i < remainder ? 1 : 0)),
    ),
  };
}

/**
 * Quick-Action-Hilfe: baut das Split-Objekt mit dem neuen Betrag und
 * klammert die markierte Steuer-Summe auf den neuen Splitbetrag
 * ein (leeres `markedAmountInput` bleibt leer — der Standard
 * „= Splitbetrag“ greift dann beim Speichern).
 */
function clampMarkedAmount(split: EntrySplitModel, newCents: number): EntrySplitModel {
  const marked = parseAmount(split.tax.markedAmountInput);
  return {
    ...split,
    amountInput: formatCents(newCents),
    tax: {
      ...split.tax,
      markedAmountInput:
        marked === null || marked === undefined
          ? split.tax.markedAmountInput
          : formatCents(Math.min(marked, newCents)),
    },
  };
}

/**
 * Übernimmt den `taxPreset` der gewählten Kategorie in die
 * Steuerkennzeichnung (Design D7):
 *
 * - `taxPreset` null oder `taxRelevant` null → keine Übernahme
 *   (Schalter bleibt, wie er ist).
 * - `taxRelevant` false → Schalter aus, Felder leer.
 * - `taxRelevant` true → Schalter ein, Kategorie übernehmen,
 *   `markedAmountInput` = Splitbetrag (formatiert) oder leer, wenn
 *   der Splitbetrag ungültig ist.
 *
 * Manuelle Änderungen nach der Übernahme sind möglich (die
 * Übernahme ist kein Lock).
 *
 * Hinweis: Das generierte `TaxPresetDto` verwendet `taxRelevant`
 * (`boolean | null`), nicht `relevant` (Abweichung zur Task-Spez).
 */
export function applyTaxPreset(
  split: EntrySplitModel,
  preset: TaxPresetDto | null | undefined,
): EntrySplitModel {
  if (preset === null || preset === undefined || preset.taxRelevant === null) {
    return split;
  }
  if (!preset.taxRelevant) {
    return {
      ...split,
      tax: {
        relevant: false,
        taxCategoryId: null,
        taxCategoryName: null,
        markedAmountInput: '',
        note: split.tax.note,
      },
    };
  }
  const splitCents = parseAmount(split.amountInput);
  return {
    ...split,
    tax: {
      relevant: true,
      taxCategoryId: preset.taxCategoryId,
      taxCategoryName: preset.taxCategoryName,
      markedAmountInput:
        splitCents === null || splitCents === undefined || splitCents <= 0
          ? ''
          : formatCents(splitCents),
      note: split.tax.note,
    },
  };
}

/**
 * Quick-Action "Rest in letzten Split" (Design D8): setzt den
 * letzten Split auf `Gesamtbetrag − Σ(alle anderen gültigen Splits)`.
 *
 * - Nur möglich, wenn der Gesamtbetrag gültig ist und mindestens ein
 *   Split vorhanden ist.
 * - Rest < 1 Cent (auch negativ, wenn die Splits das Total
 *   überschreiten) → letzter Split wird geleert.
 * - Sonst: unverändert.
 */
export function fillLastSplit(model: EntryFormModel): EntryFormModel {
  const totalCents = parseAmount(model.amountInput);
  if (totalCents === null || totalCents === undefined || model.splits.length === 0) {
    return model;
  }
  let otherSum = 0;
  for (let i = 0; i < model.splits.length - 1; i++) {
    const cents = parseAmount(model.splits[i].amountInput);
    if (cents !== null && cents !== undefined && cents > 0) {
      otherSum += cents;
    }
  }
  const rest = totalCents - otherSum;
  const last = model.splits[model.splits.length - 1];
  const updatedLast: EntrySplitModel = {
    ...last,
    amountInput: rest >= 1 ? formatCents(rest) : '',
  };
  return {
    ...model,
    splits: [...model.splits.slice(0, -1), updatedLast],
  };
}

/**
 * Setzt den Steuer-Status eines Splits. Beim Umschalten auf "nicht
 * relevant" werden Steuerkategorie und markierter Betrag geleert
 * (Design D7: keine verwaisten Kennzeichnungsdaten).
 */
export function setTaxRelevant(
  model: EntryFormModel,
  splitKey: string,
  relevant: boolean,
): EntryFormModel {
  return {
    ...model,
    splits: model.splits.map((s) => {
      if (s.key !== splitKey) {
        return s;
      }
      if (relevant) {
        return { ...s, tax: { ...s.tax, relevant: true } };
      }
      return {
        ...s,
        tax: {
          ...s.tax,
          relevant: false,
          taxCategoryId: null,
          taxCategoryName: null,
          markedAmountInput: '',
        },
      };
    }),
  };
}

/**
 * Serialisiert das Formular als Entwurf-JSON (Draft-Dienst, Design D8).
 */
export function serializeDraft(model: EntryFormModel): string {
  return JSON.stringify(model);
}

/**
 * Parst ein Entwurf-JSON zurück in ein Formularmodell. Strukturelle
 * Guards: alle Pflichtfelder müssen die erwarteten Typen haben, sonst
 * `null` (beschädigter Entwurf — der Aufrufer verwirft ihn).
 *
 * Hinweis: das committede `EntryFormModel` hat kein Versionsfeld; die
 * Guards sind rein strukturell (Greenfield — keine Altdaten).
 */
export function parseDraft(json: string): EntryFormModel | null {
  let raw: unknown;
  try {
    raw = JSON.parse(json);
  } catch {
    return null;
  }
  if (raw === null || typeof raw !== 'object' || Array.isArray(raw)) {
    return null;
  }
  const obj = raw as Record<string, unknown>;
  if (
    typeof obj['date'] !== 'string' ||
    typeof obj['amountInput'] !== 'string' ||
    typeof obj['accountId'] !== 'string' ||
    typeof obj['note'] !== 'string' ||
    !Array.isArray(obj['splits'])
  ) {
    return null;
  }
  const splits: EntrySplitModel[] = [];
  for (const item of obj['splits'] as unknown[]) {
    if (item === null || typeof item !== 'object' || Array.isArray(item)) {
      return null;
    }
    const split = item as Record<string, unknown>;
    const tax = split['tax'];
    if (
      typeof split['id'] !== 'string' ||
      typeof split['key'] !== 'string' ||
      typeof split['amountInput'] !== 'string' ||
      typeof split['poolId'] !== 'string' ||
      typeof split['categoryId'] !== 'string' ||
      typeof split['note'] !== 'string' ||
      tax === null ||
      typeof tax !== 'object' ||
      Array.isArray(tax)
    ) {
      return null;
    }
    const taxObj = tax as Record<string, unknown>;
    if (
      typeof taxObj['relevant'] !== 'boolean' ||
      (taxObj['taxCategoryId'] !== null && typeof taxObj['taxCategoryId'] !== 'string') ||
      (taxObj['taxCategoryName'] !== null && typeof taxObj['taxCategoryName'] !== 'string') ||
      typeof taxObj['markedAmountInput'] !== 'string' ||
      typeof taxObj['note'] !== 'string'
    ) {
      return null;
    }
    splits.push({
      id: split['id'] as string,
      key: split['key'] as string,
      amountInput: split['amountInput'] as string,
      poolId: split['poolId'] as string,
      categoryId: split['categoryId'] as string,
      note: split['note'] as string,
      tax: {
        relevant: taxObj['relevant'] as boolean,
        taxCategoryId: (taxObj['taxCategoryId'] as string | null) ?? null,
        taxCategoryName: (taxObj['taxCategoryName'] as string | null) ?? null,
        markedAmountInput: taxObj['markedAmountInput'] as string,
        note: taxObj['note'] as string,
      },
    });
  }
  return {
    date: obj['date'] as string,
    amountInput: obj['amountInput'] as string,
    accountId: obj['accountId'] as string,
    note: obj['note'] as string,
    splits,
  };
}
