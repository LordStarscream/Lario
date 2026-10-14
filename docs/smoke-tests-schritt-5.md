# Smoke Tests auf dem letzten gemergten Stand (Schritt 5)

**Stand:** main @ `0859cf9` (enthält Schritt-5-Merge `a004d10` + Review-Fixes).
**Erstellt:** 14. Okt. 2026 — als Fortsetzung der am 13. Okt. abgebrochenen Smoke-Tests
(damals: Test 1 bestanden, Test 2 beim Host-Stop abgebrochen, Test 3 nicht begonnen).

**Ausgangslage dieser Maschine:**
- Host-DB liegt unter `~/.local/share/lario/lario.sqlite` und ist bereits auf
  Migration 5 (alle Migrations 1–5 angewendet, Business-Tabellen leer).
- Der Host-Build läuft über das Apphost-Executable mit `DOTNET_ROOT=$HOME/.dotnet`
  (`dotnet run` findet hier die Runtime nicht — siehe README).
- Der Host bindet standardmäßig an `127.0.0.1:8080` (nur lokal erreichbar).
- Android: OnePlus 9, App `de.lario.app` (Debug-Build aus Schritt 2).
- **Vor Test 3: Smartphone per USB verbinden** — zuletzt war kein Gerät verbunden
  (`adb devices` leer).

---

## Vorbereitung (einmalig)

```bash
cd /home/mario/Projects/Lario
git checkout main && git pull        # gemergten Stand beziehen
git status --short --branch          # sollte saubere main sein

# Web-UI in den Host bauen (nur wenn src/Lario.Host/wwwroot leer/veraltet ist):
cd frontend && npm ci && npm run build:host && cd ..

# Host bauen:
dotnet build src/Lario.Host/Lario.Host.csproj
```

Snapshot-Helfer (Zeilenzählung aller Tabellen + Migrationshistorie) — für Test 2
liegt er unter `tools/lario-snapshot.sh`:

```bash
tools/lario-snapshot.sh /tmp/vor.txt
# → schreibt "Tabelle=Zeilen"-Zeilen + Migrationszähler in die Datei
diff /tmp/vor.txt /tmp/nach.txt   # außer Zeitstempel/db-Zeile muss leer sein
```

---

## Test 1 — Host mit bestehender Datenbank starten

**Ziel:** Host startet fehlerfrei auf der bestehenden DB; Health- und
Version-Endpunkt antworten.

1. Host starten (eigenes Terminal, oder mit Log-Datei):

   ```bash
   DOTNET_ROOT=$HOME/.dotnet \
     ./src/Lario.Host/bin/Debug/net10.0/Lario.Host
   ```

2. In einem zweiten Terminal prüfen:

   ```bash
   curl -s http://127.0.0.1:8080/api/health
   # → {"status":"ok","dataDirectory":"/home/mario/.local/share/lario"}
   curl -s http://127.0.0.1:8080/api/version
   ```

**Bestanden, wenn:** Startlog ohne Fehler/Ausnahmen, Health antwortet mit
`"status":"ok"`.

> **Variante „Migration + Backfill von null":** Die aktuelle DB ist bereits auf
> Migration 5, hier läuft beim Start also nichts mehr nach. Um gezielt
> „alle 5 Migrations + Upgrade-Backfill laufen ohne Fehler" zu sehen, Host
> einmalig auf einer frischen Datenablage starten:
>
> ```bash
> rm -rf /tmp/lario-smoke
> DOTNET_ROOT=$HOME/.dotnet Lario__DataDirectory=/tmp/lario-smoke \
>   ./src/Lario.Host/bin/Debug/net10.0/Lario.Host
> # Startlog: 5 Migrations angewendet, Backfill-Scan läuft, danach:
> sqlite3 /tmp/lario-smoke/lario.sqlite "SELECT COUNT(*) FROM __EFMigrationsHistory;"
> # → 5
> rm -rf /tmp/lario-smoke
> ```

---

## Test 2 — Host beenden und erneut starten: Datenintactheit

**Ziel:** Neustart mit derselben Datenablage erzeugt keine zusätzlichen und
verliert keine Datensätze. (Host aus Test 1 läuft noch.)

1. **Vorher-Snapshot** (Host läuft noch):

   ```bash
   tools/lario-snapshot.sh /tmp/lario-vor-neustart.txt && cat /tmp/lario-vor-neustart.txt
   ```

   (`/tmp/lario-vor-neustart.txt` ist damit die gerade erzeugte Datei —
   der DB-Zustand, solange der Host läuft.)

2. **Sauber beenden:** `Strg+C` im Host-Terminal (SIGTERM), bzw.
   `pkill -INT -f Lario.Host`. Warten, bis der Prozess fort ist
   (`pgrep -af Lario.Host` → leer).

3. **Erneut starten** (wie in Test 1). Health-Endpunkt erneut prüfen.

4. **Nachher-Snapshot + Diff:**

   ```bash
   tools/lario-snapshot.sh /tmp/lario-nach-neustart.txt
   diff /tmp/lario-vor-neustart.txt /tmp/lario-nach-neustart.txt
   ```

**Bestanden, wenn:** außer den beiden Kommentarzeilen (`# Zeitstempel`,
`# db=…`) keine Unterschiede — keine neue Tabelle, keine veränderten
Zeilenzahlen.

---

## Test 3 — Android-App: Offline-Daten, Geräte-ID, Verbindung

**Ziel:** App startet ohne Host/Internet, lokale Einträge sind erhalten,
Geräte-ID ist unverändert, Verbindungstest gegen den Host klappt.

Voraussetzung: OnePlus 9 per **USB** angeschlossen, USB-Debugging aktiv;
`adb devices` zeigt das Gerät.

1. **Host für LAN-Zugriff erreichbar machen.** Der Host bindet default nur auf
   `127.0.0.1`. Für den Verbindungstest vom Smartphone Host (neu) mit
   `Lario__Http__Address=0.0.0.0` starten, z. B.:

   ```bash
   DOTNET_ROOT=$HOME/.dotnet Lario__Http__Address=0.0.0.0 \
     ./src/Lario.Host/bin/Debug/net10.0/Lario.Host
   # Host-LAN-IP ermitteln: hostname -I
   # → in der App als Host-URL eintragen: http://<Host-LAN-IP>:8080
   ```

   (Falls die App in Schritt 2 auf HTTPS:8443 mit Testzertifikat eingestellt
   ist: Zertifikat unter `/tmp/lario-cert.pem` bzw. `/tmp/lario-key.pem`
   könnte nach Neustart der Maschine weg sein — dann neu erzeugen, README
   „HTTPS für den LAN-Prototyp".)

2. **Offline-Verhalten prüfen (ohne Host):** Host-Port blockieren oder Host
   beenden, App öffnen → App startet, zeigt die lokalen Einträge
   (`prototype_entries`) aus den Schritt-2-Tests.

3. **Geräte-ID prüfen:** In der App oben steht `Geräte-ID: <code>…</code>`.
   App komplett schließen (App-Übersicht wegwischen / `adb shell
   am force-stop de.lario.app`) und neu öffnen → dieselbe Geräte-ID
   (in der lokalen DB in der Tabelle `meta`, Key `device_id`, persistiert).

4. **Verbindungstest:** Host (mit `0.0.0.0`) läuft, in der App die
   Host-URL `http://<Host-LAN-IP>:8080` setzen und **„Verbindung testen"**
   drücken → erfolgreich.

**Bestanden, wenn:** lokale Einträge unverändert vorhanden, Geräte-ID nach
Neustart identisch, Verbindungstest grün.

---

## Test 4 (optional) — Frische Datenablage: Vollständiger Migrationslauf

Dies ist die in Test 1 als Variante erwähnte Prüfung, falls sie als
eigener Test gezählt werden soll:

```bash
rm -rf /tmp/lario-smoke
DOTNET_ROOT=$HOME/.dotnet Lario__DataDirectory=/tmp/lario-smoke \
  ./src/Lario.Host/bin/Debug/net10.0/Lario.Host 2>&1 | tee /tmp/lario-fresh.log
curl -s http://127.0.0.1:8080/api/health
sqlite3 /tmp/lario-smoke/lario.sqlite "SELECT COUNT(*) FROM __EFMigrationsHistory;"
rm -rf /tmp/lario-smoke
```

**Bestanden, wenn:** 5 Migrations + Backfill ohne Fehler, Health `ok`,
`__EFMigrationsHistory` = 5 Zeilen.

---

## Abbruchstand vom 13. Okt. (Protokoll)

| Test | Stand am 13.10. |
|---|---|
| 1 | **Bestanden** 19:52 UTC — 4 nachstehende Migrations + Backfill gelaufen, Diff wie erwartet (1→5 Migrationszeilen, Business-Tabellen unverändert bei 0 Zeilen), Health `ok` |
| 2 | **Abgebrochen** — bei „sauberes Beenden (SIGTERM)" |
| 3 | **Nicht begonnen** — kein adb-Gerät verbunden, `wwwroot` war leer |
