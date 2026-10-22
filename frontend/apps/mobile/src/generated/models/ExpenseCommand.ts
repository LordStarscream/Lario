/* generated using openapi-typescript-codegen -- do not edit */
/* istanbul ignore file */
/* tslint:disable */
/* eslint-disable */
import type { ExpenseSplitCommand } from './ExpenseSplitCommand';
export type ExpenseCommand = {
    id: string;
    date: string;
    totalAmount: number | string;
    accountId: string;
    note: string | null;
    splits: Array<ExpenseSplitCommand>;
    expectedBaseRevision: number | string | null;
};

