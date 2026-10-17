import { signal } from '@angular/core';
import {
  TestBed,
  type ComponentFixture,
} from '@angular/core/testing';
import { ActivatedRoute, Router } from '@angular/router';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { OpeningBalance } from './opening-balance';
import { ApiError as ApiErrorClass } from '../../generated/core/ApiError';
import type { ApiRequestOptions } from '../../generated/core/ApiRequestOptions';
import type { ApiResult } from '../../generated/core/ApiResult';
import { CatalogService } from '../../api/catalogs';
import { JournalEndpointsService } from '../../generated/services/JournalEndpointsService';
import { JournalWriteEndpointsService } from '../../generated/services/JournalWriteEndpointsService';
import type { BookingCreatedDto } from '../../generated/models/BookingCreatedDto';
import type { OpeningBalanceListItemDto } from '../../generated/models/OpeningBalanceListItemDto';

/**
 * Zwischenstep 7.5:
 * - Operation-Id bei unklarem Ausgang (Review R4 F1 / R6-1): echte
 *   Komponentenklasse, nur die API-Ebene und die Injections sind
 *   simuliert.
 * - Korrektur (A5): die Korrektur ist eine eigene Route
 *   `anfangsbestaende/:id/korrektur` — der Modus kommt aus der Route,
 *   dieselbe Komponente (Muster der Ausgaben-Korrektur).
 * - Liste (A3): vorhandene Anfangsbestände über dem Formular.
 */

beforeEach(() => {
  // Testisolierung: der PendingOperation-Zustand liegt pro Root-
  // Service-Instanz und in sessionStorage (Review R5-1/R6-1).
  sessionStorage.clear();
  // Standard: unerwartete API-Calls werden sichtbar abgelehnt;
  // jeder Test setzt das für seine Calls erwartete Verhalten.
  vi
    .spyOn(JournalWriteEndpointsService, 'createOpeningBalance')
    .mockRejectedValue(new Error('Unerwarteter API-Call: createOpeningBalance'));
  vi
    .spyOn(JournalEndpointsService, 'listOpeningBalances')
    .mockRejectedValue(new Error('Unerwarteter API-Call: listOpeningBalances'));
});

afterEach(() => {
  vi.restoreAllMocks();
});

function httpError(status: number, message = 'Serverfehler'): ApiErrorClass {
  const request = {
    method: 'POST',
    url: '/api/v1/journal/opening-balance',
  } as unknown as ApiRequestOptions;
  const response = {
    url: '/api/v1/journal/opening-balance',
    status,
    statusText: 'ERR',
    data: null,
  } as unknown as ApiResult;
  return new ApiErrorClass(request, response, message);
}

/**
 * Komponenten-Fixture erzeugen. `routeId` != null simuliert die
 * Korrektur-Route `anfangsbestaende/:id/korrektur`. Der Konstruktor
 * startet asynchron: Erstellungsmodus → Katalog + Liste, Korrekturmodus
 * → Vorbelegung (Katalog + Eintrag). `detectChanges` wird bewusst nicht
 * aufgerufen — die Ablauf-Tests prüfen die Komponente ohne Rendering
 * (gleiche Konvention wie die Ausgaben-Formular-Tests).
 */
function createFixture(
  routeId: string | null = null,
  catalogAccounts: unknown[] = [],
  catalogLoaded = false,
): ComponentFixture<OpeningBalance> {
  TestBed.resetTestingModule();
  TestBed.configureTestingModule({
    imports: [OpeningBalance],
    providers: [
      {
        provide: Router,
        useValue: { navigate: vi.fn().mockResolvedValue(true) },
      },
      {
        provide: ActivatedRoute,
        useValue: {
          snapshot: {
            paramMap: {
              get: (key: string) => (key === 'id' ? routeId : null),
            },
          },
        },
      },
      {
        provide: CatalogService,
        useValue: {
          accounts: signal(catalogAccounts),
          pools: signal([]),
          loaded: signal(catalogLoaded),
          load: vi.fn().mockResolvedValue(undefined),
        } as unknown as CatalogService,
      },
    ],
  });
  return TestBed.createComponent(OpeningBalance);
}

/** Korrektur-Vorbelegung abwarten (asynchron im Konstruktor gestartet). */
async function waitLoaded(fixture: ComponentFixture<OpeningBalance>): Promise<void> {
  await vi.waitFor(() => expect(fixture.componentInstance.loading()).toBe(false));
}

function fillValidForm(component: OpeningBalance): void {
  component.date = '2026-10-01';
  component.accountId = 'acc-1';
  component.totalText = '100,00';
  component.addRow();
  const row = component.distribution[0];
  row.poolId = 'pool-1';
  row.amountText = '40,00';
}

function created(id: string, idempotent = false): BookingCreatedDto {
  return { id, idempotent } as unknown as BookingCreatedDto;
}

function entry(
  overrides: Partial<OpeningBalanceListItemDto> = {},
): OpeningBalanceListItemDto {
  return {
    id: 'ob-1',
    date: '2026-01-01',
    accountId: 'acc-1',
    accountName: null,
    total: 10000,
    distribution: [{ poolId: 'pool-1', poolName: null, amount: 4000 }],
    note: 'alt',
    baseRevision: 1,
    ...overrides,
  };
}

describe('OpeningBalance.submit — Operation-Id bei unklarem Ausgang (Review R4 F1)', () => {
  it('wiedersendet nach verlorener Antwort mit derselben Id (idempotent)', async () => {
    const create = vi.mocked(JournalWriteEndpointsService.createOpeningBalance);
    create.mockReset();
    create.mockRejectedValueOnce(new Error('Netzwerk ausgefallen'));

    const component = createFixture().componentInstance;
    fillValidForm(component);

    await component.submit();
    expect(component.outcomeUncertain()).toBe(true);
    expect(create).toHaveBeenCalledTimes(1);

    create.mockResolvedValueOnce(created('b-1', true));
    await component.submit();

    expect(create).toHaveBeenCalledTimes(2);
    expect(create.mock.calls[1][0].id).toBe(create.mock.calls[0][0].id);
    expect(component.outcomeUncertain()).toBe(false);
  });

  it('blockt geänderte Daten bei unklarem Ausgang — keine zweite Buchung (F1)', async () => {
    const create = vi.mocked(JournalWriteEndpointsService.createOpeningBalance);
    create.mockReset();
    create.mockRejectedValueOnce(new Error('Netzwerk ausgefallen'));

    const component = createFixture().componentInstance;
    fillValidForm(component);

    await component.submit();
    expect(component.outcomeUncertain()).toBe(true);

    // Der Nutzer ändert die Daten (hier: Gesamtbetrag).
    component.totalText = '150,00';
    create.mockResolvedValue(created('b-2'));
    await component.submit();

    // Die geänderten Daten dürfen NICHT mit neuer Id gesendet werden —
    // sonst wäre eine zweite Buchung möglich, falls der erste Versuch
    // bereits committet ist.
    expect(create).toHaveBeenCalledTimes(1);
    expect(component.error()?.code).toBe('uncertain_operation_blocked');
  });

  it('erlaubt geänderte Daten erst nach manuellem Reset (neue Id)', async () => {
    const create = vi.mocked(JournalWriteEndpointsService.createOpeningBalance);
    create.mockReset();
    create.mockRejectedValueOnce(new Error('Netzwerk ausgefallen'));

    const component = createFixture().componentInstance;
    fillValidForm(component);

    await component.submit();
    component.totalText = '150,00';
    component.resetPendingOperation();
    create.mockResolvedValue(created('b-3'));
    await component.submit();

    expect(create).toHaveBeenCalledTimes(2);
    expect(create.mock.calls[1][0].id).not.toBe(create.mock.calls[0][0].id);
    expect(Number(create.mock.calls[1][0].total)).toBe(15000);
    expect(component.error()).toBeNull();
  });

  it('freigt nach definitiver 4xx-Ablehnung die Id (ohne Uncertainty)', async () => {
    const create = vi.mocked(JournalWriteEndpointsService.createOpeningBalance);
    create.mockReset();
    create.mockRejectedValueOnce(httpError(400, 'Validierung fehlgeschlagen'));

    const component = createFixture().componentInstance;
    fillValidForm(component);

    await component.submit();
    expect(component.outcomeUncertain()).toBe(false);

    component.totalText = '150,00';
    create.mockResolvedValue(created('b-4'));
    await component.submit();

    expect(create).toHaveBeenCalledTimes(2);
    expect(create.mock.calls[1][0].id).not.toBe(create.mock.calls[0][0].id);
  });

  describe('Review R6-1: In-flight Request zum Reload-Zeitpunkt', () => {
    it('stellt die Operation nach Reload als unklar wieder her — geänderte Daten erhalten keine neue Id', async () => {
      const create = vi.mocked(JournalWriteEndpointsService.createOpeningBalance);
      create.mockReset();
      // Gesendet, aber nie quittiert: Server-Commit möglich, Antwort
      // verloren — der Reload löst kein Catch im Formular aus.
      create.mockImplementationOnce(
        // nie quittierte Antwort (Reload); `as never` passt den Mock
        // an den generierten CancelablePromise-Rückgabewert an.
        () => new Promise<BookingCreatedDto>(() => undefined) as never,
      );

      const first = createFixture().componentInstance;
      fillValidForm(first);
      void first.submit();
      await vi.waitFor(() => expect(first.busy()).toBe(true));
      expect(create).toHaveBeenCalledTimes(1);

      // Reload: neue Formularinstanz (selbes sessionStorage).
      const second = createFixture().componentInstance;
      fillValidForm(second);
      expect(second.outcomeUncertain()).toBe(true);

      // Geänderte Daten: blockiert — eine neue Id könnte eine zweite
      // Buchung erzeugen.
      second.totalText = '150,00';
      await second.submit();

      expect(create).toHaveBeenCalledTimes(1);
      expect(second.error()?.code).toBe('uncertain_operation_blocked');

      // Unveränderte Daten: idempotente Wiedersendung mit gleicher Id.
      const third = createFixture().componentInstance;
      fillValidForm(third);
      const originalId = create.mock.calls[0][0].id;
      create.mockResolvedValueOnce(created('b-1', true));
      await third.submit();

      expect(create).toHaveBeenCalledTimes(2);
      expect(create.mock.calls[1][0].id).toBe(originalId);
      expect(third.error()).toBeNull();
    });
  });
});

describe('OpeningBalance — Liste vorhandener Anfangsbestände (Zwischenstep 7.5, A3)', () => {
  it('zeigt die vorhandenen Anfangsbestände (Datum, Konto, Betrag, Verteilung, Unverteilt)', async () => {
    vi.mocked(JournalEndpointsService.listOpeningBalances).mockResolvedValue([
      entry(),
      entry({ id: 'ob-2', date: '2026-02-01', accountId: 'acc-2', total: 5000, distribution: [], note: null }),
    ]);

    const fixture = createFixture();
    await vi.waitFor(() => expect(fixture.componentInstance.entries().length).toBe(2));

    const [first, second] = fixture.componentInstance.entries();
    expect(first).toMatchObject({
      id: 'ob-1',
      date: '2026-01-01',
      accountId: 'acc-1',
      total: 10000,
      note: 'alt',
    });
    expect(Number(second.total)).toBe(5000);
    // Unverteilt wird clientseitig berechnet (DTO trägt keine Summen):
    expect(fixture.componentInstance.unallocatedOf(first)).toBe(6000);
    expect(fixture.componentInstance.unallocatedOf(second)).toBe(5000);
  });

  it('zeigt bei leerer Liste einen Hinweis — das Formular bleibt nutzbar', async () => {
    vi.mocked(JournalEndpointsService.listOpeningBalances).mockResolvedValue([]);

    const fixture = createFixture();
    const component = fixture.componentInstance;
    await vi.waitFor(() => expect(component.loading()).toBe(false));

    expect(component.entries()).toEqual([]);
    // Formular bleibt unabhängig nutzbar:
    fillValidForm(component);
    expect(component.issue()).toBeNull();
  });

  it('blockiert bei Ladefehler der Liste nicht das Formular', async () => {
    // listOpeningBalances lehnt ab (vorher im beforeEach gesetzt).
    const fixture = createFixture();
    const component = fixture.componentInstance;
    await vi.waitFor(() => expect(component.loading()).toBe(false));

    expect(component.entries()).toEqual([]);

    fillValidForm(component);
    expect(component.issue()).toBeNull();
    const create = vi.mocked(JournalWriteEndpointsService.createOpeningBalance);
    create.mockResolvedValue(created('ob-3'));
    await component.submit();
    expect(create).toHaveBeenCalledTimes(1);
  });

  it('öffnet pro Eintrag die Korrektur-Route', async () => {
    vi.mocked(JournalEndpointsService.listOpeningBalances).mockResolvedValue([
      entry(),
    ]);

    const fixture = createFixture();
    // Der Router-Mock liegt in der Fixture (useValue) — nach dem
    // configureTestingModule injizieren.
    const router = TestBed.inject(Router) as unknown as {
      navigate: ReturnType<typeof vi.fn>;
    };
    await vi.waitFor(() => expect(fixture.componentInstance.entries().length).toBe(1));

    fixture.componentInstance.goCorrection('ob-1');
    // Absoluter Pfad (Commit 536d2f0: relative Navigation löst gegen die
    // Wurzel auf und würde die falsche Route treffen).
    expect(router.navigate).toHaveBeenCalledWith(
      ['/anfangsbestaende', 'ob-1', 'korrektur'],
    );
  });
});

describe('OpeningBalance — Korrektur per Route (Zwischenstep 7.5, A5)', () => {
  beforeEach(() => {
    vi
      .spyOn(JournalEndpointsService, 'listOpeningBalances')
      .mockResolvedValue([entry()]);
    vi
      .spyOn(JournalWriteEndpointsService, 'correctBooking')
      .mockResolvedValue(created('ob-1'));
  });

  it('belegt das Formular mit den vorhandenen Werten (Datum, Konto, Total, Verteilung, Notiz)', async () => {
    const fixture = createFixture('ob-1');
    await waitLoaded(fixture);
    const component = fixture.componentInstance;

    expect(component.isCorrection).toBe(true);
    expect(component.target()?.id).toBe('ob-1');
    expect(component.date).toBe('2026-01-01');
    expect(component.accountId).toBe('acc-1');
    expect(component.totalText).toBe('100,00');
    expect(component.note).toBe('alt');
    expect(component.distribution).toEqual([
      { key: expect.any(Number), poolId: 'pool-1', amountText: '40,00' },
    ]);
  });

  it('sendet eine geleerte Notiz als leeres String — explizite Löschung (Review R7-5)', async () => {
    const correct = vi.mocked(JournalWriteEndpointsService.correctBooking);
    correct.mockReset();
    correct.mockResolvedValueOnce(created('ob-1', true));

    const fixture = createFixture('ob-1');
    await waitLoaded(fixture);
    const component = fixture.componentInstance;

    // Vorbelegung trägt die Original-Notiz „alt“.
    expect(component.note).toBe('alt');
    component.note = '   ';

    await component.submit();

    expect(correct).toHaveBeenCalledTimes(1);
    // Explizite Löschung: "" — der Server setzt die Notiz auf null.
    expect(correct.mock.calls[0][1].note).toBe('');
  });

  it('sendet eine unveränderte Notiz als null — der Server hält das Original (Review R7-5)', async () => {
    const correct = vi.mocked(JournalWriteEndpointsService.correctBooking);
    correct.mockReset();
    correct.mockResolvedValueOnce(created('ob-1', true));

    const fixture = createFixture('ob-1');
    await waitLoaded(fixture);
    const component = fixture.componentInstance;

    // Nur den Betrag ändern; die Notiz bleibt wie vorgefüllt („alt“).
    component.totalText = '120,00';

    await component.submit();

    expect(correct).toHaveBeenCalledTimes(1);
    expect(Number(correct.mock.calls[0][1].totalAmount)).toBe(12000);
    expect(correct.mock.calls[0][1].note).toBeNull();
  });

  it('sendet geänderte Werte als Korrektur — unveränderte Felder bleiben null (Server hält Original)', async () => {
    const correct = vi.mocked(JournalWriteEndpointsService.correctBooking);
    const create = vi.mocked(JournalWriteEndpointsService.createOpeningBalance);
    create.mockReset();

    const fixture = createFixture('ob-1');
    await waitLoaded(fixture);
    const component = fixture.componentInstance;
    component.totalText = '120,00';
    component.distribution[0].amountText = '60,00';
    component.note = 'korrigiert';

    await component.submit();

    expect(correct).toHaveBeenCalledTimes(1);
    expect(correct.mock.calls[0][0]).toBe('ob-1');
    const body = correct.mock.calls[0][1];
    expect(body.expectedBaseRevision).toBe(1);
    // Geändert:
    expect(Number(body.totalAmount)).toBe(12000);
    expect(body.allocations).toEqual([{ poolId: 'pool-1', amount: 6000 }]);
    expect(body.note).toBe('korrigiert');
    // Unverändert → null (Server hält die Originalwerte):
    expect(body.date).toBeNull();
    expect(body.accountId).toBeNull();
    expect(create).not.toHaveBeenCalled();
    expect(component.error()).toBeNull();
    // Nach Erfolg zurück zur Liste:
    const router = TestBed.inject(Router) as unknown as {
      navigate: ReturnType<typeof vi.fn>;
    };
    expect(router.navigate).toHaveBeenCalledWith(['/anfangsbestaende']);
  });

  it('blockt das Absenden ohne Änderungen — nichts zu korrigieren', async () => {
    const correct = vi.mocked(JournalWriteEndpointsService.correctBooking);

    const fixture = createFixture('ob-1');
    await waitLoaded(fixture);
    const component = fixture.componentInstance;

    expect(component.correctionUnchanged()).toBe(true);
    await component.submit();

    expect(correct).not.toHaveBeenCalled();
    expect(component.issue()).toContain('Keine Änderungen');
  });

  it('wiedersendet nach verlorener Antwort dieselbe Operation-Id (idempotente Korrektur)', async () => {
    const correct = vi.mocked(JournalWriteEndpointsService.correctBooking);
    correct.mockReset();
    correct.mockRejectedValueOnce(new Error('Netzwerk ausgefallen'));

    const fixture = createFixture('ob-1');
    await waitLoaded(fixture);
    const component = fixture.componentInstance;
    component.totalText = '120,00';

    await component.submit();
    expect(correct).toHaveBeenCalledTimes(1);
    expect(component.outcomeUncertain()).toBe(true);

    correct.mockResolvedValueOnce(created('ob-1', true));
    await component.submit();

    expect(correct).toHaveBeenCalledTimes(2);
    expect(correct.mock.calls[1][1].operationId).toBe(
      correct.mock.calls[0][1].operationId,
    );
    expect(component.outcomeUncertain()).toBe(false);
  });

  it('hält bei Revisionskonflikt (409) den Fehler sichtbar und gibt den Slot frei', async () => {
    const correct = vi.mocked(JournalWriteEndpointsService.correctBooking);
    correct.mockReset();
    correct.mockRejectedValueOnce(
      httpError(409, 'Buchung wurde inzwischen geändert (Basisrevision 1 ≠ 2).'),
    );

    const fixture = createFixture('ob-1');
    await waitLoaded(fixture);
    const component = fixture.componentInstance;
    component.totalText = '120,00';

    await component.submit();
    expect(component.error()?.code).toBe('http_409');
    expect(component.outcomeUncertain()).toBe(false);

    // Definitive 4xx: der Slot ist frei — die nächste Einreichung erhält
    // eine neue Operation-Id.
    correct.mockResolvedValueOnce(created('ob-1'));
    await component.submit();
    expect(correct).toHaveBeenCalledTimes(2);
    expect(correct.mock.calls[1][1].operationId).not.toBe(
      correct.mock.calls[0][1].operationId,
    );
  });

  it('zeigt einen Fehler, wenn der Eintrag nicht gefunden wird', async () => {
    vi.mocked(JournalEndpointsService.listOpeningBalances).mockResolvedValue([]);

    const fixture = createFixture('ob-fehlt');
    await waitLoaded(fixture);
    const component = fixture.componentInstance;

    expect(component.target()).toBeNull();
    expect(component.loadError()).not.toBeNull();
    expect(component.loadError()?.message).toContain('nicht gefunden');
  });
});

/**
 * Review R7-3 (Einrichtungsvorgang): pro Konto genau ein Anfangsbestand —
 * die Neuanlage-Ansicht führt durch den Vorgang: bereits eingerichtete
 * Konte fallen aus der Auswahl heraus, wenn alle Konte eingerichtet sind,
 * wird statt des Formulars eine Zustandsanzeige gezeigt.
 */
describe('OpeningBalance — Einrichtungsvorgang (Review R7-3)', () => {
  const acc1 = { id: 'acc-1', name: 'Konto Eins', status: 'active' };
  const acc2 = { id: 'acc-2', name: 'Konto Zwei', status: 'active' };

  it('filtert bereits eingerichtete Konte aus der Neuanlage-Auswahl', async () => {
    vi.mocked(JournalEndpointsService.listOpeningBalances).mockResolvedValue([
      entry(), // acc-1 ist belegt
    ]);

    const fixture = createFixture(null, [acc1, acc2], true);
    await waitLoaded(fixture);
    const component = fixture.componentInstance;

    expect(component.availableAccounts().map((account) => account.id)).toEqual([
      'acc-2',
    ]);
    expect(component.allConfigured()).toBe(false);
  });

  it('zeigt die Zustandsanzeige, wenn alle aktiven Konten eingerichtet sind', async () => {
    vi.mocked(JournalEndpointsService.listOpeningBalances).mockResolvedValue([
      entry(),
      entry({ id: 'ob-2', accountId: 'acc-2', total: 8000, distribution: [] }),
    ]);

    const fixture = createFixture(null, [acc1, acc2], true);
    await waitLoaded(fixture);
    const component = fixture.componentInstance;

    expect(component.availableAccounts()).toHaveLength(0);
    expect(component.allConfigured()).toBe(true);
  });

  it('zeigt die Zustandsanzeige nicht, wenn der Katalog noch nicht geladen ist', async () => {
    vi.mocked(JournalEndpointsService.listOpeningBalances).mockResolvedValue([
      entry(),
      entry({ id: 'ob-2', accountId: 'acc-2', total: 8000, distribution: [] }),
    ]);

    const fixture = createFixture(null, [acc1, acc2], false);
    await waitLoaded(fixture);

    expect(fixture.componentInstance.allConfigured()).toBe(false);
  });
});
