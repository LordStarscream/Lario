// @vitest-environment jsdom
import {
  formatBookingId,
  formatOperationId,
  formatSplitId,
  OP_SEQ_META_KEY,
  parseOpSeq,
} from './operation-id';

describe('formatOperationId (Design B2)', () => {
  // 1.11.2026, mittags — Mittagszeit vermeidet TZ-Randfälle: die lokale
  // JJJJ/MMDD-Komponente ist in jeder Zeitzone dieselbe.
  const now = new Date(2026, 10, 1, 12, 0, 0);

  it('verwendet das lokale Datum und den 3-stelligen Zähler', () => {
    expect(formatOperationId(now, 1)).toBe('op-2026-1101-001');
    expect(formatOperationId(now, 7)).toBe('op-2026-1101-007');
    expect(formatOperationId(now, 999)).toBe('op-2026-1101-999');
  });

  it('füllt den Zähler bei 1000+ weiter auf (4+ Stellen)', () => {
    expect(formatOperationId(now, 1000)).toBe('op-2026-1101-1000');
    expect(formatOperationId(now, 12345)).toBe('op-2026-1101-12345');
  });

  it('nutzt die lokale Zeit (nicht UTC)', () => {
    // Lokal 1.11.2026 (12 Uhr): UTC kann je nach Zeitzone am 1. oder 2.
    // liegen — die ID muss IMMER 1101 tragen.
    expect(formatOperationId(now, 1)).toBe('op-2026-1101-001');
  });
});

describe('parseOpSeq (Design B2)', () => {
  it('null/leer → 1 (frisches Gerät)', () => {
    expect(parseOpSeq(null)).toBe(1);
    expect(parseOpSeq('')).toBe(1);
    expect(parseOpSeq('   ')).toBe(1);
  });

  it('gültige Zählerwerte lesen', () => {
    expect(parseOpSeq('42')).toBe(42);
    expect(parseOpSeq('1')).toBe(1);
    expect(parseOpSeq('1000')).toBe(1000);
  });

  it('nicht-positiv/ungültig → werfen', () => {
    for (const value of ['0', '-1', 'x', '1.5']) {
      expect(() => parseOpSeq(value)).toThrow();
    }
  });
});

describe('formatBookingId / formatSplitId (Design B2)', () => {
  it('formatBookingId: bk- + seq 3-stellig + - + nowMs in Base-36', () => {
    expect(formatBookingId(1, 1796100000000)).toBe(
      `bk-001-${(1796100000000).toString(36)}`,
    );
    expect(formatBookingId(7, 1000)).toBe(`bk-007-${(1000).toString(36)}`);
  });

  it('formatSplitId: Buchungs-ID + Position (1-basiert)', () => {
    const bookingId = formatBookingId(1, 1796100000000);
    expect(formatSplitId(bookingId, 1)).toBe(`${bookingId}-s1`);
    expect(formatSplitId(bookingId, 2)).toBe(`${bookingId}-s2`);
  });
});

describe('OP_SEQ_META_KEY (Design B2)', () => {
  it('ist der definierte Meta-Key', () => {
    expect(OP_SEQ_META_KEY).toBe('outbox.op_seq');
  });
});
