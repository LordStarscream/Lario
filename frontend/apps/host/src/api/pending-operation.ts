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
 * - Geänderte Daten bei unklarem Ausgang (Review R4 F1): die Einreichung
 *   wird blockiert (nextId liefert null) — eine neue Id würde eine
 *   zweite Buchung erzeugen können, falls die alte Operation bereits
 *   committet ist. Die alte Operation muss erst abgeglichen werden
 *   (Buchungsliste), dann manuell {@link PendingOperation.release}.
 */
export class PendingOperation {
  private id: string | null = null;
  private fingerprint: string | null = null;

  /** true, solange der Ausgang des letzten Versuchs unklar ist. */
  readonly outcomeUncertain = signal(false);

  /**
   * Id für die nächste Einreichung:
   * - unveränderte Payload → dieselbe Id (idempotentes Wiedersenden);
   * - keine hängende Operation oder geänderte Payload nach definitivem
   *   Ausgang → neue Id;
   * - `null` (Einreichung blockieren, Review R4 F1): unklarer Ausgang
   *   (keine Antwort/5xx) **und** geänderte Payload — eine neue Id
   *   könnte eine zweite Buchung erzeugen (die alte Operation ist
   *   möglicherweise bereits committet). Erst abgleichen, dann manuell
   *   {@link PendingOperation.release}.
   */
  nextId(prefix: string, payloadFingerprint: string): string | null {
    if (
      this.outcomeUncertain() &&
      this.id !== null &&
      this.fingerprint !== payloadFingerprint
    ) {
      return null;
    }
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
