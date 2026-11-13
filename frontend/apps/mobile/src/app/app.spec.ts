// @vitest-environment jsdom
// Batch E (P6) — App-Routing: Die App ist ein Router-Shell mit drei
// Views (Erfassen, Eingaben, Einrichtung) + Detail-/Ändern-Unter-
// routen. Getestet mit `RouterTestingHarness` gegen die ECHTEN
// Routen aus `app.config.ts` (nicht gegen eine Testkopie):
//
// - `/` → Redirect auf `/erfassen`
// - unbekannte Pfade → Fallback-Redirect auf `/erfassen`
// - `/eingaben` → Liste rendert (mit geseedeten Buchungen)
// - `/eingaben/:id` → Detail rendert
// - `/erfassen/:id` → Ändern-Modus (Banner)
// - `/erfassen/<unbekannt>` → Hinweis, Formular unverändert
//
// Zoneless (Angular 22, kein zone.js): asynchrone Zustände sind
// Signals; die Fake-DB liefert über Promises (Microtasks), ein
// einzelner Macrotask-Tick (`setTimeout(0)`) reicht zum Flushen.
import { provideZonelessChangeDetection } from '@angular/core';
import { provideRouter, Router } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { TestBed } from '@angular/core/testing';
import {
  capacitorSqliteMockFactory,
  dbState,
  type CapacitorSqliteMock,
} from './db/capacitor-sqlite-fake';
import { LocalDatabase } from './db/local-database';
import { BookingsRepository } from './db/bookings.repository';
import { SnapshotRepository } from './db/snapshot.repository';
import { routes } from './app.config';
import { App } from './app';
import { EntryComponent } from './entry/entry.component';
import { EntriesComponent } from './entries/entries.component';
import { EntriesDetailComponent } from './entries/entries-detail.component';
import { SetupComponent } from './setup/setup.component';
import type {
  AccountDto,
  CategoryDto,
  PoolDto,
  TaxCategoryDto,
} from '../generated';

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
    imports: [
      App,
      EntryComponent,
      EntriesComponent,
      EntriesDetailComponent,
      SetupComponent,
    ],
    providers: [provideZonelessChangeDetection(), provideRouter(routes)],
  });
  const db = TestBed.inject(LocalDatabase);
  await db.init();
  return RouterTestingHarness.create();
}

async function seedBooking(totalAmountCents: number): Promise<string> {
  await seedSnapshot();
  const bookings = TestBed.inject(BookingsRepository);
  const result = await bookings.save(
    {
      date: '2026-10-28',
      totalAmountCents,
      accountId: 'acc-1',
      note: null,
      splits: [
        {
          id: 's1',
          amountCents: totalAmountCents,
          poolId: 'pool-1',
          categoryId: 'cat-1',
          note: null,
          tax: null,
        },
      ],
    },
    new Date(2026, 9, 28, 12, 0, 0),
  );
  return result.bookingId;
}

/** Einen Macrotask-Tick: flush alle ausstehenden Microtasks (Fake-DB). */
function flush(): Promise<void> {
  return new Promise((resolve) => setTimeout(resolve, 0));
}

/** Aktuelle Router-URL (RouterTestingHarness kennt in Angular 22 keinen Location-Zugriff). */
function routerUrl(): string {
  return TestBed.inject(Router).url;
}

describe('App-Routing (Batch E, P6)', () => {
  it('navigate to / → Redirect auf /erfassen', async () => {
    const harness = await createHarness();
    await harness.navigateByUrl('/');
    expect(routerUrl()).toBe('/erfassen');
    expect(
      harness.fixture.nativeElement.querySelector('app-entry'),
    ).not.toBeNull();
  });

  it('Fallback: unbekannter Pfad → Redirect auf /erfassen', async () => {
    const harness = await createHarness();
    await harness.navigateByUrl('/unbekannt/pfad');
    expect(routerUrl()).toBe('/erfassen');
    expect(
      harness.fixture.nativeElement.querySelector('app-entry'),
    ).not.toBeNull();
  });

  it('navigate to /eingaben → Liste rendert (Datum, Total, Status, Aktionen)', async () => {
    const harness = await createHarness();
    await seedBooking(2500);
    await harness.navigateByUrl('/eingaben');
    await flush();
    harness.detectChanges();
    const el = harness.fixture.nativeElement as HTMLElement;
    const row = el.querySelector('[data-testid="entry-row"]') as HTMLElement;
    expect(row).not.toBeNull();
    expect(row.textContent).toContain('2026-10-28');
    // formatCents-Konvention: ganze Euro ohne Nachkomma (2500 Ct → „25“).
    expect(row.textContent).toContain('25');
    expect(row.textContent).toContain('ausstehend');
    // Aktionen: „Ändern“ → erfassen/:id, „Verwerfen“ → eingaben/:id:
    const edit = row.querySelector(
      '[data-testid="entry-edit"]',
    ) as HTMLAnchorElement;
    const discard = row.querySelector(
      '[data-testid="entry-discard"]',
    ) as HTMLAnchorElement;
    expect(edit.getAttribute('href')).toContain('/erfassen/');
    expect(discard.getAttribute('href')).toContain('/eingaben/');
  });

  it('/eingaben ohne Buchungen → Leerzustand', async () => {
    const harness = await createHarness();
    await seedSnapshot();
    await harness.navigateByUrl('/eingaben');
    await flush();
    harness.detectChanges();
    const el = harness.fixture.nativeElement as HTMLElement;
    expect(el.textContent).toContain('Keine lokalen Eingaben.');
    expect(el.querySelector('[data-testid="entry-row"]')).toBeNull();
  });

  it('navigate to /eingaben/:id → Detail rendert Felder und Aktionen', async () => {
    const harness = await createHarness();
    const bookingId = await seedBooking(1200);
    await harness.navigateByUrl(`/eingaben/${bookingId}`);
    await flush();
    harness.detectChanges();
    const el = harness.fixture.nativeElement as HTMLElement;
    expect(el.querySelector('app-entries-detail')).not.toBeNull();
    expect(el.textContent).toContain('2026-10-28');
    expect(el.textContent).toContain('ausstehend');
    // „Verwerfen“ (Zwei-Stufen) und „Ändern“ nur für queued:
    expect(el.querySelector('[data-testid="entries-detail-discard"]')).not.toBeNull();
    expect(el.querySelector('[data-testid="entries-detail-edit"]')).not.toBeNull();
  });

  it('/eingaben/:id mit unbekannter ID → Hinweis', async () => {
    const harness = await createHarness();
    await harness.navigateByUrl('/eingaben/b-unknown');
    await flush();
    harness.detectChanges();
    const el = harness.fixture.nativeElement as HTMLElement;
    expect(el.textContent).toContain('Keine Buchung mit dieser ID gefunden.');
  });

  it('navigate to /erfassen/:id → Ändern-Modus (Banner + Button)', async () => {
    const harness = await createHarness();
    const bookingId = await seedBooking(2500);
    await harness.navigateByUrl(`/erfassen/${bookingId}`);
    await flush();
    harness.detectChanges();
    const el = harness.fixture.nativeElement as HTMLElement;
    const banner = el.querySelector(
      '[data-testid="entry-edit-banner"]',
    ) as HTMLElement;
    expect(banner).not.toBeNull();
    expect(banner.textContent).toContain('wird bearbeitet');
    // Button-Label im Ändern-Modus:
    const save = el.querySelector(
      '[data-testid="entry-save"]',
    ) as HTMLButtonElement;
    expect(save.textContent).toContain('Änderungen speichern');
    expect(
      el.querySelector('[data-testid="entry-edit-cancel"]'),
    ).not.toBeNull();
  });

  it('navigate to /erfassen/<unbekannt> → Hinweis, kein Banner', async () => {
    const harness = await createHarness();
    await seedBooking(2500);
    await harness.navigateByUrl('/erfassen/b-unknown');
    await flush();
    harness.detectChanges();
    const el = harness.fixture.nativeElement as HTMLElement;
    expect(el.querySelector('[data-testid="entry-edit-banner"]')).toBeNull();
    const status = el.querySelector(
      '[data-testid="entry-save-status"]',
    ) as HTMLElement;
    expect(status.textContent).toContain('nicht gefunden');
  });

  it('App-Shell rendert Tabs (RouterLink) und Router-Outlet', () => {
    // Kein Harness: die Shell selbst wird direkt gerendert — der
    // Router muss konfiguriert sein, damit die Tabs (RouterLink)
    // funktionieren.
    TestBed.configureTestingModule({
      imports: [App],
      providers: [provideZonelessChangeDetection(), provideRouter(routes)],
    });
    const fixture = TestBed.createComponent(App);
    fixture.detectChanges();
    const el = fixture.nativeElement as HTMLElement;
    expect(el.querySelector('router-outlet')).not.toBeNull();
    const tabBar = el.querySelector('[data-testid="tab-bar"]');
    expect(tabBar).not.toBeNull();
    // Drei Tabs mit korrekten Zielen:
    const links = el.querySelectorAll('.tab-bar a');
    expect(links.length).toBe(3);
    expect((links[0] as HTMLAnchorElement).getAttribute('href')).toBe('/erfassen');
    expect((links[1] as HTMLAnchorElement).getAttribute('href')).toBe('/eingaben');
    expect((links[2] as HTMLAnchorElement).getAttribute('href')).toBe('/einrichtung');
  });
});
