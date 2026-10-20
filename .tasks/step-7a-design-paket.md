# Design-Paket Schritt 7a — PDF-/Bildnachweise
_Stand: 21. Okt. 2026 · Basis: Code-Analyse-Bericht (38 Reads) + Plan §5 Schritt 7a_

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
  - `SplitId` (FK → BookingAllocations, **nullable** = Beleg für die gesamte Ausgabe)
  - `CreatedAtUtc`
- Begründung: Plan verlangt „Datei-ID, Name, Typ, Größe, Prüfsumme **und Buchungsbezug** speichern“
  — der Buchungsbezug ist Attribut der Datei. „Gleiche Dateien verlieren keine anderen Zuordnungen“
  ist ohne Content-Dedup trivial erfüllt (jede Upload-Kopie hat exakt eine Zuordnung;
  das Löschen einer Zeile + ihres eigenen Files kann andere Zuordnungen nicht berühren).
  Eine Link-Tabelle + Content-Addressing wäre für den Grundumfang eine vorgezogene Verallgemeinerung.
  Dedup/Geteilte Belege sind bei Bedarf später erweiterbar (Sha256 ist bereits gespeichert).
- `SplitId` ist nicht null → die Zeile belegt exakt diesen Split (Summe mehrerer Split-Belege pro
  Ausgabe ist möglich); null → der Beleg nachweist die gesamte Ausgabe (einschließlich aller Splits).
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

### D3 — Upload-Vertrag: `POST /api/v1/attachments` (multipart)

- Felder: `file` (einzige Datei), `bookingId`, optional `splitId`.
- Prüfung in fester Reihenfolge:
  1. **Größe** während des Streamings (Limit `Lario:Attachments:MaxBytes`, Default **10 000 000**,
     konfigurierbar) → `413`/`validation_error` `code=attachment_too_large`.
  2. **Typ**: multipart `Content-Type` der Part **und** Magic-Bytes (PDF `%PDF-`, JPEG `FF D8 FF`,
     PNG `89 50 4E 47 0D 0A 1A 0A`) müssen übereinstimmen → sonst `400 validation_error`
     `code=unsupported_attachment_type`.
  3. **Buchungsbezug**: `bookingId` existiert (sonst `400 invalid_reference`); `splitId` existiert
     **und gehört zur Buchung** (sonst `400 invalid_reference`).
- Reihenfolge der Ablage:
  1. Datei streamen nach `Root/attachments/{id}{ext}` (FSync vor Close — dauerhafte Ablage),
     SHA-256 dabei berechnen, Größe exakt zählen.
  2. DB-Eintrag in `Bookings`-unabhängiger Transaktion anlegen (FK-Prüfung durch SQLite).
  3. **DB-Eintrag schlägt fehl → Datei wird gelöscht** (kontrollierte Bereinigung, kein Orphan).
  4. Antwort `201` erst danach, mit vollständigen Metadaten (Id, Name, Typ, Größe, Prüfsumme,
     Buchungsbezug, Split).
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

- Löscht DB-Zeile **und** physikalische Datei (bestrebt; existiert nicht mehr → Zeile löschen,
  Erfolg). Keine Berührung der Buchung (Konten/Pools unverändert — strukturell, es gibt keine
  Finanzwirkung).
- `404` bei unbekannter Id. `204` bei Erfolg.

### D6 — Idempotenz bewusst ohne Operations-ID

- Anhänge sind keine Finanzbuchung: keine Operation-ID, kein Idempotenz-Nachweis, keine
  Basisrevision (Plan: diese Mechanik gilt für Buchungsoperationen). Wiederholung nach verlorener
  Antwort → doppelte Zeile + Datei; Nutzer sieht beide Einträge und kann löschen. Bewusst
  einfacher als bei Buchungen; dokumentiert, nicht still.

### D7 — Grenzen

- `Lario:Attachments:MaxBytes` Default 10 000 000 (≈ 9,5 MB), in `Program.cs` als
  `FormOptions.MultipartBodyLengthLimit` **und** Kestrel `LimitRequests.MaxRequestBodySize`
  gesetzt (doppelte Sicherung; Endpunkt prüft zusätzlich während des Streamings).
- In `README.md` Config-Tabelle nachtragen.

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
- **Uploadfehler hinterlässt keine Waise**: (a) ungültige `bookingId` → kein Dateis schreiben,
  keine DB-Zeile; (b) DB-Eintrag nach erfolgreichem Datei-Schreiben schlägt fehl → Datei wird
  gelöscht. (b) deterministisch über Test-Doppel: `AttachmentService` nimmt ein
  `IAttachmentRepository` (Application-Interface, EF-Implementierung, DI — Muster
  `IBookingRepository`) + `LarioDataPaths`; im Test wirft das Repository-Fake beim Add →
  Verzeichnis muss leer bleiben. Beide Varianten: keine sichtbare Verknüpfung, keine Waise.
- **Entfernen**: DELETE → 204, DB-Zeile weg, Datei weg, Buchung/Konten/Pools unverändert
  (Balances vor/nach identisch); zweite Datei derselben Rechnung bleibt intakt.
- **404/410**: unbekannte Id; Datei manuell entfernt → 410, Metadaten sichtbar.
- **OpenAPI-Vertrag**: keine Saldo-/Finanzinputs im Vertrag; Regeneration ohne Diff bei
  unveränderter App.

## Batch-Plan

| Batch | Tasks | Inhalt |
|---|---|---|
| A | A1 | Domain-Entität `Attachment` + DbContext-Mapping (FKs, Indexe: `BookingId`, `SplitId` partiell) + Migration |
| A | A2 | Application: `AttachmentService` (Upload-Flow inkl. Streaming, Typ-/Größen-/Magic-Prüfung, SHA-256, Cleanup bei DB-Fehler; Download; Delete) + DI-Registrierung + Config-Default |
| B | B1 | Host: `AttachmentEndpoints` (POST multipart, GET-Liste, GET-Metadaten, GET-Download, DELETE) + `FormOptions`/Kestrel-Limits in Program.cs + README-Config-Eintrag |
| B | B2 | Api-Tests (alle D9-Fälle) |
| C | C1 | Frontend: „Belege“-Dialog in der Buchungsliste (Liste, Upload mit Split-Auswahl, Vorschau/Blob, Entfernen) + Client-Regeneration + host-Tests |
| C | C2 | E2E-Smoke-Test (headless Chromium + echter Host: Upload über UI, Neustart, Download, Entfernen) |
| D | D1 | Gesamtabnahme: Build/Tests/Lint grün, OpenAPI-Diff-Prüfung, Doku/Task-Datei, Commit |
