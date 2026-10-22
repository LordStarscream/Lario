/* generated using openapi-typescript-codegen -- do not edit */
/* istanbul ignore file */
/* tslint:disable */
/* eslint-disable */
import type { ExpenseSplitTaxDto } from './ExpenseSplitTaxDto';
export type ExpenseSplitCommand = {
    id: string;
    amount: number | string;
    poolId: string;
    categoryId: string;
    note: string | null;
    tax: (null | ExpenseSplitTaxDto);
};

