import { signal, type Signal } from '@angular/core';

function randomId(): string {
  return typeof crypto !== 'undefined' && 'randomUUID' in crypto
    ? crypto.randomUUID()
    : Math.random().toString(36).slice(2);
}

/**
 * Stabile Wiederholungsidentität (Operation-Id) für Write-Endpunkte
 * (Idempotenz, Rechenvertrag §5.4) — Review R3 P1:
 *
 * - Die Id wird beim ersten Absenden erzeugt und bleibt so lange
 *   dieselbe, wie dieselbe Payload (Fingerprint) erneut eingereicht
 *   wird. Wichtig nach verlorener Antwort: der Server hat die
 *   Operation möglicherweise bereits committet — das erneute Senden
 *   mit gleicher Id und gleichem Inhalt wird idempotent quittiert
 *   (200), statt eine zweite Buchung zu erzeugen.
 * - Ein Transportfehler (keine Antwort) lässt die Id aktiv: der
 *   nächste Klick mit unveränderter Daten sendet dieselbe Operation.
 * - Nach einer definitiven Ablehnung (4xx — die Antwort kommt an und
 *   die Operation ist sicher nicht committet, bzw. die Id wird von
 *   einer anderen Buchung belegt) oder nach Erfolg wird die Id
 *   freigegeben; die nächste Einreichung erhält eine neue Id.
 * - Geändert die Daten nach unklarem Ausgang, erzeugt die nächste
 *   Einreichung eine neue Id (Fingerprint-Wechsel) — der Hinweis
 *   {@link PendingOperation.outcomeUncertain} warnt davor.
 */
export class PendingOperation {
  private id: string | null = null;
  private fingerprint: string | null = null;

  /** true, solange der Ausgang des letzten Versuchs unklar ist. */
  readonly outcomeUncertain = signal(false);

  /**
   * Id für die nächste Einreichung: wiederverwendet, wenn die Payload
   * unverändert ist; andernfalls (oder am Anfang) wird eine neue
   * erzeugt.
   */
  nextId(prefix: string, payloadFingerprint: string): string {
    if (this.id === null || this.fingerprint !== payloadFingerprint) {
      this.id = `${prefix}-${randomId()}`;
      this.fingerprint = payloadFingerprint;
    }
    return this.id;
  }

  /** Nach verlorener Antwort (Transportfehler/5xx): Ausgang unklar. */
  markOutcomeUncertain(): void {
    this.outcomeUncertain.set(true);
  }

  /**
   * Freigabe nach definitiver Ablehnung (4xx), Erfolg oder Reset:
   * die nächste Einreichung erhält eine neue Id.
   */
  release(): void {
    this.id = null;
    this.fingerprint = null;
    this.outcomeUncertain.set(false);
  }
}

export type { Signal };
