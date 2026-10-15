# Schritt 7 – Produktive API und erste Desktop-Eingabe

**Status:** aktiv
**Branch:** `step/7-api-desktop-eingabe`
**Voraussetzung:** Schritt 3–6 (abgeschlossen, merged `140446d`)
**Plan:** `docs/Finanzplanung_Implementierungsplan.md` §5 „Schritt 7"
**Design:** `.tasks/step-7-design-paket.md` (Status: final)

## Abnahmekriterien (aus dem Plan)

- **A1:** Versionierte REST-Endpunkte (`/api/v1`) mit lokaler Authentifizierung; alle Kernfunktionen 3–6 erreichbar.
- **A2:** DTOs + verständliche Validierungsfehler (400/409 mit `code`, `message`, bei Revisionskonflikt `expectedBaseRevision`/`actualBaseRevision`).
- **A3:** Referenzfälle über die API: Korrektur 85→80 (keine zweite Buchung), Teilrückzahlung 10 €, Korrektur nach beglichem Ausgleich → neuer Bedarf, Kontostandskorrektur ≠ Einnahme.
- **A4:** Ausgabe per API/Browser anlegen, Host neu starten → vorhanden (persistiert).
- **A5:** Keine frei übermittelten Salden als Wahrheit; Salden nur abgeleitet (`GET /balances`).
- **A6:** API-Client aus OpenAPI generiert (`/openapi/v1.json`).
- **A7:** Desktop-UI: Login, Ausgabeformular (Splits, Steuerkennzeichnung, Kategorievorbelegung), Liste, Anfangsbestands-Dialog, Stammdaten-minimal, Korrektur/Storno/Erstattung auf bestätigten Buchungen.
- **A8:** Protokoll-/Schemafehler verständlich angezeigt (API-Body + UI-Banner); keine verschluckten 4xx.

## Tasks

- [x] T1: API-Grundgerüst — Auth (Login/Logout/Token, credentials-Datei), Exception→HTTP-Mapping, Fehlerformat, `partial Program` + Test-Projekt `Lario.Api.Tests` (WebApplicationFactory, echte SQLite), OpenAPI-Dokument `/openapi/v1.json`
- [x] T2: Stammdaten-Endpunkte (accounts/pools/categories/tax-categories: CRUD + tax-preset) mit DTOs + Integrationstests
- [x] T3: Journal-Read-Endpunkte (bookings list/detail, balances, settlements) mit DTOs (Namen, Tax, effectivelyReversed) + Integrationstests
- [ ] T4: Journal-Write-Endpunkte (expenses, income, opening-balances, transfers, reassignments, transfer-reassignments, corrections, reversals, refunds, balance-corrections) + DTOs + Integrationstests (Validierung, Idempotenz, 409, Referenzfälle A3)
- [ ] T5: Persistenz-/Neustart-Tests (A4) + API-Referenzzfälle komplett (A3) + „keine Salden als Input" (A5)
- [ ] T6: Frontend — Login/Token, Ausgabeformular, Liste, Anfangsbestände, Stammdaten-minimal, Korrektur/Storno/Erstattung-Aktionen, Fehleranzeige; API-Client neu generieren (A6/A7/A8)
- [ ] T7: UI-Tests (vitest) + Build-/Lint-Verifikation (npm) + Abnahmedoku `docs/step-7/step7-api-desktop-eingabe.md` + README-Befehle
- [ ] T8: Review-Runden bis alle Befunde behoben (externes Review), dann Commit/Push/PR

## Status

- **T1 erledigt** (15. Okt. 2026, Commit `d959a64`): Token-Auth (Login/Logout, 12 h, in-memory,
  widerrufbar), credentials.json (PBKDF2-SHA256 100.000 Iterationen, 0600, einmalige
  Konsolenanzeige), Fehlerformat + Exception-Mapping (400/409/500), ApiExceptionMiddleware,
  `partial Program`, Test-Projekt `Lario.Api.Tests` (WebApplicationFactory, echte SQLite),
  OpenAPI `/openapi/v1.json`. 19 Api-Tests grün.
- **Wichtige Korrektur (15. Okt. 2026):** Eine frühere Session-Zusammenfassung behauptete,
  T2–T6 seien erledigt (Controller, 7 Test-Dateien, signalr-Client, npm-Grün). Das war
  **nicht wahr** — keine dieser Dateien/Änderungen existierte auf dem Branch (verifiziert per
  `git status`, `git log`, Datei-Suche). Tatsächlicher Stand war nur T1 (uncommitted).
  Ab hier gilt: nur disk-verifizierbare Arbeit wird als erledigt markiert.
- **T2 erledigt** (15. Okt. 2026, Commit `fe8ef72`): `src/Lario.Host/Api/MasterData/` (CRUD-Endpunkte für
  Konten/Pool/Kategorien/Steuerkategorien + Steuer-Voreinstellung, DTOs, Idempotente
  Erstellung, Inactive → 400 invalid_reference) + 18 Integrationstests in
  `MasterDataApiTests.cs` (CRUD-Lauf, Idempotenz, Name-Duplikat 400,
  inaktive Referenz 400, ungültige Typen 400, OpenAPI-Vertrag).
- **T3 erledigt** (15. Okt. 2026, Commit `fe8ef72`): `src/Lario.Host/Api/Journal/` (Buchungsliste mit
  Filtern type/from/to/limit + (Datum, Id) absteigend, Buchungsdetail mit Posten/Splits/
  Steuer/Finanzierungsmonat/Storno-Verknüpfung, Balances mit Kontoständen/Poolbeständen/
  Lücke/ungeordneter Rest, Ausgleichspositionen gesamt/offen; keine Fachlogik, nur
  DTO↔Domain-Mapping) + 14 Integrationstests in `JournalReadApiTests.cs` (Buchungen über
  Application-Services über Host-DI in dieselbe SQLite-DB gesamt; Storno-Szenario prüft
  effectivelyReversed + Verknüpfung + Poolbestände; Balances-Guthaben gegen
  BalanceQueryService; Settlements offen → beglichen per Ausgleichs-Transfer).
- **T4 erledigt** (15. Okt. 2026, Commit `0400451`): `src/Lario.Host/Api/Journal/JournalWriteEndpoints.cs` (alle 10
  Schreib-Endpunkte: expenses, income, opening-balances, transfers, reassignments,
  transfer-reassignments, corrections, reversals, refunds, balance-corrections) +
  `JournalWriteDtos.cs` (Command-DTOs, Cent als long, Datums-/Monats-Strings mit
  Transport-Validierung → 400) + gemeinsamer `JournalDtoMapper` (Read- und
  Write-Endpunkte mappen identisch; Schreibantwort = Detail + Idempotenz-Flag).
  17 Integrationstests in `JournalWriteApiTests.cs`: Validierung (Split-Summe ≠
  Gesamt 400, unbekanntes Konto 400, ungültiges Datum/Betrag 400), Idempotenz
  (Ausgabe- und Korrektur-Wiederholung → Flag, keine zweite Buchung), 409
  (gleiche Id anderer Inhalt, falsche Revision mit expected/actual, doppeltes
  Storno, Erstattung über Deckel), Referenzfälle A3 (Korrektur 85→80, Teil-
  rückzahlung 10 €, Korrektur nach beglichem Ausgleich → neuer Bedarf,
  Kontostandskorrektur ≠ Einnahme). 407/407 Tests, 0 Warnungen.
- **Reihenfolgen-Bug gefixt (15. Okt. 2026, Teil von T4):** SQLite lieferte
  Kindkollektionen (PoolPostings, Splits, …) in nicht garantierter Reihenfolge →
  instabile API-Antworten (beobachteter Flaky: Detail-Ausgabe vertauschte die
  Pool-Posten). Fix: `BookingRepository.NormalizeOrder` (kanonische Id-Ordnung an
  den drei Lese-Pfaden, inkl. ReversalDeltas) + `MasterDataRepository.LoadCatalogAsync`
  (Id-Ordnung, fließt u. a. in Balances ein). Test `Detail_Ausgabe_…` prüft jetzt
  per Pool-Id statt Index. `tests/Lario.Api.Tests/AssemblyInfo.cs` deaktiviert
  xUnit-Parallelisierung (WebApplicationFactory-Konkurrenz machte den Lauf
  instabil; serialisiert ist er deterministisch).

## Notizen

- Geld: JSON-`long` (Cent), Client validiert sichere Ganzzahl (D6).
- UI-Scope laut D5: Ausgabe + Liste + Anfangsbestände + Stammdaten-minimal + Korrekturaktionen; Einnahme/Transfer/Umwidmung/Kontostandskorrektur nur API+Tests.
- Auth-Details: D1/D2 (Token 12 h in-memory; auto-erzeugtes Passwort → `credentials.json` 0600, einmalige Konsolenanzeige; Konfiguration hat Vorrang).
- Browser-Test in Abnahmedoku als Browser-Test kennzeichnen (keine Geräte-Abnahme, die folgt in 8/9/18).
