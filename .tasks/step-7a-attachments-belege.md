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

## Design-Entscheidungen

_(werden nach Code-Analyse festgehalten)_

## Batches / Tasks

_(werden nach Code-Analyse festgelegt)_
