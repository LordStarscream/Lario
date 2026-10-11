# Aufgabe: Schritt 2 – Früher Android-/LAN-Prototyp
_Status: [aktiv] (Geräte-Abnahmen offen) · Branch: `step/2-android-prototyp` (von main `e8e74d9`) · Start: 11. Okt. 2026_

Quelle: `docs/Finanzplanung_Implementierungsplan.md` §5, Schritt 2.
Voraussetzung Schritt 1 erfüllt (PR #2 merged, `e8e74d9`).

## Ziel

Installierbare Capacitor-App (Android) mit eingebetteter Angular-`mobile`-Oberfläche,
lokalem SQLite-Speicher für einen einfachen Testdatensatz und erprobter LAN-Verbindung
zum Host — einschließlich Zertifikatsvertrauen und Kopplungsgrundlage (stabile
Geräte-ID). **Keine** produktive Finanzberechnung im Prototyp.

## Entscheidungen (11. Okt. 2026)

- **Capacitor 8.5.3** (`@capacitor/core`, `@capacitor/android`, `@capacitor/cli`) —
  Projektvorgabe „Capacitor 8", kompatibel mit Angular 22 (npm-Registry verfügbar).
- **SQLite-Adapter: `@capacitor-community/sqlite` 8.1.1** — das offizielle
  `@capacitor/sqlite` existiert nicht (npm 404); die Community-Plugin-Linie ist der
  etablierte Adapter und unterstützt Capacitor 8 (Peer-Dep-Prüfung bei `npm install`).
  → Adapterentscheidung des Plans ist damit gelöst.
- **webDir:** `dist/mobile` (Angular-Build-Output des `mobile`-Projekts);
  `capacitor.config.ts` liegt im Workspace-Wurzelverzeichnis `frontend/`.
- **Testdatensatz (technisch, keine Buchung):** Tabelle `prototype_entries`
  (id TEXT = gerätegenerierte UUID, label TEXT, amount_cents INTEGER NULL,
  created_at_utc TEXT ISO-8601) + Tabelle `meta` (Stable Device-ID). Keine
  Salden- oder Deckungslogik.
- **Host-Verbindung:** App nimmt Host-URL (http/https) auf, ruft
  `GET /api/health` auf und zeigt Ergebnis bzw. Fehler (inkl.
  `ERR_CERT_AUTHORITY_INVALID` bei fehlendem Zertifikatsvertrauen) an.
  **Zertifikatsvertrauen:** Host bekommt optionales HTTPS (PEM-Zertifikat,
  Port 8443) per Konfiguration; self-signed Zertifikat wird auf dem Gerät in
  den User-Truststore installiert (WebView vertraut User-CAs).
- **Kopplungsgrundlage:** stabile Geräte-ID (UUID, einmalig erzeugt, in
  `meta` persistiert, in der UI sichtbar) — Vorbereitung für die Kopplung
  in Schritt 8/9.
- **Browser-Fallback:** Im Browser (ng serve / Tests) ist das Native-Plugin
  nicht verfügbar → Speichern wird deaktiviert mit klarer Meldung; der
  Offline-Nachweis gilt nur für das Android-Paket.
- **Umgebung (diese Maschine):** kein sudo → JDK 21 (Temurin 21.0.12.1) und
  Android SDK (cmdline-tools, platform 36 + build-tools 36.0.0) lokal unter
  `~/tools/` installiert, nicht systemweit. `ANDROID_HOME=~/tools/android-sdk`,
  `JAVA_HOME=~/tools/jdk-21`. (JDK 17 war zu Beginn installiert, wurde aber durch
  JDK 21 ersetzt: Capacitor 8's Android-Framework kompiliert auf Java 21.
  Zusätzlich: Symlink `~/.dotnet/sdk` → `/usr/share/dotnet/sdk`, damit
  `~/.dotnet/dotnet` die SDK findet; ASP.NET-10-Runtime liegt nur in
  `~/.dotnet/shared`.)
  Android-Gerät: vor Abnahme per USB (oder `adb connect`) anbinden.
- **Korrektur (11. Okt.):** webDir ist `dist/mobile/browser` (Angular-Builder-
  Output-Unterverzeichnis), nicht `dist/mobile`.
- **Korrektur (11. Okt.):** Kestrel-Bindung ist jetzt explizit (`Lario__Http__*` /
  `Lario__Https__*`), damit das Bindungsverhalten unabhängig von launchSettings/
  `ASPNETCORE_URLS` deterministisch ist; HTTPS-Endpunkt nur aktiv, wenn beide PEM-
  Pfade gesetzt sind. Zertifikat wird mit `X509Certificate2.CreateFromPemFile`
  explizit geladen (Kestrels `UseHttps(certPath, keyPath)`-Overload verknüpft den
  privaten Schlüssel nicht zuverlässig).

## Tasks

- [x] Branch `step/2-android-prototyp` von main erstellt
- [x] Capacitor-Pakete + SQLite-Plugin im Frontend-Workspace installieren, Versionen fixiert
      (Capacitor 8.5.3, `@capacitor-community/sqlite` 8.1.1; Lockfile aktualisiert)
- [x] `capacitor.config.ts` (appId `de.lario.app`, webDir `dist/mobile/browser`) +
      Android-Plattform anlegen (`frontend/android/`, committet)
- [x] Mobile-App: Testdatensatz lokal speichern (SQLite), App-Start lädt Bestand
      (`SqliteService`: `prototype_entries` + `meta`, Roundtrip-Test grün)
- [x] Mobile-App: Geräte-ID erzeugen/persistieren (Kopplungsgrundlage)
      (UUID in `meta`, Stabilität über Neustart im Test verifiziert, in UI sichtbar)
- [x] Mobile-App: Host-Verbindung testen (`/api/health`), Fehler inkl. Zertifikat sichtbar
      (`HostService`, Protokoll wählbar, Fehlercode/Name in UI; URL lokal persistiert)
- [x] Host: optionales HTTPS (PEM, Port 8443) per Konfiguration + explizite
      Kestrel-Bindung (`Lario__Http__Address` Default 127.0.0.1)
- [x] Android-SDK-Pakete installieren (platform 36 + build-tools 36.0.0, Lizenzen akzeptiert)
- [x] Debug-APK bauen (`gradlew assembleDebug` → `app/build/outputs/apk/debug/app-debug.apk`,
      13 MB; Assets == letztem Web-Build verifiziert, Chunk-Hash identisch)
- [x] Frontend-Prüfungen: lint (grün), build host + mobile (grün), Tests
      (Vitest: host 2 + mobile 16 — Browser-Abbruchpfad, Cent-Validierung,
      Geräte-ID-Stabilität, Health-Checks)
- [x] .NET-Prüfungen: build (0 Warnungen) + Tests (32/32 grün)
- [x] Live-Host-Prüfung: HTTP + HTTPS auf Loopback **und** Heimnetz-IP
      (192.168.1.226), HTTPS inkl. CA-Verifizierung (`curl --cacert`) — grün
- [x] README: Prototyp-Startweg (Build, Sync, APK, Install, HTTPS/Zertifikat, LAN)
- [ ] **Geräte-Abnahme:** Flugmodus: Testdatensatz speichern → App beenden/neustarten → wiederfinden
- [ ] **Geräte-Abnahme:** Host im Heimnetz erreichen (HTTP und HTTPS mit installiertem Zertifikat)
- [ ] **Geräte-Abnahme:** Host ausgeschaltet → weiter speichern
- [ ] Task-Index aktualisieren, Commit(s), Push (nach Freigabe), PR eröffnen

## Offene Punkte / Blocker

- **Android-Gerät ist nicht per ADB verbunden** (`adb devices` leer, kein Android-
  Gerät in `lsusb`) → die drei Geräte-Abnahmen können nicht durchgeführt werden.
  Blocker: Gerät per USB anbinden (USB-Debugging + Autorisierung). Bis dahin:
  Build + APK + Installationsbefehl vorbereitet (README "Mobile App").
  Browser-/Emulator-Prüfung ist KEIN Ersatz für diese Abnahmen.
- Host-Start über `dotnet run` funktioniert auf dieser Maschine nicht (SDK in
  `/usr/share/dotnet` ohne ASP.NET-Runtime; Apphost-Lösung via `DOTNET_ROOT`;
  siehe README-Hinweis).
