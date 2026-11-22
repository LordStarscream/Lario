/**
 * Schritt 9 — Herkunfts-Version des lokalen Geräts (Meta
 * `sync.origin_version`).
 *
 * Die Version ist der Herkunftsnachweis lokaler Eingaben (Review-Befund
 * P1 2, 19.11.2026): Sie wird ausschließlich vom Setup-Commit bei
 * erfolgreichem Hostwechsel erhöht (der erste Setup initialisiert sie
 * auf 1) und ist der einzige Vergleichsmaßstab für
 * `BookingsRepository.save`. Key und Parsing liegen in diesem
 * geteilten Modul, weil die Formularlade
 * (`SnapshotRepository.loadSnapshotWithOrigin`) und der
 * `save`-Recheck (`currentOriginVersion`) denselben Key mit derselben
 * Interpretation lesen müssen — eine abweichende Lesart (z. B.
 * anderer Key, andere Behandlung ungültiger Werte) würde den
 * Herkunftsnachweis still entwerten.
 */

/** Meta-Key der Herkunfts-Version (wird vom Setup-Commit erhöht). */
export const ORIGIN_VERSION_META_KEY = 'sync.origin_version';

/**
 * Gespeicherte Herkunfts-Version parsen: nur eine positive ganze
 * Zahl ist eine Version; alles andere (fehlend, leer, defekt)
 * bedeutet `0` (noch keine Herkunft, z. B. vor dem ersten Setup).
 */
export function parseOriginVersion(
  raw: string | null | undefined,
): number {
  const parsed =
    raw === null || raw === undefined
      ? Number.NaN
      : Number.parseInt(raw, 10);
  return Number.isInteger(parsed) && parsed > 0 ? parsed : 0;
}
