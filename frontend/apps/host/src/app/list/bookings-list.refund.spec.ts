import type { BookingDetailDto } from '../../generated/models/BookingDetailDto';
import type { ExpenseSplitDto } from '../../generated/models/ExpenseSplitDto';
import type { RefundedPoolDto } from '../../generated/models/RefundedPoolDto';
import { refundLinesFromDetail } from './bookings-list';

function split(
  poolId: string,
  amount: number,
  poolName: string | null = 'Pool',
): ExpenseSplitDto {
  return {
    id: `split-${poolId}-${amount}`,
    amount,
    poolId,
    poolName,
    categoryId: 'cat-1',
    categoryName: 'Kategorie',
    note: null,
    tax: null,
  };
}

function refunded(poolId: string, amount: number): RefundedPoolDto {
  return { poolId, amount };
}

function detail(splits: ExpenseSplitDto[], refundedByPool: RefundedPoolDto[]): BookingDetailDto {
  return {
    id: 'b1',
    type: 'expense',
    date: '2026-10-01',
    note: null,
    baseRevision: 1,
    effectivelyReversed: false,
    fundingMonth: null,
    reversesBookingId: null,
    reversalBookingIds: [],
    refundsExpenseId: null,
    relatedSettlementId: null,
    accountPostings: [],
    poolPostings: [],
    splits,
    transferPurposes: [],
    reassignmentAccounts: [],
    totalAmount: splits.reduce((sum, s) => sum + Number(s.amount), 0),
    refundedByPool,
  };
}

describe('refundLinesFromDetail (Review R3 P2: Teil-Erstattungen)', () => {
  it('zeigt ohne Erstattungen den vollen Split-Betrag als Deckel', () => {
    const { lines, fullyRefunded } = refundLinesFromDetail(
      detail([split('flex', 8000)], []),
    );
    expect(fullyRefunded).toBe(false);
    expect(lines).toEqual([
      expect.objectContaining({
        poolId: 'flex',
        maxCents: 8000,
        refundedCents: 0,
      }),
    ]);
  });

  it('zieht eine gewährte Teil-Erstattung vom Deckel des Pools ab', () => {
    const { lines, fullyRefunded } = refundLinesFromDetail(
      detail([split('flex', 8000)], [refunded('flex', 3000)]),
    );
    expect(fullyRefunded).toBe(false);
    expect(lines).toHaveLength(1);
    expect(lines[0].poolId).toBe('flex');
    expect(lines[0].maxCents).toBe(5000);
    expect(lines[0].refundedCents).toBe(3000);
  });

  it('erkennt eine vollständige Erstattung und meldet fullyRefunded', () => {
    const { lines, fullyRefunded } = refundLinesFromDetail(
      detail([split('flex', 8000)], [refunded('flex', 8000)]),
    );
    expect(fullyRefunded).toBe(true);
    expect(lines).toEqual([]);
  });

  it('aggregiert mehrere Splits desselben Pools zu einer Erstattungszeile', () => {
    const { lines, fullyRefunded } = refundLinesFromDetail(
      detail(
        [split('flex', 5000), split('flex', 3000), split('urlaub', 2000)],
        [refunded('flex', 4000)],
      ),
    );
    expect(fullyRefunded).toBe(false);
    expect(lines.map((l) => l.poolId).sort()).toEqual(['flex', 'urlaub']);
    const flex = lines.find((l) => l.poolId === 'flex');
    expect(flex?.maxCents).toBe(4000); // 8.000 − 4.000
    expect(flex?.refundedCents).toBe(4000);
    const urlaub = lines.find((l) => l.poolId === 'urlaub');
    expect(urlaub?.maxCents).toBe(2000);
  });

  it('lässt mehrere Teil-Erstattungen kumulativ an', () => {
    const { lines, fullyRefunded } = refundLinesFromDetail(
      detail(
        [split('flex', 8000)],
        [refunded('flex', 3000), refunded('flex', 2000)],
      ),
    );
    expect(fullyRefunded).toBe(false);
    expect(lines[0].maxCents).toBe(3000);
    expect(lines[0].refundedCents).toBe(5000);
  });

  it('behandelt Pools unabhängig voneinander (ein Pool voll, der andere offen)', () => {
    const { lines, fullyRefunded } = refundLinesFromDetail(
      detail(
        [split('flex', 8000), split('urlaub', 2000)],
        [refunded('flex', 8000)],
      ),
    );
    expect(fullyRefunded).toBe(false);
    expect(lines).toHaveLength(1);
    expect(lines[0].poolId).toBe('urlaub');
    expect(lines[0].maxCents).toBe(2000);
  });
});
