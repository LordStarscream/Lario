# Aufgabe: Schritt 5 – Transfers, Umwidmung und interne Ausgleiche

**Status:** [in Arbeit] (gestartet 12. Okt. 2026)
**Branch:** `step/5-transfers-ausgleiche`
**Plan:** `docs/Finanzplanung_Implementierungsplan.md` §5 „Schritt 5"
**Verbindliche Spezifikation:** `docs/step-0/rechenvertrag.md` (§3.3 Postings, §3.4 signierte Konto-Pool-Zuordnung, §3.5 offene Ausgleichspositionen, §3.6 freie Verfügbarkeit, §5.2 Transfer/Umwidmung-Status)
**Referenzfälle:** `docs/step-0/referenzfaelle/04-ausgleich.json`, `05-kontotransfer.json`, `06-umwidmung.json`

## Ziel

Reiner Transfer, Umwidmung, Kombination beider Vorgänge, Vormerkung und
Bestätigung, Abbruch, offener Ausgleich und Teilbegleichung. Zweck-/
Kontoverteilung aus Schritt 0 umsetzen (signierte Konto-Pool-Zuordnung,
abgeleitet aus den Postings; Zeilensummen = Kontostände, Spaltensummen =
Poolbestände; negative Zuordnung = interne Vorfinanzierung, verknüpft mit
einer offenen Ausgleichsposition).

**Fertig-Kriterium (Plan):** Bankstand, Zweckverteilung und offene Ausgleiche
stimmen gleichzeitig.

## Plan-„Prüfen" (verbindlich)

Start: Hauptkonto/Flex 1.000 €, Freizeitkonto/Freizeit 300 €.

1. Gemischter Einkauf 85 € vom Hauptkonto (65 € Flex + 20 € Freizeit) ergibt
   Konten 915/300 €, Pools Flex 935 € und Freizeit 280 €, offener Ausgleich 20 €.
2. Überweisung 20 € (Rückfluss) ergibt Konten 935/280 €, dieselben Pools und
   keinen offenen Ausgleich.
3. Gesamtbestand vorher 1.300 €, danach 1.215 €.
4. Teilbegleichung 5 € lässt 15 € offen.
5. Wiederholtes Bestätigen verdoppelt nichts.

## Design-Entscheidungen (mit Begründung)

- **Zweck-/Kontoverteilung als Buchungsinhalt:** Ein reiner Transfer hat keine
  Pool-Postings (Pools bleiben unverändert, §3.3); der Zweck der gewanderten
  Mittel ist daher Teil der Buchung (`TransferPurposes`, Zeilen
  Pool/Zeilenbetrag, Summe exakt = Transferbetrag). Eine Umwidmung hat keine
  Konto-Postings; das physisch tragende Konto ist Teil der Buchung
  (`ReassignmentAccounts`, Zeilen Konto/Zeilenbetrag, Summe exakt = Betrag).
  Die Kombination (TransferReassignment) trägt je exakt ein Konto- und ein
  Pool-Posting Paar und ist damit vollständig aus den Postings ableitbar.
- **Zuordnungsmatrix wird abgeleitet** (`BookingJournal.Allocations`):
  je Buchung ein Delta je Zelle (Konto, Pool). Invarianten: Zeilensummen =
  Kontostände, Spaltensummen = Poolbestände (Ausnahme: nicht zugeordneter
  Anfangsbestands-Rest, der bereits sichtbar bleibt).
- **Ausgleichspositionen (SettlementPosition)** werden **persistiert**
  (neue Tabelle, eigene stabile Id), weil Teilbegleichung und Status
  (offen/beglichen, settledByBooking) Teil des Fachzustands sind. Sie sind
  deterministisch aus dem Journal ableitbar (Kernregel: offene
  Positionsrestbeträge je Zelle (Konto, Pool) = max(0, −Zuordnung)); die
  Ableitung ist Domänenlogik und wird in Tests gegen den gespeicherten
  Zustand geprüft. Eine Zelle, die negativer wird, vertieft die offene
  Position (ggf. Neuanlage nach Abschluss); jede Buchung, die eine negative
  Zelle weniger negativ macht, reduziert die offene Position
  (Teilbegleichung) und schließt sie bei Rest 0 (settledByBooking).
- **Vormerkung/Bestätigung/Abbruch** (§5.2) wird über die
  Buchungs-Idempotenz realisiert: die Vorgang-Vorgabe (Spec) ist die
  Vormerkung; `Bestätigen` (Service `CreateAsync`) speichert die Buchung
  atomar; dieselbe Spec wird idempotent bestätigt (Wiederholtes Bestätigen
  verdoppelt nichts); Abbruch = Vorgang wird nicht bestätigt (es entsteht
  keine Buchung — nichts zu stornieren, keine halbe Buchung möglich).
  Umwidmung kann sofort gelten (bestätigt ohne Transfer), während der
  Kontotransfer später separat bestätigt wird (§5.2).
- **Kein Konto-/Pool-Saldo darf negativ werden durch einen Transfer:**
  Transfer: Quellkonto-Stand ≥ Betrag; Umwidmung: Quellpool-Bestand ≥ Betrag;
  Kombination: beides. Die Zuordnung darf dagegen negativ werden — das ist
  die interne Vorfinanzierung und erzeugt den Ausgleich (Plan-Kernfall).
- **Transfer, der einen offenen Ausgleich begleicht**, referenziert die
  Position (`relatedSettlementId`): Zielzweck muss der Positionspool sein,
  Teilbetrag ≤ Restbetrag. Eine Begleichung über ein anderes Konto/Paar ist
  erlaubt, solange die Zelle (Quellkonto, Positionspool) den Betrag trägt.

## Tasks

- [x] Plan, Rechenvertrag, Referenzfälle 04/05/06 gelesen; Design festgelegt
- [ ] Domain: `TransferSpec` + `ReassignmentSpec` (Prüfungen, ToBooking)
- [ ] Domain: `Booking` erweitern (Typen AccountTransfer/Reassignment/
      TransferReassignment: Invarianten, `AllocationDelta`, RelatedSettlementId)
- [ ] Domain: `AccountAllocation`, `SettlementPosition` (+Status-Enum)
- [ ] Domain: `BookingJournal` — Zuordnungsmatrix ableiten (Allocations,
      Invarianten), Ausgleichspositionen ableiten (deterministisch)
- [ ] Domain-Tests: Invarianten der drei Buchungstypen, Zuordnungsmatrix
      (Zeilensummen/Spaltensummen), Ausgleichs-Ableitung (Teilbegleichung,
      Vertiefung), Referenzfälle 04/05/06 als Domänenfälle
- [ ] Application: `TransferService` (inkl. Begleichung/Teilbegleichung),
      `ReassignmentService`, `TransferReassignmentService`
- [ ] Application: DI-Registrierung
- [ ] Infrastructure: EF-Mapping (TransferPurposes, ReassignmentAccounts,
      SettlementPositions) + Migration
- [ ] Infrastructure-Tests (echte SQLite): Referenzfälle 04/05/06
      end-to-end (Anfangsbestand → Ausgabe → Transfer → Abfrage),
      Teilbegleichung 5 € → 15 € offen, Wiederholtes Bestätigen idempotent,
      Konflikte, Atomarität (FK-Verletzung), Neustart-Provider
- [ ] Vollständiger Testlauf (Domain + Infrastructure) + Build ohne Warnungen
- [ ] Review (Selbst-Review + reviewer-Subagent)
- [ ] commit + push + PR (Nutzer-Workflow), Abnahme durch Nutzer

## Abweichungen / Offene Punkte

(noch keine)
