/* generated using openapi-typescript-codegen -- do not edit */
/* istanbul ignore file */
/* tslint:disable */
/* eslint-disable */
import type { BalanceCorrectionCommand } from '../models/BalanceCorrectionCommand';
import type { BookingCreatedDto } from '../models/BookingCreatedDto';
import type { CorrectionCommand } from '../models/CorrectionCommand';
import type { ExpenseCommand } from '../models/ExpenseCommand';
import type { IncomeCommand } from '../models/IncomeCommand';
import type { OpeningBalanceCommand } from '../models/OpeningBalanceCommand';
import type { ReassignmentCommand } from '../models/ReassignmentCommand';
import type { RefundCommand } from '../models/RefundCommand';
import type { ReversalCommand } from '../models/ReversalCommand';
import type { TransferCommand } from '../models/TransferCommand';
import type { TransferReassignmentCommand } from '../models/TransferReassignmentCommand';
import type { CancelablePromise } from '../core/CancelablePromise';
import { OpenAPI } from '../core/OpenAPI';
import { request as __request } from '../core/request';
export class JournalWriteEndpointsService {
    /**
     * @param requestBody
     * @returns BookingCreatedDto OK
     * @throws ApiError
     */
    public static createOpeningBalance(
        requestBody: OpeningBalanceCommand,
    ): CancelablePromise<BookingCreatedDto> {
        return __request(OpenAPI, {
            method: 'POST',
            url: '/api/v1/opening-balances',
            body: requestBody,
            mediaType: 'application/json',
            errors: {
                400: `Bad Request`,
                409: `Conflict`,
            },
        });
    }
    /**
     * @param requestBody
     * @returns BookingCreatedDto OK
     * @throws ApiError
     */
    public static createExpense(
        requestBody: ExpenseCommand,
    ): CancelablePromise<BookingCreatedDto> {
        return __request(OpenAPI, {
            method: 'POST',
            url: '/api/v1/expenses',
            body: requestBody,
            mediaType: 'application/json',
            errors: {
                400: `Bad Request`,
                409: `Conflict`,
            },
        });
    }
    /**
     * @param requestBody
     * @returns BookingCreatedDto OK
     * @throws ApiError
     */
    public static createIncome(
        requestBody: IncomeCommand,
    ): CancelablePromise<BookingCreatedDto> {
        return __request(OpenAPI, {
            method: 'POST',
            url: '/api/v1/income',
            body: requestBody,
            mediaType: 'application/json',
            errors: {
                400: `Bad Request`,
                409: `Conflict`,
            },
        });
    }
    /**
     * @param requestBody
     * @returns BookingCreatedDto OK
     * @throws ApiError
     */
    public static createTransfer(
        requestBody: TransferCommand,
    ): CancelablePromise<BookingCreatedDto> {
        return __request(OpenAPI, {
            method: 'POST',
            url: '/api/v1/transfers',
            body: requestBody,
            mediaType: 'application/json',
            errors: {
                400: `Bad Request`,
                409: `Conflict`,
            },
        });
    }
    /**
     * @param requestBody
     * @returns BookingCreatedDto OK
     * @throws ApiError
     */
    public static createReassignment(
        requestBody: ReassignmentCommand,
    ): CancelablePromise<BookingCreatedDto> {
        return __request(OpenAPI, {
            method: 'POST',
            url: '/api/v1/reassignments',
            body: requestBody,
            mediaType: 'application/json',
            errors: {
                400: `Bad Request`,
                409: `Conflict`,
            },
        });
    }
    /**
     * @param requestBody
     * @returns BookingCreatedDto OK
     * @throws ApiError
     */
    public static createTransferReassignment(
        requestBody: TransferReassignmentCommand,
    ): CancelablePromise<BookingCreatedDto> {
        return __request(OpenAPI, {
            method: 'POST',
            url: '/api/v1/transfer-reassignments',
            body: requestBody,
            mediaType: 'application/json',
            errors: {
                400: `Bad Request`,
                409: `Conflict`,
            },
        });
    }
    /**
     * @param id
     * @param requestBody
     * @returns BookingCreatedDto OK
     * @throws ApiError
     */
    public static correctBooking(
        id: string,
        requestBody: CorrectionCommand,
    ): CancelablePromise<BookingCreatedDto> {
        return __request(OpenAPI, {
            method: 'POST',
            url: '/api/v1/bookings/{id}/corrections',
            path: {
                'id': id,
            },
            body: requestBody,
            mediaType: 'application/json',
            errors: {
                400: `Bad Request`,
                409: `Conflict`,
            },
        });
    }
    /**
     * @param id
     * @param requestBody
     * @returns BookingCreatedDto OK
     * @throws ApiError
     */
    public static createReversal(
        id: string,
        requestBody: ReversalCommand,
    ): CancelablePromise<BookingCreatedDto> {
        return __request(OpenAPI, {
            method: 'POST',
            url: '/api/v1/bookings/{id}/reversals',
            path: {
                'id': id,
            },
            body: requestBody,
            mediaType: 'application/json',
            errors: {
                400: `Bad Request`,
                409: `Conflict`,
            },
        });
    }
    /**
     * @param id
     * @param requestBody
     * @returns BookingCreatedDto OK
     * @throws ApiError
     */
    public static createRefund(
        id: string,
        requestBody: RefundCommand,
    ): CancelablePromise<BookingCreatedDto> {
        return __request(OpenAPI, {
            method: 'POST',
            url: '/api/v1/bookings/{id}/refunds',
            path: {
                'id': id,
            },
            body: requestBody,
            mediaType: 'application/json',
            errors: {
                400: `Bad Request`,
                409: `Conflict`,
            },
        });
    }
    /**
     * @param requestBody
     * @returns BookingCreatedDto OK
     * @throws ApiError
     */
    public static createBalanceCorrection(
        requestBody: BalanceCorrectionCommand,
    ): CancelablePromise<BookingCreatedDto> {
        return __request(OpenAPI, {
            method: 'POST',
            url: '/api/v1/balance-corrections',
            body: requestBody,
            mediaType: 'application/json',
            errors: {
                400: `Bad Request`,
                409: `Conflict`,
            },
        });
    }
}
