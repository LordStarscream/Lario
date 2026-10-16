/* generated using openapi-typescript-codegen -- do not edit */
/* istanbul ignore file */
/* tslint:disable */
/* eslint-disable */
export type SettlementDto = {
    id: string;
    sourceAccountId: string;
    sourceAccountName: string;
    targetPoolId: string;
    targetPoolName: string;
    amount: number | string;
    restAmount: number | string;
    status: string;
    createdByBookingId: string;
    createdDate: string;
    settledByBookingId: string | null;
    settledDate: string | null;
};

