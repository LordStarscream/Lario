/**
 * Batch D (P4) — EUR-Betragshilfe für die mobile Erfassung.
 *
 * `parseAmount` ist die 1:1-Extraktion der gleichnamigen Funktion aus
 * `app.ts` (Prototyp Schritt 2); `app.ts` erhält sie per Re-Export
 * zurück (`app.html` und `app.spec.ts` bleiben unverändert).
 * `formatCents` ist die Gegenrichtung für die Live-Abgleich-Anzeige
 * des Erfassen-Formulars (Batch D, P4).
 *
 * Wie im Prototyp: nur sichere Ganzzahlen (Cent), kein Float.
 */

/**
 * Geldbeträge werden als ganzzahlige Centwerte gespeichert. Der
 * Prototyp akzeptiert nur das dezimale Komma („12,50“); ganze Zahlen
 * („12“) sind erlaubt. Ungültige Eingaben werden abgelehnt, nicht
 * gerundet oder still gedeutet.
 *
 * @returns `null` = leere Eingabe (kein Wert), `undefined` = ungültig,
 *   sonst die ganzzahlige Cent-Angabe.
 */
export function parseAmount(raw: string): number | null | undefined {
  const trimmed = raw.trim();
  if (trimmed === '') {
    return null;
  }
  // 13 Stellen vor dem Komma = Systemgrenze (long-Cent-Speicher, .NET);
  // 10^15 Cent bleibt innerhalb von Number.MAX_SAFE_INTEGER.
  const match = /^(\d{1,13})(?:[.,](\d{1,2}))?$/.exec(trimmed);
  if (!match) {
    return undefined;
  }
  const decimals = (match[2] ?? '').padEnd(2, '0');
  return Number(match[1]) * 100 + Number(decimals);
}

/**
 * Formatiert einen nichtnegativen Cent-Betrag als "12,50" (deutsches
 * Dezimalkomma). Negative Werte sind keine gültigen Ausgabe-Beträge und
 * werden nicht formatiert (ruft eine Exception aus).
 */
export function formatCents(cents: number): string {
  if (!Number.isSafeInteger(cents) || cents < 0) {
    throw new Error(`formatCents: keine nichtnegative sichere Ganzzahl: ${cents}`);
  }
  const euros = Math.floor(cents / 100);
  const rest = String(cents % 100).padStart(2, '0');
  return `${euros},${rest}`;
}
