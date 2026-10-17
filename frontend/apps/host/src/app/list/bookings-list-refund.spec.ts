import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Router } from '@angular/router';
import { afterEach, beforeEach, vi } from 'vitest';
import { BookingsList } from './bookings-list';
import { CatalogService } from '../../api/catalogs';
import { JournalWriteEndpointsService } from '../../generated/services/JournalWriteEndpointsService';
import { JournalEndpointsService } from '../../generated/services/JournalEndpointsService';
import type { BookingCreatedDto } from '../../generated/models/BookingCreatedDto';
import type { BookingDetailDto } from '../../generated/models/BookingDetailDto';
import type { BookingListItemDto } from '../../generated/models/BookingListItemDto';

/**
 * Review R4 F3: Eine Zeile leer lassen (0/leer = nicht ausgewählt)
 * ermöglicht Teil-Erstattungen; alle Zeilen leer bleibt abgelehnt;
 * ungültige/überschrittene Einträge bleiben abgelehnt.
 *
 * Ablauf-Tests: echte Komponentenklasse, nur die API-Ebene und die
 * Injections sind simuliert.
 */
beforeEach(() => {
  // Standard: unerwartete API-Calls werden sichtbar abgelehnt;
  // jeder Test setzt das für seine Calls erwartete Verhalten.
  vi.spyOn(JournalEndpointsService, 'listBookings').mockResolvedValue(
    [] as never,
  );
  vi
    .spyOn(JournalWriteEndpointsService, 'createRefund')
    .mockRejectedValue(new Error('Unerwarteter API-Call: createRefund'));
});

afterEach(() => {
  vi.restoreAllMocks();
});

function listItem(bookingId: string): BookingListItemDto {
  return {
    id: bookingId,
    type: 'Expense',
    date: '2026-10-01',
    note: null,
    totalAmount: 8000,
    baseRevision: 1,
    effectivelyReversed: false,
    accountName: 'Giro',
    splits: [],
    taxMarked: false,
  } as unknown as BookingListItemDto;
}

function detail(): BookingDetailDto {
  return {
    id: 'b1',
    type: 'Expense',
    date: '2026-10-01',
    note: null,
    baseRevision: 1,
    effectivelyReversed: false,
    accountId: 'acc-1',
    accountName: 'Giro',
    totalAmount: 8000,
    splits: [
      {
        id: 's1',
        poolId: 'flex',
        poolName: 'Flex',
        amount: 5000,
        category: { id: 'cat-1', name: 'Lebensmittel' },
        tax: null,
      },
      {
        id: 's2',
        poolId: 'haushalt',
        poolName: 'Haushalt',
        amount: 3000,
        category: { id: 'cat-2', name: 'Sonstiges' },
        tax: null,
      },
    ],
  } as unknown as BookingDetailDto;
}

function openDialog(lines: { amountText: string }[]): BookingsList {
  const component = createComponent();
  component.refund.set({
    booking: listItem('b1'),
    operationId: 'refund-1',
    loading: false,
    busy: false,
    detail: detail(),
    accountId: 'acc-1',
    date: '2026-10-05',
    lines: lines.map((line, index) => ({
      poolId: index === 0 ? 'flex' : 'haushalt',
      poolName: index === 0 ? 'Flex' : 'Haushalt',
      maxCents: index === 0 ? 5000 : 3000,
      refundedCents: 0,
      amountText: line.amountText,
    })),
    fullyRefunded: false,
    error: null,
  });
  return component;
}

function catalogStub(): CatalogService {
  return {
    accounts: signal([]),
    pools: signal([]),
    allAccounts: signal([]),
    allPools: signal([]),
    allCategories: signal([]),
    allTaxCategories: signal([]),
    categories: signal([]),
    taxCategories: signal([]),
    loading: signal(false),
    load: vi.fn().mockResolvedValue(undefined),
    refresh: vi.fn().mockResolvedValue(undefined),
  } as unknown as CatalogService;
}

function createComponent(): BookingsList {
  TestBed.resetTestingModule();
  TestBed.configureTestingModule({
    imports: [BookingsList],
    providers: [
      {
        provide: Router,
        useValue: { navigateByUrl: vi.fn().mockResolvedValue(true) },
      },
      { provide: CatalogService, useValue: catalogStub() },
    ],
  });
  return TestBed.createComponent(BookingsList).componentInstance;
}

describe('BookingsList Erstattungsdialog (Review R4 F3)', () => {
  it('Teil-Erstattung: eine Zeile leer lassen ist gültig, nur positive Zeilen werden gesendet', async () => {
    const createRefund = vi.mocked(JournalWriteEndpointsService.createRefund);
    createRefund.mockReset();
    createRefund.mockResolvedValue({ id: 'b2', idempotent: false } as unknown as BookingCreatedDto);

    const component = openDialog([
      { amountText: '20,00' },
      { amountText: '' },
    ]);

    expect(component.refundValid()).toBe(true);
    await component.submitRefund();

    expect(createRefund).toHaveBeenCalledTimes(1);
    const [bookingId, command] = createRefund.mock.calls[0];
    expect(bookingId).toBe('b1');
    expect(command.lines).toEqual([{ poolId: 'flex', amount: 2000 }]);
    expect(component.refund()).toBeNull(); // Dialog nach Erfolg geschlossen
  });

  it('alle Zeilen leer (oder 0) bleibt abgelehnt — keine leer-Zeilen an den Server', async () => {
    const createRefund = vi.mocked(JournalWriteEndpointsService.createRefund);
    createRefund.mockReset();
    createRefund.mockResolvedValue({ id: 'b2', idempotent: false } as unknown as BookingCreatedDto);

    const component = openDialog([
      { amountText: '' },
      { amountText: '0,00' },
    ]);

    expect(component.refundValid()).toBe(false);
    await component.submitRefund();
    expect(createRefund).not.toHaveBeenCalled();
  });

  it('Überschreitung des Deckels bleibt abgelehnt', async () => {
    const createRefund = vi.mocked(JournalWriteEndpointsService.createRefund);
    createRefund.mockReset();

    const component = openDialog([
      { amountText: '50,01' }, // maxCents = 5000
      { amountText: '' },
    ]);

    expect(component.refundValid()).toBe(false);
    await component.submitRefund();
    expect(createRefund).not.toHaveBeenCalled();
  });

  it('ungültiger nicht-leerer Eintrag bleibt abgelehnt', async () => {
    const createRefund = vi.mocked(JournalWriteEndpointsService.createRefund);
    createRefund.mockReset();

    const component = openDialog([
      { amountText: 'abc' },
      { amountText: '' },
    ]);

    expect(component.refundValid()).toBe(false);
    await component.submitRefund();
    expect(createRefund).not.toHaveBeenCalled();
  });
});
