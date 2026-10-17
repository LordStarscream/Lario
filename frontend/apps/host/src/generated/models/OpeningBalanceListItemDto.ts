/* generated using openapi-typescript-codegen -- do not edit */
/* istanbul ignore file */
/* tslint:disable */
/* eslint-disable */
import type { PoolAllocationDto } from './PoolAllocationDto';
export type OpeningBalanceListItemDto = {
    id: string;
    date: string;
    accountId: string;
    accountName: string | null;
    total: number | string;
    distribution: Array<PoolAllocationDto>;
    note: string | null;
    baseRevision: number | string;
};

