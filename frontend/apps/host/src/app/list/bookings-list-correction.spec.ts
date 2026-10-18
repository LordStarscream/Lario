import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Router } from '@angular/router';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { BookingsList } from './bookings-list';
import { CatalogService } from '../../api/catalogs';
import { JournalEndpointsService } from '../../generated/services/JournalEndpointsService';
import type { BookingListItemDto } from '../../generated/models/BookingListItemDto';

/**
 * 7.5, offener Punkt: Ein Anfangsbestand in der Buchungsliste öffnet
 * das Anfangsbestände-Korrekturformular (Verteilung auf Pools, in-place)
 * — nicht die Ausgaben-Korrektur. Den Korrektur-Button erhalten nur Typen
 * mit vorhandenem Korrekturformular (Ausgabe, Anfangsbestand). Einnahmen
 * sind in der Domain zwar korrigierbar, haben aber kein
 * Einnahmenformular in der UI — sie ändern über Storno + Neuausgabe
 * (Restfehler, PR-Review 18.10.).
 *
 * Ablauf-Test: echte Komponentenklasse, nur die API-Ebene und die
 * Injections sind simuliert.
 */
beforeEach(() => {
  vi.spyOn(JournalEndpointsService, 'listBookings').mockResolvedValue(
    [] as never,
  );
});

afterEach(() => {
  vi.restoreAllMocks();
});

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
    loaded: signal(true),
    load: vi.fn().mockResolvedValue(undefined),
    refresh: vi.fn().mockResolvedValue(undefined),
  } as unknown as CatalogService;
}

function item(
  id: string,
  type: BookingListItemDto['type'],
): BookingListItemDto {
  return {
    id,
    type,
    date: '2026-10-01',
    note: null,
    totalAmount: 10000,
    baseRevision: 1,
    effectivelyReversed: false,
    accountName: 'Giro',
    splits: [],
    taxMarked: false,
  } as unknown as BookingListItemDto;
}

function createFixture() {
  TestBed.configureTestingModule({
    imports: [BookingsList],
    providers: [
      { provide: Router, useValue: { navigate: vi.fn().mockResolvedValue(true) } },
      { provide: CatalogService, useValue: catalogStub() },
    ],
  });
  const component = TestBed.createComponent(BookingsList).componentInstance;
  const router = TestBed.inject(Router) as unknown as {
    navigate: ReturnType<typeof vi.fn>;
  };
  return { component, router };
}

describe('BookingsList — typgerechte Korrektur-Route (offener Punkt 7.5)', () => {
  it('öffnet für einen Anfangsbestand die Anfangsbestände-Korrektur', () => {
    const { component, router } = createFixture();

    component.goCorrection(item('ob-1', 'OpeningBalance'));

    expect(router.navigate).toHaveBeenCalledWith([
      '/anfangsbestaende',
      'ob-1',
      'korrektur',
    ]);
  });

  it('öffnet für Ausgaben die Ausgaben-Korrektur', () => {
    const { component, router } = createFixture();

    component.goCorrection(item('b-1', 'Expense'));

    expect(router.navigate).toHaveBeenCalledWith([
      '/ausgaben',
      'b-1',
      'korrektur',
    ]);
  });

  it('markiert nur Ausgaben und Anfangsbestände als korrigierbar', () => {
    const { component } = createFixture();

    expect(component.isCorrectable(item('b-1', 'Expense'))).toBe(true);
    // Restfehler: Einnahmen haben kein Korrekturformular in der UI —
    // der Button würde ins ungeeignete Ausgabenformular führen.
    expect(component.isCorrectable(item('e-1', 'Income'))).toBe(false);
    expect(component.isCorrectable(item('ob-1', 'OpeningBalance'))).toBe(true);
    expect(component.isCorrectable(item('t-1', 'Transfer'))).toBe(false);
    expect(component.isCorrectable(item('r-1', 'Reversal'))).toBe(false);
    expect(component.isCorrectable(item('s-1', 'Reassignment'))).toBe(false);
  });
});
