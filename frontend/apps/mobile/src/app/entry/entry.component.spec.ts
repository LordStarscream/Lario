// @vitest-environment jsdom
// Batch D (P4) — Erfassen-Komponente: Tests für den Erfassungs-Flow.
//
// Strategie: Komponente mit echtem Fake-DB-State (kein Mock der
// Repositories — die Transaktions- und Zeilen-Absprachen der
// Batch-A/B/C-Repositories bleiben wirksam). Zoneless-Rendering
// (Angular-22-Default, main.ts ohne Zone-Provider).
import {
  Component,
  provideZonelessChangeDetection,
} from '@angular/core';
import { TestBed } from '@angular/core/testing';
import {
  capacitorSqliteMockFactory,
  dbState,
  type CapacitorSqliteMock,
} from '../db/capacitor-sqlite-fake';
import { LocalDatabase } from '../db/local-database';
import { SnapshotRepository } from '../db/snapshot.repository';
import type {
  AccountDto,
  CategoryDto,
  PoolDto,
  TaxCategoryDto,
} from '../../generated';
import { EntryComponent } from './entry.component';
import type { EntryFormModel } from './entry-form.model';

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

/** Testhost: rendert <app-entry> in einer Zoneless-Test-App. */
@Component({
  selector: 'app-test-host',
  template: '<app-entry />',
  imports: [EntryComponent],
})
class TestHostComponent {}

interface SnapshotInput {
  accounts: AccountDto[];
  pools: PoolDto[];
  categories: CategoryDto[];
  taxCategories: TaxCategoryDto[];
}

/**
 * Baut eine gerenderte `EntryComponent` mit echtem Fake-DB-State.
 * `ngOnInit` wird explizit abgewartet (der erste Aufruf beim
 * Component-Creation ist idempotent — reiner Snapshot-Read), damit
 * der Snapshot-Load deterministisch abgeschlossen ist.
 * `snapshot === null` → kein Snapshot in der DB (Load liefert null).
 */
async function createLoaded(snapshot: SnapshotInput | null) {
  dbState.reset();
  // TestBed ist pro Test frisch (auto-Reset); die Repositories sind
  // `providedIn: 'root'` => dieselben Instanzen für Setup und Komponente.
  TestBed.configureTestingModule({
    imports: [TestHostComponent],
    providers: [provideZonelessChangeDetection()],
  });
  const db = TestBed.inject(LocalDatabase);
  await db.init(); // App-Start: Schema-Init.
  const snapshotRepo = TestBed.inject(SnapshotRepository);
  if (snapshot !== null) {
    await snapshotRepo.replace(
      {
        accounts: new Map(snapshot.accounts.map((a) => [a.id, a])),
        pools: new Map(snapshot.pools.map((p) => [p.id, p])),
        categories: new Map(snapshot.categories.map((c) => [c.id, c])),
        taxCategories: new Map(snapshot.taxCategories.map((t) => [t.id, t])),
      },
      '2026-10-01T00:00:00Z',
    );
  }

  const fixture = TestBed.createComponent(TestHostComponent);
  const comp = fixture.debugElement
    .children[0]
    .componentInstance as EntryComponent;
  await comp.ngOnInit();
  fixture.detectChanges();
  return { comp, dbState, fixture };
}

function testSnapshot(): SnapshotInput {
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
    {
      id: 'cat-2',
      name: 'Geschenke',
      parentId: null,
      status: 'active',
      baseRevision: 1,
      taxPreset: {
        taxRelevant: true,
        taxCategoryId: 'tx-1',
        taxCategoryName: 'Sachbezug',
        inheritToChildren: false,
        baseRevision: 1,
      },
    },
  ];
  const taxCategories: TaxCategoryDto[] = [
    {
      id: 'tx-1',
      name: 'Sachbezug',
      parentId: null,
      status: 'active',
      baseRevision: 1,
    },
  ];
  return { accounts, pools, categories, taxCategories };
}

/** Setzt das Formular auf einen gültigen, einfachen Zustand. */
function setValidForm(
  comp: EntryComponent,
  overrides: Partial<{
    amount: string;
    splits: { amount: string; poolId?: string; categoryId?: string }[];
  }> = {},
): void {
  const snapshot = comp.snapshot();
  if (snapshot === null) {
    throw new Error('testSnapshot fehlt');
  }
  const pools = [...snapshot.pools.values()];
  const categories = [...snapshot.categories.values()];
  const form: EntryFormModel = {
    date: '2026-10-28',
    amountInput: overrides.amount ?? '10,00',
    accountId: 'acc-1',
    note: '',
    splits: (overrides.splits ?? [{ amount: '10,00' }]).map((s, i) => ({
      id: `s${i + 1}`,
      key: `s${i + 1}`,
      amountInput: s.amount,
      poolId: s.poolId ?? pools[0]?.id ?? '',
      categoryId: s.categoryId ?? categories[0]?.id ?? '',
      note: '',
      tax: {
        relevant: false,
        taxCategoryId: null,
        taxCategoryName: null,
        markedAmountInput: '',
        note: '',
      },
    })),
  };
  comp.form.set(form);
}

describe('EntryComponent', () => {
  it('speichert eine gültige Ausgabe (2 Splits + Steuer) — Transaktion mit Booking, Splits und Outbox', async () => {
    const { comp, dbState } = await createLoaded(testSnapshot());

    // Formular: Gesamt 30,00; Split 1 10,00 + Steuer (tx-1, leer =
    // Vollmarkierung); Split 2 20,00 neutral.
    setValidForm(comp, {
      amount: '30,00',
      splits: [{ amount: '10,00' }, { amount: '20,00' }],
    });
    const form = comp.form();
    comp.form.set({
      ...form,
      splits: [
        {
          ...form.splits[0],
          tax: {
            ...form.splits[0].tax,
            relevant: true,
            taxCategoryId: 'tx-1',
            taxCategoryName: 'Sachbezug',
          },
        },
        form.splits[1],
      ],
    });

    await comp.save();

    expect(comp.saveStatus()).toBe('saved');
    // Formular zurückgesetzt (Konto bleibt erhalten).
    expect(comp.form().amountInput).toBe('');
    expect(comp.form().accountId).toBe('acc-1');

    // Genau eine Buchung.
    expect(dbState.bookings.size).toBe(1);
    const booking = [...dbState.bookings.values()][0];
    expect(booking.total_amount_cents).toBe(3000);
    expect(booking.account_id).toBe('acc-1');

    // Zwei Splits mit den erwarteten Werten (snake_case, Batch-B-Format).
    expect(dbState.splits.size).toBe(2);
    const splitRows = [...dbState.splits.values()].sort(
      (a, b) => a.amount_cents - b.amount_cents,
    );
    expect(splitRows[0].amount_cents).toBe(1000);
    expect(splitRows[0].booking_id).toBe(booking.id);
    expect(splitRows[0].tax_relevant).toBe(1);
    expect(splitRows[0].tax_category_id).toBe('tx-1');
    // Leerer markierter Betrag = Vollmarkierung → Repository speichert
    // den Splitbetrag als markierte Summe (bookings.repository.ts:
    // `tax.markedAmountCents ?? split.amountCents`).
    expect(splitRows[0].marked_amount_cents).toBe(1000);
    expect(splitRows[1].amount_cents).toBe(2000);
    // Neutraler Split: tax_relevant = 0 (Repository schreibt 0, nicht
    // NULL — `tax: { relevant: false, ... }` statt `null`).
    expect(splitRows[1].tax_relevant).toBe(0);
    expect(splitRows[1].marked_amount_cents).toBeNull();

    // Genau eine Outbox-Zeile (queued), die auf die Buchung zeigt.
    expect(dbState.outbox.size).toBe(1);
    const outbox = [...dbState.outbox.values()][0];
    expect(outbox.status).toBe('queued');
    expect(outbox.booking_id).toBe(booking.id);
  });

  it('speichert bei ungültiger Summe (Split-Summe ≠ Gesamt) nichts', async () => {
    const { comp, dbState } = await createLoaded(testSnapshot());

    // Gesamt 50,00, ein Split 30,00 → split_sum_mismatch.
    setValidForm(comp, { amount: '50,00', splits: [{ amount: '30,00' }] });

    await comp.save();

    expect(comp.saveStatus()).toBe('error');
    expect(comp.errors().some((e) => e.code === 'split_sum_mismatch')).toBe(true);
    expect(dbState.bookings.size).toBe(0);
    expect(dbState.splits.size).toBe(0);
    expect(dbState.outbox.size).toBe(0);
  });

  it('Doppeltipp: zwei parallele save()-Aufrufe erzeugen genau eine Buchung', async () => {
    const { comp, dbState } = await createLoaded(testSnapshot());
    setValidForm(comp);

    const p1 = comp.save();
    const p2 = comp.save(); // zweiter Aufruf während p1 läuft
    await p1;
    await p2;

    expect(dbState.bookings.size).toBe(1);
    expect(dbState.outbox.size).toBe(1);
  });

  it('Entwurfs-Flow: speichern → laden → verwerfen', async () => {
    const { comp, dbState } = await createLoaded(testSnapshot());

    // Teilgefülltes Formular (Total 5,00) als Entwurf speichern.
    setValidForm(comp, { amount: '5,00', splits: [{ amount: '5,00' }] });
    await comp.saveDraft();
    expect(dbState.drafts.size).toBe(1);
    expect(comp.saveStatus()).toBe('saved');

    // Formular ändern, dann Entwurf laden → alter Stand wiederhergestellt.
    setValidForm(comp, { amount: '9,99', splits: [{ amount: '9,99' }] });
    await comp.loadDraft();
    expect(comp.form().amountInput).toBe('5,00');
    expect(comp.form().splits[0].amountInput).toBe('5,00');
    expect(comp.saveStatus()).toBe('saved');

    // Entwurf verwerfen → danach kein Entwurf mehr vorhanden.
    await comp.discardDraft();
    expect(dbState.drafts.size).toBe(0);
    await comp.loadDraft();
    expect(comp.saveStatus()).toBe('error');
    expect(comp.saveMessage()).toBe('Kein Entwurf vorhanden.');
  });

  it('Kategorie-Preset: Steuerkategorie wird vorgefüllt; Schalter aus → Felder geleert', async () => {
    const { comp } = await createLoaded(testSnapshot());
    setValidForm(comp);

    // Kategorie mit taxPreset (cat-2 "Geschenke") wählen.
    comp.onSplitCategoryChange('s1', 'cat-2');
    const split = comp.form().splits[0];
    expect(split.tax.relevant).toBe(true);
    expect(split.tax.taxCategoryId).toBe('tx-1');
    expect(split.tax.markedAmountInput).toBe('10,00'); // Vollmarkierung

    // Schalter auf "nicht relevant" → Steuer-Felder werden geleert.
    comp.onSplitTaxRelevantChange('s1', false);
    const cleared = comp.form().splits[0];
    expect(cleared.tax.relevant).toBe(false);
    expect(cleared.tax.taxCategoryId).toBeNull();
    expect(cleared.tax.taxCategoryName).toBeNull();
    expect(cleared.tax.markedAmountInput).toBe('');
  });

  it('Quick-Actions: gleichmäßig (Rest auf ersten Split) und Rest in letzten Split', async () => {
    const { comp } = await createLoaded(testSnapshot());

    // Gleichmäßig: Total 10,00, 3 leere Splits → 3,34 / 3,33 / 3,33
    // (333×3 = 999, Rest 1 Cent auf den ersten Split).
    const base = comp.form();
    comp.form.set({
      ...base,
      amountInput: '10,00',
      splits: [
        { ...base.splits[0], amountInput: '' },
        { ...base.splits[0], id: 's2', key: 's2', amountInput: '' },
        { ...base.splits[0], id: 's3', key: 's3', amountInput: '' },
      ],
    });
    comp.quickEvenly();
    expect(comp.form().splits.map((s) => s.amountInput)).toEqual([
      '3,34',
      '3,33',
      '3,33',
    ]);

    // Rest in letzten Split: Total 25,00, Split 1 10,00 → Split 2 15,00.
    const two = comp.form();
    comp.form.set({
      ...two,
      amountInput: '25,00',
      splits: [
        { ...two.splits[0], id: 's1', key: 's1', amountInput: '10,00' },
        { ...two.splits[1], id: 's2', key: 's2', amountInput: '' },
      ],
    });
    comp.quickFillLast();
    expect(comp.form().splits.map((s) => s.amountInput)).toEqual([
      '10,00',
      '15,00',
    ]);
  });

  it('ohne Snapshot: Speichern schlägt fehl (Fehler sichtbar, nichts gespeichert)', async () => {
    const { comp, dbState } = await createLoaded(null);

    // Leeres Standardformular: ohne Snapshot sind die Pflichtfelder
    // nicht referenzierbar; die strukturellen Validierungsfehler
    // (missing_amount, missing_account, missing_pool, …) sind sichtbar.
    expect(comp.snapshot()).toBeNull();

    await comp.save();

    expect(comp.saveStatus()).toBe('error');
    expect(comp.errors().length).toBeGreaterThan(0);
    expect(
      comp.errors().some((e) => e.code === 'missing_amount'),
    ).toBe(true);
    expect(comp.saveEnabled()).toBe(false);
    expect(dbState.bookings.size).toBe(0);
    expect(dbState.outbox.size).toBe(0);

    // Auch ein strukturell vollständiges Formular wird ohne Snapshot
    // verworfen (toLocalInput liefert ohne Snapshot null):
    const form = comp.form();
    comp.form.set({
      ...form,
      date: '2026-10-28',
      amountInput: '10,00',
      accountId: 'acc-1',
      splits: [
        {
          ...form.splits[0],
          amountInput: '10,00',
          poolId: 'pool-1',
          categoryId: 'cat-1',
        },
      ],
    });
    await comp.save();
    expect(comp.saveStatus()).toBe('error');
    expect(dbState.bookings.size).toBe(0);
    expect(dbState.outbox.size).toBe(0);
  });

  it('Rendering: Abweichung wird im DOM angezeigt', async () => {
    const { comp, fixture } = await createLoaded(testSnapshot());
    setValidForm(comp, { amount: '50,00', splits: [{ amount: '30,00' }] });
    fixture.detectChanges();

    const compiled = fixture.nativeElement as HTMLElement;
    const deviation = compiled.querySelector(
      '[data-testid="entry-deviation"]',
    );
    expect(deviation).not.toBeNull();
    expect(deviation?.textContent).toContain('−20,00');
    expect(comp.deviationCents()).toBe(-2000);
  });

  it('Steuer-Fehlerpfad: relevant ohne Steuerkategorie → kein Speichern', async () => {
    const { comp, dbState } = await createLoaded(testSnapshot());
    setValidForm(comp);

    // Steuer-Schalter an, aber keine Steuerkategorie gewählt.
    comp.onSplitTaxRelevantChange('s1', true);

    await comp.save();

    expect(comp.saveStatus()).toBe('error');
    expect(comp.errors().some((e) => e.code === 'missing_tax_category')).toBe(true);
    expect(dbState.bookings.size).toBe(0);
  });
});
