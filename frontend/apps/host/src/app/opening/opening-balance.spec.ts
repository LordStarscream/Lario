import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Router } from '@angular/router';
import { afterEach, beforeEach, vi } from 'vitest';
import { OpeningBalance } from './opening-balance';
import { ApiError as ApiErrorClass } from '../../generated/core/ApiError';
import type { ApiRequestOptions } from '../../generated/core/ApiRequestOptions';
import type { ApiResult } from '../../generated/core/ApiResult';
import { CatalogService } from '../../api/catalogs';
import { JournalWriteEndpointsService } from '../../generated/services/JournalWriteEndpointsService';
import type { BookingCreatedDto } from '../../generated/models/BookingCreatedDto';

/**
 * Review R4 F1: Ausgang unklar (verlorene Antwort) + geänderte Daten
 * dürfen NICHT mit einer neuen Operation-Id gesendet werden — das
 * könnte eine zweite Buchung erzeugen, wenn die alte Operation
 * bereits committet ist.
 *
 * Ablauf-Tests: echte Komponentenklasse, nur die API-Ebene und die
 * Injections sind simuliert.
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

function createForm(): OpeningBalance {
  TestBed.resetTestingModule();
  TestBed.configureTestingModule({
    imports: [OpeningBalance],
    providers: [
      {
        provide: Router,
        useValue: { navigate: vi.fn().mockResolvedValue(true) },
      },
      {
        provide: CatalogService,
        useValue: {
          accounts: signal([]),
          pools: signal([]),
          load: vi.fn().mockResolvedValue(undefined),
        } as unknown as CatalogService,
      },
    ],
  });
  return TestBed.createComponent(OpeningBalance).componentInstance;
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

describe('OpeningBalance.submit — Operation-Id bei unklarem Ausgang (Review R4 F1)', () => {
  it('wiedersendet nach verlorener Antwort mit derselben Id (idempotent)', async () => {
    const create = vi.mocked(JournalWriteEndpointsService.createOpeningBalance);
    create.mockReset();
    create.mockRejectedValueOnce(new Error('Netzwerk ausgefallen'));

    const component = createForm();
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

    const component = createForm();
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

    const component = createForm();
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

    const component = createForm();
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

      const first = createForm();
      fillValidForm(first);
      void first.submit();
      await vi.waitFor(() => expect(first.busy()).toBe(true));
      expect(create).toHaveBeenCalledTimes(1);

      // Reload: neue Formularinstanz (selbes sessionStorage).
      const second = createForm();
      fillValidForm(second);
      expect(second.outcomeUncertain()).toBe(true);

      // Geänderte Daten: blockiert — eine neue Id könnte eine zweite
      // Buchung erzeugen.
      second.totalText = '150,00';
      await second.submit();

      expect(create).toHaveBeenCalledTimes(1);
      expect(second.error()?.code).toBe('uncertain_operation_blocked');

      // Unveränderte Daten: idempotente Wiedersendung mit gleicher Id.
      const third = createForm();
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
