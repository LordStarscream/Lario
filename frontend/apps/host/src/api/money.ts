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
 * Euro-Eingabe ("85", "85,50", "85.50") in Zenten umwandeln.
 * Gibt null zurück, wenn die Eingabe ungültig ist (leer, negatives
 * Vorzeichen, mehr als zwei Dezimalstellen, falsche Zeichen).
 */
export function parseEuroToCents(input: string): number | null {
  const text = input.trim().replace(/\s/g, '');
  if (text === '') {
    return null;
  }
  const normalized = text.replace(',', '.');
  if (!/^\d+(\.\d{1,2})?$/.test(normalized)) {
    return null;
  }
  const [whole, fraction = ''] = normalized.split('.');
  return Number(whole) * 100 + Number(fraction.padEnd(2, '0'));
}
