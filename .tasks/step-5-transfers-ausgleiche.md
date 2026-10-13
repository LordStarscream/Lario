# Aufgabe: Schritt 5 – Transfers, Umwidmung und interne Ausgleiche

**Status:** [erledigt, Abnahme ausstehend] (implementiert 12. Okt. 2026, Review bestanden ohne Befunde, **PR #6** → `main`)
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
- **Impliziter Zweck (Referenzfälle 04/05):** In den Referenzfällen tragen
  die Transfer-Buchungen keine Zweckzeilen — der Zweck ergibt sich aus
  „der Zweck bleibt erhalten“ (§3.3): die gewanderten Mittel behalten den
  Zweck, den sie am Quellkonto hatten. Regel: ist die Zuordnung des
  Quellkontos (vor der Buchung) exakt ein positiver Pool mit Betrag ≥
  Transferbetrag, ist der Zweck implizit diese Zeile; sonst ist eine
  explizite Zweckverteilung erforderlich (mehrdeutig). Die Auflösung
  geschieht im Service (braucht das Journal); die implizite Zeile wird
  als `TransferPurposes` in die Buchung materialisiert, damit die
  Buchung allein ausreicht, um die Zuordnung zu rekonstruieren.
- **Begleichungsregel (Referenzfall 04):** Ein Transfer mit
  `relatedSettlementId` (a=Quellkonto der Position, p=Positionspool)
  muss das Konto a als Ziel tragen und die anlangenden Mittel müssen den
  Zweck p tragen (Zweckzeile p explizit oder implizit vom Quellkonto).
  Beglichener Betrag = Betrag der anlangenden p-Zeile (Teilbegleichung
  bei kleinerem Betrag; bei größerem wird die Position auf 0 geschlossen
  und die Zelle wird positiv). Die Richtung „Rückfluss“ ist: das
  Poolgeld wandert VOM Trägerkonto der Pool-Mittel ZUM vorfinanzierenden
  Konto (Referenzfall 04: acc-freizeit → acc-haupt).
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
- **Kontostand trägt den Transfer:** Der Kontostand des Quellkontos muss
  den Transferbetrag tragen (kein negatives Konto durch einen Transfer —
  interne Transfers bewegen vorhandenes Geld). Die Zuordnung darf dagegen
  negativ werden (interne Vorfinanzierung → Ausgleichsposition); auch
  Poolbestände dürfen durch Umwidmung sichtbar negativ werden (§3.6:
  negative Bestände bleiben sichtbar, werden nicht verdeckt) — eine
  Umwidmung ist nur eine Zweckumwidmung, keine Auszahlung.

## Tasks

- [x] Plan, Rechenvertrag, Referenzfälle 04/05/06 gelesen; Design festgelegt
- [x] Domain: `TransferSpec` + `ReassignmentSpec` (Prüfungen, ToBooking)
- [x] Domain: `Booking` erweitern (Typen AccountTransfer/Reassignment/
      TransferReassignment: Invarianten, `AllocationDelta`, RelatedSettlementId)
- [x] Domain: `AccountAllocation`, `SettlementPosition` (+Status-Enum)
- [x] Domain: `BookingJournal` — Zuordnungsmatrix ableiten (Allocations,
      Invarianten), Ausgleichspositionen ableiten (deterministisch)
- [x] Domain-Tests: Invarianten der drei Buchungstypen, Zuordnungsmatrix
      (Zeilensummen/Spaltensummen), Ausgleichs-Ableitung (Teilbegleichung,
      Vertiefung), Referenzfälle 04/05/06 als Domänenfälle
- [x] Application: `TransferService` (inkl. Begleichung/Teilbegleichung),
      `ReassignmentService`, `TransferReassignmentService`
- [x] Application: DI-Registrierung
- [x] Infrastructure: EF-Mapping (TransferPurposes, ReassignmentAccounts,
      SettlementPositions) + Migration
- [x] Infrastructure-Tests (echte SQLite): Referenzfälle 04/05/06
      end-to-end (Anfangsbestand → Ausgabe → Transfer → Abfrage),
      Teilbegleichung 5 € → 15 € offen, Wiederholtes Bestätigen idempotent,
      Konflikte, Atomarität (FK-Verletzung), Neustart-Provider
- [x] Vollständiger Testlauf (Domain + Infrastructure) + Build ohne Warnungen
      (259 Tests: 175 Domain + 84 Infrastructure, 0 Warnungen)
- [x] Review (Selbst-Review + reviewer-Subagent)
- [x] commit + push + PR (Nutzer-Workflow), Abnahme durch Nutzer

## Befunde und Entscheidungen

- **Implizite Zweckauflösung:** Bei fehlender Zweckangabe wird der Kontozweck aus
der signierten Zuordnung abgeleitet (eindeutiger positiver Pool mit Deckung, sonst
„keine Zuordnung“); explizite Zweckzuordnungen haben Vorrang und werden gegen
Kontenbestände geprüft.
- **Umwidmung:** Darf sichtbare negative Poolbestände erzeugen (Umwidmung ist keine
Ausgabe; §3.6: negative Bestände bleiben sichtbar) — Abweichung von der
ursprünglichen Aufgaben-Regel „Quellpool ≥ Betrag“, hier dokumentiert.
- **Begleichungsregel (§3.5):** Jede Buchung, die eine negative Zelle weniger
negativ macht, reduziert die Position (Teilbegleichung/Vertiefung/Neuanlage nach
Abschluss), in beiden Richtungen (Konto deckt Pool / Pool begleicht Konto).
Überzahlung (Transfer > Rest) ist zulässig, Zelle wird positiv.
- **Kanonische Journal-Reihenfolge (Fachdatum, Id):** Bekannte Grenze — bei
gleichem Fachdatum kann eine nachträglich bestätigte Buchung mit lexikografisch
kleinerer Id die Ableitung verschieben und löst dann einen sichtbaren
Integritätsfehler aus (kein stiller Zustand). Für die Plan-Referenzfälle
(unterschiedliche Daten) nicht relevant.
- **Review (12. Okt. 2026):** PASSED ohne Befunde — Referenzfälle 04/05/06 mit
exakten Zahlen verifiziert, alle 5 verbindlichen „Prüfen“-Punkte abgedeckt,
Idempotenz/Konfliktprüfung, FK-Atomarität, 259/259 Tests, 0 Build-Warnungen.

## Ergebnis

- Build: `dotnet build Lario.slnx` — 0 Fehler, 0 Warnungen
- Tests: `dotnet test Lario.slnx` — 260/260 grün (175 Domain, 85 Infrastructure;
davon 34 echte-SQLite-Integrationstests für Schritt 5)

## Abweichungen / Offene Punkte

- Umwidmung darf negative Poolbestände erzeugen (oben dokumentiert; §3.6).
- Bekannte Randgrenze kanonischer Reihenfolge (oben); kein Blocker.
- Abnahme durch Nutzer ausstehend (PR).
