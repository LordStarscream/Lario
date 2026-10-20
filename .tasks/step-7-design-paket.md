# Design-Paket — Schritt 7: Produktive API und erste Desktop-Eingabe

**Status:** final (15. Okt. 2026) · **Branch:** `step/7-api-desktop-eingabe`
**Plan:** `docs/Finanzplanung_Implementierungsplan.md` §5 „Schritt 7"
**Voraussetzung:** Schritt 3–6 (abgeschlossen, merged `140446d`)

## 1. Umfang laut Plan

> **Implementieren:** Versionierte REST-Endpunkte mit grundlegender lokaler Authentifizierung, DTOs, Validierungsfehler, API-Client und kleines Desktop-Ausgabeformular mit Liste einschließlich Steuerkennzeichnung und Kategorievorbelegung. Minimaler Einrichtungsdialog für Anfangsbestände. Bereits bestätigte Buchungen über definierte Korrekturanwendungsfälle ändern.
>
> **Prüfen:** Referenzfälle über API ausführen und Ergebnisse auslesen. Ausgaben per Browser anlegen und Host neu starten. Protokoll-/Schemafehler verständlich anzeigen; keine frei übermittelten Salden als Wahrheit übernehmen.
>
> **Fertig:** Alle bisherigen Kernfunktionen sind über API und einfache Oberfläche überprüfbar.

Konkret:
- **API:** alle Kernfunktionen der Schritte 3–6 als versionierte REST-Endpunkte in `Lario.Host` (Stammdaten, Buchungen, Transfers/Umwidmung, Korrektur/Storno/Erstattung/Kontostandsabgleich, Salden- und Ausgleichsabfragen), lokale Authentifizierung, DTOs, verständliche Validierungs-/Konfliktfehler.
- **API-Client:** aus dem OpenAPI-Dokument generiert (Infrastruktur aus Schritt 2; `npm run generate:client` zeigt bereits auf `/openapi/v1.json` — Konvention bestätigt).
- **Desktop-UI** (`frontend/apps/host`): Ausgabeformular (inkl. Splits, Steuerkennzeichnung, Kategorievorbelegung), Buchungsliste, minimaler Einrichtungsdialog für Anfangsbestände, Stammdaten-Anlage (minimal — ohne Konto/Pool/Kategorie ist weder Anfangsbestand noch Ausgabe erfassbar), Korrektur-/Storno-/Erstattungs-Aktionen auf bestätigten Buchungen.
- **Nicht im UI, aber in der API und getestet** (Plan-UI-Scope ist das Ausgabeformular + Liste + Anfangsbestände; „überprüfbar" heißt über die API): Einnahmen, Transfers, Umwidmung, Transfer-Umwidmung, Kontostandskorrektur.

## 2. API-Design

### 2.1 Versionierung

- Alle Fachendpunkte unter `/api/v1/…`.
- OpenAPI-Dokument unter `/openapi/v1.json` (pfadidentisch zum bestehenden `generate:client`-Skript).
- Die existierenden, nicht fachlichen Endpunkte `/api/health` und `/api/version` bleiben unverändert (Smoke-Tests, Schritt 2) — ohne Version, ohne Authentifizierung, keine Fachdaten (Health zeigt Status + Datenverzeichnis).

### 2.2 Authentifizierung (grundlegend, lokal)

Ein einzelner lokaler Benutzer; Token-basiert (kein Browser-Basic-Dialog, sauberes Login-Formular im SPA, Token widerrufbar):

| Aspekt | Entscheidung |
|---|---|
| Zugangsdaten | `Lario:Auth:Username` / `Lario:Auth:Password` aus Konfiguration. **Nicht gesetzt:** beim ersten Start wird ein zufälliges Passwort (16 Zeichen) erzeugt, als PBKDF2-Hash (SHA-256, 100.000 Iterationen) in `{DataDirectory}/credentials.json` abgelegt (Dateirechte 0600) und das Klartextpasswort **einmal** in der Konsole ausgegeben. Konfigurierte Zugangsdaten haben Vorrang und erzeugen keine Datei. |
| Login | `POST /api/v1/auth/login` `{username, password}` → 200 `{token, expiresInMinutes}`; falsch → 401 `{error:{code:"unauthorized", message}}` |
| Token | Zufall 32 Byte (hex), **in-memory** gespeichert (lokaler Einzel-User-Host; kein Überleben über Neustart nötig), Gültigkeit 12 h |
| Logout | `POST /api/v1/auth/logout` (mit gültigem Token) → 204, Token ungültig |
| Schutz | Alle `/api/v1/…`-Endpunkte außer `auth/login` erfordern `Authorization: Bearer <token>`; fehlt/ungültig/abgelaufen → 401 |

Bewusst schlicht: kein User-Management, keine Rollen, kein Passwort-Reset-Endpunkt. Schritt 9 erweitert das auf widerrufbare Gerätezugangsdaten (dort wird die Token-Ablage entsprechend ausgebaut).

### 2.3 Fehlerformat

Einheitlicher Fehlerkörper (OpenAPI-Schema `Error`):

```json
{ "error": { "code": "domain_conflict", "message": "…verständlich auf Deutsch…",
             "expectedBaseRevision": 3, "actualBaseRevision": 4 } }
```

| Domain-Ausnahme | HTTP | code |
|---|---|---|
| `LarioDomainException` (Basis) | 400 | `validation_error` |
| `InvalidReferenceException` | 400 | `invalid_reference` |
| `CycleDetectedException` | 400 | `cycle_detected` |
| `DomainConflictException` | 409 | `domain_conflict` (+ `expectedBaseRevision`/`actualBaseRevision`, wenn gesetzt) |
| fehlender/ungültiger Bearer-Token | 401 | `unauthorized` |
| unbekannt | 500 | `internal_error` (Details nur im Host-Log, nicht im Body) |

Die deutschen Fachmeldungen der Domain (Schritte 3–6) bleiben die `message` — sie sind bereits verständlich und referenzieren Ids/Beträge. Zusätzlich maschinenlesbarer `code` für die UI.

**„Keine frei übermittelten Salden als Wahrheit":** Es gibt keinen Endpunkt, der einen Saldo übernimmt. Salden kommen ausschließlich aus `GET /api/v1/balances` (abgeleitet aus Journal + Katalog). Kontostandskorrekturen nehmen eine dokumentierte *Differenz* (`BalanceCorrectionSpec`) und werden gegen den Katalog validiert.

### 2.4 Endpunkte (v1)

Alle Beträge als JSON-Ganzzahl in Cent (`long`); `date` als `yyyy-MM-dd`, `fundingMonth` als `yyyy-MM`.

**Auth**
- `POST /auth/login`, `POST /auth/logout`

**Stammdaten** (Read + minimale Verwaltung)
- `GET /accounts`, `GET /accounts/{id}` · `POST /accounts` · `POST /accounts/{id}/rename` · `POST /accounts/{id}/archive` · `POST /accounts/{id}/unarchive`
- `GET /pools`, `GET /pools/{id}` · `POST /pools` · `POST /pools/{id}/rename` · `POST /pools/{id}/archive` · `POST /pools/{id}/unarchive`
- `GET /categories`, `GET /categories/{id}` · `POST /categories` · `POST /categories/{id}/rename` · `POST /categories/{id}/archive` · `POST /categories/{id}/unarchive`
- `GET /tax-categories` · `POST /tax-categories` · `POST /tax-categories/{id}/rename` · `POST /tax-categories/{id}/archive` · `POST /tax-categories/{id}/unarchive`
- `PUT /categories/{id}/tax-preset` · `DELETE /categories/{id}/tax-preset`

**Journal — Lesen**
- `GET /bookings?type=&from=&to=&limit=` (Default `limit` 200, max 1000) — Liste mit Stammdaten-Namen, `effectivelyReversed`
- `GET /bookings/{id}` — Detail (Splits mit Tax, Konten-/Poolposten, Transfer-Zwecke, Revisionsstatus)
- `GET /balances` — `Balances` (Konten, Pools, Summen, Gap, nicht zugeordnete Anfangsbestände)
- `GET /settlements`, `GET /settlements/open`

**Journal — Schreiben** (alle → 200 mit `BookingDto` + `Idempotent`-Flag bzw. 400/409)
- `POST /expenses` — `ExpenseCommand` (Id, date, totalAmount, accountId, note?, splits[{id, amount, poolId, categoryId, note?, tax?{relevant, taxCategoryId?, markedAmount?, note?}}], expectedBaseRevision?)
- `POST /income` — `IncomeCommand` (…, allocations[{poolId, amount}], fundingMonth?)
- `POST /opening-balances` — `OpeningBalanceCommand` (…, distribution[{poolId, amount}])
- `POST /transfers` — `TransferCommand` (…, purposes?, relatedSettlementId?)
- `POST /reassignments` — `ReassignmentCommand` (…, accounts?)
- `POST /transfer-reassignments` — `TransferReassignmentCommand`
- `POST /bookings/{id}/corrections` — `CorrectionCommand` (operationId, expectedBaseRevision, date?, note?, accountId?, totalAmount?, splits?, allocations?)
- `POST /bookings/{id}/reversals` — `ReversalCommand` (id, expectedBaseRevision, date, note?) — Path-Id = Original
- `POST /bookings/{id}/refunds` — `RefundCommand` (id, accountId, date, lines[{poolId, amount}], note?) — Path-Id = Ursprungs-Ausgabe
- `POST /balance-corrections` — `BalanceCorrectionCommand` (id, accountId, amount (signiert, ≠ 0), date, note (Pflicht))

Die Command→Spec-Mapping und die fachliche Validierung liegen in Domain/Application (bestehend); die Host-DTOs tragen nur Transportform und primitive Typen (Money-Cent als `long`), keine Fachlogik.

### 2.5 Testbarkeit

`Program.cs` bleibt Top-Level-Entry-Point, zusätzlich `public partial class Program {}` (für `WebApplicationFactory`). Test-Setup: `Lario:DataDirectory` → Temp-Verzeichnis, `Lario:Auth:Username/Password` → Testwerte. Migrations laufen im Test wie im Produktivstart (`db.Database.Migrate()` im Program).

## 3. Frontend (`apps/host`)

- **Login:** Username/Password → `POST /auth/login`, Token in `localStorage`, Http-Interceptor setzt `Authorization`; 401 → zurück zum Login. „Abmelden" ruft `/auth/logout` auf.
- **Ausgabeliste:** Datum, Gesamtbetrag (€-Darstellung aus Cent), Konto, Pools/Kategorien der Splits, Steuerkennzeichnung (Badge „St. relevant" + Kategorie), Status (storniert → abgegraut, `effectivelyReversed`). Filter: Typ (Default Ausgabe), Datum von/bis.
- **Ausgabeformular:** Datum, Gesamtbetrag (€-Eingabe, Client prüft Summe der Splits = Gesamt, Ganzzahl-Cent), Konto, Notiz; Splits: Betrag, Pool, Kategorie (Dropdown), optionale Steuer-Überschreibung (Schalter relevant ja/nein + Steuerkategorie + Teilbetrag). **Kategorievorbelegung:** bei Kategorie-Wahl zeigt das Formular die wirksame Steuer-Voreinstellung (aus `GET /categories` inkl. `taxPreset`) vor; explizite Angabe überschreibt — die Materialisierung bleibt Server-Seite (Snapshot-Regel aus Schritt 4/9).
- **Aktionen auf bestätigten Ausgaben:** „Korrektur" (Betrag/Datum/Notiz/Konto/Splits), „Storno" (Datum + Notiz, `expectedBaseRevision` aus der geladenen Buchung), „Erstattung" (Konto, Datum, Zeilen an Ursprungspools mit Deckel-Hinweis) — jeweils mit der vom Server geladenen `baseRevision`.
- **Einrichtungsdialog Anfangsbestände:** Konto, Datum (Stichtag), Gesamtbetrag, optionale Verteilung auf Pools (Rest bleibt sichtbar „nicht zugeordnet").
- **Stammdaten (minimal):** Anlage Konto (Name, Typ, zinsbringend), Pool (Name), Kategorie (Name, optional übergeordnete), Steuerkategorie (Name, optional übergeordnete), optional Kategorie-Voreinstellung; Umbenennen/Archivieren der vorhandenen.
- **Fehleranzeige:** Serverfehler (`message` + bei 409 die Revisionswerte) als Banner/Dialog — keine leeren Toasts, keine verschluckten 4xx.
- **Geld:** Eingabe in Euro (Dezimalpunkt), intern als Cent-Ganzzahl (`number`, auf Ganzzahl-Cent validiert); keine `float`-Beträge im Client-Modell.

Styling: schlichtes, eigenes CSS (keine Frameworks), deutschsprachig.

## 4. Tests

**API-Integrationstests** (`tests/Lario.Api.Tests`, `WebApplicationFactory` + echte SQLite in Temp-Verzeichnis):
1. **Auth:** kein Token → 401; falsches Passwort → 401; gültig → 200 + Token; Logout → Token ungültig; abgelaufenes Token → 401 (Zeit im Test steuerbar).
2. **Stammdaten + Ausgabe per API:** Konto/Pool/Kategorie/Steuerkategorie anlegen, Ausgabe mit zwei Splits + Steuerkennzeichnung anlegen, auslesen; **Neustart simuliert** (zweite Factory-Instanz auf derselben Datenbank) → Ausgabe und Salden bleiben.
3. **Referenzfälle über die API** (Plan „Prüfen", Schritte 4–6):
   - Ausgabe 85 € auf 80 € korrigieren (Splits angepasst) → keine zweite Buchung, Saldo korrekt;
   - Teilrückzahlung 10 € → Konto und Ursprungspool steigen;
   - Korrektur nach bereits beglichem Ausgleich → neuer Ausgleichsbedarf sichtbar (`GET /settlements/open`);
   - Kontostandskorrektur (Differenz, dokumentiert) → nicht als Einnahme gezählt (`totalIncome` unverändert).
4. **Fehlerformat:** Schemafehler (fehlendes Feld, negativer Betrag, Split-Summe ≠ Gesamt) → 400 mit `message`; unbekannte Referenz → 400 `invalid_reference`; Revisionskonflikt → 409 mit `expectedBaseRevision`/`actualBaseRevision`; gleicher Id + anderer Inhalt → 409; gleicher Id + gleicher Inhalt (Wiederholung) → idempotent, keine zweite Buchung.
5. **Keine Salden als Input:** es existiert kein Endpunkt, der einen Saldo setzt (Test: `POST /balance-corrections` mit Amount = Soll-Stand statt Differenz erzeugt die Differenz-Buchung, keine Übernahme).
6. **Idempotenz/Storno-Kette über die API:** doppeltes Storno desselben Originals → zweites wird abgelehnt (Kette/Revision); Widerruf reaktiviert nicht den veralteten Ast (R2/R3-Szenario über die API).

**UI-Tests** (vitest, `apps/host`): Formularvalidierung (Split-Summe ≠ Gesamt blockiert Senden; Euro→Cent-Konvertierung), Kategorievorbelegung zeigt die Voreinstellung, 409-Anzeige mit Revisionswerten, Login-Flow (Token wird gesetzt; 401 → Login).

**Abnahmekriterien des Plans → Prüfungen:**
- „Referenzfälle über API ausführen und Ergebnisse auslesen" → Testgruppe 2–4;
- „Ausgaben per Browser anlegen und Host neu starten" → Testgruppe 2 (Neustart) + manueller Browser-Durchlauf in der Abnahmedoku (Browser-Test wird als solcher gekennzeichnet);
- „Protokoll-/Schemafehler verständlich anzeigen" → Testgruppe 4 + UI-Fehleranzeige;
- „keine frei übermittelten Salden als Wahrheit" → Testgruppe 5.

## 5. Entscheidungen

| # | Entscheidung | Begründung |
|---|---|---|
| D1 | Token-Auth (12 h, in-memory) statt HTTP Basic | SPA braucht sauberes Login-Formular; Token widerrufbar (Logout/Expiry); „grundlegende lokale Authentifizierung"; Schritt 9 baut auf dieser Struktur auf (Gerätedaten) |
| D2 | Auto-erzeugtes Passwort in `{DataDirectory}/credentials.json` (0600) + einmalige Konsolenanzeige | Ohne Konfigurationsaufwand nutzbar; Hash statt Klartext auf Datenträger; Konfiguration bleibt Vorrang für Betrieb |
| D3 | `/api/v1` + `/openapi/v1.json` | Bestehendes `generate:client`-Skript zeigt bereits auf diesen Pfad (Schritt 2); `/api/health`, `/api/version` bleiben unversioniert (nicht fachlich) |
| D4 | DTOs + Mappings in `Lario.Host` (kein neues Projekt) | Host ist Composition Root der HTTP-Schicht; Domain/Application bleiben transportfrei (Architektur-Regel) |
| D5 | UI-Scope: Ausgabeformular + Liste + Anfangsbestände + Stammdaten-minimal + Korrektur/Storno/Erstattung-Aktionen | Plan-UI-Scope; übrige Kernfunktionen (Einnahme, Transfer, …) sind per API + Tests verfügbar („überprüfbar") — keine vorgezogenen UI-Funktionen. **Nachführung (21. Okt. 2026):** die Desktop-UI für einmalige Transfers/Umwidmungen (inkl. Begleichung interner Ausgleiche und kombinierter Variante) ist seit der Planlücken-Schließung D2 verbindlich **Schritt 12** zugeordnet (`Finanzplanung_Implementierungsplan.md`); Einnahmen-Formular, Kontostandskorrektur-Formular und Einnahmen-Korrektur-Formular bleiben offen — siehe UI-Zuordnungstabelle in `docs/Finanzplanung_Implementierungsplan.md` (§ „UI-Zuordnung der Kernfunktionen"). |
| D6 | Money als `long` Cent in JSON; Client validiert sichere Ganzzahl | Rechenvertrag/AGENTS: kein float für Geld |
| D7 | `GET /bookings` mit Filtern `type`/`from`/`to`/`limit` | Liste der UI + „Ergebnisse auslesen" im Abnahmekriterium; Pagination bewusst minimal (limit) |

## 6. Offene Punkte

- Keine — alle fachlichen Regeln kommen aus den bereits abgenommenen Schritten 3–6.
- Manuelles Browser-Setup (Login, Formular) wird in der Abnahmedoku als Browser-Test gekennzeichnet; Android-Geräte-Abnahmen gehören zu Schritt 8/9/18.
