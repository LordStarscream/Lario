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

/**
 * Fallback-Meldungen für HTTP-Status ohne nutzbares Fehler-Body.
 * Wichtiger Fall: 413, wenn Kestrel den gesamten Multipart-Envelope
 * über der abgeleiteten <c>MaxRequestBodySize</c> (Upload-Limit + 1 MB)
 * ablehnt — Kestrel schreibt die 413 selbst, ohne dass der Host einen
 * JSON-Body liefern kann (Beleg-Upload mit Datei deutlich über dem
 * Limit). Der Nutzer sieht sonst nur „HTTP 413“ ohne Handlungshinweis.
 */
const STATUS_FALLBACK_MESSAGES: Record<number, string> = {
  413: 'Die Datei ist größer als das Upload-Limit des Servers.',
  408: 'Die Anfrage ist abgelaufen. Bitte erneut versuchen.',
  504: 'Der Host hat nicht rechtzeitig geantwortet. Bitte erneut versuchen.',
};

function fallbackMessage(error: ApiErrorClass): string {
  return STATUS_FALLBACK_MESSAGES[error.status] ?? `HTTP ${error.status} ${error.statusText}`.trim();
}

/** Wandelt einen abgefangenen Fehler in eine anzeigbare Information um. */
export function toApiErrorInfo(error: unknown): ApiErrorInfo {
  if (error instanceof ApiErrorClass) {
    const inner: ApiErrorBody | undefined = (
      error.body as { error?: ApiErrorBody } | null
    )?.error;
    return {
      code: inner?.code ?? `http_${error.status}`,
      message: inner?.message ?? fallbackMessage(error),
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
