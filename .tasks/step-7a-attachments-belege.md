# Aufgabe: Schritt 7a — PDF-/Bildnachweise in der Hauptanwendung
_Status: implementiert, Abnahme ausstehend · Last updated: 21. Okt. 2026_

**Branches:**
- Lario → `step/7a-belege-impl` (Worktree `/home/mario/Projects/Lario-worktrees/step-7a`; Haupt-Checkout bleibt auf `main`). Basis `666a9ce` (Merge PR #12, Design).
- **PR: #13** (angelegt 21. Okt. 2026, Basis main, head `step/7a-belege-impl`; Merge nur auf explizite Nutzerfreigabe).

**Umgesetzt (21. Okt. 2026), 6 Commits:**
- `5c35bc6` (Batch A): Domain `Attachment` + `ReplaceSplits`-Erhaltungsregel (Umlageung belegter Split-Entfernung auf Gesamtbuchung), `AttachmentRepository` (EF, Komposit-FK), `AttachmentService` (Upload: Typ→Größe→Magic→Referenz, `.part`+Rename, Delete-Order DB→Datei, Start-Abgleich), Migration `20261021090000_Attachments` + Backfill (SHA-256), 36 Domain- + 31 Infrastructure-Tests.
- `aad0693`/`55f0ba7` (B1): `AttachmentEndpoints` (POST multipart, GET-Liste, GET-Download `inline`, DELETE), Upload-Limits (`MaxBytes=10 MB`, `MultipartBodyLengthLimit`/Kestrel `MaxRequestBodySize = MaxBytes+1 MB` abgeleitet), `DetailCode`-Felder (`unsupported_attachment_type`, `magic_byte_mismatch`, `invalid_reference`), Start-Abgleich nach `Migrate()`, README-Betriebsabschnitt (Belege-Ordner im Backup), 5 Api-Tests.
- `99fac1e` (B2): Api-Tests (a)–(iv) — 19 weitere (Typ-Prüfung, Größen-Grenze exakt/+1, Magic-Mismatch, Referenz, Auth, Persistenz, Start-Abgleich-Orphans, Delete-Order, **Parallelfall Upload vs. Split-Entfernung**: exakt einer der zwei konsistenten Endzustände, keine Orphan-Datei), 3 neue Api-Tests.
- `cd6ea91` (C1): Beleg-UI in der Buchungsliste (Belege-Button pro Ausgabe-Zeile, Belege-Dialog mit Detail-Kopfzeile, Belegliste, Upload mit Client-Vorvalidierung + Split-Zuordnung bei >1 Splits, Bildvorschau via autorisiertem Blob-Download `AttachmentDownloadService`, PDF-Download-Button, Entfernen mit Bestätigung, Fehler-Banner), OpenAPI neu generiert, `formatBytes` in `money.ts`, 13 host-Tests.
- `25b7e1a` (D2, Review 7a Befund 3): Race-Abwehr — Race A (Split zwischen Referenzprüfung und Beleg-Insert gelöscht) → exakte FK-Erkennung (`SqliteConstraintErrors`, SQLite-Erweitertecode 787) → `InvalidReferenceException` (400) + Datei-Cleanup; Race B (Beleg an entfallenden Split zwischen Lese- und Commit) → Retry mit frischem Lesezustand (≤3), Erhaltungsregel umhängt den Beleg, sonst `DomainConflictException` (409). Deterministische Reproduktionen p3/p4 per Test-Dekoration; `LarioDomainException` erhält InnerException-Konstruktor.
- `5c496f5` (D4, Review 7a Befunde 4 + 1): **Befund 4 (P1)** — `BookingRepository.UpdateAsync` führt `expectedRevision = BaseRevision - 1` einmalig her und prüft sie als CAS in jedem Retry-Versuch; der EF-ConcurrencyToken wird auf dieselbe Basis fixiert (vorher: CAS nur im ersten Versuch, `OriginalValue` auf den geladenen Wert). Eine parallele Revisionsänderung zwischen Laden und Commit schlägt damit als sichtbarer 409 fehl statt stillschweigend zu übersteuern (lost update). Deterministische Reproduktion **p5**: der erste `Attachments`-SELECT der Update-Transaktion triggert per `IDbCommandInterceptor` eine echte parallele Korrektur (Note + Basisrevision 2 + Historienzeile) über eine zweite SQLite-Verbindung; A scheitert mit 409 `DomainConflictException`, B bleibt vollständig erhalten (Note/Revision/Historie/Splits, keine Teilanlage). **Befund 1 (P2)** — `toApiErrorInfo` liefert für HTTP-Status ohne nutzbares Fehler-Body eine verständliche Fallback-Meldung statt bloßem „HTTP 413“ (413/408/504); empirisch verifiziert (Host mit `MaxBytes=2048`): Service-Limit-413, 400-Typ/Magic haben JSON-Body mit Meldung, **Kestrel-Envelope-413 (Datei > Limit + 1 MB) liefert leeren Body** — Kestrel schreibt die 413 selbst, der Host kann keinen JSON-Body liefern (ASP.NET-Limitation, serverseitig nicht abfangbar; kein Log-Eintrag) → die nutzbare Meldung kommt zwingend vom Client. 6 neue host-Unit-Tests. **Befund 2 (P2) — bereits in B1 abgedeckt** (verifiziert): `options.Limits.MaxRequestBodySize` und `FormOptions.MultipartBodyLengthLimit` sind **global** auf `MaxBytes + 1 MB` abgeleitet (nicht nur am Upload-Endpunkt) → kein 100-MB-Envelope auf irgendeinem Endpunkt; empirisch: 1-MB-Envelope an den Upload-Endpunkt → 413.

**Prüfungen (21. Okt. 2026, final nach D4):** .NET **538/538** (214 Domain, **200 Infrastructure** — +p5, 124 Api), host **109/109** (+6 api-error-Specs), mobile 23/23, Lint 0 (host+mobile), Builds grün (host+mobile); Race-Tests p1–p5 dreifach stabil (je ~1 s, keine 30-s-Lockwartungen mehr). Hinweis: `dotnet test` für Api.Tests auf dieser Maschine nur mit `$HOME/.dotnet/dotnet` grün (vstest setzt `DOTNET_ROOT_X64=/usr/share/dotnet`, dort fehlt AspNetCore.App) — **besteht auf `main` identisch** (vor 7a), kein 7a-Problem. **E2E-Smoke-Test (21. Okt. 2026, `/tmp/lario-e2e-7a/smoke.mjs`, headless Chromium/CDP + echter Host + frische SQLite-DB): 36/36**, keine Konsolen-/Seitenfehler — Phase A API 22 (Health/401/Login, Stammdaten 200, Ausgabe/Einnahme 201, Upload 201×2, Liste, Download Typ+Bytes, **400 unsupported_attachment_type**, 413 >50-KB-Limit, 400 Magic-Mismatch, 400 invalid_reference, Anhang an Einnahme 201 — Design D3 dokumentierte Typ-Restriktion fehlt in der API, Delete 204 + 404 danach) + Phase B UI 14 (Login, Ausgabe-Zeile + Belege-Button, Dialog, API-Beleg sichtbar, UI-Upload, Bildvorschau, Persistenz über Schließen/Öffnen, Entfernen über UI, Server-Zustand, Konsole). **Review-Smoke (21. Okt. 2026, `/tmp/step7a-review-smoke/run.mjs`, echter Host + frische SQLite-DB): 17/17** — Kern-Verhalten von Befund 3 E2E: Upload an Split → Korrektur entfernt den belegten Split → **Beleg auf Buchungsebene umgelagert (splitId=null, originalSplitId=sp-1)** → Download mit Original-Bytes; dazu D3-Abweichung (201 an Einnahme), 413-Limit, 400 invalid_reference (Split + Buchung), konsistenter Endzustand (Revision 2, nur sp-2). Hinweis: Smoke-Skripte liegen in /tmp (nicht im Repo); zwei Setup-Fehler im Review-Skript behoben (Host-Port via `Lario:Http:Port` statt `--urls`; S9 erwartet 201 = neue Korrektur-Buchung, 200 wäre Idempotenz-Repeat).

**Bewusste Abweichungen (dokumentiert im Design-Paket / Doku):**
- D3 (bewusst, im Design dokumentiert): die **API akzeptiert Anhänge an jeder existierenden Buchung**; die Typ-Restriktion (nur Ausgaben) gilt auf **UI-Ebene** (Belege-Button nur bei Ausgabe-Zeilen). A18 im Smoke verifiziert 201 an Einnahmen.
- A9 (bewusst): UI-Upload-Grenze **10 MB** (Standard-`MaxBytes`, Design D7), Server-Limit konfigurierbar; Smoke nutzt 50-KB-Limit + 60-KB-Datei (A15) — beide verifiziert.
- Smoke-B14 „Konsole frei“: headless Chromium ohne DevTools-UI; `pageerror`-Events + fehlende UI-Zustände als Fehlerindikatoren.

**Offene manuelle/gerätebezogene Abnahmen:** A1 (Host-Neustart mit echten Dateien — im Smoke als Persistenz + Start-Abgleich API-seitig abgedeckt, manuelle UI-Prüfung nach Neustart), A2 (mehrere Dateien an derselben Rechnung — im Smoke API-seitig; UI-Prüfung), A3 (abgebrochener Upload — Infrastructure-Tests; UI-Abbruch-Prüfung), A6 (vollständige Restore-Abnahme folgt in Schritt 17). Browser-Prüfung ≠ Geräte-/manuelle Abnahme.

**Gestartet:** 21. Okt. 2026 (Basis `d15f452`, main)

**Umfang (Plan §5 Schritt 7a):** Eigene Attachment-Metadaten + lokaler Dateispeicher auf dem Host; PDF, JPEG und PNG über die Hauptoberfläche hochladen, anzeigen/herunterladen und einer Ausgabe zuordnen; optional einzelne Splits verknüpfen (ein Beleg kann die gesamte Split-Ausgabe nachweisen); Originaldatei erhalten; Dokumente nachträglich anhängbar; fehlender Beleg blockiert keine Buchung. Datei-ID, Name, Typ, Größe, Prüfsumme und Buchungsbezug speichern; Dateien außerhalb der öffentlich ausgelieferten Oberfläche, nur über authentifizierte Endpunkte; Uploadgrenzen, Dateityp-Prüfung, serverseitig erzeugte Dateipfade. Erst nach erfolgreicher dauerhafter Ablage einen verwendbaren Anhang bestätigen; Uploadfehler verändern keine Finanzbuchung, unvollständige Ablagen werden kontrolliert bereinigt. Das Entfernen einer Zuordnung verändert keinen Geldbetrag; gleiche Dateien dürfen nicht unbeabsichtigt andere Belegzuordnungen verlieren.

## Abnahme (A1–A6)

- **A1** PDF und Bild an eine Ausgabe anhängen, Host neu starten → Metadaten + Datei vorhanden, öffnen/herunterladen funktioniert.
- **A2** Mehrere Dateien an derselben Rechnung; ein Beleg an einer Ausgabe mit mehreren Splits (ganze Ausgabe + optional einzelner Split).
- **A3** Fehler während Upload (abgebrochen/unvollständig) → keine sichtbare Verknüpfung auf eine fehlende Datei (keine DB-Zeile ohne Datei); unvollständige Ablage wird bereinigt.
- **A4** Ungültiger Dateityp, zu große Datei, unberechtigter Abruf (ohne/fehlerhafter Token) → abgewiesen (4xx/401), kein Seiteneffekt.
- **A5** Beleg/Zuordnung entfernen → Konten und Pools unverändert; eine zweite Zuordnung derselben Datei geht nicht verloren.
- **A6** Vorhandene digitale Nachweise sind ohne mobile Belegerfassung in der Hauptanwendung verwaltbar (Upload, Anzeige, Download, Zuordnung, Entfern­ung). Originaldateien + Metadaten gehören ab diesem Schritt zum Sicherungsumfang (vollständige Restore-Abnahme folgt in Schritt 17).

**Erweitert nach Designreview PR #12 (21. Okt. 2026):**

- **A7** (Review P1, nach Nachreview präzisiert) Split-Korrektur-Lebenszyklus: Korrektur entfernt einen belegten Split → Originaldatei intakt, Beleg auf die Gesamtbuchung umgehängt (SplitId null, OriginalSplitId gesetzt), Salden ausschließlich gemäß Korrektur; Split-Bezug: dieselbe SplitId darf in mehreren Buchungen vorkommen (beide Zuordnungen unabhängig gültig), nur ein nicht existierendes (bookingId, splitId)-Tupel wird abgewiesen — **keine** globale SplitId-Eindeutigkeit; Parallelfall Upload vs. Split-Entfernung als eigener Integrationstest (exakt einer der beiden konsistenten Endzustände, keine Orphan-Datei);
- **A8** (Review P2) Abbruchfestigkeit: Upload-Abbruch vor/nach Rename hinterlässt keine sichtbaren Orphans (Start-Abgleich entfernt `.part`-Dateien und finale Dateien ohne DB-Zeile); fehlgeschlagene Datei-Löschung beim Delete wird beim nächsten Start bereinigt, Metadaten sind bereits weg;
- **A9** (Review P2) Grenzen: Upload exakt `MaxBytes` wird akzeptiert, `MaxBytes + 1` abgewiesen (413); Upload mit Unicode-Dateiname + beiden Ids funktioniert; 413-Fehler ist in der UI verständlich.

## Design-Entscheidungen

Siehe `.tasks/step-7a-design-paket.md` (D1–D9b). Stand nach Designreview PR #12:

- **Review (LordStarscream, 21. Okt. 2026):** 4 Punkte (1×P1, 3×P2) — alle vier im
  Design-Paket umgesetzt:
  - **P1** Split-Bezug: Komposit-FK `(BookingId, SplitId)` → `ExpenseSplits` (Muster
    `SplitTaxMarkings`), Erhaltungsregel in `ReplaceSplits` (Umlageung auf Gesamtbuchung
    in derselben Transaktion) — D1/D1b.
  - **P2** Abbruch-Cleanup: `.part`-Tempname + Rename + Start-Abgleich nach `Migrate()`;
    Delete-Order DB-zeile → Datei — D2/D2b/D3/D5.
  - **P2** Größen: `MaxBytes` = Dateibyte; `MultipartBodyLengthLimit` und Kestrel
    `options.Limits.MaxRequestBodySize` = `MaxBytes + 1 MB` (abgeleitet) — D7.
  - **P2** Schicht: `IAttachmentFileStore` in Application, Implementierung in
    Infrastructure — D9b.
- **Nachreview (LordStarscream, 21. Okt. 2026): keine blockierenden Befunde** — Design
  freigegeben als Grundlage für die Implementierung (keine Implementierungsabnahme, kein
  Merge-Auftrag). Nicht-blockierender Präzisierungshinweis umgesetzt: A7/D3/D9(f) —
  Komposit-FK erlaubt dieselbe SplitId in mehreren Buchungen (beide unabhängig gültig),
  abgewiesen wird nur ein nicht existierendes Tupel (Tests f1/f2); Parallelfall Upload vs.
  Split-Entfernung als eigener Integrationstest (i); beschädigte Platzhalter in D3/D9
  durch das GUID-/Endungsschema (storedName) ersetzt.

## Batches / Tasks

| Batch | Tasks | Status |
|---|---|---|
| A | A1 Domain/Mapping/Migration · A2 Korrektur-Integration · A3 Application (Interfaces + Service + EF-Repo) · A4 Infrastructure (FileStore + DI) | **erledigt** (`5c35bc6`) |
| B | B1 Host-Endpunkte + Limits + Start-Abgleich + README · B2 Api-Tests (a)–(iv) | **erledigt** (`aad0693`/`55f0ba7`/`99fac1e`) |
| C | C1 Frontend-Dialog + Client-Regeneration + host-Tests · C2 E2E-Smoke-Test | **erledigt** (`cd6ea91` + Smoke 36/36) |
| D | D1 Gesamtabnahme + Commit · D2 Review-Befund 3 (Race-Abwehr) + Doku · D4 Review-Befunde 4 (CAS) + 1 (Fehlermeldung) | **erledigt** (21. Okt. 2026: final 538/538 .NET, 109 host, 23 mobile, Lint/Build grün, Smoke 36/36; D1-Zeitpunkt 535/535) |

## Abnahme-Status (A1–A9)

| # | Status | Nachweis |
|---|---|---|
| A1 | ⏳ manuell (Nutzer) | API-Persistenz + Start-Abgleich im Smoke; UI-Prüfung nach Host-Neustart offengelassen |
| A2 | ⏳ manuell (Nutzer) | API: mehrere Dateien an derselben Buchung (A11: 2 Belege); UI: Upload + Vorschau |
| A3 | ⏳ manuell (Nutzer) | Infrastructure-Tests (`.part`-Cleanup, Delete-Order); UI-Abbruch offen |
| A4 | ✅ | Smoke A14/A15/A16 (Typ/Größe/Magic), A2 (401); Api-Tests |
| A5 | ✅ | Smoke A20–A22 (Delete, 404, Liste); Api-Tests (Zweit-Zuordnung intakt) |
| A6 | ⏳ Schritt 17 | Vollständige Restore-Abnahme folgt in Schritt 17; Belege-Ordner im Backup (README) |
| A7 | ✅ | Api-Test `UploadAndSplitRemovalParallel` (exakt einer der zwei Endzustände, keine Orphan); Domain-Test Umlageung |
| A8 | ✅ | Api-Tests Start-Abgleich (`.part` + finale Datei ohne DB-Zeile); Infrastructure-Tests |
| A9 | ⚠️ Teil | UI 10 MB (D7) + Server 50-KB-Limit (Smoke A15) verifiziert; „413 in der UI verständlich“ — UI zeigt `max_content_length`-Meldung (host-Test vorhanden) |

**Stand: 21. Okt. 2026 — implementiert, Review-Befunde 1/2/3/4 umgesetzt (D2/D4), Abnahme A1–A3 manuell beim Nutzer, A6 folgt in Schritt 17.**

**Prüfstand (alle 21. Okt. 2026, Worktree):** .NET 537/537 (Domain 214, Infrastructure 199, Api 124) + Host 103/103, Mobile 23/23, Lint 0, Web-/Mobile-Production-Builds ok, E2E-Smoke 36/36 (headless Chromium/CDP gegen echten Host + frische SQLite-DB, keine Konsolen-/Seitenfehler). Hinweis: `dotnet test Lario.slnx` schlägt auf dieser Maschine bei Api.Tests fehl (vstest setzt `DOTNET_ROOT_X64=/usr/share/dotnet`, dort fehlt AspNetCore.App) — **besteht auf `main` identisch** (vor 7a), kein 7a-Problem; Einzelprojekt-Runs sind zuverlässig grün.
