/* generated using openapi-typescript-codegen -- do not edit */
/* istanbul ignore file */
/* tslint:disable */
/* eslint-disable */
import type { AttachmentDto } from '../models/AttachmentDto';
import type { IFormFile } from '../models/IFormFile';
import type { CancelablePromise } from '../core/CancelablePromise';
import { OpenAPI } from '../core/OpenAPI';
import { request as __request } from '../core/request';
export class AttachmentEndpointsService {
    /**
     * @param formData
     * @returns AttachmentDto Created
     * @throws ApiError
     */
    public static uploadAttachment(
        formData: ({
            file?: IFormFile;
        } & {
            bookingId?: string;
        } & {
            splitId?: string;
        }),
    ): CancelablePromise<AttachmentDto> {
        return __request(OpenAPI, {
            method: 'POST',
            url: '/api/v1/attachments',
            formData: formData,
            mediaType: 'multipart/form-data',
            errors: {
                400: `Bad Request`,
                413: `Payload Too Large`,
            },
        });
    }
    /**
     * @param id
     * @returns AttachmentDto OK
     * @throws ApiError
     */
    public static getAttachment(
        id: string,
    ): CancelablePromise<AttachmentDto> {
        return __request(OpenAPI, {
            method: 'GET',
            url: '/api/v1/attachments/{id}',
            path: {
                'id': id,
            },
            errors: {
                404: `Not Found`,
            },
        });
    }
    /**
     * @param id
     * @returns void
     * @throws ApiError
     */
    public static deleteAttachment(
        id: string,
    ): CancelablePromise<void> {
        return __request(OpenAPI, {
            method: 'DELETE',
            url: '/api/v1/attachments/{id}',
            path: {
                'id': id,
            },
            errors: {
                404: `Not Found`,
            },
        });
    }
    /**
     * @param id
     * @returns string OK
     * @throws ApiError
     */
    public static downloadAttachment(
        id: string,
    ): CancelablePromise<string> {
        return __request(OpenAPI, {
            method: 'GET',
            url: '/api/v1/attachments/{id}/download',
            path: {
                'id': id,
            },
            errors: {
                404: `Not Found`,
                410: `Gone`,
            },
        });
    }
    /**
     * @param bookingId
     * @returns AttachmentDto OK
     * @throws ApiError
     */
    public static getBookingAttachments(
        bookingId: string,
    ): CancelablePromise<Array<AttachmentDto>> {
        return __request(OpenAPI, {
            method: 'GET',
            url: '/api/v1/bookings/{bookingId}/attachments',
            path: {
                'bookingId': bookingId,
            },
            errors: {
                400: `Bad Request`,
            },
        });
    }
}
