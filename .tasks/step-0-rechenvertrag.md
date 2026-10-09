# Aufgabe: Schritt 0 – Fachlichen Rechenvertrag festhalten
_Status: fertig (Push wartet auf Nutzerfreigabe) · Last updated: 8. Okt. 2026_

**Branches:**
- Lario → `step/0-rechenvertrag` (im Repo-Root, kein Worktree; Push erst nach Nutzerfreigabe)

**Gestartet:** 8. Okt. 2026
**Last updated: 8. Okt. 2026 (alle 8 Referenzfälle verifiziert)**

**Geklärte Entscheidungen (mit Nutzer, 8. Okt. 2026):**
- Ablage: `docs/step-0/rechenvertrag.md` + JSON-Dateien (Beispieldaten, Referenzfälle) — Testfixtures ab Schritt 3
- Entscheidungsliste: Major-Ebene mit Begründung; Patch-Stände erst in Schritt 1 fixieren
- Sync: in Schritt 0 nur Datenmodell (Operations-ID, Payload-Nachweis, Basisrevision, Entwurf vs. Bestätigung)
- Android: echtes Gerät vorhanden → Geräte-Abnahmen in 2/8/9/18 planbar
- Commit am Ende auf `step/0-rechenvertrag`; Push erst nach expliziter Freigabe

**Quelle:** `docs/Finanzplanung_Implementierungsplan.md` §5 „Schritt 0“, Stützregeln §1–§3, Übergabevorlage §7.
**Voraussetzung:** keine. **Kein Code, keine Oberfläche** — nur verbindliche Spezifikation + Referenzfälle.

**Ziel:** Jeder Vorgang besitzt eindeutige Wirkungen; keine ungeklärte Doppelzählung in den 8 Referenzfällen.
Technische Entscheidungsliste, Money/Datum-Regeln, Ledger-Entwurf (inkl. Konto-Pool-Zuordnung und interner
Ausgleiche), Deckungsperiode, Statusautomaten (Planung/Transfers/Vorhaben) und gemeinsame JSON-Beispieldaten;
Entwürfe, bestätigte Buchungen und Sync-Operationen sind klar getrennt.

**Fertigkriterium (Plan):** Alle 8 Referenzfälle sind bis auf den Cent durchgerechnet mit erwarteten Konto-,
Pool-, Reservierungs- und freien Salden; jede Vorgangsart hat eindeutige Wirkungen; keine Doppelzählung.

## Batch A — Regeln und Ledger-Entwurf
- [x] Task 1: Technische Entscheidungsliste aufstellen
  > - `docs/step-0/rechenvertrag.md` §1: Major-Ebene (Host-Stack, Frontend, Android, Tests, Geldrepräsentation, OpenAPI, Auth, Sync-Ansatz, Backup, kein LLM, kein Altdatenimport) mit Begründung
  > - „Bewusst offen bleiben“-Tabelle: Versionen (Schritt 1), SQLite-Plugin-Verifikation (1/2), Auth (7), Sync-Details (9/10), Backup-Ziel (17), Signierung (18)
  > - Kompatible Versionen werden erst in Schritt 1 geprüft und fixiert (global.json, csproj, Lockfiles)
- [x] Task 2: Money- und Datumsregeln festhalten
  > - §2.1: nur Cent-Ganzzahlen (long/INTEGER/validierte TS-Ganzzahl), kein float/double im Geldpfad, EUR only
  > - Rundungsentscheidung: Verteilungen mit `decimal`, **Half Away From Zero**, letzter Teil erhält Residualbetrag (deterministisch, Summe = Gesamtbetrag)
  > - §2.2/2.3: UTC-Zeitstempel (ISO 8601) vs. Fachdatum ohne Zeit; Europe/Berlin; Tag-31-Ruckelregel; Quartale; „Gültigkeit ab Datum“ strikt; Gehalt M → M+1 standard
- [x] Task 3: Ledger-Entwurf mit Konto-Pool-Zuordnung und internen Ausgleichen
  > - §3.1–3.8: Kernbegriffe strikt getrennt (inkl. Steuerkategorien-Baum, Vererbung/Übersteuern, explizites Nein bleibt wirksam)
  > - Buchung = atomarer Buchungssatz (BookingId, Fachdatum, optional Finanzierungsmonat, Typ, Basisrevision, Postings); Salden immer aus Grunddaten rekonstruierbar
  > - §3.4 signierte AccountPoolAllocation: Zeilensumme = Kontostand, Spaltensumme = Poolbestand; negativer Wert = Vorfinanzierung, zwingend an Ausgleichsposition gekoppelt
  > - Plan-Beispiel 935/280 € als Regelanker 1:1 übernommen (Konten 915/300, Pools 935/280, offener Ausgleich 20, nach Transfer 935/280)
  > - Review-Korrektur: „nicht als frei“-Formulierung exakt auf den Vor-Ausgleich-Zustand bezogen
- [x] Task 4: Deckungsperiode und Bindungsregeln spezifizieren
  > - §4: Deckung = offene Vergangenheit + laufender Monat + ausdrücklich finanzierte Zukunftsmonate; 9 verbindliche Regeln (Einmalbindung, keine doppelte Verpflichtung, kein pauschales Sperren, Unterdeckung sichtbar, keine stille Poolverschiebung, Richtwert unabhängig)

## Batch B — Statusautomaten und JSON-Beispieldaten
- [x] Task 5: Statusautomaten definieren (Planung, Transfer, Vorhaben) + Entwurf/Bestätigung/Sync trennen
  > - §5.1: PlannedOccurrence `geplant → bestätigt/abgebrochen`, Bestätigung mit abweichendem Betrag = Verknüpfung (keine zweite Instanz), Nachholung idempotent über Regel+Termin
  > - §5.2: Transfer `Vormerkung → bestätigt/abgebrochen`; Umwidmung kann sofort gelten, Kontotransfer später; Kombination = eigener Typ; offener Ausgleich als Zwischenzustand
  > - §5.3: Vorhaben `reserviert → laufend → abgeschlossen/abgebrochen`; Reservierung/Transfer/Abschluss-Regeln wie Plan §3
  > - §5.4: strikte Trennung Entwurf / bestätigte Buchung / Sync-Operation (Operations-ID, Payload-Hash, Basisrevision, Idempotenz, Quittung, Cursor erst nach atomarer Übernahme)
  > - §5.5: Basisrevision je Fachentität; abweichende Revision = sichtbarer Konflikt, kein Last-write-wins
  > - Review-Korrektur: Genus in §5.4 („derselben Operations-ID“)
- [x] Task 6: Gemeinsame JSON-Beispieldaten erstellen
  > - `docs/step-0/data/`: README.md, stammdaten.json (4 Konten, 6 Pools, Vorhaben, 15 Alltagskategorien, 7 Steuerkategorien, 3 taxPresets mit Drei-Stufen-Semantik, Anfangsbestände 100000/30000), buchung.json (3 Kern-Typen: Ausgabe mit Splits, Einnahme mit Finanzierungsmonat, Transfer), zuordnung_ausgleich.json (3 Zustände + set-0001 offen→beglichen), sync-operation.json (createBooking, updateBooking, idempotente Wiederholung + 5 Sync-Regeln)
  > - Startzustand = Plan-Kernfall 935/280: Hauptkonto 100000 komplett Flex, Freizeitkonto 30000 komplett Freizeit (Unterschied zum Schritt-3-Teilzuordnungsfall 700/300 im README dokumentiert)
  > - Maschinell verifiziert: gültiges JSON, Splitsummen exakt, Matrix-Invarianten in allen Zuständen
  > - Review-Korrektur: ID `cat-ver-öpnv` → `cat-ver-opnv` (technische Bezeichner ohne Umlaut)

## Batch C — Die 8 Referenzfälle (je Fall: erwartete Konto-, Pool-, Reservierungs- und freie Salden)
- [x] Task 7: Die 8 Referenzfälle aus dem Plan als JSON erstellen (einzeln, nicht kombiniert)
  > - `docs/step-0/referenzfaelle/`: 8 Fälle + README (Plan §5 Schritt 0 „Prüfen“):
  >   01 anfangsbestand (1000 flex + 300 freizeit, keine Einnahme, idempotent),
  >   02 einkommen (Gehalt 3000 am 28.10., Finanzierungsmonat 2026-11),
  >   03 split (Regelanker: 85 = 65 flex + 20 freizeit → Konten 915/300, Pools 935/280, negative Zuordnung −20, set-0010),
  >   04 ausgleich (Transfer 20 löst set-0010: Konten 935/280, Pools unverändert; Teilbegleichung 5 → 15 offen),
  >   05 kontotransfer (800 haupt → wohnung, Zweck flex wandert mit, keine Einnahme/Ausgabe),
  >   06 umwidmung (500 flex → rücklage, Konten unverändert, keine Richtwertausgabe),
  >   07 fixkostendeckung (Gehalt 3000 bindet Miete 900 + Versicherung 1100 = 2000 einmalig; freier Flex +1000; keine Doppelverrechnung),
  >   08 vorhabenabschluss (2000 reserviert, 1500 zweckerhaltend parkt/zieht zurück, 1750 bezahlt → 250 frei an Herkunftspool, keine Abschluss-Buchung)
  > - Fall 4 baut explizit auf Fall 3 auf (Zustand nach 85-€-Einkauf), dokumentiert in `startzustand.anpassungen`
  > - Fall 8 braucht abweichenden Start: Hauptkonto 200000 statt 100000 (Reservierung 200000 + Transfer 150000 nicht finanzierbar) — in `startzustand.anpassungen` begründet; Plan-Endwert 250 € frei bleibt exakt
  > - Jede Datei: meta (Plan-Bezug), startzustand, vorgaenge mit Buchungen im data/buchung.json-Format, erwarteter_endzustand (inkl. freie_salden + kontoliquiditaet), 6–8 maschinell prüfbare pruefungen
  > - IDs: Buchungen 1201–1210 (kollisionsfrei mit 1101–1103 aus data), Ausgleichsposition set-0010 (hinter set-0001)
- [x] Task 8: Abschlussprüfung der Referenzfälle (Cent-Konsistenz, Doppelzählung, Plan-Abgleich)
  > - Maschinell (Orchestrator, Python): in JEDEM Zustand aller 8 Fälle (Start, alle 5 Zwischenstände Fall 8, End) gilt Summe Konten = Summe Pools; Splitregel exakt (6500+2000=8500, 175000=totalAmount); Fall 4 Start == Fall 3 End; keine Floats; IDs kollisionsfrei
  > - Plan-Abgleich (Orchestrator): Regelanker 915/300 · 935/280 · offener Ausgleich 20 · nach Transfer 935/280 (Plan §3/Schritt 5) ✓; 1300 → 1215 ✓; Teilbegleichung 5 → 15 ✓; Gehalt 3000/28.10./November, Verpflichtungen 2000, +1000 frei, Kontozugang 3000 (Schritt 12) ✓; 2000/1500/1750 → 250 frei, keine Zusatzbuchung, Erstattung nach Abschluss → Herkunftspool (Schritt 14) ✓; Split 84,99 ≠ 85 abgelehnt (Schritt 4) ✓
  > - Doppelzählung: Fall 7 prüft explizit Zahlung ohne erneute Flex-Senkung, Teileingang ohne doppelte Bindung, Steuererstattungs-Vorschau ohne Bestandserhöhung; Fall 3 prüft keine Doppelbindung über set-0010
  > - Korrektur: Teilbegleichungs-Prüfung in Fall 4 von 10/10 auf Plan-Nummern 5/15 € angepasst

## Batch D — Fertigmeldung und Commit
- [x] Task 9: Fertigmeldung (Übergabevorlage Plan §7) erstellen — siehe Session-Antwort
  > - 1. Umgesetzter Schritt + Verhalten, 2. Dateien + Abweichungen, 3. Prüfungen + Ergebnis, 4. offene Abnahmen, 5. Übergabe an Review
- [x] Task 10: Commit auf `step/0-rechenvertrag`
  > - `ed2133e` docs(step-0): Rechenvertrag, data/, referenzfaelle/ (15 Dateien, 2084 Zeilen)
  > - `a4946e3` chore: .tasks/ + .gitignore (.pi/ und Schlüssel-Ausschlüsse)
  > - Working tree clean; **Push erst nach expliziter Nutzerfreigabe**

## Offene Punkte (für Schritt 1 / später)
- Fall 7 `pl-ver-2026-11`: Kategorie der Jahresversicherung ist im Plan nicht vorgegeben → Platzhalter `cat-sonstiges`, im JSON als `_todo` markiert; vor Schritt 3 (Stammdaten) korrigieren
- Fall 8: abweichender Startzustand (200000 statt 100000) ist dokumentiert; für Schritt 14 bleibt der Fall so, da die Plan-Vorgaben (2000/1500/1750) sonst nicht finanzierbar sind
- Technische Patch-Stände (global.json, csproj, Lockfiles) bewusst offen → Schritt 1
