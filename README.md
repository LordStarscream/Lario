# Lario

Lokale Finanzplanung für den eigenen Haushalt: ASP.NET-Core-Host (Linux) mit Web-Oberfläche
und unabhängige Android-Ausgabenerfassung (ab Schritt 2/8). Planung und Abnahmekriterien:
`docs/Finanzplanung_Implementierungsplan.md` (maßgeblich), fachlicher Rechenvertrag:
`docs/step-0/rechenvertrag.md`.

## Technologie (fixiert in Schritt 1)

| Bereich | Wahl | Fixiert in |
|---|---|---|
| .NET | .NET 10.0.112 SDK | `global.json` |
| Web-UI | Angular 22.2 (workspace `frontend/`, App `host`) | `frontend/package.json` + Lockfile |
| OpenAPI | eingebauter .NET-OpenAPI (`Microsoft.AspNetCore.OpenApi` 10.0.12); TypeScript-Client via `openapi-typescript-codegen` 0.31.0 | `Directory.Packages.props`, `frontend/package.json` |
| Lint | ESLint 10.12.0 flat config + `@angular-eslint` 22.5 | `frontend/package.json` + Lockfile |
| Tests | xUnit 2.9.3 / .NET 10 | `Directory.Packages.props` |

Patchstände werden bei Upgrades bewusst geändert und in Commit-Nachrichten begründet
(keine stillen Major-Upgrades).

## Aufbau

```
Lario.slnx                 Solution (net10)
src/
  Lario.Domain/            Werte, Regeln, Invarianten — keine HTTP/EF/UI-Abhängigkeit
  Lario.Application/       Anwendungsfälle, Domain + gezielte Schnittstellen
  Lario.Infrastructure/    Datenablage-Pfade (EF Core 10 / SQLite ab Schritt 3)
  Lario.Host/              API, Web-UI (wwwroot), /api/health, /api/version
tests/
  Lario.Domain.Tests/      xUnit-Tests: Money (24)
  Lario.Infrastructure.Tests/  xUnit-Tests: LarioDataPaths (8)
frontend/                  Angular-Workspace
  apps/host/               Web-App (Build → src/Lario.Host/wwwroot)
  openapi.json             Spec-Snapshot der Host-API (generate:client)
```

## Voraussetzungen

- .NET 10 SDK (Version via `global.json`): `dotnet --version`
- Node.js 22.x + npm (Angular 22)

## Build und Tests (reproduzierbar)

```bash
# .NET: Solution bauen + alle Tests
dotnet build Lario.slnx
dotnet test Lario.slnx

# Frontend: Abhängigkeiten, Lint, Build beider Apps
cd frontend
npm ci
npm run lint        # ESLint (host + mobile)
npm run build       # ng build host + ng build mobile
```

## Host starten (mit Web-UI)

```bash
# 1) Frontend bauen (wird bei dotnet build des Hosts nach wwwroot kopiert)
cd frontend && npm ci && npm run build:host

# 2) Host bauen und starten
cd ..
dotnet build src/Lario.Host/Lario.Host.csproj
ASPNETCORE_URLS=http://localhost:8080 DOTNET_ROOT=$HOME/.dotnet \
  ./src/Lario.Host/bin/Debug/net10.0/Lario.Host
# → http://localhost:8080            (SPA)
# → http://localhost:8080/api/health (JSON: {"status":"ok","dataDirectory":...})
# → http://localhost:8080/api/version
# → http://localhost:8080/openapi/v1.json (OpenAPI-Spec der Host-API)
```

> **Hinweis zu dieser Maschine:** Der .NET-SDK liegt unter `/usr/share/dotnet`,
> das Microsoft-AspNetCore-Runtime-Framework aber nur unter `~/.dotnet/shared`.
> `dotnet run` / `dotnet <dll>` (Muxer) finden das Framework nicht;
> das Apphost-Executable mit `DOTNET_ROOT=$HOME/.dotnet` schon. Auf Systemen,
> bei denen SDK und Runtime nebeneinander installiert sind, genügt
> `dotnet run --project src/Lario.Host --urls http://localhost:8080`.

Das Web-Frontend ruft die API relativ zum eigenen Origin auf
(`OpenAPI.BASE = ''` in `frontend/apps/host/src/app/app.config.ts`), deshalb
funktioniert der Host auf jedem Port/jedem Rechner. Im Angular-Devserver
(`npm start`) übernimmt `proxy.conf.json` die Weiterleitung `/api` → `localhost:8080`.

### Datenverzeichnis

Wird beim Host-Start angelegt (idempotent — Neustart erzeugt keine zweite Ablage):

1. `Lario:DataDirectory` (appsettings oder `Lario__DataDirectory`), falls gesetzt
2. sonst `$XDG_DATA_HOME/lario`
3. sonst `~/.local/share/lario`

Die SQLite-Datei (`lario.sqlite`) erscheint dort ab Schritt 3.

## OpenAPI-Client generieren

```bash
# Host muss laufen; erzeugt openapi.json (Spec-Snapshot) +
# apps/host/src/generated/ (fetch-Client, von ESLint ausgenommen).
# app.config.ts setzt danach die Basisadresse außerhalb des generierten Codes.
cd frontend
npm run generate:client   # liest http://localhost:8080/openapi/v1.json
```

## Entwicklungszustand

**Schritt 1 (Projektgerüst) ist abgeschlossen:** Solution, vier Projektordner,
xUnit-Tests (Money 24, LarioDataPaths 8), Angular-Workspace mit host/mobile-Apps,
Health-/Version-Endpunkte, SPA-Hosting mit SPA-Fallback, OpenAPI-Vertrag und
TypeScript-Client-Generierung, Lint-Setup, .gitignore, Task-Tracking.

Nächster Schritt: 2 — Android-/LAN-Prototyp (Capacitor, echtes Testgerät).
Details: `.tasks/step-1-projektgeruest.md`, `.tasks/_index.md`.
