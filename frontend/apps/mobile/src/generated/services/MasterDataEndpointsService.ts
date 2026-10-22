/* generated using openapi-typescript-codegen -- do not edit */
/* istanbul ignore file */
/* tslint:disable */
/* eslint-disable */
import type { AccountDto } from '../models/AccountDto';
import type { CategoryDto } from '../models/CategoryDto';
import type { CreateAccountRequest } from '../models/CreateAccountRequest';
import type { CreateCategoryRequest } from '../models/CreateCategoryRequest';
import type { CreatePoolRequest } from '../models/CreatePoolRequest';
import type { CreateTaxCategoryRequest } from '../models/CreateTaxCategoryRequest';
import type { PoolDto } from '../models/PoolDto';
import type { RenameRequest } from '../models/RenameRequest';
import type { TaxCategoryDto } from '../models/TaxCategoryDto';
import type { TaxPresetDto } from '../models/TaxPresetDto';
import type { TaxPresetRequest } from '../models/TaxPresetRequest';
import type { CancelablePromise } from '../core/CancelablePromise';
import { OpenAPI } from '../core/OpenAPI';
import { request as __request } from '../core/request';
export class MasterDataEndpointsService {
    /**
     * @returns AccountDto OK
     * @throws ApiError
     */
    public static listAccounts(): CancelablePromise<Array<AccountDto>> {
        return __request(OpenAPI, {
            method: 'GET',
            url: '/api/v1/accounts',
        });
    }
    /**
     * @param requestBody
     * @returns AccountDto OK
     * @throws ApiError
     */
    public static createAccount(
        requestBody?: (null | CreateAccountRequest),
    ): CancelablePromise<AccountDto> {
        return __request(OpenAPI, {
            method: 'POST',
            url: '/api/v1/accounts',
            body: requestBody,
            mediaType: 'application/json',
            errors: {
                400: `Bad Request`,
            },
        });
    }
    /**
     * @param id
     * @returns AccountDto OK
     * @throws ApiError
     */
    public static getAccount(
        id: string,
    ): CancelablePromise<AccountDto> {
        return __request(OpenAPI, {
            method: 'GET',
            url: '/api/v1/accounts/{id}',
            path: {
                'id': id,
            },
            errors: {
                400: `Bad Request`,
            },
        });
    }
    /**
     * @param id
     * @param requestBody
     * @returns AccountDto OK
     * @throws ApiError
     */
    public static renameAccount(
        id: string,
        requestBody?: (null | RenameRequest),
    ): CancelablePromise<AccountDto> {
        return __request(OpenAPI, {
            method: 'PUT',
            url: '/api/v1/accounts/{id}/name',
            path: {
                'id': id,
            },
            body: requestBody,
            mediaType: 'application/json',
            errors: {
                400: `Bad Request`,
            },
        });
    }
    /**
     * @param id
     * @returns AccountDto OK
     * @throws ApiError
     */
    public static archiveAccount(
        id: string,
    ): CancelablePromise<AccountDto> {
        return __request(OpenAPI, {
            method: 'POST',
            url: '/api/v1/accounts/{id}/archive',
            path: {
                'id': id,
            },
            errors: {
                400: `Bad Request`,
            },
        });
    }
    /**
     * @param id
     * @returns AccountDto OK
     * @throws ApiError
     */
    public static unarchiveAccount(
        id: string,
    ): CancelablePromise<AccountDto> {
        return __request(OpenAPI, {
            method: 'POST',
            url: '/api/v1/accounts/{id}/unarchive',
            path: {
                'id': id,
            },
            errors: {
                400: `Bad Request`,
            },
        });
    }
    /**
     * @returns PoolDto OK
     * @throws ApiError
     */
    public static listPools(): CancelablePromise<Array<PoolDto>> {
        return __request(OpenAPI, {
            method: 'GET',
            url: '/api/v1/pools',
        });
    }
    /**
     * @param requestBody
     * @returns PoolDto OK
     * @throws ApiError
     */
    public static createPool(
        requestBody?: (null | CreatePoolRequest),
    ): CancelablePromise<PoolDto> {
        return __request(OpenAPI, {
            method: 'POST',
            url: '/api/v1/pools',
            body: requestBody,
            mediaType: 'application/json',
            errors: {
                400: `Bad Request`,
            },
        });
    }
    /**
     * @param id
     * @returns PoolDto OK
     * @throws ApiError
     */
    public static getPool(
        id: string,
    ): CancelablePromise<PoolDto> {
        return __request(OpenAPI, {
            method: 'GET',
            url: '/api/v1/pools/{id}',
            path: {
                'id': id,
            },
            errors: {
                400: `Bad Request`,
            },
        });
    }
    /**
     * @param id
     * @param requestBody
     * @returns PoolDto OK
     * @throws ApiError
     */
    public static renamePool(
        id: string,
        requestBody?: (null | RenameRequest),
    ): CancelablePromise<PoolDto> {
        return __request(OpenAPI, {
            method: 'PUT',
            url: '/api/v1/pools/{id}/name',
            path: {
                'id': id,
            },
            body: requestBody,
            mediaType: 'application/json',
            errors: {
                400: `Bad Request`,
            },
        });
    }
    /**
     * @param id
     * @returns PoolDto OK
     * @throws ApiError
     */
    public static archivePool(
        id: string,
    ): CancelablePromise<PoolDto> {
        return __request(OpenAPI, {
            method: 'POST',
            url: '/api/v1/pools/{id}/archive',
            path: {
                'id': id,
            },
            errors: {
                400: `Bad Request`,
            },
        });
    }
    /**
     * @param id
     * @returns PoolDto OK
     * @throws ApiError
     */
    public static unarchivePool(
        id: string,
    ): CancelablePromise<PoolDto> {
        return __request(OpenAPI, {
            method: 'POST',
            url: '/api/v1/pools/{id}/unarchive',
            path: {
                'id': id,
            },
            errors: {
                400: `Bad Request`,
            },
        });
    }
    /**
     * @returns CategoryDto OK
     * @throws ApiError
     */
    public static listCategories(): CancelablePromise<Array<CategoryDto>> {
        return __request(OpenAPI, {
            method: 'GET',
            url: '/api/v1/categories',
        });
    }
    /**
     * @param requestBody
     * @returns CategoryDto OK
     * @throws ApiError
     */
    public static createCategory(
        requestBody?: (null | CreateCategoryRequest),
    ): CancelablePromise<CategoryDto> {
        return __request(OpenAPI, {
            method: 'POST',
            url: '/api/v1/categories',
            body: requestBody,
            mediaType: 'application/json',
            errors: {
                400: `Bad Request`,
            },
        });
    }
    /**
     * @param id
     * @returns CategoryDto OK
     * @throws ApiError
     */
    public static getCategory(
        id: string,
    ): CancelablePromise<CategoryDto> {
        return __request(OpenAPI, {
            method: 'GET',
            url: '/api/v1/categories/{id}',
            path: {
                'id': id,
            },
            errors: {
                400: `Bad Request`,
            },
        });
    }
    /**
     * @param id
     * @param requestBody
     * @returns CategoryDto OK
     * @throws ApiError
     */
    public static renameCategory(
        id: string,
        requestBody?: (null | RenameRequest),
    ): CancelablePromise<CategoryDto> {
        return __request(OpenAPI, {
            method: 'PUT',
            url: '/api/v1/categories/{id}/name',
            path: {
                'id': id,
            },
            body: requestBody,
            mediaType: 'application/json',
            errors: {
                400: `Bad Request`,
            },
        });
    }
    /**
     * @param id
     * @returns CategoryDto OK
     * @throws ApiError
     */
    public static archiveCategory(
        id: string,
    ): CancelablePromise<CategoryDto> {
        return __request(OpenAPI, {
            method: 'POST',
            url: '/api/v1/categories/{id}/archive',
            path: {
                'id': id,
            },
            errors: {
                400: `Bad Request`,
            },
        });
    }
    /**
     * @param id
     * @returns CategoryDto OK
     * @throws ApiError
     */
    public static unarchiveCategory(
        id: string,
    ): CancelablePromise<CategoryDto> {
        return __request(OpenAPI, {
            method: 'POST',
            url: '/api/v1/categories/{id}/unarchive',
            path: {
                'id': id,
            },
            errors: {
                400: `Bad Request`,
            },
        });
    }
    /**
     * @param id
     * @param requestBody
     * @returns TaxPresetDto OK
     * @throws ApiError
     */
    public static setTaxPreset(
        id: string,
        requestBody?: (null | TaxPresetRequest),
    ): CancelablePromise<TaxPresetDto> {
        return __request(OpenAPI, {
            method: 'PUT',
            url: '/api/v1/categories/{id}/tax-preset',
            path: {
                'id': id,
            },
            body: requestBody,
            mediaType: 'application/json',
            errors: {
                400: `Bad Request`,
            },
        });
    }
    /**
     * @param id
     * @returns void
     * @throws ApiError
     */
    public static removeTaxPreset(
        id: string,
    ): CancelablePromise<void> {
        return __request(OpenAPI, {
            method: 'DELETE',
            url: '/api/v1/categories/{id}/tax-preset',
            path: {
                'id': id,
            },
            errors: {
                400: `Bad Request`,
            },
        });
    }
    /**
     * @returns TaxCategoryDto OK
     * @throws ApiError
     */
    public static listTaxCategories(): CancelablePromise<Array<TaxCategoryDto>> {
        return __request(OpenAPI, {
            method: 'GET',
            url: '/api/v1/tax-categories',
        });
    }
    /**
     * @param requestBody
     * @returns TaxCategoryDto OK
     * @throws ApiError
     */
    public static createTaxCategory(
        requestBody?: (null | CreateTaxCategoryRequest),
    ): CancelablePromise<TaxCategoryDto> {
        return __request(OpenAPI, {
            method: 'POST',
            url: '/api/v1/tax-categories',
            body: requestBody,
            mediaType: 'application/json',
            errors: {
                400: `Bad Request`,
            },
        });
    }
    /**
     * @param id
     * @returns TaxCategoryDto OK
     * @throws ApiError
     */
    public static getTaxCategory(
        id: string,
    ): CancelablePromise<TaxCategoryDto> {
        return __request(OpenAPI, {
            method: 'GET',
            url: '/api/v1/tax-categories/{id}',
            path: {
                'id': id,
            },
            errors: {
                400: `Bad Request`,
            },
        });
    }
    /**
     * @param id
     * @param requestBody
     * @returns TaxCategoryDto OK
     * @throws ApiError
     */
    public static renameTaxCategory(
        id: string,
        requestBody?: (null | RenameRequest),
    ): CancelablePromise<TaxCategoryDto> {
        return __request(OpenAPI, {
            method: 'PUT',
            url: '/api/v1/tax-categories/{id}/name',
            path: {
                'id': id,
            },
            body: requestBody,
            mediaType: 'application/json',
            errors: {
                400: `Bad Request`,
            },
        });
    }
    /**
     * @param id
     * @returns TaxCategoryDto OK
     * @throws ApiError
     */
    public static archiveTaxCategory(
        id: string,
    ): CancelablePromise<TaxCategoryDto> {
        return __request(OpenAPI, {
            method: 'POST',
            url: '/api/v1/tax-categories/{id}/archive',
            path: {
                'id': id,
            },
            errors: {
                400: `Bad Request`,
            },
        });
    }
    /**
     * @param id
     * @returns TaxCategoryDto OK
     * @throws ApiError
     */
    public static unarchiveTaxCategory(
        id: string,
    ): CancelablePromise<TaxCategoryDto> {
        return __request(OpenAPI, {
            method: 'POST',
            url: '/api/v1/tax-categories/{id}/unarchive',
            path: {
                'id': id,
            },
            errors: {
                400: `Bad Request`,
            },
        });
    }
}
