// @vitest-environment jsdom
import {
  buildExpensePayload,
  type LocalExpenseInput,
  type LocalSplitInput,
} from './expense-payload';

const split1: LocalSplitInput = {
  id: 'bk-001-abc-s1',
  amountCents: 3000,
  poolId: 'pool-1',
  categoryId: 'cat-1',
  note: null,
  tax: {
    relevant: true,
    taxCategoryId: 'tax-1',
    taxCategoryName: 'Vorsteuer abziehbar',
    markedAmountCents: 3000,
    note: null,
  },
};

const split2: LocalSplitInput = {
  id: 'bk-001-abc-s2',
  amountCents: 1730,
  poolId: 'pool-1',
  categoryId: 'cat-2',
  note: 'Molkerei',
  tax: null,
};

const input: LocalExpenseInput = {
  date: '2026-11-01',
  totalAmountCents: 4730,
  accountId: 'acct-1',
  note: 'Einkaufen',
  splits: [split1, split2],
};

describe('buildExpensePayload (Design B3)', () => {
  it('exaktes Payload-Beispiel (feste Schlüsselreihenfolge)', () => {
    expect(buildExpensePayload('bk-001-abc', input)).toBe(
      '{"id":"bk-001-abc","date":"2026-11-01","totalAmount":4730,"accountId":"acct-1","note":"Einkaufen","splits":[{"id":"bk-001-abc-s1","amount":3000,"poolId":"pool-1","categoryId":"cat-1","note":null,"tax":{"relevant":true,"taxCategoryId":"tax-1","markedAmount":3000,"note":null}},{"id":"bk-001-abc-s2","amount":1730,"poolId":"pool-1","categoryId":"cat-2","note":"Molkerei","tax":null}],"expectedBaseRevision":null}',
    );
  });

  it('expectedBaseRevision ist bei Neuanlage immer null (Key vorhanden)', () => {
    const payload = buildExpensePayload('bk-001-abc', input);
    const parsed = JSON.parse(payload);
    expect(parsed).toHaveProperty('expectedBaseRevision', null);
  });

  it('markedAmount: expliziter Wert wird übernommen', () => {
    const withPartialMark = {
      ...input,
      splits: [
        {
          ...split1,
          tax: {
            ...split1.tax!,
            markedAmountCents: 1200,
          },
        },
      ],
    };
    const parsed = JSON.parse(buildExpensePayload('bk-001-abc', withPartialMark));
    expect(parsed.splits[0].tax.markedAmount).toBe(1200);
  });

  it('markedAmount: null wird normalisiert auf den Splitbetrag', () => {
    const withNullMark = {
      ...input,
      splits: [
        {
          ...split2,
          tax: {
            relevant: true,
            taxCategoryId: 'tax-1',
            taxCategoryName: null,
            markedAmountCents: null,
            note: null,
          },
        },
      ],
    };
    const parsed = JSON.parse(buildExpensePayload('bk-001-abc', withNullMark));
    expect(parsed.splits[0].tax.markedAmount).toBe(1730);
  });

  it('tax: null bleibt null im Payload (kein leeres Objekt)', () => {
    const payload = buildExpensePayload('bk-001-abc', input);
    expect(payload).toContain('"tax":null');
  });

  it('tax ohne relevant: relevant=false, taxCategoryId null, voller Splitbetrag', () => {
    const withNotRelevant = {
      ...input,
      splits: [
        {
          ...split2,
          tax: {
            relevant: false,
            taxCategoryId: null,
            taxCategoryName: null,
            markedAmountCents: null,
            note: null,
          },
        },
      ],
    };
    const parsed = JSON.parse(buildExpensePayload('bk-001-abc', withNotRelevant));
    expect(parsed.splits[0].tax).toEqual({
      relevant: false,
      taxCategoryId: null,
      markedAmount: 1730,
      note: null,
    });
  });

  it('deterministisch: zweimal bauen = identischer String', () => {
    expect(buildExpensePayload('bk-001-abc', input)).toBe(
      buildExpensePayload('bk-001-abc', input),
    );
  });

  it('unterschiedliche Eingabe (anderes Note) → anderer String', () => {
    const other = { ...input, note: 'Andere Notiz' };
    expect(buildExpensePayload('bk-001-abc', other)).not.toBe(
      buildExpensePayload('bk-001-abc', input),
    );
  });
});
