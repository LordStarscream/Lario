import {
  Injectable,
  signal,
  type Signal,
  type WritableSignal,
} from '@angular/core';

function randomId(): string {
  return typeof crypto !== 'undefined' && 'randomUUID' in crypto
    ? crypto.randomUUID()
    : Math.random().toString(36).slice(2);
}

/**
 * Gehaltene Operation (Review R5-1): Root-Service statt Komponenten-
 * instanz, damit die Idempotenz-Kontinuität über die Zerstörung der
 * Formular-Komponente besteht — insbesondere über den 401-Login-
 * Redirect, der die Komponente (und damit jede Komponenteninstanz)
 * verwirft. Zusätzlich persistiert in sessionStorage, damit auch ein
 * harter Reload die gehaltene Operation nicht verliert (konservativ:
 * im Zweifel bleibt die Operation gehalten und wird abgeglichen).
 *
 * In-flight (Review R6-1): Ein gesendeter Request ohne eingetroffene
 * Antwort wird als in-flight markiert und persistiert. Ein Reload in
 * diesem Fenster löst kein Catch aus; die Operation wird beim
 * Wiederherstellen konservativ als "Ausgang unklar" geladen — der
 * Server kann bereits committet haben, die Antwort ging verloren.
 */
interface HeldOperation {
  id: string;
  fingerprint: string;
  outcomeUncertain: boolean;
  /**
   * Request gesendet, Antwort noch nicht eingetroffen (Review R6-1).
   * Wird beim Reload konservativ in `outcomeUncertain` aufgelöst.
   */
  inFlight: boolean;
}

const STORAGE_KEY = 'lario.pendingOperations.v1';

/**
 * Stabile Wiederholungsidentität (Operation-Id) für Write-Endpunkte
 * (Idempotenz, Rechenvertrag §5.4) — Review R3 P1, R4 F1, R5-1:
 *
 * - Die Id wird beim ersten Absenden erzeugt und bleibt so lange
 *   dieselbe, wie dieselbe Payload (Fingerprint) erneut eingereicht
 *   wird. Wichtig nach verlorener Antwort: der Server hat die
 *   Operation möglicherweise bereits committet — das erneute Senden
 *   mit gleicher Id und gleichem Inhalt wird idempotent quittiert
 *   (200), statt eine zweite Buchung zu erzeugen.
 * - Ein Transportfehler (keine Antwort) lässt die Id aktiv: der
 *   nächste Klick mit unveränderter Daten sendet dieselbe Operation.
 * - Nach einer definitiven Ablehnung (4xx) oder nach Erfolg wird die
 *   Id freigegeben; die nächste Einreichung erhält eine neue Id.
 *   **Aber** (Review R5-1): eine 4xx-Antwort auf einen Versuch mit
 *   zuvor unklarem Ausgang darf die Operation nicht freigeben — der
 *   unklare Vorversuch kann bereits committet sein (z. B. Commit,
 *   dann Antwortverlust, dann 401 wegen abgelaufener Session). Die
 *   Operation bleibt gehalten; der nächste Klick mit unveränderter
 *   Payload sendet dieselbe Operation und erhält die idempotente
 *   Quittung.
 * - Geänderte Daten bei unklarem Ausgang (Review R4 F1): die
 *   Einreichung wird blockiert (nextId liefert null) — eine neue Id
 *   würde eine zweite Buchung erzeugen können, falls die alte
 *   Operation bereits committet ist. Die alte Operation muss erst
 *   abgeglichen werden (Buchungsliste), dann manuell
 *   {@link PendingOperation.release}.
 * - In-flight Request beim Reload (Review R6-1): die Operation wird
 *   konservativ als "Ausgang unklar" wiederhergestellt — der
 *   Server-Commit kann vor dem Reload erfolgt sein, die Antwort ist
 *   verloren. Geänderte Daten erhalten erst nach manuellem Reset
 *   (oder idempotenter Wiedersendung) eine neue Id.
 *
 * Pro Formular-Pfad (Präfix, z. B. „exp“/„corr“) gilt je ein Slot;
 * die Slots sind über Komponentengrenzen hinweg dieselben (Root-
 * Service) und überleben einen Reload (sessionStorage).
 */
@Injectable({ providedIn: 'root' })
export class PendingOperation {
  private readonly held = new Map<string, HeldOperation>();
  private readonly uncertainSignals = new Map<string, WritableSignal<boolean>>();

  constructor() {
    for (const [prefix, op] of this.loadFromStorage()) {
      this.held.set(prefix, op);
    }
  }

  /**
   * true, solange der Ausgang des letzten Versuchs für das Präfix
   * unklar ist (Signal überlebt Komponentengrenzen).
   */
  outcomeUncertain(prefix: string): Signal<boolean> {
    return this.writableUncertain(prefix);
  }

  private writableUncertain(prefix: string): WritableSignal<boolean> {
    let existing = this.uncertainSignals.get(prefix);
    if (existing === undefined) {
      existing = signal(this.held.get(prefix)?.outcomeUncertain ?? false);
      this.uncertainSignals.set(prefix, existing);
    }
    return existing;
  }

  /**
   * Id für die nächste Einreichung:
   * - unveränderte Payload → dieselbe Id (idempotentes Wiedersenden);
   *   der (Wieder-)Versuch wird wieder als in-flight markiert;
   * - keine gehaltene Operation oder geänderte Payload nach
   *   definitivem Ausgang → neue Id; der Request wird sofort als
   *   in-flight persistiert (Review R6-1):
   * - `null` (Einreichung blockieren, Review R4 F1): unklarer Ausgang
   *   (keine Antwort/5xx) **oder** in-flight + geänderte Payload —
   *   eine neue Id könnte eine zweite Buchung erzeugen (die alte
   *   Operation ist möglicherweise bereits committet). Erst abgleichen,
   *   dann manuell {@link PendingOperation.release}.
   */
  nextId(prefix: string, payloadFingerprint: string): string | null {
    const current = this.held.get(prefix);
    if (current !== undefined) {
      if (current.fingerprint === payloadFingerprint) {
        if (!current.inFlight) {
          current.inFlight = true;
          this.persist();
        }
        return current.id;
      }
      if (current.outcomeUncertain || current.inFlight) {
        return null;
      }
    }
    const next: HeldOperation = {
      id: `${prefix}-${randomId()}`,
      fingerprint: payloadFingerprint,
      outcomeUncertain: false,
      inFlight: true,
    };
    this.held.set(prefix, next);
    this.persist();
    return next.id;
  }

  /**
   * Nach verlorener Antwort (Transportfehler/5xx): Ausgang unklar —
   * die Operation bleibt mit ihrer Id gehalten (Review R5-1: erst ab
   * dem nächsten Versuch ist der Ausgang „wieder“ unklar, vorher ist
   * die Id unverändert aktiv).
   */
  markOutcomeUncertain(prefix: string): void {
    const current = this.held.get(prefix);
    if (current === undefined) {
      return;
    }
    current.outcomeUncertain = true;
    current.inFlight = false;
    this.persist();
    this.writableUncertain(prefix).set(true);
  }

  /**
   * Freigabe nach definitiver Ablehnung (4xx **ohne** vorherigen
   * unklaren Versuch), Erfolg oder manuellem Reset: die nächste
   * Einreichung erhält eine neue Id.
   */
  release(prefix: string): void {
    this.held.delete(prefix);
    this.persist();
    const signal = this.uncertainSignals.get(prefix);
    if (signal !== undefined) {
      signal.set(false);
    }
  }

  // ---------- sessionStorage-Persistenz (Review R5-1) ----------

  private persist(): void {
    try {
      const obj: Record<string, HeldOperation> = {};
      for (const [prefix, op] of this.held) {
        obj[prefix] = op;
      }
      sessionStorage.setItem(STORAGE_KEY, JSON.stringify(obj));
    } catch {
      // sessionStorage nicht verfügbar (z. B. blockierter Storage-
      // Modus): der Zustand bleibt in-memory — die Idempotenz bleibt
      // innerhalb der Session erhalten.
    }
  }

  private loadFromStorage(): [string, HeldOperation][] {
    try {
      const raw = sessionStorage.getItem(STORAGE_KEY);
      if (raw === null) {
        return [];
      }
      const parsed: unknown = JSON.parse(raw);
      if (typeof parsed !== 'object' || parsed === null || Array.isArray(parsed)) {
        return [];
      }
      const result: [string, HeldOperation][] = [];
      for (const [prefix, value] of Object.entries(
        parsed as Record<string, unknown>,
      )) {
        if (isHeldOperation(value)) {
          // Review R6-1: In-flight zum Reload-Zeitpunkt (Antwort
          // verloren, Server kann bereits committet haben) →
          // konservativ als "Ausgang unklar" wiederherstellen;
          // geänderte Daten erhalten dann keine neue Id.
          result.push([
            prefix,
            {
              id: value.id,
              fingerprint: value.fingerprint,
              outcomeUncertain: value.outcomeUncertain || value.inFlight,
              inFlight: false,
            },
          ]);
        }
      }
      return result;
    } catch {
      return [];
    }
  }
}

function isHeldOperation(value: unknown): value is HeldOperation {
  return (
    typeof value === 'object' &&
    value !== null &&
    typeof (value as HeldOperation).id === 'string' &&
    typeof (value as HeldOperation).fingerprint === 'string' &&
    typeof (value as HeldOperation).outcomeUncertain === 'boolean' &&
    ((value as HeldOperation).inFlight === undefined ||
      typeof (value as HeldOperation).inFlight === 'boolean')
  );
}
