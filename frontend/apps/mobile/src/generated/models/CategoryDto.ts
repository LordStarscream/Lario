/* generated using openapi-typescript-codegen -- do not edit */
/* istanbul ignore file */
/* tslint:disable */
/* eslint-disable */
import type { TaxPresetDto } from './TaxPresetDto';
export type CategoryDto = {
    id: string;
    name: string;
    parentId: string | null;
    status: string;
    baseRevision: number | string;
    taxPreset: (null | TaxPresetDto);
};

