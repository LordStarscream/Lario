# Design-Paket Schritt 6 — Korrektur, Storno, Erstattung, Kontostandsabgleich

Status: Entwurf für Review (vor Implementierung)
Stand: 14. Okt. 2026, Branch `step/6-korrekturen-erstattungen`

## 0. Maßgebliche Textstellen (entscheiden das Design)

- Plan Schritt 6: „Verknüpfte Korrektur/Storno; **tatsächliche Erstattung
  zum Ursprung**; manueller Bankstandsabgleich mit dokumentierter
  Differenz. Auswirkungen auf Splits und Ausgleiche atomar nachführen.
  Revisionskonflikt als Fehler zurückgeben."
- Plan-Abnahme: „Ausgabe 85 € auf 80 € korrigieren, Splits passend anpassen;
  **keine zweite Ausgabe**. Teilrückzahlung 10 € erhöht Konto **und
  Ursprungspool**. Eine Änderung nach bereits beglichenem Ausgleich erzeugt
  nachvollziehbaren neuen Ausgleichsbedarf. Abgleichdifferenz wird nicht als
  normales Gehalt gezählt."
- Rechenvertrag §5.4: Bestätigte Buchung ist „revidierbar **nur** über
  Korrektur- und Storno-Anwendungsfälle".
- Rechenvertrag §5.5: erwartete Basisrevision gehört zu jeder Änderung
  existierender Entitäten; Abweichung = Revisionskonflikt (Fehler, sichtbar,
  kein Last-Write-Wins); **Idempotenz zuerst über Operations-ID**: gleicher
  Inhalt → nur quittiert, Revision bleibt unberührt; gleiche Operations-ID
  mit anderem Inhalt = Fehler.
- Rechenvertrag §3.3/§4: Storno = Gegenbewegung, verknüpft; Händlererstattung
  = Eingang am Erstattungstag mit **Rückfluss zum ursprünglichen Pool** (keine
  Einnahmeverteilung); Kontostandskorrektur = dokumentierte Differenz,
  explizite Zuordnung, **keine** Einnahmenkategorisierung.

## 1. Grundentscheidungen (konsequent aus §0 abgeleitet)

- **Korrektur = In-place-Änderung** der existierenden Buchung (Plan: „keine
  zweite Ausgabe"; Rechenvertrag: „revidierbar nur über Korrektur").
  Änderbarer Inhalt: Datum, Notiz, Konto, Gesamtbetrag, Split (Ausgabe) bzw.
  Verteilung (Einnahme). `BaseRevision` wird erhöht (1 → 2 → …). Die alte
  Version bleibt nachvollziehbar über eine **Korrektur-Historie** (neue
  Tabelle: OperationId eindeutig, Vorher/Nachher-Snapshot, Notiz,
  UTC-Zeitpunkt) — „nie unbemerkt überschrieben" wird damit konkret.
- **Storno = Reversal-Buchung** (`BookingType.Reversal` = 8, verknüpft über
  `ReversesBookingId`); Original bleibt im Journal, seine `BaseRevision`
  wird erhöht.
- **Händlererstattung = eigene Buchung `BookingType.Refund`** (7): Konto +
  (am Erstattungstag, Konto wählbar — auch anderes als Originalkonto),
  Rückfluss an den **ursprünglichen Pool** der Ausgabe (Zeilen wie Mini-Split,
  Summe = Erstattungsbetrag, je Pool gedeckelt: bereits erteilte Erstattungen
  + neue Zeile ≤ Splitbetrag der Ausgabe). Keine Einnahme, kein
  Finanzierungsmonat, keine Kategorien — „Brutto/Erstattungen separat" bleibt
  erhalten. Teilrückzahlungen sind möglich (10 € von 85 €).
- **Kontostandskorrektur = Buchung `BookingType.BalanceCorrection`** (9):
  genau ein Konto-Posting (signed ≠ 0), keine Pool-Postings, Notiz Pflicht.
  Zählt nicht als Einnahme (`TotalIncome()` zählt nur `Income`).

Das Journal bleibt die einzige Quelle der Wahrheit: alle abgeleiteten Werte
(Salden, Zuordnungsmatrix, Begleichungspositionen) entstehen per Replay.
Korrektur/Storno/Erstattung/Abgleich ändern nur das Journal (In-place bei
Korrektur, neue Buchung sonst), und die Begleichungspositionen werden
atomar mit nachgeführt — durch Verallgemeinerung der
`SettlementPositionSync`-`Plan`-Funktion auf Vorher/Nachher-Journale.
Damit greift die gesamte Schritt-5-Maschinerie (VORHER-Integritätscheck,
einheitlicher Historien-Abgleich der Runden 2–6) ohne Sonderwege.

## 2. Domain-Änderungen

### 2.1 `Booking`

- Neue Felder: `ReversesBookingId` (nur Reversal), `RefundsExpenseId` (nur
  Refund), `ReversalDeltas` (IReadOnlyList<AllocationDeltaEntry>, nur
  Reversal — die explizit gespeicherten negierten Deltas des Originals;
  `AllocationDelta()` bei Reversal liefert sie, kein Cross-Booking-Lookup im
  Replay). `BaseRevision` (set-table, vorhanden) wird jetzt erhöht.
- `Validate()`-Erweiterungen:
  - Reversal: `ReversesBookingId` nicht leer; `ReversalDeltas` nicht leer;
    Deltas balancieren gegen die eigenen Postings (Summe je Konto =
    Konto-Posting-Summe, je Pool = Pool-Posting-Summe);
  - Refund: `RefundsExpenseId` nicht leer; genau ein Konto-Posting > 0;
    Pool-Postings > 0, je Pool maximal einmal; Deltas = (Konto, Pool) je
    Zeile;
  - BalanceCorrection: genau ein Konto-Posting ≠ 0, keine Pool-Postings,
    `Note` nicht leer (Notizpflicht);
  - übrige Typen: unverändert.

### 2.2 `AllocationDelta()`

- `Reversal` → `ReversalDeltas`. `Refund` → (Konto, Pool) +Zeilenbetrag je
  Pool-Posting (Rückfluss zum Ursprungspool kann offene Ausgleiche
  (teil-)begleichen). `BalanceCorrection` → leer.
- Invarianten-Tests: `ReversalDeltas == Negation(Original.AllocationDelta())`;
  Refund-Deltas summieren exakt zum Konto-Posting.

### 2.3 Neue Kommandotypen (`Lario.Domain/Journal/`)

- `ReversalSpec { Id, OriginalBookingId, ExpectedBaseRevision (Pflicht),
  Date, Note? }` — `Id` = stabile Id der neuen Storno-Buchung.
- `CorrectionSpec { BookingId, OperationId (Pflicht, stabil),
  ExpectedBaseRevision (Pflicht), Date, Note?, AccountId?, TotalAmount?,
  Splits? (Ausgabe) / Allocations? (Einnahme) }` — Ersatzinhalte werden wie
  eine Neuanlage validiert (Katalog, Splitregel, aktive Entitäten).
- `RefundSpec { Id, OriginalExpenseId, AccountId, Date, Lines: (PoolId,
  Amount)[], Note? }`.
- `BalanceCorrectionSpec { Id, AccountId, Amount (signed ≠ 0), Date,
  Note (Pflicht) }`.

### 2.4 Neue Entität `BookingCorrection` (Korrektur-Historie)

Felder: `OperationId` (PK — Idempotenzschlüssel), `BookingId`,
`OldSnapshot`/`NewSnapshot` (JSON: Datum, Konto, Betrag, Notiz,
Split/Verteilung), `Note`, `CreatedAtUtc`. Wird atomar mit der In-place-
Änderung geschrieben. Testbar: jede Revision > 1 der Buchung hat genau eine
Historieneintrag; Wiederholung (gleiche OperationId + gleicher Inhalt) wird
quittiert, ohne zweiten Eintrag und ohne Revisionserhöhung.

### 2.5 Revision und Idempotenz (Rechenvertrag §5.4/§5.5)

- **Korrektur**: `ExpectedBaseRevision` muss der Ist-Revision entsprechen,
  sonst `DomainConflictException` mit erwartetem und Ist-Wert.
  Mehrfache Korrekturen sind zulässig (Revision 2 → 3 …) — jede mit der
  jeweils aktuellen Revision.
- **Storno**: gleiche Prüfregel; Erfolgsfall: Reversal-Buchung + Revision++
  atomar. Bewusster „Widerruf des Stornos" (neues Storno auf die Storno-
  Buchung mit aktueller Revision) ist möglich und eine eigene,
  revisionsgerechte Operation — ein **Doppelstorno** (zweite Storno-Id auf
  dasselbe Original mit alter Erwartungsrevision) schlägt dagegen als
  Revisionskonflikt fehl (B1).
- **Idempotenz zuerst** (vor Revision/Katalog-Prüfung):
  - Korrektur: über `OperationId` in der Historie. Gleicher Inhalt →
    Bestätigung; gleicher OperationId + anderer Inhalt → Konflikt.
  - Storno/Erstattung/Abgleich: über die stabile Buchungs-`Id` (Muster
    Schritt 4/5): gleiche Id + gleicher Inhalt → Bestätigung; gleiche Id +
    anderer Inhalt oder anderer Typ → `DomainConflictException`.
- **Fehlendes Original** → `InvalidReferenceException` (sichtbar).
- **Erstattung**: Deckelung je Ursprungspool (s. §1); Original muss eine
  Ausgabe sein; **kein** Revisionseffekt auf die Ausgabe.

## 3. Infrastruktur-Änderungen

### 3.1 `LarioDbContext`

- `Booking.ReversesBookingId`, `Booking.RefundsExpenseId` (string?, max 128).
- `ReversalDeltas`: eigene Tabelle über `OwnsMany(...).ToTable(
  "ReversalDeltas")` (Key = BookingId + Diskriminator — keine
  NULL-Komposit-Key-Probleme).
- Neue Tabelle `BookingCorrections`: PK `OperationId`; `BookingId` (FK
  Restrict), `OldSnapshot`, `NewSnapshot` (TEXT), `Note` (max 4000),
  `CreatedAtUtc`.
- Migration `…_Step6CorrectionsReversalsRefunds`
  (`LarioMigrationsAssembly` bleibt `Lario.Infrastructure`).

### 3.2 Neue Services (`src/Lario.Application/Journal/`, Muster der
bestehenden: Result-Record, Idempotenz zuerst, Katalog, Sync-Plan, atomar)

- `ReversalService.ReverseAsync(ReversalSpec)`:
  1. Idempotenz über `Id`;
  2. Original laden (fehlend → InvalidReference); stornierbarer Typ =
     Expense, Income, AccountTransfer, Reassignment, TransferReassignment,
     Reversal (nicht: OpeningBalance, Settlement, BalanceCorrection —
     Abgleichdifferenzen werden durch eine neue dokumentierte Korrektur
     ersetzt, nicht storniert);
  3. Revision `ExpectedBaseRevision` vs. Ist;
  4. Reversal bauen: Postings = Negation der Original-Postings, Deltas =
     Negation von `Original.AllocationDelta()`, `ReversesBookingId`,
     eigenes Fachdatum (rückdatiert zulässig);
  5. Sync-Plan (Vorher = Journal, Nachher = Journal + Reversal) und atomar:
     Reversal speichern **und** Original-Revision++ (eine Transaktion).
- `CorrectionService.CorrectAsync(CorrectionSpec)`:
  1. Idempotenz über `OperationId` (Historie);
  2. Original laden; Typ = Expense oder Income (andere Typen: sichtbar
     abgelehnt, „Korrektur über Storno + Neuausgabe");
  3. Revision;
  4. Ersatzinhalt wie Neuanlage validieren;
  5. Sync-Plan: Vorher = Journal mit altem Inhalt, Nachher = Journal mit
     neuem Inhalt (gleiche Buchung, geänderter Content) — neue
     `Plan`-Signatur (§3.3);
  6. atomar: Buchung aktualisieren (Content + Revision++), Postings/
     Teilposten ersetzen, Begleichungszeilen nachführen, Historieneintrag
     (eine Transaktion).
- `RefundService.RefundAsync(RefundSpec)`:
  1. Idempotenz über `Id`;
  2. Original-Ausgabe laden (fehlend/falscher Typ → InvalidReference);
  3. Konto aktiv; Zeilen: Summe = Gesamtbetrag, Pools ⊆ Ursprungspools,
     je-Pool-Deckelung gegen Splitbeträge minus bereits erteilte Erstattungen
     (aus dem Journal);
  4. Refund-Buchung: Konto +Gesamtbetrag, Pool + je Zeile, Deltas je Zeile,
     `RefundsExpenseId`;
  5. Sync-Plan + atomar speichern.
- `BalanceCorrectionService.ApplyAsync(BalanceCorrectionSpec)`:
  1. Idempotenz über `Id`;
  2. Konto aktiv; Betrag ≠ 0; Notiz nicht leer (sonst LarioDomainException);
  3. Buchung `BalanceCorrection` (ein Konto-Posting, keine Pools);
  4. Sync-Plan (keine Zellen-Effekte — der Mechanismus bestätigt das) +
     atomar speichern.

Alle Services: keine partielle Übernahme bei Fachfehlern (SaveChanges nur
auf dem Erfolgs-Pfad; EF-Fehler rollen zurück).

### 3.3 `SettlementPositionSync` — Verallgemeinerung (R6-struktur bleibt)

- Neue Kernsignatur `Plan(beforeBookings, afterBookings, persisted,
  newIdFactory)` (reine Funktion). Bestehende Services: dünne Hülle
  `Plan(bookings, newBooking, …)` = `Plan(bookings, bookings + [newBooking])`
  — Verhalten unverändert (bestehende 291 Tests bleiben grün).
- „Reine Addition" = Vorher-Episoden sind werteidentisches Präfix der
  Nachher-Episoden (statt `after.Count == before.Count + 1`).
- Begleichungsreferenz bei Verschwinden eines Zyklus: statt fix `newBooking`
  die **tatsächliche schließende Buchung** aus dem Nachher-Replay (Zellen-
  kumulus erstmals wieder ≥ 0; der Zyklus war vorher offen, also ist sie eine
  Nachher-Neubuchung oder — bei Korrektur — eine Buchung, die durch den
  geänderten Inhalt den Zyklus schließt).
- VORHER-Integritätscheck, Split-/Vollausgleichs-/Fortsetzungs-Zweige und
  der einheitliche Historien-Abgleich (R4/R5/R6) bleiben strukturell
  identisch — sie arbeiten bereits auf Episodenpaaren.

## 4. Host-Änderungen

- Vier Endpunkte (`/api/journal/…`):
  - `POST /api/journal/bookings/{id}/reversal`
  - `POST /api/journal/bookings/{id}/correction`
  - `POST /api/journal/bookings/{id}/refund`
  - `POST /api/journal/accounts/{accountId}/balance-correction`
- Request-Body = Kommando (stabile Id des Clients/Devices — Idempotenz,
  `ExpectedBaseRevision` Pflicht bei Korrektur/Storno).
- Response: Ergebnis-Record (Bestätigt/Ergebnis/Details) — bestehendes
  API-Pattern. `DomainConflictException` → 409 mit erwartetem + Ist-Wert;
  `InvalidReferenceException` → 422; `LarioDomainException` → 400.
- OpenAPI-Vertrag wird dadurch um die vier Schemas erweitert (automatisch
  aus den Kommando-Typen).

## 5. Abnahme-Szenarien → Tests

### A. Plan-Abnahme (Pflicht, echte SQLite-Integrationstests)

- **A1** Korrektur: Ausgabe 85 € (Konto A, 2026-11, Split 65/20) auf 80 €
  mit angepasstem Split (60/20) korrigieren → exakt **eine** Ausgabe-Buchung
  (Id unverändert, Revision 2, Inhalt 80 €), Historieneintrag mit Vorher/
  Nachher, Konto-Saldo A sinkt um 5 €, Pool-Zeilen sinken um 5 € (nur Pool
  1), kein „zweite Ausgabe"-Zustand.
- **A2** Teilrückzahlung: Ausgabe 85 € (Konto A, Pool P), Händlererstattung
  10 € am Folgetag auf Konto A → Konto-Saldo A +10 €, Pool P +10 €
  (ursprünglicher Pool, nicht „Ursprung" als Konto), Buchungstyp Refund mit
  `RefundsExpenseId`; `TotalIncome()` bleibt unverändert. Zweite Teil-
  rückzahlung bis zur Deckelung (Splitbetrag) möglich, darüber abgelehnt.
- **A3** Änderung nach beglichem Ausgleich: Ausgabe erzeugt Zyklus,
  Ausgleichsvorgang (Schritt-5-Mechanik) begleicht ihn (Revision-Zeile
  abgeschlossen), dann Korrektur der Ausgabe (kleinerer Betrag) →
  **neuer, nachvollziehbarer** Ausgleichsbedarf: alte Abschlusszeile bleibt
  (Historie), neue offene Zeile entsteht, `TotalOutstandingAllocation`
  zeigt den Rest; Kontostände bleiben konsistent (Matrix-Spalten-Check).
- **A4** Kontostandsabgleich: Bank meldet 5 € mehr als geplant →
  BalanceCorrection +5 € auf Konto A mit Notiz „Bankdifferenz 14.10." →
  Konto-Saldo A +5 €, **keine** Einnahme (TotalIncome unverändert), keine
  Pool-Änderung; Notiz leer → abgelehlt.
- **B1** Doppelstorno: Storno der Ausgabe (Revision 1 → 2, Reversal-Buchung
  verknüpft, Konten-/Pool-Salden exakt negiert); zweites Storno mit anderer
  Id und Erwartung Revision 1 → **Revisionskonflikt** (409); gleiche Id +
  gleicher Inhalt → idempotente Bestätigung (keine zweite Storno-Buchung,
  Revision bleibt 2).
- **B2** Erstattung auf anderem Konto: Ausgabe Konto A, Erstattung 10 € auf
  Konto B → Konto A unverändert, Konto B +10 €, Pool P +10 €.
- **B3** Revisionskonflikt bei Korrektur: zwei parallele Korrekturen derselben
  Ausgabe (beide erwarten Revision 1) → eine gewinnt, die andere Revisions-
  konflikt; Historie enthält exakt einen Eintrag für Revision 2.
- **B4** Storno einer beglichenen Ausgabe: Zyklus wurde beglichen, dann
  Storno → die Begleichungszeile verschwindet nicht still: Nachher-Replay
  zeigt den Zyklus wieder (oder als ausgeglichen je nach Vorher-Nachher-
  Abgleich) — Ergebnis: Konten-/Pool-Salden exakt negiert,
  Begleichungspositionen konsistent mit dem Replay (Matrix- und
  Zeilen-Summen-Checks).
- **B5** Idempotenz mit gleichem Inhalt: jede der vier Operationen erneut mit
  gleicher Id/OperationId + gleichem Inhalt → Bestätigung, **kein**
  zweiter Zustand (Buchungszahl, Revision, Historienzählung, Salden
  unverändert).
- **B6** Abgleichdifferenz nicht als Gehalt: nach A4 bleibt `TotalIncome()`
  unverändert; die Differenz ist ausschließlich auf Konto-Saldo und
  Buchungstyp BalanceCorrection sichtbar.

### C. Regeleinhaltung (Domain-Tests)

- ReversalDeltas == Negation(Original.AllocationDelta()) (Replay-Invariante).
- BalanceCorrection: genau ein Konto-Posting, keine Pools, Notiz Pflicht,
  Betrag ≠ 0.
- Refund: Deckelung je Pool; Summenregel der Zeilen; falsche Originaltypen
  abgelehnt.
- Storno nicht möglich für OpeningBalance/Settlement/BalanceCorrection.
- Revisionskonflikt: Erwartungswert ≠ Ist → Fehler mit beiden Werten
  (keine stille Übernahme).
- Gleiche Operations-ID + anderer Inhalt → Konflikt (für alle vier Fälle).

## 6. Offene Punkte / bewusst nicht in Schritt 6

- UI-Flows (Korrektur-/Storno-Dialoge) → Schritt 8+; hier nur API + Services
  + Domain + Tests.
- Mobile Sync-Endpunkte für die vier Operationen → Schritt 9 (die
  Idempotenz-Semantik ist aber jetzt schon festgelegt und wird in Schritt 9
  unverändert übernommen).
- „Erstattung über ein anderes Konto als Ursprung" ist erlaubt (B2) — die
  Pool-Zuordnung bleibt am Ursprungspool, das Konto folgt der realen
  Gutschrift (Rechenvertrag §4).
