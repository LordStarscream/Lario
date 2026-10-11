/* generated using openapi-typescript-codegen -- do not edit */
/* istanbul ignore file */
/* tslint:disable */
/* eslint-disable */
import type { HealthResponse } from '../models/HealthResponse';
import type { VersionResponse } from '../models/VersionResponse';
import type { CancelablePromise } from '../core/CancelablePromise';
import { OpenAPI } from '../core/OpenAPI';
import { request as __request } from '../core/request';
export class LarioHostService {
    /**
     * @returns HealthResponse OK
     * @throws ApiError
     */
    public static getHealth(): CancelablePromise<HealthResponse> {
        return __request(OpenAPI, {
            method: 'GET',
            url: '/api/health',
        });
    }
    /**
     * @returns VersionResponse OK
     * @throws ApiError
     */
    public static getVersion(): CancelablePromise<VersionResponse> {
        return __request(OpenAPI, {
            method: 'GET',
            url: '/api/version',
        });
    }
}
