/* generated using openapi-typescript-codegen -- do not edit */
/* istanbul ignore file */
/* tslint:disable */
/* eslint-disable */
import type { RefundLineCommand } from './RefundLineCommand';
export type RefundCommand = {
    id: string;
    accountId: string;
    date: string;
    lines: Array<RefundLineCommand>;
    note: string | null;
};

