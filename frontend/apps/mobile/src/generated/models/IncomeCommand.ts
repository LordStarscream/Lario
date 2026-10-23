/* generated using openapi-typescript-codegen -- do not edit */
/* istanbul ignore file */
/* tslint:disable */
/* eslint-disable */
import type { IncomeAllocationCommand } from './IncomeAllocationCommand';
export type IncomeCommand = {
    id: string;
    date: string;
    amount: number | string;
    accountId: string;
    note: string | null;
    fundingMonth: string | null;
    allocations: Array<IncomeAllocationCommand>;
    expectedBaseRevision: number | string | null;
};

