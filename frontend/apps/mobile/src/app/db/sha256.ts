/**
 * Reines TypeScript SHA-256 (NIST FIPS 180-4) — Schritt 8, Design B1.
 *
 * Ohne `crypto.subtle`, ohne Node-APIs: Der Hash muss im Android-WebView
 * laufen. `TextEncoder` ist erlaubt — WHATWG-Standard, in allen WebViews
 * vorhanden.
 *
 * Verwendung: deterministischer Payload-Nachweis der Outbox-Einträge
 * (D5) — `sha256:` + Hex-Verdau. Die Implementierung folgt der
 * klassischen FIPS-180-4-Struktur: UTF-8-Bytefolge, Padding
 * (0x80 + Nullen bis Länge ≡ 56 (mod 64) + 64-Bit-Bitlänge
 * big-endian), 512-Bit-Blöcke, 64 Kompressionsrunden mit den 64
 * K-Konstanten und H0–H7 (erste 32 Bit der Quadratwurzeln der ersten
 * 8 Primzahlen). Alle Ganzzahloperationen laufen mod 2^32 (bitweise
 * Operatoren, keine Float-Tricks), Ausgabe big-endian pro Wort.
 */

// K-Konstanten: erste 32 Bit der Kubikwurzeln der ersten 64 Primzahlen.
const K: readonly number[] = [
  0x428a2f98, 0x71374491, 0xb5c0fbcf, 0xe9b5dba5, 0x3956c25b, 0x59f111f1,
  0x923f82a4, 0xab1c5ed5, 0xd807aa98, 0x12835b01, 0x243185be, 0x550c7dc3,
  0x72be5d74, 0x80deb1fe, 0x9bdc06a7, 0xc19bf174, 0xe49b69c1, 0xefbe4786,
  0x0fc19dc6, 0x240ca1cc, 0x2de92c6f, 0x4a7484aa, 0x5cb0a9dc, 0x76f988da,
  0x983e5152, 0xa831c66d, 0xb00327c8, 0xbf597fc7, 0xc6e00bf3, 0xd5a79147,
  0x06ca6351, 0x14292967, 0x27b70a85, 0x2e1b2138, 0x4d2c6dfc, 0x53380d13,
  0x650a7354, 0x766a0abb, 0x81c2c92e, 0x92722c85, 0xa2bfe8a1, 0xa81a664b,
  0xc24b8b70, 0xc76c51a3, 0xd192e819, 0xd6990624, 0xf40e3585, 0x106aa070,
  0x19a4c116, 0x1e376c08, 0x2748774c, 0x34b0bcb5, 0x391c0cb3, 0x4ed8aa4a,
  0x5b9cca4f, 0x682e6ff3, 0x748f82ee, 0x78a5636f, 0x84c87814, 0x8cc70208,
  0x90befffa, 0xa4506ceb, 0xbef9a3f7, 0xc67178f2,
];

/** Rechtsdrehung um n Bit (mod 2^32). */
function rotr(x: number, n: number): number {
  return (x >>> n) | (x << (32 - n));
}

/**
 * SHA-256-Verdau als 64-stellige kleine Hex-Zeichenfolge (UTF-8-Eingabe).
 */
export function sha256Hex(input: string): string {
  const bytes = new TextEncoder().encode(input);
  const bitLength = bytes.length * 8;

  // Padding: 0x80 + Nullen bis Länge ≡ 56 (mod 64) + 8-Byte-Bitlänge
  // big-endian. Gesamtlänge: nächstes Vielfaches von 64 ≥ Länge + 9
  // (1 Byte 0x80 + 8 Byte Länge).
  const total = 64 * Math.ceil((bytes.length + 9) / 64);
  const padded = new Uint8Array(total);
  padded.set(bytes);
  padded[bytes.length] = 0x80;
  padded[total - 8] = (bitLength / 0x100000000) | 0; // hohe 32 Bit (0 für < 512 MiB)
  padded[total - 4] = (bitLength >>> 24) & 0xff;
  padded[total - 3] = (bitLength >>> 16) & 0xff;
  padded[total - 2] = (bitLength >>> 8) & 0xff;
  padded[total - 1] = bitLength & 0xff;

  // H0–H7: erste 32 Bit der Quadratwurzeln der ersten 8 Primzahlen.
  const h = [
    0x6a09e667, 0xbb67ae85, 0x3c6ef372, 0xa54ff53a,
    0x510e527f, 0x9b05688c, 0x1f83d9ab, 0x5be0cd19,
  ];
  const w = new Array<number>(64);

  for (let offset = 0; offset < total; offset += 64) {
    for (let t = 0; t < 16; t++) {
      const j = offset + t * 4;
      w[t] =
        (padded[j] << 24) |
        (padded[j + 1] << 16) |
        (padded[j + 2] << 8) |
        padded[j + 3];
    }
    for (let t = 16; t < 64; t++) {
      const w15 = w[t - 15];
      const w2 = w[t - 2];
      const s0 = rotr(w15, 7) ^ rotr(w15, 18) ^ (w15 >>> 3);
      const s1 = rotr(w2, 17) ^ rotr(w2, 19) ^ (w2 >>> 10);
      w[t] = (w[t - 16] + s0 + w[t - 7] + s1) | 0;
    }

    let a = h[0];
    let b = h[1];
    let c = h[2];
    let d = h[3];
    let e = h[4];
    let f = h[5];
    let g = h[6];
    let hh = h[7];

    for (let t = 0; t < 64; t++) {
      const S1 = rotr(e, 6) ^ rotr(e, 11) ^ rotr(e, 25);
      const ch = (e & f) ^ (~e & g);
      const temp1 = (hh + S1 + ch + K[t] + w[t]) | 0;
      const S0 = rotr(a, 2) ^ rotr(a, 13) ^ rotr(a, 22);
      const maj = (a & b) ^ (a & c) ^ (b & c);
      const temp2 = (S0 + maj) | 0;
      hh = g;
      g = f;
      f = e;
      e = (d + temp1) | 0;
      d = c;
      c = b;
      b = a;
      a = (temp1 + temp2) | 0;
    }

    h[0] = (h[0] + a) | 0;
    h[1] = (h[1] + b) | 0;
    h[2] = (h[2] + c) | 0;
    h[3] = (h[3] + d) | 0;
    h[4] = (h[4] + e) | 0;
    h[5] = (h[5] + f) | 0;
    h[6] = (h[6] + g) | 0;
    h[7] = (h[7] + hh) | 0;
  }

  let hex = '';
  for (const word of h) {
    hex += (word >>> 0).toString(16).padStart(8, '0');
  }
  return hex;
}

/**
 * Deterministischer Payload-Nachweis: `sha256:` + 64-stellige
 * kleine Hex-Zeichenfolge.
 */
export function payloadHash(payload: string): string {
  return `sha256:${sha256Hex(payload)}`;
}
