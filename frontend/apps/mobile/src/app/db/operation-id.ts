/**
 * Gerätegenerierte Operations-IDs und Buchungs-/Split-IDs (Schritt 8,
 * Design B2).
 *
 * Operations-IDs sind eindeutig pro Gerät und monoton wachsend:
 * `op-<JJJJ>-<MMDD>-<seq>` (Schritt-0-Beispiel `op-2026-1101-001`).
 * JJJJ/MMDD kommen aus der **lokalen Zeit** des `Date`-Arguments
 * (Geräteluftzeit; in Produktion Europe/Berlin) — nicht UTC. `seq` ist
 * ein persistierter monotoner Geräte-Zähler (Meta-Key
 * `outbox.op_seq`): Start 1, wächst pro Gerät strikt weiter und wird
 * NIE zurückgesetzt — weder bei Neustart noch beim Tageswechsel (das
 * Schritt-0-Beispiel zeigt `op-2026-1101-001` dann
 * `op-2026-1102-002`). Der Zähler wird mit 3 Stellen minimal
 * aufgefüllt (`padStart(3, '0')`) und darf darüber hinauswachsen
 * (4+ Stellen); die Eindeutigkeit bleibt dadurch gewahrt.
 */

/** Meta-Key des monotonen Geräte-Zählers. */
export const OP_SEQ_META_KEY = 'outbox.op_seq';

/**
 * `op-2026-1101-001` — JJJJ/MMDD aus der lokalen Zeit von `now`.
 */
export function formatOperationId(now: Date, seq: number): string {
  const yyyy = String(now.getFullYear());
  const mm = String(now.getMonth() + 1).padStart(2, '0');
  const dd = String(now.getDate()).padStart(2, '0');
  return `op-${yyyy}-${mm}${dd}-${String(seq).padStart(3, '0')}`;
}

/**
 * Meta-Wert auf Zählerwert prüfen/lesen: null/leer → 1; nicht-positives
 * Integer → werfen (Error).
 */
export function parseOpSeq(value: string | null): number {
  if (value === null || value.trim() === '') {
    return 1;
  }
  const seq = Number(value);
  if (!Number.isInteger(seq) || seq < 1) {
    throw new Error(`Ungültiger Outbox-Zählerwert in Meta: ${value}`);
  }
  return seq;
}

/**
 * Stabile gerätegenerierte Buchungs-ID: `bk-001-mj8x9k2l`
 * (`bk-` + seq 3-stellig + `-` + `nowMs` in Base-36).
 */
export function formatBookingId(seq: number, nowMs: number): string {
  return `bk-${String(seq).padStart(3, '0')}-${nowMs.toString(36)}`;
}

/**
 * Split-ID aus Buchungs-ID + Position (1-basiert): `bk-001-xyz-s1`.
 */
export function formatSplitId(bookingId: string, position: number): string {
  return `${bookingId}-s${position}`;
}
