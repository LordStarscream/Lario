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
  extraCategories: CategoryDto[] = [],
): ExpenseForm {
  TestBed.resetTestingModule();
  TestBed.configureTestingModule({
    imports: [ExpenseForm],
    providers: [
      {
        provide: Router,
        useValue: {
          navigate: vi.fn().mockResolvedValue(true),
          navigateByUrl: vi.fn().mockResolvedValue(true),
        },
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
            ([
              ...(withPreset
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
                : []),
              ...extraCategories,
            ] as unknown as CategoryDto[]),
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
  // Review R5-1: der PendingOperation-Zustand ist pro Root-Service-
  // Instanz und in sessionStorage persistiert — Testisolierung.
  sessionStorage.clear();
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

describe('ExpenseForm (Review R5: Idempotenz über 401, Preset-Wechsel, Notiz)', () => {
  describe('R5-1: Idempotenz-Zustand überlebt den 401-Login-Redirect', () => {
    it('gibt eine 4xx nach unklarem Versuch die Operation nicht frei', async () => {
      const component = createForm();
      fillValidForm(component);
      vi
        .mocked(JournalWriteEndpointsService.createExpense)
        .mockRejectedValueOnce(httpError(0)) // Antwort verloren → unklar
        .mockRejectedValueOnce(httpError(401)); // z. B. Session abgelaufen

      await component.submit();
      expect(component.outcomeUncertain()).toBe(true);
      await component.submit();
      // 4xx auf unklaren Versuch: Commit möglich → Operation bleibt
      // gehalten, statt eine neue Id zu erlauben.
      expect(component.outcomeUncertain()).toBe(true);

      // Wiederholung mit unveränderten Daten: dieselbe Operation.
      vi
        .mocked(JournalWriteEndpointsService.createExpense)
        .mockResolvedValueOnce(created);
      await component.submit();

      const calls = vi
        .mocked(JournalWriteEndpointsService.createExpense)
        .mock.calls;
      expect(calls).toHaveLength(3);
      expect(calls[2][0].id).toBe(calls[0][0].id);
      expect(JSON.stringify(calls[2][0])).toBe(JSON.stringify(calls[0][0]));
    });

    it('behält die Operation über eine neue Formularinstanz (Redirect-Szenario)', async () => {
      const first = createForm();
      fillValidForm(first);
      vi
        .mocked(JournalWriteEndpointsService.createExpense)
        .mockRejectedValueOnce(httpError(0)); // Commit möglich, Antwort weg
      await first.submit();
      expect(first.outcomeUncertain()).toBe(true);

      // Neue Instanz: 401 → Login → zurück zum Formular (die alte
      // Komponente ist zerstört, der Zustand muss erhalten sein).
      const second = createForm();
      fillValidForm(second);
      vi
        .mocked(JournalWriteEndpointsService.createExpense)
        .mockResolvedValueOnce(created);
      await second.submit();

      const calls = vi
        .mocked(JournalWriteEndpointsService.createExpense)
        .mock.calls;
      expect(calls).toHaveLength(2);
      // Dieselbe Operation: gleiche Id und exakt dieselbe Payload
      // (Split-Ids deterministisch aus dem Zeileninhalt).
      expect(calls[1][0].id).toBe(calls[0][0].id);
      expect(JSON.stringify(calls[1][0])).toBe(JSON.stringify(calls[0][0]));
    });

    it('setzt ohne vorher unklaren Versuch nach 4xx weiterhin eine neue Id', async () => {
      const component = createForm();
      fillValidForm(component);
      vi
        .mocked(JournalWriteEndpointsService.createExpense)
        .mockRejectedValueOnce(httpError(400, 'Ungültig'))
        .mockResolvedValueOnce(created);

      await component.submit();
      await component.submit();

      const calls = vi
        .mocked(JournalWriteEndpointsService.createExpense)
        .mock.calls;
      expect(calls[1][0].id).not.toBe(calls[0][0].id);
      expect(component.outcomeUncertain()).toBe(false);
    });
  });

  describe('R5-2: Kategorie-Wechsel belässt die Kennzeichnung nicht still', () => {
    it('materialisiert die neue Voreinstellung explizit („Wie Voreinstellung“)', async () => {
      vi
        .mocked(JournalEndpointsService.getBooking)
        .mockResolvedValueOnce(detailForPresetTest());
      vi
        .mocked(JournalWriteEndpointsService.correctBooking)
        .mockResolvedValue(created);
      const component = createForm('b-1', true, [noPresetCategory('cat-2')]);
      await vi.waitFor(() => expect(component.loading()).toBe(false));

      // Zeile A (s1): unmarkiert, „Wie Voreinstellung“; Kategorie auf
      // cat-1 (Preset: relevant, tax-1) wechseln und belassen.
      expect(component.splits[0].taxMode).toBe('inherit');
      component.splits[0].categoryId = 'cat-1';
      await component.submit();

      const [, command] = vi
        .mocked(JournalWriteEndpointsService.correctBooking)
        .mock.calls[0] as [string, CorrectionCommand];
      expect(command.splits).not.toBeNull();
      const s1 = command.splits!.find((s) => s.id === 's1')!;
      // A: Voreinstellung der neuen Kategorie explizit materialisiert.
      expect(s1.tax).toEqual({
        relevant: true,
        taxCategoryId: 'tax-1',
        markedAmount: null,
        note: null,
      });
      const s2 = command.splits!.find((s) => s.id === 's2')!;
      // B: unverändert (gleiche Werte wie original).
      expect(s2.tax).toEqual({
        relevant: true,
        taxCategoryId: 'tax-1',
        markedAmount: null,
        note: null,
      });
    });

    it('sendet ohne Voreinstellung explizit „nicht relevant“', async () => {
      vi
        .mocked(JournalEndpointsService.getBooking)
        .mockResolvedValueOnce(detailForPresetTest());
      vi
        .mocked(JournalWriteEndpointsService.correctBooking)
        .mockResolvedValue(created);
      const component = createForm('b-1', true, [noPresetCategory('cat-2')]);
      await vi.waitFor(() => expect(component.loading()).toBe(false));

      // Zeile A: Kategorie auf cat-2 (ohne Preset) wechseln.
      component.splits[0].categoryId = 'cat-2';
      await component.submit();

      const [, command] = vi
        .mocked(JournalWriteEndpointsService.correctBooking)
        .mock.calls[0] as [string, CorrectionCommand];
      const s1 = command.splits!.find((s) => s.id === 's1')!;
      expect(s1.tax).toEqual({
        relevant: false,
        taxCategoryId: null,
        markedAmount: null,
        note: null,
      });
    });
  });

  describe('R5-3: Steuer-Notiz bleibt bei „nicht relevant“ erhalten', () => {
    it('sendet die Notiz mit der expliziten Kennzeichnung (Korrektur)', async () => {
      vi
        .mocked(JournalEndpointsService.getBooking)
        .mockResolvedValueOnce(detailWithIrrelevantNote());
      vi
        .mocked(JournalWriteEndpointsService.correctBooking)
        .mockResolvedValue(created);
      const component = createForm('b-1');
      await vi.waitFor(() => expect(component.loading()).toBe(false));

      expect(component.splits[0].taxMode).toBe('irrelevant');
      expect(component.splits[0].taxNote).toBe('Bewusst privat');
      // Aufteilung ändern, damit die Splits explizit gesendet werden
      // (Skalar-Pfad würde die Original-Splits halten).
      component.totalText = '80,00';
      component.splits[0].amountText = '80,00';
      await component.submit();

      const [, command] = vi
        .mocked(JournalWriteEndpointsService.correctBooking)
        .mock.calls[0] as [string, CorrectionCommand];
      expect(command.splits).not.toBeNull();
      expect(command.splits![0].tax).toEqual({
        relevant: false,
        taxCategoryId: null,
        markedAmount: null,
        note: 'Bewusst privat',
      });
    });

    it('sendet die Notiz bei Neuanlage mit „nicht relevant“', async () => {
      const component = createForm();
      fillValidForm(component);
      const row = component.splits[0];
      row.taxMode = 'irrelevant';
      row.taxNote = 'Privat';
      vi
        .mocked(JournalWriteEndpointsService.createExpense)
        .mockResolvedValue(created);

      await component.submit();

      const command = vi
        .mocked(JournalWriteEndpointsService.createExpense)
        .mock.calls[0][0];
      expect(command.splits[0].tax).toEqual({
        relevant: false,
        taxCategoryId: null,
        markedAmount: null,
        note: 'Privat',
      });
    });
  });
});

describe('ExpenseForm (Review R6-2: unberührte unmarkierte Splits bleiben unmarkiert)', () => {
  it('sendet tax: null für einen unberührten unmarkierten Split, wenn ein anderer Split geändert wird', async () => {
    // cat-0 hat inzwischen ein relevantes Preset (tax-0); s1 war
    // dennoch unmarkiert. Nur die Notiz von s2 ändert sich →
    // Ersatz-Pfad; s1 darf nicht still mit dem Preset umgeschrieben
    // werden (Review R6-2).
    vi
      .mocked(JournalEndpointsService.getBooking)
      .mockResolvedValueOnce(detailUntouchedSplit());
    vi
      .mocked(JournalWriteEndpointsService.correctBooking)
      .mockResolvedValue(created);
    const component = createForm('b-1', false, [
      presetCategory('cat-0', 'tax-0'),
      presetCategory('cat-1', 'tax-1'),
    ]);
    await vi.waitFor(() => expect(component.loading()).toBe(false));

    // Nur die gewöhnliche Notiz von s2 ändern.
    component.splits[1].note = 'Neue Notiz';
    await component.submit();

    const [, command] = vi
      .mocked(JournalWriteEndpointsService.correctBooking)
      .mock.calls[0] as [string, CorrectionCommand];
    expect(command.splits).not.toBeNull();
    const s1 = command.splits!.find((s) => s.id === 's1')!;
    // Unmarkiert und unberührt → Unmarkiert-Zustand behalten.
    expect(s1.tax).toBeNull();
    const s2 = command.splits!.find((s) => s.id === 's2')!;
    expect(s2.note).toBe('Neue Notiz');
    // Die explizite Kennzeichnung von s2 bleibt unverändert.
    expect(s2.tax).toEqual({
      relevant: true,
      taxCategoryId: 'tax-1',
      markedAmount: null,
      note: null,
    });
  });

  it('materialisiert das aktuelle Preset bei bewusstem Wechsel auf „Wie Voreinstellung“', async () => {
    // Explizit markierte Zeile (relevant, tax-OLD) → bewusst „Wie
    // Voreinstellung“ (aktuelles Preset: tax-1) → das aktuelle
    // Preset wird materialisiert, nicht tax: null (Review R5-2).
    vi
      .mocked(JournalEndpointsService.getBooking)
      .mockResolvedValueOnce(detailExplicitOldTax());
    vi
      .mocked(JournalWriteEndpointsService.correctBooking)
      .mockResolvedValue(created);
    const component = createForm('b-1', true);
    await vi.waitFor(() => expect(component.loading()).toBe(false));
    expect(component.splits[0].taxMode).toBe('relevant');
    expect(component.splits[0].taxCategoryId).toBe('tax-OLD');

    component.onTaxModeChange(component.splits[0], 'inherit');
    await component.submit();

    const [, command] = vi
      .mocked(JournalWriteEndpointsService.correctBooking)
      .mock.calls[0] as [string, CorrectionCommand];
    expect(command.splits).not.toBeNull();
    const s1 = command.splits!.find((s) => s.id === 's1')!;
    expect(s1.tax).toEqual({
      relevant: true,
      taxCategoryId: 'tax-1',
      markedAmount: null,
      note: null,
    });
  });

  it('materialisiert das Preset nach bewusstem Hin- und Rückwechsel (unmarkierte Zeile)', async () => {
    // Unmarkierte Zeile (cat-0, Preset tax-0): „steuerrelevant“ wählen
    // und wieder auf „Wie Voreinstellung“ zurück — das ist eine
    // bewusste Neu-Anwendung des Presets, kein „unberührt“: die
    // Korrektur muss den Ersatz-Pfad nehmen und das Preset explizit
    // senden, statt tax: null (Review R6-2).
    vi
      .mocked(JournalEndpointsService.getBooking)
      .mockResolvedValueOnce(detailUntouchedSplit());
    vi
      .mocked(JournalWriteEndpointsService.correctBooking)
      .mockResolvedValue(created);
    const component = createForm('b-1', false, [
      presetCategory('cat-0', 'tax-0'),
      presetCategory('cat-1', 'tax-1'),
    ]);
    await vi.waitFor(() => expect(component.loading()).toBe(false));

    const row = component.splits[0];
    component.onTaxModeChange(row, 'relevant'); // füllt tax-0 vor
    expect(row.taxCategoryId).toBe('tax-0');
    component.onTaxModeChange(row, 'inherit');
    await component.submit();

    const [, command] = vi
      .mocked(JournalWriteEndpointsService.correctBooking)
      .mock.calls[0] as [string, CorrectionCommand];
    expect(command.splits).not.toBeNull();
    const s1 = command.splits!.find((s) => s.id === 's1')!;
    expect(s1.tax).toEqual({
      relevant: true,
      taxCategoryId: 'tax-0',
      markedAmount: null,
      note: null,
    });
  });
});

function presetCategory(id: string, taxCategoryId: string): CategoryDto {
  return {
    id,
    name: `Kategorie ${id}`,
    parentId: null,
    status: 'active',
    baseRevision: 1,
    taxPreset: {
      taxRelevant: true,
      taxCategoryId,
      taxCategoryName: `Steuerkategorie ${taxCategoryId}`,
      inheritToChildren: true,
      baseRevision: 1,
    },
  } as unknown as CategoryDto;
}

/**
 * R6-2: s1 unmarkiert (tax: null) in cat-0 (Preset tax-0), s2
 * explizit relevant (tax-1) — beide Splits bleiben unberührt außer
 * wo der Test etwas ändert.
 */
function detailUntouchedSplit(): BookingDetailDto {
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
        amount: 6000,
        poolId: 'pool-1',
        poolName: 'Flex',
        categoryId: 'cat-0',
        categoryName: 'Mit Preset',
        note: null,
        tax: null,
      },
      {
        id: 's2',
        amount: 4000,
        poolId: 'pool-2',
        poolName: 'Haushalt',
        categoryId: 'cat-1',
        categoryName: 'Lebensmittel',
        note: null,
        tax: {
          relevant: true,
          taxCategoryId: 'tax-1',
          taxCategoryName: 'Kasse 3',
          markedAmount: null,
          note: null,
        },
      },
    ],
    transferPurposes: [],
    reassignmentAccounts: [],
    totalAmount: 10000,
    refundedByPool: [],
  };
}

/** R6-2: s1 explizit relevant mit veralteter Steuerkategorie (tax-OLD). */
function detailExplicitOldTax(): BookingDetailDto {
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
          taxCategoryId: 'tax-OLD',
          taxCategoryName: 'Alt',
          markedAmount: null,
          note: null,
        },
      },
    ],
    transferPurposes: [],
    reassignmentAccounts: [],
    totalAmount: 10000,
    refundedByPool: [],
  };
}

function noPresetCategory(id: string): CategoryDto {
  return {
    id,
    name: 'Ohne Preset',
    parentId: null,
    status: 'active',
    baseRevision: 1,
    taxPreset: null,
  } as unknown as CategoryDto;
}

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

/** R5-2: zwei Splits — s1 unmarkiert (tax: null), s2 relevant markiert. */
function detailForPresetTest(): BookingDetailDto {
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
        amount: 6000,
        poolId: 'pool-1',
        poolName: 'Flex',
        categoryId: 'cat-0',
        categoryName: 'Ohne Preset',
        note: null,
        tax: null,
      },
      {
        id: 's2',
        amount: 4000,
        poolId: 'pool-2',
        poolName: 'Haushalt',
        categoryId: 'cat-1',
        categoryName: 'Lebensmittel',
        note: null,
        tax: {
          relevant: true,
          taxCategoryId: 'tax-1',
          taxCategoryName: 'Kasse 3',
          markedAmount: null,
          note: null,
        },
      },
    ],
    transferPurposes: [],
    reassignmentAccounts: [],
    totalAmount: 10000,
    refundedByPool: [],
  };
}

/** R5-3: Split mit expliziter „nicht relevant“-Kennzeichnung + Notiz. */
function detailWithIrrelevantNote(): BookingDetailDto {
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
          relevant: false,
          taxCategoryId: null,
          taxCategoryName: null,
          markedAmount: null,
          note: 'Bewusst privat',
        },
      },
    ],
    transferPurposes: [],
    reassignmentAccounts: [],
    totalAmount: 10000,
    refundedByPool: [],
  };
}
