/* generated using openapi-typescript-codegen -- do not edit */
/* istanbul ignore file */
/* tslint:disable */
/* eslint-disable */
import type { SyncTaxMarkingPayload } from './SyncTaxMarkingPayload';
export type SyncExpenseSplitPayload = {
    id: string | null;
    amountCents: number | string | null;
    poolId: string | null;
    categoryId: string | null;
    note: string | null;
    tax: (null | SyncTaxMarkingPayload);
};

