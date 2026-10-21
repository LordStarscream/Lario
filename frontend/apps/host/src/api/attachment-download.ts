import { ApiError } from '../generated/core/ApiError';
import type { ApiRequestOptions } from '../generated/core/ApiRequestOptions';
import { AuthService } from './auth';

/**
 * Binärer Download eines Belegs (Design D4): der generierte fetch-
 * Client liest Nicht-JSON-Antworten als Text — für binäre Typen
 * (PDF, Bilder) würde das die Daten beschädigen. Und ein
 * `<img src>` kann kein Bearer-Token mitsenden. Deshalb holen wir
 * den Blob hier und erzeugen Blob-URLs auf der Aufruferseite.
 *
 * Nicht-2xx-Antworten werden zu einer <c>ApiError</c> mit
 * verständlicher Meldung (404/410 → Beleg fehlt/verfallen, 401 →
 * abgelaufene Sitzung) normalisiert, damit die zentrale
 * Fehlerbehandlung (ApiErrorHandler → toApiErrorInfo) greift: 401
 * leitet wie überall sonst zum Login weiter, 404/410/sonstige
 * Fehler zeigen im Beleg-Dialog eine deutsche Handlungsmeldung
 * statt „HTTP 410“ o. Ä.
 */
function toDownloadError(response: Response, url: string): ApiError {
  const message =
    response.status === 401
      ? 'Die Sitzung ist abgelaufen. Bitte erneut anmelden.'
      : response.status === 404
        ? 'Der Beleg wurde nicht gefunden oder wurde bereits entfernt.'
        : response.status === 410
          ? 'Die Datei ist nicht mehr verfügbar (410).'
          : `Der Download ist fehlgeschlagen (HTTP ${response.status}).`;
  return new ApiError(
    {} as ApiRequestOptions,
    {
      url,
      ok: false,
      status: response.status,
      statusText: response.statusText,
      body: { error: { code: `http_${response.status}`, message } },
    },
    message,
  );
}

export async function fetchAttachmentBlob(id: string): Promise<Blob> {
  const token = AuthService.token();
  const url = `/api/v1/attachments/${encodeURIComponent(id)}/download`;
  const response = await fetch(url, {
    headers: token ? { Authorization: `Bearer ${token}` } : undefined,
  });
  if (!response.ok) {
    throw toDownloadError(response, url);
  }
  return response.blob();
}
