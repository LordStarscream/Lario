# Aufgaben-Index — Lario
_Last updated: 8. Okt. 2026_

**Aktiv:** — (Schritt 0 fertig; nächster Auftrag: Schritt 1)

## Legende

- Jeder Implementierungsschritt aus `docs/Finanzplanung_Implementierungsplan.md` §5 ist **eigene Aufgabe**
  („Jeder Schritt ist ein eigener Entwicklungsauftrag und endet mit einem lauffähigen, überprüfbaren Stand").
- `[aktiv]` = aktuell bearbeitet · `[geplant]` = noch nicht gestartet (Detail-Aufgabenfile entsteht bei Start)
  · `[pausiert]` / `[erledigt]` = wie üblich.
- Reihenfolge verbindlich; **7a folgt direkt auf 7**. Schritt 2 ist ein früher technischer Nachweis (echtes Androidgerät).
- „Fertig"-Kriterien und Abnahmen stehen im Plan; Aufgabe-Dateien führen sie als Tasks.

## Geklärte Entscheidungen (8. Okt. 2026, mit Nutzer)

- Schritt 0 liegt unter `docs/step-0/`: zentrale Doku `rechenvertrag.md` + JSON-Dateien für Beispieldaten und Referenzfälle (wiederverwendbar als Testfixtures ab Schritt 3).
- Technische Entscheidungsliste = Entscheidungen auf Major-Ebene mit Begründung; konkrete Patch-Stände (global.json, csproj, Lockfiles) werden erst in Schritt 1 geprüft und fixiert.
- Sync-Scope in Schritt 0: nur das Datenmodell der Sync-Operation (Operations-ID, Payload-Nachweis, Basisrevision, Entwurf/Bestätigung). Endpoints, Cursor, Generationen: Schritt 9/10.
- Android: echtes Testgerät ist **vorhanden** → Geräte-Abnahmen der Schritte 2/8/9/18 sind planbar (nicht zu verwechseln mit Browser-/Emulator-Prüfung).
- Branches: `step/<nummer>-<kurzname>`; kein Worktree; Commits klein; **Push erst nach expliziter Nutzerfreigabe**.
- Task-Struktur: Roadmap im Index + detailierte Aufgabe nur für den aktiven Schritt (Nutzer bestätigt).

## Roadmap (20 Abschnitte)

| Schritt | Aufgabe | Kurzbeschreibung | Voraussetzungen | Status |
|---|---|---|---|---|
| 0 | `step-0-rechenvertrag` | Fachlicher Rechenvertrag: Regeln, Ledger-Entwurf, 8 Referenzfälle | — | **fertig** (8. Okt. 2026; Commits `0583ce2`, `a118a25`, `43ee021` (Rebase auf main `de854f8` + Name Lario)); Review-Runde 1 (`e91f156`) und 2 (`e7bf514`: Fall 8 — Reservierung ist Anspruch im Herkunftspool, kein Pooltransfer) umgesetzt; lokal 3 Commits vor `origin/step/0-rechenvertrag`, **Push wartet auf Nutzerfreigabe** (dann Re-Review) |
| 1 | — | Projektgerüst (4 .NET-Projekte, Tests, Angular-Workspace), Linux-Start, OpenAPI-Generierung | 0 | geplant |
| 2 | — | Früher Android-/LAN-Prototyp: Capacitor-App, SQLite-Adapter, Offline-Nachweis auf echtem Gerät | 1 | geplant |
| 3 | — | Stammdaten (Konten/Pools/Kategorien/Steuerkategorien), Anfangsbestand, erste Migration | 0–1 | geplant |
| 4 | — | Kernbuchungen: Einnahmen/Ausgaben/Splits, Postings, Steuerkennzeichnung je Teilposten | 3 | geplant |
| 5 | — | Transfers, Umwidmung, interne Ausgleiche, signierte Konto-Pool-Zuordnung | 4 | geplant |
| 6 | — | Korrekturen/Storno, Erstattungen, Kontostandsabgleich, Revisionskonflikte | 5 | geplant |
| 7 | — | Produktive REST-API (Auth, DTOs, Validierung), Desktop-Ausgabeformular | 3–6 | geplant |
| 7a | — | PDF-/Bildnachweise in der Hauptanwendung (Upload, Ablage, Zuordnung) | 7 | geplant |
| 8 | — | Produktive mobile Offline-Erfassung: Formular, Outbox, lokale Transaktion | 2, 7 | geplant |
| 9 | — | Sync neuer Ausgaben: Kopplung, Snapshot/Cursor, Idempotenz, Quittung | 7–8 | geplant |
| 10 | — | Sync von Änderungen und Konflikten: Basisrevisionen, Konfliktliste, Archiv | 6, 9 | geplant |
| 11 | — | Wiederkehrende Regeln und geplante Vorgänge (PlannedOccurrence, Nachholung) | 7 | geplant |
| 12 | — | Monatsdeckung, Einkommen/Finanzierungsmonat, Flex-Anzeige | 5, 11 | geplant |
| 13 | — | Ansparungen und regelmäßige Zuweisungen (Jahreskosten, centgenaue Verteilung) | 11–12 | geplant |
| 14 | — | Vorhaben: Reservierung, zweckerhaltendes Parken, Teilfreigabe, Abschluss | 5, 10, 12 | geplant |
| 15 | — | Wohnungsbereich als Ansicht derselben Fachlogik | 11–14 | geplant |
| 16 | — | Einrichtungswizard + Auswertungen inkl. Steueransicht, CSV/Belegpaket-Export | 12–15 | geplant |
| 17 | — | Backup, Restore, Sync-Wiederabstimmung (inkl. Belegdateien) | 3 (Grundbackup), 10, 16 | geplant |
| 18 | — | Linux-/Android-Auslieferung, signierte Builds, Gesamtabnahme | alle | geplant |

## Zwischenziele (Plan §6)

- Nach **Schritt 9**: manuelle Ausgabenerfassung mobil + Desktop funktionsfähig; noch keine Monatsplanung
- Nach **Schritt 12**: erste fachlich nutzbare Version (Einkommen, Verpflichtungen, Flex-Anzeige)
- Nach **Schritt 16**: voller Funktionsumfang inkl. Wohnung und Vorhaben
- Nach **Schritt 18**: geprüfte Auslieferung + Sicherungsstrecke

## Aufgabendateien

- `step-0-rechenvertrag.md` — aktiv (Details siehe dort)
