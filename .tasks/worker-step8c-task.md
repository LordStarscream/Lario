# Worker-Task: Schritt 8, Batch C — Snapshot + Setup (mobile)

**Worktree:** `/tmp/lario-step8c` (Branch `step/8c-snapshot-setup`, HEAD `27191db` = main + Batch A + Batch B).
**Nur in diesem Worktree arbeiten.** Commits am Ende: `feat(mobile): Batch C — Snapshot + Setup` (kleine logische Commits erlaubt).

## Kontext

Lario, Schritt 8 (produktive mobile Offline-Erfassung). Batch A (Speicherfundament) und Batch B (Outbox + Buchung) sind gemerged. Batch C bringt:
1. `HostClient` (Login, Stammdaten-Liste, Health, Logout) auf Basis des **generierten** OpenAPI-Clients.
2. `SnapshotRepository` (atomarer Snapshot-Wechsel in der lokalen SQLite-DB).
3. Einrichtung-Component (`app-setup`, eigenständig, **noch nicht** im App-Root eingebunden — das ist Batch E).
4. Referenzvalidierung in `BookingsRepository` (Konto/Pool/Kategorie gegen Snapshot: Existenz + Status `active`).

**Maßgebliche Design-Dokumente** (in DEINEM Worktree lesbar):
- `.tasks/step-8-mobile-offline.md`, Abschnitt „Batch C — Snapshot + Setup (P3)" inkl. „Design-Entscheidungen Batch C (26.10.)".
- `.pi/memory/`-Notizen und `docs/Finanzplanung_Implementierungsplan.md` (Schritt 8) bei Bedarf.

Widerspruch zwischen dieser Datei und der Task-Datei → Task-Datei gewinnt; Befund dokumentieren.

## Verbotenes / Abgrenzung

- **Kein** neuer Host-Endpoint. Die 4 Stammdaten-Endpoints (`GET /api/v1/accounts|pools|categories|tax-categories`) existieren bereits auf dem Host; es gibt **kein** `/snapshot`-Endpoint.
- Kein Senden/Outbox-Sync (Schritt 9). Kein UI-Routing/Tab-Shell (Batch E). Kein Android-Geräte-Test (Batch E).
- Keine `schema.spec.ts`-Änderung (B10 ist ein eigener offener Punkt).
- Keine Dependency-Änderung, kein Regenerieren des generierten Clients (`npm run generate:client` **nicht** ausführen — der Client in `src/generated/` ist vollständig, inkl. `listAccounts/listPools/listCategories/listTaxCategories`, `authLogin/authLogout/getHealth`, `LoginRequest/LoginResponse`).
- `src/generated/` ist **read-only** für diesen Task.
- Keine Refactorings außerhalb der geänderten Dateien. `validateExpense` bleibt in `bookings.repository.ts` (keine Extraktion in `booking-validation.ts` — die Datei existiert nicht; Abweichung von der Task-Datei-Dateiliste, bewusst).

## Dateiliste (11 Dateien: 7 neu, 4 geändert)

Neu:
1. `frontend/apps/mobile/src/app/db/snapshot.repository.ts`
2. `frontend/apps/mobile/src/app/db/snapshot.repository.spec.ts`
3. `frontend/apps/mobile/src/app/setup/host.client.ts`
4. `frontend/apps/mobile/src/app/setup/host.client.spec.ts`
5. `frontend/apps/mobile/src/app/setup/setup.component.ts`
6. `frontend/apps/mobile/src/app/setup/setup.html`
7. `frontend/apps/mobile/src/app/setup/setup.component.spec.ts`

Geändert:
8. `frontend/apps/mobile/src/app/db/statements.ts` (Snapshot-Statements)
9. `frontend/apps/mobile/src/app/db/capacitor-sqlite-fake.ts` (4 neue Tabellen + Matcher)
10. `frontend/apps/mobile/src/app/db/bookings.repository.ts` (Referenzvalidierung + `getDeviceId()`)
11. `frontend/apps/mobile/src/app/db/bookings.repository.spec.ts` (neue Tests)

**Keine Datei außerhalb dieser Liste anfassen** (DoD).

## Verifizierte API-Fakten (Nicht erraten — so ist es im Code)

### Generierter Client (`frontend/apps/mobile/src/generated/`)
- Import: `import { LarioHostService, MasterDataEndpointsService, OpenAPI, ApiError } from '../../generated/index.js'` (aus `setup/`); aus `db/` ist der Client nicht nötig.
- `OpenAPI` ist ein **mutable Objekt** (`core/OpenAPI.ts`): `OpenAPI.BASE` (Default `'http://localhost:8080'`) und `OpenAPI.TOKEN` (`string | undefined`) werden zur Laufzeit gesetzt. `TOKEN` akzeptiert auch Resolver, wir setzen plain string / `undefined`.
- `LarioHostService.authLogin(requestBody?: null | LoginRequest): CancelablePromise<LoginResponse>`; `LoginRequest { username: string | null; password: string | null }`; `LoginResponse { token: string; expiresInMinutes: number | string }`. 401 → `ApiError` (Status 401).
- `LarioHostService.authLogout(): CancelablePromise<void>` (Bearer, 204).
- `LarioHostService.getHealth(): CancelablePromise<HealthResponse>` (`GET /api/health`, **kein** Bearer nötig).
- `MasterDataEndpointsService.listAccounts(): CancelablePromise<Array<AccountDto>>`; `listPools() → Array<PoolDto>`; `listCategories() → Array<CategoryDto>`; `listTaxCategories() → Array<TaxCategoryDto>`. Alle Bearer (TOKEN muss gesetzt sein).
- DTOs (exakte Felder):
  - `AccountDto { id: string; name: string; type: string; isInterestBearing: boolean; status: string; baseRevision: number | string }`
  - `PoolDto { id: string; name: string; status: string; baseRevision: number | string }`
  - `CategoryDto { id: string; name: string; parentId: string | null; status: string; baseRevision: number | string; taxPreset: null | TaxPresetDto }`
  - `TaxCategoryDto { id: string; name: string; parentId: string | null; status: string; baseRevision: number | string }`
  - `TaxPresetDto { taxRelevant: boolean | null; taxCategoryId: string | null; taxCategoryName: string | null; inheritToChildren: boolean; baseRevision: number | string }`
- Fehler: nicht-2xx → `ApiError` mit `.status: number` (und `.message`). Netzwerkfehler (fetch wirft) → `ApiError` mit Status `0` oder generischer Fehler — robust behandeln: `catch (e)` → wenn `e instanceof ApiError` und `e.status > 0` → `http_error`, sonst → `http_unreachable`.

### LocalDatabase / MetaService (`db/`)
- `LocalDatabase` (`@Injectable`, `providedIn: 'root'`): `init(): Promise<void>` (idempotent), `executeSet(sets: SqlSet[]): Promise<void>` (serialisiert; atomar), `query<T>(statement, values?): Promise<T[]>`, `withWriteTransaction(cb)`. `SqlSet = { statement: string; values?: unknown[] }` (aus `statements.ts`).
- `MetaService`: `get(key): Promise<string | null>`, `set(key, value): Promise<void>`, `delete(key): Promise<void>`.
- `bookings.repository.ts` nutzt `inject(...)` als Feld (ESLint `prefer-inject` — **keine** Constructor-Parameter).

### Fake (`capacitor-sqlite-fake.ts`) — Erweiterungspunkte
- `CapacitorSqliteFakeState` (Typ in `createCapacitorSqliteFakeState`-Bereich) + `createFakeState()`: 4 neue Maps hinzufügen: `accounts`, `pools`, `categories`, `taxCategories` (Zeilen als snake_case-Objekte, analog `bookings`).
- `StateTables` = `Pick<...>`-Liste um die 4 Maps ergänzen.
- `FakeTableSnapshot` + `takeSnapshot` + `restoreSnapshot`: 4 Maps ergänzen (tiefe Kopie wie bei `bookings`).
- `reset()` und `simulateRestart()`: setzen Felder **einzeln** — dort die 4 `new Map()` ergänzen.
- `applySet(...)`: neue Matcher vor dem finalen `throw` einfügen (Muster: `if (stmt === normalize(CONST)) { ...; return; }`):
  - `DELETE_SNAPSHOT_ACCOUNTS/POOLS/CATEGORIES/TAX_CATEGORIES` → `map.clear()`.
  - `INSERT_SNAPSHOT_*` → NOT-NULL-Prüfung der Pflichtspalten (Muster `INSERT_BOOKING`), PK-Kollision → `constraintError(table, 'id', value)`, Zeile setzen.
- `runQuery(...)`: neue Matcher:
  - `SELECT_SNAPSHOT_ACCOUNTS/POOLS/CATEGORIES/TAX_CATEGORIES` → komplette Zeilen (Array, unsortiert ok).
  - `SELECT_ACCOUNT_STATUSES/POOL_STATUSES/CATEGORY_STATUSES/TAX_CATEGORY_STATUSES` → `{ id, status }`-Zeilen.

### BookingsRepository (`bookings.repository.ts`)
- `validateExpense(input)` (exportiert, pure) — bestehende Reasons: `invalid_date | invalid_amount | invalid_account | no_splits | invalid_split_amount | invalid_pool | invalid_category | split_sum_mismatch | tax_inconsistent | invalid_marked_amount`.
- `doSave`: `validateExpense(input)` → `getOrCreateDeviceId()` → seq → **ein** `executeSet`.
- `update`: `validateExpense(input)` → normalisieren → `withWriteTransaction`.
- `getOrCreateDeviceId()` ist privat; `DEVICE_ID_META_KEY = 'device.id'` exportiert.
- `BookingValidationError(reason, message)` — Reason-Union muss um die Batch-C-Reasons erweitert werden.

### DOM-Spec-Muster (jsdom) — exakt so wie `bookings.repository.spec.ts`
- `// @vitest-environment jsdom` als erste Zeile.
- `vi.mock('@capacitor/core', () => ({ Capacitor: { isNativePlatform: () => true } }));`
- `vi.mock('@capacitor-community/sqlite', () => { const mock = () => capacitorSqliteMockFactory().CapacitorSQLite; return { CapacitorSQLite: { createConnection: (opts) => mock().createConnection(opts), ... } satisfies CapacitorSqliteMock }; });` (alle 12 Plugin-Methoden durchleiten, Muster in `bookings.repository.spec.ts` Zeilen ~24–48 und `local-database.spec.ts` kopieren).
- Imports: `import { EnvironmentInjector, createEnvironmentInjector } from '@angular/core'` + `import { TestBed } from '@angular/core/testing'` + `import { capacitorSqliteMockFactory, dbState, type CapacitorSqliteMock } from './capacitor-sqlite-fake'` (relativ).
- `beforeEach(async () => { dbState.reset(); const env = createEnvironmentInjector([LocalDatabase, MetaService, SnapshotRepository, BookingsRepository], TestBed.inject(EnvironmentInjector)); ...; await db.init(); });`
- Assertions direkt gegen `dbState.*`-Maps (snake_case-Zeilen).

### tsconfig / Tooling
- Specs: `tsconfig.spec.json` (node-Typen sind bereits ergänzt). Vitest läuft pro Datei; `// @vitest-environment`-Kommentar steuert jsdom/node.
- ESLint: `prefer-inject`, keine `any`, keine unused Imports, 2-Space-Indent, Semikons. `npm run lint` muss grün bleiben.
- Testlauf: `cd frontend && npm test -- --project mobile` (bzw. das in `package.json` definierte Skript; `npm test` mit Projektfilter für mobile, host-Projekt darf nicht gebrochen werden).

## Spezifikation je Datei

### 1) `db/statements.ts` — Ergänzung (Batch-C-Sektion am Ende)

```ts
// Batch C — Stammdaten-Snapshot (Design D3)

/** Alle Konten des Snapshots löschen (vor neuem Replace). */
export const DELETE_SNAPSHOT_ACCOUNTS = 'DELETE FROM accounts';
/** Alle Pools des Snapshots löschen (vor neuem Replace). */
export const DELETE_SNAPSHOT_POOLS = 'DELETE FROM pools';
/** Alle Kategorien des Snapshots löschen (vor neuem Replace). */
export const DELETE_SNAPSHOT_CATEGORIES = 'DELETE FROM categories';
/** Alle Steuerkategorien des Snapshots löschen (vor neuem Replace). */
export const DELETE_SNAPSHOT_TAX_CATEGORIES = 'DELETE FROM tax_categories';

/** Konto in den Snapshot schreiben. */
export const INSERT_SNAPSHOT_ACCOUNT =
  'INSERT INTO accounts (id, name, type, is_interest_bearing, status, base_revision) VALUES (?, ?, ?, ?, ?, ?)';
/** Pool in den Snapshot schreiben. */
export const INSERT_SNAPSHOT_POOL =
  'INSERT INTO pools (id, name, status, base_revision) VALUES (?, ?, ?, ?)';
/** Kategorie in den Snapshot schreiben (`tax_preset_json` NULL oder JSON). */
export const INSERT_SNAPSHOT_CATEGORY =
  'INSERT INTO categories (id, name, parent_id, status, base_revision, tax_preset_json) VALUES (?, ?, ?, ?, ?, ?)';
/** Steuerkategorie in den Snapshot schreiben. */
export const INSERT_SNAPSHOT_TAX_CATEGORY =
  'INSERT INTO tax_categories (id, name, parent_id, status, base_revision) VALUES (?, ?, ?, ?, ?)';

/** Alle Snapshot-Konten (vollständige Zeilen). */
export const SELECT_SNAPSHOT_ACCOUNTS =
  'SELECT id, name, type, is_interest_bearing, status, base_revision FROM accounts';
/** Alle Snapshot-Pools (vollständige Zeilen). */
export const SELECT_SNAPSHOT_POOLS = 'SELECT id, name, status, base_revision FROM pools';
/** Alle Snapshot-Kategorien (vollständige Zeilen). */
export const SELECT_SNAPSHOT_CATEGORIES =
  'SELECT id, name, parent_id, status, base_revision, tax_preset_json FROM categories';
/** Alle Snapshot-Steuerkategorien (vollständige Zeilen). */
export const SELECT_SNAPSHOT_TAX_CATEGORIES =
  'SELECT id, name, parent_id, status, base_revision FROM tax_categories';

/** Existenz-/Status-Katalog Konto (Referenzvalidierung). */
export const SELECT_ACCOUNT_STATUSES = 'SELECT id, status FROM accounts';
/** Existenz-/Status-Katalog Pool (Referenzvalidierung). */
export const SELECT_POOL_STATUSES = 'SELECT id, status FROM pools';
/** Existenz-/Status-Katalog Kategorie (Referenzvalidierung). */
export const SELECT_CATEGORY_STATUSES = 'SELECT id, status FROM categories';
/** Existenz-/Status-Katalog Steuerkategorie (Referenzvalidierung). */
export const SELECT_TAX_CATEGORY_STATUSES = 'SELECT id, status FROM tax_categories';
```

### 2) `db/snapshot.repository.ts` (neu)

`@Injectable({ providedIn: 'root' })`, `private readonly db = inject(LocalDatabase);`

**Typen** (DTO-Typen aus `../generated/index.js` wiederverwenden, keine Duplikate):
```ts
import type { AccountDto, CategoryDto, PoolDto, TaxCategoryDto } from '../generated/index.js';

export interface MasterDataSnapshot {
  accounts: Map<string, AccountDto>;
  pools: Map<string, PoolDto>;
  categories: Map<string, CategoryDto>;
  taxCategories: Map<string, TaxCategoryDto>;
}
/** id → status je Stammdaten-Tabelle (für die Referenzvalidierung). */
export interface ReferenceCatalog {
  accounts: Map<string, string>;
  pools: Map<string, string>;
  categories: Map<string, string>;
  taxCategories: Map<string, string>;
}
```

**Meta-Key:** `static readonly FETCHED_AT_META_KEY = 'snapshot.fetched_at';` (UTC-ISO-String).

**`replace(snapshot: MasterDataSnapshot, fetchedAtUtc: string): Promise<void>`**
EINE `executeSet` (all-or-nothing, Design D3):
1. 4× DELETE (Konten, Pools, Kategorien, Steuerkategorien).
2. INSERT je Zeile — Werte exakt:
   - Account: `[id, name, type, isInterestBearing ? 1 : 0, status, Number(baseRevision)]`
   - Pool: `[id, name, status, Number(baseRevision)]`
   - Category: `[id, name, parentId, status, Number(baseRevision), taxPreset === null ? null : JSON.stringify(taxPreset)]`
   - TaxCategory: `[id, name, parentId, status, Number(baseRevision)]`
3. `upsertMetaSet(FETCHED_AT_META_KEY, fetchedAtUtc)` (aus `statements.ts` importiert).

**`loadSnapshot(): Promise<MasterDataSnapshot | null>`**
- `fetched_at` via `MetaService`? Nein — direkt über `db.query(selectMetaSet(key), [key])` ODER `MetaService` injecten (beides ok; MetaService ist der etablierte Weg — `inject(MetaService)`).
- 4× `db.query(SELECT_SNAPSHOT_*)`.
- Zeilen → DTOs mappen: `is_interest_bearing: 0/1 → boolean`; `base_revision → Number(...)`; `parent_id: null | string` (leeres Ergebnis → null); `tax_preset_json: null | JSON.parse(...)`.
- Ergebnis: `null` wenn **keine** Zeilen in allen 4 Tabellen **und** kein `fetched_at`-Meta. Sonst `MasterDataSnapshot` mit Maps (leere Tabellen sind erlaubt — gültiger leerer Snapshot).

**`referenceCatalog(): Promise<ReferenceCatalog | null>`**
- `fetched_at` fehlt → `null` (kein Snapshot geladen).
- Sonst 4× `db.query(SELECT_*_STATUSES)` → `Map<string, string>` (id → status).

### 3) `setup/host.client.ts` (neu)

Klasse `HostClient` (`@Injectable({ providedIn: 'root' })`), `private readonly meta = inject(MetaService);`

**Meta-Keys** (Design D3/D8):
```ts
export const AUTH_META_KEYS = {
  host: 'auth.host',
  username: 'auth.username',
  password: 'auth.password',
  expiresAt: 'auth.expires_at',
} as const;
```
(Doku-Limitation in Code-Kommentar: Passwort liegt gemäß Design D3 „lokal gemerkt" als Klartext in der lokalen Geräte-DB — bewusste Entscheidung, Schritt-8-Scope.)

**Fehler:**
```ts
export type HostClientErrorCode =
  | 'http_unreachable'   // Netzwerkfehler / Host nicht erreichbar (auch Login-Netzwerkfehler)
  | 'http_error'         // Host antwortet mit Fehlerstatus (401, 500, …)
  | 'bad_response';      // Antwort unlesbar /unexpectede Struktur (kein Token, keine Arrays)
export class HostClientError extends Error {
  constructor(readonly code: HostClientErrorCode, message: string) { super(message); this.name = 'HostClientError'; }
}
```
Fehlermeldungen auf Deutsch, UI-tauglich (die Setup-View zeigt `error.message`), z. B. `Host nicht erreichbar`, `Anmeldung fehlgeschlagen (401)`, `Antwort des Hosts unlesbar`.

**`fetchMasterData(host: string, username: string, password: string, now: Date): Promise<{ snapshot: MasterDataSnapshot; fetchedAtUtc: string; expiresInMinutes: number }>`**

Fluss (Design-Entscheidung 26.10. „Fluss connect", Netzwerk-Teil):
1. `OpenAPI.BASE = host.trim();`
2. `LarioHostService.authLogin({ username, password })`
   - `ApiError` mit `status > 0` → `HostClientError('http_error', \`Anmeldung fehlgeschlagen (${status})\`)`; sonst → `HostClientError('http_unreachable', 'Host nicht erreichbar')`.
   - Erfolg: `login.token` muss nicht-leerer String sein, sonst `bad_response`.
3. `OpenAPI.TOKEN = login.token;`
4. `Promise.all([listAccounts(), listPools(), listCategories(), listTaxCategories()])`
   - `ApiError` mit `status > 0` → `http_error`; sonst `http_unreachable`.
   - Ergebnis-Arrays müssen Arrays sein (sonst `bad_response`).
5. Maps bauen: `new Map(arr.map(d => [d.id, d]))` je Typ → `snapshot`.
6. `fetchedAtUtc = now.toISOString();` → Return `{ snapshot, fetchedAtUtc, expiresInMinutes: Number(login.expiresInMinutes) }`.

**Trennscharfe Regel:** `fetchMasterData` macht **keinen** DB-Schrieb und setzt **keine** auth-Meta. Die Setup-Component orchestriert (s. u.): `fetchMasterData` → `SnapshotRepository.replace(snapshot, fetchedAtUtc)` → auth-Meta setzen. Fehler **nach** erfolgreichem Login (Schritt 4/5): Komponente löst `HostClient.logout(...)` best-effort aus, schreibt keine auth-Meta, Snapshot bleibt unverändert (replace all-or-nothing). Begründung: HostClient bleibt DB-schreibfrei und ohne Netzwerk-Abhängigkeit testbar.

**Logout + auth-Meta (im HostClient):**
   - `async logout(host: string, username: string, password: string): Promise<void>` — best-effort: `OpenAPI.BASE = host; authLogin(...)` (neues Token) → `OpenAPI.TOKEN = token; authLogout()` → `OpenAPI.TOKEN = undefined`. **Alle Fehler swallowing** (loggen via `console.warn`), außer: wenn der Login selbst fehlschlägt → einfach `OpenAPI.TOKEN = undefined` und Return. (Design: „best-effort Re-Login + authLogout (Fehler ignoriert)".)
   - `clearAuthMeta(): Promise<void>` — löscht die 4 auth-Meta-Keys (über MetaService).
   - `loadAuthMeta(): Promise<{ host: string; username: string; password: string; expiresAt: string } | null>` — liest die 4 Keys; einer fehlt → `null`. (Für „lokal gemerkt" in der Setup-View + logout.)

### 4) `db/bookings.repository.ts` — Änderungen

**a) Reason-Union `BookingValidationError` erweitern** (ergänzen, bestehende behalten):
`'unknown_account' | 'archived_account' | 'unknown_pool' | 'archived_pool' | 'unknown_category' | 'archived_category' | 'no_snapshot'`

**b) Neue Abhängigkeit** (Muster der bestehenden Felder):
```ts
private readonly snapshotRepository = inject(SnapshotRepository);
```
Import aus `./snapshot.repository.js`.

**c) Neue private Methode `validateSnapshotReferences(input: LocalExpenseInput)`** (async — DB-Lesung):
```ts
/**
 * Referenzvalidierung gegen den Stammdaten-Snapshot (Batch C, Design D7):
 * Konto + alle Split-Pool/Kategorie-Referenzen müssen existieren und
 * Status `active` haben. Läuft NACH validateExpense, VOR jeder
 * Transaktion; bei Fehler wird nichts persistiert (auch keine
 * device.id-Meta).
 */
```
Logik:
1. `const catalog = await this.snapshotRepository.referenceCatalog();`
2. `catalog === null` → `throw new BookingValidationError('no_snapshot', 'Kein Stammdaten-Snapshot geladen — bitte in der Einrichtung verbinden')`.
3. `input.accountId`: fehlt im `catalog.accounts` → `unknown_account`; `status !== 'active'` → `archived_account`. (Kein Trimmen — exakte ID, wie bei Batch B `invalid_pool`/`invalid_category`.)
4. Jeder Split: `poolId` → `catalog.pools` (`unknown_pool` / `archived_pool`); `categoryId` → `catalog.categories` (`unknown_category` / `archived_category`).
5. **Steuerkategorie-NICHT prüfen** (`tax.taxCategoryId` bleibt wie Batch B: nur Format-/Konsistenzregeln, keine Snapshot-Existenz — Host-Regel B6).
Fehlermeldungen deutsch, mit der betroffenen ID, z. B. `Unbekannter Pool: ${id}` / `Pool ist archiviert: ${id}`.

**d) Aufrufpunkte:**
- `doSave`: direkt NACH `validateExpense(input);`, VOR `getOrCreateDeviceId()` — so wird bei Referenzfehler garantiert keine `device.id`-Meta erzeugt (Design-Entscheidung 26.10.).
- `update`: direkt NACH `validateExpense(input);`, VOR `withWriteTransaction` (außerhalb der Transaktion — reine Lese-Validierung, konsistent mit save).

**e) Öffentliche Methode `getDeviceId(): Promise<string>`** — delegiert an `getOrCreateDeviceId()` (für die Setup-View „Geräte-ID" anzeigen; erzeugt bei Bedarf, idempotent).

### 5) `setup/setup.component.ts` + `setup.html` (neu)

**`setup.component.ts`:**
- `@Component({ selector: 'app-setup', imports: [FormsModule], templateUrl: './setup.html' })` — **ohne** styleUrl, kein CSS-File (Styling ist Batch E).
- Zoneless (kein `markForCheck`, keine NgZone) — asynchrone Zustände als **Signals** (Muster `app.ts`).
- `inject`: `private readonly hostClient = inject(HostClient);`, `private readonly snapshotRepository = inject(SnapshotRepository);`, `private readonly meta = inject(MetaService);`, `private readonly db = inject(LocalDatabase);`
- Signals: `url` (Default `''`), `username`, `password`, `busy` (bool), `status` (`'idle' | 'success' | 'error'`), `message` (string | null), `deviceId` (string | null), `snapshotFetchedAt` (string | null), `health` (string | null), `showPassword` (bool, Standard false).
- `ngOnInit`: `this.db.init()` ignorieren (bereits initialisiert per App-Start); `loadLocalState()` aufrufen:
  - `deviceId = await repo.getDeviceId()` (via BookingsRepository — `inject(BookingsRepository)` statt MetaService für die ID; beides akzeptabel, **Entscheidung: BookingsRepository.getDeviceId()**).
  - `loadAuthMeta()` → url/username vorbefüllen (Passwort NICHT im Feld anzeigen, nur „gemerkt" anzeigen: `passwordRemembered` Signal).
  - `snapshotFetchedAt` via `SnapshotRepository` (neue kleine öffentliche Methode `fetchedAt(): Promise<string | null>` in SnapshotRepository — nur Meta-Lesung).
  - Health-Prüfung: `checkHealth()` (s. u.).
- `saveAndLoad()` (Button „Verbinden & Stammdaten laden"):
  1. Leere Felder → `status='error'`, `message='Host-Adresse fehlt'` bzw. `'Benutzername fehlt'` bzw. `'Passwort fehlt'`; kein fetch.
  2. `busy = true` (Doppeltipp-Sperre, Design D7).
  3. `const result = await this.hostClient.fetchMasterData(url, username, password, new Date());`
  4. `await this.snapshotRepository.replace(result.snapshot, result.fetchedAtUtc);`
  5. auth-Meta setzen (einschließlich `auth.expires_at` = `new Date(Date.now() + result.expiresInMinutes * 60_000).toISOString()`).
  6. Erfolg: `status='success'`, `message='Verbunden — Stammdaten geladen (n Konten, m Pools, k Kategorien)'` (Zahlen aus `result.snapshot`-Map-Größen), `snapshotFetchedAt` aktualisieren.
  7. Fehler: `catch (e)` → wenn `HostClientError` nach Login (code !== fehlgeschlagenes Login — praktisch: bei `http_error`/`http_unreachable`/`bad_response`) → `await this.hostClient.logout(url, username, password)` (swallowing); `status='error'`, `message = e.message`.
  8. `finally: busy = false`.
  - **Wichtig:** auth-Meta wird NUR nach erfolgreichem `replace` gesetzt (Design: „Login ok, aber list/replace fehlerhaft → keine auth-Meta, Snapshot unverändert").
- `checkHealth()`: `OpenAPI` nicht nötig — `getHealth()` braucht kein Token; `await LarioHostService.getHealth()` mit `OpenAPI.BASE = this.url` → `health = 'ok'` + `message` mit `dataDirectory`? → **einfach halten:** `health` Signal = `'ok' | 'unreachable' | 'error'` + kurze Meldung. Fehler → `'unreachable'`/`'error'` mit deutscher Meldung. (Health ist Zweitstufe — Setup funktioniert ohne; Health-Status wird angezeigt, blockiert aber nichts.)
- `disconnect()` (Button „Abmelden"):
  1. `loadAuthMeta()` → null → `message='Nicht verbunden'`, Return.
  2. `await this.hostClient.logout(host, username, password)` (swallowing).
  3. `await this.hostClient.clearAuthMeta();`
  4. `OpenAPI.TOKEN = undefined;`
  5. `status='success'`, `message='Abgemeldet — Snapshot bleibt lokal verfügbar (Offline-Erfassung)'`, `snapshotFetchedAt` bleibt, `passwordRemembered=false`.
  - Snapshot bleibt bewusst lokal (Design D8).

**`setup.html`:** schlichtes Formular (keine Styling-Ansprüche):
- H2 „Einrichtung", Kurztext zur Offline-Erfassung.
- Felder: Host-URL (text, placeholder `http://192.168.1.226:8080`), Benutzername, Passwort (type per `showPassword` Schalter).
- Buttons: „Verbinden & Stammdaten laden" (disabled bei `busy`), „Health prüfen", „Abmelden" (disabled wenn kein auth-Meta).
- Anzeigebereich: Geräte-ID (monospace), Snapshot-Stand (`snapshotFetchedAt`, human lesbar), Health-Status, `status`/`message` (bei `error` sichtbar).
- Bindings via `(ngModelChange)`/`[ngModel]` (FormsModule) oder Signal-basiert — **Entscheidung:** FormsModule wie in `app.ts`.

### 6) `setup/host.client.spec.ts` (neu, jsdom)

- fetch-Mock: `vi.stubGlobal('fetch', vi.fn(...))` — der generierte Client nutzt `fetch` global (Muster `generated-client.spec.ts` ansehen, falls vorhanden).
- Setup: `beforeEach` mit `dbState.reset()` + `createEnvironmentInjector([MetaService, HostClient], TestBed.inject(EnvironmentInjector))`; `localStorage.clear()` falls genutzt (MetaService nutzt die DB, nicht localStorage).
- Tests (mind.):
  1. `fetchMasterData` Erfolg: Login + 4 Lists (fetch-Aufrufe: POST /api/v1/auth/login + 4 GETs), Rückgabe-Maps korrekt befüllt, `fetchedAtUtc` = `now.toISOString()`, `OpenAPI.BASE` gesetzt, `OpenAPI.TOKEN` = Login-Token.
  2. Login 401 → `HostClientError` code `http_error`, `OpenAPI.TOKEN` bleibt undefined, keine List-Calls.
  3. Login-Netzwerkfehler (fetch rejectet) → code `http_unreachable`.
  4. Login ok, aber `listAccounts` 500 → `http_error`.
  5. Login ok, eine Liste liefert kein Array (z. B. `{}`) → `bad_response`.
  6. `logout`: Login+Logout-Call erfolgt; Fehler im Logout werden geschluckt (kein throw); `OpenAPI.TOKEN` am Ende undefined.
  7. `clearAuthMeta` löscht die 4 Keys; `loadAuthMeta` liefert null wenn Key fehlt, sonst die Werte.
  8. `fetchMasterData` schreibt NICHTS in die DB (dbState.accounts etc. leer, kein snapshot.fetched_at-Meta).

### 7) `db/snapshot.repository.spec.ts` (neu, jsdom, DOM-Spec-Muster)

- `beforeEach`: `dbState.reset()`, env-Injector `[LocalDatabase, MetaService, SnapshotRepository]`, `await db.init()`.
- Tests (mind.):
  1. `replace` + `loadSnapshot`: vollständiger Snapshot (1 Account, 1 Pool, 1 Kategorie mit taxPreset, 1 Steuerkategorie) → `loadSnapshot` liefert identische Daten (Maps, `baseRevision` als number, `isInterestBearing` boolean, `taxPreset`-Objekt rekonstruiert, `fetched_at`-Meta gesetzt).
  2. `replace` ersetzt komplett: zweiter `replace` mit anderem Inhalt → alte Zeilen weg, neue da (kein Rest).
  3. `replace` mit leeren Maps → alle Tabellen leer, `fetched_at` gesetzt; `loadSnapshot` liefert leere (nicht null); `referenceCatalog` liefert leere Maps (nicht null).
  4. `loadSnapshot` ohne replace → `null`; `referenceCatalog` ohne replace → `null`.
  5. `replace`-Atomarität: simulieren, dass der 5. INSERT fehlschlägt (z. B. doppelte PK-ID in einer Liste ist nicht simulierbar — stattdessen: `dbState.simulateRestart()`/Fehler-Injektion nach BEGIN? → **Muster aus `local-database.spec.ts`** für fehlernden executeSet nehmen: `dbState.failNextExecuteSet = ...`? — prüfen, was die Fake dafür bietet; falls keine Injektion existiert, Test über `vi.spyOn(db, 'executeSet')` mit throw → danach `loadSnapshot` ist null (Rollen-Back durch LocalDatabase-Transaktion)).
  6. `referenceCatalog`: gemischte Status (`active`/`archived`) → korrekt gemappt.
  7. `fetchedAt()`: null ohne Snapshot, ISO-String danach.

### 8) `db/bookings.repository.spec.ts` — neue Tests (bestehende NICHT ändern/abschwächen)

Neues `describe`-Block „Batch C — Referenzvalidierung" (Setup wie die übrigen Tests; zusätzlich: Snapshot via `SnapshotRepository.replace` im env-Injector vorbereiten):
1. **Kein Snapshot:** `save` mit gültiger Eingabe (aber kein Snapshot) → `BookingValidationError` reason `no_snapshot`; nichts persistiert (keine bookings, keine outbox, **keine device.id-Meta**).
2. **Unbekanntes Konto:** Snapshot mit Pool/Kategorie, aber Konto `acc-1` nicht enthalten → `unknown_account`, nichts persistiert.
3. **Archiviertes Konto:** Konto mit status `archived` → `archived_account`.
4. **Unbekannter Pool:** gültiges Konto, Split-`poolId` nicht im Snapshot → `unknown_pool`, nichts persistiert (auch keine device.id-Meta).
5. **Archivierte Kategorie:** Kategorie `archived` → `archived_category`.
6. **Gültig:** alle Referenzen aktiv → `save` erfolgreich (Buchung + Outbox + device.id wie Batch B).
7. **update:** `update` auf bestehender queued-Buchung mit jetzt-archiviertem Pool (Snapshot zwischen save und update geändert) → `archived_pool`, Buchung + Outbox unverändert.
8. **Steuerkategorie:** Split mit `tax.taxCategoryId` auf ID, die NICHT im Snapshot existiert → trotzdem Erfolg (wird nicht geprüft).
9. **`getDeviceId`:** ruft device.id an (Meta gesetzt) — idempotent (zweiter Call gleiche ID).

### 9) `setup/setup.component.spec.ts` (neu, jsdom)

- `// @vitest-environment jsdom`; TestBed: `TestBed.configureTestingModule({ imports: [SetupComponent], providers: [LocalDatabase, MetaService, SnapshotRepository, BookingsRepository, HostClient] })` — plus die beiden `vi.mock`-Blöcke (Muster oben) + `vi.stubGlobal('fetch', ...)` für HostClient.
- `beforeEach`: `dbState.reset()`, `localStorage.clear()` (falls relevant), `TestBed.resetTestingModule()`, `const comp = TestBed.inject(SetupComponent); await comp.ngOnInit?` (ngOnInit nur wenn `TestBed`-Rendering — alternativ Komponenten-Methoden direkt testen: `await comp.saveAndLoad()`).
- Tests (mind.):
  1. `saveAndLoad` mit leerer URL → `status='error'`, message nennt Host-Adresse, **kein** fetch-Call.
  2. `saveAndLoad` mit leerem Passwort → error, kein fetch.
  3. `saveAndLoad` Erfolg (fetch-Mock: Login 200 + 4×200) → `status='success'`; Snapshot in dbState (accounts-Map gefüllt); auth-Meta gesetzt (4 Keys); `snapshotFetchedAt` != null.
  4. `saveAndLoad`: Login ok, `listPools` 500 → `status='error'`; **keine** auth-Meta; Snapshot-Tabelle leer (unverändert); best-effort `logout` erfolgte (in den fetch-Calls genau ein Call auf den `/auth/logout`-Endpoint sichtbar — die 4 Lists laufen parallel, also insgesamt 7 Calls: login + 4 lists + logout-login + logout).
  5. `saveAndLoad` bei `replace`-Fehler (executeSet throwen) → error, keine auth-Meta.
  6. `disconnect` ohne auth-Meta → message „Nicht verbunden", Snapshot bleibt.
  7. `disconnect` mit auth-Meta → Logout-Call, auth-Meta weg, `OpenAPI.TOKEN === undefined`, `snapshotFetchedAt` bleibt gesetzt.
  8. `busy`-Sperre: während `saveAndLoad` läuft ist `busy() === true` (Promise-basiert testen), zweiter Call wird ignoriert oder wartet (Implementation entscheiden, Test auf 1 fetch-Batch).

## Definition of Done

- [ ] Exakt die 11 Dateien der Dateiliste geändert/neu (prüfen: `git status --short` — nichts anderes).
- [ ] `cd frontend && npm run lint` → grün.
- [ ] `cd frontend && npm test` (oder das mobile-Projekt-Skript) → ALLE Tests grün (bestehende 231 + neue).
- [ ] `cd frontend && npm run build:mobile` → grün.
- [ ] Keine Abhängigkeits-Änderung (`package.json`/Lockfiles unverändert).
- [ ] `src/generated/` unverändert.
- [ ] Commits auf Branch `step/8c-snapshot-setup` (klein + aussagekräftig, z. B. `feat(mobile): Batch C — Snapshot-Repository + Statements`, `feat(mobile): Batch C — HostClient (Login/Stammdaten/Logout)`, `feat(mobile): Batch C — Referenzvalidierung in BookingsRepository`, `feat(mobile): Batch C — Setup-Component`, `test(mobile): Batch C — Specs`).
- [ ] **Kein** Push, **kein** PR (macht der Controller).
- [ ] Am Ende: kurze Zusammenfassung (was implementiert, Abweichungen von dieser Spec + Gründe, Test-Ergebnisse mit Zahlen, offene Punkte).
