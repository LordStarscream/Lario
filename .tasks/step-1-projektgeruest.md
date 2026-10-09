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
| xunit.runner.visualstudio | 3.1.5 | `Directory.Packages.props` |
| Microsoft.NET.Test.Sdk | 17.14.1 | `Directory.Packages.props` |
| @angular/* (core/cli/build-angular) | 22.2.2 | `frontend/package.json` + Lockfile |
| TypeScript | 6.0.3 (Peer von Angular 22.2.2: `>=6.0 <6.1`) | `frontend/package.json` |
| ESLint / typescript-eslint | 9.39.5 / 8.71.1 | `frontend/package.json` |
| openapi-typescript-codegen | 0.31.0 | `frontend/package.json` |
| @capacitor/* | — (Schritt 2) | nicht installiert |

Abweichungen von der Planung: `@angular/capacitor` existiert nicht (404) — Capacitor-Anbindung
in Schritt 2 direkt mit `@capacitor/core` + `@capacitor/android` 8.5.3 (geprüft).
`@hey-api/cli` existiert nicht (404) — Client-Generierung mit `openapi-typescript-codegen`.

## Abhängigkeiten

- Schritt 0 (Rechenvertrag) — abgenommen 9. Okt. 2026.
- Kein Android-Gerät für diesen Schritt nötig (das ist Schritt 2).

## Tasks

- [ ] Task 1: `.gitignore` (dotnet, node/angular, Lario-Daten, Belege, DB-/Backupdateien)
- [ ] Task 2: .NET-Gerüst: `global.json`, `Lario.sln`, `Directory.Build.props`,
      `Directory.Packages.props`; Projekte `src/Lario.{Domain,Application,Infrastructure,Host}`,
      `tests/Lario.{Domain,Infrastructure}.Tests` mit Referenzen laut Architektur
- [ ] Task 3: Domain: `Money` (EUR als long-Cents nach Rechenvertrag; Addition/Subtraktion,
      Overflow-Wächter, Referenzwerte) — erste echte Domain-Regel aus Schritt 0
- [ ] Task 4: Infrastructure: `LarioDataPaths` (konfiguriertes, deterministisches
      lokales Datenverzeichnis, XDG-Default, Anlage beim ersten Start)
- [ ] Task 5: Host: Health-/Versionsendpunkt, statische Weboberfläche aus `frontend/dist`,
      OpenAPI unter `/openapi/v1.json`, Konfiguration `Lario:DataDirectory`,
      ASPNETCORE_URLS-Default `http://localhost:8080`
- [ ] Task 6: Domain-Tests (Money-Referenzwerte, Grenzfälle) + Infrastructure-Tests
      (Datenverzeichnis: Determinismus/Neustart, XDG-Override) mit xUnit
- [ ] Task 7: Angular-Workspace in `frontend/`: Apps `host` + `mobile`, ESLint,
      `package-lock.json`; host-App ruft /api/health + /api/version auf
- [ ] Task 8: OpenAPI → TypeScript-Client: npm-Skript generiert Client aus
      `/openapi/v1.json` in `frontend/apps/host/src/generated/`; host-App nutzt generierten Client
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
- [ ] Testprojekte vorhanden und grün
- [ ] Angular-Workspace für Haupt- und Mobiloberfläche
- [ ] Host liefert minimale Oberfläche und Health-/Versionsendpunkt
- [ ] SDK/Pakete fixiert (global.json, csproj/Lockfiles)
- [ ] Konfiguration und lokales Datenverzeichnis angelegt
- [ ] OpenAPI-Vertrag und TypeScript-Generierung vorbereitet
- [ ] Aus frischem Checkout bauen → grün
- [ ] Host unter Linux starten, Browser öffnen, Testlauf, API-Client generieren
- [ ] Neustart erzeugt keine leere zweite Datenablage
- [ ] Dokumentierter Start-/Buildweg (README)
