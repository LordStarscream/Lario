# Design-Paket Schritt 7a — PDF-/Bildnachweise
_Stand: 21. Okt. 2026, überarbeitet nach Designreview PR #12 (4 Punkte: 1×P1, 3×P2 — alle in D1/D1b/D2/D2b/D3/D5/D7/D9/D9b/Batch umgesetzt) · Basis: Code-Analyse-Bericht (38 Reads) + Plan §5 Schritt 7a_

## Umfassung aus dem Plan (verbindlich)

- Eigene Attachment-Metadaten + lokaler Dateispeicher auf dem Host
- PDF, JPEG, PNG über die Hauptoberfläche hochladen, anzeigen/herunterladen, einer Ausgabe zuordnen
- Optional einzelne Splits verknüpfen; ein Beleg kann die gesamte Split-Ausgabe nachweisen
- Originaldatei erhalten; Dokumente nachträglich anhängbar; fehlender Beleg blockiert keine Buchung
- Datei-ID, Name, Typ, Größe, Prüfsumme, Buchungsbezug speichern
- Dateien außerhalb der öffentlich ausgelieferten Oberfläche, nur über authentifizierte Endpunkte
- Uploadgrenzen festlegen, Dateityp prüfen, Dateipfade serverseitig erzeugen
- Erst nach erfolgreicher dauerhafter Ablage einen verwendbaren Anhang bestätigen
- Uploadfehler verändern keine Finanzbuchung; unvollständige Ablagen kontrolliert bereinigt
- Entfernen einer Zuordnung verändert keinen Geldbetrag; gleiche Dateien verlieren keine anderen Zuordnungen
- Prüfen: PDF+Bild anhängen/Neustart/öffnen; mehrere Dateien je Rechnung; Beleg mit mehreren Splits;
  Uploadfehler ohne Waise; ungültiger Typ/zu groß/unberechtigt abgewiesen; Entfernen → Konten/Pools unverändert

## Aufbaufakten (aus Analyse)

- Auth: `ApiAuth.UseApiAuth` prüft Pfad `/api/v1` (case-insensitiv, Segment-Präfix) außer `/auth/login`,
  Bearer via `AuthTokenStore` — neue `/api/v1/attachments…`-Routen sind **automatisch geschützt**.
- Datenpfade: `LarioDataPaths.Root` (XDG, `EnsureCreated` idempotent); DB `Root/lario.sqlite`;
  credentials.json liegt nebenbei im Root → Muster für ein Unterverzeichnis `Root/attachments`.
- Endpunkt-Muster: statische `MapXxx(IEndpointRouteBuilder)`-Extensions in `src/Lario.Host/Api/…`;
  `Produces<T>(201/200).Produces<ErrorResponse>(400/409)`; Handler `static async Task<IResult>`;
  DTO-Records neben den Endpunkten; Fehlerformat `ApiErrorBody` (Codes `validation_error`,
  `invalid_reference`, `domain_conflict`, `internal_error`).
- DbContext: Domain-Typen direkt gemappt, 16 DbSets; partielle Unique-Indizes mit `HasFilter` vorhanden
  (Muster `UX_Bookings_OpeningBalanceAccountId`); Concurrency-Tokens wo relevant.
  Migrations in `src/Lario.Api…/Infrastructure/Migrations/` (9 vorhanden), Anwendung `Program.cs` Z. 84.
- Test-Factory: `LarioApiFactory : WebApplicationFactory<Program>`, pro Instanz Temp-`Lario:DataDirectory`
  + Test-Credentials; `Program` ist `public partial`. Write-Tests: Login → Bearer → `PostAsJsonAsync`.
- Frontend: Zoneless, Signals, Dialoge als In-Page-Overlays in `list/bookings-list.*`
  (Muster Storno/Erstattung); generierter Client in `apps/host/src/generated/` (fetch,
  `OpenAPI.TOKEN` aus `localStorage['lario.apiToken']`), Regeneration `npm run generate:client`
  (benötigt laufenden Host); `ApiErrorHandler` → zentral 401-Redirect.
- Größengrenzen: repo-weit keine Multipart-/FormOptions-Konfiguration vorhanden — Defaults gelten.

## Design-Entscheidungen

### D1 — Datenmodell: eine Tabelle `Attachment`, Buchung ist immer Teil der Zuordnung

- Neue Domain-Entität `Attachment` (`src/Lario.Domain/Attachments/Attachment.cs`), keine
  separate Link-Tabelle:
  - `Id` (Guid, string, serverseitig erzeugt)
  - `OriginalFilename` (Client-Name, nur Anzeige; Dateipfad wird nie daraus abgeleitet)
  - `ContentType` (`application/pdf`, `image/jpeg`, `image/png`)
  - `SizeBytes` (long), `Sha256` (hex, 64 Zeichen)
  - `StoredName` (serverseitig erzeugt: `{Id}{.pdf|.jpg|.png}`)
  - `BookingId` (FK → Bookings, **required**)
  - `SplitId` (**nullable**, **Komposit-FK `(BookingId, SplitId)` → `ExpenseSplits`** —
    Teilposten-Id ist nur innerhalb der Buchung eindeutig; ein isolierter SplitId-FK
    scheitert in SQLite mit *foreign key mismatch*. Muster: `SplitTaxMarkings` nutzt
    denselben Komposit-Schlüssel `(SplitId, BookingId)`)
  - `OriginalSplitId` (plain String, **keine** FK; nur bei Umlageung durch Korrektur
    gesetzt — D1b)
  - `CreatedAtUtc`
- Begründung: Plan verlangt „Datei-ID, Name, Typ, Größe, Prüfsumme **und Buchungsbezug** speichern“
  — der Buchungsbezug ist Attribut der Datei. „Gleiche Dateien verlieren keine anderen Zuordnungen“
  ist ohne Content-Dedup trivial erfüllt (jede Upload-Kopie hat exakt eine Zuordnung;
  das Löschen einer Zeile + ihres eigenen Files kann andere Zuordnungen nicht berühren).
  Eine Link-Tabelle + Content-Addressing wäre für den Grundumfang eine vorgezogene Verallgemeinerung.
  Dedup/Geteilte Belege sind bei Bedarf später erweiterbar (Sha256 ist bereits gespeichert).
- `SplitId` ist nicht null → die Zeile belegt exakt diesen Split (Summe mehrerer Split-Belege pro
  Ausgabe ist möglich); null → der Beleg nachweist die gesamte Ausgabe (einschließlich aller Splits).
  Die Zugehörigkeit wird damit atomar in SQLite erzwungen (Komposit-FK-Prüfung beim Insert).

### D1b — Korrektur-Lebenszyklus: Erhaltungsregel für belegte Splits (Review P1)

`BookingRepository.ReplaceSplits` löscht bei Korrekturen entfallene Split-Zeilen. Ohne
Regel würde der Attachment-FK die Korrektur blockieren (Restrict) oder Metadaten
mitlöschen und die Datei zurücklassen (Cascade). **Festgelegte Regel:**

- FK `Attachment → ExpenseSplits` mit `OnDelete(Restrict)` (Defensiv: unbeabsichtigtes
  Split-Löschen ohne Umlageung schlägt laut fehl, keine stille Cascade).
- `ReplaceSplits` wird in derselben DB-Transaktion erweitert: **bevor** eine Split-Zeile
  gelöscht wird, werden deren Attachment-Zeilen umgehängt — `SplitId = null`,
  `OriginalSplitId = <alte Split-Id>` (Nachvollziehbarkeit), `BookingId` und Datei bleiben
  unverändert. Der Beleg gilt danach für die gesamte Ausgabe — fachlich korrekt, denn die
  Ausgabe selbst existiert weiter (nur der Split entfällt).
- Keine Berührung von Konten/Pools/Salden: ausschließlich die Attachment-Zeilen ändern sich;
  Salden ändern sich ausschließlich gemäß der Korrektur selbst.
- In-Place aktualisierte Splits (gleiche Id, geänderter Betrag/Pool/Kategorie) bleiben als
  Belegziel unverändert gültig — keine Aktion nötig.
- Integration in den Korrekturpfad (`BookingRepository.ReplaceSplits`), kein unabhängiger
  Attachment-CRUD.
- Kein Concurrency-Token: Anhänge sind keine Finanzbuchung, Revision/Outbox/Sync berühren sie
  (mobile Belege = spätere Erweiterung, eigenes Modell laut Plan §8).

### D2 — Ablage: `Root/attachments/`, serverseitige Namen, nur über API

- Verzeichnis `LarioDataPaths.AttachmentDirectory` = `{Root}/attachments`, erzeugt beim Start
  neben `EnsureCreated()` (idempotent).
- Dateiname ausschließlich serverseitig: `{Guid}{Endung}` — Client-Name wird nie zum Pfad
  (keine Path-Traversal, keine Kollisionen, keine Namensabhängigkeit).
- Keine statische Auslieferung: Dateien liegen außerhalb von `wwwroot`; der einzige Zugriff ist
  `GET /api/v1/attachments/{id}/download` (Auth wie alle `/api/v1`).
- Endung kommt aus dem **validierten** ContentType (Tabelle D3), nicht aus dem Dateinamen.
- **Temporärer Name während des Uploads**: `{Guid}{Endung}.part` — erst nach vollständiger
  Validierung (Typ/Größe) und FSync Rename auf den finalen Namen (auf demselben
  Dateisystem atomar); danach erst der DB-Commit (D3).

### D2b — Bereinigung nach Abbruch (Review P2)

Catch-Only-Cleanup deckt Prozessabbrüche nicht ab. Ergänzung:

- **Start-Abgleich** (in `Program.cs` nach `Migrate()`, **vor** Annahme neuer Requests —
  der lokale Host ist Single-Instance, daher keine parallelen Uploads während des
  Abgleichs):
  1. `*.part`-Dateien im Attachment-Verzeichnis → löschen (unterbrochene Uploads).
  2. Finale Dateien `{guid}{ext}` **ohne** DB-Zeile (Namensschema: GUID-Präfix-Regex, nur
     dieses Verzeichnis) → löschen (Abbruch zwischen Rename und DB-Commit).
  3. Referenzierte, aber fehlende Dateien → **nicht** löschen, weiterhin 410 melden (D4).
- **Laufend**: `finally`-Cleanup des `.part`-Namens bei jeder Upload-Exception.
- **Delete-Order** (D5): DB-Zeile zuerst committen, danach Datei löschen. Fehlgeschlagene
  Löschung bleibt als Waise und wird beim nächsten Start-Abgleich entfernt.
- Abgleich ist strikt auf das eigene Verzeichnis + Namensschema begrenzt (fremde Dateien
  im Verzeichnis bleiben unberührt).

### D3 — Upload-Vertrag: `POST /api/v1/attachments` (multipart)

- Felder: `file` (einzige Datei), `bookingId`, optional `splitId`.
- Prüfung in fester Reihenfolge:
  1. **Dateigröße** während des Streamings: der Part-Inhaltszähler läuft gegen
     `Lario:Attachments:MaxBytes` (Default **10 000 000** Byte, konfigurierbar — zählt die
     **eigentlichen Dateibyte**, nicht den Multipart-Body; Review P2, siehe D7) →
     `413` `validation_error` `code=attachment_too_large`.
  2. **Typ**: multipart `Content-Type` der Part **und** Magic-Bytes (PDF `%PDF-`, JPEG `FF D8 FF`,
     PNG `89 50 4E 47 0D 0A 1A 0A`) müssen übereinstimmen → sonst `400 validation_error`
     `code=unsupported_attachment_type`.
  3. **Buchungsbezug**: `bookingId` existiert (sonst `400 invalid_reference`); `splitId`
     existiert **und gehört zur Buchung** (Komposit-FK, D1 — gleiche SplitId in anderer
     Buchung wird abgewiesen) (sonst `400 invalid_reference`).
- Reihenfolge der Ablage (Review P2 — Abbruchfest):
  1. Datei streamen nach `Root/attachments/{id[BUDGET-CAP]ext}.part` (FSync vor Close — dauerhafte
     Ablage), SHA-256 dabei berechnen, Größe exakt zählen.
  2. Typ- und Größenprüfung erfolgreich → Rename auf `{id[BUDGET-CAP]ext}` (atomar).
  3. DB-Eintrag in `Bookings`-unabhängiger Transaktion anlegen (FK-Prüfung durch SQLite,
     inkl. Komposit-Split-FK).
  4. **Jeder Fehler → `finally`-Cleanup** der `.part`- bzw. (nach Rename) finalen Datei ohne
     DB-Zeile; bei Prozessabbruch übernimmt der Start-Abgleich (D2b).
  5. Antwort `201` erst danach, mit vollständigen Metadaten (Id, Name, Typ, Größe, Prüfsumme,
     Buchungsbezug, Split).
- **Konkurrenz Upload vs. Split-Entfernung** (Review P1-Test): SQLite prüft den FK beim
  Insert — Korrektur entfernt den Split vor dem Insert → sauberes 400 invalid_reference
  ohne Orphan-Datei; Korrektur kommt nach dem Insert → Umlageung via D1b. Beide
  Endzustände konsistent (Tests f–h).
- Uploadfehler verändern **keine** Finanzbuchung: kein Repository der Buchungen wird angerührt,
  keine Revision, keine Operation. Wiederholter Upload nach verlorener Antwort erzeugt einen
  zweiten Eintrag (bewusst, D6).
- Buchungs-Typ-RESTRIKTION: bewusst **keine** — die API akzeptiert jede existierende Buchung
  (Ausgabe, Einnahme, Transfer, …). Plan-Scope „einer Ausgabe zuordnen“ ist der Hauptfall;
  die UI bietet die Aktion an Ausgabe-Zeilen an. Begründung: keine stillen fachlichen Sperren
  außerhalb des Plans, Erweiterungsfreiheit für Schritt 16 (Steueransicht: fehlende Nachweise).

### D4 — Lesen: Metadaten + Download

- `GET /api/v1/bookings/{bookingId}/attachments` → Liste der Metadaten (für die Buchungsliste).
- `GET /api/v1/attachments/{id}` → Metadaten-Objekt (für Detailanzeige).
- `GET /api/v1/attachments/{id}/download` → Originaldatei:
  - `Content-Type` aus der Metadatenzeile, `Content-Length`,
  - `Content-Disposition: inline; filename="…"`: PDF/Bilder inline (Anzeige im Browser),
    `filename` = Originalname (RFC 5987 `filename*=UTF-8''…` für Umlaute) — Download mit
    erkennbarem Namen, Anzeige inline.
  - Datei fehlt auf dem Datenträger (manuell entfernt) → `410 gone` + Hinweis; Metadaten bleiben
    sichtbar (keine stille Löschung).
- Anzeige in der UI: Dialog mit Vorschaubild (`<img src=download>` — **kein** Auth-Header bei
  `<img>`-Requests, daher: `fetch` mit Bearer → `URL.createObjectURL(blob)` → `img.src`/`<embed>`;
  PDF im Dialog als „Öffnen“ (neuer Tab via Blob-URL) bzw. Download-Button.

### D5 — Löschen: `DELETE /api/v1/attachments/{id}`

- **Festgelegte Reihenfolge** (Review P2): DB-Zeile **zuerst** committen, danach Datei
  löschen (existiert nicht mehr → Erfolg). Fehlgeschlagene Datei-Löschung bleibt als Waise
  und wird beim nächsten Start-Abgleich (D2b) entfernt. Umgekehrte Reihenfolge würde einen
  permanenten 410-Zustand erzeugen.
- Keine Berührung der Buchung (Konten/Pools unverändert — strukturell, es gibt keine
  Finanzwirkung).
- `404` bei unbekannter Id. `204` bei Erfolg.

### D6 — Idempotenz bewusst ohne Operations-ID

- Anhänge sind keine Finanzbuchung: keine Operation-ID, kein Idempotenz-Nachweis, keine
  Basisrevision (Plan: diese Mechanik gilt für Buchungsoperationen). Wiederholung nach verlorener
  Antwort → doppelte Zeile + Datei; Nutzer sieht beide Einträge und kann löschen. Bewusst
  einfacher als bei Buchungen; dokumentiert, nicht still.

### D7 — Grenzen: Dateibyte und Request-Größe getrennt (Review P2)

`MaxBytes` begrenzt **ausschließlich die eigentlichen Dateibyte** (Part-Inhalt). Der
Multipart-Body enthält zusätzlich Boundary, Part-Header, Dateinamen und `bookingId`/`splitId`
(ein 10.000.000-Byte-File erzeugt bereits ≈10.000.198 Byte Body) — ein gemeinsames Limit
würde zulässige Dateien exakt am Limit schon vor dem Dateizähler abweisen. Drei abgestimmte
Werte, **alle aus `MaxBytes` abgeleitet** (Konfigurationsvalidierung: äußere Limits können
nie kleiner sein als die abgeleiteten — ein konfiguriertes Dateilimit scheitert nicht an
einem kleineren äußeren Standardlimit):

- `Lario:Attachments:MaxBytes` (Default 10 000 000 ≈ 9,5 MB) — Dateibyte-Limit, geprüft vom
  Streaming-Zähler im Endpunkt → `413`.
- `FormOptions.MultipartBodyLengthLimit` = `MaxBytes + 1 000 000` (Overhead: Boundary,
  Part-Header, Unicode-Dateiname, beide IDs, moderate Feldüberschreitung) → `413`.
- Kestrel `options.Limits.MaxRequestBodySize` = `MaxBytes + 1 000 000` (gesamter Request;
  korrekt `options.Limits.…`, nicht `LimitRequests`) → `413`.
- `413`-Antworten nutzen das Standard-Fehlerformat (`validation_error`,
  `code=attachment_too_large`) und werden in der UI verständlich angezeigt (D8).
- In `README.md` Config-Tabelle nachtragen (`MaxBytes` einziger konfigurierbarer Wert,
  äußere Limits abgeleitet).

### D8 — UI: „Belege“-Dialog in der Buchungsliste

- Neue Zeilen-Aktion **„Belege“** an Ausgabe-Zeilen (Muster Storno-/Erstattungs-Dialog,
  In-Page-Overlay, Signals-State) in `list/bookings-list.*`:
  - Liste der Belege der Buchung (Name, Typ, Größe, Datum, Split-Zuordnung oder „gesamte Ausgabe“).
  - Upload: Dateiauswahl (accept `application/pdf,image/jpeg,image/png`), optional Split wählen
    (nur wenn die Ausgabe mehrere Splits hat; Default „gesamte Ausgabe“), hochladen →
    Liste aktualisiert. Client-Vorvalidierung (Typ/Große) + Serverfehler verständlich anzeigen
    (bestehender `ApiErrorHandler`-Stil).
  - Anzeige: Bild-Vorschau via Blob-URL; PDF: „Öffnen“ (Blob-URL, neuer Tab) + „Herunterladen“.
  - Entfernen mit Bestätigungsfrage.
- Keine neue Route: der Dialog lebt in der Liste (konsistent mit Storno/Erstattung).
- Mobile-App: **nicht** betroffen (mobile Belege sind spätere Erweiterung, Plan §8).

### D9 — Tests (echte SQLite + Temp-DataDirectory via `LarioApiFactory`)

- **Upload/Grundfall**: PDF + JPEG + PNG an eine Ausgabe; 201; Metadaten vollständig
  (inkl. korrektem SHA-256, serverseitigem Namen); Datei liegt unter `attachments/`.
- **Neustart**: Host neu (neue Factory-Instanz mit **gleichem** DataDirectory) → Metadaten +
  Datei vorhanden, Download identisch (SHA-Prüfung).
- **Mehrere Dateien** an derselben Rechnung; **Beleg auf Split** + **Beleg auf Gesamt-Ausgabe**
  derselben Split-Ausgabe; `GET /bookings/{id}/attachments` listet korrekt.
- **Split-Validierung**: `splitId` unbekannt bzw. anderer Buchung → 400 invalid_reference.
- **Ablehnung**: falscher Typ (z. B. HTML/ZIP mit PDF-Spoof: Magic-Bytes-Mismatch) → 400;
  zu große Datei (kleines Testlimit via Setting) → 413/400; unberechtigter Abruf (kein/
  falscher Token) → 401.
- **Uploadfehler hinterlässt keine Waise** (Review P2 — Repository-Fake allein reicht
  **nicht**; Test-Doubles für Repository **und** FileStore + echte Dateien):
  (a) ungültige `bookingId` → keine `.part`-Datei, keine DB-Zeile;
  (b) DB-Commit nach erfolgreichem Rename schlägt fehl (Repository-Fake) → finale Datei
      wird gelöscht, Verzeichnis bleibt leer;
  (c) **simulierter Abbruch vor Rename**: `.part`-Datei vorhanden → Start-Abgleich (D2b)
      entfernt sie, keine DB-Zeile;
  (d) **simulierter Abbruch nach Rename vor DB-Commit**: finale `{guid[BUDGET-CAP]ext}` ohne DB-Zeile
      → Start-Abgleich entfernt sie;
  (e) **Delete mit fehlgeschlagener Datei-Löschung** (FileStore-Fake) → DB-Zeile weg (204),
      Datei bleibt → nächster Start-Abgleich entfernt sie; referenzierte, manuell entfernte
      Datei → 410, Metadaten sichtbar (bleibt davon unberührt).
- **Split-Bezug & Korrektur** (Review P1):
  (f) zwei Buchungen mit **gleicher SplitId**: Upload mit fremder SplitId (passende Id,
      andere Buchung) → 400 invalid_reference (Komposit-FK);
  (g) Korrektur entfernt einen **belegten** Split → Originaldatei intakt, Beleg erreichbar
      (umgehängt: `SplitId = null`, `OriginalSplitId` gesetzt), Salden ausschließlich
      gemäß Korrektur, Buchung/Konten/Pools sonst unverändert;
  (h) In-Place-Korrektur eines belegten Splits (Betrag/Pool ändern) → Beleg bleibt an
      gleicher SplitId gültig.
- **Größen** (Review P2): (i) Upload exakt `MaxBytes` → 201; (ii) `MaxBytes + 1` → 413;
  (iii) zulässiger Upload mit **Unicode-Dateiname** (Umlaute) + `bookingId` + `splitId` →
  201, `OriginalFilename` korrekt, `filename*` im Download-Header RFC 5987-korrekt;
  (iv) 413 inkl. UI-Fehlermeldung (host-Test/E2E).
- **Entfernen**: DELETE → 204, DB-Zeile weg, Datei weg, Buchung/Konten/Pools unverändert
  (Balances vor/nach identisch); zweite Datei derselben Rechnung bleibt intakt.
- **404/410**: unbekannte Id; Datei manuell entfernt → 410, Metadaten sichtbar.
- **OpenAPI-Vertrag**: keine Saldo-/Finanzinputs im Vertrag; Regeneration ohne Diff bei
  unveränderter App.

### D9b — Schichtentrennung: `IAttachmentFileStore` (Review P2)

`LarioDataPaths` liegt in `Lario.Infrastructure`; `Application` darf nicht zurück nach
Infrastructure referenzieren (Projektzyklus). Daher:

- **`IAttachmentFileStore` in `Lario.Application`** (neuer Ordner `Attachments/`):
  - `WriteAsync(id, storedName, sourceStream, contentType, maxBytes)` → streamt unter
    `.part`-Namen, validiert Größe/Hash, fsync, Rename; liefert `(SizeBytes, Sha256)`;
  - `ReadAsync(id)` → Stream (Download), fehlende Datei als `FileMissing`-Ergebnis (410-Semantik);
  - `DeleteAsync(id)` → Datei löschen (fehlt bereits → Erfolg);
  - `ListStoredFilesAsync()` → finale + `.part`-Dateinamen nach Namensschema (für den
    Start-Abgleich; die DB-Referenzprüfung macht der Service über das Repository).
- **Implementierung `AttachmentFileStore` in `Lario.Infrastructure`** — nutzt
  `LarioDataPaths` (neues `AttachmentDirectory`), GUID-Namensschema, FSync. Host registriert
  Store + `IAttachmentRepository` (beide Scoped, `LarioServiceCollectionExtensions`).
- **`AttachmentService` (Application)** orchestriert nur `IAttachmentFileStore` +
  `IAttachmentRepository`; Domain bleibt IO-frei.
- Test-Doubles für **beide** Interfaces erlauben gezielte Write-/Commit-/Delete-Fehler
  (Fälle b–e); Integrationstests laufen weiter mit echten Dateien + echter SQLite.

## Batch-Plan (überarbeitet nach Review)

| Batch | Tasks | Inhalt |
|---|---|---|
| A | A1 | Domain-Entität `Attachment` + DbContext-Mapping (Komposit-FK `(BookingId, SplitId)` → `ExpenseSplits` mit Restrict, `OriginalSplitId` plain, Indexe) + Migration |
| A | A2 | **Korrektur-Integration (Review P1)**: `BookingRepository.ReplaceSplits` hängt belegte Splits in derselben Transaktion auf die Gesamtbuchung um (SplitId → null, OriginalSplitId); Tests (f)–(h) |
| A | A3 | Application: `IAttachmentRepository` + `IAttachmentFileStore` (Schnittstellen), `AttachmentService` (Upload-Flow temp/rename/commit + Cleanup; Download; Delete in D5-Reihenfolge; Reconcile-Orchestrierung), EF-Repository-Implementierung |
| A | A4 | Infrastructure: `AttachmentFileStore` (`LarioDataPaths.AttachmentDirectory`, `.part`-Namensschema, FSync, Reconcile-Dateioperationen) + DI-Registrierung (Store + Repository) |
| B | B1 | Host: `AttachmentEndpoints` (POST multipart mit Streaming-Zähler, GET-Liste, GET-Metadaten, GET-Download, DELETE) + abgeleitete Limits in Program.cs (D7: `MultipartBodyLengthLimit`, Kestrel `options.Limits.MaxRequestBodySize`) + Start-Abgleich nach `Migrate()` + README-Config-Eintrag |
| B | B2 | Api-Tests: alle D9-Fälle (a)–(iv), echte Dateien + SQLite, Neustart mit gleichem DataDirectory |
| C | C1 | Frontend: „Belege“-Dialog in der Buchungsliste (Liste, Upload mit Split-Auswahl, Vorschau/Blob, Entfernen, 413-Fehlermeldung) + Client-Regeneration + host-Tests |
| C | C2 | E2E-Smoke-Test (headless Chromium + echter Host: Upload über UI inkl. 413-Fehlerpfad, Neustart, Download, Entfernen) |
| D | D1 | Gesamtabnahme: Build/Tests/Lint grün, OpenAPI-Diff-Prüfung, Doku/Task-Datei, Commit |
