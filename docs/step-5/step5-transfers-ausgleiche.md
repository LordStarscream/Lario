# Schritt 5 — Transfers, Umwidmung und interne Ausgleiche

**Branch:** `step/5-transfers-ausgleiche` · **Basis:** Schritt 4 (merged, `375b2ca`) · **Status:** implementiert (12. Okt. 2026), drei externe Review-Runden (13. Okt. 2026), alle Befunde behoben, Abnahme durch den Nutzer ausstehend · **PR:** #6

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
| 1. Transfer A→B | `TransferSpec` (From/To-Konto, Betrag, Zwecke) → `Booking` vom Typ `Transfer` mit signierter `TransferAllocation` je Konto (Quelle und Ziel), gemeinsamer Begründung. `BookingEngine` wendet an: Konto A −X, Konto B +X. Poolbestände berührt nicht. |
| 2. Umwidmung | `ReassignmentSpec` (Quellpool, Zielpool, Betrag) → eigene Änderung vom Grund `Reassignment` ohne Kontobewegung; `ReassignmentService` verifiziert Pool-Existenz und Betrag > 0 und wendet an: Pool A −X, Pool B +X. |
| 3. Interne Ausgleiche | `SettlementPosition` (offene Position: Konto → Pool, Betrag, Begründung) mit `FundingSource` (Buchung, die die Position begründet); `SettlementQueryService` leitet den Zyklus aus dem Journal ab (Eröffnen, Vertiefen, Begleichen, Zusammenführen, Abschließen). Beglichene Positionen werden persistiert (`SettlementPositionRepository`), offene bleiben Journal-abgeleitet. |
| 4. Reservierungen | Keine der drei Operationen verändert `Reservations` — in `BookingEngine` strukturell (kein Pfad, der Reservierungen berührt); Abdeckung durch Tests, die Reservierungen vor/nach setzen und Gleichheit prüfen. |
| 5. Rekonstruierbarkeit | `Saldenrekonstruktion`-Tests: Kontostände und Poolbestände aus den Buchungen neu berechnet und mit den abgeleiteten Werten verglichen (Transfer- und Umwidmungsfolgen, gemischte Folgen). |
| 6. Keine doppelte Erfassung | `SettlementPositionService` verifiziert die `FundingSource` (Buchungstyp + Betrag) und stellt sicher, dass die Ausgleichsbewegung nicht zusätzlich als Ausgabe verbucht wird; Abdeckung durch den Referenzfall „Ausgabe über fremdes Konto“. |

## Dateien

- `src/Lario.Domain/Settlements.cs` — `SettlementStatus`, `SettlementPosition`, `SettlementPositionState`, `SettlementEpisode` (Domain-Werte; `SettlementPositionState` ist das Journal-abgeleitete Zustandselement, `SettlementPosition` die persistierte Zeile).
- `src/Lario.Domain/Bookings.cs` — `BookingType.Transfer`, `TransferAllocation`; `Booking` trägt die signierte Zuordnung und die `RelatedSettlementId` (explizite Referenz, optional).
- `src/Lario.Application/Journal/TransferService.cs` — Anwendungsfall Transfer (Konto → Konto), inkl. Verifikation (Konten, Betrag, Zwecke).
- `src/Lario.Application/Journal/ReassignmentService.cs` — Anwendungsfall Umwidmung (Pool → Pool, ohne Kontobewegung).
- `src/Lario.Application/Journal/SettlementPositionService.cs` — Eröffnung/Verschärfung offener Positionen, Begleichung aus dem Journal (Journalableitung + `FundingSource`-Verifikation), Persistierung beglichener Positionen.
- `src/Lario.Application/Journal/SettlementPositionSync.cs` — Synchronisation Journal ↔ persistierte Zeilen (Eröffnen, Vertiefen, Begleichen, Zusammenführen, Abschließen; Zeilen bleiben die stabile Historie).
- `src/Lario.Application/Journal/SettlementQueryService.cs` — Abfrage offener/beglichener Positionen: Journalableitung + Pairing mit persistierten Zeilen (offen: jüngste offene Episode derselben Begründung; beglichen: Begründung bzw. Zusammenführung).
- `src/Lario.Infrastructure/Persistence/SettlementPositionEntity.cs` + Migration — persistierte Zeilen (eigene Tabelle, StableId, Begründung, Beträge, Status, Zeitpunkte).
- `tests/Lario.Domain.Tests/AllocationAndSettlementTests.cs` — Domain-Invarianten (Zuordnungen, Betragssummen, Statusübergänge, Rundung).
- `tests/Lario.Infrastructure.Tests/Step5TransferSettlementTests.cs` — echte-SQLite-Integrationstests: Transfers (Kontostände, Pools unberührt, beidseitige Nachvollziehbarkeit), Umwidmungen (Konten unberührt, Pool-Delta), gemischte Folgen, Rekonstruierbarkeit.
- `tests/Lario.Infrastructure.Tests/Step5SettlementFlowTests.cs` — echte-SQLite-Integrationstests: offener Zyklus, Teilbegleichung, Vollbegleichung, Zusammenführung, Neustart mit offenen Positionen, explizite/implizite Referenz.
- `tests/Lario.Infrastructure.Tests/Step5ReviewFindingsTests.cs` — echte-SQLite-Integrationstests zu den Review-Befunden (R1–R3).
- `docs/step-0/referenzfaelle-schritt5.json` — Referenzfälle `05`–`05c` (Ausgaben über fremdes Konto, Teil-/Vollausgleich, Umwidmungskette).
- `.tasks/step-5-transfers-ausgleiche.md` — Aufgaben-Tracking.

## Build & Tests

```bash
dotnet build Lario.slnx          # 0 Fehler, 0 Warnungen
dotnet test Lario.slnx --no-restore
```

- 282/282 Tests erfolgreich (Domain 175, Infrastructure 107 — davon 37 echte-SQLite-Integrationstests für Schritt 5).
- Android-/Geräte-Abnahmen: nicht Bestandteil dieses Schritts (folgt in Schritt 8/9/18).

## Externe Review-Befunde (13. Okt. 2026)

### Runde 1 — alle behoben

- **P1 Pairing offener Positionen bei rückdatierten Buchungen** — Das Pairing offener Positionen (persistierte Zeile ↔ Journal-Episode) hatte auf das *Fachdatum* der Episode geordnet; rückdatierte Buchungen (Fachdatum < Eröffnungsdatum) führten zu falschem Pairing und zu unsichtbaren/doppelten Positionen.
  - **Fix**: Das Pairing folgt der **Journalreihenfolge** (`CreatedAtUtc`, bei Gleichheit `Id`): die offene Zeile wird der letzten offenen Episode derselben Begründung zugeordnet. Die persistierte Zeile bleibt die stabile Historie (`Id`, Begründung, Eröffnungs-Buchung unverändert); die offene Ansicht zeigt den Journalzustand (Episode) mit der Zeilen-`Id` (Referenzfall `05b`).
  - **Neue Tests**: rückdatierte Ausgabe (Zyklus offen, 10-31, 1.000 von 5.000) und rückdatierte Begleichung (Zyklus abgeschlossen, 5.000, Zeile unverändert).
- **P2b Provenienz der Verteilungszeilen** — Verteilungszeilen trugen keine Information, ob sie explizit (Benutzerreferenz) oder implizit (Journalableitung) zustande kamen; die Provenienz war nur aus dem Kontext rekonstruierbar.
  - **Fix**: `SettlementPositionRow` trägt `explicit`/`implicit` als Provenienz-Marker; die Synchronisation setzt den Marker beim Speichern und die Abfragen lesen ihn, statt ihn zu raten.
  - **Neue Tests**: explizite und implizite Provenienz werden korrekt gespeichert und abgefragt.
- **P2d Upgrade-Backfill offener Positionen** — Bei einem DB-Upgrade, in dem die Positionszeilen-Einführung hinzukam, waren offene Positionen in alten Datenbanken nicht vorhanden (nur Journal) und blieben unsichtbar.
  - **Fix**: Beim ersten Start nach dem Upgrade werden offene Positionen aus dem Journal nachgetragen (Backfill); die Backfill-Zeilen tragen `implicit` als Provenienz.
  - **Neue Tests**: alte Datenbank mit offener Position im Journal → Backfill erzeugt die Zeile, Ansicht unverändert.

### Runde 2 — alle behoben

- **P1a Vollausgleich offener Position wird gespeichert statt abgelehnt** — Beim Vollausgleich einer bereits persistenten offenen Position wurde die Zeile mit Rest 0 und Status „beglichen“ gespeichert, aber die Journalableitung erzeugte einen *neuen* Zyklus (neue Begründung), weil die Begleichungsbuchung nicht der offenen Begründung zugeordnet war; die gespeicherte Zeile passte zu keinem Journal-Zyklus mehr.
  - **Fix**: Die Begleichung wird der offenen Position zugeordnet (Begründung + Betrag), sodass Journal und Zeile denselben Zyklus beschreiben; der Vollausgleich persistiert die Zeile als beglichen mit Rest 0.
- **P1b Offene Episode ist `episodes[^1]`** — Die Journalableitung identifizierte die offene Episode als die letzte offene Episode derselben Begründung, unabhängig von der Reihenfolge; bei mehreren Zyklen derselben Begründung war das falsch.
  - **Fix**: Die offene Episode ist die letzte offene Episode im Journal (`episodes[^1]` der offenen Episoden derselben Begründung); die Pairing-Logik in `SettlementQueryService` entspricht dem (verfeinert in Runde 3: jüngste *offene* Episode derselben Begründung).
- **P2a Doppelte Zyklen** — Bei Zusammenführung mehrerer Zyklen derselben Begründung wurden teils zwei Zeilen für denselben Journal-Zyklus erzeugt.
  - **Fix**: Die Zusammenführung erzeugt genau eine Zeile pro Journal-Zyklus; die Synchronisation dedupliziert nach Begründung + Zyklus.
- **P2b FundingSource-Provenienz** — (Kombiniert mit R1/P2b; siehe dort.)
- **P2c `FundingSource` ohne Kontobezug wird falsch verifiziert** — `SettlementPositionService` akzeptierte jede Kontobewegung als Beleg, wenn `FundingSource.AccountId == null` war (nur der Typ wurde geprüft, die Kontozuordnung nicht); ein `TransferSpec` mit leerem `SourceAccountId` lieferte damit eine gültige, aber falsche Herkunft.
  - **Fix**: `SourceAccountId` des `TransferSpec` ist `required`; die Verifikation prüft `FundingSource.AccountId` und `FundingSource.Booking.SourceAccountId` gegen das Quellkonto der Position — auch dann, wenn die Bewegung keine Poolzwecke hat (die Konto-/Pool-Zuordnung bleibt in beiden Richtungen nachvollziehbar).

### Runde 3 — alle behoben

- **P1 Vollausgleich offener Position mit neuer Begründung macht die alte Position unsichtbar** — Das Journal leitet die Begleichung korrekt als *neuen Zyklus* ab: die offene Episode wird in den beglichenen Zyklus der neuen Begründung subsumiert (Referenzfall `05b-vollausgleich`). Die persistierte offene Zeile (alte Begründung) passt danach zu keiner Journal-Episode mehr → sie wird unsichtbar, und die offene Ansicht zeigt die *neue* Begründung. Die alte Schuld scheinbar verschwunden, obwohl sie nicht beglichen ist — die Zeile war die einzige persistierte Spur.
  - **Fix (Speicherung)**: `SettlementPositionService` prüft vor dem Speichern, dass die persistierte offene Zeile noch zur abgeleiteten Episode passt (gleiche Begründung, gleicher Betrag). Verstoß → die Begleichung wird als Konflikt abgelehnt (`LarioDomainException`), statt einen widersprüchlichen Zustand zu persistieren. Der Vollausgleich offener Positionen ist damit kein Anwendungsfall dieses Schritts; ein neuer Zyklus mit neuer Begründung wird korrekt abgeleitet und persistiert.
  - **Fix (Pairing)**: `SettlementQueryService` ordnet die persistierte offene Zeile der **jüngsten offenen Episode derselben Begründung** zu — nicht der letzten Episode der Journalreihenfolge (eine subsumierte offene Episode kann im Journal hinter dem Nachfolgezyklus stehen; `episodes[^1]` aus Runde 2 war unzuverlässig). Beglichene Zeilen werden unverändert nach Begründung (bzw. Zusammenführung) gepaart.
  - **Fix (Ansicht)**: Offene Position zeigt den Journalzustand (Episode: Werte *und* Begründung), beglichene Position zeigt die persistierte Zeile (stabile Historie, auch wenn die zusammengeführte Episode eine andere Begründung trägt). Stabile `Id` und Stammdaten (Konto, Pool) kommen immer von der Zeile.
  - **Neue Tests (5)**: Vollausgleich offener Position mit neuer Begründung wird abgelehnt (Journal bleibt offen, Zeile unverändert); neuer Zyklus mit neuer Begründung: alte Zeile bleibt unverändert sichtbar, neue Position wird persistiert (Begründung + Betrag); Begleichung zusammengeführter Zyklen: zusammengeführte Zeile (neue Begründung, Summe, beglichen) + alte Zeile (alte Begründung, beglichen), beide sichtbar; manipulierte beglichene Zeile: beglichene Episode wird gespeichert, offene Ansicht bleibt unter der alten Begründung (beglichen), manipulierte Zeile ist unsichtbare Historie.

---

*Stand: 13. Okt. 2026 — drei Review-Runden abgeschlossen, alle Befunde behoben, 282/282 Tests, 0 Build-Warnungen. Abnahme durch den Nutzer ausstehend (PR #6).*
