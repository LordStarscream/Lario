import { beforeEach, describe, expect, it } from 'vitest';
import { PendingOperation } from './pending-operation';

/**
 * Review R6-1: In-flight Request zum Reload-Zeitpunkt (Server-Commit
 * möglich, Antwort verloren, kein Catch) → die wiederhergestellte
 * Operation muss konservativ als „Ausgang unklar“ gelten:
 * - unveränderte Daten → dieselbe Id (idempotente Wiedersendung);
 * - geänderte Daten → blockiert (keine neue Id — doppelte Buchung).
 *
 * Reload = neue Service-Instanz über demselben sessionStorage.
 */
describe('PendingOperation (Review R6-1: In-Flight-Persistenz über Reload)', () => {
  beforeEach(() => {
    sessionStorage.clear();
  });

  it('restellt in-flight nach Reload als unklar wieder und blockt neue Ids für geänderte Daten', () => {
    // Versuch 1: nextId markiert den Request als in-flight; danach
    // Reload ohne Antwort (kein Catch, keine Quittung).
    const first = new PendingOperation();
    const id = first.nextId('exp', 'payload-1');
    expect(id).not.toBeNull();

    // Reload: neue Instanz (selbes sessionStorage).
    const second = new PendingOperation();
    expect(second.outcomeUncertain('exp')()).toBe(true);

    // Unveränderte Daten: dieselbe Id (idempotente Wiedersendung).
    expect(second.nextId('exp', 'payload-1')).toBe(id);
    // Geänderte Daten: blockiert — eine neue Id könnte eine zweite
    // Buchung erzeugen, wenn der Server bereits committet hat.
    expect(second.nextId('exp', 'payload-2')).toBeNull();
    expect(second.outcomeUncertain('exp')()).toBe(true);

    // Manuelles Reset: geänderte Daten dürfen dann mit neuer Id.
    second.release('exp');
    expect(second.outcomeUncertain('exp')()).toBe(false);
    const fresh = second.nextId('exp', 'payload-2');
    expect(fresh).not.toBeNull();
    expect(fresh).not.toBe(id);
  });

  it('markiert die idempotente Wiedersendung erneut als in-flight (weitere Änderung bleibt blockiert)', () => {
    const first = new PendingOperation();
    const id = first.nextId('corr', 'payload-1');
    const second = new PendingOperation();
    const resent = second.nextId('corr', 'payload-1');
    expect(resent).toBe(id);
    // Wiedersendung ist in-flight — eine geänderte Payload bleibt
    // blockiert, bis sie quittiert oder zurückgesetzt wird.
    expect(second.nextId('corr', 'payload-2')).toBeNull();
  });

  it('ändert das 4xx-/Erfolgs-Verhalten nicht (Review R5-1-Regressionsschutz)', () => {
    // Definitive Ablehnung ohne vorher unklaren Versuch: die Id wird
    // freigegeben, der nächste Versuch (auch mit anderen Daten) erhält
    // eine neue Id.
    const service = new PendingOperation();
    const id = service.nextId('exp', 'payload-1');
    service.release('exp');
    const next = service.nextId('exp', 'payload-2');
    expect(next).not.toBeNull();
    expect(next).not.toBe(id);

    // Erfolg: Freigabe, neuer Versuch neue Id.
    const id2 = service.nextId('exp', 'payload-3');
    service.release('exp');
    const id3 = service.nextId('exp', 'payload-3');
    expect(id3).not.toBe(id2);
  });

  it('restellt eine freigegebene Operation nicht wieder her', () => {
    const service = new PendingOperation();
    service.nextId('exp', 'payload-1');
    service.release('exp');
    const second = new PendingOperation();
    expect(second.outcomeUncertain('exp')()).toBe(false);
    const id = second.nextId('exp', 'payload-2');
    expect(id).not.toBeNull();
  });
});
