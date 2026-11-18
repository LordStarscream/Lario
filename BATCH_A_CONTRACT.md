# Schritt 9 — Synchronisation neuer Ausgaben

**Status:** aktiv (Branch `step/9-sync-neue-ausgaben` ab main `85e5925`)
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

- **A — Host-Protokoll** (EF: `SyncDevice`, `sync_protocol_state`, `sync_changes`,
  `sync_operations` + Migration; Auth-Middleware-Erweiterung; Endpunkte
  pair/devices/bootstrap/push/changes; ChangeLog-Haken in
  MasterDataRepository; `SyncService`/Mapper in Host; Api-Tests D8-Host;
  Desktop-UI „Verbundene Geräte“; OpenAPI neu generieren).
- **B — Mobile Sync** (SyncService, Sync-Repository-Methoden (Cursor/Generationen
  in Meta, Snapshot-Replace atomar), Outbox-Status `rejected` + Spalte
  `status_reason`, `update`/`discard` auf `rejected`, HostClient-Erweiterung
  (pair/bootstrap/push/changes), Setup-Erweiterung (Pair), Sync-UI +
  Eingaben-UI `rejected`, mobile Tests D8-Mobile).
- **C — Integration/Abnahme** (E2E-Smoke 20-Eingaben, Abnahmedoku
  `docs/step-9/`, Index-Update, Geräte-Abnahmen als offene Blocker dokumentiert).

## Offene Abnahmen (Gerät)

- Echte Android-Geräte-Abnahmen: LAN-Sync, HTTPS-Login/Kopplung,
  Offline-Neustart mit offener Outbox + Sync nach Neustart — **Blocker: kein
  Gerät verfügbar** (wie Schritt 8).

## Übergabe/Review

- PR `step/9-sync-neue-ausgaben` → `main`; Review-Worktree nach Bedarf.
- Kein Merge ohne explizite Nutzerfreigabe.
