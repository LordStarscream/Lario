# Schritt 5 — Transfers, Umwidmung und interne Ausgleiche

**Branch:** `step/5-transfers-ausgleiche` · **Basis:** Schritt 4 (merged, `375b2ca`) · **Status:** implementiert (12. Okt. 2026), drei externe Review-Runden (13. Okt. 2026), alle Befunde behoben, Abnahme durch den Nutzer ausstehend · **PR:** #6
**Spezifikation:** `docs/step-0/rechenvertrag.md` (§3.3 Postings, §3.4 signierte Konto-Pool-Zuordnung, §3.5 offene Ausgleichspositionen, §3.6 freie Verfügbarkeit, §5.2 Transfer/Umwidmung-Status)
**Referenzfälle:** `docs/step-0/referenzfaelle/04-ausgleich.json`, `05-kontotransfer.json`, `06-umwidmung.json`

## Abnahmekriterien (aus dem Plan)

1. **Transfer A→B (Konten)** — Ein Transfer von Konto A nach Konto B reduziert das Guthaben von A und erhöht das von B um den Transferbetrag. Er ist weder Einnahme noch Ausgabe; die Poolbestände bleiben unverändert. Die Buchung enthält eine signierte Konto-Pool-Zuordnung, die den Transfer eindeutig belegt: `Konto A −X` und `Konto B +X`, jeweils mit demselben Betrag und derselben Begründung (Notiz) verknüpft. Die Zuordnung ist in beide Richtungen nachvollziehbar: Vom Konto A lässt sich der Transfer nach Konto B bestimmen, und umgekehrt.

2. **Umwidmung (Pool A → Pool B, ohne Kontobewegung)** — Eine Umwidmung verschiebt Betrag vom Pool A zum Pool B, ohne ein Konto zu berühren. Die Kontostände bleiben unverändert; die Poolbestände ändern sich exakt um den Umwidmungsbetrag. Sie ist keine Kontobewegung und erzeugt keine Buchungszeile mit Kontosalde; sie wird als eigene Änderung (Änderungsgrund „Umwidmung“) mit Quelle und Ziel protokolliert.

3. **Interner Ausgleich (Konto ↔ Pool)** — Ein interner Ausgleich gleicht einen Kontostand mit einem Pool aus: Der Betrag wird dem Konto gutgeschrieben oder belastet und dem Pool in der Gegenrichtung zugebucht. Die Summe aus Konto- und Poolseite bleibt dabei invariant (was dem Konto zufließt, verlässt den Pool, und umgekehrt). Die Buchung trägt die signierte Zuordnung `Konto +X` / `Pool −X` (bzw. umgekehrt) mit gemeinsamer Begründung.

4. **Reservierungen werden nicht angetastet** — Weder Transfer noch Umwidmung noch interner Ausgleich verändert Reservierungen: Reservierte Beträge bleiben Reservierung des jeweiligen Pools; Transfers „erhalten“ Reservierungen im Sinne von: eine Reservierung, die an einen Pool gebunden ist, bleibt an diesem Pool gebunden, auch wenn der Pool durch eine Umwidmung Mittel erhält oder abgibt. Ein Transfer zwischen Konten bewegt keine Reservierung (diese sind Poolgebunden, nicht kontogebunden).

5. **Abgeleitete Salden bleiben rekonstruierbar** — Nach einer beliebigen Folge aus Transfers, Umwidmungen und internen Ausgleichen lassen sich alle Kontostände und Poolbestände durch Neuberechnung aus den Buchungen exakt rekonstruieren. Die Summe aller Kontoguthaben ändert sich nur durch Einnahmen/Ausgaben (und Anfangsbestände), nicht durch interne Bewegungen; die Summe aller Poolbestände ändert sich nur durch Einnahmen (Zufluss), Ausgaben (Abfluss) und Reservierungen (Bindung/Free), nicht durch Umwidmungen.

6. **Keine doppelte Erfassung** — Ein interner Ausgleich, der eine Ausgabe „finanziert“ (z. B. Konto lädt Pool auf, um eine Ausgabe zu decken), wird nicht zusätzlich als Ausgabe verbucht: Die Ausgabe reduziert den Pool einmal; der vorgängige Ausgleich ist eine interne Bewegung ohne eigenen Pool-Effekt auf die Ausgabe.

## Umsetzung

| Kriterium | Umsetzung |
|---|---|
| 1. Transfer A→B | `TransferSpec` (Quell-/Zielkonto, Betrag, Zwecke) → Buchung vom Typ AccountTransfer mit Konto-Pool-Zeilen (`TransferPurposes`); impliziter Zweck aus der signierten Zuordnung (eindeutiger positiver Pool mit Deckung) oder explizit angegeben, als `TransferPurposes` materialisiert. `TransferService` verifiziert Konten, Betrag (Quellkonto trägt den Betrag) und Zwecke; die Poolbestände bleiben unverändert. |
| 2. Umwidmung | `ReassignmentSpec` (Quell-/Zielpool, Betrag, tragende Konten `ReassignmentAccounts`) → Buchung vom Typ Reassignment ohne Konto-Postings; `ReassignmentService` wendet an: Pool A −X, Pool B +X. Die Kombination `TransferReassignmentSpec` trägt je exakt ein Konto- und ein Pool-Posting-Paar (`TransferReassignmentService`). |
| 3. Interne Ausgleiche | Negative Zelle (Konto, Pool) in der abgeleiteten Zuordnungsmatrix = offene Ausgleichsposition (§3.5). `SettlementPosition` wird persistiert (eigene stabile Id, Status, Begleichungsreferenz); `SettlementPositionSync` leitet bei jeder Buchung den Übergang des Zellenzustands ab und speichert die Positionsatome mit der Buchung; `SettlementQueryService` führt Journal und Tabelle zur Ansicht zusammen. Begleichung/Teilbegleichung: jede Buchung, die eine negative Zelle weniger negativ macht, reduziert den Rest (Teilbegleichung bei kleinerem Betrag; bei Rest 0 geschlossen mit `settledByBooking`). |
| 4. Reservierungen | Keine der drei Operationen verändert Reservierungen — in der `BookingEngine` strukturell (kein Pfad, der Reservierungen berührt); Abdeckung durch Tests, die Reservierungen vor/nach setzen und Gleichheit prüfen. |
| 5. Rekonstruierbarkeit | `BookingJournal.Allocations` (Zuordnungsmatrix: je Buchung ein Delta je Zelle) mit Invarianten Zeilensummen = Kontostände, Spaltensummen = Poolbestände; Rekonstruktions-Tests über Transfer-/Umwidmungsfolgen und gemischte Folgen (Referenzfälle 04/05/06 end-to-end). |
| 6. Keine doppelte Erfassung | Begleichungsregel: die Ausgleichsbewegung (Transfer mit `relatedSettlementId`, Ziel = Quellkonto der Position, anlangende Mittel tragen den Positionspool) reduziert nur die offene Position; Referenzfall „Ausgabe über fremdes Konto“ (04) als echter SQLite-Integrationstest. |

## Dateien

**Domain** (`src/Lario.Domain/Journal/`):
- `TransferSpec.cs`, `ReassignmentSpec.cs`, `TransferReassignmentSpec.cs` — Vorgaben (Prüfungen, Materialisierung)
- `Booking.cs` — Buchungsmodell (Typen AccountTransfer/Reassignment/TransferReassignment, `RelatedSettlementId`, `AllocationExplicit`)
- `AccountAllocation.cs`, `AccountPoolCell.cs`, `AllocationDeltaEntry.cs` — signierte Konto-Pool-Zuordnung
- `BookingJournal.cs` — Zuordnungsmatrix und Ausgleichsepisoden ableiten (deterministisch, kanonische Reihenfolge Fachdatum + Id)
- `SettlementPosition.cs`, `SettlementPositionState.cs` — persistierte Zeile bzw. Journal-abgeleiteter Episoden-Zustand

**Application** (`src/Lario.Application/Journal/`):
- `TransferService.cs`, `ReassignmentService.cs`, `TransferReassignmentService.cs` — Anwendungsfälle (inkl. Vormerkung/Bestätigung/Abbruch über Buchungs-Idempotenz, Begleichung/Teilbegleichung)
- `SettlementPositionSync.cs` — Übergangsanalyse Zellenzustand (vorher/nachher der Buchung) und Speicherung der Positionsänderungen atomar mit der Buchung
- `SettlementPositionBackfillService.cs` — Upgrade-Backfill offener Positionen (Host-Start, idempotent)
- `SettlementQueryService.cs` — Abfrage offener/beglichener Positionen (Journal + Tabelle, Integritätsfehler sichtbar)
- `ISettlementPositionRepository.cs` — Schnittstelle

**Infrastructure**:
- `LarioDbContext.cs` — EF-Mapping (TransferPurposes, ReassignmentAccounts, SettlementPositions)
- `Migrations/20261012200106_TransfersAndSettlements.cs` — Positionsverwaltung
- `Migrations/20261013072950_AllocationProvenance.cs` — `AllocationExplicit` (Review R1/P2b)
- `src/Lario.Host/Program.cs` — Backfill-Aufruf nach der Migration

**Tests**:
- `tests/Lario.Domain.Tests/AllocationAndSettlementTests.cs` — Domain-Invarianten (Buchungstypen, Zuordnungsmatrix, Ausgleichs-Ableitung, Referenzfälle 04/05/06)
- `tests/Lario.Infrastructure.Tests/Step5TransferSettlementTests.cs` — echte SQLite: Referenzfälle end-to-end, Teilbegleichung, Idempotenz, Konflikte, Atomarität, Neustart
- `tests/Lario.Infrastructure.Tests/Step5SettlementFlowTests.cs` — echte SQLite: offener Zyklus, Teil-/Vollbegleichung, Zusammenführung, Neustart, explizite/implizite Referenz
- `tests/Lario.Infrastructure.Tests/Step5ReviewFindingsTests.cs` — echte SQLite: Review-Befunde R1–R3

## Build & Tests

```bash
dotnet build Lario.slnx
dotnet test Lario.slnx --no-restore
```

- 282/282 Tests erfolgreich (175 Domain, 107 Infrastructure; davon 56 echte-SQLite-Integrationstests für Schritt 5).
- Android-/Geräte-Abnahmen: nicht Bestandteil dieses Schritts (folgt in Schritt 8/9/18).

## Externe Review-Befunde (13. Okt. 2026)

### Runde 1 — drei Befunde, alle behoben

- **P1 — Pairing bei rückdatierten/gleichdatierten Buchungen** — `SettlementPositionSync.Plan` und `SettlementQueryService` paarten Zeilen und abgeleitete Episoden über `(Konto, Pool, Begründungsbuchung)`. Eine rückdatierte Ausgabe verschiebt die Begründung des offenen Zyklus — die alte Zeile wäre dann nie gepaart worden (offene Episode ohne Zeile / Zeile ohne Episode).
  - **Fix**: Journal-Wahrheit mit stabiler Identität: pro Zelle trägt die **jüngste Zeile die (maximal eine) offene Episode**; beglichene Episoden/Zeilen werden chronologisch gepaart. Persistierte Zeilen behalten ihre Identität (Id, Begründung, Notiz); die Ansicht zeigt den Journalzustand mit der stabilen Zeilen-Id. Abweichungen = sichtbarer Integritätsfehler, kein stiller Zustand.
  - **Neue Tests**: `SettlementIdentityTests` (6 Fälle: Teilbegleichung/Vollbegleichung gleichdatiert, rückdatierte Einnahme/Ausgabe, Zusammenführung zweier beglichener Zyklen, Storno-Ablehnung).
- **P2b — Herkunft der Finanzierung (explicit/implicit)** — Verteilungszeilen trugen keine Information, ob sie explizit (Benutzerangabe) oder implizit (aus dem Journal abgeleitet) zustande kamen.
  - **Fix**: `Booking.AllocationExplicit` (`bool?`, Migration `20261013072950_AllocationProvenance`; `null` = Buchungstyp ohne Verteilungszeilen oder Vor-Review-Daten): `true` = explizit, `false` = implizit. Teil des Original-Eingabeinhalts und der Idempotenzprüfung (explizite Wiederholung muss explizit gespeichert sein und umgekehrt), analog `TaxMarking.InheritedFromPreset`.
  - **Neue Tests**: `AllocationProvenanceTests` (5 Fälle inkl. Persistenz-Roundtrip über echten SQLite-Provider-Neustart und Idempotenz bei Wiederholung mit/ohne Zeilenangabe).
- **P2d — Upgrade-Backfill für bestehende Datenbanken** — In einer Datenbank, die vor Einführung der Positionsverwaltung lag, existierten offene Positionen nur im Journal und blieben unsichtbar.
  - **Fix**: `SettlementPositionBackfillService` läuft beim Host-Start nach der Migration und materialisiert fehlende **offene** Positionen (offene Episode in einer Zelle ohne persistierte Zeile), atomar und idempotent. Beglichene Episoden ohne Zeile bleiben unsichtbar (Historie).
  - **Neue Tests**: `SettlementBackfillTests` (2 Fälle: frische DB → 0 Zeilen; DB ohne Positionsverwaltung → 2 Zeilen, zweiter Lauf → 0).

### Runde 2 — alle Befunde behoben

- **P1a — Vollausgleich einer offenen Position durch die neue Buchung** (rückdatierte oder gleichdatierte Einnahme, kanonisch vor der Begründung sortiert): Der erste P1-Fix behandelte nur Teilbegleichung und Zusammenführung; der Vollausgleich lieferte `episodes.Count == 0` und wurde weiterhin als `LarioDomainException` abgelehnt.
  - **Fix**: Die gültige Einnahme wird gespeichert; die offene Positionszeile wird kontrolliert abgeschlossen (Status beglichen, Rest 0, Identität und Höhe bleiben, Begleichungsreferenz = die neue Buchung). Ist der Zyklus im Replay als jüngste beglichene Episode erhalten (Zufluss nach der Begründung), folgt die Zeile der Journalwahrheit (Begleichungsreferenz der kanonischen Reihenfolge). Die Query-Prüfung „offene Zeile ohne Episode“ bleibt als Sicherheitsnetz für fremde/korrupte Datenbanken.
  - **Neue Tests**: `BackdatedIncome_FullSettlement_SavesIncomeAndClosesRow` (Salden, abgeschlossene Historie, Neustart) und `SameDayIncome_SmallerId_FullSettlement_SavesIncomeAndClosesRow` (gleichdatierter Zufluss mit lexikografisch kleinerer Id).
- **P1b — `episodes[0]` statt der jüngsten Episode** — In `SettlementPositionSync.Plan` wählte der P1-Fix die offene Episode mit `episodes[0]`; bei Altdaten mit bereits abgeschlossener Episode ohne Zeile + neuer Ausgabe materialisierte er die falsche (beglichene) Episode als neue offene Zeile.
  - **Fix**: Die offene Episode ist per Ableitungsregel immer `episodes[^1]`; Paarung beglichener Episoden und Zeilen einheitlich von hinten (älteste Episoden ohne Zeile bleiben unsichtbare Historie) — auch in `SettlementQueryService`.
  - **Neuer Test**: `LegacyCell_NewExpense_CreatesExactlyOneOpenRow` (inkl. Nachweis, dass der P2d-Backfill in diesem Zustand 0 Zeilen legt).
- **R2 — Idempotenz vor Aktivitätsprüfung** — `TransferService`/`ReassignmentService` prüften bei impliziter Wiederholung die Aktivität der materialisierten Konten/Pools — eine identische Vorgabe wurde nach Archivierung als Konflikt verworfen statt idempotent bestätigt.
  - **Fix**: Idempotenz greift vor der Aktivitätsprüfung; die gehört zur tatsächlichen Neuanlage und bleibt dort.
  - **Neue Tests**: `Repeat_Implicit_AfterMaterializedPoolArchived_IsConfirmed` (Transfer), `Repeat_Implicit_AfterMaterializedAccountArchived_IsConfirmed` (Umwidmung).

### Runde 3 — alle Befunde behoben

- **P1 — Neuer Zyklus überschreibt abgeschlossene Position** — `SettlementPositionSync.Plan` und `SettlementQueryService` verwendeten die Regel „Zeilen ≥ Episoden: die jüngste Zeile trägt die offene Episode“. Startete ein **neuer** Zyklus auf einer Zelle mit beglichener Zeile (reine Addition: die vorherigen Episoden sind im neuen Zustand werteidentisch enthalten, die offene Episode ist angehängt), wurde die jüngste **beglichene** Zeile wieder geöffnet und trug den neuen Zyklus — die abgeschlossene Historie (Status, Begleichungsreferenz, Identität) ging verloren. Gleiches galt nach einem rückdatierten Vollausgleich (P1a): die kontrolliert abgeschlossene Zeile wurde vom nächsten Zyklus überschrieben.
  - **Fix (Übergangsanalyse)**: `SettlementPositionSync.Plan` leitet den Zellenzustand **vor und nach** der Buchung ab; die Form des Übergangs bestimmt die Änderung:
    - *offen → offen*: dieselbe Episode (vertieft/gekürzt/umgeordnet) — die offene Zeile trägt sie;
    - *offen → beglichen*: Vollausgleich — die offene Zeile wird kontrolliert abgeschlossen (P1a);
    - *beglichen → offen*: **reine Addition** → neuer Zyklus → **eigene Zeile mit neuem Id**; sonst (Buchung verlängert den jüngsten beglichenen Zyklus oder führt mehrere zusammen) → die jüngste Zeile trägt die offene Episode (wird sie beglichen, wird sie wieder geöffnet und behält ihre Identität; die älteren zusammengeführten Zeilen bleiben als beglichene Historie erhalten); ohne jede Zeile (Historie vor der Positionsverwaltung) wird ebenfalls eine neue angelegt;
    - *beglichen → beglichen*: die beglichene Historie bleibt unverändert.
    Neue Integritätsfehler (sichtbar, kein stiller Zustand): zwei offene Zeilen pro Zelle; offene Episode ohne offene Zeile; beglichene Episode, die nicht älter ist als die älteste Zeile, ohne werteidentische Zeile. Verwaiste Zeilen ohne passende Episode (zusammengeführte bzw. voll ausgeglichene Zyklen) bleiben legitime, unsichtbare Historie (R1/R3).
  - **Fix (Ansicht)**: `SettlementQueryService` führt dieselbe Paarung aus; die **offene** Ansicht zeigt den Journalzustand (Episode: Werte *und* Begründung) mit der stabilen Zeilen-Id, die **beglichene** Ansicht zeigt die persistierte Zeile (stabile Historie — auch wenn die zusammengeführte Episode eine andere Begründung trägt). Id und Stammdaten (Konto, Pool) kommen immer von der Zeile.
  - **Neue Tests (5, echte SQLite)**: `NewCycleAfterSettledEpisode_CreatesNewRow_KeepsOldSettled` (neue Zeile mit neuem Id, alte Zeile bleibt beglichen, stabil über Neustart), `NewCycleAfterBackdatedFullSettlement_CreatesNewRow_KeepsClosedHistory`, `NewCycleAfterMergeAndSettlement_CreatesThirdRow` (dritte Zeile; Ansicht zeigt alle drei mit den Zeilen-Identitäten), `LegacyEpisodeWithoutRow_NewCycle_ViewStaysConsistent` (Legacy bleibt unsichtbar), `TamperedSettledRow_UnchangedPrefix_FailsVisibly` (manipulierte beglichene Zeile in unverändertem Präfix: sichtbarer Integritätsfehler, Buchung wird verworfen).

---

*Stand: 13. Okt. 2026 — drei Review-Runden abgeschlossen, alle Befunde behoben, 282/282 Tests, 0 Build-Warnungen. Abnahme durch den Nutzer ausstehend (PR #6).*
