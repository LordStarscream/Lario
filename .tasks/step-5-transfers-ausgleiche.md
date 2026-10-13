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

### Externe Review-Runde (13. Okt. 2026) — drei Befunde, alle behoben

- **P1 — Pairing bei rückdatierten/gleichdatierten Buchungen:**
  `SettlementPositionSync.Plan` und `SettlementQueryService` paarten Zeilen und
  abgeleitete Episoden über `(Konto, Pool, Begründungsbuchung)`. Eine
  rückdatierte Ausgabe verschiebt die Begründung des offenen Zyklus —
  die alte Zeile wäre dann nie gepaart worden (offene Episode ohne Zeile /
  Zeile ohne Episode). **Fix:** Journal-Wahrheit mit stabiler Identität:
  pro Zelle trägt die **jüngste Zeile die (maximal eine) offene Episode**;
  beglichene Episoden/Zeilen werden chronologisch gepaart (Zeilen ≥ Episoden:
  von hinten, überzählige älteste Zeilen bleiben unsichtbare Historie;
  Episoden > Zeilen: von vorn, überzählige jüngste Episoden bleiben
  unsichtbar). Persistierte Zeilen behalten ihre Identität (Id, Begründung,
  Notiz); die Ansicht zeigt den Journalzustand mit der stabilen Zeilen-Id.
  Abweichungen = sichtbarer Integritätsfehler, kein stiller Zustand.
  Neue Tests: `SettlementIdentityTests` (6 Fälle: Teilbegleichung/Vollbegleichung
  gleichdatiert, rückdatierte Einnahme/Ausgabe, Zusammenführung zweier
  beglichener Zyklen, Storno-Ablehnung).
- **P2b — Herkunft der Finanzierung (explicit/implicit):**
  `Booking.AllocationExplicit` (bool?, Migration
  `20261013072950_AllocationProvenance`, INTEGER nullable; `null` =
  Buchungstyp ohne Verteilungszeilen oder Vor-Review-Daten): `true` = Zeilen
  explizit angegeben, `false` = implizit aus dem Journal abgeleitet. Teil des
  Original-Eingabeinhalts und der Idempotenzprüfung (explizite Wiederholung
  muss explizit gespeichert sein und umgekehrt), analog
  `TaxMarking.InheritedFromPreset`. Neue Tests: `AllocationProvenanceTests`
  (5 Fälle inkl. Persistenz-Roundtrip über echten SQLite-Provider-Neustart
  und Idempotenz bei Wiederholung mit/ohne Zeilenangabe).
- **P2d — Upgrade-Backfill für bestehende Datenbanken:**
  `SettlementPositionBackfillService` läuft beim Host-Start nach der
  Migration und materialisiert fehlende **offene** Positionen (offene
  Episode in einer Zelle ohne persistierte Zeile, z. B. DB nur bis
  `SplitMarkingProvenance` migriert), atomar und idempotent. Beglichene
  Episoden ohne Zeile bleiben unsichtbar (Historie). Neue Tests:
  `SettlementBackfillTests` (2 Fälle: frische DB → 0 Zeilen; DB ohne
  Positionsverwaltung → 2 Zeilen, zweiter Lauf → 0).

### Zweite externe Review-Runde (13. Okt. 2026, nach der P1-Fix-Lieferung) — alle Befunde behoben

- **P1a — Vollausgleich einer offenen Position durch die neue Buchung
  (rückdatierte oder gleichdatierte Einnahme, kanonisch vor der Begründung
  sortiert):** Der erste P1-Fix behandelte nur Teilbegleichung und
  Zusammenführung; der Vollausgleich lieferte `episodes.Count == 0` und
  wurde weiterhin als `LarioDomainException` abgelehnt. **Fix:** Die
  gültige Einnahme wird gespeichert; die offene Positionszeile wird
  kontrolliert abgeschlossen (Status beglichen, Rest 0, Identität und
  Höhe bleiben, Begleichungsreferenz = die neue Buchung). Ist der Zyklus
  im Replay als jüngste beglichene Episode erhalten (Zufluss nach der
  Begründung), folgt die Zeile der Journalwahrheit (Begleichungsreferenz
  der kanonischen Reihenfolge). Die Query-Prüfung „offene Zeile ohne
  Episode“ bleibt als Sicherheitsnetz für fremde/korrupte Datenbanken.
  Neue Tests: `BackdatedIncome_FullSettlement_SavesIncomeAndClosesRow`
  (Salden, abgeschlossene Historie, Neustart) und
  `SameDayIncome_SmallerId_FullSettlement_SavesIncomeAndClosesRow`
  (gleichdatierter Zufluss mit lexikografisch kleinerer Id).
- **P1b — `episodes[0]` statt der jüngsten Episode:** In
  `SettlementPositionSync.Plan` wählte der P1-Fix die offene Episode mit
  `episodes[0]`; bei Altdaten mit bereits abgeschlossener Episode ohne
  Zeile + neuer Ausgabe materialisierte er die falsche (beglichene)
  Episode als neue offene Zeile. **Fix:** Die offene Episode ist per
  Ableitungsregel immer `episodes[^1]`; Paarung beglichener Episoden und
  Zeilen einheitlich von hinten (älteste Episoden ohne Zeile bleiben
  unsichtbare Historie) — auch in `SettlementQueryService`.
  Neuer Test: `LegacyCell_NewExpense_CreatesExactlyOneOpenRow` (inkl.
  Nachweis, dass der P2d-Backfill in diesem Zustand 0 Zeilen legt).
- **R2 — Idempotenz vor Aktivitätsprüfung:**
  `TransferService`/`ReassignmentService` prüften bei impliziter
  Wiederholung die Aktivität der materialisierten Konten/Pools — eine
  identische Vorgabe wurde nach Archivierung als Konflikt verworfen
  statt idempotent bestätigt (Idempotenz greift vor der Aktivitäts-
  prüfung; die gehört zur tatsächlichen Neuanlage und bleibt dort).
  Neue Tests: `Repeat_Implicit_AfterMaterializedPoolArchived_IsConfirmed`
  (Transfer), `Repeat_Implicit_AfterMaterializedAccountArchived_IsConfirmed`
  (Umwidmung).

## Ergebnis

- Build: `dotnet build Lario.slnx` — 0 Fehler, 0 Warnungen
- Tests: `dotnet test Lario.slnx` — 277/277 grün (175 Domain, 102
  Infrastructure; davon 51 echte-SQLite-Integrationstests für Schritt 5)

## Abweichungen / Offene Punkte

- Umwidmung darf negative Poolbestände erzeugen (oben dokumentiert; §3.6).
- Bekannte Randgrenze kanonischer Reihenfolge (oben); kein Blocker.
- Abnahme durch Nutzer ausstehend (PR #6, um die Review-Fixes beider
  Runden erweitert).
