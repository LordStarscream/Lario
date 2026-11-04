# Worker-Task: Schritt 8, Batch D — Erfassen-View (mobile)

**Worktree:** `/tmp/lario-step8d` (Branch `step/8d-entry`, HEAD = main `b15c9b9` = main + Batch A + B + C).
**Nur in diesem Worktree arbeiten.** Commits am Ende: `feat(mobile): Batch D — Erfassen-View` (kleine logische Commits erlaubt). **Kein Push, kein PR** (macht der Controller).

## Kontext

Lario, Schritt 8 (produktive mobile Offline-Erfassung). Batch A/B/C sind fertig: lokale SQLite-DB (`lario`), Outbox, `BookingsRepository.save` (atomare executeSet-Transaktion, Referenzvalidierung gegen Snapshot), `DraftsRepository` (save/get/list/delete, `data` = freier JSON-String), `SnapshotRepository.loadSnapshot()` (liefert `MasterDataSnapshot | null`), `HostClient`, Setup-View (noch nicht im App-Root; Integration ist Batch E).

Batch D bringt die **Erfassen-View** als eigenständige Zoneless-Standalone-Component (`app-entry`), **noch nicht** im App-Root eingebunden (Batch E). Sie deckt Design D7 (Validierung + speichern) und D8 (Layout) der Task-Datei ab.

**Maßgebliche Design-Dokumente** (in DEINEM Worktree lesbar):
- `.tasks/step-8-mobile-offline.md`, Abschnitt „Batch D — Erfassen-View (P3)" + „Design-Entscheidungen Batch D (26.10.)" (D7/D8).
- Entwürfe (D8 „Entwurf speichern/laden/verwerfen") sind bereits in Batch B als `DraftsRepository` implementiert (B7); D9 = generierter Client (Batch C).

**Widerspruch zwischen dieser Datei und der Task-Datei → Task-Datei gewinnt; Befund dokumentieren.**

## Verbotenes / Abgrenzung

- **Kein** Senden/Outbox-Sync (Schritt 9). **Kein** UI-Routing/Tab-Shell/App-Root-Änderung (Batch E). **Kein** Android-Geräte-Test (Batch E).
- `app/app.ts` und `app/app.html` bleiben funktional unverändert (der Schritt-2-Prototyp läuft weiter) — **Ausnahme**: `app.ts`/`app.spec.ts` dürfen den `parseAmount`-Import auf `./amount` umstellen (Extraktion, s. Dateiliste). **Keine** andere Änderung an `app.ts`/`app.html`/`sqlite.service.ts`.
- `src/generated/`, `db/*`-Repositories (Batch A/B/C) **read-only** — keine Änderung. `setup/*` read-only.
- Keine Dependency-Änderung. Keine neue CSS-Datei (Styling ist Batch E) — die Component hat **kein** `styleUrl`.
- Keine Refactorings außerhalb der Dateiliste.

## Dateiliste (9 Dateien: 6 neu, 3 geändert)

Neu:
1. `frontend/apps/mobile/src/app/amount.ts`
2. `frontend/apps/mobile/src/app/entry/entry-form.model.ts`
3. `frontend/apps/mobile/src/app/entry/entry-form.model.spec.ts`
4. `frontend/apps/mobile/src/app/entry/entry.component.ts`
5. `frontend/apps/mobile/src/app/entry/entry.html`
6. `frontend/apps/mobile/src/app/entry/entry.component.spec.ts`

Geändert:
7. `frontend/apps/mobile/src/app/app.ts` (nur: `parseAmount` kommt jetzt aus `./amount`, Re-Export behalten)
8. `frontend/apps/mobile/src/app/app.spec.ts` (nur: Import-Quelle `parseAmount` auf `./amount` umstellen)
9. `frontend/apps/mobile/src/app/db/capacitor-sqlite-fake.ts` — **nur wenn nötig** (prüfen: die `drafts`-Tabelle existiert bereits aus Batch B; falls nicht, erst hier ergänzen — Befund dokumentieren)

## Verifizierte API-Fakten (Nicht erraten — so ist es im Code)

### Bestehende Bausteine (read-only)

- `parseAmount` (aktuell in `app.ts`, wird nach `amount.ts` gezogen):
  `export function parseAmount(raw: string): number | null | undefined` — `''` → `null`; ungültig → `undefined`; gültig → Cent (number). Akzeptiert `12,50`/`12.50`/`12`, max. 2 Nachkommastellen, Integer-Teil max. 13 Ziffern (`9999999999999,99` → `999999999999999`; `10000000000000,00` → `undefined`). **Semantik unverändert übernehmen** (bestehende Tests in `app.spec.ts` müssen weiter grün sein).
- `db/bookings.repository.ts` (read-only):
  - `LocalExpenseInput` (exportiert): `{ date: string; account: { id: string; name: string }; amountCents: number; note: string | null; splits: LocalSplitInput[] }`.
  - `LocalSplitInput` (exportiert): `{ amountCents: number; poolId: string; categoryId: string; note: string | null; tax: LocalTaxInput | null }`.
  - `LocalTaxInput` (exportiert): `{ relevant: boolean; taxCategoryId: string | null; taxCategoryName: string | null; markedAmountCents: number | null; note: string | null }`.
  - `save(input: LocalExpenseInput): Promise<LocalBookingRecord>` — validiert (inkl. `validateExpense`, exportiert, pure), dann **eine** `executeSet`-Transaktion (Buchung + Outbox). Wirft `BookingValidationError(reason, message)` (Reason u. a. `invalid_date | invalid_amount | invalid_account | no_splits | invalid_split_amount | invalid_pool | invalid_category | split_sum_mismatch | tax_inconsistent | invalid_marked_amount | no_snapshot | unknown_account | archived_account | unknown_pool | archived_pool | archived_category | unknown_tax_category`) oder `Error` (DB-Fehler). **Auflösung erst nach Commit** → UI bestätigt erst danach.
- `db/drafts.repository.ts` (read-only, exakte API):
  - `LocalDraft { id: string; name: string; data: string; createdAtUtc: string; updatedAtUtc: string }` (data = freier JSON-String).
  - `save(id: string, name: string, data: string, now: Date): Promise<void>` (Upsert — `now` ist expliziter Parameter, keine versteckte Uhr), `get(id: string): Promise<LocalDraft | null>`, `list(): Promise<LocalDraft[]>`, `delete(id: string): Promise<void>`.
  - **Design-Entscheidung Batch D (Controller):** Die Erfassen-View nutzt **einen festen Entwurfs-Slot**: `const DRAFT_ID = 'expense-entry'; const DRAFT_NAME = 'Ausgabeeingabe';` (Konstanten in der Component). speichern = `save(DRAFT_ID, DRAFT_NAME, …)`, laden = `get(DRAFT_ID)`, verwerfen = `delete(DRAFT_ID)`. Mehrere Entwürfe sind **kein** Batch-D-Umfang (D8 kennt nur die 3 Verben).
- `db/snapshot.repository.ts` (read-only, exakte API):
  - `MasterDataSnapshot { accounts: Map<string, AccountDto>; pools: Map<string, PoolDto>; categories: Map<string, CategoryDto>; taxCategories: Map<string, TaxCategoryDto> }` — die DTO-Typen kommen aus `db/snapshot.repository.ts` selbst re-exportiert bzw. aus dem generierten Client (im Worktree prüfen, woher `snapshot.repository.ts` sie importiert).
  - `replace(snapshot: MasterDataSnapshot, fetchedAtUtc: string): Promise<void>` — atomares 4×DELETE + INSERT + Zeitstempel-Meta.
  - `loadSnapshot(): Promise<MasterDataSnapshot | null>` — `null` ohne Snapshot (kein Zeitstempel-Meta); leere Maps = gültiger leerer Snapshot.
  - `fetchedAt(): Promise<string | null>`.
  - `referenceCatalog(): Promise<ReferenceCatalog | null>` mit `ReferenceCatalog { accounts, pools, categories, taxCategories: Map<string, string> }` (id → status-String; `null` ohne Snapshot).
  - DTO-Felder (generiert, `useUnionTypes`): `AccountDto { id: string; name: string; type: string; isInterestBearing: boolean; status: string; baseRevision: number | string }`; `PoolDto { id; name; status: string; baseRevision: number | string }`; `CategoryDto { id; name; parentId: string | null; status: string; baseRevision: number | string; taxPreset: null | TaxPresetDto }`; `TaxCategoryDto { id; name; parentId: string | null; status: string; baseRevision: number | string }`; `TaxPresetDto { taxRelevant: boolean | null; taxCategoryId: string | null; taxCategoryName: string | null; inheritToChildren: boolean; baseRevision: number | string }`.
  - **Wichtig:** `status` ist `string` (keine `'active'|'archived`-Union) — Aktiv-Check ist `row.status === 'active'`.
  - `db/bookings.repository.ts` exportiert zudem `LocalExpenseInput`/`LocalSplitInput`/`LocalTaxInput`/`validateExpense`/`BookingValidationError` — s. o.
- `db/local-database.ts` (read-only): `LocalDatabase.init()` idempotent, `@Injectable providedIn:'root'`.

### Test-Infrastruktur (Muster 1:1 kopieren)

- **DOM-Spec-Muster (jsdom)** — exakt wie `setup.component.spec.ts` / `bookings.repository.spec.ts` (im Worktree lesen):
  - `// @vitest-environment jsdom` als erste Zeile.
  - `vi.mock('@capacitor/core', () => ({ Capacitor: { isNativePlatform: () => true } }));`
  - `vi.mock('@capacitor-community/sqlite', () => { ... })` mit allen 12 Plugin-Methoden, die durchleiten auf `capacitorSqliteMockFactory().CapacitorSQLite` (Muster in `bookings.repository.spec.ts` kopieren).
  - `import { capacitorSqliteMockFactory, dbState } from '../db/capacitor-sqlite-fake'` (relativ aus `entry/`).
  - `beforeEach`: `dbState.reset();` + `createEnvironmentInjector([LocalDatabase, MetaService, SnapshotRepository, BookingsRepository, DraftRepository], TestBed.inject(EnvironmentInjector)); await db.init();`
  - Assertions direkt gegen `dbState.*`-Maps (snake_case-Zeilen: `dbState.bookings`, `dbState.outbox`, `dbState.drafts`, `dbState.accounts`/`pools`/`categories`/`taxCategories` für den Snapshot).
  - `dbState.meta` ist ein `Map` (MetaService-Keys, z. B. `snapshot.fetched_at`); `dbState.drafts` ist ein `Map<string, FakeDraftRow>` (snake_case: `id/name/data/created_at_utc/updated_at_utc`).
- **Pure-Modell-Spec** (`entry-form.model.spec.ts`): normale node-Spec (kein jsdom, kein DB-Setup) — wie `sha256.spec.ts`.
- **Snapshot-Vorbereitung in Specs:** `const snapRepo = env.inject(SnapshotRepository); await snapRepo.replace({ accounts: new Map([['acc-1', { id:'acc-1', name:'Giro', type:'checking', isInterestBearing:false, status:'active', baseRevision:1 }]], ...), pools: ..., categories: ..., taxCategories: ... }, new Date().toISOString());` — exakter `replace`-Signatur gemäß `snapshot.repository.ts` folgen (im Worktree lesen; Map-basiert).

### Tooling

- TypeScript-strict, ESLint `prefer-inject` (keine Constructor-Parameter — `inject(...)` als Feld), keine `any`, keine unused Imports, 2-Space-Indent, Semikons.
- `cd frontend && npm run lint` grün; `cd frontend && npm test` (läuft `ng test host && ng test mobile`) grün; `cd frontend && npm run build:mobile` grün.

## Spezifikation je Datei

### 1) `app/amount.ts` (neu)

- `parseAmount` **1:1 aus `app.ts` hierher** (Dokumentation übernehmen, Semantik identisch).
- Neu: `export function formatCents(cents: number): string` — ganze Cent → Eingabe-Format ohne Währung: `formatCents(1250) === '12,50'`, `formatCents(1200) === '12'`, `formatCents(5) === '0,05'`. Nur für nicht-negative Ganzzahl-Centwerte verwenden (Quick-Actions); Implementierung mit `Math.floor(c/100)` + `'0'+...`-Padding.
- In `app.ts`: `export { parseAmount } from './amount';` (Re-Export behalten, damit `app.spec.ts`-Semantik erhalten bleibt; Import in `app.ts` selbst auf `./amount` umstellen) — **alternativ** Re-Export weglassen und `app.spec.ts` direkt von `./amount` importieren; **Entscheidung: Re-Export behalten** (kleinste Änderung an `app.ts`).

### 2) `entry/entry-form.model.ts` (neu) — pure Daten + pure Funktionen, KEIN Angular-Import

Form-Modell (plain, serialisierbar für Entwürfe):

```ts
export interface EntryTaxModel {
  relevant: boolean;
  taxCategoryId: string | null;
  taxCategoryName: string | null;
  markedAmountInput: string; // '' = Splitbetrag (Vollmarke)
  note: string;
}
export interface EntrySplitModel {
  key: string;            // stabile UI-ID ('s1', 's2', ... — bei Addition: nächster freier Index)
  amountInput: string;    // Raw-Text (parseAmount)
  poolId: string;
  categoryId: string;
  note: string;
  tax: EntryTaxModel;
}
export interface EntryFormModel {
  v: 1;                   // Entwurfs-Formatversion
  date: string;           // 'yyyy-MM-dd'
  amountInput: string;    // Raw-Text (parseAmount)
  accountId: string;
  note: string;
  splits: EntrySplitModel[];
}
```

Pure Funktionen (alle exportiert, keine Mutation des Eingabe-Modells — neue Objekte zurückgeben):

- `createDefaultForm(today: string): EntryFormModel` — `date: today`, `accountId: ''`, `note: ''`, 1 leeres Split (`key 's1'`, leere Felder, `tax` nicht relevant).
- `addSplit(model): EntryFormModel` — Split am Ende (Key = `s${nächster freier Index}`).
- `removeSplit(model, key): EntryFormModel` (min. 1 Split behalten: bei 1 Split nichts tun).
- `evenlySplit(model): EntryFormModel` — **nur wenn Total gültig (>0) und ≥1 Split**: `base = Math.floor(total/n)`, `rem = total - base*n`; die ersten `rem` Zeilen erhalten `base+1`, Rest `base` (alle als `formatCents`). Sonst: unverändert.
- `fillLastSplit(model): EntryFormModel` — **nur wenn Total gültig und ≥1 Split**: `rest = total - Σ(übrige gültige Split-Cent; ungültig/leer zählen als 0)`; `rest >= 1` → letztes `amountInput = formatCents(rest)`; `rest < 1` → letztes `amountInput = ''` (Validierung zeigt den Fehler). Sonst unverändert.
- `applyCategoryPreset(model, splitKey, preset: TaxPresetDto | null): EntryFormModel` (`TaxPresetDto` aus dem generierten Client importieren) — bei Kategorienwechsel: `preset` vorhanden **und** `preset.taxRelevant === true` → `tax.relevant = true`, `tax.taxCategoryId = preset.taxCategoryId`, `tax.taxCategoryName = preset.taxCategoryName`; `preset.taxRelevant === false` → `tax.relevant = false`, `taxCategoryId/taxCategoryName/markedAmountInput` leeren. `preset` null oder `taxRelevant === null` → unverändert (bestehende Markierung bleibt, überschreibbar durch den User).
- `setTaxRelevant(model, splitKey, relevant: boolean): EntryFormModel` — bei false: `taxCategoryId/taxCategoryName/markedAmountInput` leeren.
- `toLocalInput(model, snapshot: MasterDataSnapshot): LocalExpenseInput | null` — baut das `LocalExpenseInput` (nur wenn alle Felder gültig; sonst `null` — die Fehler liefert `validateEntryForm`):
  - `date` = `model.date`; `account = { id, name: snapshot.accounts.get(id)!.name }`; `amountCents = parseAmount(model.amountInput)!`; `note` = `model.note || null`.
  - Splits in Reihenfolge; `note: s.note || null`; `tax: s.tax.relevant ? { relevant: true, taxCategoryId, taxCategoryName, markedAmountCents: s.tax.markedAmountInput === '' ? null : parseAmount(...), note: s.tax.note || null } : { relevant: false, taxCategoryId: null, taxCategoryName: null, markedAmountCents: null, note: s.tax.note || null }`.
- `splitSumCents(model): number` — Summe der gültig geparsten Split-Beträge (ungültig/leer = 0; für Live-Anzeige).
- `totalCents(model): number | null` — `parseAmount(model.amountInput)` oder null.
- `validateEntryForm(model, snapshot: MasterDataSnapshot | null): EntryFormError[]` — **alle** Fehler sammeln (nicht nur der erste):
  - `EntryFormError = { code: string; message: string }`.
  - Codes (deutsche `message`):
    - `no_snapshot` — „Kein Stammdatensnapshot — bitte zuerst in der Einrichtung verbinden." (alle weiteren Referenz-Checks überspringen, wenn null)
    - `invalid_total` — Total fehlt/ungültig; `total_not_positive` — Total ≤ 0.
    - `invalid_date` — `date` matcht nicht `^\d{4}-\d{2}-\d{2}$` oder ist kein reales Kalenderdatum (z. B. `2026-02-30` ungültig; `2024-02-29` gültig). Rückdatierung erlaubt (keine Untergrenze), keine Obergrenze.
    - `no_account` — Konto nicht ausgewählt; `unknown_account` / `archived_account` — ID nicht im Snapshot bzw. status ≠ 'active'.
    - `no_splits` — keine Splits.
    - je Split (mit Index, z. B. „Split 1: …"): `invalid_split_amount` (fehlend/ungültig), `split_not_positive`, `unknown_pool`, `archived_pool`, `unknown_category`, `archived_category`.
    - `split_sum_mismatch` — `Σ splits ≠ total` (nur wenn beide Seiten gültig; Meldung mit beidem: „Splitsumme (X) stimmt nicht mit Total (Y) überein.").
    - je Split mit `tax.relevant`: `tax_category_missing` — „Steuerkategorie fehlt (Form-Regel: bei steuerlich relevant Pflicht)"; `unknown_tax_category` — ID nicht im Snapshot; `archived_tax_category`.
    - je Split mit `markedAmountInput !== ''`: `invalid_marked_amount` (ungültig), `marked_amount_range` — `< 0` oder `> Splitbetrag` (nur wenn Splitbetrag gültig; sonst `invalid_split_amount` reicht).
  - Reihenfolge der Prüfung frei, aber **deterministisch**.
- `serializeDraft(model): string` / `parseDraft(json: string): EntryFormModel | null` — JSON mit `v: 1`-Wächter: `v !== 1` → null; Struktur-Guards (splits ist Array, Pflichtfelder sind Strings/bool) — bei Verletzung → null.
- `todayInBerlin(): string` — `new Intl.DateTimeFormat('en-CA', { timeZone: 'Europe/Berlin' }).format(new Date())` (liefert `yyyy-MM-dd`).

### 3) `entry/entry-form.model.spec.ts` (neu, node)

Mind. diese Fälle (Referenzzahlen, wie im Projekt üblich — keine Implementationsdetails spiegeln):
1. `parseAmount`-Unverträglichkeiten sind NICHT hier (stehen in `app.spec.ts`) — stattdessen: `formatCents(1250)==='12,50'`, `formatCents(1200)==='12'`, `formatCents(5)==='0,05'`.
2. `evenlySplit`: Total `10,00` (1000 Cent), 3 Splits → Beträge `3,34 / 3,33 / 3,33` (Summe exakt 1000). Total `9,99`, 4 Splits → `2,50 / 2,50 / 2,49 / 2,50`? **Nein** — Deterministik: base=249, rem=3 → Zeilen 1–3 bekommen 250, Zeile 4 249 → `2,50/2,50/2,50/2,49`. Total ungültig → unverändert.
3. `fillLastSplit`: Total `25,00`, Split 1 `10,00` → Split 2 = `15,00`. Total `5,00`, Split 1 `10,00` → letztes `amountInput === ''`. Nur 1 Split → erhält das Total.
4. `applyCategoryPreset`: preset `taxRelevant: true` mit `taxCategoryId 'tx-1'` → relevant + ID + Name gesetzt; `taxRelevant: false` → relevant false + Felder geleert; preset null → unverändert (bestehende Markierung bleibt).
5. `setTaxRelevant(false)` leert `taxCategoryId/markedAmountInput`.
6. `validateEntryForm` (Snapshot mit 1 aktivem Konto/Pool/Kategorie/Steuerkategorie + 1 archivierten Konto):
   - Valides Modell (1 Split, Summe passt, steuerneutral) → `[]`.
   - Summenabweichung (Total 10,00 / Split 9,99) → genau `split_sum_mismatch` enthalten.
   - Total `0` → `total_not_positive`; Total `abc` → `invalid_total`.
   - Datum `2026-02-30` → `invalid_date`; `2024-02-29` → kein `invalid_date`.
   - Konto `''` → `no_account`; unbekannt → `unknown_account`; archiviert → `archived_account`.
   - Split: ungültig `12,345` → `invalid_split_amount`; Summe 0 Splits → `no_splits`.
   - Steuer: relevant ohne Kategorie → `tax_category_missing`; relevante Kategorie nicht im Snapshot → `unknown_tax_category`; `markedAmountInput '99,99'` bei Split `1,00` → `marked_amount_range`; `''` → kein Fehler (= Vollmarke).
   - `no_snapshot` bei `snapshot === null`.
7. `toLocalInput`: valides Modell → exaktes `LocalExpenseInput`-Objekt (deep-equal, `note` null bei leer, tax-Objekt korrekt inkl. `markedAmountCents: null` bei `''`); ungültiges Modell → `null`.
8. `parseDraft`: gültiges JSON → identisches Modell; `v: 2` → null; kaputtes JSON → null; `splits` kein Array → null.
9. `todayInBerlin()` matcht `^\d{4}-\d{2}-\d{2}$`.

### 4) `entry/entry.component.ts` (neu)

- `@Component({ selector: 'app-entry', imports: [FormsModule], templateUrl: './entry.html' })` — **ohne** styleUrl, zoneless (Signals, kein `markForCheck`/NgZone — Muster `app.ts`/`setup.component.ts`).
- `inject`: `snapshotRepository`, `bookingsRepository` (`BookingsRepository`), `draftRepository` (`DraftRepository`).
- Signals:
  - `form = signal<EntryFormModel>(createDefaultForm(todayInBerlin()))`
  - `snapshot = signal<MasterDataSnapshot | null>(null)`
  - `snapshotError = signal<string | null>(null)` (z. B. wenn loadSnapshot wirft)
  - `saving = signal(false)` — Doppeltipp-Sperre (Design D7)
  - `saveStatus = signal<'idle' | 'saved' | 'error'>('idle')`
  - `saveMessage = signal<string | null>(null)`
  - `errors = computed(() => validateEntryForm(this.form(), this.snapshot()))`
  - `totalCents = computed(() => totalCents(this.form()))`, `splitSum = computed(() => splitSumCents(this.form()))`
- `ngOnInit`: `this.snapshot.set(await this.snapshotRepository.loadSnapshot())` in try/catch (Fehler → `snapshotError`, `snapshot` bleibt null).
- Mutator-Methoden (alle setzen `form` mit der puren Modellfunktion, nie in-place-Mutation): `updateField(patch)`, `addSplit()`, `removeSplit(key)`, `updateSplit(key, patch)`, `updateSplitTax(key, taxPatch)`, `onSplitCategoryChange(key, categoryId)` (setzt categoryId **und** wendet `applyCategoryPreset` mit `snapshot.categories.get(categoryId)?.taxPreset ?? null` an), `quickEvenly()`, `quickFillLast()`, `setTaxRelevant(key, relevant)`.
- `save()` (D7 — bestätigen erst nach Commit):
  1. `if (this.saving()) return;` (Doppeltipp-Sperre)
  2. `if (validateEntryForm(this.form(), this.snapshot()).length > 0)` → `saveStatus='error'`, `saveMessage='Bitte Eingabe korrigieren (Fehler unten)'.` — keine DB-Operation.
  3. `const input = toLocalInput(...)!` (nach 2. garantiert nicht null).
  4. `saving = true; saveStatus='idle'; saveMessage=null;`
  5. `try { const record = await this.bookingsRepository.save(input);` → `saveStatus='saved'`, `saveMessage='Ausgabe gespeichert (Buchung ' + record.id + ') — lokal in der Outbox.'` — **Form zurücksetzen, Konto behalten**: `form.set({ ...createDefaultForm(todayInBerlin()), accountId: this.form().accountId })`.
  6. `catch (e)` → `saveStatus='error'`, `saveMessage = e instanceof BookingValidationError ? e.message : 'Speichern fehlgeschlagen: ' + (e as Error).message`.
  7. `finally { saving = false; }`
- Entwürfe (D9):
  - `saveDraft()`: `await this.draftRepository.save(DRAFT_ID, DRAFT_NAME, serializeDraft(this.form()), new Date())` → `saveMessage='Entwurf gespeichert.'`, `saveStatus='saved'`; Fehler → `saveStatus='error'` + Meldung. **Keine Validierung** (Teilzustand ist erlaubt — das ist der Zweck des Entwurfs).
  - `loadDraft()`: `const d = await this.draftRepository.get(DRAFT_ID)`; null → `saveStatus='error'`, `saveMessage='Kein Entwurf vorhanden.'`; sonst `parseDraft(d.data)` → null → `await this.draftRepository.delete(DRAFT_ID)`, `saveStatus='error'`, `saveMessage='Entwurf beschädigt — verworfen.'`; gültig → `form.set(parsed)`, `saveStatus='saved'`, `saveMessage='Entwurf geladen.'`.
  - `discardDraft()`: `await this.draftRepository.delete(DRAFT_ID)` → `saveStatus='saved'`, `saveMessage='Entwurf verworfen.'`.
  - Konstanten (Modul-Ende der Component): `const DRAFT_ID = 'expense-entry'; const DRAFT_NAME = 'Ausgabeeingabe';`
- Snapshot-Optionen für die Selects (computed): `accounts` (aktiv, nach Name sortiert), `pools`, `categories`, `taxCategories` — je `{ id, label }`-Arrays.

### 5) `entry/entry.html` (neu) — schlichtes Markup, KEIN CSS (Styling Batch E)

Struktur (deutsche Beschriftungen, FormsModule-Bindings wie `app.ts`/`setup.html`):
- H2 „Erfassen".
- Block **Grunddaten**: Datum (type=date, `[ngModel]`), Betrag (text, placeholder `0,00`), Konto (select aus `accounts`, leerer Eintrag „— bitte wählen —"), Notiz (text).
- Block **Splits** (je Split eine Sektion mit `*ngFor` über `form().splits`):
  - Betrag (text), Pool (select `pools`), Kategorie (select `categories`, `(ngModelChange)` → `onSplitCategoryChange`), Notiz.
  - Steuer-Unterblock: Checkbox „steuerlich relevant" (`setTaxRelevant`); **bei relevant** (ngIf): Steuerkategorie (select `taxCategories`), Markierter Betrag (text, placeholder = Splitbetrag als `formatCents`, Hilfe-Text „leer = voller Splitbetrag"), Steuer-Notiz.
  - Button „Split entfernen" (disabled bei nur 1 Split).
- Button „Split hinzufügen".
- Block **Schnellaktionen**: Button „Gleichmäßig" (`quickEvenly`), Button „Rest in letzten Split" (`quickFillLast`); beide disabled, wenn `totalCents()` ungültig/≤0 oder keine Splits.
- **Live-Summenabgleich** (immer sichtbar): „Total: {{…}} € | Splitsumme: {{…}} €" + bei Abweichung eine Zeile „Abweichung: {{differenz}} €" (beides als formatCents; bei ungültigem Total „—").
- **Fehlerliste** (ngIf `errors.length`): `<ul>` mit allen `errors().message` (D7: live, vor dem Speichern).
- Block **Speichern**: Button „Ausgabe speichern" (disabled bei `saving()` oder `snapshot() === null`), Status-Zeile `saveMessage` (bei `saveStatus !== 'idle'`), `snapshotError`-Anzeige.
- Block **Entwurf**: Buttons „Entwurf speichern", „Entwurf laden", „Entwurf verwerfen".
- Hinweiszeile bei `snapshot() === null`: „Kein Stammdatensnapshot — bitte zuerst in der Einrichtung verbinden."

### 6) `entry/entry.component.spec.ts` (neu, jsdom)

Muster siehe oben (vi.mock-Blöcke, `dbState.reset()`, env-Injector mit `[LocalDatabase, MetaService, SnapshotRepository, BookingsRepository, DraftRepository]`, `await db.init()`). Snapshot-Vorbereitung: `snapshotRepository.replace(...)` mit aktivem Konto `acc-1` („Giro"), Pool `pool-1`, Kategorie `cat-1` (taxPreset null), Steuerkategorie `tx-1` („Sachbezug"), je `baseRevision: 1`, `fetchedAtUtc` = ISO.

Component per `TestBed.configureTestingModule({ imports: [EntryComponent] })` + `TestBed.inject(EntryComponent)` — **Direktmethodentests** (wie `setup.component.spec.ts`), plus 1 Rendering-Test mit `TestBed.createComponent` für die Summenanzeige.

Tests (mind.):
1. **Gültige Ausgabe (2 Splits + Steuer) wird gespeichert:** Form setzen (Total `30,00`, Konto `acc-1`, Split 1 `10,00` + tax relevant mit `tx-1` und `markedAmountInput ''`, Split 2 `20,00` neutral) → `await comp.save()` → `dbState.bookings` hat **1** Zeile (`total_amount_cents === 3000`, `account_id === 'acc-1'`); `dbState.splits` hat **2** Zeilen (`FakeSplitRow`, snake_case: `amount_cents` 1000/2000, `pool_id`/`category_id` korrekt; Split 1: `tax_relevant === 1`, `tax_category_id === 'tx-1'`, `marked_amount_cents === null` (leer = Vollmarke); Split 2 neutral: `tax_relevant` wie vom Repository geschrieben (0 oder null — aus dem Repository-Code ablesen, nicht raten)); `dbState.outbox` hat 1 Zeile (`status === 'queued'`, `booking_id` = Buchungs-ID); `saveStatus()==='saved'`, Form reset (amountInput `''`, 1 leeres Split, **Konto `acc-1` bleibt**).
2. **Ungültige (Summenabweichung) wird NICHT gespeichert:** Total `30,00`, ein Split `10,00` → `await comp.save()` → `saveStatus()==='error'`, `dbState.bookings.size === 0`, `dbState.outbox.size === 0`.
3. **Doppeltipp → 1 Buchung:** `const p = comp.save(); await comp.save(); await p;` (gültige Form) → `dbState.bookings.size === 1`.
4. **Entwurfs-Flow:** Form füllen (Total `5,00`, 2 Splits, tax relevant mit `tx-1`) → `await comp.saveDraft()` → `dbState.drafts.size === 1`; Form ändern (Total `9,99`) → `await comp.loadDraft()` → `form().amountInput === '5,00'`, 2 Splits, tax relevant; `await comp.discardDraft()` → `dbState.drafts.size === 0`; `await comp.loadDraft()` → `saveMessage()` enthält „Kein Entwurf".
5. **Kategorie-Preset-Übernahme:** Snapshot-Kategorie `cat-2` mit `taxPreset: { taxRelevant: true, taxCategoryId: 'tx-1', taxCategoryName: 'Sachbezug', inheritToChildren: true, baseRevision: 1 }`; Split-Kategorie auf `cat-2` (`comp.onSplitCategoryChange`) → `tax.relevant === true` und `taxCategoryId === 'tx-1'`; danach manuell `setTaxRelevant(false)` → Felder geleert (überschreibbar).
6. **Schnellaktionen in der Component:** Total `10,00`, 3 leere Splits → `quickEvenly()` → Beträge `3,34/3,33/3,33`; Total `25,00`, Split 1 `10,00` → `quickFillLast()` → Split 2 `15,00`.
7. **Ohne Snapshot:** `loadSnapshot` → null (kein `replace`) → `await comp.ngOnInit()` (bzw. die Init-Methode) → `snapshot() === null`; `comp.save()` → `saveStatus()==='error'`, keine Buchung; `errors()` enthält `no_snapshot`-Meldung.
8. **Rendering (1 Test):** `TestBed.createComponent(EntryComponent)`, Snapshot + gültige Form mit Summenabweichung setzen, `changeDetectionRef.detectChanges()` → DOM enthält die Abweichungszahl (z. B. `Abweichung`-Text).
9. **Steuer-Fehlerpfad:** relevant ohne Steuerkategorie → `errors()` enthält die `tax_category_missing`-Meldung und `save()` speichert nicht.

### 7) `app.ts` / 8) `app.spec.ts` (geändert, minimal)

- `app.ts`: `parseAmount`-Definition löschen, stattdessen `import { parseAmount } from './amount';` + `export { parseAmount };` (alle anderen Verwendungen bleiben).
- `app.spec.ts`: `import { App } from './app'; import { parseAmount } from './amount';` (bzw. Re-Export nutzen — **Entscheidung: direkter Import aus `./amount`**).
- Beide Dateien: **keine weitere Änderung** — bestehende Tests müssen unverändert grün bleiben.

### 9) `capacitor-sqlite-fake.ts` (nur falls nötig)

Vorher prüfen: `dbState.drafts` und die Draft-Statements (`SELECT_DRAFTS`/`SELECT_DRAFT_BY_ID`/`UPSERT_DRAFT`/`DELETE_DRAFT` in `statements.ts`, von `drafts.repository.ts` importiert) existieren aus Batch B. Erwartung: ja → **Datei nicht anfassen**. Falls nein → wie bei Batch B ergänzen (Map + Matcher); Befund dokumentieren.

## Definition of Done

- [ ] Exakt die Dateien der Dateiliste geändert/neu (prüfen: `git status --short` — nichts anderes; `capacitor-sqlite-fake.ts` nur falls Befund oben zuschlägt).
- [ ] `cd frontend && npm run lint` → grün.
- [ ] `cd frontend && npm test` → ALLE Tests grün (bestehende + neue; Zahlen berichten).
- [ ] `cd frontend && npm run build:mobile` → grün.
- [ ] Keine Abhängigkeits-Änderung (`package.json`/Lockfiles unverändert).
- [ ] `src/generated/`, `db/*` (außer ggf. Fake), `setup/*`, `app/app.html`, `sqlite.service.ts` unverändert.
- [ ] Commits auf Branch `step/8d-entry` (klein + aussagekräftig, z. B. `refactor(mobile): parseAmount nach amount.ts extrahieren (+formatCents)`, `feat(mobile): Batch D — Entry-Form-Modell (Validierung, Schnellaktionen, Entwürfe)`, `feat(mobile): Batch D — Erfassen-Component`, `test(mobile): Batch D — Specs`).
- [ ] **Kein** Push, **kein** PR.
- [ ] Am Ende: kurze Zusammenfassung (was implementiert, Abweichungen von dieser Spec + Gründe, Test-Ergebnisse mit Zahlen, offene Punkte).
