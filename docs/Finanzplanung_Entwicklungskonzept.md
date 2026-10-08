# Lario – Fachliches Entwicklungskonzept

**Projektname:** Lario. Anwendung, Repository, .NET-Solution und Namespace verwenden `Lario`. Der bisherige Projektname GSpend wird für die Neuentwicklung nicht weiterverwendet.

**Logo:** [Lario-Logo](../assets/branding/lario-logo.png). Der Entwurf zeigt ein Nest mit drei Familienpunkten unter einem schützenden Dachbogen in Dunkelblau und Türkis. Lario verbindet den persönlichen Bezug zu Mario mit der Idee eines Haushüters (Lar).

Stand: 8. Oktober 2026. Fachliches Konzept aus dem gemeinsamen Gespräch; Grundlage für ein neues Greenfield-Projekt ohne Übernahme von Altdaten. Technologiestack und Plattformen sind im Architekturplan festgelegt. Das neue Projekt startet ohne Altdaten.

## 1. Ziel und Schwerpunkt

Die Anwendung zeigt vor allem, wie viel Geld für flexible Ausgaben aktuell verfügbar ist und ob der laufende Bedarf im monatlichen Richtwert bleibt. Daneben verwaltet sie Einkommen, regelmäßige Verpflichtungen, Ansparungen, Vorhaben und mehrere reale Konten.

Manuelle Erfassung ist der primäre Arbeitsablauf. Ein Einkauf kann mehrere Zwecke enthalten; Bankabbuchungen allein liefern daher nicht die benötigte Aufteilung. Eine mobil nutzbare App mit eigenständiger Offline-Erfassung ist eine feste Anforderung. Die Hauptanwendung läuft ausschließlich lokal beziehungsweise im lokalen Netzwerk und muss unterwegs nicht erreichbar sein.

Das System unterstützt Entscheidungen und dokumentiert tatsächliche Zahlungen. Vorgemerkte Überweisungen werden vom Nutzer bei der Bank selbst durchgeführt und anschließend bestätigt.

## 2. Vier getrennte fachliche Bausteine

| Baustein | Bedeutung | Beispiele |
|---|---|---|
| Konto | Tatsächlicher Aufbewahrungs- und Zahlungsort | Hauptkonto, Wohnungskonto, Freizeitkonto, verzinstes Rücklagenkonto |
| Geldpool | Zweckbindung verfügbarer Mittel | Flex, Freizeit, jährliche Fixkosten, Rücklagen |
| Kategorie | Auswertungszuordnung von Einnahmen und Ausgaben | Lebensmittel, Haus → Energie, Wohnung → Wohngeld |
| Planungsart | Verhalten eines Postens | Wiederkehrende Zahlung, variable Ausgabe, einmalige Einnahme, Reservierung |

Zusätzlich gibt es unabhängige Steuerkategorien mit Unterkategorien. Sie ordnen steuerlich markierte Teilbeträge ein und ersetzen nicht die normalen Ausgabenkategorien.

Ein Pool kann auf mehrere Konten verteilt sein. Ein Konto kann Mittel mehrerer Pools enthalten. Kategorien sind nicht an ein Konto oder einen Pool gebunden. Beispiel: Renovierungsmaterial gehört zu Haus → Renovierung und wird aus Flex bezahlt.

Konten, Pools, Kategorien und Unterkategorien sind über die Oberfläche erweiterbar. Die bisherigen Bereiche sind Startkonfigurationen. Bereits verwendete Einträge werden archiviert, damit historische Buchungen erhalten bleiben.

## 3. Flex: Verfügbarkeit und Richtwert

Flex umfasst alle Mittel, die nach Berücksichtigung von Verpflichtungen und Zweckbindungen frei zugeordnet werden können. Nicht verbrauchtes Geld bleibt über Monatsgrenzen erhalten. Es gibt keine automatische Abschöpfung zum Monatsende.

Die Startseite zeigt:

- Flex insgesamt, einschließlich darin enthaltener Vorhabenreservierungen.
- Davon für Vorhaben reserviert.
- Flex frei und unverplant.
- Verteilung auf Konten, insbesondere den auf dem Hauptkonto liegenden Anteil.
- Monatlicher Richtwert für laufenden Bedarf und bisherige entsprechende Ausgaben.
- Größere Ausgaben beziehungsweise Vorhaben separat.
- Restliche Tage und optional den verbleibenden Richtwert pro Tag/Woche als Orientierung.
- Ausstehende Kontoausgleiche und erforderliche Rücküberweisungen.

Der monatliche Richtwert ist ein Zielwert, keine harte Ausgabengrenze. Eine Überschreitung verbraucht vorhandenes Flex-Geld, wird aber sichtbar. Richtwertänderungen gelten ab einem gewählten Monat. Der Richtwert wird monatlich neu betrachtet; das vorhandene Geld wird weitergeführt.

Größere geplante Anschaffungen zählen standardmäßig nicht zum Richtwert des laufenden Bedarfs, reduzieren aber das Flex-Geld. Diese Einordnung soll explizit am Posten beziehungsweise Vorhaben erkennbar sein.

## 4. Schnelle manuelle Ausgabenerfassung

Minimaler Ablauf: Betrag, Kategorie, Zahlungsort und finanzierender Pool; häufige Angaben werden sinnvoll vorbelegt. Notiz und Datum können ergänzt werden. Ein Einkauf kann auf mehrere Teilposten aufgeteilt werden. Die Summe der Teilposten muss dem Gesamtbetrag entsprechen.

Beispiel: 85 € vom Hauptkonto bezahlt, davon 65 € Lebensmittel/Seife aus Flex und 20 € Geschenk aus Freizeit. Das Hauptkonto sinkt um 85 €, Flex um 65 € und Freizeit um 20 €. Liegt das Freizeitgeld auf dem Freizeitkonto, entsteht zusätzlich ein offener Ausgleich von 20 € zum Hauptkonto. Dessen Bestätigung darf weder eine zweite Ausgabe noch einen erneuten Poolabzug erzeugen.

Ausgaben sollen korrigierbar sein. Änderungen müssen sämtliche abhängigen Salden, Reservierungen und offenen Ausgleiche konsistent aktualisieren. Stornos fehlerhafter Buchungen und echte Händlererstattungen werden getrennt geführt. Erstattungen gehen zum ursprünglichen Pool zurück; abgeschlossene Vorhaben werden dadurch nicht automatisch wieder geöffnet. Manuelle Kontostandsabgleiche erzeugen dokumentierte Korrekturen statt stiller Saldoänderungen.

Ausgaben und Splits können steuerlich gekennzeichnet werden. Vorhandene PDFs/Bilder lassen sich in der Hauptanwendung sofort oder später anhängen; sie verändern keine Geldbeträge.

## 5. Monatliche und unregelmäßige Verpflichtungen

Regelmäßige Posten werden einmal angelegt: Bezeichnung, Betrag, Kategorie, Konto, finanzierender Pool beziehungsweise Deckungsregel, Rhythmus, Fälligkeit, Beginn und optional Ende. Monatliche, vierteljährliche, jährliche und weitere konfigurierbare Rhythmen sind vorgesehen.

Die Anwendung erzeugt erwartete Zahlungen automatisch. Der Nutzer bestätigt sie als bezahlt oder korrigiert tatsächlichen Betrag und Datum. Geplante und gebuchte Zahlungen bleiben verknüpft, um Doppelzählung zu verhindern.

Hauskosten wie Darlehen, Wärmepumpenrate und Stromabschlag erscheinen gemeinsam mit anderen Verpflichtungen in der Monatsübersicht. Sie behalten ihre eigene Kategorie und ihre Auswertung im Hausbereich.

Änderungen werden zeitlich versioniert: beispielsweise neues Wohngeld ab Januar oder neuer Telefonpreis ab März. Vergangene Monate werden nicht durch spätere Standardwerte überschrieben.

## 6. Ansparung für jährliche und vierteljährliche Kosten

Ein eigener Pool und gegebenenfalls ein eigenes Konto sammeln die monatlichen Zuführungen. Jahreskosten / 12 ergeben die monatliche Ansparung; eine vierteljährliche Rechnung / 3 ergibt ihren monatlichen Anteil.

Zuführung und Rechnung sind getrennt: Die Zuführung bindet vorhandenes Geld und kann mit einem internen Kontotransfer verbunden sein. Die Rechnung verbraucht diese Mittel und ist die tatsächliche Ausgabe. Beides darf nicht als doppelte Ausgabe erscheinen.

Bei Einrichtung werden vorhandenes Guthaben und nächste Fälligkeiten berücksichtigt. Eine reine Teilung durch 12 reicht nicht, wenn eine Rechnung schon vor vollständiger Ansparung fällig wird. Unterdeckung wird sichtbar und kann durch eine einmalige Zuweisung gedeckt werden.

## 7. Einnahmen

Regelmäßige und einmalige Einnahmen werden unterstützt: Gehalt, Kaltmiete, Nebenkostenvorauszahlungen, Jahressonderzahlungen, Steuerrückzahlungen, Zinsen und frei definierbare weitere Arten.

Je Einnahme: erwarteter Betrag/Termin, tatsächlicher Betrag/Termin, Eingangskonto, Kategorie und Verteilungsregel. Einnahmen können auf mehrere Pools verteilt werden. Regelmäßige Einnahmen werden automatisch vorgeschlagen und beim Eingang bestätigt.

Erwartete Einnahmen gehören in die Vorschau. Erst tatsächlich eingegangene Beträge erhöhen aktuell verfügbare Mittel. Abweichungen zwischen Planung und Eingang werden angezeigt. Zinsen werden als tatsächliche Einnahme erfasst und einem gewählten Pool zugewiesen; sie werden nicht automatisch einem Vorhaben zugeschlagen.

Die Verteilung muss Verpflichtungen, Ansparungen und Restzuweisung an Flex berücksichtigen. Eigenüberweisungen zählen nie als neues Einkommen.

Buchungsdatum und Finanzierungsmonat sind getrennt. Gehalt am Monatsende finanziert standardmäßig den Folgemonat; der Richtwertzeitraum bleibt vom 1. bis zum Monatsende. Bereits finanzierte Monate erzeugen einmalige Bindungen für Verpflichtungen und vorgesehene Zuweisungen. Eine weiter reichende Vorschau bindet nicht automatisch sämtliche künftigen Kosten. Der verbleibende Anteil kann Flex zugeordnet werden; das gesamte Gehalt wird nicht pauschal gesperrt.

## 8. Kontotransfers und Umwidmungen

Zwei unabhängig kombinierbare Aktionen:

1. Konto wechseln: Geld behält seinen bisherigen Pool und gegebenenfalls seine Vorhabenreservierung.
2. Zweck ändern: Geld wird zwischen Pools umgewidmet, mit oder ohne Kontotransfer.

Beispiele: Flex auf das verzinste Konto parken; Flex dem Freizeitpool zuweisen und aufs Freizeitkonto überweisen; eine Rücklage für eine Sondertilgung reservieren.

Transfers können einmalig oder wiederkehrend sein. Wiederkehrende Beträge gelten ab einem gewählten Monat. Für vorgemerkte Aktionen müssen Zweckbindung und tatsächlicher Kontostand getrennt dargestellt werden. Eine bereits wirksame Zweckreservierung darf bei Bestätigung nicht nochmals erfolgen. Beim Abbrechen einer Vormerkung wird deren Bindung aufgehoben.

Kontotransfers verändern die Summe aller Konten nicht. Umwidmungen verändern die Summe aller Pools nicht. Beides zählt nicht als Konsumausgabe und belastet keinen Richtwert.

## 9. Vorhaben und Reservierungen

Vorhaben besitzen Name, Kategorie, Herkunftspool, reservierten Betrag, optionale Zielsumme/Termin und Status. Beispiele: Renovierung, Ausbau, Urlaub oder Sondertilgung.

Eine Reservierung reduziert den unverplanten Anteil des Pools, nicht dessen Gesamtbestand und keinen Kontostand. Reservierte Mittel können über mehrere Konten verteilt werden. Zusätzliche Reservierungen und teilweise Freigaben sind möglich.

Ausgaben können einem Vorhaben zugeordnet werden. Sie reduzieren Poolbestand und verbleibende Reservierung um den tatsächlich gedeckten Betrag. Bei Überschreitung der Reservierung muss zusätzliche Finanzierung explizit erfasst werden; eine negative Reservierung entsteht nicht stillschweigend.

Aktionen: abschließen und Rest freigeben; abbrechen und Rest freigeben. Bereits erfolgte Ausgaben bleiben erhalten. Restgeld kehrt in den unverplanten Anteil des Herkunftspools zurück. Die Freigabe erzeugt keinen Kontotransfer und keine weitere Ausgabe. Abgeschlossene Vorhaben bleiben auswertbar.

Beispiel: 2.000 € Renovierungsreserve, davon 500 € auf dem Hauptkonto und 1.500 € auf dem verzinsten Konto. Rücküberweisung von 1.500 € ändert nur den Ort. Nach Ausgaben von 1.750 € werden beim Abschluss 250 € wieder frei.

## 10. Eigener Wohnungsbereich

Die Wohnung erhält eine Detailansicht für Einnahmen, Kosten, Kontobewegungen und Ansparungen. Kaltmiete und Nebenkostenvorauszahlungen werden getrennt erfasst. Kreditraten, Erbbauzins, Wohngeld und weitere Kosten werden einzeln gepflegt und ab ihrem Änderungsdatum berücksichtigt.

Aktuelles vereinbartes Beispiel:

| Vorgang monatlich | Wohnungskonto | Hauptkonto |
|---|---:|---:|
| Kaltmiete | +900,00 € | — |
| Nebenkostenvorauszahlung | +200,00 € | — |
| Interner Transfer | −800,00 € | +800,00 € |
| Wohnungskosten insgesamt | — | −958,68 € |
| Veränderung | +300,00 € | −158,68 € |

Der Zahlungsüberschuss der Wohnung ist 141,32 €. Die bewusste Ansparung auf dem Wohnungskonto beträgt 300 €; darin stecken zusätzlich 158,68 € aus sonstigen Mitteln auf dem Hauptkonto. Diese zusätzliche Bindung darf nicht als frei verfügbares Flex-Geld erscheinen. Der Zahlungsüberschuss ist eine Liquiditätskennzahl, keine steuerliche Gewinnberechnung.

| Transfer aufs Hauptkonto | Ansparung Wohnungskonto | Zusätzliche Deckung vom Hauptkonto |
|---|---:|---:|
| 700 € | 400 € | 258,68 € |
| 800 € | 300 € | 158,68 € |
| 900 € | 200 € | 58,68 € |

Der Transfer ist frei änderbar ab Monat X. Kostenänderungen wirken automatisch auf die Auswertung und den Bedarf an sonstigen Mitteln. Wohnungsmittel können für Renovierungen oder Sondertilgungen am Haus zugewiesen werden.

Nebenkostenvorauszahlungen bleiben separat nachvollziehbar; erwartete Nachzahlungen oder Rückzahlungen aus Abrechnungen lassen sich planen und später buchen. Die genaue Aufteilung der 958,68 € muss bei Einrichtung aus den tatsächlichen Einzelposten erfolgen, nicht anhand erfundener Beträge.

## 11. Berechnungsregeln und Konsistenz

- Kontostand = Anfangsbestand + gebuchte Eingänge − gebuchte Abgänge. Transfers haben eine zusammengehörige Quell- und Zielbuchung.
- Poolbestand = Anfangszuweisung + tatsächliche Zuweisungen − tatsächliche Ausgaben + eingehende − ausgehende Umwidmungen.
- Freier Poolanteil = Poolbestand − noch wirksame Reservierungen/Zweckbindungen. Dieselbe Verpflichtung wird nur einmal gebunden.
- Die Summe aller zugeordneten Mittel entspricht der Summe der berücksichtigten Kontostände; offene kontenübergreifende Ausgleiche müssen dabei nachvollziehbar dargestellt sein.
- Eine bestätigte Verpflichtung ersetzt ihre noch offene Bindung durch den tatsächlichen Abgang. Sie wird nicht zusätzlich zur ursprünglichen Planung abgezogen.
- Verpflichtungen, die bereits aus einem gesonderten Pool gedeckt werden, reduzieren Flex nicht nochmals.
- In Vorschau und aktueller Verfügbarkeit werden erwartete und tatsächliche Vorgänge getrennt gehalten. Prognosen sind eindeutig beschriftet.
- Beginn eines neuen Monats erzeugt keine erneute Einnahme aus übernommenem Restgeld.
- Negative freie Mittel und Deckungslücken werden sichtbar, nicht durch stille Verschiebung aus anderen Pools ausgeglichen.

Bei Zahlungen aus einem anderen Konto als dem Aufbewahrungsort des finanzierenden Pools wird ein offener Ausgleich geführt. Bereits verbrauchtes Geld darf dadurch auf dem ursprünglichen Konto nicht weiterhin als frei erscheinen. Technisch wird eine signierte Konto-Pool-Zuordnung mit expliziten internen Ausgleichen verwendet. Negative Zuordnungen stehen für Vorfinanzierung, nicht für zusätzlich vorhandenes Geld. Die Summe je Konto entspricht dem realen Kontostand, die Summe je Pool dessen Bestand. Global freies Flex-Geld und sofort auf einem bestimmten Konto ausgebbare Mittel werden getrennt angezeigt. Das konkrete Rechenbeispiel steht im Implementierungsplan.

## 12. Ansichten

1. Mobile Startseite mit schneller, aufteilbarer Ausgabenerfassung; Flex-Anzeige optional.
2. Monatsübersicht: Einkommen, Verpflichtungen, Ansparungen, Richtwert und freie Mittel.
3. Konten: tatsächliche Bestände, Zweckverteilung, offene Transfers/Ausgleiche und Kontovorschau.
4. Wohnung: Einnahmen/Kosten einzeln, Zahlungsüberschuss und bewusste Ansparung.
5. Vorhaben: reserviert, ausgegeben, verbleibend, Kontoverteilung und Abschluss.
6. Auswertung nach Zeitraum, Kategorie, Pool, Konto und Vorhaben; Transfers separat.
7. Verwaltung für Konten, Kategorien, Pools, Steuerkategorien, wiederkehrende Regeln und Änderungen ab Datum.
8. Steueransicht nach Zahlungsjahr und Steuerkategorien/Unterkategorien: Teilbeträge, Summen, Nachweise, fehlende Belege, CSV und Belegpaket.

## 13. Einrichtung, bestehendes Projekt und Datensicherheit

Ein neuer Einrichtungsassistent wird erstellt: Konten und Anfangsbestände, Poolzuweisungen, Einkommen, Verpflichtungen, Ansparungen, Transfers, Richtwert und bestehende Vorhaben. Anfangsbestände sind keine Einnahmen.

Die Anwendung wird als Greenfield-Projekt neu erstellt. Die bisherige Python-Anwendung enthält keine Nutzdaten und dient höchstens als Referenz für Bedienabläufe. Migration, Altdatenimport und Kompatibilität zum alten Datenmodell entfallen. Schema-Migrationen innerhalb der neuen Anwendung bleiben für spätere Updates erforderlich.

Ergänzende technische Anforderungen aus dem Einsatzkontext: Geldbeträge exakt in Cent/Decimal verarbeiten; synchronisierte Eingaben über stabile IDs vor Duplikaten schützen; Änderungen/Konflikte nachvollziehbar behandeln; Export, Sicherung und Wiederherstellung einschließlich angehängter Belegdateien anbieten. Diese Anforderungen sind besonders wegen des früheren Datenverlusts sinnvoll. Die Offline-Erfassung ist verpflichtend; die technische Umsetzung und das Synchronisationsverfahren sind im Architektur- und Implementierungsplan beschrieben. Nach Wiederherstellung des zentralen Datenbestands wird eine neue Synchronisationsgeneration gesetzt, um mobile Eingaben nicht stillschweigend zu verlieren.

### 13.1 Mobile App und Betrieb ohne Hauptanwendung

Der Hauptfokus der mobilen App ist die schnelle direkte Ausgabenerfassung unterwegs. Die Erfassung ist die primäre Ansicht; Planung, Verwaltung und detaillierte Auswertungen bleiben Aufgaben der Hauptanwendung. Eine mobile Flex-Anzeige ist optional und keine Voraussetzung für die erste Umsetzung. Die App darf eine eigenständige Anwendung oder dieselbe Software mit mobiler Oberfläche sein. Entscheidend ist, dass sie nach der Ersteinrichtung ohne laufende oder erreichbare Hauptanwendung funktioniert – auch ohne Internetverbindung und nach einem Neustart der mobilen App.

- Ausgaben einschließlich Datum, Kategorien, Konto, Pool, optionalem Vorhaben, Notiz, steuerlicher Kennzeichnung und Aufteilung auf mehrere Teilposten lokal erfassen und speichern. Kennzeichnung kann später ergänzt werden.
- Die hierfür benötigten Stammdaten nach einer vorherigen Synchronisierung lokal vorhalten. Bereits erfasste, noch nicht synchronisierte Ausgaben anzeigen und korrigieren können.
- Eingaben dauerhaft auf dem Mobilgerät speichern; sie dürfen bei Schließen, Neustart oder fehlgeschlagener Synchronisierung nicht verloren gehen. Eine nur vorübergehend geöffnete Oberfläche oder ein flüchtiger Zwischenspeicher reicht nicht.
- Noch nicht synchronisierte Eingaben und den Synchronisationsstatus sichtbar machen. Optional kann der zuletzt synchronisierte Flex-Stand mit lokalen Ausgaben fortgeschrieben werden; falls diese Anzeige umgesetzt wird, muss sie als möglicherweise veraltet gekennzeichnet sein.
- Im lokalen Netzwerk mit der erreichbaren Hauptanwendung synchronisieren; ein manueller Synchronisationsstart muss möglich sein. Außerhalb des lokalen Netzwerks ist keine Verbindung zur Hauptanwendung erforderlich.
- Neue Buchungen und Änderungen übertragen sowie aktualisierte Stammdaten und Finanzstände übernehmen. Jede Ausgabe genau einmal verbuchen, auch nach Wiederholung oder Verbindungsabbruch.
- Synchronisierungsfehler und widersprüchliche Änderungen sichtbar behandeln. Lokale Änderungen erst nach bestätigter Übernahme als synchronisiert markieren; Konflikte dürfen nicht stillschweigend Buchungen verlieren lassen oder überschreiben.

Die Architektur setzt weder öffentliche Erreichbarkeit der Hauptanwendung noch Cloudbetrieb oder einen unterwegs verfügbaren Server voraus. Plattform und Verpackung (separate App oder gemeinsame Anwendung mit dauerhaftem lokalem Speicher) sind technische Entscheidungen. Mindestumfang ist die unabhängige mobile Ausgabenerfassung, nicht die vollständige mobile Verwaltung aller Funktionen.

## 14. Abnahmeszenarien

| Szenario | Erwartetes Ergebnis |
|---|---|
| Einkauf 85 €, davon 65 € Flex und 20 € Freizeit | Konto −85 €, Flex −65 €, Freizeit −20 €; offener Ausgleich 20 € bei getrennten Konten |
| Ausgleich 20 € wird bestätigt | Nur Kontoverteilung/offener Ausgleich ändern sich; keine zweite Ausgabe |
| Flexgeld auf verzinstes Konto übertragen | Flexsumme und Reservierungen bleiben erhalten |
| 300 € Flex in Freizeit umwidmen | Flex −300 €, Freizeit +300 €; kein Einfluss auf Richtwertausgaben |
| Renovierung 2.000 € reserviert, 1.750 € bezahlt, abgeschlossen | 250 € werden frei; Ausgaben bleiben erhalten; Abschluss verändert kein Konto |
| Monatswechsel mit Flexrest | Rest bleibt erhalten; Richtwertauswertung startet neu |
| Erwartete Steuerrückzahlung | In Vorschau, noch nicht aktuell verfügbar |
| Geplante Fixzahlung wird bestätigt | Ein tatsächlicher Abgang; keine doppelte Bindung |
| Wohnung nach aktuellem Beispiel | 300 € angespart, 141,32 € Zahlungsüberschuss, 158,68 € zusätzliche Deckung |
| Wohnungstransfer ab Januar von 800 auf 700 € | Ab Januar Ansparung +100 € und Hauptkontozufluss −100 €; frühere Monate unverändert |
| Wohngeldänderung ab Januar | Nur zukünftige gültige Zahlungen und Prognosen ändern sich |
| Mobile Eingabe zweimal synchronisiert | Genau eine Ausgabe wird verbucht |
| Hauptanwendung ausgeschaltet, Mobilgerät ohne Netzwerk | Neue Ausgaben einschließlich Aufteilungen können erfasst und lokal gespeichert werden |
| Mobile App nach Offline-Erfassung geschlossen und neu gestartet | Ausgaben und ausstehender Synchronisationsstatus bleiben erhalten |
| Rückkehr ins lokale Netzwerk und Synchronisierung | Offline-Ausgaben werden einmal übernommen; aktualisierte Stammdaten und Finanzstände kommen auf das Mobilgerät |
| Verbindung während Synchronisierung unterbrochen, anschließend wiederholt | Keine verlorenen oder doppelten Buchungen; ausstehende Änderungen bleiben erkennbar |
| Optionale Flex-Anzeige unterwegs, falls umgesetzt | Letzter Synchronisationsstand und lokale Fortschreibung sind eindeutig erkennbar |

## 15. Umsetzungsvorschlag und noch technische Entscheidungen

Die verbindliche Reihenfolge und einzeln prüfbare Abnahmen stehen in Finanzplanung_Implementierungsplan.md. Die mobile Offline-Erfassung wird früh technisch erprobt; Altdatenmigration ist nicht Teil der Umsetzung.

Festgelegt sind Linux, C#/ASP.NET Core mit SQLite, Angular und eine Android-App mit Capacitor und SQLite. Der Planungsmonat ist immer der Kalendermonat. Gehalt am Monatsende finanziert standardmäßig den Folgemonat; das tatsächliche Eingangsdatum verändert weder Monatsbeginn noch Richtwertzeitraum. Ergänzende Regeln stehen im Implementierungsplan. Die unabhängige mobile Offline-Erfassung und spätere Synchronisierung im lokalen Netzwerk sind verbindliche Anforderungen. Eine automatische Bankanbindung ist für den beschriebenen Hauptablauf nicht erforderlich. Steuerberechnungen und vollständige Immobilienbuchhaltung sind bisher nicht Teil der vereinbarten Anforderungen.

Dieses Konzept definiert das Zielverhalten. Der Architekturplan beschreibt die technischen Bausteine; der Implementierungsplan legt die aktuellen Detailregeln, Arbeitsreihenfolge und Abnahmen fest. Eine Sichtung des alten Python-Projekts ist keine Voraussetzung.

## 16. Steuerkategorien und Nachweise im Grundumfang

Steuerlich relevante Ausgaben beziehungsweise Splits können einen markierten Teilbetrag, eine Steuerkategorie und eine Notiz erhalten. Steuerkategorien und Unterkategorien sind frei anlegbar und unabhängig von normalen Ausgabenkategorien. Je Split ist eine Steuerkategorie vorgesehen; mehrere Zuordnungen einer Rechnung werden über Teilposten abgebildet. Der markierte Teilbetrag überschreitet nie den zugehörigen Zahlbetrag.

Bereiche wie die Wohnung können die Kennzeichnung und Steuerkategorie vererben. Einzelne Posten können die Vorbelegung überschreiben, einschließlich eines ausdrücklichen Nein. Die beim Erfassen wirksame Vorbelegung wird im Posten gespeichert; spätere Änderungen überschreiben keine historischen Angaben. Wiederkehrende Posten übernehmen die Kennzeichnung. Die Kennzeichnung verändert keinen Konto-, Pool- oder Richtwertbetrag.

Die Steueransicht zeigt tatsächlich gezahlte relevante Teilbeträge nach Zahlungsjahr und Steuerkategorie, inklusive Unterkategorien und Gesamtbeträgen ohne Doppelzählung. Geplante Rechnungen, interne Transfers und Umwidmungen sind keine gezahlten steuerrelevanten Ausgaben. Relevante Posten ohne Kategorie und fehlende Nachweise sind filterbar. Erstattungen werden verknüpft und getrennt von Bruttozahlungen ausgewiesen.

Auswertungen und Originalbelege können als CSV beziehungsweise ZIP-Belegpaket mit Zuordnungsliste exportiert werden. Die Kennzeichnung ist eine Sammlung für die Steuerunterlagen und keine verbindliche Prüfung der Abzugsfähigkeit oder Steuerberechnung. Der Zahlungsjahresfilter ersetzt keine steuerrechtliche Jahreszuordnung.

## 17. Belegablage jetzt, mobile Belegerfassung später

Bereits im Grundumfang kann die Hauptanwendung vorhandene PDF-, JPEG- und PNG-Dateien an Ausgaben anhängen, anzeigen und herunterladen. Mehrere Dateien pro Rechnung und ein gemeinsamer Beleg für mehrere Splits sind möglich. Ein fehlender Beleg blockiert keine Ausgabe. Dateien und ihre Verknüpfungen werden mitgesichert und zusammen wiederhergestellt; Belegänderungen verändern keine Finanzbeträge.

Spätere Android-Erweiterung: Kameraaufnahme, digitaler Import/Teilen, lokale Offline-Dateiablage und späterer LAN-Dateitransfer. Geldbuchung und Dateitransfer haben getrennte Status, damit ein fehlgeschlagener Upload keine Ausgabe verliert oder verdoppelt. Noch später kann eine Texterkennung bearbeitbare Entwürfe vorschlagen; nur bestätigte Angaben werden gebucht. Ein externer Beleg-/OCR-Dienst ist nicht Teil des bisherigen Plans.

## 18. Ergänzende Abnahmen

Steuerlich markierte 80 € einer 120-€-Ausgabe ändern den Finanzsaldo um 120 € und erscheinen mit 80 € in der Steuersammlung. Kategorievorbelegungen und ausdrückliche Ausnahmen bleiben historisch stabil. Unterkategorien summieren sich exakt einmal. PDF-/Bildnachweise lassen sich nach Neustart öffnen und mit Zuordnung exportieren. Nach Restore sind Buchungen und Belegdateien zusammen verfügbar. Mobile Geräte erkennen eine neue Restoregeneration und behalten neuere gespeicherte Eingaben für die Wiederabstimmung.
