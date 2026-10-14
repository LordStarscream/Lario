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

- [ ] T1: API-Grundgerüst — Auth (Login/Logout/Token, credentials-Datei), Exception→HTTP-Mapping, Fehlerformat, `partial Program` + Test-Projekt `Lario.Api.Tests` (WebApplicationFactory, echte SQLite), OpenAPI-Dokument `/openapi/v1.json`
- [ ] T2: Stammdaten-Endpunkte (accounts/pools/categories/tax-categories: CRUD + tax-preset) mit DTOs + Integrationstests
- [ ] T3: Journal-Read-Endpunkte (bookings list/detail, balances, settlements) mit DTOs (Namen, Tax, effectivelyReversed) + Integrationstests
- [ ] T4: Journal-Write-Endpunkte (expenses, income, opening-balances, transfers, reassignments, transfer-reassignments, corrections, reversals, refunds, balance-corrections) + DTOs + Integrationstests (Validierung, Idempotenz, 409, Referenzfälle A3)
- [ ] T5: Persistenz-/Neustart-Tests (A4) + API-Referenzzfälle komplett (A3) + „keine Salden als Input" (A5)
- [ ] T6: Frontend — Login/Token, Ausgabeformular, Liste, Anfangsbestände, Stammdaten-minimal, Korrektur/Storno/Erstattung-Aktionen, Fehleranzeige; API-Client neu generieren (A6/A7/A8)
- [ ] T7: UI-Tests (vitest) + Build-/Lint-Verifikation (npm) + Abnahmedoku `docs/step-7/step7-api-desktop-eingabe.md` + README-Befehle
- [ ] T8: Review-Runden bis alle Befunde behoben (externes Review), dann Commit/Push/PR

## Notizen

- Geld: JSON-`long` (Cent), Client validiert sichere Ganzzahl (D6).
- UI-Scope laut D5: Ausgabe + Liste + Anfangsbestände + Stammdaten-minimal + Korrekturaktionen; Einnahme/Transfer/Umwidmung/Kontostandskorrektur nur API+Tests.
- Auth-Details: D1/D2 (Token 12 h in-memory; auto-erzeugtes Passwort → `credentials.json` 0600, einmalige Konsolenanzeige; Konfiguration hat Vorrang).
- Browser-Test in Abnahmedoku als Browser-Test kennzeichnen (keine Geräte-Abnahme, die folgt in 8/9/18).
