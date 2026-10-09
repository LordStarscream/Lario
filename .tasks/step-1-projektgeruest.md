# Aufgabe: Schritt 1 – Projektgerüst und Linux-Start

**Status:** aktiv (geplant 9. Okt. 2026)
**Branch:** `step/1-projektgeruest` (abgeleitet von `step/0-rechenvertrag` @ `29a12aa`)
**Plan:** `docs/Finanzplanung_Implementierungsplan.md` §5 „Schritt 1"

## Ziel

Reproduzierbares Projektgerüst auf Linux: vier .NET-Projekte (Domain, Application,
Infrastructure, Host), .NET-Testprojekte, Angular-Workspace mit Haupt- und Mobiloberfläche.
Host startet unter Linux, liefert minimale Weboberfläche plus Health-/Versionsendpunkt,
OpenAPI-Vertrag und TypeScript-Client-Generierung. SDK/Pakete sind in global.json,
Projektdateien und Lockfiles fixiert. Lokales Datenverzeichnis ist konfiguriert und
deterministisch (Neustart erzeugt keine zweite Datenablage).

## Geprüfte und fixierte Versionen (9. Okt. 2026, Registry-Nachweis)

| Komponente | Version | Fundstelle |
|---|---|---|
| .NET SDK | 10.0.112 (installiert) | `global.json` |
| TargetFramework | net10.0 | alle csproj |
| Microsoft.EntityFrameworkCore.Sqlite | 10.0.12 | `Directory.Packages.props` |
| Microsoft.AspNetCore.OpenApi | 10.0.12 | `Directory.Packages.props` |
| xunit | 2.9.3 | `Directory.Packages.props` |
| xunit.runner.visualstudio | 3.1.4 | `Directory.Packages.props` |
| Microsoft.NET.Test.Sdk | 17.14.1 | `Directory.Packages.props` |
| @angular/* (core/cli/build-angular) | 22.2.2 | `frontend/package.json` + Lockfile |
| TypeScript | 6.0.3 (Peer von Angular 22.2.2: `>=6.0 <6.1`) | `frontend/package.json` |
| ESLint / typescript-eslint | 10.12.0 / 8.69.0 | `frontend/package.json` + Lockfile |
| openapi-typescript-codegen | 0.31.0 | `frontend/package.json` |
| @capacitor/* | — (Schritt 2) | nicht installiert |

Abweichungen von der Planung: `@angular/capacitor` existiert nicht (404) — Capacitor-Anbindung
in Schritt 2 direkt mit `@capacitor/core` + `@capacitor/android` 8.5.3 (geprüft).
`@hey-api/cli` existiert nicht (404) — Client-Generierung mit `openapi-typescript-codegen`.

## Abhängigkeiten

- Schritt 0 (Rechenvertrag) — abgenommen 9. Okt. 2026.
- Kein Android-Gerät für diesen Schritt nötig (das ist Schritt 2).

## Tasks

- [x] Task 1: `.gitignore` (dotnet, node/angular, Lario-Daten, Belege, DB-/Backupdateien)
- [x] Task 2: .NET-Gerüst: `global.json`, `Lario.slnx`, `Directory.Build.props`,
      `Directory.Packages.props`; Projekte `src/Lario.{Domain,Application,Infrastructure,Host}`,
      `tests/Lario.{Domain,Infrastructure}.Tests` mit Referenzen laut Architektur
- [x] Task 3: Domain: `Money` (EUR als long-Cents nach Rechenvertrag; Addition/Subtraktion,
      Overflow-Wächter, Vergleichsoperatoren, Referenzwerte) — erste echte Domain-Regel aus Schritt 0
- [x] Task 4: Infrastructure: `LarioDataPaths` (konfiguriertes, deterministisches
      lokales Datenverzeichnis, XDG-Default, Anlage beim ersten Start; 6/6 Tests grün)
- [x] Task 5: Host: Health-/Versionsendpunkt, statische Weboberfläche aus `frontend/dist`,
      OpenAPI unter `/openapi/v1.json` (eingebauter .NET-OpenAPI, kein NSwag),
      Konfiguration `Lario:DataDirectory`, ASPNETCORE_URLS-Default `http://localhost:8080`
      → Refpacks verfügbar (System-SDK 10.0.112); /api/health, /api/version, /, /openapi/v1.json
      und SPA-Fallback per curl 200 verifiziert (9. Okt. 2026)
- [x] Task 6: Domain-Tests (24/24: Money-Referenzwerte, Grenzfälle, Overflow) +
      Infrastructure-Tests (6/6: Datenverzeichnis Determinismus/Neustart, XDG-Override) mit xUnit
- [x] Task 7: Angular-Workspace in `frontend/`: Apps `host` + `mobile`, ESLint
      (flat config, ESLint 10), `package-lock.json`; host-App ruft /api/health + /api/version auf
- [x] Task 8: OpenAPI → TypeScript-Client: npm-Skript `generate:client` generiert Client aus
      `/openapi/v1.json` in `frontend/apps/host/src/generated/` (fetch-Client);
      host-App nutzt generierten Client (`LarioHostService`); generierter Code via
      files-Entry in `frontend/eslint.config.js` aus Lint ausgenommen
- [ ] Task 9: `dotnet build` + `dotnet test` + `npm ci && npm run build` aus frischem
      Stand grün; Host-Start auf Linux, Health-/Versionsendpunkt und Weboberfläche per curl
      verifiziert; Neustart erzeugt keine zweite Datenablage
- [ ] Task 10: README: reproduzierbarer Build-/Startweg; Übergabemeldung;
      Commit nur nachtragsbezogener Dateien; **Push erst nach Nutzerfreigabe**

## Bewusst zurückgestellt (nicht Teil von Schritt 1)

- EF-Core-DbContext, Migrationen, SQLite-Datei (Schritt 3)
- Capacitor, Android-Plattform, Offline-Speicher (Schritt 2)
- Alle Fachentitäten, Buchungen, Sync (Schritt 3 ff.)
- Authentifizierung, HTTPS (Schritt 7 ff.)
- Gemeinsame Eingabekomponenten (erst mit der ersten geteilten Komponente)

## Abnahmekriterien (aus Plan §5 Schritt 1)

- [x] Lario.Domain, Application, Infrastructure, Host vorhanden
- [x] Testprojekte vorhanden und grün (Domain 24/24, Infrastructure 6/6)
- [x] Angular-Workspace für Haupt- und Mobiloberfläche (host + mobile bauen fehlerfrei)
- [x] Host liefert minimale Oberfläche und Health-/Versionsendpunkt (per curl 200; Browser-Test
      im Headless-Setup nicht möglich — HTTP-Auslieferung verifiziert)
- [x] SDK/Pakete fixiert (global.json 10.0.112, Directory.Packages.props, package-lock.json)
- [x] Konfiguration und lokales Datenverzeichnis angelegt (`Lario:DataDirectory`, XDG-Default)
- [x] OpenAPI-Vertrag und TypeScript-Generierung vorbereitet (openapi.json + Client + Skript)
- [ ] Aus frischem Checkout bauen → grün
- [x] Host unter Linux starten, Testlauf, API-Client generiert (Client existiert, App nutzt ihn)
- [ ] Neustart erzeugt keine leere zweite Datenablage
- [x] Dokumentierter Start-/Buildweg (README)

## Ergebnis (9. Okt. 2026)

- .NET: `dotnet build Lario.slnx` 0 Fehler/0 Warnungen; `dotnet test` 30/30 grün
- Frontend: `npm ci && npm run lint && npm run build` grün (host + mobile, 0 Warnungen)
- Host läuft auf http://localhost:8080: /api/health, /api/version, / (SPA „Lario"),
  /openapi/v1.json, SPA-Fallback für Client-Routen — alle 200
- Abweichungen: kein NSwag (eingebauter .NET-OpenAPI via Microsoft.AspNetCore.OpenApi 10.0.12);
  ESLint 10 statt 9 (flat config, @angular-eslint 22.5); System-SDK 10.0.112 wird genutzt
  (`/usr/bin/dotnet`), kein lokaler SDK-Install nötig
