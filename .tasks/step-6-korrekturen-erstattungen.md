# Schritt 6 – Korrekturen, Erstattungen und Kontostandsabgleich

**Status:** aktiv
**Branch:** `step/6-korrekturen-erstattungen`
**Voraussetzung:** Schritt 5 (abgeschlossen, merged `a004d10`)
**Plan:** `docs/Finanzplanung_Implementierungsplan.md` §5 „Schritt 6"
**Spezifikation:** `docs/step-0/rechenvertrag.md` §2.4 (Beständigkeit), §3.3 (Storno-Postings), §3.8 (Sonderfälle), §5.4/5.5 (Revision/expectedBaseRevision)

> **Nebenwunsch (14.10., erledigt):** Smoke-Tests auf dem gemergten Stand (main @ `0859cf9`) — Anleitung in `docs/smoke-tests-schritt-5.md` (Helfer `tools/lario-snapshot.sh`). **Alle Tests am 14.10. durchgelaufen und bestanden** (inkl. Test 2 Stop/Neustart-Datenintactheit und Test 3 Android: Einträge/geräte-ID/Verbindung).

## Ziel

Fehlerhafte bestätigte Buchungen können **korrigiert oder storniert** werden — nie unbemerkt überschrieben. Echte Händlererstattungen fließen am Erstattungszeitpunkt zum ursprünglichen Pool zurück (auch teilweise). Manuelle Bankstandsabgleich-Differenzen werden als dokumentierte Kontostandskorrektur gebucht, nicht als normales Gehalt. Revisionskonflikte werden als Fehler zurückgegeben, kein Last-Write-Wins. Auswirkungen auf Splits und offene/beglichene Ausgleiche werden atomar nachgeführt.

**Fertig-Kriterium (Plan):** Fehlerhafte Eingaben können korrigiert werden, ohne Salden manuell zu überschreiben.

## Abnahmekriterien aus dem Plan (verbindlich, als Tests umsetzen)

| # | Szenario | Erwartetes Ergebnis |
|---|---|---|
| A1 | Ausgabe 85 € (mit Splits) auf 80 € korrigieren, Splits passend anpassen | Keine zweite Ausgabe; neue Split-Summe exakt 80 €; alte Postings nachvollziehbar über Revision; Salden korrekt |
| A2 | Teilrückzahlung 10 € einer Ausgabe | Konto steigt um 10 €, Ursprungspool steigt um 10 €; verknüpft mit Originalausgabe; Erstattungstag als Tatsachdatum |
| A3 | Änderung einer Buchung, deren negativer Ausgleich bereits beglichen war | Erzeugt einen **nachvollziehbaren neuen** Ausgleichsbedarf (keine stille Änderung alter Position) |
| A4 | Abgleichdifferenz per Kontostandskorrektur buchen | Konto wird auf tatsächlichen Stand korrigiert; Differenz mit expliziter Pool-Zuordnung und Begründung; **wird nicht als normales Gehalt/Einkommen gezählt** |

Zusätzliche Pflichtfälle (aus AGENTS.md/Rechenvertrag):
- B1: Storno einer Ausgabe = Gegenbewegung auf Konto und Pool, verknüpft mit Original; Auswertungen nachvollziehbar; doppelte Storno abgelehnt.
- B2: Storno ≠ Erstattung — Erstattung auf stornierte (oder bereits korrigierte) Buchung ablehnen bzw. klar trennen.
- B3: Erstattungsbetrag > verbleibender Splitbetrag des Ursprungspools wird abgelehnt (max. Splitbetrag).
- B4: Revisionskonflikt (expectedBaseRevision ≠ aktuelle Revision) bei Korrektur/Storno/Erstattung → Fehler, kein Partial State, keine Revisionserhöhung.
- B5: Alle Änderungen atomar (echte SQLite): Abbruch hinterlässt keine halbe Buchung; Zeilen-/Spaltensummen der Account-Pool-Zuordnung bleiben intakt.
- B6: Korrektur ändert keine historischen Steuerkennzeichnungen anderer Buchungen; die Steuerkennzeichnung der korrigierten Buchung bleibt an den (angepassten) Splits nachvollziehbar (Markierter Teilbetrag ≤ Splitbetrag).

## Design-Entscheidungen (final, 15. Okt. 2026 — validiert gegen Ist-Code)

Vollständiges Design-Paket: `.tasks/step-6-design-paket.md` (Status: final). Kern:

- **Korrektur = In-place** (keine zweite Buchung; `BaseRevision` 1→2→…), Nachvollziehbarkeit über neue Tabelle `BookingCorrections` (PK `OperationId` = Idempotenzschlüssel, Vorher/Nachher-Snapshot, atomar mit der Änderung).
- **Storno** = neue Buchung `BookingType.Reversal` (8) mit negierten Postings + explizit gespeicherten `ReversalDeltas` (Negation von `Original.AllocationDelta()`, kein Cross-Booking-Lookup im Replay) + `ReversesBookingId`; Original-Revision++ atomar. Doppelstorno schlägt als Revisionskonflikt fehl; Widerruf = neues Storno auf die Storno-Buchung.
- **Erstattung** = eigene Buchung `BookingType.Refund` (9), Konto wählbar (auch anderes), Rückfluss zum Ursprungspool, je-Pool-Deckelung (bereits erteilte + neu ≤ Splitbetrag); kein Revisionseffekt auf die Ausgabe; keine Einnahme.
- **Kontostandskorrektur** = `BookingType.BalanceCorrection` (10): genau ein Konto-Posting ≠ 0, keine Pools, Notiz Pflicht; `TotalIncome()` zählt nur `Income` → bleibt unverändert.
- **Enum-Werte** sind seit Schritt 0 in `BookingType` festgelegt (Reversal=8, Refund=9, BalanceCorrection=10; 7=Closeout belegt) — keine neuen Werte.
- **Revision:** `ExpectedBaseRevision` als nicht-nullbare Pflicht bei `CorrectionSpec`/`ReversalSpec`; Abweichung → `DomainConflictException`, um optionale Properties `ExpectedBaseRevision`/`ActualBaseRevision` erweitert.
- **Idempotenz zuerst** (vor Revision/Katalog): Korrektur über `OperationId` in der Historie; Storno/Erstattung/Abgleich über die stabile Buchungs-`Id` (Muster der Schritt-4/5-Services).
- **Begleichungsnachführung:** Verallgemeinerung `SettlementPositionSync.Plan(before, after, …)` (reine Präfix-Identität statt Count-Check; schließende Buchung aus dem Nachher-Replay statt fix `newBooking`); R6-VORHER-Integritätscheck und Historien-Abgleich unverändert.
- **Validierung:** neue Typen werden in `ValidateTypeSpecific()` (Konstrutor) geprüft — es gibt keine öffentliche `Validate()`.
- **Scope-Abschluss:** REST-Endpunkte + Exception→HTTP-Mapping (409/422/400) gehören zu Schritt 7 (Plan §5: „Bereits bestätigte Buchungen über definierte Korrekturanwendungsfälle ändern"); Schritt 6 = Domain + Application + Infrastructure + Tests.

## Tasks

- [x] T1: Design festhalten (Korrektur/Storno-Modell, Erstattungs-Modell, Kontostandskorrektur-Modell, Revision, Ausgleichsnachführung) — in dieser Datei + bei Bedarf kurze Doku `docs/step-6/`
  > - 15. Okt. 2026: Design-Paket `.tasks/step-6-design-paket.md` gegen Ist-Code validiert (Analyse-Agent; Stichproben verifiziert) und finalisiert.
  > - Korrigierte Abweichungen: (1) Enum-Werte Refund/Reversal/BalanceCorrection = 9/8/10 existieren seit Schritt 0 — Design-Nummerierung (7/8/9) war falsch, keine neuen Werte nötig; (2) Validierung läuft in `ValidateTypeSpecific()` im Konstrutor, keine öffentliche `Validate()`; (3) `DomainConflictException` trägt bisher nur Message → um optionale Expected/Actual-Properties erweitern; (4) Host-§4 (REST-Endpunkte, Exception-Mapping) gehört zu Schritt 7 — aus Schritt-6-Scope herausgenommen (Plan §5 bestätigt); (5) `ReversalDeltas` ohne `OwnsMany`, Muster der bestehenden Posting-Tabellen (Schatten-FK, Komposit-Key, Cascade); (6) Migrations-Assembly ist der Default `Lario.Infrastructure` (Bezeichner `LarioMigrationsAssembly` existiert nicht).
- [x] T2: Domain: Buchungstypen/Specs erweitern (CorrectionSpec, ReversalSpec/Storno, RefundSpec/Erstattung, BalanceCorrectionSpec) inkl. Invarianten und Tests (xUnit, Referenzzahlen)
  > Specs + `ValidateTypeSpecific()`-Invarianten + `ToBooking`/`BuildUpdatedBooking` sind implementiert und gegen Ist-Code validiert. `tests/Lario.Domain.Tests/Step6SpecsTests.cs` (22 Tests, Referenzzahlen 85 € / 10 € / 5 €) — alle bestanden.
- [x] T3: Application: Anwendungsfälle Korrektur, Storno, Erstattung, Kontostandskorrektur inkl. expectedBaseRevision-Prüfung und atomarer Ausgleichsnachführung
  > 15. Okt. 2026: `CorrectionService`, `ReversalService`, `RefundService`, `BalanceCorrectionService` implementiert (Idempotenz zuerst, Revisionskonflikt → `DomainConflictException`, Doppelstorno-Ablehnung, Erstattungs-Deckelung je Pool, atomare Begleichungsnachführung über `SettlementPositionSync.Plan`). DI registriert, Build grün, 291 Existing-Tests bestehen.
- [x] T4: Infrastructure: Persistenz/EF-Mapping + Migration für neue Buchungstypen/Verknüpfungen
  > Migration `20261014101434_Step6CorrectionsStornos` (ReversalDeltas, BookingCorrections) existiert; `BookingRepository` trägt `AddWithOriginalRevisionAsync` (Storno + Original-Revision++ atomar), `UpdateAsync` (In-place-Korrektur + Historie + Positionseffekte atomar), `FindCorrectionAsync`; `Migrate_CreatesAllTables` testet die neuen Tabellen. 291 Existing-Tests (inkl. echter SQLite) bestehen.
- [x] T5: Integrationstests (echte SQLite): A1–A4, B1–B6
  > 15. Okt. 2026: `tests/Lario.Infrastructure.Tests/Step6CorrectionsReversalsTests.cs` (13 Tests, echte SQLite, Referenzzahlen 85 € / 10 € / 5 € / 20 €) — alle bestanden. A1 (Korrektur In-place, Revision 1→2, keine zweite Ausgabe, Vorher/Nachher-Snapshot 85 €→80 €), A2 (Teilrückzahlung 10 €, Konto +10 € und Ursprungspool +10 €, verknüpft, Erstattungstag = Tatsachdatum, kein Einkommen, Original unangetastet), A3 (Korrektur beglichener Ausgabe 20 €→25 € → nachvollziehbarer neuer Ausgleichsbedarf 5 €, Begründung = korrigierte Ausgabe, Historie), A4 (Kontostandskorrektur 5 €, Konto korrigiert, kein Einkommen, Differenz sichtbar als Konto-Pool-Lücke + Notiz, Invarianten intakt), B1 (Storno = Gegenbewegung, verknüpft, ReversalDeltas = Negation, Original-Revision++; Doppelstorno als Revisionskonflikt abgelehnt; Widerruf = Storno des Stornos erlaubt), B2 (Erstattung auf stornierte Ausgabe abgelehnt), B3 (Erstattung > Splitbetrag abgelehnt; Teilrückzahlungen akkumulieren bis zur je-Pool-Deckelung), B4 (Revisionskonflikt bei Korrektur → kein Partial State, keine Revisionserhöhung, Salden intakt), B5 (Atomarität: abgelehnte Operation verändert nichts, Zeilen-/Spaltensummen intakt), B6 (Steuerkennzeichnung bleibt an den angepassten Splits, markierter Teilbetrag ≤ Splitbetrag, andere Buchungen unangetastet).
  > **Domain-Änderung (Design-Erfordernis, kein Test-Workaround):** `BookingJournal.AllocationInvariantViolations()` behandelt den Rest aus Kontostandskorrekturen als nicht zugeteilten Rest (neben dem nicht zugeordneten Anfangsbestands-Rest). Die Abgleichskorrektur trägt genau ein Konto-Posting ohne Pool-Zelle (Design Schritt 6 §2.6); ihre Differenz muss die Zeilensummen-Invariante ausgleichen, damit A4 „Journal-Invarianten bleiben intakt" erfüllt ist. Neuer privater Helper `BalanceCorrectionRemainderOf()`. Keine Auswirkungen auf bestehende Tests (keine Abgleichskorrekturen → Rest 0); 326/326 Tests grün, 0 Build-Warnungen.
- [x] T6: Build + Gesamtttest grün (0 neue Warnungen), Doku-Auslieferung, Abnahmedoku `docs/step-6/`
  > 15. Okt. 2026: Build 0 Fehler / 0 Warnungen; 326/326 Tests grün (197 Domain, 129 Infrastructure). Abnahmedoku `docs/step-6/step6-korrekturen-erstattungen.md` (Abnahmekriterien A1–A4 + B1–B6, Umsetzungstabelle, Dateien, Build & Tests). Vor-Commit-Review des Diffs: keine unbeabsichtigten Änderungen (verifiziert: `SettlementPositionSync`/`BookingJournal`-Änderungen entsprechen Design §3.2/§3.3; `DomainConflictException`-Erweiterung = T1-Punkt (3); DI-Registrierung der vier Services).
- [x] T7: Review-Runden bis alle Befunde behoben (externes Review), dann Commit/Push/PR
  > 15. Okt. 2026: 6 Commits auf `step/6-korrekturen-erstattungen` (smoke-Test-Nachtrag, domain, application, infrastructure, test, docs+tasks), gepusht, **PR #8** angelegt. Übergabe: `docs/step-6/step6-korrekturen-erstattungen.md` (Abnahmedoku) + PR-Beschreibung; Prüfpunkte für das Review: (1) Sync-Verallgemeinerung (neue `Plan`-Überladung in-place, R6-Struktur unverändert), (2) Storno-Replay über explizite `ReversalDeltas`, (3) Invarianten-Änderung `BookingJournal` (Abgleichs-Rest als nicht zugeteilter Rest), (4) Idempotenz-Reihenfolge (zuerst Idempotenz, dann Revision), (5) Deckelungslogik `RefundService`.
  > **Review R1 (14. Okt. 2026) — alle Befunde behoben, gepusht (Commit `a9d7e40`), PR #8 aktualisiert:**
  > - **P1 Optimistische Synchronisation:** `BaseRevision` ist jetzt EF-Concurrency-Token (`IsConcurrencyToken`); die Erwartungsrevision wird **vor** der Mutation aus dem Original erfasst (vorher nach dem `Save` → jede zweite Operation war immer ein falscher Revisionskonflikt). `BookingRepository.UpdateAsync`/`AddWithOriginalRevisionAsync`.
  > - **P1 Storno eines Transfers mit Pool-Zweck:** poolneutrale Deltas (von −x / zu +x auf demselben Pool, Deltasumme 0 ohne Pool-Posting) werden akzeptiert (`Booking.ValidateReversalDeltasBalance` — die „ungleich 0 Netto-Posting"-Prüfung entfernt, Balancierung der Deltasumme bleibt).
  > - **P2 Wirksamer Storno-Status über die Storno-Historie:** `BookingJournal.IsEffectivelyReversed` zählt die Parität aller wirksamen Stornos **rekursiv** (ein Storno wirkt, solange es nicht selbst wirksam widerrufen ist) statt nur die erste Kette; Doppelstorno-Prüfung in Reversal-/Korrektur-/Erstattungs-Service.
  > - **P2 Idempotenznachweis:** kanonischer Original-Payload in `BookingCorrection.Payload` (Migration `Step6CorrectionPayload`); Wiederholung derselben Operation wird nur quittet, wenn der Payload exakt entspricht; gleiche OperationId mit anderem Inhalt = Konflikt.
  > - **P2 Erstattungs-Deckelung bei Korrektur:** `CorrectionService.CheckRefundCoverage` stellt sicher, dass die korrigierten Splitbeträge die bereits erteilten Erstattungen decken.
  > - Neu: `Step6ReviewR1Tests` (10 Tests, echte SQLite) fangen alle Befunde; zusätzlich Test-Fehlerkorrekturen (Referenzzahlen, Scope-Wiederverwendung, Id-Kollision `bk-c7-r2`).
  > - **Status: 336/336 Tests (197 Domain, 139 Infrastructure), 0 Build-Warnungen.** Zweite Review-Runde (R2) ausstehend — Übergabe: PR #8 + dieser Befund-Nachtrag.

## Bewusst zurückgestellt (nicht in Schritt 6)

- Erstattung auf abgeschlossenes **Vorhaben** → Herkunftspool (Vorhaben existiert erst ab Schritt 14; bis dahin: Rückfluss zum Ursprungspool des Splits)
- UI-Formulare für Korrektur/Storno/Erstattung (ab Schritt 7, API + Desktop)
- Sync-Transport für diese Vorgänge (Schritt 10: Sync von Änderungen und Konflikten)
- Wiederkehrende Regeln/PlannedOccurrence (Schritt 11)
- Auswertungslogik Netto-Brutto-Erstattungsanzeige (Schritt 12/16) — Schritt 6 liefert nur die Datenbasis (Erstattung verknüpft + getrennter Buchungstyp)
