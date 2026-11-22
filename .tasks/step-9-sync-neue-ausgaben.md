# Schritt 9 — Synchronisation neuer Ausgaben

**Status:** aktiv (Branch `step/9-sync-neue-ausgaben`, neu ab `origin/main` `85e5925` = PR #20, 18.11.2026) — A1 (Host-Protokoll) + Changelog-Fix in `4b8c64a`, A2 (Mobile-DB-Ebene) in `91ba0cf`, Outbox-Zustandsmaschine `queued→sending→confirmed|rejected` + Hash-gebundene Quittung in `a631ab1`, regenerierte OpenAPI-Clients (Sync-Vertrag mit `cursor`/`already_processed`/`ack`) in `f5392fc`. Review-Runde (LordStarscream, 18.11., 3 Befunde P1/P1/P2 auf Stand `7564bfd`): Client-Vertrag durch `f5392fc` abgestimmt, Quittungsbindung und `confirmed`-Schutz durch `a631ab1` auf DB-Ebene (CAS + Payload-Hash, 22 node:sqlite-Tests). Offen: Desktop-UI „Verbundene Geräte" + Batch B-Rest (SyncService, Setup-Pairing, Sync-UI, D8-SyncService-Tests) + Batch C (E2E-Smoke, Abnahmedoku).

**Host-Vertrag (maßgeblich, aus Batch A1 — weicht stellenweise von D5/D6 oben ab):**
- Push-Antwort: `{status: 'accepted' | 'already_processed', bookingId, protocolGeneration, revision, ack}` (`ack: true` = einzige Quittungsquelle); 409 `payload_mismatch` als ErrorResponse.
- Pull: `GET /api/v1/sync/changes?cursor=<n>&limit=<n>` (Default 0) → `{cursor, complete, truncated, changes:[{id, dbGeneration, entityType, entityId, revision, occurredAtUtc}]}`.
- Bootstrap: `POST /api/v1/sync/bootstrap` → `{protocolGeneration, dbGeneration, cursor, accounts, pools, categories, taxCategories}`.
- Pair: `POST /api/v1/sync/pair` Body `{name}` → `DeviceDto {id, name, token, createdAtUtc, revokedAtUtc, lastUsedAtUtc}`; Geräte-Liste `GET /devices` (token leer), Widerruf `DELETE /devices/{id}` (idempotent, 204).
- Sync-Endpunkte akzeptieren **nur** Gerätetokens (Session-Token → 401).

**Mobile-Entscheidungen (Batch B, 16.11.2026):**
- Gerät-Meta als eigene Keys `sync.device_id` / `sync.device_token` / `sync.device_name` (nicht in der 5-Keys-auth-Meta — `loadAuthMeta` bleibt unverändert; Abweichung von D1, dokumentiert).
- Cursor/Generationen in Meta: `sync.cursor`, `sync.db_generation`, `sync.protocol_generation`; letzter Sync `sync.last_sync_at`.
- Pairing läuft im Setup-Fluss nach Login+Listen (Fehler dort = Setup-Fehler, sichtbar); bei erneutem Setup koppelt die App neu (D1 bleibt bestehen).
- Sync-UI: Statuszeile + Button „Synchronisieren“ im **Eingaben-Tab** (D7); Setup zeigt Geräteinfo (Name, ID).
- Sync-Reihenfolge pro Start: (1) kein lokaler Cursor oder Generation-Wechsel → Bootstrap; (2) Push aller queued (sequenziell); (3) Pull `changes?cursor=`; (4) Cursor erst nach lokaler atomarer Übernahme.
- `outbox.status_reason TEXT NULL` (Migration via `PRAGMA table_info` + `ALTER TABLE` in `LocalDatabase.doInit`); `update()`/`discard()` akzeptieren `queued` und `rejected` (D7); `confirmed` bleibt gesperrt.
- Outbox-Zustandsmaschine (Review-Nachgang, `a631ab1`): `prepareSend(operationId, payloadHash)` friert den Inhalt ein (CAS `queued→sending`; bereits `sending` → eingefrorener Inhalt bleibt unverändert); `markConfirmed`/`markRejected` nur aus `sending` und nur mit dem Hash des gesendeten Inhalts (Abweichung → `payload_conflict`, kein Statuswechsel); `update()`/`discard()` verweigern `sending`; Wiederholung nach unklarem Ergebnis sendet denselben Inhalt + dieselbe ID (idempotent, keine Wiederverwendung). 22 Tests gegen echte SQLite (`node:sqlite`).
- `bookings.base_revision` wird bei Quittung aus der Push-Antwort (`protocolGeneration` = Basisrevision des Hosts) gesetzt.
**Plan:** `docs/Finanzplanung_Implementierungsplan.md` §5 „Schritt 9“; Protokollgrundsätze:
`docs/Finanzplanung_Architektur_und_Umsetzungsplan.md` §6, `docs/step-0/rechenvertrag.md` §5.4/§5.5,
Datenmodell `docs/step-0/data/sync-operation.json`.

## Auftrag (verbindlich aus dem Plan)

**Implementieren:** Gerätekopplung, widerrufbare Zugangsdaten, Bootstrap mit konsistentem
Snapshot/Cursor, Push von Operations-ID plus Payloadnachweis, atomare Annahme inklusive
ChangeLog, Quittierung und Pull. Datenbank-/Protokollgeneration von Anfang an speichern.

**Prüfen (Abnahmen A1–A5):**
1. Alle 20 Eingaben aus Schritt 8 einmal übertragen (20 Offline-Erfassungen → Sync → 20 exakt
   einmalige Buchungen im Host, korrekte Desktop-Salden).
2. Derselbe Request dreimal ergibt dieselben Buchungen (Idempotenz).
3. Verbindung nach Servercommit vor Antwort abbrechen; Wiederholung bestätigt statt doppelt zu buchen.
4. Gleiche Operations-ID mit verändertem Payload wird abgelehnt.
5. Token widerrufen: Sync scheitert, lokale Erfassung bleibt möglich.

**Fertig:** Erste vollständige Strecke Einrichtung → offline erfassen → LAN synchronisieren
→ korrekte Desktop-Salden.

**Scope-Grenzen (dokumentiert):**
- Nur Operations-Typ `createBooking` (Ausgaben) — das ist der Outbox-Inhalt aus Schritt 8.
  `updateBooking`/Storno/Archiv/Konfliktbehandlung = Schritt 10.
- Keine Automatik-Sync (nur manueller Start), keine Hintergrundausführung, keine Cloud.
- Mobile Kamera/QR = spätere Erweiterung; die Kopplung nutzt den bestehenden
  Host-Login (Adresse + Benutzername + Passwort), QR kann später auf demselben
  Gerätetoken-Modell aufsetzen.
- Pull übernimmt auf dem Gerät nur Stammdaten-Änderungen (Snapshot-Refresh).
  Buchung-Änderungen anderer Quellen werden im ChangeLog protokolliert, aber in Schritt 9
  nicht lokal angewendet (Schritt 10).
- Geräte-Abnahmen (echtes Android: Offline-Neustart, LAN/HTTPS) bleiben offen — kein Gerät
  verfügbar (Blocker, bereits in Schritt 8 dokumentiert).

## Design-Entscheidungen

### D1 — Gerätekopplung und widerrufbare Zugangsdaten

- Neue EF-Entität `SyncDevice` (Tabelle `sync_devices`): `Id` (GUID-String, PK),
  `Name` (Anzeigename, vom Gerät gesendet, z. B. „Pixel 8“), `TokenHash`
  (SHA-256-Hex des Tokens, UNIQUE — im Klartext nur bei Ausstellung), `CreatedAtUtc`,
  `RevokedAtUtc?`, `LastUsedAtUtc?`.
- Gerätetoken: 32 Zufallsbytes als Hex (wie Sitzungstokens), **lang gültig** (kein
  Ablauf) — Entwertung ausschließlich über Widerruf. Persistiert in der DB
  (Host-Neustart verliert keine Kopplungen; das in-memory `AuthTokenStore` reicht dafür
  nicht).
- **Kopplung:** `POST /api/v1/sync/pair` (Session-Token des bestehenden Logins,
  Body `{name}`) → legt `SyncDevice` an und liefert `{deviceId, name, token}`
  (Token genau einmal im Klartext). Die mobile App ruft das im Setup unmittelbar nach
  erfolgreichem Login auf; `deviceId`+`token` landen in der auth-Meta
  (`auth.device_id`, `auth.device_token`). Bei erneutem Setup mit bestehendem Gerät
  koppelt die App neu (neues Gerät; alte Kopplung bleibt, bis widerrufen — dokumentiert).
- **Widerruf:** `GET /api/v1/sync/devices` (Session) → Liste
  (`{id, name, createdAtUtc, lastUsedAtUtc, revokedAtUtc}`);
  `DELETE /api/v1/sync/devices/{id}` (Session) → setzt `RevokedAtUtc` (idempotent).
  Spätere Sync-Aufrufe des widerrufenen Tokens → 401 (Fehlerformat, Code
  `unauthorized`, Meldung „Gerät nicht mehr verbunden“).
- **Auth-Middleware:** `UseApiAuth` akzeptiert einen Bearer, wenn er ein gültiges
  Session-Token **oder** ein aktives (nicht widerrufenes) Gerätetoken ist
  (Hash-Lookup in `sync_devices`). `LastUsedAtUtc` wird bei erfolgreichem Sync
  (Push/Bootstrap/Pull) mitgeschrieben, nicht in der Middleware (kein Schreibpfad
  auf dem Leseweg).
- **Desktop-UI (dünn):** Einrichtung-Seite erhält einen Abschnitt „Verbundene Geräte“:
  Liste + „Widerrufen“ mit Bestätigung. Damit ist Widerruf ohne curl nutzbar.
- Widerrufter/abgelaufener Gerätetoken im Gerät: Sync schlägt fehl mit sichtbarer
  Meldung (Gerät erneut einrichten); **lokale Erfassung bleibt möglich** (DB und
  Formular sind unabhängig von der Verbindung).

### D2 — Protokollzustand: Generationen und Cursor

- Neue EF-Tabelle `sync_protocol_state` (einzelne Zeile, PK `id='1'`):
  `DbGeneration` (Start 1), `ProtocolGeneration` (Start 1), `CursorCounter`
  (monoton steigendes Long, Start 0).
- Die Zeile wird von der Schritt-9-Migration angelegt („von Anfang an speichern“).
- `DbGeneration` steigt bei Restore (Schritt 17), `ProtocolGeneration` bei
  Protokollwechsel — in Schritt 9 existiert nur die Infrastruktur (Speichern +
  Mitteilen + Mismatch-Reaktion), keine Erhöhungspfade.
- **Cursor** = `sync_changes.Id` (monoton). Gerätetoken-Antworten
  (Bootstrap/Pull) enthalten `dbGeneration`, `protocolGeneration` und `cursor`.
  Weicht eine Generation vom lokal gespeicherten Wert ab, setzt das Gerät auf
  volles Bootstrap (Snapshot + Cursor + Generation atomar neu übernehmen) —
  Reaktion auf zurückgesetzten Änderungsstand (dormant in Schritt 9, per Test
  mit manipulierten Werten belegt).

### D3 — ChangeLog

- Neue EF-Tabelle `sync_changes`: `Id` (INTEGER PK, autoincrement = Cursorwert),
  `DbGeneration`, `EntityType` (`account`|`pool`|`category`|`taxCategory`|`booking`),
  `EntityId`, `Revision` (Revision der Entität nach der Änderung), `OccurredAtUtc`.
- **Schreibzugänge (alle in derselben Transaktion wie die Fachänderung):**
  - Sync-Push (Buchungsanlage) → `booking`-Zeile (Revision 1).
  - Stammdaten-Schreibpfade (Konto/Pool/Kategorie/Steuerkategorie: Anlage, Update,
    Archivierung) → je eine Zeile. Haken: in den bestehenden
    `MasterDataRepository`-Schreibmethoden (gleicher Scope/`SaveChanges`).
- ChangeLog-Zeilen werden **nie gelöscht** (Archiv für Schritt 10/17,
  Wiederabstimmung).

### D4 — Bootstrap: konsistenter Snapshot + Cursor

- `POST /api/v1/sync/bootstrap` (Gerätetoken, Body `{}`) →
  `{dbGeneration, protocolGeneration, cursor, snapshot: {accounts, pools, categories, taxCategories}}`
  (gleiche DTOs wie die Stammdaten-Liste-Endpunkte).
- **Konsistenz:** in einer expliziten Read-Transaktion (SQLite) zuerst
  `cursor = MAX(sync_changes.Id)` (0 wenn leer) lesen, dann die vier
  Stammdaten-Listen — dieselbe transaktionale Sicht, kein gemischter Stand
  (Muster: Schritt 7a D5, Autocommit-Reads sind jeweils frisch).
- Gerät: Snapshot-Replace + Cursor + Generationen in **einer** lokalen
  Schreibtransaktion (erweitertes `SnapshotRepository` oder neuer
  Sync-Repository-Methoden-Pair; Cursor/Generationen in der Meta-Tabelle:
  `sync.cursor`, `sync.db_generation`, `sync.protocol_generation`).

### D5 — Push: atomare Annahme + Idempotenz

- `POST /api/v1/sync/push` (Gerätetoken), **eine Operation pro Request**
  (das Gerät schickt die queued Operations nacheinander in lokaler Reihenfolge:
  `createdAtUtc` aufsteigend, dann `operationId`):
  Body `{operationId, type: 'expense', payload: <ExpensePayload JSON>, payloadHash (Hex), createdAtUtc}`.
- Neue EF-Tabelle `sync_operations` (verarbeitete Operationen, **dauerhaft** —
  „nicht quittierte Operationen bei Pull erhalten“ + Operationsarchiv):
  PK `(DeviceId, OperationId)`, `Type`, `PayloadHash`, `PayloadJson`,
  `BookingId`, `AcceptedAtUtc`.
- **Verarbeitung (eine DB-Transaktion, EF):**
  1. Idempotenz-Check vor allem anderen (Rechenvertrag §5.5):
     - `(deviceId, operationId)` vorhanden + `payloadHash` gleich → **idempotenter
       Treffer**: nichts anwenden, Antwort
       `{status: 'already_processed', bookingId, revision, ack: true}`.
     - Vorhanden + `payloadHash` **anders** → **409 `payload_mismatch`**
       (sichtbar; die Operation wird im Gerät als `rejected` markiert, die
       Warteschlange läuft weiter — kein Stillstand).
     - Nicht vorhanden → weiter (eine nie angenommene Operation kann mit
       geändertem Inhalt erneut eingereicht werden — z. B. nach lokaler
       Korrektur einer `rejected`-Eingabe; Idempotenschutz greift erst,
       wenn eine Annahme existiert).
  2. Payload-Validierung (Host-Stammdaten, nicht Geräte-Snapshot): Struktur
     (Betrag > 0, Splits > 0, Summe Splits = Gesamtbetrag, Split-IDs eindeutig) +
     Referenzen (Konto/Pool/Kategorie existieren und sind aktiv;
     Steuerkategorie optional, existiert + aktiv). Fehler → **400** mit Detailcodes
     (Muster der bestehenden Validierung: `invalid_pool`, `unknown_account` …).
  3. Buchung über den **bestehenden** `ExpenseService.CreateAsync` anlegen
     (client-specified `Id` = Geräte-Buchungs-ID; die Service-Idempotenz
     (gleiche ID + gleicher Inhalt) bleibt als zweite Verteidigungsebene,
     primär ist die Operationsebene).
  4. `sync_changes`-Zeile (`booking`, Revision 1) + `sync_operations`-Zeile
     in derselben Transaktion.
  5. Commit → erst dann Antwort `{status: 'accepted', bookingId, revision: 1, ack: true}`.
     **Antwort erst nach Commit** = Grundlage für Abnahme A3 (Abbruch nach
     Servercommit: Wiederholung → `already_processed`, keine doppelte Buchung).
- `LastUsedAtUtc` des Geräts wird bei erfolgreicher Annahme mitgesetzt.

### D6 — Pull: Änderungen seit Cursor

- `GET /api/v1/sync/changes?cursor=N` (Gerätetoken) →
  `{cursor: N', changes: [{id, entityType, entityId, revision}]}`
  (Zeilen mit `Id > N`; `N'` = max `Id` bei der selben konsistenten Lese-Transaktion
  wie D4; leer + `N' = N` erlaubt).
- Gerät-Sequenz pro Sync (manueller Start):
  1. Kein lokaler Cursor (oder Generation-Wechsel) → **Bootstrap** (D4).
  2. **Push** aller `queued` Operations (D5), sequenziell in lokaler Reihenfolge.
     Fehler 400/409 → Operation `rejected` (mit Grund), weiter mit der nächsten.
     Fehler 401/Netzwerk → Sync **stoppen**, alles bleibt `queued` (kein
     `rejected` — bei 401/Netzwerk ist der Serverzustand unbekannt; die nächste
     Sync-Sendung idempotisiert).
  3. **Pull** (D6): gibt es Änderungen mit `entityType` in
     `account|pool|category|taxCategory` → neues Bootstrap (volles Snapshot +
     aktueller Cursor, atomar lokal übernehmen); nur `booking`-Änderungen →
     Cursor in der Meta fortschreiben (Buchungs-Änderungen werden in Schritt 9
     nicht angewendet — dokumentiert, siehe Scope).
  4. **Cursor erst nach lokaler atomarer Übernahme** fortschreiben (Rechenvertrag
     §5.4) — im selben lokalen Transaktions-Batch wie Snapshot/Generationen
     bzw. als eigener atomarer Meta-Schrieb.
- **Quittung:** nur `ack: true` im expliziten Server-Antwort macht die Outbox-Zeile
  `confirmed` (+ `bookings.base_revision = 1` aus der Antwort). „Nur explizit
  bestätigte Operationen werden lokal quittiert.“

### D7 — Mobile Outbox-Status und UI

- Outbox-Statuswerte: `queued` (bestehend) → **`confirmed`** (nach `ack`;
  `mapOutboxStatus` kennt den Wert bereits) → **`rejected`** (neuer Status:
  Host-Ablehnung 400/409, mit Grund in neuem Spalten-Paar
  `rejected_reason_code`/`rejected_reason_message`? — **Entscheidung:** neue Spalte
  `status_reason TEXT NULL` in `outbox`; `rejected`-Gründe sind UI-Meldungen,
  keine Finanzdaten).
- `BookingsRepository.update()`/`discard()` akzeptieren `queued` **und** `rejected`
  (Korrektur nach Ablehnung: Payload/Hash ersetzt, Status zurück auf `queued`,
  Operation-ID bleibt — Host hat nie angenommen, s. D5-Step 1; Verwerfen löscht
  Buchung + Outbox wie heute). `confirmed` bleibt gesperrt (Korrektur = Schritt 10).
- **Sync-UI:** in der App-Shell (Tab-Bereich, z. B. oberhalb des Tab-Contents oder
  als eigene Zeile im „Eingaben“-Tab): Statuszeile „X Eingaben ausstehend“ +
  Button „Synchronisieren“ (nur wenn eingrichtetes Gerät + ≥1 queued oder
  Stammdaten-Refresh nötig). Während Sync: gesperrt + Laufzeit-Meldung
  („Synchronisiere 3/20 …“). Ergebnis: Erfolgszeile (n übernommen, m bestätigt,
  k abgelehnt) oder Fehler (Host nicht erreichbar / Gerät nicht mehr verbunden).
  Eingaben-Liste/Detail zeigen `rejected` mit Grund + Hinweis (ändern oder
  verwerfen).
- Setup-View: nach Login zusätzlich `pair` aufrufen; `auth.device_id`/
  `auth.device_token` in der auth-Meta (gleicher Persistenz-Batch wie heute).
  „Gerät nicht mehr verbunden“-Fehler bei Sync → Hinweis „Einrichtung erneut
  ausführen“ (die Einrichtung koppelt neu; bestehende queued Eingaben bleiben
  unberührt).

### D8 — Tests (verbindlich, echte SQLite — kein InMemory)

**Host (Lario.Api.Tests, `WebApplicationFactory` + echte SQLite):**
- Pair/Revocation: Pair liefert Token; Token valid für Sync; Widerruf → 401;
  `LastUsedAtUtc` nach Push gesetzt; Session-Token bleibt für Sync gültig?
  **Nein — Sync-Endpunkte akzeptieren nur Gerätetokens** (Session-Tokens an
  /sync/* → 401, damit Kopplung und Desktop-Sessions getrennt bleiben).
- Idempotenz (A2): gleicher Push 3× → 1 Buchung, 2× `already_processed`
  mit identischem `bookingId`; Revision bleibt 1.
- A3: Push → `already_processed`-Semantik nach simuliertem Antwortverlust
  (Test: zweiter identischer Request); zusätzlich deterministischer
  Commit-then-Abort-Test auf Host-Prozessebene ist auf dem Gerät nicht
  reproduzierbar — der Idempotenztest deckt die Semantik (dokumentiert).
- A4: gleiche `operationId`, geändertes Payload nach Annahme → 409
  `payload_mismatch`, keine neue Buchung; geändertes Payload **vor** Annahme
  (nach 400-Validierungsfehler) → neue Validierung + Annahme erlaubt.
- A5: widerrufenes Gerätetoken → 401 an bootstrap/push/changes.
- Bootstrap-Konsistenz: Cursor + Snapshot aus derselben Lese-Transaktion
  (zwei Host-Instanzen/Verbindungen: Masterdaten-Änderung zwischen
  Cursor-Lese und Snapshot-Lese wird entweder vollständig enthalten oder
  nicht — Test mit direkter zweiter Verbindung).
- ChangeLog: Stammdaten-Änderungen (Konto anlegen/umbenennen/archivieren,
  Pool, Kategorie, Steuerkategorie) erzeugen Zeilen; Cursor-Pull liefert sie
  in Ordnung; `changes?cursor=letzte` → leer.
- Generations-Mismatch: `DbGeneration` manuell erhöht → Pull-Antwort trägt
  höhere Generation (Gerät müsste Bootstrappen — Host-Seite: Werte korrekt
  gemeldet).
- 20×-Szenario (A1): 20 verschiedene queued Payloads nacheinander pushen
  (einer zweimal) → 20 Buchungen, Salden korrekt (Kontostand/Poolbestände),
  ChangeLog 20 booking-Zeilen.
- Konflikt-freie Parallelität: zwei parallele Pushes derselben Operation
  (zweite Verbindung) → exakt eine Buchung (Unique-Constraint
  `(device_id, operation_id)`).
- Push-Validierung: unbekanntes Konto/Pool/Kategorie, inaktive Referenz,
  Split-Summe ≠ Total, Betrag ≤ 0, Split ≤ 0, doppelte Split-ID → 400
  mit Detailcode; nichts persistiert (weder Buchung noch sync_operations).

**Mobile (Vitest, Fake-DB):**
- SyncService-Fluss: Bootstrap (erster Start) → Push → Pull, Cursor erst nach
  lokalem Commit; 20 Eingaben (Schritt-8-Referenz) → 20 `confirmed`,
  `base_revision = 1`; Outbox-Ordnung (createdAtUtc/operationId).
- Idempotenz/Abort: Server antwortet bei Operation k nicht (Netzwerkfehler) →
  alles bleibt `queued`; Folgesync schickt k erneut → `already_processed` →
  `confirmed`, keine doppelte lokale Markierung.
- Ablehnung: 400 (Validierung) und 409 (`payload_mismatch`) → `rejected` +
  Grund, Warteschlange läuft weiter (nächste Operation wird gesendet);
  `update()`/`discard()` auf `rejected` erlaubt, auf `confirmed` gesperrt.
- 401 (Token widerrufen) → Sync stoppt, keine Statusänderung, Meldung;
  lokale Erfassung (`save`) funktioniert weiter (Fake-Host 401, DB frei).
- Generation-Wechsel → volles Bootstrap statt Pull.
- Pull mit nur booking-Änderungen → nur Cursor vor, Snapshot unverändert;
  mit Stammdaten-Änderungen → Snapshot-Replace (atomar, inkl. Cursor).
- Pull schlägt fehl NACH erfolgreichem Push → Push-Ergebnisse sind lokal
  übernommen (`confirmed`), Cursor alt — Folgesync idempotent (dokumentiert).
- UI: Sync-Button-Status (ausstehend/aktiv/Ergebnis/Fehler),
  `rejected`-Darstellung in Liste/Detail, Setup koppelt neu nach Widerruf.

**E2E-Smoke (headless, echter Host + echte mobile Web-Build-Logik über
HostClient gegen laufenden Host):** 20-Eingaben-Strecke Einrichtung →
Offline (Host gestoppt) erfassen → Host starten → Sync → Desktop-API-Salden
prüfen + Idempotenz + Widerruf. (Browser-/Host-Smoke ≠ Android-Geräte-Abnahme.)

## Batches

- **A — Host-Protokoll** — **fertig in main `85e5925`** (EF: `SyncDevice`,
  `sync_protocol_state`, `sync_changes`, `sync_operations` + Migration;
  Auth-Middleware; Endpunkte pair/devices/bootstrap/push/changes;
  ChangeLog-Haken in MasterDataRepository; `SyncService`/Mapper; Api-Tests).
  Offen aus A: **Desktop-UI „Verbundene Geräte“** (Liste + Widerruf mit
  Bestätigung, Route `/geraete`) + OpenAPI neu generieren (Sync-Endpoints
  fehlen noch in `frontend/openapi.json` — **erledigt 16.11.2026**: Host
  gestartet, Dokument gezogen, Clients für host+mobile regeneriert).
- **B — Mobile Sync** — **teilweise fertig:** Mobile-DB-Ebene (`91ba0cf`):
  Outbox-Status `rejected` + Spalte `status_reason`, `update`/`discard` auf
  `rejected`; Outbox-Zustandsmaschine `sending` + Hash-gebundene Quittung
  (`a631ab1`, 22 node:sqlite-Tests); regenerierte OpenAPI-Clients mit den
  Sync-Endpunkten (`91ba0cf`, korrigiert `f5392fc`). **Offen:** SyncService
  (Bootstrap/Push/Pull-Orchestrierung, Cursor/Generationen in Meta,
  Snapshot-Replace atomar), Setup-Erweiterung (Pair), Sync-UI (Statuszeile +
  Button im Eingaben-Tab) + `rejected`-Darstellung in Eingaben-Liste/Detail,
  D8-Mobile-Tests für den SyncService-Fluss.
- **C — Integration/Abnahme** (E2E-Smoke 20-Eingaben, Abnahmedoku
  `docs/step-9/`, Index-Update, Geräte-Abnahmen als offene Blocker dokumentiert).

## Offene Abnahmen (Gerät)

- Echte Android-Geräte-Abnahmen: LAN-Sync, HTTPS-Login/Kopplung,
  Offline-Neustart mit offener Outbox + Sync nach Neustart — **Blocker: kein
  Gerät verfügbar** (wie Schritt 8).

## Changelog-Fix: Buchungszahlen (18.11.2026)

**Befund:** `SyncChangelogAvailable` war tot — die Methode trug kein `#pragma warning disable CS8602` (EF Core 10: `ChangeTracker.Entries()` ist nullable), der `Lario.Infrastructure`-Build lief mit `TreatWarningsAsErrors=true` durch, aber die Sammelaufrufe `SyncChangelogAvailable`/`SyncChangelogBooking` (A1) hatten nie einen Haken im `LarioDbContext` (A2 hatte sie nicht ergänzt). Folge: **Sync-Pushes erzeugten keine `booking`-ChangeLog-Zeile**, die Desktop-Masterdaten-Haken funktionierten (Build-Zwang). Scratch-Test (`Scratch_ChangelogCount.cs`): Session-Ausgabe → 5 Zeilen (nur Stammdaten), 0 `booking`.

**Fix (`LarioDbContext.SaveChangesAsync`):**
1. `SyncChangelogAvailable` mit `#pragma warning disable/restore CS8602` (null-Koaleszenz ist bewusst nicht genutzt — der Haken soll bei EF-Core-Änderungen auffallen).
2. Sammelbereich: `var entries = ChangeTracker.Entries().Where(e => e.State is Added or Modified or Deleted).ToList()` — **einmalig** sammeln (die früheren drei `ChangeTracker.Entries()`-Aufrufe ohne ToList() sind nicht lauffähig — EF Core 10: `IEnumerable<EntityEntry>` ist nicht `IAsyncEnumerable`, `await foreach` ist ein Compile-Fehler), danach Filter nach Entitätstyp.
3. **`booking`-Zeilen** aus `Booking`-Entitäten (Added/Modified/Deleted), Revision aus `CurrentValues["Revision"]`. Damit deckt der Haken **alle** Buchungs-Pfade ab — Sync-Push (A1) **und** Session-Erfassung (`ExpenseService`, Desktop-UI) — konsistent mit den Masterdaten-Haken, die Session- und Sync-Pfade beide protokollieren.
4. `account`/`pool`-Zeilen aus `Account`/`Pool`-Entitäten; `category`/`taxCategory`-Zeilen aus `Category`/`TaxCategory`-Entitäten (ersetzt die auflösenden Repository-Aufrufe; die `MasterDataRepository`-Haken bleiben als no-op).

**Red/Green:** Sammler temporär deaktiviert → `Session_Ausgabe_erzeugt_genau_eine_BuchungsZeile_im_Changelog` rot (5 statt 6 Zeilen); Fix aktiv → grün. Volle Suite grün: **574/574** (API 160, Domain 214, Infrastructure 200) via `~/.dotnet/dotnet test Lario.slnx -m:1` (paralleler Lauf: dokumentierter dotnet-Umgebungssonderfall, MEMORY.md).

**Neue Tests (`Step9ChangelogApiTests.cs`, 4×):** Session-Ausgabe → exakt 1 `booking`-Zeile (Revision 1, korrektes entityId); Konto via Session → 1 `account`-Zeile; 20 Sync-Pushes → 20 `booking`- + 15 Stammdaten-Zeilen, Revisionen 1–20, in Ordung, Pull danach leer; Push mit unbekanntem Konto (400) → keine Buchung, keine `booking`-Zeile (kein Phantom-Log).

**Betroffen (committet in `4b8c64a`/`91ba0cf`):** `LarioDbContext.cs` (Fix), `Step9ChangelogApiTests.cs` (neu), `MasterDataRepository.cs` (Haken no-op); `Scratch_ChangelogCount.cs` (Beweis-Test) wurde vor dem Commit entfernt.

## Übergabe/Review

- **PR #21** `step/9-sync-neue-ausgaben` → `main` (angelegt 18.11.2026; Head `a631ab1`: `4b8c64a` A1, `91ba0cf` A2 (Mobile-DB-Ebene), `3d479b6`+`7564bfd` Doku, `634b1f4` Vertragsdok, `f5392fc` regenerierte Clients, `a631ab1` Outbox-Zustandsmaschine).
- **Review-Runde (LordStarscream, 18.11.2026, auf Stand `7564bfd`, 3 Befunde):**
  1. **P1 Client-Vertrag:** generierter Client beschrieb einen anderen Vertrag (`changes?after=`, `PushResponseDto` ohne `ack`/`revision`, Status `accepted|duplicate`) als der Host (`changes?cursor=`, `SyncPushResponse` mit `ack`/`revision`, `accepted|already_processed`). **Fix `f5392fc`:** Clients aus dem aktuellen `openapi.json` regeneriert; frische Generierung erzeugt keinen Diff (verifiziert 18.11.). Regression mit echtem Host + generiertem Client = Batch-C-E2E-Smoke (offen, in PR gelistet).
  2. **P1 Quittungsbindung:** Quittung war an die Outbox-Zeile gebunden, nicht an den gesendeten Inhalt. **Fix `a631ab1` (DB-Ebene):** `prepareSend` friert den Inhalt ein (`sending`), `markConfirmed` nur aus `sending` + nur mit Hash des gesendeten Inhalts (sonst `payload_conflict`, kein Statuswechsel); `sync-outbox.spec.ts` (22 Tests, echte SQLite via `node:sqlite`).
  3. **P2 `confirmed`-Schutz:** `rejected` war aus jedem Status möglich. **Fix `a631ab1`:** `markRejected` nur aus `sending` (CAS + Hash); `confirmed` unveränderbar; Test vorhanden.
  - Alle 3 Threads beantwortet und aufgelöst (18.11.2026).
- **Nachreview (LordStarscream, 18.11.2026, Head `fb78b4c`): „Alle drei bisherigen Befunde sind behoben. Keine neuen Befunde im geprüften Nachgang; der A1/A2-Teilstand von PR #21 ist aus Code-Review-Sicht zum Merge geeignet.“** — Prüfnachweise: Client unter Node mit abgefangenem `request()` (korrektes Envelope, `cursor` statt `after`); Quittung/Inhaltsbindung mit Original-TS + echter `node:sqlite` (update/discard während `sending` gesperrt, erneutes `prepareSend`/neue Instanz behalten exakt Payload/Hash/Operation-ID, falscher ACK-Hash und fremde Booking-ID verändern nichts); Status/Revision (confirmed bleibt bei verspäteter Ablehnung confirmed, zweites ACK mit abweichender Revision ändert weder Buchungs- noch Outboxrevision, Trigger-abgebrochenes zweites ACK → vollständiger Rollback, Status bleibt `sending`). Prüfgrenzen: .NET-Suite/Android nicht erneut ausgeführt; Freigabe gilt nur für den A1/A2-Teilstand — SyncService/Setup/UI/Integration + Geräte-Abnahmen bleiben Batches B/C. PR-Kommentar 20:01 (id 6990007072) fasst den Stand zusammen.
- **PR #21 wartet auf Nutzer-Merge** (Reviewer-Empfehlung: A1/A2 mergebar; Merge nur auf Nutzerfreigabe).


## Übernahme von pi — 19. November 2026

Rest von Batch B und Desktop-Geräteverwaltung ergänzt. Manueller Sync im Eingaben-Tab,
gekoppelte Gerätetokens statt Sitzungstoken für Bootstrap/Push/Pull, konsistentes
Snapshot-Replace samt Cursor/Generationen in einer lokalen SQLite-Transaktion.
Setup, Health und Sync teilen eine Sperre für die globale OpenAPI-Konfiguration.

Verbindliche Präzisierung zu D2/D5/D6/D7: HTTP 400 wird als korrigierbare Ablehnung
angezeigt. HTTP 409 bleibt als gesperrter Konflikt in `sending`, weil möglicherweise
bereits eine Hostbuchung existiert; Verwerfen würde den lokalen Nachweis verlieren.
Netzwerk-/Antwortverlust bleibt ebenfalls `sending` und wird mit identischer ID und
identischem Inhalt wiederholt. Ein Datenbank-/Protokollgenerationswechsel stoppt vor
Push; eine automatische Neubasierung könnte Restoreverluste verdecken. Die spätere
kontrollierte Geräteabstimmung bleibt Schritt 17. Host- und Generationsbindung bleibt
beim Abmelden erhalten. Neukopplung beim gleichen Host/Generation kann unbestätigte
Sendungen unverändert fortsetzen (zweite Idempotenzstufe über stabile Buchungs-ID).
Abmelden mit unbestätigter Sendung bleibt gesperrt.

Batch-C-Smoke und Prüfgrenzen: `docs/step-9/integration-smoke.md`.
Geräteabnahmen sind weiter offen; Schritt 9 ist noch nicht vollständig abgenommen.
Review-Schwerpunkte: `sync.service.ts` (Quittung/Cursor/Konflikte), Setup-Persistenz
und Herkunftsbindung, `devices.ts` (Widerruf), echter Host-Smoke.


## Übergabe an pi — aktueller Arbeitsstand vom 19. November 2026

**Nutzerauftrag:** Codex-Arbeit jetzt an pi zurückgeben. Keine Folgeetappe beginnen.
**Wichtig: Die neue Implementierung liegt ausschließlich uncommittet in diesem Worktree:**
`/home/mario/Projects/Lario/.worktrees/step9-sync`
Branch: `step/9-sync-integration`, Basis `fa91eb7` (bisheriger Schritt-9-Branch).
Der bisherige pi-Worktree `/home/mario/Projects/Lario-step9-wt` enthält die neuen
Codeänderungen NICHT. Zum Fortsetzen in das oben genannte Arbeitsverzeichnis wechseln.
`main` bleibt auf `85e5925`. Keine Änderungen committet, gepusht oder gemerged;
PR #21 enthält noch den bisherigen Stand. Es laufen keine beauftragten Implementierungsagenten mehr.

### Implementiert, noch nicht veröffentlicht

- Desktop: `/geraete` samt Navigation, Liste, Bestätigung des Widerrufs,
  Doppelklickschutz, Fehleranzeige; bestehende generierte Sync-Endpunkte genutzt.
- Mobile: `sync/sync.service.ts` + `sync-connection-lock.ts` — manueller Bootstrap,
  Push, paginierter Pull, atomarer Snapshot/Cursor/Generationen, sichere ACK-Prüfung,
  Fortschritt/Ergebnis; `sending` bleibt bei Antwortverlust identisch wiederholbar.
- Einrichtung: nach Login Pair + Bootstrap mit Gerätetoken; Snapshot/Auth/Sync-Meta
  in einer lokalen Transaktion; Fehler räumt neue Kopplung best effort auf.
  Setup/Health/Sync teilen eine Sperre gegen globale OpenAPI-Rennen.
- Herkunftsschutz: `sync.host` und Generationen bleiben beim Abmelden erhalten;
  Hostwechsel mit lokalen Eingaben und Generationswechsel stoppen.
  Gleicher Host/Generation erlaubt Neukopplung mit unveränderter offener Sendung;
  Host dedupliziert zusätzlich über stabile Buchungs-ID. Abmelden bei `sending` gesperrt.
- Eingaben-Liste/Detail: manueller Sync, Status/Grund, `rejected` korrigierbar,
  `sending`/`confirmed` gegen Ändern und Verwerfen gesperrt.
- Sicherheitskorrektur aus unabhängigem Review: HTTP 409 bleibt gesperrtes `sending`
  und stoppt den Lauf mit Konflikthinweis; nur HTTP 400 wird bearbeitbares `rejected`.
  Das präzisiert den bisherigen Task-Entwurf, der 400/409 gleich behandelte.
- Neue Tests: `devices/devices.spec.ts`, `setup/setup-sync.spec.ts`,
  `sync/sync.service.spec.ts` (echte SQLite), optionale `sync/sync-host.integration.spec.ts`.
- Neuer Smoke-Befehl: `bash scripts/test-step9-sync.sh`.
  Doku: `docs/step-9/sync-contract.md`, `docs/step-9/integration-smoke.md`, README,
  Implementierungsplan; `BATCH_A_CONTRACT.md` als überholter Entwurf gekennzeichnet.

### Tatsächlich bestandene Prüfungen

Alle folgenden Befehle liefen im neuen Worktree (Frontendbefehle unter `frontend/`):

- `LANG=de_DE.UTF-8 LC_ALL=de_DE.UTF-8 /home/mario/.dotnet/dotnet test Lario.slnx -m:1 --no-restore`
  → **573 bestanden** (API 159, Domain 214, Infrastructure 200).
  Die frühere Doku nannte 574; diese Zahl wurde nicht übernommen. Keine .NET-Tests verändert.
- `npm test -- --watch=false` → **135 Host + 313 Mobile bestanden**, ein optionaler
  Host-Integrationstest im Standardlauf bewusst übersprungen.
- `npm run lint` → Host und Mobile grün.
- `npm run build` → beide Produktionsbuilds grün; nach letzter 409-Korrektur
  zusätzlich `npm run build:mobile` erneut grün.
- `DOTNET_ROOT=/home/mario/.dotnet bash scripts/test-step9-sync.sh`
  → echter Hostbuild + **ein Integrationstest bestanden**: 20 Offline-Ausgaben,
  echte mobile/Host-SQLite, Host-/Service-Neustart, Antwort nach Servercommit verloren,
  Widerruf + Neukopplung + sichere Wiederholung, zwei weitere Wiederholungen,
  exakt 20 Ausgaben, Salden 83000 Cent gesamt / Flex 57000 / Freizeit 26000;
  erneuter Widerruf stoppt Sync, lokale Speicherung bleibt möglich.
- `git diff --check` → grün vor Übergabe; nochmals nach Übergabedoku prüfen.

Umgebungsdetails: System-`dotnet` findet ASP.NET-Runtime nicht, daher obiger
`/home/mario/.dotnet/dotnet`-Befehl. Ohne de_DE scheitert ein bestehender API-Test
an erwartetem Dezimalkomma. NuGet-Restore brauchte Netzwerkfreigabe; VSTest und
Smoke brauchen Loopback außerhalb der Socket-Sandbox. `npm ci --offline` scheiterte
am leeren temporären Cache; identische vorhandene `node_modules` aus dem bisherigen
Step-9-Worktree wurden ins neue Arbeitsverzeichnis kopiert. Keine Abhängigkeiten geändert.

### Review und nächste konkrete Schritte

Unabhängige Agentreviews für Desktop, Sync, Setup und finalen Delta erfolgt;
P1 zur lokalen Löschbarkeit nach 409 behoben und regressionsgetestet. Final kein
neuer funktionaler Befund. Einige Kommentare alter Setup-Tests nennen noch frühere
Callcounts/Statementpositionen; Assertions sind erhalten, nur Mocks/Anzahl ergänzt.

1. Im **neuen Worktree** `git status --short --branch`, `git diff` und neue Dateien lesen
   (untracked Dateien erscheinen nicht in `git diff --stat`!).
2. Reviewfähigen Gesamtstand prüfen, bei weiteren Änderungen passende Tests wiederholen.
3. Änderungen auf den bisherigen PR-Branch `step/9-sync-neue-ausgaben` bringen,
   auftragsbezogen committen/pushen und PR #21 Beschreibung aktualisieren.
   Branch im alten Worktree nicht unbedacht wechseln/überschreiben. Kein Merge ohne Freigabe.
4. Geräteabnahmen auf echtem Android bleiben offen: dauerhaftes Offline-Speichern,
   Appkill/Neustart, LAN/HTTPS/Zertifikatsvertrauen und Sync. Smoke rendert das
   Setup-Formular nicht; es nutzt die originale HostClient-Netzwerklogik und SyncService.
5. Native Keystore-Anbindung ist weiterhin nicht umgesetzt; bestehende lokale
   Zugangsdatenablage aus Schritt 8 bleibt bestehen (in Vertragsdoku benannt).
   Daher keine vollständige Sicherheits-/Geräteabnahme behaupten.

### Review-Befund P1 — Herkunfts-Schreibschutz (LordStarscream, 19.11.2026, Kommentar auf `3f1a64a`)

**Befund:** Herkunfts- und Schreibschutz sind unzureichend. Gerät ist mit A verbunden und
hat lokale Eingaben. Hostwechsel auf B startet; Setup prüft vor Pairing, dass keine lokalen
Eingaben existieren. Während Setup-Netzwerk läuft, wechselt der Nutzer zur Offline-Erfassung,
legt eine Ausgabe an und speichert. Setup meldet den Hostwechsel ab und stellt A wieder her,
aber die Anlage läuft gegen den A-Kontext und kann danach mit der B-Bindung synchronisiert
werden. Die Herkunft einer Eingabe muss während eines verbindungsändernden Setup-Vorgangs
eindeutig bleiben.

**Behoben (19.11.2026):**
- Neue `OriginWriteLock` (`frontend/apps/mobile/src/app/sync/origin-write-lock.ts`):
  serialisiert die gesamte lokale Anlage (`BookingsRepository.save` —
  Referenzvalidierung gegen Snapshot, Zähler-/Geräte-ID-Lesung, finaler
  Schreib-Batch) mit dem verbindungsändernden Setup-Commit (FIFO-Promise-Kette,
  Muster `LocalDatabase.serialize`). `update`/`discard` bleiben außerhalb: Sie
  können keine der Herkunftsprüfung unsichtbaren neuen Eingaben erzeugen.
- `SetupComponent.saveAndLoad`: Die entscheidende Herkunftsprüfung
  (`listPending` + Host-Wechsel) läuft zusätzlich in der geschützten Sektion
  direkt vor dem atomaren Commit — zwischen Neuprüfung und Commit kann keine
  neue lokale Eingabe entstehen; eine unter dem alten Host laufende Anlage
  läuft zuerst zu Ende und verhindert dann den Commit (Herkunft bleibt A).
  Die Frühprüfung vor dem Pairing bleibt als schnelle Feedback-Stufe (spart
  die B-Kopplung, wenn der Wechsel ohnehin abgelehnt wird).
- Zwei deterministische Regressionstests in `setup-sync.spec.ts` (Pair-Gate bzw.
  Pair-Gate + Write-Gate):
  (a) Buchung nach leerer Herkunftsprüfung, vor B-Commit → Commit blockiert,
  Herkunft A bleibt (Meta, Laufzeit-Konfiguration, B-Kopplung widerrufen);
  (b) unter A laufende Anlage, deren finaler Write auf den B-Commit trifft →
  B wartet auf die Sperre, Anlage landet an A, B-Commit blockiert.

**Nachgeprüfte Prüfungen (19.11.2026, Worktree `.worktrees/step9-sync`):**
- `npx ng test mobile` → **315 bestanden**, 1 übersprungen (Integrationsspec ohne
  `LARIO_SYNC_HOST`), 21 Dateien (vorher 313; +2 neue P1-Tests).
- `npx ng test host` → 135 bestanden, 15 Dateien (unverändert, keine Host-Änderung).
- `npx ng lint mobile` → 0 Befunde.
- `DOTNET_ROOT=/home/mario/.dotnet bash scripts/test-step9-sync.sh` →
  Integrationstest gegen echten Host bestanden (1/1).

**Schritt 9 ist wegen offener Pflichtabnahmen nicht vollständig abgenommen.**

### Review-Befund P1 2 — Herkunftskonflikt bei wartender Anlage (LordStarscream, 19.11.2026, Kommentar auf `8d4104c`)

**Befund:** Das Setup blockiert die Anlage (via `OriginWriteLock`), aber wenn die
Anlage auf die Sperre wartet und der Setup-Commit (Host B) zuerst durchläuft,
wird die A-Anlage nach dem Commit gegen Snapshot B validiert und bei
identischen Referenz-IDs (z. B. geklonte Datenbestände) still unter der neuen
Herkunft gespeichert. `OriginWriteLock` allein reicht nicht — die
Herkunftsprüfung muss den Hostwechsel erkennen und die wartende Anlage ablehnen.

**Behoben (19.11.2026):**
- Neue Herkunfts-Version Meta `sync.origin_version` (`ORIGIN_VERSION_META_KEY`):
  wird ausschließlich vom Setup-Commit erhöht — Hostwechsel → +1 (unter der
  `OriginWriteLock`, in derselben Sektion wie der Commit), Neukopplung am selben
  Host → Version bleibt, erster Setup → 1. Lesen + Erhöhen liegen in derselben
  geschützten Sektion (kein paralleles Setup kann die Version dazwischen ändern).
- `BookingsRepository.save` akzeptiert optional `originVersion` (Herkunft der
  Eingabedaten): wird VOR `saveChain`/Sperre erfasst (ohne Angabe: aktuelle
  Version am Aufruf) und in `doSave` unter der `OriginWriteLock` gegen die
  aktuelle Version nachgerechnet. Abweichung → neue `OriginConflictError` (nichts
  wird gespeichert). Der Recheck läuft durch `currentOriginVersion()` über
  `withSerializedRead` (Initialisierung sicher, Plugin-Fehler wie im übrigen
  Anlagepfad).
- `EntryComponent`: erfasst die Version direkt nach dem Snapshot-Load
  (`formOriginVersion`) und übergibt sie bei `save` — die Referenzdaten des
  Formulars (Snapshot) sind die Validierungsbasis; `OriginConflictError` wird wie
  `BookingValidationError` mit der Nachricht angezeigt (Formular bleibt erhalten).
- `update`/`discard` bleiben unberührt (erzeugen keine neuen Eingaben —
  Designentscheidung des P1-Nachgangs, unverändert).
- Deterministischer Regressionstest in `setup-sync.spec.ts` (ListPending-Gate am
  geschützten Read + Capture-Gate an `currentOriginVersion`): Anlage startet
  unter A, Setup hält die `OriginWriteLock` beim geschützten Outbox-Read, Anlage
  erfasst Version 1 hinter dem Commit, Commit erhöht auf 2 → Anlage wird mit
  `OriginConflictError` abgelehnt; lokale Buchung bleibt leer, Host B mit
  Device-Token 2 und Version 2 wird committet.

**Nachgeprüfte Prüfungen (19.11.2026, Worktree `.worktrees/step9-sync`):**
- `npx ng test mobile` → **316 bestanden**, 1 übersprungen (Integrationsspec ohne
  `LARIO_SYNC_HOST`), 21 Dateien (vorher 315; +1 neuer Test).
- `npx ng test host` → 135 bestanden, 15 Dateien (unverändert, keine Host-Änderung).
- `npx ng lint mobile` → 0 Befunde.
- `npx ng build mobile` → grün (Produktionsbuild mit den UI-Änderungen).
- `DOTNET_ROOT=/home/mario/.dotnet bash scripts/test-step9-sync.sh` →
  Integrationstest gegen echten Host bestanden (1/1, unverändert — keine
  Host-/Sync-Änderung durch diesen Befund).

**Schritt 9 ist wegen offener Pflichtabnahmen (Android-Gerät) weiterhin nicht
vollständig abgenommen.**

### Vollständige Nachverifizierung aller Review-Befunde (22.11.2026)

Auf Aufforderung („Kommentare im PR weiterbearbeiten, bis es passt“) wurden
alle fünf Review-Befunde von PR #21 am aktuellen Head `ccd51bc` gegen den
Code und mit den vollständigen Prüfungen nachverifiziert.

**Befund-Abgleich im Code (Worktree `.worktrees/step9-sync`):**
1. **P1 Wire-Vertrag (f5392fc):** Generierte Clients (host + mobile) und
   `frontend/openapi.json` beschreiben exakt den Host-Vertrag —
   `syncGetChanges` bindet `[FromQuery] cursor`/`limit`
   (`src/Lario.Host/Api/Sync/SyncEndpoints.cs`), `SyncPushResponse` trägt
   `status`/`bookingId`/`protocolGeneration`/`revision`/`ack`.
   Neu-Generierung beider Clients aus dem eingecheckten `openapi.json`
   (`npx openapi --client fetch --useUnionTypes`) → **kein Diff**.
2. **P1 Quittung an gesendeten Inhalt (a631ab1):** `prepareSend` friert
   `queued → sending` (CAS `WHERE status='queued'`); `markConfirmed`/
   `markRejected` nur aus `sending` mit CAS auf Status **und**
   `payload_hash` (`UPDATE_OUTBOX_CONFIRMED`/`UPDATE_OUTBOX_REJECTED`);
   `update()`/`discard()` verweigern `sending`/`confirmed` via
   `loadEditable`; Wiederholung sendet denselben eingefrorenen Inhalt
   (`listSyncable` = queued + sending).
3. **P2 `confirmed` als Endzustand (a631ab1):** Doppelte/späte Quittung ist
   vor jedem Write ein no-op (`status === 'confirmed' → return`) — die
   Buchungs-`base_revision` wird nicht mehr separat erhöht; `confirmed`
   geht nie zurück auf `rejected`; beide Statements liegen in einer
   `withWriteTransaction`.
4. **P1 Herkunftsprüfung vs. parallele Anlage (8d4104c):**
   `OriginWriteLock` serialisiert den kompletten `save`-Pfad mit dem
   Setup-Commit; geschützte `listPending`-Neuprüfung unter derselben Sperre
   direkt vor dem atomaren `executeSet`-Commit; B-Kopplung wird bei
   Blockade best-effort widerrufen.
5. **P1 wartende Anlage nach Hostwechsel (2eafab9):**
   `sync.origin_version` wird nur vom Setup-Commit erhöht (Hostwechsel +1,
   Neukopplung gleich, erstes Setup 1); `BookingsRepository.save` erfasst
   die Version **vor** `saveChain`/Sperre und rechnet sie in `doSave`
   unter der Sperre nach — Abweichung → `OriginConflictError` (nichts
   gespeichert); `EntryComponent` übergibt die beim Formular-Load erfasste
   Version.

**Vollständig ausgeführte Prüfungen (22.11.2026, Worktree `.worktrees/step9-sync`,
Head `ccd51bc`):**
- Neu-Generierung OpenAPI-Clients aus eingechecktem `openapi.json` →
  **kein Diff** (Clients im Gleichgewicht mit der Spec).
- `npx ng test mobile` → **316 bestanden**, 1 übersprungen
  (Integrationsspec ohne `LARIO_SYNC_HOST`), 21 Dateien.
- `npx ng test host` → **135 bestanden**, 15 Dateien.
- `npx ng lint mobile` + `npx ng lint host` → **0 Befunde**.
- `npx ng build mobile` → grün (Produktionsbuild).
- `dotnet build Lario.slnx` → **0 Warnungen, 0 Fehler**.
- `dotnet test` → **573 bestanden**: Domain 214, Infrastructure 200,
  Api 159 (Api-Tests im Solution-Lauf zunächst Launch-Fehler
  „No frameworks were found“, direkt gestartet fehlerfrei —
  Environment-Artefakt, kein Testfehler).
- `DOTNET_ROOT=/home/mario/.dotnet bash scripts/test-step9-sync.sh` →
  **1/1** (generierter Client gegen echten Host: Pair/Bootstrap, 20
  Pushes, Antwortverlust nach Servercommit, idempotente Wiederholung,
  Widerruf/Neukopplung, Pull, Salden).
- Worktree nach allen Prüfungen **sauber** (keine unbeabsichtigten
  Änderungen).

**Fazit:** Alle fünf Review-Befunde sind im Code behoben und durch
deterministische Tests bzw. die E2E-Smoke abgedeckt; sämtliche
automatisierbaren Prüfungen sind am Head grün. Offen bleibt nur die
finale Review-Bestätigung/Nachverifizierung durch den Reviewer und die
Android-Geräteabnahme (Pflichtabnahme, Gerät erforderlich) — Schritt 9
ist danach vollständig abnehmbar. Merge in `main` nur auf Nutzerfreigabe.

### Review-Befund P1 3 — Snapshot und Herkunftsversion gemeinsam lesen (LordStarscream, 22.11.2026, Kommentar auf `2eafab9`)

`loadSnapshot()` und `currentOriginVersion()` liefen in **getrennten**
serialisierten Leseabschnitten. Ein laufender Setup-Commit (Hostwechsel:
Snapshot-Replace + `sync.origin_version`-Erhöhung, eine Sektion) konnte
dazwischen committen — das Formular zeigte dann Stammdaten von A mit der
Version von B (Mischpaar). Bei identischen Referenz-IDs (geklonte
Datenbestände) akzeptierte `save()` die Eingabe still unter der neuen
Herkunft, weil die Formular-Version der neuen Host-Generation entsprach.

**Behoben (22.11.2026, `da505d7`):**
- `SnapshotRepository.loadSnapshotWithOrigin` (neu,
  `SnapshotWithOrigin`): Snapshot UND `sync.origin_version` in **einer**
  `withSerializedRead`-Sektion. Der Setup-Commit steht auf derselben
  Connection-Operations-Kette — FIFO davor (dann Paar B sichtbar) oder
  danach (dann Paar A), nie dazwischen. Mischpaare strukturell
  ausgeschlossen.
- `EntryComponent`: kombiniert geladen; ohne Snapshot bleibt
  `formOriginVersion` `null` (Save gesperrt). Bewusst kein „Version am
  Aufruf“-Fallback — der wäre der fehlende Herkunftsnachweis.
- `sync/origin-version.ts` (neu): `ORIGIN_VERSION_META_KEY` +
  `parseOriginVersion` geteilt; `BookingsRepository` re-exportiert den
  Key (bestehende Importe stabil).

**Red/Grün:**
- **Rot (Zwei-Abschnitte-Implementation, Dateizustand pro Lauf
  verifiziert):** deterministischer Fehlschlag `expected 2 to be 1` —
  Paar A = alter Snapshot + neue Version (13/13 Läufe).
- **Grün (integrierte Lese):** 33 aufeinanderfolgende grüne Läufe
  (11/11); zwei unklare Fehler unmittelbar nach dem Red/Green-Dateiwechsel
  (1× `expected 1 to be 0`, 1× `expected 1 to be 2`) traten nur direkt
  nach dem Dateitausch auf und sind seitdem nicht reproduzierbar —
  danach durch File-State-Verifikation (git hash + diff) abgesichert.
- Testaufbau: Setup A (leer) → Snapshot-Replace auf B (Host `B-…`) +
  `version 2` in `executeSet` → Pair-Gate hält die Bootstrap-Antwort →
  Formularlade startet (Pair-Antwort 1) → `listPending` hält Pair-Antwort 2
  (Setup-Commit hält `OriginWriteLock` beim geschützten Outbox-Read) →
  Paar-A-Assertions → Setup-Commit committet → Paar B. Save mit Paar A
  → `OriginConflictError`, lokal bleibt alles leer.

**Ausgeführte Prüfungen (22.11.2026, Worktree `.worktrees/step9-sync`,
Head `da505d7`):** 11/11 `setup-sync.spec.ts` (stabil), 317 mobile
Tests (20 Dateien, 1 übersprungen), lint host+mobile 0 Befunde,
`build:mobile` grün.

PR-Thread beantwortet und aufgelöst (Reply 22.11.2026 23:11).
