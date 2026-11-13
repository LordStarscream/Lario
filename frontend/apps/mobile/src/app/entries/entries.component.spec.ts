// @vitest-environment jsdom
// Batch E (P5) — Eingaben-Liste: Zeilen (Datum, Total, Konto, Pools,
// Status), rohe-ID-Fallback, Leerzustand und der Verwerfen-Hinweis aus
// dem Router-State der Detail-View.
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
import { EntriesComponent } from './entries.component';
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

/** Stammdaten: ein Konto, zwei Pools, eine Kategorie (Namen für die
 *  Anzeige). */
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
  const taxCategories: TaxCategoryDto[] = [];
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

async function createHarness(): Promise<RouterTestingHarness> {
  dbState.reset();
  TestBed.configureTestingModule({
    imports: [EntriesComponent],
    providers: [provideZonelessChangeDetection(), provideRouter(routes)],
  });
  const db = TestBed.inject(LocalDatabase);
  await db.init();
  return RouterTestingHarness.create();
}

/** Einen Macrotask-Tick: flush alle ausstehenden Microtasks (Fake-DB). */
function flush(): Promise<void> {
  return new Promise((resolve) => setTimeout(resolve, 0));
}

function rowElements(harness: RouterTestingHarness): HTMLElement[] {
  const el = harness.fixture.nativeElement as HTMLElement;
  return Array.from(el.querySelectorAll<HTMLElement>('[data-testid="entry-row"]'));
}

describe('EntriesComponent (Batch E, P5)', () => {
  it('rendert Zeilen mit Datum, Total, Kontonamen, Pool-Namen und Status (2 Buchungen, eine mit 2 Splits auf verschiedenen Pools)', async () => {
    const harness = await createHarness();
    await seedSnapshot();
    const bookings = TestBed.inject(BookingsRepository);
    await bookings.save(
      {
        date: '2026-11-01',
        totalAmountCents: 1500,
        accountId: 'acc-1',
        note: null,
        splits: [
          {
            id: 's1',
            amountCents: 1500,
            poolId: 'pool-1',
            categoryId: 'cat-1',
            note: null,
            tax: null,
          },
        ],
      },
      new Date('2026-11-01T08:00:00.000Z'),
    );
    await bookings.save(
      {
        date: '2026-11-02',
        totalAmountCents: 1500,
        accountId: 'acc-1',
        note: null,
        splits: [
          {
            id: 's1',
            amountCents: 700,
            poolId: 'pool-1',
            categoryId: 'cat-1',
            note: null,
            tax: null,
          },
          {
            id: 's2',
            amountCents: 800,
            poolId: 'pool-2',
            categoryId: 'cat-1',
            note: null,
            tax: null,
          },
        ],
      },
      new Date('2026-11-02T08:00:00.000Z'),
    );
    await harness.navigateByUrl('/eingaben');
    await flush();
    harness.detectChanges();

    const rows = rowElements(harness);
    expect(rows).toHaveLength(2);
    // createdAtUtc aufsteigend: ältere Buchung zuerst.
    expect(rows[0].querySelector('.entries-item-date')?.textContent).toBe(
      '2026-11-01',
    );
    // formatCents(1500) = „15“ (ganze Euro ohne Nachkomma).
    expect(rows[0].querySelector('.entries-item-amount')?.textContent).toBe(
      '15 €',
    );
    expect(rows[0].textContent).toContain('Giro');
    expect(rows[0].textContent).toContain('Allgemein');
    expect(rows[0].textContent).toContain('ausstehend');
    expect(rows[1].querySelector('.entries-item-date')?.textContent).toBe(
      '2026-11-02',
    );
    // Beide Pool-Namen der 2-Split-Buchung (Reihenfolge nicht
    // garantiert — einzeln prüfen).
    expect(rows[1].textContent).toContain('Allgemein');
    expect(rows[1].textContent).toContain('Freizeit');
  });

  it('unbekannte Konto-ID → rohe ID als Fallback (Konto aus Snapshot entfernt)', async () => {
    const harness = await createHarness();
    await seedSnapshot();
    // Buchung mit gültigem Konto anlegen (das Repository validiert
    // die Referenzen gegen den aktuellen Snapshot) …
    const bookings = TestBed.inject(BookingsRepository);
    await bookings.save(
      {
        date: '2026-11-01',
        totalAmountCents: 100,
        accountId: 'acc-1',
        note: null,
        splits: [
          {
            id: 's1',
            amountCents: 100,
            poolId: 'pool-1',
            categoryId: 'cat-1',
            note: null,
            tax: null,
          },
        ],
      },
      new Date('2026-11-01T08:00:00.000Z'),
    );
    // … dann das Konto aus dem Snapshot entfernen (Snapshot-Update,
    // z. B. Konto im Host gelöscht) → Anzeige fällt auf die ID zurück.
    await TestBed.inject(SnapshotRepository).replace(
      { accounts: new Map(), pools: new Map(), categories: new Map(), taxCategories: new Map() },
      '2026-11-05T00:00:00Z',
    );
    await harness.navigateByUrl('/eingaben');
    await flush();
    harness.detectChanges();

    const rows = rowElements(harness);
    expect(rows).toHaveLength(1);
    expect(rows[0].textContent).toContain('acc-1');
    expect(rows[0].textContent).not.toContain('Giro');
  });

  it('leerer Zustand → „Keine lokalen Eingaben.“', async () => {
    const harness = await createHarness();
    await seedSnapshot();
    await harness.navigateByUrl('/eingaben');
    await flush();
    harness.detectChanges();

    const el = harness.fixture.nativeElement as HTMLElement;
    expect(el.textContent).toContain('Keine lokalen Eingaben.');
    expect(rowElements(harness)).toHaveLength(0);
  });

  it('discardMessage (Router-State der Detail-View) → Hinweis, dann schließen', async () => {
    const harness = await createHarness();
    await seedSnapshot();
    // Über den echten Router mit State navigieren (Harness-API kennt
    // keine State-Option).
    const router = TestBed.inject(Router);
    await router.navigate(['/eingaben'], {
      state: { discardMessage: 'Buchung verworfen.' },
    });
    await flush();
    harness.detectChanges();

    const el = harness.fixture.nativeElement as HTMLElement;
    const notice = el.querySelector('[role="status"]');
    expect(notice).not.toBeNull();
    expect(notice?.textContent).toContain('Buchung verworfen.');

    const close = notice?.querySelector('button') as HTMLButtonElement;
    expect(close).not.toBeNull();
    close.click();
    harness.detectChanges();
    expect(
      (harness.fixture.nativeElement as HTMLElement).querySelector(
        '[role="status"]',
      ),
    ).toBeNull();
  });
});
