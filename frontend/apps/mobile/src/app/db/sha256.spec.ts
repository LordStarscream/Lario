// @vitest-environment jsdom
import { createHash } from 'node:crypto';
import { payloadHash, sha256Hex } from './sha256';

/** Test-only Referenz: `node:crypto` (Vitest läuft in Node). */
function nodeSha256(input: string): string {
  return createHash('sha256').update(input, 'utf8').digest('hex');
}

describe('sha256Hex (Design B1, NIST FIPS 180-4)', () => {
  it('NIST-Testvektoren (exakt, kleine Hex)', () => {
    const vectors: [string, string][] = [
      [
        '',
        'e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855',
      ],
      [
        'abc',
        'ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad',
      ],
      [
        'abcdbcdecdefdefgefghfghighijhijkijkljklmklmnlmnomnopnopq',
        '248d6a61d20638b8e5c026930c3e6039a33ce45964ff2167f6ecedd419db06c1',
      ],
      [
        'abcdefghbcdefghicdefghijdefghijkefghijklfghijklmghijklmnhijklmnoijklmnopjklmnopqklmnopqrlmnopqrsmnopqrstnopqrstu',
        'cf5b16a778af8380036ce59e7b0492370b249b11e8f07a51afac45037afee9d1',
      ],
      [
        'a'.repeat(1_000_000),
        'cdc76e5c9914fb9281a1c7e284d73e67f1809a48a497200e046d39ccc7112cd0',
      ],
    ];
    for (const [input, expected] of vectors) {
      expect(sha256Hex(input)).toBe(expected);
    }
  });

  it('UTF-8-Eingaben gegen node:crypto (Sänger, Euro-Zeichen)', () => {
    expect(sha256Hex('Sänger')).toBe(nodeSha256('Sänger'));
    expect(sha256Hex('€')).toBe(nodeSha256('€'));
  });

  it('Padding-Grenzen 55/56/64 Bytes gegen node:crypto', () => {
    for (const length of [55, 56, 64]) {
      const input = 'x'.repeat(length);
      expect(sha256Hex(input)).toBe(nodeSha256(input));
    }
  });

  it('liefert immer eine 64-stellige kleine Hex-Zeichenfolge', () => {
    expect(sha256Hex('beliebiger Inhalt')).toMatch(/^[0-9a-f]{64}$/);
    expect(sha256Hex('')).toMatch(/^[0-9a-f]{64}$/);
  });
});

describe('payloadHash (Design B1)', () => {
  it('präfixiert den Verdau mit sha256:', () => {
    const hash = payloadHash('{"a":1}');
    expect(hash).toBe(`sha256:${sha256Hex('{"a":1}')}`);
    expect(hash).toMatch(/^sha256:[0-9a-f]{64}$/);
  });

  it('ist deterministisch (zweimal bauen = identisch)', () => {
    const payload = '{"id":"bk-001","date":"2026-11-01"}';
    expect(payloadHash(payload)).toBe(payloadHash(payload));
  });

  it('unterscheidet sich bei geändertem Inhalt', () => {
    expect(payloadHash('{"a":1}')).not.toBe(payloadHash('{"a":2}'));
  });
});
