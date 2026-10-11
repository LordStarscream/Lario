# Aufgabe: Schritt 2 – Früher Android-/LAN-Prototyp
_Status: [erledigt] (abgenommen 11. Okt. 2026 — Geräte-Abnahmen auf OnePlus 9 bestanden) · Branch: `step/2-android-prototyp` (von main `e8e74d9`) · Start: 11. Okt. 2026 · **PR #3 wartet auf Merge**_

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
- **Review-Runde 1 (11. Okt. 2026, externes Review — umgesetzt):**
  1. *SQLite-Verbindungslifecycle:* `init()` rief `open()` ohne vorherige
     `createConnection()` auf — das Plugin (`CapacitorSQLite.java` 8.1.1)
     hält pro Prozess ein Register (`dbDict`, Schlüssel `RW_<Name>`/
     `RO_<Name>`), und `open()` wirft ohne registrierte Connection
     „No available connection for database …“. Außerdem adressierte
     `readonly: true` die nie angelegte `RO_`-Connection. → `init()` legt
     jetzt pro App-Start die RW-Connection an (idempotent: Guard
     `connectionCreated`, `createConnection()` würfe sonst „already
     exists“) und öffnet sie; `run()`/`query()`/`execute()` laufen mit
     explizitem `readonly: false` durchweg auf der RW-Connection.
  2. *Test-Fake modelliert den Vertrag:* Der Vitest-Fake simuliert jetzt
     das `dbDict` (createConnection/open/run/query/execute inkl.
     Fehlermeldungen und RO/RW-Schlüsseln). Neue Tests: open ohne
     createConnection schlägt fehl, RO-Query ohne RO-Connection schlägt
     fehl, init() ist idempotent, Neustart-Test modelliert neuen Prozess
     (leeres Register, Daten bleiben). → mobile 16 → 19 Tests, alle grün.
  3. *LAN-Fetch-Pfad nativ:* `CapacitorHttp` in `capacitor.config.ts`
     aktiviert (`enabled: true`) — `fetch` wird im WebView auf den
     nativen HTTP-Stack umgeleitet: kein Mixed-Content-Problem
     (HTTPS-App + HTTP-Host), keine CORS-Einschränkung. Im Browser bleibt
     der reguläre Web-Fetch (Tests nutzen `vi.stubGlobal('fetch')`).
  4. *Netzwerksicherheit:* `android/app/src/main/res/xml/network_security_config.xml`
     neu (im `AndroidManifest.xml` verankert, im APK verifiziert): Basis
     ohne Cleartext, Vertrauensanker System-CAs + **Nutzer-CAs**
     (installiertes Test-Zertifikat wird verifiziert); `debug-overrides`
     erlaubt Cleartext nur im Debug-APK (HTTP-Testweg). Zertifikats-
     prüfung bleibt aktiv (kein Wildcard, kein Bypass). Doku in README
     (Sektion „Mobile App“).
- **Review-Runde 2 (11. Okt. 2026, externes Review — umgesetzt):**
  Restpunkt aus Zeilenkommentar an `network_security_config.xml`: Laut
  Android-Schema erlaubt `<debug-overrides>` ausschließlich
  `<trust-anchors>` — die verschachtelte `base-config` mit
  Cleartext-Umschaltung wird dort nicht unterstützt, die wirksame Basis
  wäre `cleartextTrafficPermitted="false"` geblieben (HTTP-Testweg im
  Debug-APK trotzdem gesperrt). → Cleartext-Freigabe jetzt über
  variantenspezifische Ressourcen:
  - `main/res/xml/network_security_config.xml`: Basis ohne Cleartext,
    Trust-Anchor System-CAs + Nutzer-CAs (Release-Builds, HTTPS-Testweg).
  - `debug/res/xml/network_security_config.xml`: ersetzt die Basisdatei
    in Debug-Builds (Ressourcen-Override), gültige top-level
    `base-config` mit Cleartext=true für den HTTP-LAN-Testweg.
  Verifiziert: AGP-Resource-Merger wählt für Debug die `src/debug`-
  Datei, für Release nur `src/main`; Debug-APK-Inhalt per
  `aapt2 dump xmltree` geprüft (`cleartextTrafficPermitted=true` nur
  im Debug-APK). README: Schritt 2 als „implementiert, Geräte-Abnahme
  offen“ gekennzeichnet (statt „abgeschlossen“); PR-Text Testanzahl
  18/18 → 21/21 korrigiert.
- **Gerätebefund OnePlus 9 (11. Okt. 2026, App auf echtem Gerät getestet
  — umgesetzt, Review-Fix `6e0b908`, `e902f12`):**
  1. *Zoneless-Change-Detection:* Nach Appstart blieben asynchrone Werte
     (Geräte-ID, gespeicherte Einträge, Speichern-/Host-Ergebnisse) in
     der UI unsichtbar, bis ein User-Event (z. B. Tippen in ein
     Eingabefeld) einen CD-Pass auslöste. Angular läuft in diesem Projekt
     zoneless (Angular 22, kein zone.js), und ohne zone.js meldet nur ein
     Signal-Update (oder markForCheck) asynchrone Änderungen nach
     `await`. → Alle asynchronen UI-Zustände in `App` sind jetzt Signals
     (`deviceId`, `initError`, `entries`, `saving`, `saveError`,
     `hostUrl`, `checking`, `hostCheck`); Template bindet über
     Signal-Aufrufe (`hostUrl` per `ngModel`/`ngModelChange`). Neue
     Zoneless-DOM-Tests: verzögerte Mocks, Assertions **ohne** manuelles
     `detectChanges()` — der DOM-Update muss von Angular selbst kommen
     (`untilVisible` wartet auf den gerenderten Zustand, weil
     `whenStable()` im Zoneless-Testumgebung nur Angular-interne Tasks
     trackt, nicht die rohen Mock-Timer). Mutations-Check: kaputtes
     Signal-Write wird von den Tests erkannt (Timeout mit klarem Fehler).
  2. *SQLite-Plugin-Vertrag:* `CapacitorSQLite` 8.1.1 prüft `has("values")`
     und wirft `query()`/`run()`-Aufrufe ohne `values`-Feld ab — auch bei
     parameterlosen Queries — der Appstart brach beim Laden der Einträge
     ab. → `listEntries()` sendet explizit `values: []`; der Test-Fake
     modelliert den Vertrag (`requireValues`), neuer Test sichert ihn ab
     (ohne `values` fehlerhaft, mit `[]` gültig).
  3. *Konsistenz im selben Bereich:* Beträgeingabe — Fehlermeldung
     „max. 13 Stellen vor dem Komma“ vs. Regex `\d{1,10}` → Regex auf 13
     Stellen (long-Cent-Systemgrenze; 10^15 Cent bleibt ein sicheres
     TypeScript-Integer), Grenztest ergänzt; Browser-Meldung jetzt
     „installierten Android-App“ (Test-Assertion angepasst).
  4. *Debug-APK neu gebaut* (21:11) und verifiziert: enthält die neuen
     Chunks (`main-PYPOQRA3.js` inkl. neuer Meldung) — Installation auf
     dem Gerät ist der nächste Schritt zur Abnahme.

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
      (Vitest: host 2 + mobile 23 — Browser-Abbruchpfad, Cent-Validierung
      inkl. 13-Stellen-Grenze, Geräte-ID-Stabilität, Health-Checks,
      Zoneless-DOM-Tests, Plugin-`values`-Vertrag)
- [x] .NET-Prüfungen: build (0 Warnungen) + Tests (32/32 grün)
- [x] Live-Host-Prüfung: HTTP + HTTPS auf Loopback **und** Heimnetz-IP
      (192.168.1.226), HTTPS inkl. CA-Verifizierung (`curl --cacert`) — grün
- [x] README: Prototyp-Startweg (Build, Sync, APK, Install, HTTPS/Zertifikat, LAN)
- [x] **Geräte-Abnahme (bestanden 11. Okt. 2026, OnePlus 9):** Flugmodus: Testdatensatz speichern → App beenden/neustarten → wiederfinden
- [x] **Geräte-Abnahme (bestanden 11. Okt. 2026, OnePlus 9):** Host im Heimnetz erreichen (HTTP und HTTPS mit installiertem Zertifikat)
- [x] **Geräte-Abnahme (bestanden 11. Okt. 2026, OnePlus 9):** Host ausgeschaltet → weiter speichern
- [x] Task-Index aktualisiert, Commits (`40a8b1f`, `4e44943`, `aa281c2`, `b5606a0`), **gepusht** auf
      `origin/step/2-android-prototyp`, **PR #3** → main (11. Okt. 2026, nach Nutzerfreigabe)
- [x] Gerätebefund OnePlus 9 umgesetzt (Zoneless-Signale, `values`-Pflichtfeld,
      Eingabegrenzen), Review-Fix-Commits `6e0b908`, `e902f12` gepusht, Debug-APK
      neu gebaut und verifiziert (11. Okt. 2026)

## Offene Punkte / Blocker

- **Keine fachlichen Blocker mehr.** Alle Abnahmen (automatisiert +
  Geräte-Abnahmen) bestanden.
- **Restpunkt Prozess:** PR #3 (`step/2-android-prototyp` → `main`) wartet
  auf Merge — erst nach Merge beginnt Schritt 3 (Reihenfolge verbindlich).
- **Kenntnis (kein Blocker):** Host-Start über `dotnet run` funktioniert auf
  dieser Maschine nicht (SDK in `/usr/share/dotnet` ohne ASP.NET-Runtime;
  Apphost-Lösung via `DOTNET_ROOT`; siehe README-Hinweis).
