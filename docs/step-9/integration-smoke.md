# Schritt 9 — Integration gegen echten Host

Der optionale Batch-C-Test `frontend/apps/mobile/src/app/sync/sync-host.integration.spec.ts`
führt die tatsächlichen mobilen Repositories und den `SyncService` mit den eingecheckten
generierten OpenAPI-Clients gegen einen separaten ASP.NET-Core-Prozess aus. Mobile und
Host verwenden echte SQLite-Dateien in einem temporären Verzeichnis. Ausschließlich
synthetische Zugangsdaten und Buchungen werden angelegt; die Dateien werden nach dem
Test gelöscht. Der Host bindet nur an Loopback.

## Ausführen

Voraussetzungen: Abhängigkeiten gemäß README installiert, .NET SDK verfügbar,
Node mit `node:sqlite`, freier Loopback-Port 18089.

```bash
bash scripts/test-step9-sync.sh
# Bei abweichendem lokalen .NET-Verzeichnis zum Beispiel:
DOTNET_ROOT=/home/mario/.dotnet bash scripts/test-step9-sync.sh
# Optional anderer Port:
LARIO_SYNC_SMOKE_PORT=18090 bash scripts/test-step9-sync.sh
```

Das Script baut den Host, setzt `LARIO_SYNC_HOST` und `LARIO_SYNC_HOST_BINARY` und
startet ausschließlich die Integration-Spec. Ohne `LARIO_SYNC_HOST` überspringt die
normale Frontend-Suite diesen Test. Die Spec startet und beendet ihren eigenen Host;
keinen produktiven Host oder produktive Datenverzeichnisse übergeben.

## Nachweise

1. Login, Stammdatenabruf, Gerätekopplung und Bootstrap über den originalen mobilen
   `HostClient` und seine generierten Clients; weiterer Bootstrap über den echten
   mobilen `SyncService`. Das Setup-Formular selbst wird dabei nicht gerendert;
   dessen Bedienablauf und atomare Einrichtung prüfen separate Komponententests.
2. Hostprozess beenden; dessen Nichterreichbarkeit prüfen; zwanzig Ausgaben mit je
   8,50 € und zwei Splits (6,50 € Flex, 2,00 € Freizeit) lokal speichern.
3. Mobile SQLite schließen und erneut öffnen; neue Service-Instanzen finden alle
   zwanzig ausstehenden Eingaben. Host mit derselben temporären Datenbank neu starten.
4. Die erste Push-Antwort nach erfolgreichem echtem HTTP-Aufruf verwerfen: Server hat
   bereits committet, lokal bleibt dieselbe Operation mit demselben Hash `sending`.
   Der Gerätezugang wird danach widerrufen: Sync bleibt gesperrt und alle zwanzig
   Eingaben bleiben erhalten. Nach erneuter Kopplung über `HostClient` bestätigt
   der Folgesync dieselbe Buchung über die zweite Idempotenzstufe (stabile
   Buchungs-ID bei neuer Geräte-ID) und übernimmt die übrigen neunzehn.
5. Alle zwanzig lokalen Outbox-Zeilen sind `confirmed`, alle Buchungsrevisionen 1.
   Zwei weitere identische Requests liefern `already_processed` mit Quittung.
6. Desktop-Lese-API liefert exakt zwanzig Ausgaben. Von 1.000,00 € Anfangsbestand
   bleiben 830,00 € Konto-/Poolbestand, davon 570,00 € Flex und 260,00 € Freizeit.
7. Gerätezugang widerrufen; Sync zeigt den Widerruf. Weitere lokale Ausgaben lassen
   sich weiterhin speichern und bleiben in der Outbox.

Die Antwortverlustsimulation verwirft die Antwort in einem Fetch-Wrapper nach
Servererfolg; sie simuliert keinen physischen LAN-Abbruch. Dieser headless Test
ersetzt keine Android-Abnahme: SQLite-Plugin auf echtem Gerät, Offline-Appneustart,
LAN/HTTPS und Zertifikatsvertrauen bleiben separat zu prüfen.

## Ergebnis

Am 19. November 2026 ausgeführt:

```bash
DOTNET_ROOT=/home/mario/.dotnet bash scripts/test-step9-sync.sh
```

Ergebnis: Host-Build ohne Warnungen/Fehler; ein Integrationstest bestanden
(3,96 Sekunden). Enthalten sind der echte Host- und mobile SQLite-Neustart,
zwanzig bestätigte Ausgaben, verlorene erste Quittung, Widerruf und erneute
Kopplung vor Wiederholung, doppelte Wiederholung sowie die Desktop-Saldenprüfung.
Gezieltes ESLint der Integration-Spec bestanden. Der Lauf benötigte die Freigabe
für Loopback-Netzwerkzugriff außerhalb der Sandbox.
