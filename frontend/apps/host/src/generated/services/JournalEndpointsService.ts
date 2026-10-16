/* generated using openapi-typescript-codegen -- do not edit */
/* istanbul ignore file */
/* tslint:disable */
/* eslint-disable */
import type { BalancesDto } from '../models/BalancesDto';
import type { BookingDetailDto } from '../models/BookingDetailDto';
import type { BookingListItemDto } from '../models/BookingListItemDto';
import type { SettlementDto } from '../models/SettlementDto';
import type { CancelablePromise } from '../core/CancelablePromise';
import { OpenAPI } from '../core/OpenAPI';
import { request as __request } from '../core/request';
export class JournalEndpointsService {
    /**
     * @param type
     * @param from
     * @param to
     * @param limit
     * @returns BookingListItemDto OK
     * @throws ApiError
     */
    public static listBookings(
        type?: string,
        from?: string,
        to?: string,
        limit?: number | string,
    ): CancelablePromise<Array<BookingListItemDto>> {
        return __request(OpenAPI, {
            method: 'GET',
            url: '/api/v1/bookings',
            query: {
                'type': type,
                'from': from,
                'to': to,
                'limit': limit,
            },
            errors: {
                400: `Bad Request`,
            },
        });
    }
    /**
     * @param id
     * @returns BookingDetailDto OK
     * @throws ApiError
     */
    public static getBooking(
        id: string,
    ): CancelablePromise<BookingDetailDto> {
        return __request(OpenAPI, {
            method: 'GET',
            url: '/api/v1/bookings/{id}',
            path: {
                'id': id,
            },
            errors: {
                400: `Bad Request`,
            },
        });
    }
    /**
     * @returns BalancesDto OK
     * @throws ApiError
     */
    public static getBalances(): CancelablePromise<BalancesDto> {
        return __request(OpenAPI, {
            method: 'GET',
            url: '/api/v1/balances',
            errors: {
                400: `Bad Request`,
            },
        });
    }
    /**
     * @returns SettlementDto OK
     * @throws ApiError
     */
    public static listSettlements(): CancelablePromise<Array<SettlementDto>> {
        return __request(OpenAPI, {
            method: 'GET',
            url: '/api/v1/settlements',
            errors: {
                400: `Bad Request`,
            },
        });
    }
    /**
     * @returns SettlementDto OK
     * @throws ApiError
     */
    public static listOpenSettlements(): CancelablePromise<Array<SettlementDto>> {
        return __request(OpenAPI, {
            method: 'GET',
            url: '/api/v1/settlements/open',
            errors: {
                400: `Bad Request`,
            },
        });
    }
}
