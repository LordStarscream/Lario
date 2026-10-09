# Lario

Lokale Finanzplanung für den eigenen Haushalt: ASP.NET-Core-Host (Linux) mit Web-Oberfläche
und unabhängige Android-Ausgabenerfassung (ab Schritt 2/8). Planung und Abnahmekriterien:
`docs/Finanzplanung_Implementierungsplan.md` (maßgeblich), fachlicher Rechenvertrag:
`docs/step-0/rechenvertrag.md`.

## Technologie (fixiert in Schritt 1)

| Bereich | Wahl | Fixiert in |
|---|---|---|
| .NET | .NET 10.0.100 SDK (STS) | `global.json` |
| Web-UI | Angular 22.2 (workspace `frontend/`, App `host`) | `frontend/package.json` |
| OpenAPI | NSwag 14.x (Code-Gen Host-API → TypeScript-Client) | `Directory.Packages.props` |
| Lint | ESLint 9.39 flat config + `@angular-eslint` 22 | `frontend/package.json` |
| Tests | xUnit 2.9.5 / .NET 10 | `Directory.Packages.props` |

Patchstände werden bei Upgrades bewusst geändert und in Commit-Nachrichten begründet
(keine stillen Major-Upgrades).

## Aufbau

```
Lario.slnx                 Solution (net10)
src/
  Lario.Domain/            Werte, Regeln, Invarianten — keine HTTP/EF/UI-Abhängigkeit
  Lario.Application/       Anwendungsfälle, Domain + gezielte Schnittstellen
  Lario.Infrastructure/    EF Core 10 / SQLite (ab Schritt 3)
  Lario.Host/              API, Web-UI (wwwroot), /api/health, /api/version
tests/
  Lario.Domain.Tests/      xUnit-Tests
frontend/                  Angular-Workspace
  apps/host/               Web-App (Build → src/Lario.Host/wwwroot)
  openapi.json             Spec-Snapshot der Host-API (generate:client)
```

## Voraussetzungen

- .NET 10 SDK (Version via `global.json`): `dotnet --version`
- Node.js 22.x + npm (Angular 22)

## Build und Tests (reproduzierbar)

```bash
# .NET: Solution bauen + Domain-Tests
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
# 1) Frontend bauen (kopiert dist/host/browser nach src/Lario.Host/wwwroot)
cd frontend && npm ci && npm run build:host

# 2) Host starten
cd ..
dotnet run --project src/Lario.Host --urls http://localhost:8080
# → http://localhost:8080            (SPA)
# → http://localhost:8080/api/health (JSON: {"status":"ok",...})
# → http://localhost:8080/api/version
# → http://localhost:8080/openapi/v1.json (NSwag-Spec, Versionierung ab Schritt 3)
```

Datenverzeichnis (ab Schritt 3): `~/.local/share/lario` (Linux XDG).

## OpenAPI-Client generieren (ab Schritt 3)

```bash
# Host muss laufen; generiert apps/host/src/generated/ (kein Commit-Pflicht-Problem:
# generierter Code ist Teil des Frontend-Builds und wird via ESLint ausgenommen)
cd frontend
npm run generate:client   # liest http://localhost:8080/openapi/v1.json → openapi.json + Client
```

## Entwicklungszustand

**Schritt 1 (Projektgerüst) ist abgeschlossen:** Solution, vier Projektordner mit
Architektur-Tests, Angular-Workspace mit host/mobile-Apps, Health-/Version-Endpunkte,
SPA-Hosting mit SPA-Fallback, NSwag-Grundgerüst, Lint-Setup, .gitignore, Task-Tracking.

Nächster Schritt: 2 — Android-App-Grundgerüst (Capacitor, echtes Testgerät).
Details: `.tasks/step-1-projektgeruest.md`, `.tasks/_index.md`.
