# Aufgabe: Schritt 4 – Einnahmen, Ausgaben und Splits im Kern

**Status:** [abgeschlossen — wartet auf Review] (12. Okt. 2026)
**Branch:** `step/4-einnahmen-ausgaben`
**Plan:** `docs/Finanzplanung_Implementierungsplan.md` §5 „Schritt 4“, §9 „Steuerkategorien, relevante Zahlungen und Nachweise“
**Verbindliche Spezifikation:** `docs/step-0/rechenvertrag.md` (§2.2/2.3 Finanzierungsmonat, §3.2–3.4 Buchungen/Postings, §3.5 Splitregel, §5.5 Revision)
**Testfixtures:** `docs/step-0/data/stammdaten.json`, `docs/step-0/referenzfaelle/02-einkommen.json`, `docs/step-0/referenzfaelle/03-split.json`

## Ziel (Plan-Text)

Buchungsjournal, Account-/Fund-Postings, Einnahmeverteilung, Ausgabe mit mehreren
Teilposten, Datum/Kategorie/Notiz, Basisrevision und steuerliche Kennzeichnung je
Teilposten gemäß Abschnitt 9. Alle zusammengehörigen Änderungen atomar speichern.
Abfragen berechnen Salden aus den Grunddaten.

## Prüfen (Plan-Text)

1. Einnahme 1.100 € auf Wohnungskonto; noch keine Transferannahme.
2. Ausgabe 85 € mit 65 € Flex und 20 € Freizeit verbraucht genau diese Pools; für
   diesen ersten Test liegen beide Pools auf dem Zahlungskonto.
3. Kontenübergreifende Deckung folgt in Schritt 5.
4. Split 84,99 € zu Gesamtbetrag 85 € wird zurückgewiesen.
5. Fehler während Commit hinterlässt keine halbe Buchung.
6. Unterdeckung bleibt sichtbar.

## Fertig (Plan-Text)

Kernbuchungen stimmen unabhängig von der Oberfläche.

## Scope-Entscheidungen (mit Begründung)

- **Kein REST/Keine UI in Schritt 4:** Produktive API ist Schritt 7, Ein-/Ausgabefelder
  in der Desktop-UI Schritt 7, mobile Erfassung Schritt 8. Abnahme über Domain- und
  Infrastructure-Tests (echte SQLite). Host bleibt unverändert (Health/Version).
- **Keine Ausgleichspositionen in Schritt 4:** Signierte Konto-Pool-Zuordnung und
  interne Vorfinanzierung (negative Zuordnung) sind Kern von Schritt 5. In Schritt 4
  zahlt das Konto, auf dem die finanzierten Pools liegen (Plan: „für diesen ersten
  Test liegen beide Pools auf dem Zahlungskonto“). Konto-Pool-Gap bleibt 0.
- **Keine Reservierungen/Richtwerte/Vorhaben:** Schritt 12/14. Buchungstypen
  `Reservation`/`Closeout` bleiben im Enum, werden hier nicht erzeugt.
- **Keine Korrektur/Storno/Erstattung:** Schritt 6. Bestätigte Buchungen werden in
  Schritt 4 nicht verändert (Revision bleibt 1; `BaseRevision` wird mitgeführt).
- **Einnahme = Account+ (Eingang) und Pool+ (Zuweisung), vollständig verteilt:**
  Summe der Verteilung = Gesamtbetrag, exakt. Ein nicht verteilter Einnahme-Rest
  existiert im Rechenvertrag nicht (nur der Anfangsbestand hat die
  „nicht zugeordnet“-Systemposition); ein Teil-Rest würde den Konto-Pool-Gap
  unscharf machen und ist deshalb abgelehnt.
- **Finanzierungsmonat:** Optionales Feld der Einnahme (Rechenvertrag: „Einnahmen
  können zusätzlich einen Finanzierungsmonat tragen“). Ohne Angabe gilt die
  Standardregel „Gehalt am Monatsende finanziert standardmäßig den Folgemonat“:
  Default = Folgemonat des Buchungsdats (deterministisch, wird in der Buchung
  materialisiert — „Tatsächlicher Eingang und Finanzierungsmonat sind unabhängig“).
  Bindung an Verpflichtungen und Deckungsrechnung: Schritt 12.
- **Splitregel (Rechenvertrag §3.5):** Summe der Splitbeträge = Gesamtbetrag der
  Ausgabe, exakt; Abweichung (z. B. 84,99 € bei 85 €) wird zurückgewiesen. Der
  Gesamtbetrag ist eigenes Feld der Vorgabe (erlaubte Zahlungsgrößenangabe), die
  Splits müssen exakt dazu addieren.
- **Ausgabe = Account− (Zahlungskonto, −Gesamtbetrag) und Pool− je Split:**
  Mehrere Splits desselben Pools werden in einem PoolPosting zusammengefasst
  (eine Buchung fasst einen Pool maximal einmal auf — bestehende Invariante).
  Das Fachdatum der Ausgabe bestimmt die Zurechnung (Rechenvertrag §2.2).
- **Kategorie je Split (nicht je Buchung):** Die Ausgabe hat Datum + Notiz auf
  Buchungsstufe; jeder Teilposten trägt Pool, Kategorie, Betrag, optional Notiz und
  die Steuerkennzeichnung (Plan: „Ausgabe mit mehreren Teilposten,
  Datum/Kategorie/Notiz … steuerliche Kennzeichnung je Teilposten“).
- **Steuerkennzeichnung je Teilposten (Plan §9):** `TaxMarking` = steuerlich
  relevant ja/nein, optional Steuerkategorie, optional Notiz, optional
  gekennzeichneter Teilbetrag (darf den Splitbetrag nicht übersteigen).
  - `relevant = false` → keine Steuerkategorie und kein Teilbetrag (nichts zu
    kennzeichnen).
  - `relevant = true` → Steuerkategorie optional („relevante Posten ohne gewählte
    Steuerkategorie sind in einer eigenen Liste sichtbar“), Teilbetrag optional
    (null = gesamte Splitbetrag markiert).
  - Steuerkategorie muss bei Erstellung existierend und aktiv sein
    (neue Referenz; „bereits verwendete Steuerkategorien werden archiviert statt
    gelöscht“ — historische Kennzeichnungen behalten ihren Referenzverweis).
  - Kennzeichnung verändert keine Finanzsalden (kein Posting, nur Daten am Split).
- **Vorbelegung wird bei Erstellung in den Posten übernommen (Plan §9):**
  Ohne explizite Kennzeichnung übernimmt der Split die wirksame Voreinstellung der
  Split-Kategorie (`MasterDataCatalog.ResolveEffectivePreset`): keine wirksame
  Voreinstellung oder `taxRelevant = null` („nicht festgelegt“) → keine
  Kennzeichnung; sonst Materialisierung (relevant, Steuerkategorie, kein Teilbetrag).
  Explizite Kennzeichnung (auch explizites Nein) übersteuert die Voreinstellung.
  Die Kennzeichnung wird als Snapshot im Split gespeichert — spätere Änderungen
  der Kategorie-Voreinstellung schreiben historische Kennzeichnungen nicht um.
- **Idempotenz (wie Schritt 3):** stabile Vorgabe-Id; gleiche Id + gleicher Inhalt →
  idempotente Bestätigung (keine zweite Buchung); gleiche Id + anderer Inhalt →
  sichtbarer Konflikt; Id einer Buchung mit anderem Typ → Konflikt.
- **Atomarität:** Buchung inkl. aller Account-/Pool-Postings und Splits inkl.
  Steuerkennzeichnung in einer SQLite-Transaktion; FK-Verletzung verwirft alles
  (keine halbe Buchung).
- **Idempotenz vergleicht den Inhalt der Vorgabe, nicht das abgeleitete
  Buchungsbild:** Steuerliche Voreinstellungen sind Stammdaten und können sich
  zwischen Original und Wiederholung ändern (oder entfernen). Eine fehlende
  Kennzeichnung in der Wiederholung ist deshalb kein Konflikt — die gespeicherte
  Kennzeichnung ist ein Snapshot der Anlage (Rechenvertrag: die Rechnung hängt
  nur von den gespeicherten Eingabedaten ab). Eine explizit übergebene
  Kennzeichnung dagegen ist Inhalt der Vorgabe und muss exakt stimmen. (Durch
  Integrationstest `Preset_MaterializedAsSnapshot_NotReDerived` abgesichert.)
- **`BookingJournal.TotalExpense()` bewusst nicht implementiert:** kein konkreter
  Bedarf in Schritt 4 (Verbrauch zeigt sich an den negativen Poolbeständen;
  Deckungsrechnung mit Aufwands-Summen gehört zu Schritt 5/12). Vermeidung
  vorgezogener Funktionen (AGENTS.md).

## Dateien (geplant)

### Lario.Domain
- `YearMonth.cs` — validierter Monatswert (Jahr, Monat 1–12), `Next()`, ISO-Format
- `Journal/TaxMarking.cs` — Steuerkennzeichnung eines Teilpostens
- `Journal/ExpenseSplit.cs` — Teilposten (Id, Betrag, Pool, Kategorie, Notiz, Kennzeichnung)
- `Journal/Booking.cs` (Erweiterung) — `FundingMonth` (nur Einnahme), `Splits` (nur Ausgabe),
  typspezifische Invarianten (Vorzeichen, Summen, exakt ein Konto)
- `Journal/IncomeSpec.cs` — Einnahmeverteilung; Validierung gegen Katalog;
  Default Finanzierungsmonat; `ToBooking()`
- `Journal/ExpenseSpec.cs` (+ `ExpenseSplitSpec`) — Ausgabe mit Splits; Splitregel;
  Kennzeichnungsvalidierung; Materialisierung der wirksamen Vorbelegung; `ToBooking()`
- ~~`Journal/BookingJournal.cs` (Erweiterung) — `TotalExpense()`~~ (nicht umgesetzt, s. Scope-Entscheidungen)

### Lario.Application
- `Journal/IncomeService.cs` — Idempotenz + Validierung + atomare Anlage
- `Journal/ExpenseService.cs` — Idempotenz + Validierung + atomare Anlage

### Lario.Infrastructure
- `LarioDbContext.cs` (Erweiterung) — `ExpenseSplits`, `SplitTaxMarkings` (1:1,
  Schatten-FK), `Bookings.FundingMonth`
- Migration `IncomeExpenseSplits`
- `BookingRepository.cs` (Erweiterung) — Splits inkl. Kennzeichnung laden/speichern

### Tests
- `tests/Lario.Domain.Tests/YearMonthTests.cs`
- `tests/Lario.Domain.Tests/IncomeSpecTests.cs`
- `tests/Lario.Domain.Tests/ExpenseSpecTests.cs`
- `tests/Lario.Domain.Tests/BookingTypeInvariantsTests.cs`
- `tests/Lario.Infrastructure.Tests/IncomeExpenseIntegrationTests.cs` (echte SQLite:
  Roundtrip, Idempotenz, Atomarität, Unterdeckung, Plan-Prüffälle)

## Abnahmetasks

- [x] T1: `YearMonth` (Validierung, Next, ISO, Vergleich)
- [x] T2: `TaxMarking` + `ExpenseSplit` + Booking-Erweiterung mit typspezifischen Invarianten
- [x] T3: `IncomeSpec` (Verteilung exakt, aktive Referenzen, Default-Finanzierungsmonat, ToBooking)
- [x] T4: `ExpenseSpec` (Splitregel exakt inkl. 84,99/85-Fall, Kategorie/Pool/Steuerkategorie, Vorbelegungs-Materialisierung inkl. explizitem Nein)
- [x] T5: `IncomeService` + `ExpenseService` (Idempotenz: Bestätigung/Konflikt/Typ-Konflikt)
- [x] T6: EF-Mapping + Migration + Repository (Splits/Kennzeichnung/FundingMonth)
- [x] T7: Domain-Tests (alle Invarianten, Referenzfall 02 + 03-Struktur, Plan-Prüffälle)
- [x] T8: SQLite-Integrationstests (Roundtrip, Idempotenz, Atomarität bei FK-Fehler, Unterdeckung sichtbar, Plan-Prüffälle 1.100 €/85 €)
- [x] T9: Build + alle Tests grün + Smoke-Test Host (Migration angewendet)
- [x] T10: Commit(s), Push, PR mit Review-Tabelle

## Verlauf

- 12. Okt. 2026: Aufgabe angelegt (Schritt 3 gemerged, `b7d9eab`).
- 12. Okt. 2026: Implementiert und getestet. PR #5 erstellt. 167 .NET-Tests (123 Domain, 44
  Infrastruktur, davon 20 Step-4-Integrationstests auf echter SQLite), Build ohne
  Warnungen. Host-Smoke-Test: frische Datenbank, Migration `IncomeExpenseSplits`
  angewendet, `/api/health` ok, neue Tabellen/Spalte verifiziert. Integrationstest
  hat einen echten Fehler in der Idempotenz gefangen (Neu-Ableitung der
  Voreinstellung statt Snapshot-Vergleich) — behoben, s. Scope-Entscheidungen.
