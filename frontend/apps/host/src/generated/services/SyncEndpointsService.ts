/* generated using openapi-typescript-codegen -- do not edit */
/* istanbul ignore file */
/* tslint:disable */
/* eslint-disable */
import type { BootstrapResponseDto } from '../models/BootstrapResponseDto';
import type { DeviceDto } from '../models/DeviceDto';
import type { PairDeviceRequest } from '../models/PairDeviceRequest';
import type { SyncPullResponse } from '../models/SyncPullResponse';
import type { SyncPushRequest } from '../models/SyncPushRequest';
import type { SyncPushResponse } from '../models/SyncPushResponse';
import type { CancelablePromise } from '../core/CancelablePromise';
import { OpenAPI } from '../core/OpenAPI';
import { request as __request } from '../core/request';
export class SyncEndpointsService {
    /**
     * @param requestBody
     * @returns DeviceDto OK
     * @throws ApiError
     */
    public static syncPair(
        requestBody?: (null | PairDeviceRequest),
    ): CancelablePromise<DeviceDto> {
        return __request(OpenAPI, {
            method: 'POST',
            url: '/api/v1/sync/pair',
            body: requestBody,
            mediaType: 'application/json',
            errors: {
                400: `Bad Request`,
            },
        });
    }
    /**
     * @returns DeviceDto OK
     * @throws ApiError
     */
    public static syncListDevices(): CancelablePromise<Array<DeviceDto>> {
        return __request(OpenAPI, {
            method: 'GET',
            url: '/api/v1/sync/devices',
        });
    }
    /**
     * @param id
     * @returns void
     * @throws ApiError
     */
    public static syncRevokeDevice(
        id: string,
    ): CancelablePromise<void> {
        return __request(OpenAPI, {
            method: 'DELETE',
            url: '/api/v1/sync/devices/{id}',
            path: {
                'id': id,
            },
            errors: {
                400: `Bad Request`,
            },
        });
    }
    /**
     * @returns BootstrapResponseDto OK
     * @throws ApiError
     */
    public static syncBootstrap(): CancelablePromise<BootstrapResponseDto> {
        return __request(OpenAPI, {
            method: 'POST',
            url: '/api/v1/sync/bootstrap',
            errors: {
                400: `Bad Request`,
            },
        });
    }
    /**
     * @param requestBody
     * @returns SyncPushResponse OK
     * @throws ApiError
     */
    public static syncPush(
        requestBody?: (null | SyncPushRequest),
    ): CancelablePromise<SyncPushResponse> {
        return __request(OpenAPI, {
            method: 'POST',
            url: '/api/v1/sync/push',
            body: requestBody,
            mediaType: 'application/json',
            errors: {
                400: `Bad Request`,
                409: `Conflict`,
            },
        });
    }
    /**
     * @param cursor
     * @param limit
     * @returns SyncPullResponse OK
     * @throws ApiError
     */
    public static syncGetChanges(
        cursor: number | string,
        limit: number | string,
    ): CancelablePromise<SyncPullResponse> {
        return __request(OpenAPI, {
            method: 'GET',
            url: '/api/v1/sync/changes',
            query: {
                'cursor': cursor,
                'limit': limit,
            },
            errors: {
                400: `Bad Request`,
            },
        });
    }
}
