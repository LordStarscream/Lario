# Aufgabe: Schritt 7a — PDF-/Bildnachweise in der Hauptanwendung
_Status: aktiv · Last updated: 21. Okt. 2026_

**Branches:**
- Lario → `step/7a-attachments-belege` (Worktree `/home/mario/Projects/Lario-worktrees/step-7a`; Haupt-Checkout bleibt auf `main`)

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

- **A7** (Review P1) Split-Korrektur-Lebenszyklus: Korrektur entfernt einen belegten Split → Originaldatei intakt, Beleg auf die Gesamtbuchung umgehängt (SplitId null, OriginalSplitId gesetzt), Salden ausschließlich gemäß Korrektur; gleiche SplitId in anderer Buchung wird beim Upload abgewiesen (Komposit-FK);
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

## Batches / Tasks

| Batch | Tasks | Status |
|---|---|---|
| A | A1 Domain/Mapping/Migration · A2 Korrektur-Integration · A3 Application (Interfaces + Service + EF-Repo) · A4 Infrastructure (FileStore + DI) | offen |
| B | B1 Host-Endpunkte + Limits + Start-Abgleich + README · B2 Api-Tests (a)–(iv) | offen |
| C | C1 Frontend-Dialog + Client-Regeneration + host-Tests · C2 E2E-Smoke-Test | offen |
| D | D1 Gesamtabnahme + Commit | offen |
