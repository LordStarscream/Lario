# Lario – Fachlicher Rechenvertrag (Schritt 0)

**Status:** verbindlich für die Umsetzung
**Stand:** 8. Oktober 2026 (Entscheidungen mit dem Nutzer geklärt)
**Bezug:** Implementierungsplan für Lario (Grundsatz §1, Review-Entscheidungen §2, Präzise Regeln §3, Arbeitsweise §4, Schrittkontext §5, Steuerkategorien §9).

> **Hinweis:** Bei Abweichungen zwischen diesem Dokument und dem Implementierungsplan ist der Implementierungsplan zu aktualisieren — dieses Dokument ist seine Konkretisierung. Alle späteren Code-Schritte (3–18) und alle Tests orientieren sich an diesem Rechenvertrag. Die vollständigen, bis auf den Cent durchgerechneten Referenzfälle liegen in separaten JSON-Dateien (Abschnitt 6).

**Geltungsbereich:** Fachliche Finanzlogik von Lario — Repräsentation von Geld und Zeit, Buchungsführung (Ledger), Deckungslogik und Statusautomaten. Nicht betroffen: konkrete Technologieversionen (Schritt 1), Auth-Verfahren (Schritt 7), Sync-Protokoll (Schritt 9/10), UI-Gestaltung, Backup-Zielkonfiguration (Schritt 17), Signierung (Schritt 18).

---

## 1. Technische Entscheidungsliste

Entscheidungen auf Major-Ebene mit Begründung. Konkrete Patch-Stände (global.json, csproj, Lockfiles, Plugin-Versionen) werden erst in Schritt 1 geprüft und fixiert; hier gilt nur diese Entscheidungsvorlage.

| Entscheidung | Wahl | Begründung |
|---|---|---|
| Host-Stack | C#/.NET 10, ASP.NET Core, EF Core, SQLite (Dateidatenbank) | Hauptprogramm läuft lokal oder im lokalen Netzwerk; eine lokale SQLite-Datei genügt und vereinfacht Backup als Datei-Export. Patch-Stände werden in Schritt 1 fixiert. |
| Web-Frontend | Angular im Browser | Ein Frontend-Stack für Web und Mobile; Hauptprogramm mit vollständiger Oberfläche. |
| Android | Angular + Capacitor 8 mit eigener eingebetteter SQLite-Datenbank | Erste Mobilplattform ist Android; nach Ersteinrichtung ohne Host und ohne Internet nutzbar. Schwerpunkt ausschließlich schnelle Ausgabenerfassung, Splits, eigene Eingaben und deren Synchronisationsstatus; mobile Finanzübersicht optional. |
| .NET-Tests | xUnit mit echter SQLite-Instanz | EF-Core-InMemory ist ausdrücklich ungeeignet, weil Transaktionen, Constraints und Ledger-Integrität nur an einer echten Datenbank geprüft werden können. |
| Geldrepräsentation | EUR-Cent als ganze Zahl: `long` (.NET), `INTEGER` (SQLite), validierte sichere Ganzzahl (TypeScript) | Deterministisch, verlustfrei, bis auf den Cent prüfbar. Kein `float`/`double` irgendwo im Geldpfad. |
| API-Vertrag | OpenAPI aus ASP.NET Core, daraus TypeScript-Generierung | Ein Vertrag für Web- und Mobile-Client; Drift zwischen Server und Client wird bei der Generierung sichtbar. |
| Auth | Lokal, widerrufbare Zugangsdaten, kein Cloud-Dienst | Betrieb ausschließlich im Heimnetz; kein externer Dienst. Konkretes Verfahren in Schritt 7 festgelegt. |
| Synchronisation | Manuell im LAN, operations-basiert (Operationen mit stabiler ID und Payloadnachweis) | Keine automatische Hintergrundsynchronisation im Grundumfang; End-to-End-Abbruchszenarien (z. B. Abbruch nach Servercommit) bleiben dadurch testbar. Protokoll-Details in Schritt 9/10. |
| Backup | Datei-Export von Datenbank + Belegen | Einfaches, lokal verifizierbares Backup; keine laufende Kopie der ganzen SQLite-Datei als Synchronisationsverfahren. Konfiguration in Schritt 17. |
| LLM | Kein LLM in der Laufzeit | Finanzlogik bleibt vollständig deterministisch und nachvollziehbar. |
| Altdaten | Kein Altdatenimport, keine Portierung des alten Datenmodells, kein Kompatibilitätsbetrieb | Die frühere Python-Anwendung enthält keine Nutzdaten; Lario ist Greenfield. Nur spätere Schemaänderungen der neuen Anwendung benötigen Migrationen. |

### Entscheidungen, die bewusst offen bleiben

| Thema | Wird festgelegt in |
|---|---|
| Konkrete Patch-Versionen (SDK, global.json, csproj, Lockfiles, Plugin-Versionen) | Schritt 1 |
| Verifikation des Capacitor-SQLite-Plugins (Pluginwahl, Verhalten auf echtem Gerät) | Schritt 1/2 (Geräte-Abnahme, Gerät vorhanden) |
| Auth-Verfahren (Zugangsdaten-Format, Widerruf, Bootstrap) | Schritt 7 |
| Sync-Protokoll-Details (Endpoints, Cursor-Verfahren, Generationen, Quittungsprotokoll, Restore) | Schritt 9/10 |
| Backup-Zielkonfiguration (Zielpfad, Wiederherstellungstest) | Schritt 17 |
| Android-Signierung (Schlüssel, Build) | Schritt 18 |

---

## 2. Money- und Datumsregeln

### 2.1 Geld

- Alle Geldbeträge werden ausschließlich als **EUR-Cent in ganzen Zahlen** gespeichert und berechnet: `long` in .NET, `INTEGER` in SQLite, in TypeScript als validierte sichere Ganzzahl (Prüfung auf `Number.isSafeInteger` bei jeder Grenze des Clients).
- `float`/`double` darf für Geldbeträge **niemals** verwendet werden — weder in Berechnungen, noch in Persistenz, noch in der Übertragung (API-Payload enthält Cent-Werte als Ganzzahlen).
- Währung ist in Version 1 ausschließlich EUR. Keine Mehrwährung, keine Umrechnung.
- Splits und Verteilungen: Teilbeträge werden als Ganzzahl-Cent berechnet. Bei Aufteilung auf mehrere Teile wird mit `decimal` gerechnet und mit dem Rundungsmodus **Half Away From Zero** auf Cent gerundet; der letzte Teil erhält den Residualbetrag (Gesamtbetrag minus Summe der vorherigen Teile). Die Summe der Teilbeträge muss exakt den Gesamtbetrag ergeben; die Anwendung muss dies bei jeder Verteilung prüfen.
- Steuervermerkte Teilbeträge dürfen den Splitbetrag nicht übersteigen (Prüfpflicht).

### 2.2 Zeit und Daten

- Technische Zeitstempel (Erstellungs-/Änderungszeitpunkte, Revisionszeitpunkte) sind **UTC** und werden als ISO 8601 mit Offset `Z` gespeichert und übertragen.
- Fachliche Daten (Buchungsjournal-Datum, Fälligkeiten, Stichtage) sind **Datumswerte ohne Zeitkomponente**.
- Die fachliche Monatszuordnung erfolgt nach **Europe/Berlin**. Ein Kalendermonat ist immer der 1. bis zum Monatsende.
- Das tatsächliche Ausgabedatum bestimmt die Zurechnung von Ausgaben und Richtwertauswertung. Einnahmen können zusätzlich einen **Finanzierungsmonat** tragen; das ist keine Verschiebung der tatsächlichen Kontobewegung. Eine Gehaltszahlung am 28. oder am 30. Oktober finanziert in beiden Fällen den November.
- Unterschiedliche Gehaltstermine verschieben weder Monatsbeginn noch Richtwertzeitraum. Das Eingangsdatum beeinflusst die Liquidität, nicht die Dauer des Richtwertzeitraums.

### 2.3 Deterministische Datumsregeln

- **31. des Monats:** Eine Fälligkeit mit Tag 31 in Monaten mit weniger als 31 Tagen rückt auf den letzten Tag des Monats (z. B. 31. → 28. Februar, 29. im Schaltjahr).
- **Februar:** Wie oben; keine Sonderregeln pro Schaltjahr abweichend vom Kalender.
- **Quartale:** 1. Quartal = Januar–März, 2. = April–Juni, 3. = Juli–September, 4. = Oktober–Dezember.
- **Jahresgrenzen:** Laufzeit- und Gültigkeitsangaben über Jahreswechsel behalten ihren Kalenderbezug; „Gültigkeit ab Datum" gilt strikt ab dem 0-Uhr-Punkt des fachlichen Datums (Europe/Berlin) — eine Änderung ab Januar lässt Dezember unberührt.
- **Gehalt:** Standardmäßig finanziert das Gehalt des Monats M den Folgemonat M+1 (Buchungsdatum und Finanzierungsmonat sind getrennte Angaben). Eine andere Zuordnung ist nur durch explizite Angabe des Finanzierungsmonats möglich.

### 2.4 Beständigkeit der Buchungen

- Bestätigte Buchungen werden **korrigiert oder storniert**, nie unbemerkt überschrieben.
- Änderungen an offenen zukünftigen Vorgängen gelten standardmäßig; rückwirkende Korrekturen sind explizit.
- Alle Datums- und Rundungsregeln dieses Abschnitts sind deterministisch: gleiche Eingaben erzeugen in allen Instanzen (Host, Android) identische Ergebnisse.

---

## 3. Ledger-Entwurf

### 3.1 Kernbegriffe

Die Begriffe bleiben strikt getrennt und dürfen nicht verwechselt werden:

- **Account:** Reales Konto, z. B. Hauptkonto bei der Bank, Freizeitkonto.
- **Pool:** Zweck, in den Geld eingeordnet ist, z. B. Flex, Freizeit.
- **Kategorie/Unterkategorie:** Alltagsordnung von Ausgaben (z. B. Lebensmittel).
- **Steuerkategorie/Steuerunterkategorie:** Eigener, frei anlegbarer Baum, unabhängig von Alltagskategorien, Konten und Pools.
- **Vorhaben:** Separates Sammelziel (z. B. Urlaub) mit eigener Reservierung; Vorhaben sind von Konto, Pool und Kategorie getrennt.

Steuerkategorien: Die Steueransicht besitzt eigene Kategorien und Unterkategorien. Je relevantem Split wird eine Steuerkategorie gewählt. Pro Ausgabe/Split gilt: steuerlich relevant ja/nein, Steuerkategorie mit optionalen Unterkategorien, optional Notiz und ein gekennzeichneter Teilbetrag, der den Splitbetrag nicht übersteigen darf. Die Kennzeichnung ist unabhängig von Konto, Pool und normaler Ausgabenkategorie; sie verändert keinen Saldo und keinen Richtwert. Kategorien/Bereiche können Kennzeichnung und Steuerkategorie vorbelegen (Vererbung, explizites Übersteuern möglich; ein explizites Nein darf nicht wieder durch Vererbung ersetzt werden). Bei Erstellung wird die wirksame Vorbelegung in den Posten übernommen; spätere Änderungen an Kategorien ändern historische Kennzeichnungen nicht.

### 3.2 Buchung (Booking)

Eine Buchung ist der kleinste unsplittbare Buchungssatz des Journals:

- **Identität:** Stabile `BookingId`.
- **Fachdatum:** Tatsächliches Datum (Abschnitt 2.2); Einnahmen optional zusätzlich **Finanzierungsmonat**.
- **Typ:** Einnahme, Ausgabe, Kontotransfer, Umwidmung, Kombination (Transfer + Umwidmung), Vormerkung/Bestätigung, Reservierung, Abschluss/Abbruch, Storno, Erstattung, Kontostandskorrektur, Anfangsbestand.
- **Notiz:** Freitext, optional.
- **Basisrevision:** Für Revision und Konfliktprüfung (Abschnitt 5.5).
- **Zusammengehörige Postings:** Alle Postings einer Buchung (Account-/Pool-Postings, Reservierungsposten, Steuerkennzeichnungen je Teilposten) werden **atomar** gespeichert; eine Teilübernahme ist unmöglich.

Abfragen berechnen Salden aus den Grunddaten (Journaleinträgen). Es gibt keine gesondert gepflegten Saldo-Felder. Abgeleitete Salden müssen jederzeit rekonstruierbar bleiben: Jeder Saldowert in jeder Oberfläche muss sich aus den Postings exakt berechnen lassen.

### 3.3 Postings

Je Buchung werden **AccountPostings** (Kontobewegungen) und **PoolPostings** (Poolbewegungen) gebucht:

- **Tatsächliche Einnahme:** Account+ (Eingang) und Pool+ (Zuweisung erhöht den Poolbestand). Offene Deckung des Finanzierungsmonats berücksichtigen (Abschnitt 4).
- **Tatsächliche Ausgabe:** Account− (Abgang) und Pool− (Verbrauch), aufgeteilt je Split. Zugeordnete Reservierung sinkt; laufender Bedarf zählt zum Richtwert.
- **Reiner Kontotransfer:** Account− (Quelle sinkt), Account+ (Ziel steigt); der Gesamtpoolbestand bleibt unverändert. Keine Ausgabe; der Zweck bleibt erhalten.
- **Umwidmung:** Konten unverändert; Pool− (Quelle sinkt) und Pool+ (Ziel steigt). Keine Richtwertausgabe.
- **Reservierung:** Konten unverändert, Poolbestand unverändert; der frei verfügbare Anteil sinkt, die Reservierung steigt.
- **Abschluss/Abbruch eines Vorhabens:** Konten unverändert, Poolbestand unverändert; der Rest wird im Herkunftspool wieder frei.
- **Storno:** Gegenbewegung zu Konto und Pool; verknüpft mit der stornierten Buchung; Auswertungen werden nachvollziehbar korrigiert.

**Splitregel:** Die Splitbeträge einer Ausgabe summieren sich exakt zum Gesamtbetrag der Buchung. Abweichung ist ein Fehler.

### 3.4 Signierte Konto-Pool-Zuordnung (AccountPoolAllocation)

Die Zuordnung ist eine Matrix Account × Pool mit **signierten Cent-Werten**. Sie wird aus den Postings abgeleitet und erfüllt zwingend:

- **Zeilensumme je Account = tatsächlicher Kontostand.**
- **Spaltensumme je Pool = Poolbestand.**
- Eine **negative Zuordnung** bezeichnet eine explizite interne Vorfinanzierung. Sie muss mit einer offenen Ausgleichsposition (Abschnitt 3.5) verknüpft sein und wird **nicht** als vorhandenes Geld angezeigt.
- Freie Mittel dürfen nicht doppelt auf zwei Konten erscheinen.

**Regelanker (konkreter Fall):** Hauptkonto/Flex 1.000 € und Freizeitkonto/Freizeit 300 €. Nach 85 € Einkauf vom Hauptkonto (65 € Flex, 20 € Freizeit) stehen beim Hauptkonto die Zuordnungen **Flex 935 €** und **Freizeit −20 €**; auf dem Freizeitkonto Freizeit 300 €. Die realen Konten sind **915/300 €**, die Pools **935/280 €**. Der offene Ausgleich von 20 € erklärt die Vorfinanzierung. Nach dessen Transfer stehen die Konten **935/280 €**, die Hauptkonto-Freizeit-Zuordnung ist **0 €**, der Freizeitpool weiterhin **280 €**. Vor dem Ausgleich lagen 300 € physisch am Freizeitkonto; diese 300 € enthalten 20 € bereits verbrauchtes Geld (verbraucht vom Hauptkonto), sodass dort nur **280 €** als frei angezeigt werden.

### 3.5 Offene interne Ausgleichspositionen

- Eine Ausgleichsposition entsteht, wenn ein Konto für Pools „vorfinanziert" hat (negatives `AccountPoolAllocation`).
- **Teilbegleichung** auf einen Ausgleich ist möglich; die Position sinkt um den Teilbetrag und bleibt offen, bis sie 0 erreicht.
- Automatische Ausgleichsvorschläge brauchen eine **deterministische bevorzugte Poolquelle**. Bei mehreren geeigneten Konten wählt der Nutzer die Quelle.
- Die Kernfunktion „Kontoverteilung mit internen Ausgleichen" ist der **erste Kernfall** der manuellen Referenzfälle (Abschnitt 6, Fall 4 „Ausgleich").

### 3.6 Freie Verfügbarkeit

Zwei getrennte Werte, die niemals vertauscht werden dürfen:

- **Global freier Poolbestand** (z. B. frei verfügbares Flex insgesamt).
- **Kontobezogene Liquidität:** der unmittelbar am Konto ausgebare Betrag, begrenzt durch den tatsächlichen Kontostand, Bindungen (Abschnitt 4) und offene Ausgleichsbedarfe.

Im Regelanker (3.4) sind 935 € Flex vorhanden, aber vor dem Ausgleich nur **915 €** physisch auf dem Hauptkonto. Bindungen und offene Ausgleichsbedarfe werden für die Kontoliquidität berücksichtigt; sie reduzieren den bereits korrekt berechneten Gesamtpool **nicht nochmals** (keine doppelte Abrechnung).

Negative Poolbestände bzw. Deckungslücken bleiben **sichtbar**; sie werden nicht durch stille Entnahme aus anderen Pools verdeckt. Freie Verfügbarkeit eines Zielkontos ist stets durch dessen tatsächlichen Stand und Verpflichtungen begrenzt.

### 3.7 Anfangsbestand

- Der Anfangsbestand wird mit **Stichtag** erfasst und **explizit auf Pools verteilt** (Verteilung ist Teil der Vorgabedaten).
- Er ist **keine Einnahme**: kein Finanzierungsmonat, keine Deckungsberechnung, keine Zuweisung über Einnahme-Logik.
- Die explizite Systemposition **„noch nicht zugeordnet"** bleibt sichtbar und zählt **nicht automatisch** zu Flex.
- **Idempotenz:** Dieselbe Anfangsbestands-Vorgabe (gleiche Identität/Stichtag-Kombination) erzeugt keine doppelte Buchung — die Anwendung muss die Vorgabe erkennen und ablehnen bzw. idempotent behandeln (Umsetzung und Test in Schritt 3).

### 3.8 Sonderfälle

- **Storno einer falschen Buchung:** Gegenbewegung auf Konto und Pool, verknüpft mit der Originalbuchung. Eine Rückgabe ist **nicht** dasselbe wie das Storno einer fehlerhaften Eingabe.
- **Echte Händlererstattung:** Eingang am **Erstattungstag** (tatsächliches Datum), Rückfluss zum **ursprünglichen Pool**. Auswertung: laufender Bedarf — Nettoausgaben im Erstattungsmonat reduzieren sich; Brutto und Erstattungen werden separat ausgewiesen. Eine Erstattung auf ein abgeschlossenes Vorhaben fließt standardmäßig in den Herkunftspool zurück und **öffnet das Vorhaben nicht automatisch** wieder.
- **Kontostandskorrektur:** Dokumentierte Differenz mit expliziter Zuordnung; sie verwendet **keine** normale Einnahmen-/Ausgabenkategorie. Es gibt kein stilles Überschreiben eines Saldos.

---

## 4. Deckungsperiode und Bindungen

**Deckungsperiode (verbindlich):** Offene vergangene Vorgänge + laufender Kalendermonat + ausdrücklich bereits finanzierte zukünftige Monate. Alle anderen Monate sind **nur Vorschau**. Alle zukünftigen Fixkosten abzuziehen ist untersagt — das würde Flex unbrauchbar machen.

Regeln:

1. Für finanzierte Monate werden **offene Verpflichtungen** und **festgelegte Anspar-/Umwidmungsziele gebunden**, auch wenn die dafür nötige Überweisung noch nicht ausgeführt ist.
2. Jede Bindung gehört zu einem **konkreten geplanten Vorgang** und wird **nur einmal erzeugt**. Mehrere Einnahmen für denselben Monat erzeugen **keine mehrfachen Verpflichtungen**.
3. Eine Vorschau für weitere Monate erzeugt **keine** Bindungen auf den gesamten zukünftigen Horizont.
4. Die Finanzierungszuordnung sperrt **nicht pauschal das gesamte Einkommen**. Der nach Verpflichtungen und vorgesehenen Zuweisungen verbleibende Anteil erhöht **Flex**.
5. Wer darüber hinaus Geld für spätere Ausgaben zurückhalten möchte, **reserviert es ausdrücklich** (Vorhaben/Reservierung, Abschnitt 5.3). Der feste Kalenderrichtwert bleibt davon unabhängig.
6. Ein **verspäteter Gehaltseingang erzeugt kein fiktives Geld**. Vorhandene freie Mittel können die Deckung übernehmen. Fehlen Mittel, zeigt die Anwendung die **Unterdeckung**. Tatsächlich entstandene Ausgaben dürfen trotzdem erfasst werden; negative Poolbestände bzw. Deckungslücken werden sichtbar (kein Verdecken, Abschnitt 3.6).
7. **Gesondert finanzierte Kosten** (z. B. Jahreskosten mit eigenem Finanzierungsmonat) werden nicht erneut von Flex abgezogen.
8. Eine **offene Verpflichtung und die spätere tatsächliche Zahlung** werden nie doppelt abgezogen: Die Bindung gilt bis zur Zahlung; die Zahlung löst die Bindung auf und bucht den Verbrauch — keine zweite Verpflichtung.
9. **Richtwert:** Fester Kalenderrichtwert pro Pool/Bedarf pro Kalendermonat. Das Eingangsdatum von Einkommen beeinflusst nur die Liquidität, nie die Dauer des Richtwertzeitraums.

---

## 5. Statusautomaten

### 5.1 Geplanter Vorgang (PlannedOccurrence aus Regel)

- Regeln besitzen Gültigkeit ab Datum, Rhythmus, Fälligkeit, Ende/Pause und erwartete Einnahmen/Ausgaben/Transfers. Je Regel und Termin existiert **exakt eine eindeutige PlannedOccurrence**.
- Zustände: `geplant` → `bestätigt` / `abgebrochen`; `geplant` → `korrigiert` (korrigierte offene Vorgänge bleiben `geplant`).
- **Bestätigung mit abweichendem tatsächlichem Betrag** wird verknüpft (gleiche Instanz) und erzeugt **keine zweite offene Instanz**.
- **Nachholung:** Fehlende Vorgänge nach Stillstand (z. B. App-Neustart, Flugmodus) werden nachgeholt — ohne doppelte Erzeugung (Idempotenz über Regel + Termin).
- **Gültigkeit ab Datum:** Eine Änderung ab Januar lässt Dezember unberührt (Abschnitt 2.3); rückwirkende Korrekturen sind explizit.
- Bestätigte Buchungen bleiben bei Änderungen erhalten (Abschnitt 2.4).

### 5.2 Transfer und Umwidmung

- **Vormerkung (geplant) → bestätigt / abgebrochen.**
- **Umwidmung kann sofort gelten**, während der tatsächliche Kontotransfer später bestätigt wird — getrennte Wirkungen und getrennte Status (Abschnitt 3.3).
- **Kombination** aus Transfer und Umwidmung ist ein eigener Vorgangstyp mit einer atomaren Buchung.
- **Offener Ausgleich** mit **Teilbegleichung** (Abschnitt 3.5) ist ein zulässiger Zwischenzustand.

### 5.3 Vorhaben

- Zustände: `reserviert` → `laufend` → `abgeschlossen` / `abgebrochen`.
- **Reservierung verändert keinen Kontostand** (nur die Reservierung im Pool).
- **Transfers erhalten Reservierungen** (der Zweck bleibt erhalten, Abschnitt 3.3).
- **Ausgabe reduziert die verbleibende Reservierung.**
- **Abschluss gibt nur den Rest** an den Herkunftspool frei; bereits verbrauchte Anteile fließen nicht zurück.
- **Erstattung nach Abschluss** → Herkunftspool; das Vorhaben wird **nicht automatisch** wieder geöffnet.

### 5.4 Trennung Entwurf / bestätigte Buchung / Sync-Operation

Drei getrennte Ebenen, die nicht vermischt werden dürfen:

- **Entwurf (Draft):** Lokal, noch nicht wirksam, darf verworfen werden.
- **Bestätigte Buchung (Booking):** Im Journal, atomar (Abschnitt 3.2), revidierbar **nur** über Korrektur- und Storno-Anwendungsfälle.
- **Sync-Operation (Operation):** Geräteste Einheit für die Synchronisation (Datenmodell in Schritt 0; Endpoints, Cursor-Verfahren, Generationen und Quittungsprotokoll sind Schritt 9/10):
  - Stabile **Operations-ID** (geräteseitig vergeben, dauerhaft).
  - **Payloadnachweis:** Hash des fachlichen Payloads.
  - **Erwartete Basisrevision** der Zielinstanz.
  - **Wiederholung derselben Operations-ID mit gleichem Inhalt = idempotent** (wird nicht erneut angewendet, wird quittiert).
  - **Derselben Operations-ID mit anderem Inhalt = Fehler** (sichtbar, kein Stillstand).
  - **Quittung nur nach expliziter Bestätigung** der Übernahme.
  - Der **Cursor** der Quelle fortschreitet erst nach **lokaler atomarer Übernahme** (ChangeLog-Eintrag + Buchung zusammen).
  - **Nicht quittierte Operationen werden bei Pull erhalten** (kein Verlust nach Server-Restore; Wiederabstimmung über die aufbewahrten Operationen und die Datenbank-/Protokollgeneration).

### 5.5 Revision

- Jede Fachentität (Konten, Pools, Kategorien, Regeln, Vorhaben, Buchungen, Operationen) trägt eine **Basisrevision**.
- Eine Änderung mit **abweichender Basisrevision** ist ein **Revisionskonflikt**: Fehler, sichtbar dem Nutzer, kein Last-Write-Wins.

---

## 6. Verweise

```
docs/step-0/
├── rechenvertrag.md      # dieses Dokument (verbindliche Spezifikation)
├── data/                 # JSON-Beispieldaten (gemeinsam für alle Referenzfälle)
└── referenzfaelle/       # 8 Referenzfälle, je Fall eine JSON-Datei
```

Die JSON-Dateien sind **Testfixtures** und werden ab Schritt 3 wiederverwendet. Die acht manuell bis auf den Cent durchgerechneten Fälle lauten: **Anfangsbestand, Einkommen, Split, Ausgleich, Kontotransfer, Umwidmung, Fixkostendeckung, Vorhabenabschluss**. Pro Fall werden die erwarteten Konto-, Pool-, Reservierungs- und freien Salden festgehalten; die Regelanker dieses Dokuments (u. a. 1.000 € / 300 € Start, 85 € Split, Konten 915/300 €, Pools 935/280 €, nach Ausgleich 935/280 €) müssen von den JSON-Werten exakt bestätigt werden.

**Abnahmekriterium Schritt 0:** Jeder Vorgang besitzt eindeutige Wirkungen; es gibt keine ungeklärte Doppelzählung in den acht Referenzfällen. Noch keine Oberfläche ist erforderlich.
