import { ApiError as ApiErrorClass } from '../generated/core/ApiError';
import type { ApiErrorBody } from '../generated/models/ApiErrorBody';

/**
 * Normalisierte Fehlerinformation aus einer API-Antwort (Design §2.3):
 * Wire-Format ist die Wanne `{ "error": { code, message, ... } }`.
 */
export interface ApiErrorInfo {
  code: string;
  message: string;
  expectedBaseRevision: number | null;
  actualBaseRevision: number | null;
}

function toInt(value: number | string | null | undefined): number | null {
  if (value === null || value === undefined) {
    return null;
  }
  const n = Number(value);
  return Number.isFinite(n) ? Math.trunc(n) : null;
}

/** Wandelt einen abgefangenen Fehler in eine anzeigbare Information um. */
export function toApiErrorInfo(error: unknown): ApiErrorInfo {
  if (error instanceof ApiErrorClass) {
    const inner: ApiErrorBody | undefined = (
      error.body as { error?: ApiErrorBody } | null
    )?.error;
    return {
      code: inner?.code ?? `http_${error.status}`,
      message:
        inner?.message ??
        `HTTP ${error.status} ${error.statusText}`.trim(),
      expectedBaseRevision: toInt(inner?.expectedBaseRevision),
      actualBaseRevision: toInt(inner?.actualBaseRevision),
    };
  }
  return {
    code: 'unknown',
    message: error instanceof Error ? error.message : String(error),
    expectedBaseRevision: null,
    actualBaseRevision: null,
  };
}
