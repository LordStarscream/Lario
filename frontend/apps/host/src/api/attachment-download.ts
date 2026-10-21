import { AuthService } from './auth';

/**
 * Binärer Download eines Belegs (Design D4): der generierte fetch-
 * Client liest Nicht-JSON-Antworten als Text — für binäre Typen
 * (PDF, Bilder) würde das die Daten beschädigen. Und ein
 * `<img src>` kann kein Bearer-Token mitsenden. Deshalb holen wir
 * den Blob hier und erzeugen Blob-URLs auf der Aufruferseite.
 */
export async function fetchAttachmentBlob(id: string): Promise<Blob> {
  const token = AuthService.token();
  const response = await fetch(
    `/api/v1/attachments/${encodeURIComponent(id)}/download`,
    {
      headers: token ? { Authorization: `Bearer ${token}` } : undefined,
    },
  );
  if (!response.ok) {
    throw new Error(
      response.status === 410
        ? 'Die Datei ist nicht mehr verfügbar (410).'
        : `Der Download ist fehlgeschlagen (HTTP ${response.status}).`,
    );
  }
  return response.blob();
}
