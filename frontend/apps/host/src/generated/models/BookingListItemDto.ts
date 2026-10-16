/* generated using openapi-typescript-codegen -- do not edit */
/* istanbul ignore file */
/* tslint:disable */
/* eslint-disable */
import type { BookingSplitSummaryDto } from './BookingSplitSummaryDto';
export type BookingListItemDto = {
    id: string;
    type: string;
    date: string;
    note: string | null;
    totalAmount: number | string;
    accountId: string | null;
    accountName: string | null;
    splits: Array<BookingSplitSummaryDto>;
    poolNames: Array<string>;
    baseRevision: number | string;
    effectivelyReversed: boolean;
};

