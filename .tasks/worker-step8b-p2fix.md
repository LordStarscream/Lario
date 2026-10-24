# P2-Nachgang Batch B — Spezifikation (Entscheidung: Option A)

## Kontext
- **Worktree:** `/tmp/lario-step8b` (Branch `step/8b-mobile-outbox`, HEAD `ad9e945`), `node_modules` vorhanden.
- Alle Pfade relativ zu `frontend/apps/mobile/src/app/db/` (= `$D`), außer anders angegeben.
- Die Review-Befunde liegen in `REVIEW-STORAGE-BATCH-B.md` (P2-1 + P2-2).
- **Entscheidung (Nutzer, 23.10.2026): Option A** — `ON DELETE CASCADE` bleibt in der DDL; B10-Item-2 und der Fake werden auf Cascade korrigiert.
- **Kein** `console.log`, keine neuen Dependencies, keine Änderung an `package.json`/Lockfile.
- Am Ende MÜSSEN grün sein: `npm run lint`, `npm run test`, `npm run build:mobile` (alle aus `frontend/`).

## Aufgabe 1 (P2-1): `outbox.ts`/`outbox.spec.ts` entfernen, Gerät-ID ins Repository

### 1.1 Löschen
- `$D/outbox.ts`
- `$D/outbox.spec.ts`

### 1.2 `$D/bookings.repository.ts` ändern
- Entferne `import { OutboxService } from './outbox';` (aktuell Zeile 4).
- Entferne das Feld `private readonly outbox = inject(OutboxService);` (aktuell Zeile 234).
- Ersetze `const deviceId = await this.outbox.getOrCreateDeviceId();` (aktuell Zeile 274) durch `const deviceId = await this.getOrCreateDeviceId();`.
- Füge auf **Modulebene** (vor `@Injectable`, nach den Imports) hinzu:
  ```ts
  /** Meta-Key der stabilen Geräte-ID (Design D1; UUIDv4). */
  export const DEVICE_ID_META_KEY = 'device.id';

  /**
   * UUIDv4 (Muster `app/sqlite.service.ts`): `crypto.randomUUID()` im
   * Android-WebView (HTTPS-Kontext); Fallback über
   * `crypto.getRandomValues`, dadurch auf jedem Pfad verfügbar.
   */
  function newId(): string {
    const cryptoObj = globalThis.crypto;
    if (cryptoObj?.randomUUID) {
      return cryptoObj.randomUUID();
    }
    const bytes = new Uint8Array(16);
    cryptoObj.getRandomValues(bytes);
    bytes[6] = (bytes[6] & 0x0f) | 0x40; // Version 4
    bytes[8] = (bytes[8] & 0x3f) | 0x80; // Variant 10
    const hex = Array.from(bytes, (b) => b.toString(16).padStart(2, '0')).join('');
    return `${hex.slice(0, 8)}-${hex.slice(8, 12)}-${hex.slice(12, 16)}-${hex.slice(16, 20)}-${hex.slice(20)}`;
  }
  ```
- Füge in die Klasse `BookingsRepository` eine private Methode hinzu (z. B. direkt vor `doSave`):
  ```ts
  /**
   * Stabile Geräte-ID lesen bzw. einmalig anlegen (Design D1; `device.id`,
   * UUIDv4). `save` ruft diese innerhalb der `saveChain` auf — die Anlage
   * (ein `meta`-Upsert) ist dadurch gegen parallele erste Saves serialisiert.
   * Der Upsert ist idempotent.
   */
  private async getOrCreateDeviceId(): Promise<string> {
    const existing = await this.meta.get(DEVICE_ID_META_KEY);
    if (existing !== null) {
      return existing;
    }
    const deviceId = newId();
    await this.meta.set(DEVICE_ID_META_KEY, deviceId);
    return deviceId;
  }
  ```
- Aktualisiere den JSDoc-Kommentar an `save` (aktuell Zeile ~47), der `OutboxService.getOrCreateDeviceId` referenziert — er muss nun auf die **lokale** `getOrCreateDeviceId`-Methode verweisen (keine Referenz mehr auf `OutboxService`).

### 1.3 `$D/bookings.repository.spec.ts` ändern
- Import (aktuell Zeile 11) `import { DEVICE_ID_META_KEY, OutboxService } from './outbox';` → `import { DEVICE_ID_META_KEY } from './bookings.repository';`.
- Entferne `let outbox: OutboxService;` (aktuell Zeile 122) und `outbox = env.get(OutboxService);` (aktuell Zeile 137).
- Entferne `OutboxService` aus den drei Provider-Listen (aktuell Zeilen 132, 358, 417) → jeweils `[LocalDatabase, MetaService, BookingsRepository]`.
- (k)-Test „update/discard auf confirmed Buchung" (aktuell Zeile ~472): ersetze `await outbox.confirmOutbox(result.operationId);` durch direktes Setzen des Status im (Fake-)State:
  ```ts
  // Status direkt im (Fake-)State auf 'confirmed' setzen — ohne
  // OutboxService; testet das reale not_queued-Verhalten.
  const confirmedRow = dbState.outbox.get(result.operationId)!;
  confirmedRow.status = 'confirmed';
  ```
  (`dbState` ist im Test-Scope bereits vorhanden — die Tests nutzen `dbState.outbox`.)

### 1.4 `$D/capacitor-sqlite-fake.ts` ändern
- **Import-Block** (aktuell Zeilen 2–17): `INSERT_OUTBOX` und `DELETE_OUTBOX` zu den Imports aus `./statements` hinzufügen (fehlen aktuell).
- **`OUTBOX_STATEMENTS`-Objekt komplett löschen** (aktuell Zeile ~219–232: die 9 hardgecodeten String-Einträge `insert`, `selectByOperation`, `updateModify`, `updateConfirm`, `selectAll`, `selectCounts`, …) — sie duplizieren die kanonischen Statements aus `statements.ts`.
- Handler `if (stmt === OUTBOX_STATEMENTS.insert)` (aktuell Zeile ~456) → `if (stmt === normalize(INSERT_OUTBOX))`.
- Handler `if (stmt === OUTBOX_STATEMENTS.delete)` (aktuell Zeile ~529) → `if (stmt === normalize(DELETE_OUTBOX))`.
- **Diese Handler löschen** (nur von `outbox.ts` genutzt): `updateModify` (~494), `updateConfirm` (~506), `selectByOperation` (~617), `selectAll` (~630), `selectCounts` (~638).
- **`DELETE_BOOKING`-Handler** (aktuell Zeile ~430–455) auf `ON DELETE CASCADE` umstellen (Option A): entferne den `if (state.foreignKeys) { … throw … }`-Block; behalte den Cascade-Löschteil:
  ```ts
  if (stmt === normalize(DELETE_BOOKING)) {
    const bookingId = String(values[0]);
    state.bookings.delete(bookingId);
    // ON DELETE CASCADE (splits.booking_id REFERENCES bookings.id):
    // abhängige Splits werden mitgelöscht, wenn sie nicht vorher explizit
    // per DELETE_SPLITS entfernt wurden.
    for (const [id, row] of [...state.splits]) {
      if (row.booking_id === bookingId) {
        state.splits.delete(id);
      }
    }
    return;
  }
  ```
- **Unverändert** lassen: Handler `normalize(UPDATE_OUTBOX_PAYLOAD)` (~516) und `normalize(SELECT_OUTBOX_BY_BOOKING)` (~621).

### 1.5 `$D/statements.ts` — Kommentar korrigieren (aktuell Zeile ~146)
- `/** Buchung löschen (Splits fallen im echten SQLite über ON DELETE CASCADE; der Fake modelliert das bei \`foreignKeys: true\` explizit). */`
- → `/** Buchung löschen (Splits fallen im echten SQLite über ON DELETE CASCADE; der Fake modelliert die Cascade ebenfalls). */`

## Aufgabe 2 (P2-2, Option A): B10 in `$D/schema.spec.ts` ergänzen (echte SQLite via `node:sqlite`)

`schema.spec.ts` nutzt das bestehende Setup: `initSchema(db)` (`PRAGMA foreign_keys = ON` + `SCHEMA_STATEMENTS`), `runScript(db, sets)` (BEGIN…COMMIT), `executeSet(db, set)`, `querySet(db, set)`.

- **Importe** aus `./statements` ergänzen: `INSERT_BOOKING, INSERT_SPLIT, INSERT_OUTBOX, DELETE_BOOKING, DELETE_SPLITS, UPDATE_OUTBOX_PAYLOAD` (bisher nur `SCHEMA_STATEMENTS` + Meta-Helfer).
- Neuen Block `describe('Batch-B-Speicherinvarianten (echte SQLite)', …)` mit `beforeEach` (frische DB + `initSchema(db)`), analog zum bestehenden Batch-A-Block.
- **Spaltenreihenfolgen (exakt, aus `statements.ts`):**
  - `INSERT_BOOKING` (8 Werte): `[id, date, type, account_id, total_amount_cents, note, base_revision, created_at_utc]`
  - `INSERT_SPLIT` (11 Werte): `[id, booking_id, amount_cents, pool_id, category_id, note, tax_relevant, tax_category_id, tax_category_name, marked_amount_cents, tax_note]`
  - `INSERT_OUTBOX` (10 Werte): `[operation_id, device_id, type, booking_id, payload, payload_hash, expected_base_revision, status, created_at_utc, updated_at_utc]`
  - `UPDATE_OUTBOX_PAYLOAD` (5 Werte): `[payload, payload_hash, updated_at_utc, operation_id, status]`
- Hilfsfunktion (nicht-tax Splits; `tax_relevant` usw. = `null`):
  ```ts
  const TS = '2026-11-01T00:00:00.000Z';
  function insertBookingWithSplits(
    db: DatabaseSync,
    bookingId: string,
    total: number,
    splits: number[],
  ): void {
    runScript(db, [
      {
        statement: INSERT_BOOKING,
        values: [bookingId, '2026-11-01', 'expense', 'acc-1', total, 'note', null, TS],
      },
      ...splits.map((amount, i) => ({
        statement: INSERT_SPLIT,
        values: [
          `${bookingId}-s${i + 1}`, bookingId, amount, 'pool-1', 'cat-1',
          null, null, null, null, null, null,
        ],
      })),
    ]);
  }
  ```
- **Zählen-Helfer** via `SELECT COUNT(*)` (z. B. `const n = (t: string) => Number((db.prepare(`SELECT COUNT(*) AS c FROM ${t}`).get() as { c: number }).c);`).

### Die 5 B10-Fälle (alle mit echter `node:sqlite`-DB)
1. **Atomarität:** `runScript` mit gültiger Buchung + gültigem Split + **letzte** `INSERT_OUTBOX`-Zeile mit `booking_id` = `'bk-ghost'` (existiert nicht → FK-Verletzung auf `outbox.booking_id`). Der Skript-Aufruf wirft; danach **ROLLBACK** (Transaktion ist noch offen — nach dem Catch `db.exec('ROLLBACK')`), dann prüfen: `bookings` = 0, `splits` = 0, `outbox` = 0 (keine Teilzeilen).
2. **Cascade (Option A, korrigiert):** `insertBookingWithSplits(db, 'bk-c', 300, [200, 100])`; dann `executeSet(db, { statement: DELETE_BOOKING, values: ['bk-c'] })` (kein `DELETE_SPLITS` vorher). Prüfen: `bookings` = 0, `splits` = 0 (Splits wurden cascadiert).
3. **`UPDATE_OUTBOX_PAYLOAD` auf `confirmed` = 0 Zeilen:** Buchung + Split + `INSERT_OUTBOX` (`status='queued'`); dann `UPDATE outbox SET status = 'confirmed' WHERE operation_id = ?`; dann `UPDATE_OUTBOX_PAYLOAD` mit `status='queued'` ausführen. Prüfen: Outbox-Zeile **unverändert** (`status='confirmed'`, Payload wie zuvor), Buchung + Splits intakt.
4. **Unique `operation_id`:** `INSERT_OUTBOX` mit `operation_id='op-x'` (`status='queued'`, gültiges `booking_id`); zweites `INSERT_OUTBOX` mit gleicher `operation_id='op-x'` → **wirft** (UNIQUE). Prüfen: `outbox` hat **genau 1** Zeile (`op-x`, der erste Payload).
5. **Splits-Ersatz bei update:** `insertBookingWithSplits(db, 'bk-u', 300, [200, 100])` (IDs `bk-u-s1`, `bk-u-s2`); dann `runScript(db, [DELETE_SPLITS('bk-u'), INSERT_SPLIT(bk-u-s1, 150, …)])`. Prüfen: `splits` = genau 1 Zeile (ID `bk-u-s1`, Betrag 150), keine `bk-u-s2`, keine doppelten IDs, Buchung intakt.

## Verifikation (MUST, alle grün)
Aus `frontend/`:
- `npm run lint`
- `npm run test` (erwartet: grün; die Anzahl kann sich leicht ändern, da `outbox.spec.ts` weg und B10/`schema.spec.ts` neu — wichtig ist **0 Fehlschläge**)
- `npm run build:mobile`

## Commit
EIN Commit auf `step/8b-mobile-outbox`:
`Step 8: Batch B — P2-Nachgang (outbox.ts entfernen, B10 auf echter SQLite, Option A)`

Kein Push, kein PR (macht der Controller).
