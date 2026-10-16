import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, Router } from '@angular/router';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import { CatalogService } from '../../api/catalogs';
import type { BookingCreatedDto } from '../../generated/models/BookingCreatedDto';
import type { BookingDetailDto } from '../../generated/models/BookingDetailDto';
import type { CategoryDto } from '../../generated/models/CategoryDto';
import type { CorrectionCommand } from '../../generated/models/CorrectionCommand';
import { ApiError as ApiErrorClass } from '../../generated/core/ApiError';
import { JournalEndpointsService } from '../../generated/services/JournalEndpointsService';
import { JournalWriteEndpointsService } from '../../generated/services/JournalWriteEndpointsService';
import { ExpenseForm } from './expense-form';

/**
 * Review R4 (F1 Stable-Operation-Id, F4 Inherit, F5 Steuer-Notiz,
 * F6 Zeilen-Key): Ablauf-Tests mit echter Komponentenklasse; simuliert
 * werden nur die API-Statik und die Injections.
 */
const created = { id: 'b-1', idempotent: false } as unknown as BookingCreatedDto;

function httpError(
  status: number,
  message = 'Simulierter Netzwerkfehler',
): ApiErrorClass {
  const request = {} as never;
  const response = { status } as never;
  return new ApiErrorClass(request, response, message);
}

/**
 * Komponente ohne TestBed: eigene Injector-Umgebung für die
 * Injections, `createComponent` für den Injection-Kontext.
 * `withPreset`: Kategorie „cat-1“ mit steuerlicher Voreinstellung
 * (Review R4 F4).
 */
function createForm(
  routeId: string | null = null,
  withPreset = false,
): ExpenseForm {
  TestBed.resetTestingModule();
  TestBed.configureTestingModule({
    imports: [ExpenseForm],
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
          accounts: () => [],
          pools: () => [],
          categories: () =>
            (withPreset
              ? [
                  {
                    id: 'cat-1',
                    name: 'Lebensmittel',
                    parentId: null,
                    status: 'active',
                    baseRevision: 1,
                    taxPreset: {
                      taxRelevant: true,
                      taxCategoryId: 'tax-1',
                      taxCategoryName: 'Kasse 3',
                      inheritToChildren: true,
                      baseRevision: 1,
                    },
                  },
                ]
              : []
            ) as unknown as CategoryDto[],
          taxCategories: () => [],
          load: vi.fn().mockResolvedValue(undefined),
        },
      },
    ],
  });
  return TestBed.createComponent(ExpenseForm).componentInstance;
}

function fillValidForm(component: ExpenseForm): void {
  component.date = '2026-10-01';
  component.accountId = 'acc-1';
  component.totalText = '100,00';
  component.splits[0].amountText = '100,00';
  component.splits[0].poolId = 'pool-1';
  component.splits[0].categoryId = 'cat-1';
}

beforeEach(() => {
  // Standard: unerwartete API-Calls werden sichtbar abgelehnt;
  // jeder Test setzt das für seine Calls erwartete Verhalten.
  vi
    .spyOn(JournalWriteEndpointsService, 'createExpense')
    .mockRejectedValue(new Error('Unerwarteter API-Call: createExpense'));
  vi
    .spyOn(JournalWriteEndpointsService, 'correctBooking')
    .mockRejectedValue(new Error('Unerwarteter API-Call: correctBooking'));
  vi
    .spyOn(JournalEndpointsService, 'getBooking')
    .mockRejectedValue(new Error('Unerwarteter API-Call: getBooking'));
});

afterEach(() => {
  vi.restoreAllMocks();
});

describe('ExpenseForm (Review R4: F1 Stable-Operation-Id, F4 Inherit, F5 Steuer-Notiz, F6 Zeilen-Key)', () => {
  describe('F1: Stable-Operation-Id über verlorene Antworten', () => {
    it('sendet nach verlorener Antwort mit unveränderten Daten dieselbe Operation (idempotentes Wiedersenden)', async () => {
      const component = createForm();
      fillValidForm(component);
      vi.mocked(JournalWriteEndpointsService.createExpense)
        .mockRejectedValueOnce(httpError(0))
        .mockResolvedValueOnce(created);

      await component.submit();
      await component.submit();

      expect(component.outcomeUncertain()).toBe(false);
      expect(JournalWriteEndpointsService.createExpense).toHaveBeenCalledTimes(2);
      const calls = vi.mocked(JournalWriteEndpointsService.createExpense).mock
        .calls;
      expect(calls[1][0].id).toBe(calls[0][0].id);
      expect(calls[0][0].id).not.toBe('');
      expect(JSON.stringify(calls[1][0])).toBe(JSON.stringify(calls[0][0]));
    });

    it('blockiert geänderte Daten bei unklarem Ausgang, bis die Operation zurückgesetzt ist', async () => {
      const component = createForm();
      fillValidForm(component);
      vi.mocked(JournalWriteEndpointsService.createExpense).mockRejectedValue(
        httpError(0),
      );

      await component.submit();
      expect(component.outcomeUncertain()).toBe(true);

      // geänderte Daten bei unklarem Ausgang (konsistent: Summe bleibt exakt)
      component.totalText = '150,00';
      component.splits[0].amountText = '150,00';
      await component.submit();

      expect(JournalWriteEndpointsService.createExpense).toHaveBeenCalledTimes(
        1,
      );
      expect(component.outcomeUncertain()).toBe(true);
      expect(component.submitError()?.code).toBe('uncertain_operation_blocked');

      // Nach manuellem Zurücksetzen: neue Operation mit neuer Id erlaubt.
      component.resetPendingOperation();
      expect(component.outcomeUncertain()).toBe(false);
      vi.mocked(JournalWriteEndpointsService.createExpense).mockResolvedValue(
        created,
      );
      await component.submit();

      const calls = vi.mocked(JournalWriteEndpointsService.createExpense).mock
        .calls;
      expect(calls).toHaveLength(2);
      expect(calls[1][0].id).not.toBe(calls[0][0].id);
      expect(calls[1][0].id).not.toBe('');
    });

    it('setzt nach definitorischer 4xx-Ablehnung eine neue Operation-Id', async () => {
      const component = createForm();
      fillValidForm(component);
      vi.mocked(JournalWriteEndpointsService.createExpense)
        .mockRejectedValueOnce(httpError(400, 'Ungültiger Betrag'))
        .mockResolvedValueOnce(created);

      await component.submit();
      expect(component.outcomeUncertain()).toBe(false);

      component.totalText = '150,00';
      component.splits[0].amountText = '150,00';
      await component.submit();

      const calls = vi.mocked(JournalWriteEndpointsService.createExpense).mock
        .calls;
      expect(calls).toHaveLength(2);
      expect(calls[1][0].id).not.toBe(calls[0][0].id);
      expect(calls[1][0].id).not.toBe('');
    });

    it('verwendet bei der Korrektur eine Operation-Id mit Präfix „corr“', async () => {
      vi.mocked(JournalEndpointsService.getBooking).mockResolvedValue(
        detailWithTaxNote(),
      );
      vi.mocked(JournalWriteEndpointsService.correctBooking).mockResolvedValue(
        created,
      );
      const component = createForm('b-1');
      // Vorladen abgeschlossen (Detail prefilled, Formular gültig).
      await vi.waitFor(() => expect(component.loading()).toBe(false));
      expect(component.correctionId).toBe('b-1');
      expect(component.formIssue()).toBeNull();

      await component.submit();

      const [bookingId, command] = vi.mocked(
        JournalWriteEndpointsService.correctBooking,
      ).mock.calls[0] as [string, CorrectionCommand];
      // Path-Parameter ist die Originalbuchung; der Body trägt die
      // idempotente Korrektur-ID und die Splits.
      expect(bookingId).toBe('b-1');
      expect(String(command.operationId).startsWith('corr-')).toBe(true);
      // Aufteilung unverändert → Skalar-Pfad: Splits/Gesamtbetrag null
      // (Domain-Regel: Splits nur zusammen mit neuem Gesamtbetrag).
      expect(command.splits).toBeNull();
      expect(command.totalAmount).toBeNull();
    });
  });

  describe('F4: Rückkehr zu „Voreinstellung“ inaktiviert die explizite Erfassung', () => {
    it('leert Steuerkategorie und Teilmenge beim Wechsel zurück auf inherit', () => {
      const component = createForm();
      const row = component.splits[0];
      row.taxMode = 'relevant';
      row.taxCategoryId = 'tax-1';
      row.partialCapture = true;
      row.markedText = '10,00';

      component.onTaxModeChange(row, 'inherit');

      expect(row.taxCategoryId).toBe('');
      expect(row.partialCapture).toBe(false);
      expect(row.markedText).toBe('');
    });

    it('füllt bei „Steuerrelevant“ die Kategorie-Voreinstellung vor', () => {
      const component = createForm(null, true);
      const row = component.splits[0];
      row.categoryId = 'cat-1'; // Voreinstellung: steuerrelevant, „tax-1“
      row.taxMode = 'relevant';
      row.taxCategoryId = '';

      component.onTaxModeChange(row, 'relevant');

      expect(row.taxCategoryId).toBe('tax-1');
    });
  });

  describe('F5: Steuerkennzeichnungs-Notiz', () => {
    it('gibt die Notiz im relevanten Steuerblock der Payload aus', async () => {
      const component = createForm();
      fillValidForm(component);
      const row = component.splits[0];
      row.taxMode = 'relevant';
      row.taxCategoryId = 'tax-1';
      row.taxNote = '  Kasse 3  ';
      vi.mocked(JournalWriteEndpointsService.createExpense).mockResolvedValue(
        created,
      );

      await component.submit();

      const command = vi.mocked(JournalWriteEndpointsService.createExpense)
        .mock.calls[0][0];
      expect(command.splits[0].tax).toEqual({
        relevant: true,
        taxCategoryId: 'tax-1',
        markedAmount: null,
        note: 'Kasse 3',
      });
    });

    it('sendt null-Notiz, wenn das Feld leer bleibt', async () => {
      const component = createForm();
      fillValidForm(component);
      const row = component.splits[0];
      row.taxMode = 'relevant';
      row.taxCategoryId = 'tax-1';
      vi.mocked(JournalWriteEndpointsService.createExpense).mockResolvedValue(
        created,
      );

      await component.submit();

      const command = vi.mocked(JournalWriteEndpointsService.createExpense)
        .mock.calls[0][0];
      expect(command.splits[0].tax?.note).toBeNull();
    });

    it('validiert die maximale Notizlänge (500 Zeichen)', () => {
      const component = createForm();
      fillValidForm(component);
      const row = component.splits[0];
      row.taxMode = 'relevant';
      row.taxCategoryId = 'tax-1';
      row.taxNote = 'x'.repeat(501);

      expect(component.formIssue()).toContain('Steuer-Notiz zu lang');

      row.taxNote = 'x'.repeat(500);
      expect(component.formIssue()).toBeNull();
    });

    it('übernimmt die Notiz aus dem Detail beim Vorladen (Korrektur)', async () => {
      vi.mocked(JournalEndpointsService.getBooking).mockResolvedValueOnce(
        detailWithTaxNote(),
      );
      const component = createForm('b-1');

      await vi.waitFor(() => expect(component.loading()).toBe(false));
      expect(component.splits[0].taxMode).toBe('relevant');
      expect(component.splits[0].taxCategoryId).toBe('tax-1');
      expect(component.splits[0].partialCapture).toBe(true);
      expect(component.splits[0].markedText).toBe('30,00');
      expect(component.splits[0].taxNote).toBe('Kasse 3');
    });
  });

  describe('F6: eindeutige Split-Zeilen-Keys', () => {
    it('bleiben nach Löschen von Zeilen eindeutig', () => {
      const component = createForm();
      expect(component.splits).toHaveLength(1);

      component.addSplit(); // zweite Zeile
      expect(component.splits).toHaveLength(2);
      component.removeSplit(component.splits[0].key); // erste löschen
      expect(component.splits).toHaveLength(1);
      component.addSplit(); // erneut anhängen

      const keys = component.splits.map((row) => row.key);
      expect(new Set(keys).size).toBe(keys.length);
    });
  });
});

function detailWithTaxNote(): BookingDetailDto {
  return {
    id: 'b-1',
    type: 'expense',
    date: '2026-10-01',
    note: null,
    baseRevision: 5,
    effectivelyReversed: false,
    fundingMonth: null,
    reversesBookingId: null,
    reversalBookingIds: [],
    refundsExpenseId: null,
    relatedSettlementId: null,
    accountPostings: [
      { accountId: 'acc-1', accountName: 'Giro', amount: -10000 },
    ],
    poolPostings: [],
    splits: [
      {
        id: 's1',
        amount: 10000,
        poolId: 'pool-1',
        poolName: 'Flex',
        categoryId: 'cat-1',
        categoryName: 'Lebensmittel',
        note: null,
        tax: {
          relevant: true,
          taxCategoryId: 'tax-1',
          taxCategoryName: 'Kasse 3',
          markedAmount: 3000,
          note: 'Kasse 3',
        },
      },
    ],
    transferPurposes: [],
    reassignmentAccounts: [],
    totalAmount: 10000,
    refundedByPool: [],
  };
}
