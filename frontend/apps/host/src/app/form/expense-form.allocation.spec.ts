import {
  allocationFingerprint,
  type AllocationState,
} from './expense-form';

function baseState(): AllocationState {
  return {
    totalCents: 8000,
    splits: [
      {
        id: 's1',
        amountCents: 5000,
        poolId: 'flex',
        categoryId: 'c1',
        note: '',
        tax: {
          relevant: true,
          taxCategoryId: 't1',
          markedAmountCents: 2000,
        },
      },
      {
        id: 's2',
        amountCents: 3000,
        poolId: 'urlaub',
        categoryId: 'c2',
        note: 'Gemeinsam bezahlt',
        tax: { relevant: null, taxCategoryId: null, markedAmountCents: null },
      },
    ],
  };
}

describe('allocationFingerprint (Review R3 P2: Skalar-Korrektur-Pfad)', () => {
  it('ist bei identischer Aufteilung stabil (Skalar-Korrektur-Pfad bleibt offen)', () => {
    const a = allocationFingerprint(baseState());
    const b = allocationFingerprint(baseState());
    expect(a).toBe(b);
  });

  it('ändert sich, wenn der Gesamtbetrag geändert wird', () => {
    const before = baseState();
    const after = JSON.parse(JSON.stringify(before)) as AllocationState;
    after.totalCents = 8500;
    expect(allocationFingerprint(after)).not.toBe(allocationFingerprint(before));
  });

  it('ändert sich, wenn ein Split-Betrag geändert wird', () => {
    const before = baseState();
    const after = JSON.parse(JSON.stringify(before)) as AllocationState;
    after.splits[0].amountCents = 4000;
    expect(allocationFingerprint(after)).not.toBe(allocationFingerprint(before));
  });

  it('ändert sich, wenn ein Split-Pool oder -Kategorie geändert wird', () => {
    const before = baseState();
    const afterPool = JSON.parse(JSON.stringify(before)) as AllocationState;
    afterPool.splits[1].poolId = 'flex';
    const afterCat = JSON.parse(JSON.stringify(before)) as AllocationState;
    afterCat.splits[0].categoryId = 'c3';
    expect(allocationFingerprint(afterPool)).not.toBe(allocationFingerprint(before));
    expect(allocationFingerprint(afterCat)).not.toBe(allocationFingerprint(before));
  });

  it('ändert sich, wenn eine Split-Notiz geändert wird (keine stille Verwirrung)', () => {
    const before = baseState();
    const after = JSON.parse(JSON.stringify(before)) as AllocationState;
    after.splits[1].note = 'Gemeinsam bezahlt (korrigiert)';
    expect(allocationFingerprint(after)).not.toBe(allocationFingerprint(before));
  });

  it('ändert sich, wenn die Steuerkennzeichnung geändert wird', () => {
    const before = baseState();
    const afterRelevant = JSON.parse(JSON.stringify(before)) as AllocationState;
    afterRelevant.splits[1].tax = {
      relevant: true,
      taxCategoryId: 't2',
      markedAmountCents: null,
    };
    const afterMark = JSON.parse(JSON.stringify(before)) as AllocationState;
    afterMark.splits[0].tax.markedAmountCents = 1500;
    const afterCategory = JSON.parse(JSON.stringify(before)) as AllocationState;
    afterCategory.splits[0].tax.taxCategoryId = 't2';
    expect(allocationFingerprint(afterRelevant)).not.toBe(allocationFingerprint(before));
    expect(allocationFingerprint(afterMark)).not.toBe(allocationFingerprint(before));
    expect(allocationFingerprint(afterCategory)).not.toBe(allocationFingerprint(before));
  });

  it('ändert sich, wenn Split-Ids oder die Reihenfolge sich ändern', () => {
    const before = baseState();
    const afterId = JSON.parse(JSON.stringify(before)) as AllocationState;
    afterId.splits[0].id = 'neu';
    const afterOrder = JSON.parse(JSON.stringify(before)) as AllocationState;
    [afterOrder.splits[0], afterOrder.splits[1]] = [
      afterOrder.splits[1],
      afterOrder.splits[0],
    ];
    expect(allocationFingerprint(afterId)).not.toBe(allocationFingerprint(before));
    expect(allocationFingerprint(afterOrder)).not.toBe(allocationFingerprint(before));
  });
});
