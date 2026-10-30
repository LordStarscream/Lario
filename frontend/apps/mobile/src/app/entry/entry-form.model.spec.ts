// Batch D (P4) — Entry-Formularmodell: Validierung, Summen-Abgleich,
// Quick-Actions, taxPreset, toLocalInput. Reines TS, keine
// Angular-/DOM-Abhängigkeit.
import { describe, expect, it } from 'vitest';
import { parseAmount } from '../amount';
import type { MasterDataSnapshot } from '../db/snapshot.repository';
import type { TaxPresetDto } from '../../generated';
import {
  applyEqualSplit,
  applyHalfSplit,
  applyTaxPreset,
  createDefaultSplit,
  toLocalInput,
  todayInBerlin,
  validateEntryForm,
  type EntryFormModel,
  type EntrySplitModel,
} from './entry-form.model';

/** Snapshot: je ein aktives Konto/Pool/Kategorie/Steuerkategorie. */
function snapshot(): MasterDataSnapshot {
  return {
    accounts: new Map([
      ['acc-1', { id: 'acc-1', name: 'Giro', type: 'bank', isInterestBearing: false, status: 'active', baseRevision: 1 }],
    ]),
    pools: new Map([
      ['pool-1', { id: 'pool-1', name: 'Flex', status: 'active', baseRevision: 1 }],
      ['pool-arch', { id: 'pool-arch', name: 'Alt', status: 'archived', baseRevision: 2 }],
    ]),
    categories: new Map([
      ['cat-1', { id: 'cat-1', name: 'Essen', parentId: null, status: 'active', baseRevision: 1, taxPreset: null }],
    ]),
    taxCategories: new Map([
      ['tax-1', { id: 'tax-1', name: 'USt', parentId: null, status: 'active', baseRevision: 1 }],
    ]),
  };
}

/**
 * Gültiges Formular: 10,00 € Gesamt, ein Split (10,00 €), heute.
 * `date` wird per `todayInBerlin()` erzeugt — die Tests pruefen
 * nur Struktur und Summen, kein festes Datum.
 */
function validForm(): EntryFormModel {
  return {
    date: todayInBerlin(),
    amountInput: '10,00',
    accountId: 'acc-1',
    note: '',
    splits: [
      {
        id: 's1',
        key: 's1',
        amountInput: '10,00',
        poolId: 'pool-1',
        categoryId: 'cat-1',
        note: '',
        tax: { relevant: false, taxCategoryId: null, taxCategoryName: null, markedAmountInput: '', note: '' },
      },
    ],
  };
}

/**
 * Split mit gültigem Standardbetrag (10,00 € = `validForm`-Gesamt);
 * Overrides ersetzen einzelne Felder (auch `amountInput`).
 */
function withSplit(overrides: Partial<EntrySplitModel>): EntrySplitModel {
  return {
    ...createDefaultSplit('s1'),
    amountInput: '10,00',
    poolId: 'pool-1',
    categoryId: 'cat-1',
    ...overrides,
  };
}

describe('validateEntryForm (Batch D, P4)', () => {
  it('gültiges Formular ohne Steuer → keine Fehler', () => {
    expect(validateEntryForm(validForm(), snapshot())).toEqual([]);
  });

  it('ungültiges Datum → invalid_date', () => {
    const form = { ...validForm(), date: '2026-02-30' };
    const errors = validateEntryForm(form, snapshot());
    expect(errors.map((e) => e.code)).toContain('invalid_date');
  });

  it('leeres Datum → invalid_date', () => {
    const form = { ...validForm(), date: '' };
    expect(validateEntryForm(form, snapshot()).map((e) => e.code)).toContain('invalid_date');
  });

  it('leerer Gesamtbetrag → missing_amount', () => {
    const form = { ...validForm(), amountInput: '' };
    expect(validateEntryForm(form, snapshot()).map((e) => e.code)).toContain('missing_amount');
  });

  it('ungültiger Gesamtbetrag (3 Nachkommastellen) → missing_amount (parse undefined)', () => {
    const form = { ...validForm(), amountInput: '1,234' };
    expect(validateEntryForm(form, snapshot()).map((e) => e.code)).toContain('missing_amount');
  });

  it('Gesamtbetrag 0 → invalid_amount', () => {
    const form = { ...validForm(), amountInput: '0' };
    expect(validateEntryForm(form, snapshot()).map((e) => e.code)).toContain('invalid_amount');
  });

  it('leeres Konto → missing_account', () => {
    const form = { ...validForm(), accountId: '' };
    expect(validateEntryForm(form, snapshot()).map((e) => e.code)).toContain('missing_account');
  });

  it('unbekanntes Konto → unknown_account', () => {
    const form = { ...validForm(), accountId: 'acc-unknown' };
    expect(validateEntryForm(form, snapshot()).map((e) => e.code)).toContain('unknown_account');
  });

  it('kein Split → missing_split', () => {
    const form = { ...validForm(), splits: [] };
    expect(validateEntryForm(form, snapshot()).map((e) => e.code)).toContain('missing_split');
  });

  it('ungültiger Split-Betrag → invalid_split_amount', () => {
    const form = { ...validForm(), splits: [withSplit({ amountInput: 'abc' })] };
    expect(validateEntryForm(form, snapshot()).map((e) => e.code)).toContain('invalid_split_amount');
  });

  it('Split-Summe ≠ Gesamt → split_sum_mismatch (Summe zu hoch)', () => {
    const form = {
      ...validForm(),
      amountInput: '10,00',
      splits: [
        withSplit({ amountInput: '6,00' }),
        withSplit({ key: 's2', amountInput: '6,00' }),
      ],
    };
    expect(validateEntryForm(form, snapshot()).map((e) => e.code)).toContain('split_sum_mismatch');
  });

  it('Split-Summe ≠ Gesamt → split_sum_mismatch (Summe zu niedrig)', () => {
    const form = {
      ...validForm(),
      amountInput: '10,00',
      splits: [withSplit({ amountInput: '4,00' })],
    };
    expect(validateEntryForm(form, snapshot()).map((e) => e.code)).toContain('split_sum_mismatch');
  });

  it('unbekannter Pool → unknown_pool', () => {
    const form = { ...validForm(), splits: [withSplit({ poolId: 'pool-x' })] };
    expect(validateEntryForm(form, snapshot()).map((e) => e.code)).toContain('unknown_pool');
  });

  it('leerer Pool → missing_pool', () => {
    const form = { ...validForm(), splits: [withSplit({ poolId: '' })] };
    expect(validateEntryForm(form, snapshot()).map((e) => e.code)).toContain('missing_pool');
  });

  it('leere Kategorie → missing_category', () => {
    const form = { ...validForm(), splits: [withSplit({ categoryId: '' })] };
    expect(validateEntryForm(form, snapshot()).map((e) => e.code)).toContain('missing_category');
  });

  it('archivierter Pool → archived_pool', () => {
    const form = { ...validForm(), splits: [withSplit({ poolId: 'pool-arch' })] };
    expect(validateEntryForm(form, snapshot()).map((e) => e.code)).toContain('archived_pool');
  });

  it('Steuer relevant ohne Kategorie → missing_tax_category', () => {
    const form = {
      ...validForm(),
      splits: [
        withSplit({
          tax: { relevant: true, taxCategoryId: null, taxCategoryName: null, markedAmountInput: '', note: '' },
        }),
      ],
    };
    expect(validateEntryForm(form, snapshot()).map((e) => e.code)).toContain('missing_tax_category');
  });

  it('Steuer relevant, markierter Betrag > Splitbetrag → marked_amount_range', () => {
    const form = {
      ...validForm(),
      splits: [
        withSplit({
          tax: {
            relevant: true,
            taxCategoryId: 'tax-1',
            taxCategoryName: 'USt',
            markedAmountInput: '15,00',
            note: '',
          },
        }),
      ],
    };
    expect(validateEntryForm(form, snapshot()).map((e) => e.code)).toContain('marked_amount_range');
  });

  it('Steuer relevant, markierter Betrag = Splitbetrag → ok (Obergrenze erlaubt)', () => {
    const form = {
      ...validForm(),
      splits: [
        withSplit({
          tax: {
            relevant: true,
            taxCategoryId: 'tax-1',
            taxCategoryName: 'USt',
            markedAmountInput: '10,00',
            note: '',
          },
        }),
      ],
    };
    expect(validateEntryForm(form, snapshot())).toEqual([]);
  });

  it('Steuer relevant, markierter Betrag 0 → ok (Untergrenze erlaubt)', () => {
    const form = {
      ...validForm(),
      splits: [
        withSplit({
          tax: {
            relevant: true,
            taxCategoryId: 'tax-1',
            taxCategoryName: 'USt',
            markedAmountInput: '0,00',
            note: '',
          },
        }),
      ],
    };
    expect(validateEntryForm(form, snapshot())).toEqual([]);
  });

  it('Steuer neutral → keine Steuer-Fehler (auch ohne Kategorie)', () => {
    expect(validateEntryForm(validForm(), snapshot())).toEqual([]);
  });

  it('snapshot null → keine Snapshot-Fehler (nur Struktur/Summen)', () => {
    const form = { ...validForm(), accountId: 'was-immer' };
    expect(validateEntryForm(form, null)).toEqual([]);
  });
});

describe('toLocalInput (Batch D, P4)', () => {
  it('gültiges Formular → LocalExpenseInput mit exakten Werten', () => {
    const form = {
      ...validForm(),
      note: '  Einkauf  ',
      splits: [
        withSplit({
          note: '  Milch  ',
          tax: { relevant: true, taxCategoryId: 'tax-1', taxCategoryName: 'USt', markedAmountInput: '4,00', note: ' teurer  ' },
        }),
      ],
    };
    const input = toLocalInput(form, snapshot());
    expect(input).not.toBeNull();
    expect(input!.date).toBe(todayInBerlin());
    expect(input!.totalAmountCents).toBe(1000);
    expect(input!.accountId).toBe('acc-1');
    expect(input!.note).toBe('Einkauf');
    expect(input!.splits).toHaveLength(1);
    expect(input!.splits[0].amountCents).toBe(1000);
    expect(input!.splits[0].poolId).toBe('pool-1');
    expect(input!.splits[0].categoryId).toBe('cat-1');
    expect(input!.splits[0].note).toBe('Milch');
    expect(input!.splits[0].tax).toEqual({
      relevant: true,
      taxCategoryId: 'tax-1',
      taxCategoryName: 'USt',
      markedAmountCents: 400,
      note: 'teurer',
    });
  });

  it('ungültiges Formular → null', () => {
    const form = { ...validForm(), amountInput: '0' };
    expect(toLocalInput(form, snapshot())).toBeNull();
  });

  it('snapshot null → null', () => {
    expect(toLocalInput(validForm(), null)).toBeNull();
  });

  it('Steuer neutral → tax.relevant=false, markedAmountCents=null, note übernommen', () => {
    const form = {
      ...validForm(),
      splits: [
        withSplit({
          tax: { relevant: false, taxCategoryId: null, taxCategoryName: null, markedAmountInput: '', note: 'ohne USt' },
        }),
      ],
    };
    const input = toLocalInput(form, snapshot())!;
    expect(input.splits[0].tax).toEqual({
      relevant: false,
      taxCategoryId: null,
      taxCategoryName: null,
      markedAmountCents: null,
      note: 'ohne USt',
    });
  });

  it('markedAmountInput leer bei relevant → markedAmountCents null', () => {
    const form = {
      ...validForm(),
      splits: [
        withSplit({
          tax: { relevant: true, taxCategoryId: 'tax-1', taxCategoryName: 'USt', markedAmountInput: '', note: '' },
        }),
      ],
    };
    const input = toLocalInput(form, snapshot())!;
    expect(input.splits[0].tax!.relevant).toBe(true);
    expect(input.splits[0].tax!.markedAmountCents).toBeNull();
  });
});

describe('Quick-Actions (Batch D, P4)', () => {
  it('applyHalfSplit: Gesamt 100,00, zwei Splits 60/40 → Gesamt 50,00; Split-Summe exakt 5000', () => {
    const form = {
      ...validForm(),
      amountInput: '100,00',
      splits: [
        withSplit({ amountInput: '60,00' }),
        withSplit({ key: 's2', amountInput: '40,00' }),
      ],
    };
    const result = applyHalfSplit(form)!;
    expect(parseTotal(result)).toBe(5000);
    expect(sumOfSplits(result)).toBe(5000);
  });

  it('applyHalfSplit: ungerade Summe → Rest auf den ersten Split (exakte Summe)', () => {
    // Gesamt 10,01 (1001 Cent), Splits 6,00/4,01 → Hälfte 500;
    // 300 + 200 = 500, Rest 0. Ungleich: 5,00/5,01 → 250 + 250 = 500.
    const form = {
      ...validForm(),
      amountInput: '10,01',
      splits: [withSplit({ amountInput: '5,00' }), withSplit({ key: 's2', amountInput: '5,01' })],
    };
    const result = applyHalfSplit(form)!;
    expect(parseTotal(result)).toBe(500);
    expect(sumOfSplits(result)).toBe(500);
  });

  it('applyHalfSplit: Summenabweichung → null (Präcondition sum === total)', () => {
    const form = {
      ...validForm(),
      amountInput: '10,00',
      splits: [withSplit({ amountInput: '4,00' })],
    };
    expect(applyHalfSplit(form)).toBeNull();
  });

  it('applyHalfSplit: ungültige Split-Beträge → null', () => {
    const form = {
      ...validForm(),
      amountInput: '10,00',
      splits: [withSplit({ amountInput: '' })],
    };
    expect(applyHalfSplit(form)).toBeNull();
  });

  it('applyEqualSplit: 10,00 auf 3 Splits → 3,34/3,33/3,33 (Summe exakt 1000)', () => {
    const form = {
      ...validForm(),
      splits: [
        withSplit({ amountInput: '1,00' }),
        withSplit({ key: 's2', amountInput: '1,00' }),
        withSplit({ key: 's3', amountInput: '1,00' }),
      ],
    };
    const result = applyEqualSplit(form)!;
    const amounts = result.splits.map((s) => parseAmountSafe(s.amountInput));
    expect(amounts).toEqual([334, 333, 333]);
    expect(sumOfSplits(result)).toBe(1000);
  });

  it('applyEqualSplit: Gesamt 0 → null', () => {
    const form = { ...validForm(), amountInput: '0' };
    expect(applyEqualSplit(form)).toBeNull();
  });
});

describe('applyTaxPreset (Batch D, P4)', () => {
  const preset: TaxPresetDto = {
    taxRelevant: true,
    taxCategoryId: 'tax-1',
    taxCategoryName: 'USt',
    inheritToChildren: false,
    baseRevision: 1,
  };

  it('relevant-Preset → Schalter ein, Kategorie übernommen, markedAmount = Splitbetrag', () => {
    const split = withSplit({ amountInput: '7,50' });
    const result = applyTaxPreset(split, preset);
    expect(result.tax.relevant).toBe(true);
    expect(result.tax.taxCategoryId).toBe('tax-1');
    expect(result.tax.taxCategoryName).toBe('USt');
    expect(result.tax.markedAmountInput).toBe('7,50');
  });

  it('Preset null → Split unverändert', () => {
    const split = withSplit({ amountInput: '7,50' });
    expect(applyTaxPreset(split, null)).toBe(split);
  });

  it('Preset taxRelevant null → Split unverändert', () => {
    const split = withSplit({ amountInput: '7,50' });
    expect(applyTaxPreset(split, { ...preset, taxRelevant: null })).toBe(split);
  });

  it('Preset relevant=false → Schalter aus, Felder leer', () => {
    const split = withSplit({
      amountInput: '7,50',
      tax: { relevant: true, taxCategoryId: 'tax-1', taxCategoryName: 'USt', markedAmountInput: '7,50', note: 'alt' },
    });
    const result = applyTaxPreset(split, { ...preset, taxRelevant: false });
    expect(result.tax.relevant).toBe(false);
    expect(result.tax.taxCategoryId).toBeNull();
    expect(result.tax.markedAmountInput).toBe('');
    // Notiz bleibt erhalten (Übernahme ist kein Reset der Notiz).
    expect(result.tax.note).toBe('alt');
  });

  it('Preset relevant, Splitbetrag ungültig → markedAmountInput leer', () => {
    const split = withSplit({ amountInput: '' });
    const result = applyTaxPreset(split, preset);
    expect(result.tax.relevant).toBe(true);
    expect(result.tax.markedAmountInput).toBe('');
  });
});

// --- Test-Hilfen -----------------------------------------------------------

function parseAmountSafe(raw: string): number | null | undefined {
  return parseAmount(raw);
}

function parseTotal(model: EntryFormModel): number {
  const c = parseAmountSafe(model.amountInput);
  expect(c).not.toBeNull();
  expect(c).not.toBeUndefined();
  return c!;
}

function sumOfSplits(model: EntryFormModel): number {
  return model.splits.reduce((sum, s) => {
    const c = parseAmountSafe(s.amountInput);
    expect(c).not.toBeNull();
    expect(c).not.toBeUndefined();
    return sum + c!;
  }, 0);
}
