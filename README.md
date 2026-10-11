# Lario

Lokale Finanzplanung für den eigenen Haushalt: ASP.NET-Core-Host (Linux) mit Web-Oberfläche
und unabhängige Android-Ausgabenerfassung (ab Schritt 2/8). Planung und Abnahmekriterien:
`docs/Finanzplanung_Implementierungsplan.md` (maßgeblich), fachlicher Rechenvertrag:
`docs/step-0/rechenvertrag.md`.

## Technologie (fixiert in Schritt 1)

| Bereich | Wahl | Fixiert in |
|---|---|---|
| .NET | .NET 10.0.112 SDK | `global.json` |
| Web-UI | Angular 22.2 (workspace `frontend/`, Apps `host` + `mobile`) | `frontend/package.json` + Lockfile |
| Mobile | Capacitor 8.5.3 + `@capacitor-community/sqlite` 8.1.1 (eigener dauerhafter SQLite-Speicher) | `frontend/package.json` + Lockfile, `frontend/capacitor.config.ts` |
| Android | Gradle 8.14.3, AGP 8.13.0, compileSdk/targetSdk 36, minSdk 24, JDK 21 (Build), Android SDK platform 36 + build-tools 36.0.0 | `frontend/android/` (generiert, committet) |
| OpenAPI | eingebauter .NET-OpenAPI (`Microsoft.AspNetCore.OpenApi` 10.0.12); TypeScript-Client via `openapi-typescript-codegen` 0.31.0 | `Directory.Packages.props`, `frontend/package.json` |
| Lint | ESLint 10.12.0 flat config + `@angular-eslint` 22.5 | `frontend/package.json` + Lockfile |
| Tests | xUnit 2.9.3 / .NET 10; Frontend: Vitest 5.0.3 (Angular-Unit-Test-Builder) | `Directory.Packages.props`, `frontend/package.json` |

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
  apps/mobile/             Android-App (Capacitor; Browser-Vorschau via npm start:mobile)
  capacitor.config.ts      Capacitor-Konfiguration (appId de.lario.app)
  android/                 natives Android-Projekt (via `cap add android` generiert)
  openapi.json             Spec-Snapshot der Host-API (generate:client)
```

## Voraussetzungen

- .NET 10 SDK (Version via `global.json`): `dotnet --version`
- Node.js 22.x + npm (Angular 22)
- APK-Bau: JDK 21 (`~/tools/jdk-21`), Android SDK mit platform 36 + build-tools 36.0.0
  (`~/tools/android-sdk`); Installation auf dem Gerät: `adb`

## Build und Tests (reproduzierbar)

```bash
# .NET: Solution bauen + alle Tests
dotnet build Lario.slnx
dotnet test Lario.slnx

# Frontend: Abhängigkeiten, Lint, Build beider Apps, Tests
cd frontend
npm ci
npm run lint        # ESLint (host + mobile)
npm run build       # ng build host + ng build mobile
npm test            # Vitest (headless; host + mobile)
```

## Host starten (mit Web-UI)

```bash
# 1) Frontend bauen (wird bei dotnet build des Hosts nach wwwroot kopiert)
cd frontend && npm ci && npm run build:host

# 2) Host bauen und starten
cd ..
dotnet build src/Lario.Host/Lario.Host.csproj
DOTNET_ROOT=$HOME/.dotnet \
  ./src/Lario.Host/bin/Debug/net10.0/Lario.Host
# → http://127.0.0.1:8080            (SPA)
# → http://127.0.0.1:8080/api/health (JSON: {"status":"ok","dataDirectory":...})
# → http://127.0.0.1:8080/api/version
# → http://127.0.0.1:8080/openapi/v1.json (OpenAPI-Spec der Host-API)
```

Die Kestrel-Bindung ist explizit und wird über die Konfiguration gesteuert
(`ASPNETCORE_URLS`/launchSettings sind damit nicht wirksam):

| Schlüssel | Default | Bedeutung |
|---|---|---|
| `Lario__Http__Address` | `127.0.0.1` | `0.0.0.0` für den Zugriff aus dem Heimnetz |
| `Lario__Http__Port` | `8080` | HTTP-Port |
| `Lario__Https__Address` | `127.0.0.1` | Bindungsadresse des HTTPS-Endpunkts |
| `Lario__Https__Port` | `8443` | HTTPS-Port |
| `Lario__Https__CertificateFile` / `Lario__Https__CertificateKeyFile` | — (leer) | PEM-Zertifikat + PEM-Schlüssel; HTTPS wird **nur** aktiviert, wenn beide gesetzt sind |

### HTTPS für den LAN-Prototyp (Schritt 2)

Beispiel: selbstsigniertes Testzertifikat für die Heimnetz-IP (NUR für den
Prototyp; in Schritt 9 folgt die gezielte Trust-Konfiguration):

```bash
openssl req -x509 -newkey rsa:2048 -nodes -days 30 \
  -keyout /tmp/lario-key.pem -out /tmp/lario-cert.pem \
  -subj "/CN=192.168.1.226" \
  -addext "subjectAltName=IP:192.168.1.226,IP:127.0.0.1,DNS:localhost"

DOTNET_ROOT=$HOME/.dotnet \
  Lario__Http__Address=0.0.0.0 Lario__Https__Address=0.0.0.0 \
  Lario__Https__CertificateFile=/tmp/lario-cert.pem \
  Lario__Https__CertificateKeyFile=/tmp/lario-key.pem \
  ./src/Lario.Host/bin/Debug/net10.0/Lario.Host
# → https://192.168.1.226:8443 (Zertifikat muss auf dem Android-Gerät
#    im Truststore installiert sein; Zertifikatsprüfung bleibt aktiv)
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

## Mobile App (Android, Schritt 2 Prototyp)

```bash
cd frontend
npm run build:mobile    # Build → dist/mobile/browser
npx cap sync android    # Web-Assets + SQLite-Plugin in android/ kopieren
cd android
JAVA_HOME=~/tools/jdk-21 ANDROID_HOME=~/tools/android-sdk ./gradlew assembleDebug
# → app/build/outputs/apk/debug/app-debug.apk

adb install -r app/build/outputs/apk/debug/app-debug.apk   # auf dem Gerät
```

Die App zeigt: dauerhaften lokalen Speicher (SQLite via Capacitor-Plugin),
ein Host-Verbindungsfeld (HTTP/HTTPS, Protokoll wählbar) mit Test-Button gegen
`/api/health` und die Eintragsliste. Browser-Vorschau ohne Android-Paket:
`npm start:mobile` (Speichern ist dort bewusst deaktiviert — der lokale
Speicher existiert nur im Native-Paket).

**Hinweis (Buildumgebung):** `cap sync` nach jedem Web-Ändern, dann `assembleDebug`.
Das Debug-APK ist für Testgeräte gedacht; das Signieren für den Release erfolgt
in Schritt 18 (dort auch der eigene, nicht committete Signierschlüssel).

## Entwicklungszustand

**Schritt 2 (Android-/LAN-Prototyp) ist abgeschlossen** (Details:
`.tasks/step-2-android-prototyp.md`):

- **LAN-Prototyp:** Host bindet explizit über `Lario__Http__*` / `Lario__Https__*`
  (Default Loopback:8080; `0.0.0.0` für Heimnetz), optionaler HTTPS-Endpunkt
  (PEM, Default 8443). Verifiziert: HTTP + HTTPS (CA-verifiziert) auf Loopback
  und Heimnetz-IP.
- **Mobile-App:** eigenes `mobile`-Projekt im Angular-Workspace, Capacitor 8 +
  `@capacitor-community/sqlite` 8.1.1, Android-Plattform in `frontend/android/`
  (committet, Build-Artefakte gitignore-d). Debug-APK baut (`gradlew assembleDebug`).
  Offline-fähig: ohne Host und ohne Internet starten/speichern; Host-URL wird
  lokal in der App-Datenbank gehalten und ist jederzeit änderbar.
- **Tests:** Vitest im Angular-Workspace (host 2, mobile 16) — u. a. Browser-Abbruchpfad,
  Cent-Validierung, Geräte-ID-Stabilität über Neustart, Host-Health-Checks.

**Noch offen (manuell, Gerät erforderlich — Gerät aktuell nicht per ADB
verbunden):** APK-Installation auf dem echten Android-Gerät, Offline-Dauertest
(Speichern, App-Kill, Neustart), LAN-/HTTPS-Verbindung vom Gerät.

Nächster Schritt: 3 — Datenbank und Grund-API (EF Core 10, SQLite, Migrationen).
Details: `.tasks/_index.md`.
