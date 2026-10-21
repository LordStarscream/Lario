import { describe, expect, it } from 'vitest';
import type { ApiRequestOptions } from '../generated/core/ApiRequestOptions';
import type { ApiResult } from '../generated/core/ApiResult';
import { ApiError } from '../generated/core/ApiError';
import { toApiErrorInfo } from './api-error';

function apiError(status: number, body: unknown, statusText = ''): ApiError {
  const request: ApiRequestOptions = {
    method: 'POST',
    url: 'http://host/api/v1/attachments',
  };
  const result: ApiResult = {
    url: 'http://host/api/v1/attachments',
    ok: false,
    status,
    statusText,
    body,
  };
  return new ApiError(request, result, 'Fehler');
}

describe('toApiErrorInfo', () => {
  it('liest Code und Message aus dem Wannen-Format', () => {
    const info = toApiErrorInfo(
      apiError(413, {
        error: {
          code: 'validation_error',
          message: 'Datei überschreitet die maximale Größe von 10485760 Byte.',
          expectedBaseRevision: null,
          actualBaseRevision: null,
          detailCode: 'attachment_too_large',
        },
      }),
    );
    expect(info.code).toBe('validation_error');
    expect(info.message).toBe(
      'Datei überschreitet die maximale Größe von 10485760 Byte.',
    );
  });

  it('liefert bei 413 ohne Body (Kestrel-Envelope-Limit) eine nutzbare Meldung statt „HTTP 413“', () => {
    const info = toApiErrorInfo(apiError(413, undefined));
    expect(info.code).toBe('http_413');
    expect(info.message).toBe('Die Datei ist größer als das Upload-Limit des Servers.');
  });

  it('liefert bei 413 ohne Body auch mit Status-Text eine nutzbare Meldung', () => {
    const info = toApiErrorInfo(apiError(413, undefined, 'Payload Too Large'));
    expect(info.message).toBe('Die Datei ist größer als das Upload-Limit des Servers.');
  });

  it('übernimmt Revisionsfelder aus dem Body', () => {
    const info = toApiErrorInfo(
      apiError(409, {
        error: {
          code: 'domain_conflict',
          message: 'Revisionskonflikt.',
          expectedBaseRevision: 2,
          actualBaseRevision: 3,
        },
      }),
    );
    expect(info.expectedBaseRevision).toBe(2);
    expect(info.actualBaseRevision).toBe(3);
  });

  it('behält für Status ohne Body und ohne Fallback den HTTP-Text', () => {
    const info = toApiErrorInfo(apiError(502, undefined, 'Bad Gateway'));
    expect(info.code).toBe('http_502');
    expect(info.message).toBe('HTTP 502 Bad Gateway');
  });

  it('normalisiert nicht-API-Fehler', () => {
    const info = toApiErrorInfo(new Error('Netzwerkfehler'));
    expect(info.code).toBe('unknown');
    expect(info.message).toBe('Netzwerkfehler');
  });
});
