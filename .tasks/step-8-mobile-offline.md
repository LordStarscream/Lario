# Aufgabe: Schritt 8 — Produktive mobile Offline-Erfassung
_Status: aktiv · Last updated: 22. Okt. 2026_

**Branches:**
- Lario → `step/8-mobile-offline`  (Worktree: `/home/mario/Projects/Lario-worktrees/step-8`)

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
  `drafts(id,name,data,created_at_utc,updated_at_utc)`. **D2a FK-Pragma:** Das
  Capacitor-Plugin aktiviert `PRAGMA foreign_keys` nicht (Android-Quelle 8.1.1
  geprüft) — SQLite hält FKs pro Verbindung standardmäßig AUS. `LocalDatabase.init()`
  führt daher nach `open()` `PRAGMA foreign_keys = ON` aus (pro neuem Prozess,
  nicht persistiert); node:sqlite-Tests setzen dasselbe Pragma (D10). Zusätzlich
  bleibt die explizite Lösch-Reihenfolge (Splits vor Buchung) in der Verwerfen-
  Transaktion als Verteidigungstiefe.
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

## Batch A — Speicherfundament (P1)

- [ ] `LocalDatabase`-Service: DB `lario` öffnen, Schema anlegen (idempotent),
      `executeSet`-Wrapper mit get/set-Transaktion; zentrale SQL-Vorlagen in
      `statements.ts` (alle INSERT/UPDATE/DELETE/SELECT als konstante Arrays)
- [ ] `meta`-Service (get/set/delete), `@types/node` devDependency,
      `node:sqlite`-Tests: Schema läuft fehlerfrei, FK-Cascade, Unique-Constraints,
      executeSet-Atomarität (Statement 2/5 schlägt fehl → alles rollt zurück)
- [ ] Mobiler generierter Client: `generate:client`-Skript erzeugt
      `apps/mobile/src/generated`; Test, dass Client die Catalog-Endpoints kennt

## Batch B — Outbox + Buchungs-Speicher (P2)

- [ ] SHA-256 in TS (reine Implementierung) + NIST-Referenzvektoren +
      deterministischer Payload-Hash `sha256:<hex>`
- [ ] operationId-Generator (`op-<JJJJ>-<MMDD>-<seq>`, persistierter monotoner Zähler,
      kein Reset über Neustart) + Tests
- [ ] `BookingsRepository`: speichern (eine `executeSet`-Transaktion: Buchung +
      Splits + Operation + Zähler), ändern (nur queued), verwerfen (nur queued,
      Cascade), laden (Liste + Detail mit Outbox-Status), `DraftsRepository`
- [ ] Fake um `executeSet` mit Transaktions-/Constraint-Semantik erweitern
- [ ] Service-Tests: 20 Ausgaben + Neustart-Persistenz; Doppeltippen → 1 Eingabe;
      Verwerfen → Outbox leer; ungültig → weder `bookings` noch `outbox`;
      Abbruch beim 2. Statement → kein Teilzustand

## Batch C — Snapshot + Setup (P3)

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
