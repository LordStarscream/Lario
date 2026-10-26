# Aufgabe: Schritt 8 — Produktive mobile Offline-Erfassung
_Status: aktiv — Batch A fertig (PR #15 gemerged), Batch B fertig inkl. P2-Nachgang (`abc521b`) + Transaktions-Nachgang (`f0ef94c`, PR #16 offen) · Last updated: 26. Okt. 2026_

**Branches:**
- Lario → `step/8-mobile-offline`  (Worktree: `/tmp/lario-head-check`)

**Gestartet:** 22. Okt. 2026

Quelle: `docs/Finanzplanung_Implementierungsplan.md` §5, Schritt 8.
Voraussetzungen: Schritt 2 (PR #3) und Schritt 7/7a (API + Desktop) — erfüllt.

## Ziel

Die Android-App erfasst Ausgaben vollständig offline: Ausgabeformular als primäre
Ansicht (Betrag, Konto, Pool/Kategorie je Split, Splits, Notiz), Steuerkennzeichnung
per Schalter oder Kategorievorbelegung, Stammdatensnapshot, lokale Entwürfe und Outbox.
Ausgabe + Operation in **einer** lokalen SQLite-Transaktion; UI bestätigt erst nach
Commit. Eigene unsynchronisierte Eingaben anzeigen/ändern/verwerfen. Nach einmaliger
Einrichtung (Snapshot) keine Abhängigkeit von Host oder Internet bei der Erfassung.
Kein Senden (Schritt 9/10), keine mobile Saldenengine (AGENTS.md).

## Plan-Abnahmen (verbindlich)

- Prüfen: 20 Ausgaben inkl. Splits ohne Host, hart beenden/neu starten; Doppeltippen
  erzeugt keine zweite Eingabe; verworfene unsynced Eingabe wird nicht später
  hochgeladen; ungültige Eingabe landet nicht in der Outbox.
- Fertig: Erfassung benötigt nach Einrichtung weder Host noch Internet.

## Entscheidungen

- **D1 Umfang:** Vorhaben ist Feature von Schritt 14 (Feature-Matrix des Plans) —
  Formular hat jetzt Notiz, Vorhaben-Feld kommt in Schritt 14 (dok. Abweichung).
  Kein Netzwerk-Senden in Schritt 8.
- **D2 Neue Produktions-DB** `lario` (Prototyp-DB `lario-prototype` bleibt unverändert):
  `meta(key,value)`; Snapshot-Tabellen `accounts,pools,categories,tax_categories`
  (alle Status, Formular filtert auf aktiv); `bookings(id,date,type,account_id,
  total_amount_cents,note,base_revision,created_at_utc)`; `splits(id, booking_id→
  bookings ON DELETE CASCADE, amount_cents, pool_id, category_id, note, tax_relevant,
  tax_category_id, tax_category_name, marked_amount_cents, tax_note)`;
  `outbox(operation_id PK, device_id, type, booking_id, payload, payload_hash,
  expected_base_revision, status, created_at_utc, updated_at_utc)` (Modell Schritt 0);
  `drafts(id,name,data,created_at_utc,updated_at_utc)`. **D2a FK-Pragma
  (review-korrigiert, 23.10.):** Android 8.1.1 aktiviert FK bereits beim
  `open()` (`Database.java`: `setForeignKeyConstraintsEnabled(true)`); die
  frühere Annahme „Plugin aktiviert FKs nicht“ war falsch. Allerdings ist
  `PRAGMA foreign_keys = ON` INNEHALB einer Transaktion wirkungslos
  (SQLite-Semantik) — das frühere Pragma im transaktionalen Schema-Batch
  hätte nichts bewirkt. `LocalDatabase.init()` setzt es daher explizit
  außerhalb einer Transaktion (`transaction: false`) und verifiziert den
  wirksamen Zustand per `PRAGMA foreign_keys`: plattformunabhängig
  (Web/Browser-Fallback) und laut fehlend, falls ein Pluginwechsel die
  Aktivierung beim Öffnen entfernt. node:sqlite-Tests setzen das Pragma
  außerhalb von Transaktionen (D10). Zusätzlich bleibt die explizite
  Lösch-Reihenfolge (Splits vor Buchung) in der Verwerfen-Transaktion als
  Verteidigungstiefe.
- **D3 Snapshot:** Setup-View mit Host-URL + User/Passwort (lokal gemerkt) →
  `POST /api/v1/auth/login` → Token in `meta`; `GET /accounts|/pools|/categories|
  /tax-categories` → Snapshot-Tabellen in **einer** `executeSet`-Transaktion atomar
  ersetzt + `meta` fetchedAt. Token widerrufbar über Host-Logout; Schritt 9 verfeinert
  zu Geräte-Kopplung. Keine produktive Standard-URL.
- **D4 Outbox:** operationId `op-<JJJJ>-<MMDD>-<seq>` (Schritt-0-Beispiel
  `op-2026-1101-001`), seq = persistierter monotoner Geräte-Zähler (Start 1, 3-stellig,
  kein Reset). payload = exakt `ExpenseCommand`-Shape, feste Schlüsselreihenfolge,
  kein Whitespace, `expectedBaseRevision: null` bei Neuanlage. payloadHash =
  `sha256:<hex>` über SHA-256(UTF-8(payload)) — **reine TS-Implementierung** (kein
  crypto.subtle/Secure-Context-Risiko), NIST-Testvektoren. Schritt 8 schreibt nur
  `status='queued'`. Ändern (nur queued): Buchung + Payload/Hash in einer Transaktion
  ersetzen (gleiche operationId, Host hat sie nie gesehen). Verwerfen (nur queued):
  Buchung + Splits + Operation in einer Transaktion löschen.
- **D5 Speichern = eine Transaktion:** Buchung + Splits + Operation + Zähler in einem
  `executeSet`-Aufruf (Plugin: `transaction:true` Default; Android: begin/commit,
  bei Fehler rollback im finally). UI bestätigt erst nach Commit.
- **D6 Doppeltippen:** Button während Save gesperrt (`saving`-Signal → `disabled`);
  zweites Tippen ignoriert. Test: Doppelklick → exakt 1 Buchung + 1 Operation.
- **D7 Validierung (vor Transaktion, ungültig → nichts gespeichert):** Betrag > 0,
  max. 13 Stellen vor Komma (long-Cent-Grenze, wie Schritt-2-`parseAmount`); Datum
  `yyyy-MM-dd` gültig (Rückbuchung erlaubt); Konto = aktive Snapshot-Referenz; Splits ≥ 1,
  jeder > 0, Pool+Kategorie aktive Referenzen, **Summe = Total exakt (Cent)**;
  Steuer: „relevant“ → Steuerkategorie Pflicht, `markedAmount` ∈ [0, Splitbetrag],
  Standard = Splitbetrag; Kategorie-`taxPreset` übernimmt bei Kategorienwechsel,
  überschreibbar.
- **D8 UI:** Tabs **Erfassen** (primäre Startansicht: Betrag/Konto/Datum/Notiz,
  Splits mit Pool+Kategorie+Steuer, Quick-Actions „gleichmäßig“/„Rest in letzten
  Split“, Splitsummen-Abgleich live, Speichern, Entwurf speichern/laden/verwerfen),
  **Eingaben** (lokal + Status „ausstehend“, Detail: anzeigen/ändern/verwerfen mit
  Bestätigung), **Einrichtung** (Host-URL, Zugangsdaten, Verbinden+Snapshot,
  Geräte-ID, Snapshot-Stand, `/api/health`-Prüfung, Abmelden = Host-Logout +
  Token löschen). Zoneless (Signals, Schritt-2-Muster). Browser-Fallback: klare
  Meldung „lokal nur in der installierten Android-App“.
- **D9 Generierter Client:** `generate:client` erzeugt zusätzlich
  `apps/mobile/src/generated` (gleiche openapi.json/Optionen); `OpenAPI.BASE` aus
  Einrichtung zur Laufzeit, `OpenAPI.TOKEN` aus `meta`.
- **D10 Tests:** Plugin-Fake um `executeSet` mit Transaktions-/Constraint-Semantik
  erweitert; **echte SQLite-Tests über `node:sqlite`** (Node 26 eingebaut,
  `@types/node` devDependency, `// @vitest-environment node`, `PRAGMA foreign_keys = ON`
  wie auf dem Gerät (D2a): Schema, FK-Cascade, Unique-Constraints, Atomarität der
  Statement-Sequenzen aus `statements.ts`;
  SHA-256 NIST; Validierung; Doppeltippen (DOM); Neustart mit 20 Ausgaben; Abbruch
  ohne Teilzustand; Zoneless-DOM-Tests.
- **D11 Geräte-Abnahme (manuell):** APK, Setup gegen LAN-Host, Flugmodus: 20 Ausgaben
  inkl. Splits, hart beenden + Neustart → alles da; Doppeltippen → eine Eingabe.
- **D12 Worktree + Worker:** Die Batch-Implementierung läuft in separaten
  Worktrees mit beaufsichtigten Workern (start_worker). Die Worker sehen
  `.tasks/`, `.pi/` und `.git/` nicht (Guard-Schutz) — alle nötigen Design-
  Details stehen daher im Worker-Auftrag; Commits und Doku-Nachträge macht die
  Hauptsession. Worker-Checks: nur `npm run lint` / `npm run test` (per Wrapper-
  package.json im Worktree-Root); `build:mobile` führt die Hauptsession aus.

## Batch A — Speicherfundament (P1) — fertig (23. Okt. 2026)

- [x] `LocalDatabase`-Service: DB `lario` öffnen, Schema anlegen (idempotent),
      `executeSet`-Wrapper mit get/set-Transaktion; zentrale SQL-Vorlagen in
      `statements.ts` (alle INSERT/UPDATE/DELETE/SELECT als konstante Arrays)
- [x] `meta`-Service (get/set/delete), `@types/node` devDependency,
      `node:sqlite`-Tests: Schema läuft fehlerfrei, FK-Cascade, Unique-Constraints,
      executeSet-Atomarität (Statement 2/5 schlägt fehl → alles rollt zurück)
- [x] Mobiler generierter Client: `generate:client`-Skript erzeugt
      `apps/mobile/src/generated`; Test, dass Client die Catalog-Endpoints kennt
      (Client: c7a1595; Import-Check: `generated-client.spec.ts`)

Ergebnis: `frontend/apps/mobile/src/app/db/` — `statements.ts` (Schema + Meta-Helfer),
`local-database.ts` (RW-Connection-Guard, geteiltes Init-Promise, FK-Pragma mit
`transaction: false` + Verifikation, Schema in transaktional executeSet),
`meta.service.ts` (inject()-Muster), `capacitor-sqlite-fake.ts` (geteilter Test-Fake
mit vi.hoisted-State; an den echten 8.1.1-Vertrag ausgerichtet: values-Pflicht je
Element, transaction-Option, FK-State; aus dem App-Build in `tsconfig.app.json`
ausgeschlossen, weil er Vitest-Globals nutzt) + 3 Specs: `local-database.spec.ts`
(jsdom/Fake: init-Lifecycle, P1/P2-Regressionen, Idempotenz, Neustart-Simulation,
iOS/Android-mapRows, MetaService), `schema.spec.ts` (echte SQLite via node:sqlite:
Idempotenz, FK-Cascade, Unique, FK-Verletzung, Atomarität, D2a-Pragma-Semantik,
Meta-Helfer), `generated-client.spec.ts` (Import-Check).

**Review-Nachgang (23.10., PR #15 Review):**
- **P1 (Blocker, behoben):** Android 8.1.1 liest `values` bei JEDEM
  executeSet-Element zwingend (`Database.executeSet()`, Zeile 458) — der
  Initialisierungsbatch hatte nur `{ statement }` und wäre abgebrochen.
  Jetzt: `toPluginSets()` normalisiert an der Plugin-Grenze jedes Element
  auf `values: []` (init + öffentlicher executeSet-Wrapper); der Fake
  ablehnt Sets ohne `values` („ExecuteSet: No value for values").
  Regressionstests: init mit strengem Fake (alle Elemente tragen values),
  `db.executeSet([{statement: 'DELETE FROM meta'}])` → Grenze bekommt
  `values: []`.
- **P2 (behoben):** Parallele `init()`-Aufrufe liefen `createConnection()`
  doppelt (zweiter wirft „already exists"). Jetzt: gemeinsames Promise
  `initializing` serialisiert create/open/Pragma/Schema; nach Fehlschlag
  wird es freigegeben, `connectionCreated` bleibt nur nach erfolgreichem
  Create. Regressionstests: 2 parallele init mit Gate → alle erfolgreich,
  genau 1 create + 2 Batches; fehlgeschlagener init → Retry ohne
  doppelte Connection.
- **D2a-Korrektur (Doku + Verhalten):** siehe D2 oben — Pragma lief früher
  im transaktionalen Batch (wirkungslos), läuft jetzt außerhalb
  (`transaction: false`) + Verifikation per `PRAGMA foreign_keys`; Fake
  spiegelt Android (open() aktiviert FK; Pragma in Transaktion No-Op);
  neue node:sqlite-Test fängt die No-Op-Semantik.

Prüfungen: `npm run lint` grün; `npm run test`: host 14 Dateien/130 Tests, mobile
5 Dateien/46 Tests; `npm run build:mobile` grün. Plan-Abnahme (h) damit abgedeckt.

## Batch B — Outbox + Buchungs-Speicher (P2) — fertig (inkl. P2-Nachgang)

_Worktree `/tmp/lario-step8b`, Branch `step/8b-mobile-outbox`, vollständige Vorgabe:
`.tasks/worker-step8b-task.md` (Implementierung, Commit `ad9e945`) +
`.tasks/worker-step8b-p2fix.md` (P2-Nachgang, Commit `abc521b`). PR #16 offen.
Checks: `npm run lint`/`test`/`build:mobile` (aus `frontend/`)._

- [x] SHA-256 in TS (reine Implementierung) + NIST-Referenzvektoren +
      deterministischer Payload-Hash `sha256:<hex>`
- [x] operationId-Generator (`op-<JJJJ>-<MMDD>-<seq>`, persistierter monotoner Zähler,
      kein Reset über Neustart) + Tests
- [x] `BookingsRepository`: speichern (eine `executeSet`-Transaktion: Buchung +
      Splits + Outbox-Zeile + Zähler), ändern (nur queued), verwerfen (nur queued,
      Cascade), laden (Liste + Detail mit Outbox-Status), `DraftsRepository`,
      `getOrCreateDeviceId` lokal
- [x] Fake um `executeSet` mit Transaktions-/Constraint-Semantik erweitern
- [x] Service-Tests: 20 Ausgaben + Neustart-Persistenz; Doppeltippen → 1 Eingabe;
      Verwerfen → Outbox leer; ungültig → weder `bookings` noch `outbox`;
      Abbruch beim 2. Statement → kein Teilzustand

**Ergebnis (23.10.2026):** 17 Dateien (sha256/operation-id/expense-payload/
bookings+drafts-Repositories + Fake-Erweiterung), Commit `ad9e945`.
**Unabhängige Review:** Kern B1–B4 ✅ keine Befunde; Speicher B5–B10 → 2×P2, 3×P3,
kein P1. **P2-Nachgang (Commit `abc521b`, Option A — Nutzerentscheidung 23.10.):**
P2-1 OutboxService (outbox.ts/outbox.spec.ts) entfernt — toter Code + paralleler
Mutationspfad; getOrCreateDeviceId ins Repository; Fake-Cascade immer an.
P2-2 B10 auf echter SQLite (schema.spec.ts, 5 Fälle: Atomarität/Outbox-FK,
Cascade, Update-auf-confirmed, Unique operation_id, Splits-Ersatz) + DDL
outbox.booking_id `REFERENCES bookings(id) ON DELETE CASCADE`. **Checks:**
lint host+mobile grün, 225 Tests (host 130 + mobile 95), build:mobile grün.
Keine Host-App-/Domain-Änderung, keine Produktivserver-URL.

**Transaktions-Nachgang (26.10.2026, Commit `f0ef94c`, Review-Worktree):**
`LocalDatabase.withWriteTransaction` — native Schreibtransaktion
(BEGIN IMMEDIATE → Callback → COMMIT, bei Fehler ROLLBACK, kein
Teilzustand). Schreiboperationen (`executeSet`, `withWriteTransaction`)
werden auf der Connection-Operations-Kette serialisiert: eine offene
Transaktion hält die Connection, andere Schreibzugriffe warten bis nach
COMMIT/ROLLBACK. Leseoperationen (`query`) laufen außerhalb der Kette;
auf derselben Connection ist der Zustand der offenen Transaktion
sichtbar. Der Transaktions-Callback erhält Query-/Write-Methoden auf
derselben Connection ohne eigene automatische Transaktion
(`transaction: false`, keine Verschachtelung).
`BookingsRepository.getOrCreateDeviceId`: Read-Check-Write
(SELECT→UPSERT) in einer atomaren Schreibtransaktion — kein TOCTOU-
Fenster (zuvor: `meta.get` + `meta.set` als zwei getrennte
Transaktionen, nur durch die `saveChain`-Serialisierung geschützt).
Fake: manuelle Transaktionsunterstützung (Snapshot beim BEGIN,
Restore beim ROLLBACK, Rollback uncommitteter Transaktion in
`simulateRestart`, Verschachtelungs-Schutz gegen `executeSet`-
Transaktionen). **Motivation:** Schritt 9 (Sync) braucht Read-Check-
Write-Atomarität mit Statuswächtern auf der Outbox (Review P2-1 hatte
den parallelen Mutationspfad ohne SQL-Statuswächter als Divergenz-Risiko
benannt); die Geräte-ID ohne TOCTOU; Fundament für Batches C–E.
8 neue Tests (Serialisierung, COMMIT/ROLLBACK, Sichtbarkeit uncommitteter
Schreibzugriffe auf derselben Connection, Fehlertoleranz der Operations-
Kette) + Test-Isolation (`afterEach restoreAllMocks`). **Checks:**
lint grün, 233 Tests (host 130 + mobile 103), build:mobile grün,
gepusht 26.10.2026 (PR #16 aktualisiert).

## Batch C — Snapshot + Setup (P3)

_Design-Fakten (verifiziert 23.10., Host-Seite):_
- **Login:** `POST /api/v1/auth/login` `{username, password}` → 200
  `{token, expiresInMinutes}` / 401 `ErrorResponse{error:{code:'unauthorized',…}}`
  (`src/Lario.Host/Api/AuthEndpoints.cs`). **Logout:** `POST /api/v1/auth/logout`
  (Bearer) → 204. **Health:** `GET /api/health` → `{status:'ok', dataDirectory}`
  (`Program.cs:158`).
- **Stammdaten:** `GET /api/v1/accounts|pools|categories|tax-categories`
  (`MasterDataEndpoints.cs`), Bearer-Token. DTOs (`MasterDataDtos.cs`):
  `AccountDto{id,name,type('bank'|'savings'),isInterestBearing(bool),
  status('active'|'archived'),baseRevision}`; `PoolDto{id,name,status,
  baseRevision}`; `CategoryDto{id,name,parentId?,status,baseRevision,
  taxPreset?}`; `TaxCategoryDto{id,name,parentId?,status,baseRevision}`;
  `TaxPresetDto{taxRelevant? (bool|null), taxCategoryId?, taxCategoryName?,
  inheritToChildren(bool), baseRevision}`. Enumerations als stabile Strings.
- **Generierter Client** (`apps/mobile/src/generated`): `LarioHostService.
  authLogin(LoginRequest)/authLogout()/getHealth()`; `MasterDataEndpointsService.
  listAccounts()/listPools()/listCategories()/listTaxCategories()`; `OpenAPI.
  BASE`/`OpenAPI.TOKEN` zur Laufzeit setzen (D9).
- **Snapshot-Schema (Batch A, fertig):** `accounts(id,name,type,
  is_interest_bearing INTEGER,status,base_revision)`, `pools(id,name,status,
  base_revision)`, `categories(id,name,parent_id,status,base_revision,
  tax_preset_json TEXT)`, `tax_categories(id,name,parent_id,status,
  base_revision)` — passt 1:1 zu den DTOs; `tax_preset_json` =
  `JSON.stringify(TaxPresetDto)` oder NULL.
- **Mapping DTO→DB (exakt, verifiziert in `src/generated/models/`):**
  Feldnamen camelCase→snake_case (`parentId→parent_id`, `baseRevision→
  base_revision`, `isInterestBearing→is_interest_bearing` als 0/1).
  `baseRevision` ist `number | string` → beim INSERT mit `Number(...)`
  normalisieren. `CategoryDto.taxPreset` heißt exakt `taxPreset` (Typ
  `null | TaxPresetDto`) → `tax_preset_json`. `isInterestBearing` ist `boolean`
  → 0/1. `OpenAPI.BASE` (Default `http://localhost:8080`) und `OpenAPI.TOKEN`
  sind mutable statische Felder in `src/generated/configuration/OpenAPI.ts` —
  bei connect vor den Calls setzen, bei disconnect `TOKEN=undefined`.
- **Device-ID:** UUIDv4-Hilfsfunktion (Muster `app/sqlite.service.ts:170-180`:
  `crypto.randomUUID()` mit `crypto.getRandomValues`-Fallback); Key
  `device.id` in meta, einmalig generiert, stabil.
- **Meta-Keys (Setup):** `device.id`, `auth.host`, `auth.username`,
  `auth.password` (D3 „lokal gemerkt“ — Doku-Limitation: Klartext in der
  lokalen Geräte-DB), `auth.expires_at` (UTC-ISO, aus `expiresInMinutes`),
  `snapshot.fetched_at` (UTC-ISO, via SnapshotRepository).
- **Fluss `connect(host, user, pass)`:** login → Token → 4× list parallel
  (Promise.all) → `SnapshotRepository.replace()` (EINE executeSet: 4× DELETE +
  alle INSERT + `snapshot.fetched_at`) → auth.* + expires meta setzen.
  **`disconnect()`:** best-effort `authLogout` (Fehler nicht fatal),
  auth.*-Meta löschen, Snapshot bleibt (Offline-Erfassung bleibt möglich, D8).
- **Form-Referenzen:** Formular filtert auf `status='active'` (D2); Steuer:
  D7 verlangt am FORM relevant→Steuerkategorie Pflicht (strenger als Host;
  Repository-B6 bleibt bei der Host-Regel: Kategorie optional bei relevant).

- [ ] `HostClient` (login → Token; GET accounts/pools/categories/tax-categories;
      health) auf Basis des generierten Clients, `OpenAPI.BASE`/`TOKEN` konfigurierbar
- [ ] `SnapshotRepository`: atomarer Snapshot-Wechsel (eine `executeSet`: alle
      Snapshot-Tabellen ersetzen + `meta` fetchedAt) + Test
- [ ] Einrichtung-View: Host-URL/Zugangsdaten, „Verbinden & Stammdaten laden“,
      Geräte-ID, Snapshot-Stand, Health-Prüfung, Abmelden (Host-Logout + Token löschen)
- [ ] Tests: Snapshot-Wechsel atomar; Token-Flow; Abmeldung

## Batch D — Ausgabeformular (P4)

- [ ] Erfassen-View (primäre Startansicht): Betrag/Konto/Datum/Notiz, Split-Liste
      (Betrag/Pool/Kategorie/Steuer je Split), Quick-Actions, live Summen-Abgleich,
      Validierungs-UX gemäß D7
- [ ] Steuerkennzeichnung: Schalter pro Split + Kategorie-`taxPreset`-Übernahme,
      markedAmount-Input (Standard = Splitbetrag, Obergrenze = Splitbetrag)
- [ ] Speichern (eine Transaktion, erst nach Commit Bestätigung, `saving`-Sperre),
      Entwurf speichern/laden/verwerfen
- [ ] Zoneless-DOM-Tests: gültige Ausgabe (2 Splits + Steuer) wird gespeichert;
      ungültig (Summenabweichung) nicht; Doppeltippen → 1 Buchung; Entwurf-Flow

## Batch E — Eingaben-View + Shell + Integration (P5)

- [ ] Tab-Shell (Erfassen/Eingaben/Einrichtung), Routing, Browser-Fallback-Meldung
- [ ] Eingaben-View: Liste (Datum, Total, Konto, Pools, Status), Detail mit
      Ändern (über Formular) und Verwerfen (mit Bestätigung)
- [ ] README-Abschnitt (mobile Offline-Erfassung, DBs, Grenzen: kein Senden in 8),
      `npm run lint`/`test`/`build:mobile` grün, Debug-APK bauen (`cap sync && build`)
- [ ] Geräte-Abnahme: APK installieren, Setup gegen LAN-Host, Flugmodus 20 Ausgaben,
      hartes Beenden/Neustart, Doppeltippen

## Abnahmekriterien (konkret übersetzt)

- (a) 20 Ausgaben inkl. Splits (2+ Splits, mit/ohne Steuerkennzeichnung) offline
      gespeichert; Neustart (Fake: frisches Prozessregister) → alle 20 + Splits +
      Outbox `queued` vorhanden.
- (b) Doppelklick auf Speichern → exakt 1 Buchung + 1 Operation.
- (c) Verworfene nie-gesendete Eingabe → Buchung/Splits/Operation in einer
      Transaktion weg; Outbox enthält nichts davon.
- (d) Ungültig (Splitsumme ≠ Total, Betrag 0/über Grenze, inaktive Referenz,
      markedAmount > Splitbetrag, relevant ohne Steuerkategorie) → sichtbarer Fehler,
      weder in `bookings` noch in `outbox`.
- (e) Fehler beim 2. von N Statements → Rollback, kein Teilzustand.
- (f) SHA-256 NIST grün; Payload-Hash deterministisch, Format `sha256:<64-hex>`.
- (g) operationIds: Format `op-<JJJJ>-<MMDD>-<seq>`, eindeutig, seq über Neustart
      monoton (persistiert).
- (h) node:sqlite: Schema-Statements fehlerfrei, FK-Cascade, Unique-Constraints.
- (i) Erfassung nach Snapshot ohne Host-Call (Fake: Host-Client nicht aufgerufen).
