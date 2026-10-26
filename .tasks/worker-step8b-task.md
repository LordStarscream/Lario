# Worker-Auftrag: Schritt 8, Batch B — Outbox + Buchungs-Speicher (mobile)

## Kontext

Lario ist eine lokale Finanzplanung. Die Android-App (Angular 22 + Capacitor,
zoneless, Signals) erfasst Ausgaben ab Schritt 8 **vollständig offline**:
Ausgabe + Sync-Operation in **einer** lokalen SQLite-Transaktion. Schritt 8
speichert NUR lokal — es wird in diesem Schritt NICHTS an einen Host gesendet
(kein Netzwerk-Code, kein fetch). Der Host ist die maßgebliche Finanzengine
(C#); die mobile App hat keine eigene Saldenengine.

**Batch A ist fertig** und liegt in `frontend/apps/mobile/src/app/db/`:
- `local-database.ts` — `LocalDatabase`-Service (DI `providedIn: 'root'`):
  öffnet die SQLite-DB `lario` über `@capacitor-community/sqlite` (8.1.1),
  legt das Schema idempotent an, bietet `executeSet(sets: SqlSet[])`
  (immer `transaction: true`, normalisiert jedes Element an der
  Plugin-Grenze auf ein `values`-Array) und `query<T>(statement, values)`.
- `statements.ts` — alle SQL-Vorlagen als Export-Constants + Meta-Helfer
  (`upsertMetaSet`, `deleteMetaSet`, `selectMetaSet`), Typ `SqlSet`
  (`{ statement: string; values?: unknown[] }`).
- `meta.service.ts` — `MetaService` (get/set/delete der `meta`-Tabelle).
- `capacitor-sqlite-fake.ts` — geteilter Vitest-Fake der Plugin-Grenze
  (in `vi.hoisted`, Factory `capacitorSqliteMockFactory`, State `dbState`),
  kennt aktuell nur: meta, Schema-Statements, `PRAGMA foreign_keys`.
- Specs: `local-database.spec.ts` (jsdom/Fake), `schema.spec.ts`
  (**echte SQLite** via `node:sqlite`, `// @vitest-environment node`),
  `generated-client.spec.ts`.

**Tabellen-Schema** (in `statements.ts` als `SCHEMA_STATEMENTS`, schon da):
- `bookings(id TEXT PK, date TEXT, type TEXT, account_id TEXT,
  total_amount_cents INTEGER, note TEXT, base_revision INTEGER,
  created_at_utc TEXT)`
- `splits(id TEXT PK, booking_id TEXT NOT NULL REFERENCES bookings(id)
  ON DELETE CASCADE, amount_cents INTEGER, pool_id TEXT, category_id TEXT,
  note TEXT, tax_relevant INTEGER, tax_category_id TEXT,
  tax_category_name TEXT, marked_amount_cents INTEGER, tax_note TEXT)`
- `outbox(operation_id TEXT PK, device_id TEXT, type TEXT, booking_id TEXT,
  payload TEXT, payload_hash TEXT, expected_base_revision INTEGER,
  status TEXT, created_at_utc TEXT, updated_at_utc TEXT)`
- `drafts(id TEXT PK, name TEXT, data TEXT, created_at_utc TEXT,
  updated_at_utc TEXT)`
- `meta(key TEXT PK, value TEXT)`

## Arbeitsweise

- Worktree: `/tmp/lario-step8b` (dein cwd). Lese vor dem Schreiben die
  vorhandenen Dateien unter `frontend/apps/mobile/src/app/db/` vollständig.
- Schreibe NUR die im Abschnitt „Dateien“ gelisteten Pfade.
- Kommunikation und Doku-Kommentare auf Deutsch; Typen/Member/Bezeichner
  auf Englisch. JSDoc-Kommentare wie im Bestand (präzise, mit Design-Verweis).
- ESLint-Konfiguration: `frontend/eslint.config.js` (angular-eslint,
  `prefer-inject` — DI per `inject()` statt Konstruktor-Parameter).
  Vor Abschluss: `npm --prefix frontend run lint` und
  `npm --prefix frontend run test` müssen grün sein (beide laufen auch
  als Checks; `npm run test` testet host UND mobile).
- Keine neuen Abhängigkeiten, keine `crypto`/`crypto.subtle`-Imports im
  Produktionscode (Web-View-Secure-Context-Risiko), keine Node-APIs im
  App-Code. In **Specs** (Vitest) darf `node:crypto`/`node:sqlite` genutzt
  werden (Vitest läuft in Node; `node:sqlite`-Specs brauchen
  `// @vitest-environment node` als erste Zeile, wie `schema.spec.ts`).
- Keine `console.log`. Fehler werfen als Typed Exceptions (siehe unten).

## Dateizuordnung (exakt diese Pfade, relativ zum Worktree-Root)

NEU:
1. `frontend/apps/mobile/src/app/db/sha256.ts`
2. `frontend/apps/mobile/src/app/db/sha256.spec.ts`
3. `frontend/apps/mobile/src/app/db/operation-id.ts`
4. `frontend/apps/mobile/src/app/db/operation-id.spec.ts`
5. `frontend/apps/mobile/src/app/db/expense-payload.ts`
6. `frontend/apps/mobile/src/app/db/expense-payload.spec.ts`
7. `frontend/apps/mobile/src/app/db/bookings.repository.ts`
8. `frontend/apps/mobile/src/app/db/drafts.repository.ts`
9. `frontend/apps/mobile/src/app/db/bookings.repository.spec.ts`
10. `frontend/apps/mobile/src/app/db/drafts.repository.spec.ts`

ÄNDERN (erweitern, Bestand erhalten):
11. `frontend/apps/mobile/src/app/db/statements.ts` — Batch-B-Statements
12. `frontend/apps/mobile/src/app/db/capacitor-sqlite-fake.ts` — Fake
13. `frontend/apps/mobile/src/app/db/schema.spec.ts` — Batch-B-Tests

## Design (verbindlich)

### B1 — SHA-256 (reine TS, `sha256.ts`)

NIST FIPS 180-4, ohne `crypto.subtle`, ohne Node-APIs (muss im
Android-WebView laufen; `TextEncoder` ist erlaubt — WHATWG-Standard,
in allen WebViews vorhanden).

```ts
/** SHA-256-Verdau als 64-stellige kleine Hex-Zeichenfolge (UTF-8-Eingabe). */
export function sha256Hex(input: string): string;

/** Deterministischer Payload-Nachweis: `sha256:` + 64-stellige Hex-Zeichenfolge. */
export function payloadHash(payload: string): string;
```

Klassische Implementierung: UTF-8-Bytefolge (TextEncoder), Padding
(`0x80` + Nullen bis Länge ≡ 56 (mod 64) + 64-Bit-Bitlänge big-endian),
512-Bit-Blöcke, `w[0..15]` aus dem Block, `w[16..63] = σ1(w[t-2]) + w[t-7] +
σ0(w[t-16])`, 64 Kompressionsrunden mit den 64 K-Konstanten und
H0–H7 (erste 32 Bit der Quadratwurzeln der ersten 8 Primzahlen),
alle Ganzzahloperationen mod 2^32 (bitweise Operatoren, keine Float-Tricks),
Ausgabe big-endian pro Wort.

**Spec** (`sha256.spec.ts`, jsdom reicht — kein Node-Environment nötig):
- NIST-Testvektoren (exakt, kleine Hex):
  - `""` → `e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855`
  - `"abc"` → `ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad`
  - `"abcdbcdecdefdefgefghfghighijhijkijkljklmklmnlmnomnopnopq"` →
    `248d6a61d20638b8e5c026930c3e6039a33ce45964ff2167f6ecedd419db06c1`
  - `"abcdefghbcdefghicdefghijdefghijkefghijklfghijklmghijklmnhijklmnoijklmnopjklmnopqklmnopqrlmnopqrsmnopqrstnopqrstu"` →
    `cf5b16a778af8380036ce59e7b0492370b249b11e8f07a51afac45037afee9d1`
  - 1.000.000×`"a"` →
    `cdc76e5c9914fb9281a1c7e284d73e67f1809a48a497200e046d39ccc7112cd0`
- UTF-8: `sha256Hex('Sänger')` und `sha256Hex('€')` — gegen
  `node:crypto` (createHash, test-only Import) abgleichen; zusätzlich
  Format-Check `^[0-9a-f]{64}$`.
- `payloadHash('{"a":1}')` → `sha256:` + `sha256Hex('{"a":1}')`; Format
  `^sha256:[0-9a-f]{64}$`; Determinismus (zweimal = identisch).
- 55/56/64-Byte-Eingaben (Padding-Grenzen): gegen `node:crypto`.

### B2 — Operation-IDs + Zähler (`operation-id.ts`)

Format: `op-<JJJJ>-<MMDD>-<seq>` (Schritt-0-Beispiel `op-2026-1101-001`).
JJJJ/MMDD = **lokales Datum** des `Date`-Arguments (Geräteluftzeit,
in Produktion Europe/Berlin) — nicht UTC. seq = **persistierter monotoner
Geräte-Zähler**: Start 1, wächst pro Gerät strikt weiter, wird NIE
zurückgesetzt (nicht bei Neustart, nicht beim Tageswechsel — das
Schritt-0-Beispiel zeigt `op-2026-1101-001` dann `op-2026-1102-002`).
Format: 3-stellige Mindest-Auffüllung (`padStart(3, '0')`); der Zähler
darf darüber hinauswachsen (4+ Stellen), Eindeutigkeit bleibt gewahrt.
Der Zähler lebt in `meta` unter dem Key `outbox.op_seq`.

```ts
export const OP_SEQ_META_KEY = 'outbox.op_seq';

/** `op-2026-1101-001` — JJJJ/MMDD aus der lokalen Zeit von `now`. */
export function formatOperationId(now: Date, seq: number): string;

/** Meta-Wert auf Zählerwert prüfen/lesen: null/leer → 1; nicht-positives
 *  Integer → werfen (Error). */
export function parseOpSeq(value: string | null): number;

/** Stabile gerätegenerierte Buchungs-ID: `bk-001-mj8x9k2l`
 *  (`bk-` + seq 3-stellig + `-` + `nowMs` in Base-36). */
export function formatBookingId(seq: number, nowMs: number): string;

/** Split-ID aus Buchungs-ID + Position (1-basiert): `bk-001-xyz-s1`. */
export function formatSplitId(bookingId: string, position: number): string;
```

**Spec** (`operation-id.spec.ts`): Format mit fixen `new Date(2026, 10, 1)`
(1.11.2026) und seq 1/7/999/1000; `formatOperationId` nutzt lokale Zeit
(TZ-Abhängigkeit: Test mit `new Date(2026, 10, 1, 12, 0, 0)` — Mittagszeit
vermeidet TZ-Randfälle); `parseOpSeq` (null → 1, `"42"` → 42, `""` → 1,
`"0"`/`"-1"`/`"x"`/`"1.5"` → werfen); `formatBookingId(1, 1796100000000)`
→ exakt `bk-001-` + `(1796100000000).toString(36)`; `formatSplitId`.

### B3 — Typen + Payload (`expense-payload.ts`)

Der Outbox-Payload ist **exakt** die `ExpenseCommand`-Shape des Hosts
(`src/Lario.Host/Api/Journal/JournalWriteDtos.cs`), kompakt (kein
Whitespace), **feste Schlüsselreihenfolge** (JSON.stringify erhält bei
String-Keys die Einfügereihenfolge):

- Booking: `id, date, totalAmount, accountId, note, splits, expectedBaseRevision`
- Split: `id, amount, poolId, categoryId, note, tax`
- tax: `relevant, taxCategoryId, markedAmount, note`
- `expectedBaseRevision` ist bei Neuanlage immer `null`; Beträge sind
  Ganzzahl-Centwerte als JSON-Nummern (keine Strings).

```ts
export interface LocalTaxInput {
  /** Steuerlich relevant (Schalter). */
  relevant: boolean;
  /** Steuerkategorie; null = keine (bei relevant erlaubt, bei !relevant Pflicht-null). */
  taxCategoryId: string | null;
  /** Nur lokales DB-Feld (kein Payload-Teil): Name für die Anzeige. */
  taxCategoryName: string | null;
  /** null = gesamter Splitbetrag markiert (Standard). */
  markedAmountCents: number | null;
  note: string | null;
}

export interface LocalSplitInput {
  id: string;
  amountCents: number;
  poolId: string;
  categoryId: string;
  note: string | null;
  /** null = keine explizite Kennzeichnung (Host materialisiert Voreinstellung). */
  tax: LocalTaxInput | null;
}

export interface LocalExpenseInput {
  /** Fachdatum `yyyy-MM-dd` (Europe/Berlin-Logik der App). */
  date: string;
  totalAmountCents: number;
  accountId: string;
  note: string | null;
  splits: LocalSplitInput[];
}

/** Kompaktes JSON mit fester Schlüsselreihenfolge (B3 oben). */
export function buildExpensePayload(bookingId: string, input: LocalExpenseInput): string;
```

Payload-Regeln:
- `markedAmount`: `tax.markedAmountCents ?? amountCents` (null wird
  normalisiert auf den Splitbetrag — deterministisch für den Hash).
- `tax: null` bleibt `null` im Payload (kein leeres Objekt).
- `date`/`totalAmount`/`accountId`/`note` direkt aus `input`.

**Spec** (`expense-payload.spec.ts`):
- Exakter String-Abgleich (JSON.parse + Reihenfolge über
  `Object.keys`/`Object.entries` oder exakte String-Gleichheit):
  Booking mit 2 Splits (einer tax: null, einer relevant mit
  taxCategoryId + markedAmount null → normalisiert auf amount) —
  `JSON.stringify`-Ergebnis exakt erwartet (kompakt, keine Leerzeichen).
- `expectedBaseRevision` = `null` (Key vorhanden, Wert null).
- Reihenfolge: `Object.keys(parsed)` === erwartete Reihenfolge (Booking,
  Split, tax).
- Determinismus: zweimal bauen = identisch.
- UTF-8-Notiz (`"Kaffee für Gäste"`) bleibt gültiges kompakt JSON.

### B4 — SQL-Statements (`statements.ts`, anhängen, Bestand NICHT ändern)

```ts
// Batch B — Buchungen, Splits, Outbox, Entwürfe

export const INSERT_BOOKING =
  'INSERT INTO bookings (id, date, type, account_id, total_amount_cents, note, base_revision, created_at_utc) VALUES (?, ?, ?, ?, ?, ?, ?, ?)';
export const UPDATE_BOOKING =
  'UPDATE bookings SET date = ?, total_amount_cents = ?, account_id = ?, note = ? WHERE id = ?';
export const DELETE_BOOKING = 'DELETE FROM bookings WHERE id = ?';
export const INSERT_SPLIT =
  'INSERT INTO splits (id, booking_id, amount_cents, pool_id, category_id, note, tax_relevant, tax_category_id, tax_category_name, marked_amount_cents, tax_note) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)';
export const DELETE_SPLITS = 'DELETE FROM splits WHERE booking_id = ?';
export const INSERT_OUTBOX =
  "INSERT INTO outbox (operation_id, device_id, type, booking_id, payload, payload_hash, expected_base_revision, status, created_at_utc, updated_at_utc) VALUES (?, ?, ?, ?, ?, ?, ?, 'queued', ?, ?)";
export const UPDATE_OUTBOX_PAYLOAD =
  'UPDATE outbox SET payload = ?, payload_hash = ?, updated_at_utc = ? WHERE operation_id = ? AND status = ?';
export const DELETE_OUTBOX = 'DELETE FROM outbox WHERE operation_id = ?';
export const SELECT_BOOKING_LIST =
  'SELECT b.id, b.date, b.total_amount_cents, b.account_id, b.note, o.status AS outbox_status FROM bookings b LEFT JOIN outbox o ON o.booking_id = b.id ORDER BY b.date DESC, b.created_at_utc DESC';
export const SELECT_BOOKING_BY_ID =
  'SELECT id, date, type, account_id, total_amount_cents, note, base_revision, created_at_utc FROM bookings WHERE id = ?';
export const SELECT_SPLITS_BY_BOOKING =
  'SELECT id, amount_cents, pool_id, category_id, note, tax_relevant, tax_category_id, tax_category_name, marked_amount_cents, tax_note FROM splits WHERE booking_id = ? ORDER BY id';
export const SELECT_OUTBOX_BY_BOOKING =
  'SELECT operation_id, status, payload, payload_hash, updated_at_utc FROM outbox WHERE booking_id = ?';
export const UPSERT_DRAFT =
  'INSERT INTO drafts (id, name, data, created_at_utc, updated_at_utc) VALUES (?, ?, ?, ?, ?) ON CONFLICT(id) DO UPDATE SET name = excluded.name, data = excluded.data, updated_at_utc = excluded.updated_at_utc';
export const SELECT_DRAFT_BY_ID =
  'SELECT id, name, data, created_at_utc, updated_at_utc FROM drafts WHERE id = ?';
export const SELECT_DRAFTS =
  'SELECT id, name, data, created_at_utc, updated_at_utc FROM drafts ORDER BY updated_at_utc DESC, id';
export const DELETE_DRAFT = 'DELETE FROM drafts WHERE id = ?';
```

### B5 — Exceptions (`bookings.repository.ts`, exportiert)

```ts
/** Validierungsfehler: `reason` maschinell unterscheidbar. */
export class BookingValidationError extends Error {
  constructor(
    readonly reason:
      | 'invalid_date'
      | 'invalid_amount'
      | 'invalid_account'
      | 'no_splits'
      | 'invalid_split_amount'
      | 'split_sum_mismatch'
      | 'tax_inconsistent'
      | 'invalid_marked_amount',
    message: string,
  ) { super(message); this.name = 'BookingValidationError'; }
}

/** Zustandsfehler: `reason` maschinell unterscheidbar. */
export class BookingStateError extends Error {
  constructor(
    readonly reason: 'not_found' | 'not_queued' | 'duplicate_operation',
    message: string,
  ) { super(message); this.name = 'BookingStateError'; }
}
```

### B6 — Repository (`bookings.repository.ts`)

`@Injectable({ providedIn: 'root' }) class BookingsRepository` mit
`private readonly db = inject(LocalDatabase);` und
`private readonly meta = inject(MetaService);`

**Invariants** (Funktion `validateExpense(input: LocalExpenseInput): void`,
exportiert für Tests, wirft `BookingValidationError` mit exaktem `reason`):
1. `date` matcht `^\d{4}-\d{2}-\d{2}$` UND ist ein echtes Kalendertag
   (z. B. `2026-02-30` ungültig) → `invalid_date`.
2. `totalAmountCents`: ganze Zahl (Number.isInteger), `1 ≤ x ≤ 9999999999999`
   (13-stellige long-Cent-Grenze) → `invalid_amount`.
3. `accountId`: nicht leer (nach trim) → `invalid_account`.
4. `splits.length ≥ 1` → `no_splits`.
5. Jeder Split: `amountCents` ganze Zahl > 0 → `invalid_split_amount`.
6. Summe der Splitbeträge === `totalAmountCents` (exakt, Cent) →
   `split_sum_mismatch`.
7. Steuer (Domain-Regel `Lario.Domain/Journal/TaxMarking.cs`, maßgeblich):
   - `tax !== null && !tax.relevant` → `taxCategoryId === null` UND
     `markedAmountCents === null` → sonst `tax_inconsistent`.
   - `tax !== null && tax.relevant` → `taxCategoryId` **optional** (null
     erlaubt); `markedAmountCents === null` → OK; sonst ganze Zahl mit
     `0 ≤ x ≤ amountCents` → sonst `invalid_marked_amount`.

**`save(input: LocalExpenseInput, deviceId: string, now: Date): Promise<{ bookingId: string; operationId: string }>`**
1. `validateExpense(input)`.
2. `const seq = parseOpSeq(await this.meta.get(OP_SEQ_META_KEY));`
3. `const bookingId = formatBookingId(seq, now.getTime());`
   `const operationId = formatOperationId(now, seq);`
4. `const payload = buildExpensePayload(bookingId, input);`
   `const hash = payloadHash(payload);`
   `const nowIso = now.toISOString();`
5. **EIN** `this.db.executeSet([...])` (atomar, D5):
   - `INSERT_BOOKING` values: `[bookingId, input.date, 'expense', input.accountId, input.totalAmountCents, input.note, null, nowIso]`
   - je Split `INSERT_SPLIT` values: `[split.id, bookingId, split.amountCents, split.poolId, split.categoryId, split.note, tax ? (tax.relevant ? 1 : 0) : null, tax?.taxCategoryId ?? null, tax?.taxCategoryName ?? null, tax ? (tax.markedAmountCents ?? split.amountCents) : null, tax?.note ?? null]`
   - `INSERT_OUTBOX` values: `[operationId, deviceId, 'expense', bookingId, payload, hash, null, nowIso, nowIso]`
   - `upsertMetaSet(OP_SEQ_META_KEY, String(seq + 1))`
6. `return { bookingId, operationId };`

   Hinweis: Bei einem `executeSet`-Fehler (z. B. UNIQUE-Verstoß auf
   `outbox.operation_id` durch parallel gelesenem Zähler) rollt die
  Transaktion komplett zurück — kein Teilzustand. Den Fehler weiterwerfen.

**`update(bookingId: string, input: LocalExpenseInput, now: Date): Promise<void>`**
1. Bestandsbuchung laden (`SELECT_BOOKING_BY_ID` + `SELECT_OUTBOX_BY_BOOKING`):
   Buchung fehlt → `BookingStateError('not_found')`; Outbox-Zeile fehlt →
   `BookingStateError('not_found')`; Outbox-`status !== 'queued'` →
   `BookingStateError('not_queued')` (nur queued darf geändert werden —
   der Host hat die Operation nie gesehen).
2. `validateExpense(input)`.
3. Neues Payload + Hash (gleiche `bookingId`, `expectedBaseRevision: null`);
   `nowIso = now.toISOString()`.
4. **EIN** `executeSet`:
   - `UPDATE_BOOKING` values: `[input.date, input.totalAmountCents, input.accountId, input.note, bookingId]`
   - `DELETE_SPLITS` values: `[bookingId]` (Splitanzahl kann sich ändern)
   - je Split `INSERT_SPLIT` (wie bei save, ID neu per
     `formatSplitId(bookingId, pos)` — Position stabil)
   - `UPDATE_OUTBOX_PAYLOAD` values: `[payload, hash, nowIso, existingOperationId, 'queued']`
   - (Kein Zähler-Änderungs-Set: die operationId bleibt erhalten.)

**`discard(bookingId: string): Promise<void>`**
1. Laden wie bei update: Buchung fehlt → `not_found`; Outbox fehlt →
   `not_found`; `status !== 'queued'` → `not_queued`.
2. **EIN** `executeSet` (explizite Lösch-Reihenfolge Splits → Buchung →
   Outbox als Verteidigungstiefe, Design D2a):
   - `DELETE_SPLITS` `[bookingId]`
   - `DELETE_BOOKING` `[bookingId]`
   - `DELETE_OUTBOX` `[operationId]`

**`list(): Promise<LocalBookingListItem[]>`**
`SELECT_BOOKING_LIST` → Zeilen: `{ bookingId, date, totalAmountCents,
accountId, note, status }` mit `status: 'queued' | 'sent' | 'unknown'`
(Outbox-Status `queued`/`sent` direkt; keine Outbox-Zeile → `'unknown'`).

**`get(bookingId: string): Promise<LocalBookingDetail | null>`**
null, wenn keine Buchung. Sonst `{ bookingId, date, type, accountId,
totalAmountCents, note, createdAtUtc, status, splits: [{ id, amountCents,
poolId, categoryId, note, tax: null | { relevant, taxCategoryId,
taxCategoryName, markedAmountCents, note } }] }`.
`tax_relevant` 0/1/NULL → `false`/`true`/`tax: null` (NULL = keine
Kennzeichnung). `status` wie bei list.

Typen `LocalBookingListItem` und `LocalBookingDetail` in
`bookings.repository.ts` exportieren.

### B7 — Drafts-Repository (`drafts.repository.ts`)

`@Injectable({ providedIn: 'root' }) class DraftsRepository`:
- `save(id: string, name: string, data: string, now: Date): Promise<void>`
  → `UPSERT_DRAFT` values `[id, name, data, nowIso, nowIso]`
  (Upsert: bestehender Entwurf wird ersetzt — created bleibt erhalten,
  weil nur name/data/updated_at gesetzt werden).
- `get(id: string): Promise<LocalDraft | null>`
- `list(): Promise<LocalDraft[]>`
- `delete(id: string): Promise<void>`
- `LocalDraft = { id, name, data, created_at_utc, updated_at_utc }`
  (Member-Namen: `createdAtUtc`/`updatedAtUtc` — TypeScript-CamelCase).
Kein `now`-Default: `now` ist immer Parameter (Testbarkeit, keine
versteckte Uhr).

### B8 — Fake-Erweiterung (`capacitor-sqlite-fake.ts`)

Der Fake modelliert ab Batch B die vier Fachtabellen mit echten
SQLite-Constraint-Semantiken. **Auffinde-Strategie: exakte
Statement-Strings** (importiere die Constants aus `./statements`),
kein Substring-Matching für Batch-B-Statements.

State-Erweiterung (in `dbState` + `reset()`):
```ts
bookings: Map<string, FakeRow>;  // Key: id
splits: Map<string, FakeRow>;    // Key: id
outbox: Map<string, FakeRow>;    // Key: operation_id
drafts: Map<string, FakeRow>;    // Key: id
```
`FakeRow = Record<string, string | number | null>` (Spaltennamen =
Schema-Spalten, snake_case). `simulateRestart()` behält ALLE Tabellen
(und meta) — nur `connections` wird geleert (die SQLite-Datei bleibt).
`reset()` leert alles.

`applySet(state, set, inTransaction)` — zusätzlich zu Batch A:
- `INSERT_BOOKING`: NOT NULL-Checks (date/type/account_id/
  total_amount_cents/created_at_utc); PK: existierender `id` →
  `Error('SQLITE_CONSTRAINT: UNIQUE constraint failed: bookings.id')`.
- `UPDATE_BOOKING`: existiert die Buchung nicht → still no-op (0 Zeilen,
  SQLite-Semantik); sonst Felder setzen.
- `DELETE_BOOKING`: bei `state.foreignKeys === true` und vorhandenen
  Splits mit diesem booking_id →
  `Error('SQLITE_CONSTRAINT: FOREIGN KEY constraint failed: splits.booking_id')`;
  sonst löschen. (Splits-Cascade: das echte SQLite löscht die Splits
  automatisch — der Fake muss das bei `foreignKeys: true` ebenfalls tun:
  nach erfolgreichem `DELETE_BOOKING` die Splits mit dem booking_id
  entfernen.)
- `INSERT_SPLIT`: NOT NULL (id/booking_id/amount_cents/pool_id/category_id);
  PK wie oben; FK: `foreignKeys === true` und kein
  `bookings.get(booking_id)` →
  `Error('SQLITE_CONSTRAINT: FOREIGN KEY constraint failed: splits.booking_id')`.
- `DELETE_SPLITS`: alle Splits mit dem booking_id entfernen.
- `INSERT_OUTBOX`: PK auf operation_id wie oben; NOT NULL (operation_id/
  device_id/type/booking_id/payload/payload_hash/status/created_at_utc/
  updated_at_utc); FK: booking_id muss in bookings existieren (bei
  foreignKeys).
- `UPDATE_OUTBOX_PAYLOAD`: WHERE operation_id AND status — existiert die
  Zeile mit passendem status → Felder setzen, sonst no-op.
- `DELETE_OUTBOX`: Zeile entfernen.
- `UPSERT_DRAFT`: existiert → name/data/updated_at_utc setzen; sonst
  einfügen. PK-Konflikt-Logik wie `ON CONFLICT DO UPDATE` (kein Fehler!).
- `SELECT_DRAFT_BY_ID` / `SELECT_DRAFTS` / `DELETE_DRAFT` wie Schema.
- Batch-A-Fälle (PRAGMA, CREATE TABLE, Meta) bleiben unverändert.
- Unbekanntes Statement → weiter werfen (mit klarer Meldung, dass der
  Fake es nicht kennt).

Transaktion: Die Snapshot-/Rückabwicklungslogik von `executeSet` muss
jetzt ALLE Tabellen mappen (tiefe Kopie der Maps), nicht nur meta.
`executedSets`-Historie unverändert fortführen.

`query(options)` — zusätzlich unterstützen (exakte Statement-Strings):
- `SELECT_BOOKING_BY_ID` → Zeile mit den 8 Spalten (oder leer).
- `SELECT_SPLITS_BY_BOOKING` → Zeilen (ORDER BY id — einfaches
  Array-Iterieren über die Map genügt, wenn die IDs sortiert einsortiert
  werden; sonst `Array.from(map).sort((a,b)=>a[0]<b[0]?-1:1)`).
- `SELECT_OUTBOX_BY_BOOKING` → Zeile(n) mit 5 Spalten.
- `SELECT_BOOKING_LIST` → LEFT JOIN: jede Booking-Zeile +
  `outbox_status` (Spalte `status` der Outbox-Zeile mit gleichem
  booking_id, sonst null); ORDER BY date DESC, created_at_utc DESC
  (String-Vergleich genügt für ISO-Daten).
- `SELECT_DRAFT_BY_ID` / `SELECT_DRAFTS` (ORDER BY updated_at_utc DESC, id).
- Batch-A-Abfragen bleiben.

### B9 — Tests (Fake-basiert, jsdom)

Alle Specs beginnen wie `local-database.spec.ts` mit der ersten Zeile
`// @vitest-environment jsdom` und EXAKT diesen Mocks (identisch
übernehmen):
```ts
vi.mock('@capacitor/core', () => ({
  Capacitor: { isNativePlatform: () => true },
}));
vi.mock('@capacitor-community/sqlite', capacitorSqliteMockFactory);
```
(zusätzlich `beforeEach(() => dbState.reset())`). `LocalDatabase` wird
per `new LocalDatabase()` + `await db.init()` gebaut (wie im Bestand).
Repositories nutzen `inject()` → Konstruktion im Injection-Context mit
dem Bestandsmuster:
```ts
import { TestBed } from '@angular/core/testing';
const repo = TestBed.runInInjectionContext(() => new BookingsRepository());
```
(`LocalDatabase`/`MetaService` sind `providedIn: 'root'` — der
TestBed-Root-Injector reicht; keine weiteren Provider nötig.)

**`bookings.repository.spec.ts`** (Mindestfälle):
1. **Save happy path**: 2 Splits (einer tax: null, einer relevant mit
   taxCategoryId, markedAmount null) → `{ bookingId, operationId }`;
   `dbState.bookings` 1 Zeile (type `expense`, created_at_utc = nowIso),
   `dbState.splits` 2 Zeilen, `dbState.outbox` 1 Zeile (status `queued`,
   payload = `buildExpensePayload(...)`, payload_hash =
   `payloadHash(payload)`, device_id, expected_base_revision null),
   meta `outbox.op_seq` = `2`; operationId-Format mit dem overgebenen
   `now` exakt; `get(bookingId)` liefert Detail mit Splits + tax-Roundtrip
   (relevant true/false/null korrekt; markedAmount null → normalisiert
   auf amountCents in payload UND in DB-Spalte `marked_amount_cents`).
2. **20 Ausgaben + Neustart** (Plan-Abnahme (a)): Schleife 20× save
   (variiere Beträge/Datum/2+ Splits, manche mit Steuer) →
   `dbState.simulateRestart()` → **neue** `LocalDatabase` + `init()` →
   neues Repository → `list()` 20 Einträge, `get()` auf Probe: Splits +
   Outbox-Zeilen `queued` vorhanden; operationIds 001–020 (seq monoton).
3. **Parallele Saves (Doppeltipp-Pendant auf Repository-Ebene)**:
   `await Promise.all([save(...), save(...)])` → beide erfolgreich mit
   konsekutiven operationIds (001/002), 2 Buchungen, Zähler = 3 —
   oder einer wirft `SQLITE_CONSTRAINT` (beides konsistent; der
   eigentliche UI-Sperren-Test kommt in Batch D). Assert: kein
   Teilzustand (Zähler === Anzahl erfolgreicher Buchungen + 1), keine
   Orphan-Splits (jeder Split referenziert existierende Buchung).
4. **Verwerfen** (Plan-Abnahme (c)): save → discard → `bookings` leer,
   `splits` leer, `outbox` leer; Zähler bleibt (kein Reset); zweites
   discard → `BookingStateError` `not_found`.
5. **Ändern nur queued** (Plan-Abnahme (c)/D4): save → update (Betrag +
   Splits neu, Summe passt) → Buchung/Splits ersetzt, operationId
   GLEICH, payload/Hash neu, created_at_utc gleich, updated_at_utc neu;
   Zähler unverändert. update nach simuliertem `status = 'sent'`
   (Fake-State direkt setzen) → `not_queued`. discard mit `sent` →
   `not_queued`.
6. **Ungültig → nichts gespeichert** (Plan-Abnahme (d)): je ein Fall pro
   `reason` (invalid_date `2026-02-30`, invalid_date `31.10.2026`,
   invalid_amount 0, invalid_amount 10000000000000, invalid_account `""`,
   no_splits `[]`, invalid_split_amount 0, split_sum_mismatch 100≠99,
   tax_inconsistent (!relevant mit taxCategoryId), invalid_marked_amount
   (markedAmount > amount)) → jeweils: Fehler mit exaktem reason,
   `bookings`/`splits`/`outbox` leer, Zähler = 1 (unverändert).
7. **Abbruch beim 2. Statement → kein Teilzustand** (Plan-Abnahme (e)):
   Save so präparieren, dass das 2. Set fehlschlägt — z. B. Vorab
   `dbState.bookings.set(bookingId, {...})` ist nicht vorhersagbar
   (ID enthält Timestamp). Besser: den Fake um eine Test-Option
   `failOnStatementIndex: number | null` erweitern (in `dbState` +
   `reset()`; executeSet wirft bei dem Set mit diesem Index
   `Error('simulated failure')`) → save wirft, danach: `bookings` leer,
   `splits` leer, `outbox` leer, Zähler = 1. (Option gehört in die
   Fake-Schnittstelle `CapacitorSqliteFakeState`; `reset()` setzt sie
   zurück.)

**`drafts.repository.spec.ts`**: save neu (get/list), save bestehender
ID (Upsert: created gleich, updated neu, name/data ersetzt), get nicht
vorhanden → null, delete, delete nicht vorhanden → no-op, list-Reihenfolge
(updated_at_utc DESC).

### B10 — Echte SQLite-Tests (`schema.spec.ts` erweitern)

Neue `describe`-Blöcke (Muster der Datei: `initSchema(db)`, `runScript`):
1. **Batch-B-Statement-Sequenzen atomar**: save-Äquivalent als
   `runScript` (INSERT_BOOKING + 2× INSERT_SPLIT + INSERT_OUTBOX +
   upsertMetaSet); dann dieselbe Sequenz mit einem bewusst fehlerhaften
   2. Statement (z. B. INSERT_SPLIT mit FK-verletztem booking_id, weil
   `PRAGMA foreign_keys = ON` gesetzt ist) → wirft, danach: bookings/
   splits/outbox/meta alle unverändert (kein Teilzustand).
2. **FK-Cascade + Lösch-Reihenfolge**: Buchung + Splits + Outbox anlegen;
   `DELETE_SPLITS` + `DELETE_BOOKING` (wie discard) → alles weg, outbox
   manuell; direkt `DELETE_BOOKING` ohne `DELETE_SPLITS` → wirft
   (FK-Verletzung); danach mit `DELETE_SPLITS` zuerst → sauber.
3. **Outbox-Unique**: zweites `INSERT_OUTBOX` mit gleicher operation_id
   → wirft (UNIQUE); `UPDATE_OUTBOX_PAYLOAD` mit falschem status
   (WHERE status = 'queued', Zeile ist 'sent') → no-op (0 Zeilen),
   Payload unverändert.
4. **Drafts**: Upsert (created_at bleibt), Unique, Delete.
5. **SELECT_BOOKING_LIST**: 2 Buchungen (eine mit Outbox queued, eine
   ohne) → Zeilen mit outbox_status queued/null, Reihenfolge date DESC.

## Abnahme (Definition of Done)

- `npm --prefix frontend run lint` grün (im Worktree-Root gibt es
  `package.json` mit den Scripts `lint`/`test` — alternativ direkt
  `npm --prefix frontend run …`).
- `npm --prefix frontend run test` grün (host + mobile; alle Batch-A-Tests
  bleiben grün — nichts davon brechen/ändern, außer dass
  `schema.spec.ts`/`capacitor-sqlite-fake.ts` erweitert werden).
- Alle B9/B10-Mindestfälle vorhanden.
- Keine Datei außerhalb der Dateiliste angefasst; kein `console.log`;
  keine neuen Dependencies.
- Am Ende eine kurze Zusammenfassung: Dateien, Tests (Anzahl),
  Abweichungen von dieser Vorgabe (falls nötig — begründen).
