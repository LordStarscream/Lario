/**
 * Batch D — Tests für die Betragshilfen `parseAmount`/`formatCents`
 * (P4: Extraktion aus app.ts; Batch D Review: formatCents-Grenzfälle).
 *
 * `parseAmount`-Verhalten ist unverändert übernommen (Prototyp-Tests in
 * `app.spec.ts` bleiben der Referenz); hier zusätzlich die Systemgrenzen
 * der Long-Cent-Speicherung. `formatCents` ist neu in Batch D und
 * benötigt eigene Grenzwert-Tests (Review P3).
 */

import { parseAmount, formatCents } from './amount';

describe('parseAmount — Systemgrenzen (Long-Cent-Speicher)', () => {
  it('13 Ziffern vor dem Komma (maximal parsebar) → sichere Cent-Ganzzahl', () => {
    // 999999999999999 Cent ≈ 10^15 — innerhalb MAX_SAFE_INTEGER.
    expect(parseAmount('9999999999999')).toBe(999999999999900);
    expect(parseAmount('9999999999999,99')).toBe(999999999999999);
  });

  it('14 Ziffern vor dem Komma → ungültig (undefined), nicht still gekürzt', () => {
    expect(parseAmount('10000000000000')).toBeUndefined();
    expect(parseAmount('10000000000000,00')).toBeUndefined();
  });

  it('Eingabe-Grundformen bleiben unverändert', () => {
    expect(parseAmount('')).toBeNull();
    expect(parseAmount('  5  ')).toBe(500);
    expect(parseAmount('12')).toBe(1200);
    expect(parseAmount('12,5')).toBe(1250);
    expect(parseAmount('12.5')).toBe(1250);
    expect(parseAmount('1,234')).toBeUndefined(); // Tausender-Trenner unzulässig
    expect(parseAmount('-1')).toBeUndefined();
    expect(parseAmount('abc')).toBeUndefined();
  });
});

describe('formatCents — Grenzwerte (Batch D Review, P3)', () => {
  it('Normalfälle: ganze Euro ohne Cent-Anteil, sonst Dezimalkomma', () => {
    expect(formatCents(0)).toBe('0');
    expect(formatCents(5)).toBe('0,05');
    expect(formatCents(50)).toBe('0,50');
    expect(formatCents(99)).toBe('0,99');
    expect(formatCents(100)).toBe('1');
    expect(formatCents(1000)).toBe('10');
    expect(formatCents(1050)).toBe('10,50');
    expect(formatCents(123456789)).toBe('1234567,89');
  });

  it('Systemgrenze: MAX_SAFE_INTEGER Cent → gültiges String-Ergebnis', () => {
    // 9007199254740991 Cent = 90071992547409,91 € — die größte
    // darstellbare sichere Cent-Ganzzahl.
    expect(formatCents(Number.MAX_SAFE_INTEGER)).toBe('90071992547409,91');
  });

  it('Unsichere Ganzzahl (MAX_SAFE_INTEGER + 1) → Exception', () => {
    expect(() => formatCents(Number.MAX_SAFE_INTEGER + 1)).toThrow(
      /keine nichtnegative sichere Ganzzahl/,
    );
  });

  it('Negative Werte → Exception (keine Ausgabe-Beträge)', () => {
    expect(() => formatCents(-1)).toThrow(/keine nichtnegative sichere Ganzzahl/);
  });

  it('Nicht-ganzzahlige Werte → Exception', () => {
    expect(() => formatCents(10.5)).toThrow(/keine nichtnegative sichere Ganzzahl/);
  });
});
