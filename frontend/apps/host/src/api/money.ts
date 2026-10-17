const euroFormatter = new Intl.NumberFormat('de-DE', {
  style: 'currency',
  currency: 'EUR',
});

/** Zentwert aus dem generierten `number | string`-Typ. */
export function cents(value: number | string | null | undefined): number {
  if (value === null || value === undefined) {
    return 0;
  }
  const n = typeof value === 'number' ? value : Number(value);
  return Number.isFinite(n) ? Math.trunc(n) : 0;
}

/** Zentwert als Euro-String formatieren (z. B. "1.234,56 €"). */
export function formatCents(value: number | string | null | undefined): string {
  return euroFormatter.format(cents(value) / 100);
}

/**
 * Zentwert als Euro-Eingabetext für Maskened-Felder („1234,56").
 * Inverse zu {@link parseEuroToCents}: der zurückgegebene Text wird
 * von `parseEuroToCents` wieder exakt zum selben Zentwert geparst
 * (z. B. beim Vorbelegen von Korrektur-Formularen).
 */
export function centsToEuroText(value: number | string | null | undefined): string {
  const c = Math.abs(cents(value));
  const sign = cents(value) < 0 ? '-' : '';
  return `${sign}${Math.trunc(c / 100)},${String(c % 100).padStart(2, '0')}`;
}

const ISO_DATE = /^\d{4}-\d{2}-\d{2}$/;

/** ISO-Datum "2026-10-15" als "15.10.2026" formatieren. */
export function formatDate(iso: string | null | undefined): string {
  if (!iso || !ISO_DATE.test(iso)) {
    return iso ?? '—';
  }
  const [year, month, day] = iso.split('-');
  return `${day}.${month}.${year}`;
}

/** Heutiges Datum als ISO-String "JJJJ-MM-TT". */
export function todayIso(): string {
  const now = new Date();
  const month = String(now.getMonth() + 1).padStart(2, '0');
  const day = String(now.getDate()).padStart(2, '0');
  return `${now.getFullYear()}-${month}-${day}`;
}

/**
 * Euro-Eingabe in Zenten umwandeln. Akzeptiert (deutsche
 * Konvention, konsistent mit dem Anzeigeformat von `formatCents`):
 * - Ganzzahl: "85", "1000"
 * - Dezimalkomma: "85,50", "85,5"
 * - Dezimalpunkt: "85.50", "85.5"
 * - Tausenderpunkt: "1.000", "1.000.000", "1.000,50", "12.345,6"
 * Gibt null zurück, wenn die Eingabe ungültig ist (leer, negatives
 * Vorzeichen, mehr als zwei Dezimalstellen, fehlerhaft gruppierte
 * Tausenderstellen, falsche Zeichen). Zuvor gültige Eingaben behalten
 * ihren Wert; neu akzeptiert wird nur, was das Anzeigeformat zeigt.
 */
export function parseEuroToCents(input: string): number | null {
  const text = input.trim().replace(/\s/g, '');
  if (text === '') {
    return null;
  }

  // Deutsches Format: optionale Tausender-Punkte, ein Komma,
  // 1–2 Dezimalstellen.
  if (text.includes(',')) {
    const [intPart, ...rest] = text.split(',');
    if (rest.length !== 1) {
      return null;
    }
    const fraction = rest[0];
    if (!/^\d{1,2}$/.test(fraction)) {
      return null;
    }
    // Tausenderpunkt nur in korrekten Dreiergruppen; ohne Punkt
    // bleibt die Zahl unverändert zulässig.
    if (intPart.includes('.') && !/^\d{1,3}(\.\d{3})*$/.test(intPart)) {
      return null;
    }
    const intDigits = intPart.replace(/\./g, '');
    if (!/^\d+$/.test(intDigits)) {
      return null;
    }
    return Number(intDigits) * 100 + Number(fraction.padEnd(2, '0'));
  }

  // Punkt ohne Komma: Dezimalpunkt (1–2 Stellen) oder deutscher
  // Tausenderpunkt (Dreiergruppen).
  if (text.includes('.')) {
    const parts = text.split('.');
    const last = parts[parts.length - 1];
    if (parts.length === 2 && /^\d+$/.test(parts[0]) && /^\d{1,2}$/.test(last)) {
      return Number(parts[0]) * 100 + Number(last.padEnd(2, '0'));
    }
    if (
      /^\d{1,3}$/.test(parts[0]) &&
      parts.slice(1).every((part) => /^\d{3}$/.test(part))
    ) {
      return Number(text.replace(/\./g, '')) * 100;
    }
    return null;
  }

  // Reine Ziffern: "85" → 85 €.
  if (!/^\d+$/.test(text)) {
    return null;
  }
  return Number(text) * 100;
}
