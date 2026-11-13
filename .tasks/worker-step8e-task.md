# Batch E — Produktive Offline-Erfassung (Schritt 8) — SPEC (Worktree-Kopie)

> **Ablauf:** DU (Worker) implementierst NUR die dir im Task-Prompt zugewiesenen Abschnitte,
> indem du Dateien in deinem Worktree `/tmp/lario-step8e` schreibst/bearbeitest.
> **Keine Commits, kein Staging, keine Checks, keine Shell** — das macht der Controller danach.
> Lies nur Dateien innerhalb dieses Worktrees (Pfade außerhalb werden blockiert und stoppen dich).
> Behaupte in deiner Abschlussmeldung KEINERLEI Test-/Lint-/Build-Ergebnisse.

**Ziel:** Die mobile App wird zur produktiven Offline-Erfassung: Tab-Shell (Erfassen / Eingaben / Einrichtung) mit Routing, Ändern-Modus im Erfassen-Formular, Eingaben-Liste & Detail (mit Verwerfen). Der Schritt-2-Prototyp (app.ts/app.html + sqlite.service + host.service) wird ersetzt und gelöscht.
**Kein Senden in Schritt 8** (Outbox bleibt `queued`; Synchronisation ist Schritt 9).
**Keine neuen Abhängigkeiten.** Keine Host-Änderungen. Keine Schema-Änderungen. Keine Änderungen an `validateExpense`, Payload/Hash-Logik, Outbox- oder Snapshot-Repositories.

## Stand (Batches A–D, alle gemerged)

- `db/`: LocalDatabase (DB `lario`), MetaService, SnapshotRepository (D3), BookingsRepository mit `save`/`update`/`discard`/`list`/`get` (Batches B+D), `BookingValidationError`/`BookingStateError`.
- `entry/`: EntryComponent (Neu-Erfassung, Validierung, Snapshot-Referenz-Check, Doppel-Tipp-Sperre, formGeneration-Guard, Entwürfe, `fromFormModel`), `entry-form.model.ts` (EntryFormModel, parseAmount, validateExpense, formatCents).
- `setup/`: SetupComponent (Login/Status/Logout/Health) + `HostClient`.
- **Prototyp bereits vom Controller gelöscht:** `app/sqlite.service.ts`, `app/sqlite.service.spec.ts`, `app/host.service.ts`, `app/app.spec.ts` (alte Prototyp-Spec). `app.ts`/`app.html` enthalten noch den alten Prototyp-Code → werden in Abschnitt 1 ersetzt.
- Tests laufen gegen `capacitor-sqlite-fake.ts` (jsdom) — `dbState` hält den DB-Zustand pro Test.

## 1. Routing + Tab-Shell

### 1.1 `app.config.ts` — Router

```ts
export const routes: Routes = [
  { path: '', pathMatch: 'full', redirectTo: 'erfassen' },
  { path: 'erfassen', loadComponent: () => import('./entry/entry.component').then((m) => m.EntryComponent) },
  { path: 'erfassen/:bookingId', loadComponent: () => import('./entry/entry.component').then((m) => m.EntryComponent) },
  { path: 'eingaben', loadComponent: () => import('./entries/entries.component').then((m) => m.EntriesComponent) },
  { path: 'eingaben/:bookingId', loadComponent: () => import('./entries/entries-detail.component').then((m) => m.EntriesDetailComponent) },
  { path: 'einrichtung', loadComponent: () => import('./setup/setup.component').then((m) => m.SetupComponent) },
  { path: '**', redirectTo: 'erfassen' },
];
```
`appConfig` bekommt `provideRouter(routes)`. `routes` wird exportiert (Tests).

### 1.2 `app.ts` / `app.html` — Shell (Prototyp-Code komplett ersetzen)

- `readonly isNative = inject(LocalDatabase).isNative;`
- **Native:** `<h1>Lario</h1>` + `<nav>` mit drei `<a [routerLink]>` (Erfassen → `/erfassen`, Eingaben → `/eingaben`, Einrichtung → `/einrichtung`), jeweils `routerLinkActive="active"` (klassenbasiert; alle drei müssen markiert werden). Darunter `<router-outlet />`.
- **Browser-Fallback (D8):** wenn `!isNative` — keine Tabs, keine Outlet, klare Meldung: „Browser-Vorschau: Der lokale Speicher ist nur in der installierten Android-App verfügbar (nicht in der Browser-Vorschau). Die Offline-Erfassung funktioniert nur im Android-Paket."

### 1.3 Prototyp löschen

**Bereits vom Controller erledigt** (sqlite.service.ts, sqlite.service.spec.ts, host.service.ts, app.spec.ts). `main.ts` bleibt unverändert. Die alte Prototyp-DB `lario-prototype` wird nirgends mehr angesprochen.

## 2. Ändern-Modus im Erfassen-Formular

### 2.1 `entry-form.model.ts` — `fromBookingDetail`

Neue exportierte Funktion (kein Snapshot-Parameter — Referenz-Namen werden nicht benötigt, rohe IDs werden von der UI per Fallback aufgelöst):
```ts
export function fromBookingDetail(detail: LocalBookingDetail): EntryFormModel
```
- `date` = `detail.date`; `amountInput` = `formatCents(detail.totalAmountCents)`; `accountId` = `detail.accountId`; `note` = `detail.note ?? ''`.
- Splits (Reihenfolge aus `detail.splits`, Keys `s1…sN`, `id` = `s<N>`): `amountInput` = `formatCents(split.amountCents)`, `poolId`, `categoryId`, `note = split.note ?? ''`; `tax`:
  - `taxRelevant` false → `{ relevant: false }`
  - true → `{ relevant: true, taxCategoryId, taxCategoryName, markedAmountInput: split.markedAmountCents === null ? '' : formatCents(split.markedAmountCents), note: split.taxNote ?? '' }`
- **Wichtige Semantik:** Referenz-IDs aus der Buchung werden 1:1 übernommen — auch wenn die Referenz im Snapshot archiviert/fehlend ist. Steuername-Entkopplung (D11): `taxCategoryName` kommt aus der Buchung, nicht aus dem Snapshot.

### 2.2 `entry.component.ts` — Ändern-Modus

- `private readonly route = inject(ActivatedRoute);`
- `readonly editingBookingId = signal<string | null>(null);`
- `ngOnInit`: nach dem Snapshot-Load (vorbestehende Reihenfolge beibehalten):
  ```ts
  const bookingId = this.route.snapshot.paramMap.get('bookingId');
  if (bookingId !== null) await this.activateEdit(bookingId);
  ```
- `activateEdit(bookingId: string): Promise<void>`:
  - `const detail = await this.bookingsRepository.get(bookingId);`
  - `detail === null` → `saveStatus.set('error')`, `saveMessage.set('Buchung nicht gefunden — das Formular bleibt unverändert.')`; **kein** Form-Reset, `editingBookingId` bleibt null.
  - sonst: `this.editingBookingId.set(bookingId)`; `this.setForm(fromBookingDetail(detail))` — **über den bestehenden `setForm`-Weg** (formGeneration-Counter läuft mit; bestehende formGeneration-Logik NICHT umgehen). `saveStatus` → `'idle'`, `saveMessage` → null.
- `save()`: Zweigung **nach** `toLocalInput(...)` + Null-Check und dem Erfassen von `savedFormGeneration`/`saving.set(true)` (bestehende Struktur 1:1 beibehalten), **vor** dem Repository-Aufruf:
  ```ts
  const editingId = this.editingBookingId();
  const result = editingId === null
    ? await this.bookingsRepository.save(input, new Date())   // bestehend
    : await this.bookingsRepository.update(editingId, input, new Date());
  ```
  - Erfolg: `saveStatus 'saved'`; Nachricht: Neu → bestehende „Ausgabe gespeichert …"; Änderung → `Ausgabe geändert (Buchung ${result.bookingId}) — Payload und Hash ersetzt, Operation-ID bleibt.`
  - Reset **im bestehenden Generations-Block** (`if (this.formGeneration === savedFormGeneration)`): `formGeneration += 1`, `this.form.set({ ...createDefaultForm(todayInBerlin()), accountId })` (Konto bleibt erhalten) **und** `this.editingBookingId.set(null)`. Liegt die Generation nicht mehr vor (Formular wurde während des Saves geändert), bleibt der Ändern-Modus aktiv (konsistent mit dem bestehenden Verhalten).
  - Fehlerpfade: wie bestehend — `BookingValidationError` → `e.message`, sonst passende Vorsilbe („Ändern fehlgeschlagen" bzw. „Speichern fehlgeschlagen") + `describeError(e)` (`BookingStateError` aus `update` läuft über den sonst-Zweig).
- `cancelEdit(): void` — „Änderung verwerfen" (nur lokal, DB bleibt angetastet): Formular-Reset (`setForm(createDefaultForm())`), `editingBookingId.set(null)`, `saveStatus 'info'`, `saveMessage 'Änderung verworfen — die gespeicherte Ausgabe bleibt unverändert.'`
- Entwurf-Methoden (`saveDraft`, `loadDraft`, `discardDraft`): Guard am Anfang: `if (this.editingBookingId() !== null) { this.saveMessage.set('Keine Entwürfe während einer Änderung.'); this.saveStatus.set('info'); return; }`
- `createDefaultForm()` (bzw. bestehender Default): bleibt.

### 2.3 `entry.html` — Ändern-UI

- `@if (editingBookingId()) { }`-Block oberhalb/unterhalb des Headers: „Änderungen einer gespeicherten Ausgabe" + `{{ editingBookingId() }}` + Button `type="button"` „Änderung verwerfen" → `cancelEdit()`.
- Draft-Block (`@if (drafts().length > 0)` …): zusätzlich `!editingBookingId()` — während einer Änderung sind Entwürfe ausgeblendet (und methodisch ge-guardet).
- Speichern-Button-Label: `@if (editingBookingId()) { Änderungen speichern } @else { Ausgabe speichern }`.

## 3. Eingaben-Liste & Detail

### 3.1 Repository-Liste: Pools ergänzen

`LocalBookingListItem` bekommt `poolIds: string[]` (dedupliziert, Reihenfolge aus Splits).
`SELECT_BOOKING_LIST` (statements.ts) wird zu:
```sql
SELECT b.id, b.date, b.total_amount_cents, b.account_id, b.note,
       o.status AS outbox_status,
       (SELECT GROUP_CONCAT(DISTINCT s2.pool_id)
          FROM splits s2 WHERE s2.booking_id = b.id) AS pool_ids
FROM bookings b LEFT JOIN outbox o ON o.booking_id = b.id
ORDER BY b.date DESC, b.created_at_utc DESC
```
- **EIN-argumentiges `GROUP_CONCAT(DISTINCT …)`** (Standard-Trenner `,`) — die Zwei-Argument-Form mit DISTINCT braucht SQLite ≥ 3.44 und ist auf älteren Android-Geräten nicht verfügbar; in JS `row['pool_ids']` (string | null) → `null ? [] : split(',')`.
- `capacitor-sqlite-fake.ts`: SELECT_BOOKING_LIST-Handler auf die neue Spalte `pool_ids` erweitern (aus den Splits-Zeilen im Fake-State berechnen, dedupliziert, Reihenfolge = Split-Reihenfolge, Kommakollation wie SQLite).
- `bookings.repository.spec.ts`: (→ Tests-Worker) Fall `list()` liefert `poolIds`.

### 3.2 `entries/entries.component.ts` + `.html` (neu)

- `EntriesComponent` (Selector `app-entries`):
  - `ngOnInit`: `Promise.all([bookingsRepository.list(), snapshotRepository.loadSnapshot()])` → `items`-Signal (null = noch nicht geladen), `loadError`-Signal.
  - Name-Auflösung: Snapshot (`Map` id → Referenz mit Name); fehlende IDs → **rohe ID als Fallback** anzeigen (konsistent mit Entry-Listen).
  - Zeile pro Buchung: Fachdatum (`{{ item.date }}`), Total `formatCents(item.totalAmountCents)` + „ €", Kontoname, Pools (Namen durch „, " verbunden), Status: `queued` → **„ausstehend"**, sonst roher Statuswert (`confirmed`/`unknown` können in Schritt 8 nicht entstehen — Anzeige bleibt generisch).
  - Zeile ist `<a [routerLink]="['/eingaben', item.bookingId]">` (oder umgebender Link).
  - Leerer Zustand: „Keine lokalen Eingaben."
  - Load-Fehler: `loadError`-Nachricht.
- Kein „Neu"-Button nötig (Tab „Erfassen").

### 3.3 `entries/entries-detail.component.ts` + `.html` (neu)

- `EntriesDetailComponent` (Selector `app-entries-detail`):
  - Route-Parameter `bookingId` (`ActivatedRoute.snapshot.paramMap`); `ngOnInit`: `Promise.all([bookingsRepository.get(id), snapshotRepository.loadSnapshot()])`.
  - `detail === null` → Ansicht „Buchung nicht gefunden." + Link „Zurück zur Liste" (`/eingaben`).
  - Detail-Anzeige: Datum, Total (formatCents + „ €"), Konto (Name, Fallback rohe ID), Notiz („—" bei null), erstellt (`createdAtUtc` roher Wert), Status (queued → „ausstehend", sonst roh), Splits-Tabelle/Listen: Betrag, Pool, Kategorie, Notiz; Steuer-Block pro Split bei `taxRelevant`: „Steuerkategorie: {taxCategoryName}" + Teilbetrag (`markedAmountCents === null` → „voller Betrag", sonst formatCents) + Steuer-Notiz.
  - **„Ändern"**: `<a [routerLink]="['/erfassen', bookingId]">` — **nur bei `status === 'queued'`** (Repository-`update` erlaubt es auch nur dann; UI blendet es sonst aus).
  - **„Verwerfen"**: Zwei-Schritt-Bestätigung — erst Button „Verwerfen", dann Bestätigungsblock „Wirklich verwerfen? Buchung und Outbox-Eintrag werden endgültig gelöscht." mit „Ja, verwerfen" / „Abbrechen". „Ja": `await bookingsRepository.discard(bookingId)` → `this.router.navigate(['/eingaben'])`; Fehler (z. B. `BookingStateError`) → Fehlermeldung, keine Navigation.
  - Name-Auflösung wie Liste (Snapshot + roher-Id-Fallback).

## 4. Tests (alle jsdom, Fake-DB; bestehende Specs nicht abschwächen) — UMFANG VOM TESTS-WORKER

1. **`app.spec.ts` (NEU — die alte Prototyp-Spec wurde vom Controller gelöscht)** — Mock-Muster aus `entry.component.spec.ts` übernehmen (Capacitor-Mock-Fabrik, `Capacitor.isNativePlatform: () => true` bzw. `false`):
   - Native: Shell rendert 3 Tabs; `provideRouter(routes)` + `RouterTestingHarness`: Startpfad `''` leitet nach `/erfassen` weiter; `/eingaben` rendert `app-entries`; `/einrichtung` rendert `app-setup`.
   - Browser (`isNativePlatform` false): Fallback-Meldung wird gerendert, keine Tabs, kein `app-entry`.
2. **`entry-form.model.spec.ts`** — `fromBookingDetail`:
   - Rundreise: Buchung mit 2 Splits (1 mit Steuer relevant, `markedAmountCents` gesetzt; 1 ohne Steuer) → Model-Felder exakt (Beträge als Dezimalzeichen-Strings, `markedAmountInput`); `markedAmountCents === null` → `markedAmountInput === ''`.
   - Archivierter/fehlender Referenz-Id bleibt roh (wird nicht ersetzt).
   - `parseAmount(fromBookingDetail(…).amountInput)` === `totalAmountCents` (Roundtrip).
3. **`entry.component.spec.ts`** — Ändern-Modus (neue Fälle, bestehende bleiben):
   - `activateEdit(id)` füllt das Formular (Betrag, Konto, Splits inkl. Steuer-Teilbetrag) — via gerendertem Formular oder Formular-Zugriff (bestehendes Test-Muster folgen).
   - `activateEdit('missing')` → Fehlermeldung, `editingBookingId` bleibt null, Formular unverändert.
   - `save()` im Ändern-Modus → `bookingsRepository.update`-Pfad: `dbState` zeigt gleiche `operation_id`, neues `payload`/`payload_hash`, ersetzte Splits; danach Formular zurückgesetzt, `editingBookingId` null, Erfolgsmeldung.
   - Gerenderte Ansicht: im Ändern-Modus ist der Draft-Block ausgeblendet, Speichern-Label „Änderungen speichern".
4. **`entries.component.spec.ts` (NEU)**:
   - 2 Buchungen per `bookingsRepository.save` einspielen (eine mit 2 Splits auf verschiedenen Pools) + Snapshot per `snapshotRepository.replace` → Zeilen mit Datum, formatiertem Total, Kontonamen, beiden Pool-Namen, „ausstehend".
   - Unbekannte Konto-ID (Buchung ohne Snapshot-Eintrag) → rohe ID als Fallback.
   - Leerer Zustand: „Keine lokalen Eingaben."
5. **`entries-detail.component.spec.ts` (NEU)**:
   - Detail-Anzeige: alle Felder inkl. Steuer-Teilbetrag und „voller Betrag" bei `null`.
   - `Ändern`-Link vorhanden bei `queued`, Ziel `/erfassen/{id}`.
   - Verwerfen-Flow: Button → Bestätigungsblock → „Ja" → `dbState`: bookings/splits/outbox leer; Router-`navigate` nach `/eingaben` (Spy).
   - Unbekannte ID → „Buchung nicht gefunden."
6. **`bookings.repository.spec.ts`**: `list()` `poolIds` (siehe 3.1).

## 5. README (Wurzel `README.md`) — UMFANG VOM IMPLEMENTIERUNGS-WORKER

Abschnitt „Mobile App (Android, Schritt 2 Prototyp)" **ersetzen/erweitern** durch den produktiven Stand (Schritt 8):
- Drei Tabs **Erfassen / Eingaben / Einrichtung**; Routing; Browser-Vorschau zeigt Fallback-Meldung (lokal speichern nur im Android-Paket).
- **Erfassen**: Validierung + Referenzcheck gegen Snapshot, Doppel-Tipp-Sperre, Entwürfe, **Ändern** gespeicherter Ausgaben (Operation-ID bleibt).
- **Eingaben**: Liste (Datum/Total/Konto/Pools/Status „ausstehend"), Detail, **Verwerfen** mit Bestätigung (lokal, Outbox-Zeile mit).
- **Einrichtung**: Login (Token + Snapshot), Status, Health, Logout (Batch C).
- **Kein Senden in Schritt 8**: Eingaben bleiben lokal `queued` (Synchronisation = Schritt 9).
- **Datenhaltung**: Produktions-DB `lario` (eigene Dateien; Prototyp-DB `lario-prototype` nicht mehr verwendet); Metadaten (device.id, host.url, token, snapshot_loaded_at).
- Build-/Sync-/Install-Befehle und die LAN-/TLS-Notizen **beibehalten** (sie gelten weiter).
- „Entwicklungszustand": Schritt 8 fertig (Offline-Erfassung), nächster Schritt 9.

## 6. Constraints

- **Keine neuen Abhängigkeiten** (kein `@angular/material`, kein UI-Toolkit).
- **Keine Host-Änderungen.** Keine Schema-Änderungen (keine neue Tabelle/Spalte; nur die SELECT-Liste wächst).
- `db/`-Änderungen **nur**: `statements.ts` (SELECT_BOOKING_LIST), `bookings.repository.ts` (ListItem + Mapping), `capacitor-sqlite-fake.ts` (Row-Shape) + dazugehörige Spec-Erweiterungen.
- Bestehende Validierung (`validateExpense`), Payload/Hash, Outbox-Verhalten, formGeneration-Guard, Doppel-Tipp-Sperre: **nicht verändern** (nur um den Ändern-Zweig erweitern, wo spezifiziert).
- Zoneless (kein OnPush nötig), bestehende DI-Muster, UI-Texte Deutsch, Bezeichner Englisch.
- `entry/entry.component.ts` ist die einzige Stelle, die `bookingsRepository.update` aufruft.
- Lint-Regeln beachten.

## 7. Commits — NICHT VOM WORKER

Der Worker legt **keine** Commits an. Der Controller verifiziert (lint/test/build) und legt danach die Commits an.

## 8. Checks — NICHT VOM WORKER

Der Worker führt **keine** Checks aus (`worker_check` nicht verfügbar). Der Controller verifiziert nach Abschluss. Der Worker implementiert daher besonders sorgfältig (Typen, Imports, Template-Referenzen, Lint-Regeln) und behauptet in der Abschlussmeldung **keinerlei** Test-/Lint-/Build-Ergebnisse.
