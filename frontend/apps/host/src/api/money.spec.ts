import { describe, expect, it } from 'vitest';
import { centsToEuroText, formatCents, parseEuroToCents } from './money';

describe('centsToEuroText', () => {
  it('liefert einen Text, den parseEuroToCents exakt zurückparst (Round-trip)', () => {
    for (const cents of [0, 1, 5, 100, 999, 10000, 123456, 123456789]) {
      const text = centsToEuroText(cents);
      expect(parseEuroToCents(text), `Round-trip für ${cents}`).toBe(cents);
    }
  });

  it('formatiert wie erwartet (deutsches Dezimalkomma, zwei Stellen)', () => {
    expect(centsToEuroText(0)).toBe('0,00');
    expect(centsToEuroText(5)).toBe('0,05');
    expect(centsToEuroText(100)).toBe('1,00');
    expect(centsToEuroText(123456)).toBe('1234,56');
  });

  it('akzeptiert auch String-Werte (generierte DTO-Typen number | string)', () => {
    expect(centsToEuroText('123456')).toBe('1234,56');
    expect(centsToEuroText(null)).toBe('0,00');
    expect(centsToEuroText(undefined)).toBe('0,00');
  });
});

describe('parseEuroToCents', () => {
  it('akzeptiert einfache Formate (Ganzzahl, Komma, Punkt)', () => {
    expect(parseEuroToCents('85')).toBe(8500);
    expect(parseEuroToCents('1000')).toBe(100000);
    expect(parseEuroToCents('85,50')).toBe(8550);
    expect(parseEuroToCents('85,5')).toBe(8550);
    expect(parseEuroToCents('85.50')).toBe(8550);
    expect(parseEuroToCents('85.5')).toBe(8550);
  });

  it('akzeptiert Tausenderpunkte, wie sie formatCents anzeigt', () => {
    expect(parseEuroToCents('1.000')).toBe(100000);
    expect(parseEuroToCents('1.000,50')).toBe(100050);
    expect(parseEuroToCents('1.000.000')).toBe(100000000);
    expect(parseEuroToCents('12.345,6')).toBe(1234560);
    expect(parseEuroToCents('1.234')).toBe(123400);
  });

  it('jedes formatCents-Ergebnis (ohne „€“) lässt sich zurückparsen', () => {
    for (const cents of [0, 5, 123456, 1000500, 123456789012]) {
      // Anzeigeformat („1.234.567,89 €“) minus Währungszeichen/Leerzeichen:
      const text = formatCents(cents).replace(/[^\d.,]/g, '');
      expect(parseEuroToCents(text), `Round-trip für ${cents}`).toBe(cents);
    }
  });

  it('verwirft ungültige Eingaben', () => {
    expect(parseEuroToCents('')).toBeNull();
    expect(parseEuroToCents('  ')).toBeNull();
    expect(parseEuroToCents('-5')).toBeNull();
    expect(parseEuroToCents('1,2,3')).toBeNull();
    expect(parseEuroToCents('1,000')).toBeNull();
    expect(parseEuroToCents('1.00,50')).toBeNull();
    expect(parseEuroToCents('1..50')).toBeNull();
    expect(parseEuroToCents('.50')).toBeNull();
    expect(parseEuroToCents('1.23.45')).toBeNull();
    expect(parseEuroToCents('1.2345')).toBeNull();
    expect(parseEuroToCents('abc')).toBeNull();
    expect(parseEuroToCents('1.000,505')).toBeNull();
  });
});
