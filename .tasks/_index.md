# Aufgaben-Index — Lario
_Last updated: 12. Okt. 2026_

**Aktiv:** Schritt 5 (Transfers, Umwidmung und interne Ausgleiche) — implementiert 12. Okt. 2026 auf Branch `step/5-transfers-ausgleiche`; externe Review-Runde (13. Okt. 2026: P1 Pairing rückdatierter Buchungen, P2b FundingSource-Provenienz, P2d Upgrade-Backfill) — alle Befunde behoben, 273/273 Tests, 0 Build-Warnungen; **PR #6** (um die Review-Fixes erweitert), **Abnahme durch Nutzer ausstehend**. Schritt 4 ist **abgenommen und gemerged (12. Okt. 2026)** — Merge-Commit `375b2ca`, PR #5 (zwei Review-Runden, alle Befunde behoben; 184 .NET-Tests, davon 24 echte-SQLite-Integrationstests). Schritt 3 ist **abgenommen und gemerged (12. Okt. 2026)** — Merge-Commit `b7d9eab`, PR #4 (drei Review-Runden, alle Befunde behoben; 74 .NET-Tests, davon 16 echte-SQLite-Integrationstests). Schritt 2 ist **abgenommen (11. Okt. 2026)** und **gemerged** (Merge-Commit `61c3a07`, PR #3).

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
- Branches: `step/<nummer>-<kurzname>`; kein Worktree; Commits klein.
- **Workflow (Nutzerfreigabe 12. Okt. 2026):** Nach Abschluss einer Implementierungsphase automatisch commit + push + PR. Merge in `main` nur auf explizite Nutzerfreigabe. (In AGENTS.md verankert.)
- Task-Struktur: Roadmap im Index + detailierte Aufgabe nur für den aktiven Schritt (Nutzer bestätigt).

## Roadmap (20 Abschnitte)

| Schritt | Aufgabe | Kurzbeschreibung | Voraussetzungen | Status |
|---|---|---|---|---|
| 0 | `step-0-rechenvertrag` | Fachlicher Rechenvertrag: Regeln, Ledger-Entwurf, 8 Referenzfälle | — | **fertig** (8. Okt. 2026; Commits `0583ce2`, `a118a25`, `43ee021` (Rebase auf main `de854f8` + Name Lario)); Review-Runde 1 (`e91f156`), 2 (`e7bf514`: Fall 8 — Reservierung ist Anspruch im Herkunftspool, kein Pooltransfer) und 3 (`60f3407`: Revisionskonzept vereinheitlicht, Fall-4-Richtung, Fall-8-Format) umgesetzt; Runden 1–4 **gepusht** auf `origin/step/0-rechenvertrag` (Runde 4: `4773461` Sync-Neuanlage — expectedBaseRevision null statt 1), abgenommen vom Nutzer 9. Okt. 2026, gepusht auf `origin/step/0-rechenvertrag`; **PR #1** (9. Okt. 2026, `step/0-rechenvertrag` → `main`) **merged** (Merge-Commit `ecc696f`); `origin/main` in `step/1-projektgeruest` überführt (Merge `4d02d14`) |
| 1 | `step-1-projektgeruest` | Projektgerüst (4 .NET-Projekte, Tests, Angular-Workspace), Linux-Start, OpenAPI-Generierung | 0 | **fertig** (9. Okt. 2026; Commits `ffe799f` Gerüst, `e3f501f` Domain/Infra/Host+Tests, `a1d5281` Angular-Workspace+Client, `030bfd8` README, `37dbeb8` Abnahme; Fresh-Checkout-Test grün; Abnahmekorrekturen 9. Okt. (BASE relativ, XDG_DATA_HOME, EnsureCreated, README — Commits `496824d`, `987a295`, `a30cae9`, `d8ddd24`, `825e8ed`); **gepusht** auf `origin/step/1-projektgeruest`; **PR #2** (9. Okt. 2026, `step/1-projektgeruest` → `main`) eröffnet) |
| 2 | `step-2-android-prototyp` | Früher Android-/LAN-Prototyp: Capacitor-App, SQLite-Adapter, Offline-Nachweis auf echtem Gerät | 1 | **fertig** (11. Okt. 2026; Code + APK + Live-Host-Prüfung; Review-Runden 1–2 + Gerätebefund OnePlus 9 umgesetzt; **alle drei Geräte-Abnahmen bestanden** (OnePlus 9, finales Debug-APK); gepusht, **PR #3** → main, **merged** (`61c3a07`)) |
| 3 | `step-3-stammdaten` | Stammdaten (Konten/Pools/Kategorien/Steuerkategorien), Anfangsbestand, erste Migration | 0–1 | **PR #4 offen (12. Okt. 2026)** — wartet auf Abnahme/Merge (Branch `step/3-stammdaten`; 58 Tests grün) |
| 4 | `step-4-einnahmen-ausgaben` | Kernbuchungen: Einnahmen/Ausgaben/Splits, Postings, Steuerkennzeichnung je Teilposten | 3 | **abgenommen (12. Okt. 2026)** — Merge `375b2ca`, PR #5 |
| 5 | `step-5-transfers-ausgleiche` | Transfers, Umwidmung, interne Ausgleiche, signierte Konto-Pool-Zuordnung | 4 | **implementiert, Abnahme ausstehend** (12. Okt. 2026; Review ohne Befunde, 259/259 Tests, PR; dokumentierte Abweichung: Umwidmung darf sichtbare negative Poolbestände erzeugen, §3.6) |
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
