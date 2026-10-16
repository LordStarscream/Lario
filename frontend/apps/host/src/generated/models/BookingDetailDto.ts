/* generated using openapi-typescript-codegen -- do not edit */
/* istanbul ignore file */
/* tslint:disable */
/* eslint-disable */
import type { AccountAllocationDto } from './AccountAllocationDto';
import type { AccountPostingDto } from './AccountPostingDto';
import type { ExpenseSplitDto } from './ExpenseSplitDto';
import type { PoolAllocationDto } from './PoolAllocationDto';
import type { PoolPostingDto } from './PoolPostingDto';
export type BookingDetailDto = {
    id: string;
    type: string;
    date: string;
    note: string | null;
    baseRevision: number | string;
    effectivelyReversed: boolean;
    fundingMonth: string | null;
    reversesBookingId: string | null;
    reversalBookingIds: Array<string>;
    refundsExpenseId: string | null;
    relatedSettlementId: string | null;
    accountPostings: Array<AccountPostingDto>;
    poolPostings: Array<PoolPostingDto>;
    splits: Array<ExpenseSplitDto>;
    transferPurposes: Array<PoolAllocationDto>;
    reassignmentAccounts: Array<AccountAllocationDto>;
    totalAmount: number | string;
};

