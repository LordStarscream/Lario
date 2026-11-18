/* generated using openapi-typescript-codegen -- do not edit */
/* istanbul ignore file */
/* tslint:disable */
/* eslint-disable */
import type { BootstrapSnapshotDto } from '../models/BootstrapSnapshotDto';
import type { DeviceDto } from '../models/DeviceDto';
import type { PairDeviceRequest } from '../models/PairDeviceRequest';
import type { PushResponseDto } from '../models/PushResponseDto';
import type { SyncChangesDto } from '../models/SyncChangesDto';
import type { SyncExpensePayload } from '../models/SyncExpensePayload';
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
     * @returns BootstrapSnapshotDto OK
     * @throws ApiError
     */
    public static syncBootstrap(): CancelablePromise<BootstrapSnapshotDto> {
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
     * @returns PushResponseDto OK
     * @throws ApiError
     */
    public static syncPush(
        requestBody?: (null | SyncExpensePayload),
    ): CancelablePromise<PushResponseDto> {
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
     * @param after
     * @param limit
     * @returns SyncChangesDto OK
     * @throws ApiError
     */
    public static syncGetChanges(
        after: number | string,
        limit: number | string,
    ): CancelablePromise<SyncChangesDto> {
        return __request(OpenAPI, {
            method: 'GET',
            url: '/api/v1/sync/changes',
            query: {
                'after': after,
                'limit': limit,
            },
            errors: {
                400: `Bad Request`,
            },
        });
    }
}
