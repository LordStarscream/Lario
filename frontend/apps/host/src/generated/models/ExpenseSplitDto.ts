/* generated using openapi-typescript-codegen -- do not edit */
/* istanbul ignore file */
/* tslint:disable */
/* eslint-disable */
import type { TaxMarkingDto } from './TaxMarkingDto';
export type ExpenseSplitDto = {
    id: string;
    amount: number | string;
    poolId: string;
    poolName: string | null;
    categoryId: string;
    categoryName: string | null;
    tax: (null | TaxMarkingDto);
    note: string | null;
};

