# Schritt 6 — Korrekturen, Erstattungen und Kontostandsabgleich

**Branch:** `step/6-korrekturen-erstattungen` · **Basis:** Schritt 5 (merged, `a004d10`) · **Status:** implementiert (15. Okt. 2026); Review R1 und R2 vollständig beantwortet (14./15. Okt. 2026), 340/340 Tests
**Spezifikation:** `docs/step-0/rechenvertrag.md` (§2.1 Splitregel, §3.3 Postings, §3.5 offene Ausgleichspositionen)
**Design:** `.tasks/step-6-design-paket.md` (verbindliches Design-Paket, 15. Okt. 2026)
**Referenzfälle:** A1–A4 aus `docs/Finanzplanung_Implementierungsplan.md` §5 Schritt 6 („Prüfen"), Pflichtfälle B1–B6 aus dem Design-Paket

## Abnahmekriterien (aus dem Plan)

1. **A1 — Korrektur:** Ausgabe 85 € auf 80 € korrigieren, Splits passend anpassen (65/20 → 60/20); **keine zweite Ausgabe**. Die Buchung behält Id, Typ und Fachdatum; Konto- und Pool-Salden passen zum neuen Gesamtbetrag; die Änderung ist in der Korrektur-Historie mit Vorher-/Nachher-Snapshot nachvollziehbar.
2. **A2 — Teilrückzahlung:** Händlererstattung 10 € auf eine Ausgabe von 85 €: das Konto (auch ein anderes als das Ursprungskonto) und der Ursprungspool steigen um 10 €. Die Erstattung ist eine eigene, mit der Ausgabe verknüpfte Buchung am Erstattungstag — **kein Einkommen**, keine Kategorie.
3. **A3 — Änderung nach beglichener Episode:** Eine Änderung (Korrektur) nach bereits beglichener Ausgleichsepisode erzeugt einen **nachvollziehbaren neuen Ausgleichsbedarf** (offene Position mit Begründung auf die korrigierte Buchung) — kein stiller Salde-Übertrag.
4. **A4 — Kontostandskorrektur:** Die Abgleichdifferenz wird als dokumentierte Buchung (Notiz Pflicht) verbucht, **nicht als normales Gehalt/Einkommen** gezählt; die Differenz bleibt als Konto-Pool-Lücke sichtbar, die Journal-Invarianten bleiben intakt.

### Pflichtfälle (Design-Paket)

- **B1** Storno = exakte Gegenbewegung (verknüpft, Salden exakt negiert); Doppelstorno abgelehnt; Widerruf (Storno des Stornos) erlaubt.
- **B2** Erstattung auf stornierte Ausgabe abgelehnt (kein Ursprung mehr).
- **B3** Erstattung über den je-Pool-Splitbetrag abgelehnt; Teilrückzahlungen akkumulieren bis zur Deckelung.
- **B4** Revisionskonflikt bei Korrektur (zwei parallele Korrekturen): zweite wird abgelehnt, **kein Partial State**, keine Revisionserhöhung.
- **B5** Atomarität: abgelehnte Operation verändert nichts; Zeilen-/Spaltensummen-Invarianten intakt.
- **B6** Steuerkennzeichnung bleibt an den angepassten Splits erhalten (markierter Teilbetrag ≤ Splitbetrag), andere Buchungen unangetastet.

## Umsetzung

| Kriterium | Umsetzung |
|---|---|
| A1 Korrektur | `CorrectionSpec` (optionale Felder; null = unverändert, Splits/Allocations wie Neuanlage validiert) → `CorrectionService.CorrectAsync`: Original laden, Idempotenz über `OperationId` (gleicher Inhalt → Bestätigung, anderer Inhalt → Konflikt), `ExpectedBaseRevision`-Prüfung (Konflikt mit erwartetem + Ist-Wert), **in-place** Update derselben Buchung (Id/Typ/Fachdatum erhalten, `BaseRevision`++), Postings/Teilposten ersetzt, `BookingCorrection`-Historieneintrag (Old-/New-Snapshot) — alles in **einer Transaktion**. Keine zweite Buchung. |
| A2 Erstattung | `RefundSpec` (Konto wählbar, Zeilen je Ursprungspool) → `RefundService.RegisterAsync`: neue Buchung `BookingType.Refund` mit `RefundsExpenseId`, Konto-Posting +Gesamtbetrag, Pool-Posting je Zeile, **je-Pool-Deckelung** gegen Splitbetrag minus bereits erteilte Erstattungen (aus dem Journal). Kein Einkommen, keine Kategorie. |
| A3 Ausgleichsnachführung | `SettlementPositionSync.Plan` verallgemeinert (Design §3.3): neue Kernsignatur für den Übergang „Buchung wird in-place korrigiert“ (vorher = Journal mit altem Inhalt, nachher = Journal mit neuem Inhalt, dieselbe Id). Die R6-Struktur (Übergangsanalyse, VORHER-Integritätscheck, Upsert je Zeile) bleibt unverändert; A3: 20 €→25 € korrigierte beglichene Ausgabe → neue offene Position 5 € mit Begründung = korrigierte Buchung. |
| A4 Kontostandskorrektur | `BalanceCorrectionSpec` (Betrag ≠ 0, Notiz Pflicht) → `BalanceCorrectionService.CorrectBalanceAsync`: Buchung `BookingType.BalanceCorrection` mit **genau einem Konto-Posting** und keinem Pool-Posting; `BookingJournal` führt den Rest als „nicht zugeteilten Rest“ (analog Anfangsbestands-Rest) — Zeilen-/Spaltensummen-Invarianten bleiben intakt, die Lücke ist sichtbar. |
| B1 Storno | `ReversalSpec` → `ReversalService.ReverseAsync`: Storno ist eine **neue Buchung** `BookingType.Reversal` (eigene Id/Fachdatum, rückdatiert zulässig) mit exakt negierten Postings und `ReversalDeltas` = Negation des Original-Deltas (explizit persistiert, damit der Replay den Effekt sieht); `ReversesBookingId`-Verknüpfung; Original-Revision++ atomar mit dem Storno. Stornierbar: Expense, Income, AccountTransfer, Reassignment, TransferReassignment, Reversal (nicht: OpeningBalance, Settlement, Refund, BalanceCorrection). |
| B2/B3 Deckelung | `RefundService` prüft vor der Anlage: Pools ⊆ Ursprungspools, Summe je Pool ≤ Splitbetrag − bereits erteilt. Erstattung auf stornierter Ausgabe: das Original ist storniert (Salden negiert) — die Deckelung greift (bereits erteilt + neu > 0 Splitbetrag bzw. storniertes Original wird abgelehnt). |
| B4 Revisionskonflikt | Alle vier Services: `ExpectedBaseRevision != Ist` → `DomainConflictException` mit erwartetem und Ist-Wert, **vor** jeder Änderung; keine partielle Übernahme (SaveChanges nur auf dem Erfolgs-Pfad). |
| B5 Atomarität | Jede Operation = eine EF-Transaktion (Buchung + Postings + Positionszeilen + Historieneintrag); `dotnet ef`-Migration `20261014101434_Step6CorrectionsStornos`; Integrationstests prüfen Invarianten nach abgelehnten Operationen. |
| B6 Steuerkennzeichnung | Korrektur ersetzt Splits nur, wenn Splits mitgeliefert werden (sonst unverändert); mitgelieferte Splits werden wie bei Neuanlage validiert (inkl. `TaxMarking`-Materialisierung aus dem Katalog); markierter Teilbetrag ≤ Splitbetrag bleibt Invariante der `ExpenseSplit`-Validierung. |

## Dateien

**Domain** (`src/Lario.Domain/Journal/`):
- `CorrectionSpec.cs`, `ReversalSpec.cs`, `RefundSpec.cs`, `BalanceCorrectionSpec.cs` — neue Spezifikationen (Validierung, Materialisierung)
- `Booking.cs` — neue Buchungstypen Refund (7)/Reversal (8)/BalanceCorrection (9), `BaseRevision`, `ReversesBookingId`, `RefundsExpenseId`, `ReversalDeltas`; `AllocationDelta()`: Reversal → gespeicherte `ReversalDeltas`
- `BookingCorrection.cs` — neue Entität (Korrektur-Historie: OperationId, Old-/New-Snapshot, Notiz, Zeitstempel)
- `BookingJournal.cs` — Invariantenprüfung: Rest aus Kontostandskorrekturen zählt als nicht zugeteilter Rest (Design §2.6)

**Application** (`src/Lario.Application/Journal/`):
- `CorrectionService.cs`, `ReversalService.cs`, `RefundService.cs`, `BalanceCorrectionService.cs` — Anwendungsfälle (Idempotenz zuerst, dann Revision, dann Anlage/Änderung; atomare Positions- und Revisionseffekte)
- `SettlementPositionSync.cs` — neue `Plan`-Überladung für in-place Korrekturen (Design §3.3; bestehende Signatur bleibt dünne Hülle, Verhalten unverändert)
- `IBookingRepository.cs` — `FindCorrectionAsync`, `UpdateAsync` (atomar Buchung + Historie)

**Infrastructure**:
- `LarioDbContext.cs` — EF-Mapping: `ReversalDeltas` (eigene Tabelle, Composite Key, Cascade), `BookingCorrections` (PK OperationId, Restrict auf Booking), `ReversesBookingId`/`RefundsExpenseId` als Referenzspalten ohne FK (Existenzprüfung liegt beim Service)
- `ReversalDeltaRow.cs` — Zeilendarstellung der Reversal-Deltas
- `BookingRepository.cs` — Laden/Speichern der Reversal-Deltas und der Historie
- `Migrations/20261014101434_Step6CorrectionsStornos.cs` — Schema-Migration

**Tests**:
- `tests/Lario.Domain.Tests/Step6SpecsTests.cs` — 22 Tests: Spezifikations-Validierung, Storno-Negation (Postings + Deltas), Korrektur-Aufbau (In-place, Revision), Erstattungs-Zeilen, Abgleichs-Spezifikation, Rejection-Fälle
- `tests/Lario.Infrastructure.Tests/Step6CorrectionsReversalsTests.cs` — 13 echte-SQLite-Integrationstests: A1–A4 und B1–B6 mit den Plan-Referenzzahlen (85/80/10/5/20/25 €) inkl. Idempotenz- und Konflikt-Wiederholungen

## Build & Tests

```bash
dotnet build Lario.slnx
dotnet test Lario.slnx --no-restore
```

- 326/326 Tests erfolgreich (197 Domain, 129 Infrastructure; davon 13 echte-SQLite-Integrationstests für Schritt 6).
- REST-Endpunkte: ausweislich des Designs **Schritt 7** (dieser Schritt liefert Domain/Application/Infrastructure; die Mobile-App und die API nutzen die Services über die Endpunkte).
- Android-/Geräte-Abnahmen: nicht Bestandteil dieses Schritts (folgt in Schritt 8/9/18).

## Externe Review-Befunde

### Runde 1 (14. Okt. 2026) — alle Befunde behoben (Commit `a9d7e40`)

| # | Befund | Umsetzung |
|---|---|---|
| P1 | Revision nur als Concurrency-Token: zwei Kontexte lesen dieselbe Revision, beide committen → beide Updates greifen, zwei Buchungen mit derselben erwarteten Revision | `BaseRevision` ist ein EF-RowVersion-Äquivalent (CAS im UPDATE): parallele Mutation schlägt als `DbUpdateConcurrencyException` fehl und rollt Buchung, Historie und Positionen gemeinsam zurück — als `DomainConflictException` mit erwartetem/Ist-Wert sichtbar. Erwartete Revision wird **vor** der Mutation erfasst (sonst meldet der Konflikt die bereits erhöhte Revision). |
| P1 | Storno eines Transfers mit Zweckverteilung erzeugt inkonsistente poolneutrale Deltas | Poolneutrale Deltas (Postingsumme 0) werden konsistent gebildet und geprüft. |
| P2 | Wirksamer Storno-Status nicht über die Kette nachvollziehbar | `BookingJournal.IsEffectivelyReversed` rekursiv über die Storno-Kette (Parität) — ein widerrufenes Storno zählt nicht mehr. |
| P2 | Idempotenz-Payload unvollständig (Steuer-/Splitinhalt) | Payload-Nachweis umfasst den vollständigen fachlichen Inhalt; gleicher OperationId mit anderem Inhalt ist ein Konflikt. |
| P2 | Erstattung übersteigt bei Korrektur den Deckel (bereits gewährte Erstattung nicht berücksichtigt) | Korrektur wird abgelehnt, wenn ein Split unter bereits gewährte Erstattungen sinkt; Erstattungen kumulieren nie über den Deckel. |

Neu: `Step6ReviewR1Tests` (10 Tests, echte SQLite; zwei-Kontext-Szenarien mit Barriere nach dem Lesen). Status nach R1: 336/336 Tests, 0 Build-Warnungen.

### Runde 2 (15. Okt. 2026) — alle Befunde behoben

| # | Befund | Umsetzung |
|---|---|---|
| B1 (P1) | **Storno-Kette bei Widerruf:** Ein Storno eines Stornos (Widerruf) reaktiviert den Ast darüber. (a) Neben einem neuen Storno des Wurzeloriginals erzeugt er ein zweites wirksames Storno derselben Buchung — im Status pariert sich das zu „nicht storniert“, im Saldo verschiebt es um den Stornobetrag. (b) Nach einer In-place-Korrektur des Wurzeloriginals bildet der Ast den alten Inhalt ab — seine Reaktivierung erzeugt Geld aus dem Nichts (Referenz: E 100→80, R(100)+W(−100)+X(+100) = +20). (c) Zwei Widerrufe desselben Stornos gabeln die Kette und machen den wirksamen Status mehrdeutig. | `ReversalService.CheckReversalChainConsistency` prüft den künftigen Zustand (aktuelles Journal + neue Buchung) vor jedem Widerruf: (1) **Linearität** — ein Storno trägt höchstens ein Storno; (2) **Höchstens ein wirksames Storno je Kettenglied** (insbesondere am Wurzeloriginal); (3) **Frische** — jedes wirksame Storno des Wurzeloriginals muss dessen aktuellen Inhalt abbilden (Kontoposten und Pool-Deltas, negiert, mengenweise verglichen — `MatchesCurrentContent`). Verletzt: `DomainConflictException` mit Hinweis, das Wurzeloriginal neu zu stornieren (das übernimmt den aktuellen Inhalt). |
| B2 (P1) | **Ketten-Revision:** Ein Storno erhöhte nur die Revision des direkten Originals. Ein Kontext, der ein Kettenglied (und damit dessen wirksamen Status) vor dem Commit eines parallelen Stornos tiefer in der Kette gelesen hat, committet trotzdem — der veraltete wirksame Status wird akzeptiert und erzeugt genau den B1-Zustand. | "Wirksamer Status + Revision" bilden ein konsistentes Aggregat: `BookingRepository.AddWithOriginalRevisionAsync` erhöht in derselben Transaktion das Original **und alle Kettenvorfahren** (wenn es selbst ein Storno ist: dessen Original, … bis zum Wurzeloriginal) um 1 — jedes mit eigenem Concurrency-Token. Ein paralleler Commit auf Basis eines veralteten Kettenglieds erhält einen Revisionskonflikt (gemeldet wird das Kettenglied, dessen CAS tatsächlich fehlgeschlagen ist) und wird vollständig zurückgerollt. |

Neu: `Step6ReviewR2Tests` (4 Tests, echte SQLite): veralteter Ast nach Korrektur (inkl. dem vorgeschlagenen Weg — neues Storno des Wurzeloriginals), doppeltes wirksames Storno, Linearität, paralleler Widerruf in der Kette (zwei Kontexte, Barriere nach dem Lesen). Alle vier Tests schlagen auf dem Stand vor R2 fehl und bestanden auf dem korrigierten Stand. Status nach R2: **340/340 Tests (197 Domain, 143 Infrastructure), 0 Build-Warnungen.**
