# Aufgabe: Schritt 3 – Stammdaten und Anfangsbestand

**Status:** [erledigt] (12. Okt. 2026) — wartet auf Nutzerabnahme, Push und Merge
**Branch:** `step/3-stammdaten`
**Plan:** `docs/Finanzplanung_Implementierungsplan.md` §5 „Schritt 3“
**Verbindliche Spezifikation:** `docs/step-0/rechenvertrag.md` (§3.1 Kernbegriffe, §3.7 Anfangsbestand, §5.5 Revision)
**Testfixtures:** `docs/step-0/data/stammdaten.json`, `docs/step-0/referenzfaelle/01-anfangsbestand.json`

## Ziel (Plan-Text)

Konten, Pools, Kategorien/Unterkategorien, separate Steuerkategorien/Unterkategorien und vererbbare Steuer-Voreinstellungen, Archivierung, Anfangsbestände mit Stichtag und explizite Verteilung. SQLite-Schema samt erster Migration. Verhindern, dass derselbe Anfangsbestand mehrfach als Buchung erzeugt wird.

## Abnahmekriterien (Plan-Text)

1. Hauptkonto 1.000 €, davon 700 € Flex und 300 € Freizeit. Kontosumme und zugeordnete Summe jeweils 1.000 €; normale Einnahmen 0 €.
2. Teilweise Zuordnung lässt einen sichtbaren Rest.
3. Kategoriezyklus und ungültige Referenzen ablehnen.
4. Fertig: Einrichtung ist persistent, wiederholbar ohne Verdopplung und centgenau.

## Scope-Entscheidungen (mit Begründung)

- **Kein REST/Keine UI in Schritt 3:** Die produktive API (Auth, DTOs, Validierung) ist explizit Schritt 7; die Einrichtungs-UI Schritt 7/16. Schritt 3 wird über Domain- und Infrastructure-Tests (echte SQLite) abgenommen. Der Host bleibt unverändert (Health/Version).
- **Kein Vorhaben-Modell:** Projekte/Vorhaben sind Kern von Schritt 14. `pool-vorhaben-urlaub` aus der Fixtures-Datei wird in Schritt 3 nicht modelliert (die Fixture bleibt ab Schritt 14 vollständig verwendbar).
- **Minimales Journal:** Anfangsbestände sind Buchungstypen des Journals (Rechenvertrag §3.2). Dafür werden `Booking`, `AccountPosting`, `PoolPosting` und Saldenabfragen (Kontostände, Poolbestände, nicht zugeordneter Rest, Einnahmensumme) implementiert — der Rest des Buchungsmodells (Splits, Transfers …) folgt in Schritt 4/5.
- **„Nicht zugeordnet“** ist eine abgeleitete Systemposition: Rest = Anfangsbestands-Betrag − Summe der Pool-Verteilung. Nicht automatisch Flex (Rechenvertrag §3.7).
- **Idempotenz (Rechenvertrag §3.7 „ablehnen bzw. idempotent“):** gleicher Anfangsbestands-ID + gleicher Inhalt → idempotente Bestätigung (keine zweite Buchung, Revision unverändert); gleiche ID + anderer Inhalt → sichtbarer Konflikt (Fehler). Konsistent mit §5.5 (Operations-ID zuerst prüfen; gleicher ID + anderer Inhalt = Fehler).
- **Archivierung statt Löschung** (Plan §9): Status `active`/`archived`, `baseRevision` wird bei Statuswechsel um 1 erhöht. Eine archivierte Entität kann nicht mehr als Referenz für neue Daten dienen (z. B. als Elternteil, als Verteilungsziel).
- **Steuer-Voreinstellungen** gehören zur Kategorie (1:1, `CategoryId` als PK): `taxRelevant` ja/nein/offen (null), `taxCategoryId` (optional, eigener Baum), `inheritToChildren` (bool). Wirksame Voreinstellung einer Kategorie = eigene explizite Voreinstellung gewinnt; sonst Vererbung vom nächsten Vorfahren mit `inheritToChildren=true`. Ein explizites Nein einer Untergruppe wird nicht durch übergeordnete Vererbung ersetzt.
- **Revision (Rechenvertrag §5.5):** alle Stammdaten-Entitäten und Buchungen starten mit `baseRevision=1`; Änderungen/Statuswechsel erhöhen um 1. Erwartete Basisrevision gehört zu Änderungen existierender Entitäten (ab Schritt 6/10) — in Schritt 3 wird das Feld nur mitgeführt.

## Dateien (geplant)

### Lario.Domain (rein, keine EF/HTTP/UI)
- `MasterData/Account.cs`, `Pool.cs`, `Category.cs`, `TaxCategory.cs`, `TaxPreset.cs`
- `MasterData/MasterDataCatalog.cs` — Invarianten: Zyklus (Kategorien + Steuerkategorien), ungültige Referenzen, Archivierung, Vererbung wirksamer Voreinstellung
- `Journal/BookingType.cs`, `Booking.cs`, `AccountPosting.cs`, `PoolPosting.cs`, `OpeningBalanceSpec.cs` (Validierung: Referenzen, Beträge, Rest)
- `Journal/BookingJournal.cs` — Salden aus Postings: Kontostand, Poolbestand, nicht zugeordneter Rest, Einnahmensumme, Gesamtsummen
- `LarioDomainException.cs`, `DomainConflictException.cs`, `InvalidReferenceException.cs`, `CycleDetectedException.cs`

### Lario.Application
- Schnittstellen (gezielt, keine generische Repository-Abstraktion): `IMasterDataRepository`, `IBookingRepository`
- Anwendungsfälle: `MasterDataAdmin` (Anlage/Archivierung/Voreinstellungen), `OpeningBalanceService` (idempotentes Anlegen), `BalanceQueryService` (Salden)

### Lario.Infrastructure
- EF: `LarioDbContext`, Entity-Klassen + Mapping, erste Migration (`Migrations`), `LarioDbContextFactory`
- Repository-Implementierungen (EF, echte SQLite)

### Tests
- `tests/Lario.Domain.Tests`: Invarianten (Zyklus, Referenzen, Rest, Vererbung, Salden aus Postings)
- `tests/Lario.Infrastructure.Tests`: echte SQLite — Plan-Fall 1000/700/300 + Einnahmen 0, teilweiser Rest, Idempotenz (Wiederholung, Konflikt), Persistenz über „Neustart“ (neues DbContext auf derselben Datei), Migration erzeugt Schema

### Host
- DI-Registrierung; kein neues REST (Schritt 7)

## Aufgaben

- [x] A1: Branch `step/3-stammdaten` anlegen
- [x] A2: Domain — Stammdaten-Entitäten + Katalog mit Invarianten (Zyklus, Referenzen, Archivierung)
- [x] A3: Domain — Steuerkategorien + vererbbare Voreinstellungen (wirksame Voreinstellung, explizites Nein gewinnt)
- [x] A4: Domain — Journal-Kern (Booking/Postings, Anfangsbestand-Validierung, Saldenabfragen)
- [x] A5: Application — Schnittstellen + Anwendungsfälle (Admin, Anfangsbestand idempotent, Salden)
- [x] A6: Infrastructure — EF DbContext, Mapping, erste Migration, Repositories
- [x] A7: Host — DI, `Migrate()` statt `EnsureCreated`
- [x] A8: Domain-Tests (Invarianten, Referenzfallzahlen, Vererbung)
- [x] A9: Infrastructure-Tests (echte SQLite: Plan-Abnahme, Idempotenz, Persistenz)
- [x] A10: Build + Tests + README/Task-Index aktualisieren
- [x] A11: Fertigmeldung an Nutzer (Änderungen, Tests, offene Punkte)

## Verlauf

- 12. Okt. 2026: Aufgabe gestartet (Schritt 2 merged, `61c3a07`).
- 12. Okt. 2026: Implementiert und getestet. `dotnet build Lario.slnx` fehlerfrei (0 Warnungen);
  `dotnet test Lario.slnx` **58/58 grün** (Domain 34: Money 14, MasterDataCatalog 11, OpeningBalance 9;
  Infrastructure 24: LarioDataPaths 8, Step3Persistence 16). Plan-Referenzfall (1.000 € / 700 Flex /
  300 Freizeit, Einnahmen 0) als Integrationstest auf echter SQLite umgesetzt; Idempotenz (gleicher
  Inhalt → Bestätigung ohne Duplikat, gleicher Id + anderer Inhalt → Konflikt) und Persistenz über
  neuen DbContext auf derselben Datei verifiziert. Keine Geräte-Abnahmen in diesem Schritt.
  Commits: `36a4ed9` (Domain), `4b593d8` (Application), `ca7cf65` (Infrastructure),
  `12115a5` (Host), `6e7746f` (Tests), `c170355` (Doku).
  Push + **PR #4** eröffnet (12. Okt. 2026). Open-Loop: Nutzerabnahme und Merge.
- 12. Okt. 2026: Neue Projekt-Vorgabe (Nutzer): nach Abschluss einer Implementierungsphase
  automatisch commit + push + PR; Merge nur auf explizite Freigabe. In AGENTS.md (Git) und
  im Aufgaben-Index verankert.
- 12. Okt. 2026: Externes Review (reviewer, PR #4) + systematische Gegenprüfung gegen
  Implementierungsplan, Rechenvertrag und AGENTS.md. Befunde und Auflösung:
  - **P1 Duplizierung bei Wiederholung (zufällige Id):** In Schritt 3 war eine REST-Endpunkt-
    Schicht eingeführt worden — jenseits des dokumentierten Scopes („Kein REST in Schritt 3,
    produktive API in Schritt 7“), die bei fehlendem `id` eine zufällige Id erzeugte.
    **Auflösung:** Host auf den dokumentierten Stand zurückgesetzt (nur Health/Version,
    + DI, + `Migrate()`); die stabile Wiederholungsidentität wird nun verbindlich auf
    Service-Ebene erzwungen (leere `Id` → `LarioDomainException` vor jedem Datenbankzugriff).
  - **P2 `2026-13-45` → 500:** Entfällt mit der Endpoint-Entfernung (ungültiges Datum wird
    bereits beim Parsen abgelehnt; die Domain-Schicht sieht keinen HTTP-Datentyp).
  - **P2 Archivierung vs. Idempotenz:** `OpeningBalanceService` prüft Idempotenz jetzt
    **vor** der Katalogvalidierung; Wiederholung nach Archivierung von Konto/Pool bestätigt
    die vorhandene Buchung unverändert (kein Fehler, keine Duplizierung). Archivierung
    verhindert nur neue Referenzen, nicht die Bestätigung historischer.
  - **P2 Voreinstellung vs. Archivierung der Steuerkategorie:** Blockiert wird nur durch
    *aktive* Kategorien; Voreinstellungen archivierter Kategorien sind historische
    Verweise (bleiben erhalten, werden nicht umgeschrieben, blockieren nicht).
  - **Vorhandener Befund (Schritt 1, nicht vom Review eingeführt):**
    `ASPNETCORE_ENVIRONMENT=Development` + `dotnet run` stürzte, sobald `frontend/dist/host`
    existierte (Static-Web-Assets erwartet `src/Lario.Host/wwwroot` als Quellen-Root).
    Behoben: leeres `wwwroot/` mit `.gitkeep` bleibt im Quellbaum (konventionelles
    Sdk.Web-Layout).
  Neue Regressionstests: idempotente Wiederholung nach Archivierung (echte SQLite),
  leere `Id` → keine Buchung, Steuerkategorie-Archivierung mit Voreinstellung einer
  archivierten Kategorie (Domain). Alle grün: **69 laufende Tests** (45 Domain, 24
  Infrastructure; 16 davon echte-SQLite-Integrationstests). Smoke-Test: Host startet in
  Development und Production, OpenAPI-Fläche = Health/Version, Frontend wird ausgeliefert.
