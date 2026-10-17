/* generated using openapi-typescript-codegen -- do not edit */
/* istanbul ignore file */
/* tslint:disable */
/* eslint-disable */
import type { HealthResponse } from '../models/HealthResponse';
import type { LoginRequest } from '../models/LoginRequest';
import type { LoginResponse } from '../models/LoginResponse';
import type { VersionResponse } from '../models/VersionResponse';
import type { CancelablePromise } from '../core/CancelablePromise';
import { OpenAPI } from '../core/OpenAPI';
import { request as __request } from '../core/request';
export class LarioHostService {
    /**
     * @param requestBody
     * @returns LoginResponse OK
     * @throws ApiError
     */
    public static authLogin(
        requestBody?: (null | LoginRequest),
    ): CancelablePromise<LoginResponse> {
        return __request(OpenAPI, {
            method: 'POST',
            url: '/api/v1/auth/login',
            body: requestBody,
            mediaType: 'application/json',
            errors: {
                401: `Unauthorized`,
            },
        });
    }
    /**
     * @returns void
     * @throws ApiError
     */
    public static authLogout(): CancelablePromise<void> {
        return __request(OpenAPI, {
            method: 'POST',
            url: '/api/v1/auth/logout',
        });
    }
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
