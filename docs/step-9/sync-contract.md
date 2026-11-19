# Schritt 9 — implementierter Synchronisationsvertrag

Maßgeblich für DTOs ist `frontend/openapi.json`; beide Oberflächen verwenden die
hieraus generierten Clients. Alle Pfade beginnen mit `/api/v1/sync`.

| Endpunkt | Zugang | Zweck |
|---|---|---|
| `POST /pair` `{name}` | Sitzungstoken | Gerät anlegen; Antwort enthält `id`, `token` |
| `GET /devices` | Sitzungstoken | Geräte auflisten; ohne Klartexttoken |
| `DELETE /devices/{id}` | Sitzungstoken | Gerät widerrufen |
| `POST /bootstrap` | Gerätetoken | Konsistente Stammdaten mit `cursor`, `dbGeneration`, `protocolGeneration` |
| `POST /push` | Gerätetoken | `{operationId,type:'expense',payload,payloadHash,createdAtUtc}` |
| `GET /changes?cursor=N&limit=200` | Gerätetoken | `{cursor,complete,truncated,changes}` |

Push bestätigt nur mit `ack: true`, passender `bookingId`, gültiger Revision und
passender Protokollgeneration. Status ist `accepted` oder `already_processed`.
Die lokale Quittierung bindet die Operation an den Hash ihres eingefrorenen Inhalts.
Der Host behandelt gleiche Operation/gleichen Inhalt idempotent; die stabile
Buchungs-ID verhindert zusätzlich Duplikate nach einer erneuten Kopplung.

`queued` wird vor Netzwerkzugriff atomar `sending`. Antwortverlust/401/Netzfehler
lassen den Inhalt eingefroren. HTTP 400 wird `rejected` mit sichtbarem Grund und ist
korrigierbar. HTTP 409 bleibt `sending` mit sichtbarer Konfliktmeldung; kontrollierte
Abstimmung erforderlich, kein stilles Überschreiben oder Verwerfen. Nur eindeutig
quittierte Eingaben werden `confirmed` und bleiben unveränderbar.

Vor Push wird ein Bootstrap geladen und die gespeicherte Generation geprüft.
Generationwechsel stoppt ohne lokale Eingaben zu löschen oder die alte Generation
zu ersetzen. Snapshot, Cursor und Generationen werden gemeinsam atomar gespeichert.
Pull-Seiten werden mit der letzten Änderungs-ID weitergelesen; der Antwortcursor ist
die obere Grenze der Leseansicht und wird erst bei vollständiger Übernahme gespeichert.
Stammdatenänderungen lösen einen neuen konsistenten Snapshot aus. Fremde Buchungen
werden in Schritt 9 noch nicht lokal übernommen (Schritt 10).

Die Einrichtung lädt nach Login und Pair einen Bootstrap mit Gerätetoken. Snapshot,
Verbindung und Sync-Meta werden in einer lokalen Transaktion übernommen; bei Fehler
wird die neue Kopplung best effort widerrufen und die vorherige Verbindung erhalten.
Hostbindung (`sync.host`) und Generationen bleiben beim Abmelden erhalten. Ein
Hostwechsel mit vorhandenen Eingaben benötigt kontrollierte Geräteabstimmung.
Neukopplung zum selben Host und derselben Generation erhält offene Sendungen.
Setup, Health und Synchronisation teilen eine Sperre für die Client-Konfiguration.

Die bisherige lokale Zugangsdatenablage aus Schritt 8 bleibt bestehen; eine native
Keystore-Anbindung ist in diesem Stand nicht umgesetzt. LAN/HTTPS, gezieltes
Zertifikatsvertrauen und dauerhafte Speicherung sind auf echtem Android erneut zu
prüfen. Zertifikatsprüfung bleibt aktiv. Testumfang und Grenzen stehen in
[integration-smoke.md](integration-smoke.md).
