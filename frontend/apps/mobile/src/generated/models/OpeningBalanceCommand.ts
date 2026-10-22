/* generated using openapi-typescript-codegen -- do not edit */
/* istanbul ignore file */
/* tslint:disable */
/* eslint-disable */
import type { PoolAllocationCommand } from './PoolAllocationCommand';
export type OpeningBalanceCommand = {
    id: string;
    date: string;
    total: number | string;
    accountId: string;
    distribution: Array<PoolAllocationCommand>;
    note: string | null;
};

