/* generated using openapi-typescript-codegen -- do not edit */
/* istanbul ignore file */
/* tslint:disable */
/* eslint-disable */
import type { SyncChangeDto } from './SyncChangeDto';
export type SyncChangesDto = {
    cursor: number | string;
    complete: boolean;
    truncated: boolean;
    changes: Array<SyncChangeDto>;
};

