/* generated using openapi-typescript-codegen -- do not edit */
/* istanbul ignore file */
/* tslint:disable */
/* eslint-disable */
import type { AccountDto } from './AccountDto';
import type { CategoryDto } from './CategoryDto';
import type { PoolDto } from './PoolDto';
import type { TaxCategoryDto } from './TaxCategoryDto';
export type BootstrapSnapshotDto = {
    protocolGeneration: number | string;
    dbGeneration: number | string;
    cursor: number | string;
    accounts: Array<AccountDto>;
    pools: Array<PoolDto>;
    categories: Array<CategoryDto>;
    taxCategories: Array<TaxCategoryDto>;
};

