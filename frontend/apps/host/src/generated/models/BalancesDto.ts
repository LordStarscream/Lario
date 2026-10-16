/* generated using openapi-typescript-codegen -- do not edit */
/* istanbul ignore file */
/* tslint:disable */
/* eslint-disable */
import type { AccountBalanceDto } from './AccountBalanceDto';
import type { PoolBalanceDto } from './PoolBalanceDto';
import type { UnallocatedOpeningBalanceDto } from './UnallocatedOpeningBalanceDto';
export type BalancesDto = {
    accounts: Array<AccountBalanceDto>;
    pools: Array<PoolBalanceDto>;
    totalAccountBalance: number | string;
    totalPoolBalance: number | string;
    totalIncome: number | string;
    accountPoolGap: number | string;
    unallocatedOpeningBalances: Array<UnallocatedOpeningBalanceDto>;
};

