// @vitest-environment jsdom
// Batch E (P5) — Eingaben-Detail: Felder inkl. Steuer-Block (Teilbetrag,
// Design-D2-Normalisierung bei null-Teilbetrag, kein Block bei
// !relevant), „Ändern“-Link (nur queued) und der Zwei-Stufen-
// Verwerfen-Flow (DB leer + Router-Navigation).
//
// Ergänzt `app.spec.ts` (Routing/Shell): dort nur Existenz-Checks der
// Aktionen, hier Verhalten und DB-Zustand.
//
// Strategie wie `app.spec.ts`: RouterTestingHarness gegen die echten
// Routen, Fake-DB (kein Repository-Mock).
import { provideZonelessChangeDetection } from '@angular/core';
import { provideRouter, Router } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { TestBed } from '@angular/core/testing';
import {
  capacitorSqliteMockFactory,
  dbState,
  type CapacitorSqliteMock,
} from '../db/capacitor-sqlite-fake';
import { LocalDatabase } from '../db/local-database';
import { BookingsRepository } from '../db/bookings.repository';
import { SnapshotRepository } from '../db/snapshot.repository';
import { routes } from '../app.config';
import { EntriesDetailComponent } from './entries-detail.component';
import type {
  AccountDto,
  CategoryDto,
  PoolDto,
  TaxCategoryDto,
} from '../../generated';

vi.mock('@capacitor/core', () => ({
  Capacitor: { isNativePlatform: () => true },
}));
vi.mock('@capacitor-community/sqlite', () => {
  const mock = () => capacitorSqliteMockFactory().CapacitorSQLite;
  return {
    CapacitorSQLite: {
      createConnection: (
        opts: Parameters<CapacitorSqliteMock['createConnection']>[0],
      ) => mock().createConnection(opts),
      open: (opts: Parameters<CapacitorSqliteMock['open']>[0]) =>
        mock().open(opts),
      executeSet: (opts: Parameters<CapacitorSqliteMock['executeSet']>[0]) =>
        mock().executeSet(opts),
      query: (opts: Parameters<CapacitorSqliteMock['query']>[0]) =>
        mock().query(opts),
      run: (opts: Parameters<CapacitorSqliteMock['run']>[0]) =>
        mock().run(opts),
      beginTransaction: (
        opts: Parameters<CapacitorSqliteMock['beginTransaction']>[0],
      ) => mock().beginTransaction(opts),
      commitTransaction: (
        opts: Parameters<CapacitorSqliteMock['commitTransaction']>[0],
      ) => mock().commitTransaction(opts),
      rollbackTransaction: (
        opts: Parameters<CapacitorSqliteMock['rollbackTransaction']>[0],
      ) => mock().rollbackTransaction(opts),
      isTransactionActive: (
        opts: Parameters<CapacitorSqliteMock['isTransactionActive']>[0],
      ) => mock().isTransactionActive(opts),
    } satisfies CapacitorSqliteMock,
  };
});

/** Stammdaten inkl. einer Steuerkategorie (für den Steuer-Block). */
async function seedSnapshot(): Promise<void> {
  const accounts: AccountDto[] = [
    {
      id: 'acc-1',
      name: 'Giro',
      type: 'checking',
      isInterestBearing: false,
      status: 'active',
      baseRevision: 3,
    },
  ];
  const pools: PoolDto[] = [
    { id: 'pool-1', name: 'Allgemein', status: 'active', baseRevision: 2 },
    { id: 'pool-2', name: 'Freizeit', status: 'active', baseRevision: 2 },
  ];
  const categories: CategoryDto[] = [
    {
      id: 'cat-1',
      name: 'Lebensmittel',
      parentId: null,
      status: 'active',
      baseRevision: 1,
      taxPreset: null,
    },
  ];
  const taxCategories: TaxCategoryDto[] = [
    {
      id: 'tax-1',
      name: 'Vorsteuer',
      parentId: null,
      status: 'active',
      baseRevision: 1,
    },
  ];
  const snapshotRepo = TestBed.inject(SnapshotRepository);
  await snapshotRepo.replace(
    {
      accounts: new Map(accounts.map((a) => [a.id, a])),
      pools: new Map(pools.map((p) => [p.id, p])),
      categories: new Map(categories.map((c) => [c.id, c])),
      taxCategories: new Map(taxCategories.map((t) => [t.id, t])),
    },
    '2026-10-01T00:00:00Z',
  );
}

/**
 * Buchung mit drei Splits (14 € gesamt):
 * - s1 (800 Ct): relevant + Steuerkategorie + Teilbetrag 320 Ct + Notiz,
 * - s2 (400 Ct): relevant + `markedAmountCents: null` → Repository
 *   normalisiert auf den Splitbetrag (Design D2) → Anzeige „(4 €)“,
 * - s3 (200 Ct): nicht relevant → kein Steuer-Block in der View.
 */
async function seedBookingWithTax(): Promise<string> {
  await seedSnapshot();
  const bookings = TestBed.inject(BookingsRepository);
  const result = await bookings.save(
    {
      date: '2026-11-02',
      totalAmountCents: 1400,
      accountId: 'acc-1',
      note: 'Wochenende',
      splits: [
        {
          id: 's1',
          amountCents: 800,
          poolId: 'pool-1',
          categoryId: 'cat-1',
          note: null,
          tax: {
            relevant: true,
            taxCategoryId: 'tax-1',
            taxCategoryName: 'Vorsteuer',
            markedAmountCents: 320,
            note: 'Voll abziehbar',
          },
        },
        {
          id: 's2',
          amountCents: 400,
          poolId: 'pool-2',
          categoryId: 'cat-1',
          note: null,
          tax: {
            relevant: true,
            taxCategoryId: 'tax-1',
            taxCategoryName: 'Vorsteuer',
            markedAmountCents: null,
            note: null,
          },
        },
        {
          id: 's3',
          amountCents: 200,
          poolId: 'pool-1',
          categoryId: 'cat-1',
          note: null,
          tax: {
            relevant: false,
            taxCategoryId: null,
            taxCategoryName: null,
            markedAmountCents: null,
            note: null,
          },
        },
      ],
    },
    new Date('2026-11-02T08:30:00.000Z'),
  );
  return result.bookingId;
}

/** Einen Macrotask-Tick: flush alle ausstehenden Microtasks (Fake-DB). */
function flush(): Promise<void> {
  return new Promise((resolve) => setTimeout(resolve, 0));
}

describe('EntriesDetailComponent (Batch E, P5)', () => {
  it('rendert alle Felder inkl. Steuer-Blocke (Teilbetrag, D2-Normalisierung, kein Block bei !relevant)', async () => {
    const harness = await createHarness();
    const bookingId = await seedBookingWithTax();
    await harness.navigateByUrl(`/eingaben/${bookingId}`);
    await flush();
    harness.detectChanges();
    const el = harness.fixture.nativeElement as HTMLElement;

    // Kopf-Felder:
    expect(el.querySelector('app-entries-detail')).not.toBeNull();
    expect(el.textContent).toContain('2026-11-02');
    // formatCents(1400) = „14“.
    expect(el.textContent).toContain('14 €');
    expect(el.textContent).toContain('Giro');
    expect(el.textContent).toContain('Wochenende');
    expect(el.textContent).toContain('2026-11-02T08:30:00.000Z');
    expect(el.textContent).toContain('ausstehend');

    // Split-Beträge (Reihenfolge nicht garantiert — als Menge prüfen).
    const splitAmounts = Array.from(
      el.querySelectorAll('[data-testid^="entries-split-amount-"]'),
    ).map((n) => (n.textContent ?? '').trim());
    expect(splitAmounts.sort()).toEqual(['2 €', '4 €', '8 €']);

    // Steuer-Blocke: genau zwei (nur die relevanten Splits).
    const taxBlocks = Array.from(
      el.querySelectorAll('[data-testid^="entries-split-tax-"]'),
    );
    expect(taxBlocks).toHaveLength(2);
    // s1: Teilbetrag 3,20 € + Notiz.
    const withMarked = taxBlocks.find((b) =>
      b.textContent?.includes('3,20 €'),
    );
    expect(withMarked).toBeDefined();
    expect(withMarked?.textContent).toContain('Steuerkategorie: Vorsteuer');
    expect(withMarked?.textContent).toContain('Voll abziehbar');
    // s2: null-Teilbetrag wurde beim Speichern auf den Splitbetrag
    // normalisiert (Design D2) → „(4 €)“, kein „voller Betrag“-Text.
    const normalized = taxBlocks.find((b) => b !== withMarked);
    expect(normalized).toBeDefined();
    expect(normalized?.textContent).toContain('(4 €)');
    expect(normalized?.textContent).not.toContain('voller Betrag');
    expect(normalized?.textContent).not.toContain('Voll abziehbar');
    // s3 (!relevant): kein Steuer-Block — die Split-Zeile existiert,
    // trägt aber keinen `[data-testid^="entries-split-tax-"]`-Block.
    const notRelevantSplit = el.querySelector(
      '[data-testid^="entries-split-"][data-testid$="-s3"]',
    );
    expect(notRelevantSplit).not.toBeNull();
    expect(
      notRelevantSplit?.querySelector('[data-testid^="entries-split-tax-"]'),
    ).toBeNull();
  });

  it('„Ändern“-Link vorhanden bei queued, Ziel /erfassen/{id}', async () => {
    const harness = await createHarness();
    const bookingId = await seedBookingWithTax();
    await harness.navigateByUrl(`/eingaben/${bookingId}`);
    await flush();
    harness.detectChanges();
    const el = harness.fixture.nativeElement as HTMLElement;

    const edit = el.querySelector(
      '[data-testid="entries-detail-edit"]',
    ) as HTMLAnchorElement;
    expect(edit).not.toBeNull();
    expect(edit.getAttribute('href')).toBe(`/erfassen/${bookingId}`);
    // „Verwerfen“ (Zwei-Stufen) sichtbar:
    expect(
      el.querySelector('[data-testid="entries-detail-discard"]'),
    ).not.toBeNull();
  });

  it('Verwerfen-Flow: Button → Bestätigungsblock → „Ja“ → DB leer + Navigation', async () => {
    const harness = await createHarness();
    const bookingId = await seedBookingWithTax();
    await harness.navigateByUrl(`/eingaben/${bookingId}`);
    await flush();
    harness.detectChanges();
    const el = harness.fixture.nativeElement as HTMLElement;

    // Navigation abfangen (Spy): die eigentliche Weiterleitung wird
    // nur geprüft, nicht ausgeführt.
    const router = TestBed.inject(Router);
    const navSpy = vi.spyOn(router, 'navigate').mockResolvedValue(true);

    // Erster Klick: Bestätigungsblock.
    const discard = el.querySelector(
      '[data-testid="entries-detail-discard"]',
    ) as HTMLButtonElement;
    expect(discard).not.toBeNull();
    discard.click();
    harness.detectChanges();
    const confirm = el.querySelector(
      '[data-testid="entries-detail-discard-confirm"]',
    );
    expect(confirm).not.toBeNull();
    // Vor dem „Ja“ ist nichts gelöscht:
    expect(dbState.bookings.size).toBe(1);
    expect(dbState.splits.size).toBe(3);
    expect(dbState.outbox.size).toBe(1);

    // Zweiter Klick: „Ja, verwerfen“.
    const yes = el.querySelector(
      '[data-testid="entries-detail-discard-confirm-yes"]',
    ) as HTMLButtonElement;
    expect(yes).not.toBeNull();
    yes.click();
    await flush();
    harness.detectChanges();

    // DB: Buchung, Splits und Outbox-Zeile sind weg.
    expect(dbState.bookings.size).toBe(0);
    expect(dbState.splits.size).toBe(0);
    expect(dbState.outbox.size).toBe(0);
    // Navigation zurück zur Liste (mit Hinweis-State).
    expect(navSpy).toHaveBeenCalledWith(
      ['/eingaben'],
      expect.objectContaining({
        state: expect.objectContaining({ discardMessage: 'Buchung verworfen.' }),
      }),
    );
  });

  it('unbekannte ID → „Keine Buchung mit dieser ID gefunden.“', async () => {
    const harness = await createHarness();
    await seedSnapshot();
    await harness.navigateByUrl('/eingaben/b-unknown');
    await flush();
    harness.detectChanges();
    const el = harness.fixture.nativeElement as HTMLElement;
    expect(el.textContent).toContain('Keine Buchung mit dieser ID gefunden.');
    expect(
      el.querySelector('[data-testid="entries-detail-notfound"]'),
    ).not.toBeNull();
  });
});

async function createHarness(): Promise<RouterTestingHarness> {
  dbState.reset();
  TestBed.configureTestingModule({
    imports: [EntriesDetailComponent],
    providers: [provideZonelessChangeDetection(), provideRouter(routes)],
  });
  const db = TestBed.inject(LocalDatabase);
  await db.init();
  return RouterTestingHarness.create();
}
