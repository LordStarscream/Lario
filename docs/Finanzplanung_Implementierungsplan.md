# Lario – Geprüfter Implementierungsplan

**Projektname:** Lario. Anwendung, Repository, .NET-Solution und Namespace verwenden `Lario`. Der bisherige Projektname GSpend wird für die Neuentwicklung nicht weiterverwendet.

**Logo:** [Lario-Logo](../assets/branding/lario-logo.png). Der Entwurf zeigt ein Nest mit drei Familienpunkten unter einem schützenden Dachbogen in Dunkelblau und Türkis. Lario verbindet den persönlichen Bezug zu Mario mit der Idee eines Haushüters (Lar).

Stand: 8. Oktober 2026. Verbindlicher Arbeitsplan für eine Greenfield-Neuentwicklung. Ergänzt und konkretisiert Fachkonzept und Architekturplan. Bei Abweichungen gelten die hier dokumentierten aktuellen Entscheidungen.

## 1. Festgelegte Grundlage

- Neues Projekt; in der früheren Python-Anwendung existieren keine Nutzdaten. Kein Altdatenimport, keine Portierung des alten Datenmodells, kein Kompatibilitätsbetrieb.
- Hauptprogramm unter Linux: C#/.NET 10, ASP.NET Core, EF Core und SQLite; Angular im Browser.
- Erste Mobilplattform ist Android: Angular/Capacitor, eingebettete Oberfläche, eigene SQLite-Datenbank. Schwerpunkt ausschließlich schnelle Ausgabenerfassung, Splits, eigene Eingaben und deren Synchronisationsstatus. Mobile Finanzübersicht optional.
- Hauptprogramm läuft lokal oder im lokalen Netzwerk. Mobil funktioniert nach Ersteinrichtung ohne Host und ohne Internet. Synchronisation zunächst manuell im Heimnetz.
- Der Monat ist immer ein Kalendermonat vom 1. bis zum Monatsende. Unterschiedliche Gehaltstermine verschieben weder Monatsbeginn noch Richtwertzeitraum.
- Gehalt am Monatsende finanziert standardmäßig den Folgemonat. Buchungsdatum und Finanzierungsmonat sind getrennte Angaben. Erwartetes Einkommen ist Vorschau, tatsächlich eingegangenes Geld ist Bestand.
- Flex trägt unverbrauchte Mittel weiter. Sein Richtwert gilt für laufenden Bedarf. Vorhaben sind separat. Konto, Pool, Kategorie und Vorhaben bleiben getrennt.

## 2. Review: Lücken und deren Behandlung

| Befund | Entscheidung beziehungsweise Umsetzung |
|---|---|
| Alte Dokumente enthielten Bestandsprüfung und Altdatenmigration | Entfällt vollständig. Nur spätere Schemaänderungen der neuen Anwendung benötigen Migrationen. |
| Alle zukünftigen Fixkosten abzuziehen würde Flex unbrauchbar machen | Verbindliche Deckungsperiode: offene vergangene Posten, aktueller Monat und ausdrücklich bereits finanzierte zukünftige Monate. Andere Monate sind nur Vorschau. |
| Gehalt am Monatsende könnte als freies Geld erscheinen | Finanzierungsmonat standardmäßig Folgemonat; dessen Verpflichtungen und geplante Zuweisungen werden einmal gebunden. |
| Gehaltseingang könnte den Budgetanfang verschieben | Kalendermonate bleiben fest. Das Eingangsdatum beeinflusst Liquidität, nicht die Dauer des Richtwertzeitraums. |
| Geplanter Transfer, Umwidmung und echte Überweisung waren vermischt | Getrennte Wirkungen und Status. Eine Umwidmung kann sofort gelten; der tatsächliche Kontotransfer wird später bestätigt. |
| Konto-/Pool-Zuordnung mit offenem Ausgleich nicht vollständig definiert | Explizite interne Ausgleichspositionen; freie Mittel dürfen nicht doppelt auf zwei Konten erscheinen. Als erster Kernfall testen. |
| Anfangsbestände und unzugeordnetes Geld | Anfangsbestand wird auf Pools verteilt. Eine explizite Systemposition „noch nicht zugeordnet“ bleibt sichtbar und zählt nicht automatisch zu Flex. |
| Erstattungen, Stornos und Kontostandskorrekturen fehlten | Eigene Vorgänge mit Buchungsbezug und nachvollziehbarer Poolzuordnung; kein stilles Überschreiben eines Saldos. |
| Rückwirkende Änderungen könnten erledigte Posten neu erzeugen | Bestätigte Buchungen bleiben erhalten. Änderungen gelten standardmäßig für offene zukünftige Vorgänge; rückwirkende Korrekturen sind explizit. |
| Sync nach Server-Restore könnte bestätigte mobile Eingaben verlieren | Synchronisationsgeneration, aufbewahrte mobile Operationen und geregelte Wiederabstimmung nach Restore. |
| Archivierte Stammdaten können noch in Offline-Eingaben vorkommen | Historische Referenzen bleiben gültig; neue Auswahl wird eingeschränkt. Keine automatische Löschung alter Verweise. |
| „Offline“ war nur technisch allgemein beschrieben | Appneustart, Flugmodus, lokaler Commit und Abbruch nach Servercommit sind Pflichtabnahmen auf echtem Androidgerät. |

Die folgenden Detailregeln sind Umsetzungsvorgaben dieses Plans. Weitere Produktentscheidungen sind für den ersten vollständigen Durchstich nicht erforderlich. Steuerberechnung und verbindliche Abzugsfähigkeitsprüfung, Bankanbindung, Mehrwährung, iOS, mobile Kamera-/Belegimports, OCR und automatische Hintergrundsynchronisation gehören nicht zum ersten Umfang. Das Anhängen vorhandener PDFs und Bilder in der Hauptanwendung sowie die manuelle Steuerkennzeichnung und Auswertung gehören zum Grundumfang (Schritt 7a und Abschnitt 9). Die gewünschte spätere mobile Belegerweiterung ist in Abschnitt 8 festgehalten.

## 3. Präzise Regeln vor dem ersten Finanzcode

### Monate und Deckung

Buchungen haben ein tatsächliches Datum. Einnahmen können zusätzlich einen Finanzierungsmonat haben; das ist keine Verschiebung ihrer tatsächlichen Kontobewegung. Ausgaben und Richtwertauswertung folgen dem tatsächlichen Ausgabedatum. Eine Gehaltszahlung am 28. oder am 30. Oktober finanziert in beiden Fällen den November.

Für finanzierte Monate werden offene Verpflichtungen und festgelegte Anspar-/Umwidmungsziele gebunden, auch wenn die dafür nötige Überweisung noch nicht ausgeführt ist. Jede Bindung gehört zu einem konkreten geplanten Vorgang und wird nur einmal erzeugt. Mehrere Einnahmen für denselben Monat erzeugen keine mehrfachen Verpflichtungen. Eine Vorschau für weitere Monate erzeugt keine Bindungen auf den gesamten zukünftigen Horizont.

Die Finanzierungszuordnung sperrt nicht pauschal das gesamte Gehalt. Der nach Verpflichtungen und vorgesehenen Zuweisungen verbleibende Anteil erhöht Flex. Wer darüber hinaus Geld für spätere Ausgaben zurückhalten möchte, reserviert es ausdrücklich. Der feste Kalenderrichtwert bleibt davon unabhängig.

Ein verspäteter Gehaltseingang erzeugt kein fiktives Geld. Vorhandene freie Mittel können die Deckung übernehmen. Fehlen Mittel, zeigt die Anwendung die Unterdeckung. Tatsächlich entstandene Ausgaben dürfen trotzdem erfasst werden; negative Poolbestände beziehungsweise Deckungslücken werden sichtbar, nicht durch stille Entnahme aus anderen Pools verdeckt.

### Wirkungen der Vorgänge

| Vorgang | Konto | Pool | Reservierung/Richtwert |
|---|---|---|---|
| Tatsächliche Einnahme | Eingang | Zuweisung erhöht Bestand | Offene Deckung berücksichtigen |
| Tatsächliche Ausgabe | Abgang | Verbrauch | Zugeordnete Reservierung sinkt; laufender Bedarf zählt zum Richtwert |
| Reiner Kontotransfer | Quelle sinkt, Ziel steigt | Gesamter Pool unverändert | Keine Ausgabe, Zweck bleibt erhalten |
| Umwidmung | Unverändert | Quelle sinkt, Ziel steigt | Keine Richtwertausgabe |
| Reservierung | Unverändert | Bestand unverändert | Frei verfügbar sinkt |
| Abschluss/Abbruch | Unverändert | Bestand unverändert | Rest wird im Herkunftspool frei |
| Storno einer falschen Buchung | Gegenbewegung | Gegenbewegung | Auswertungen werden nachvollziehbar korrigiert |
| Echte Händlererstattung | Eingang am Erstattungstag | Rückfluss zum ursprünglichen Pool | Laufender Bedarf: Nettoausgaben im Erstattungsmonat reduzieren; Brutto/Erstattungen separat zeigen |
| Kontostandskorrektur | Dokumentierte Differenz | Explizite Zuordnung | Keine normale Einnahme-/Ausgabenkategorie |

Eine Erstattung auf ein abgeschlossenes Vorhaben fließt standardmäßig in den Herkunftspool zurück und öffnet das Vorhaben nicht automatisch wieder. Eine Rückgabe ist nicht dasselbe wie das Storno einer fehlerhaften Eingabe.

Kontoverteilung und interne Ausgleiche müssen sowohl reale Bankstände als auch bereits verbrauchte Zweckmittel korrekt zeigen. Als Ledgerregel wird eine signierte Konto-Pool-Zuordnung verwendet: Die Summe über Pools entspricht je Konto dessen tatsächlichem Stand; die Summe über Konten entspricht je Pool dessen Bestand. Eine negative Zuordnung bezeichnet eine explizite interne Vorfinanzierung und muss mit einer Ausgleichsposition verknüpft sein. Sie wird nicht als vorhandenes Geld angezeigt.

Konkreter Fall: Hauptkonto/Flex 1.000 € und Freizeitkonto/Freizeit 300 €. Nach 85 € Einkauf vom Hauptkonto, davon 65 € Flex und 20 € Freizeit, stehen beim Hauptkonto die Zuordnungen Flex 935 € und Freizeit −20 €; auf dem Freizeitkonto Freizeit 300 €. Die realen Konten sind 915/300 €, die Pools 935/280 €. Der offene Ausgleich 20 € erklärt die Vorfinanzierung. Nach dessen Transfer stehen die Konten 935/280 €, die Hauptkonto-Freizeit-Zuordnung ist 0 €, der Freizeitpool weiterhin 280 €. Die vorher am Freizeitkonto liegenden 300 € enthalten also 20 € bereits verbrauchtes Geld; diese 20 € werden dort nicht als frei angezeigt.

Global freier Flex und unmittelbar am Hauptkonto ausgebbarer Betrag sind getrennte Werte. Im Beispiel sind 935 € Flex vorhanden, aber vor dem Ausgleich nur 915 € physisch auf dem Hauptkonto. Bindungen und offene Ausgleichsbedarfe werden für die Kontoliquidität berücksichtigt; sie reduzieren den bereits korrekt berechneten Gesamtpool nicht nochmals. Freie Verfügbarkeit eines Zielkontos ist stets durch dessen tatsächlichen Stand und Verpflichtungen begrenzt. Schritt 0 dokumentiert diese Regeln als ausführbare Referenzfälle.

Automatische Ausgleichsvorschläge brauchen eine deterministische bevorzugte Poolquelle; bei mehreren geeigneten Konten kann der Nutzer die Quelle wählen. Teilzahlungen auf einen Ausgleich sind möglich.

Für Version 1: EUR, Cent als ganzzahlige Beträge, technische Zeitpunkte UTC, fachliche Monatsgrenzen Europe/Berlin. Datumsregeln und Rundungen sind deterministisch. Bestätigte Buchungen werden korrigiert/storniert, nicht unbemerkt überschrieben. Keine laufende Kopie der ganzen SQLite-Datei als Synchronisationsverfahren.

## 4. Arbeitsweise für jeden Implementierungsschritt

Jeder Schritt ist ein eigener Entwicklungsauftrag und endet mit einem lauffähigen, überprüfbaren Stand. Erst nach bestandenen Abnahmen folgt der abhängige Schritt. Tests werden zusammen mit der Funktion implementiert, nicht erst am Projektende.

Jeder Auftrag enthält: Ziel, Abhängigkeiten, betroffene Projekte, konkrete Änderungen, Beispieldaten, Abnahmekriterien und bewusst zurückgestellte Funktionen. Nach Abschluss: kurze Änderungsliste, ausgeführte Tests mit Ergebnissen und bekannter Restumfang. Keine vorgezogenen Großumbauten für spätere Schritte.

Zu Beginn Git-Repository mit kleinen nachvollziehbaren Commits und reproduzierbaren Builds anlegen. Keine feste Aufwandsschätzung ohne den technischen Prototyp; die Reihenfolge und Fertigkriterien sind verbindlich, nicht erfundene Tageszahlen.

## 5. Schritte mit einzeln prüfbaren Ergebnissen

### Schritt 0 – Fachlichen Rechenvertrag festhalten

**Voraussetzung:** Keine.

**Implementieren:** Technische Entscheidungsliste, Money/Datum-Regeln, Ledgerentwurf, Deckungsperiode, Statusautomaten für Planung/Transfers/Vorhaben und gemeinsame JSON-Beispieldaten. Kontoverteilung mit internen Ausgleichen konkret spezifizieren. Entwürfe, bestätigte Buchungen und Syncoperationen voneinander unterscheiden.

**Prüfen:** Acht Referenzfälle manuell bis auf Cent durchrechnen: Anfangsbestand, Einkommen, Split, Ausgleich, Kontotransfer, Umwidmung, Fixkostendeckung und Vorhabenabschluss. Pro Fall erwartete Konto-, Pool-, Reservierungs- und freien Salden festhalten. Noch keine Oberfläche erforderlich.

**Fertig:** Jeder Vorgang besitzt eindeutige Wirkungen; es gibt keine ungeklärte Doppelzählung in den Referenzfällen.

### Schritt 1 – Projektgerüst und Linux-Start

**Voraussetzung:** 0.

**Implementieren:** Lario.Domain, Application, Infrastructure, Host; Testprojekte und Angular-Workspace für Haupt- und Mobiloberfläche. Host liefert minimale Oberfläche und Health-/Versionsendpunkt. SDK/Pakete festschreiben, Konfiguration und lokales Datenverzeichnis anlegen. OpenAPI-Vertrag und TypeScript-Generierung vorbereiten.

**Prüfen:** Aus frischem Checkout bauen, Host unter Linux starten, Browser öffnen, Testlauf durchführen und API-Client generieren. Neustart darf keine leere zweite Datenablage erzeugen.

**Fertig:** Ein dokumentierter Start-/Buildweg funktioniert reproduzierbar auf dem Ziel-Linux.

### Schritt 2 – Früher Android-/LAN-Prototyp

**Voraussetzung:** 1.

**Implementieren:** Installierbare Capacitor-App mit eingebetteter Oberfläche; SQLite-Speicheradapter. Ein einfacher Testdatensatz wird lokal gespeichert. Native Verbindung zum Host einschließlich Zertifikatsvertrauen und Kopplungsgrundlage erproben. Keine produktive Finanzberechnung im Prototyp.

**Prüfen:** Im Flugmodus Testdatensatz speichern, App beenden/neustarten und wiederfinden. Im Heimnetz Host erreichen; bei ausgeschaltetem Host weiter speichern. Funktioniert kein kompatibler SQLite-/Capacitor-Stand, Adapterentscheidung jetzt lösen.

**Fertig:** Offline-Speicherung und verifizierte LAN-Verbindung sind auf echtem Androidgerät nachgewiesen. Der technische Datensatz ist noch keine finale Finanzbuchung.

### Schritt 3 – Stammdaten und Anfangsbestand

**Voraussetzung:** 0–1.

**Implementieren:** Konten, Pools, Kategorien/Unterkategorien, separate Steuerkategorien/Unterkategorien und vererbbare Steuer-Voreinstellungen, Archivierung, Anfangsbestände mit Stichtag und explizite Verteilung. SQLite-Schema samt erster Migration. Verhindern, dass derselbe Anfangsbestand mehrfach als Buchung erzeugt wird.

**Prüfen:** Hauptkonto 1.000 €, davon 700 € Flex und 300 € Freizeit. Kontosumme und zugeordnete Summe jeweils 1.000 €; normale Einnahmen 0 €. Teilweise Zuordnung lässt einen sichtbaren Rest. Kategoriezyklus und ungültige Referenzen ablehnen.

**Fertig:** Einrichtung ist persistent, wiederholbar ohne Verdopplung und centgenau.

### Schritt 4 – Einnahmen, Ausgaben und Splits im Kern

**Voraussetzung:** 3.

**Implementieren:** Buchungsjournal, Account-/Fund-Postings, Einnahmeverteilung, Ausgabe mit mehreren Teilposten, Datum/Kategorie/Notiz, Basisrevision und steuerliche Kennzeichnung je Teilposten gemäß Abschnitt 9. Alle zusammengehörigen Änderungen atomar speichern. Abfragen berechnen Salden aus den Grunddaten.

**Prüfen:** Einnahme 1.100 € auf Wohnungskonto; noch keine Transferannahme. Ausgabe 85 € mit 65 € Flex und 20 € Freizeit verbraucht genau diese Pools; für diesen ersten Test liegen beide Pools auf dem Zahlungskonto. Kontenübergreifende Deckung folgt in Schritt 5. Split 84,99 € zu Gesamtbetrag 85 € wird zurückgewiesen. Fehler während Commit hinterlässt keine halbe Buchung. Unterdeckung bleibt sichtbar.

**Fertig:** Kernbuchungen stimmen unabhängig von der Oberfläche.

### Schritt 5 – Transfers, Umwidmung und interne Ausgleiche

**Voraussetzung:** 4.

**Implementieren:** Reiner Transfer, Umwidmung, Kombination beider Vorgänge, Vormerkung und Bestätigung, Abbruch, offener Ausgleich und Teilbegleichung. Zweck-/Kontoverteilung aus Schritt 0 umsetzen.

**Prüfen:** Start: Hauptkonto/Flex 1.000 €, Freizeitkonto/Freizeit 300 €. Gemischter Einkauf 85 € vom Hauptkonto ergibt Konten 915/300 €, Pools Flex 935 € und Freizeit 280 €, offener Ausgleich 20 €. Überweisung 20 € ergibt Konten 935/280 €, dieselben Pools und keinen offenen Ausgleich. Gesamtbestand vorher 1.300 €, danach 1.215 €. Teilbegleichung 5 € lässt 15 € offen. Wiederholtes Bestätigen verdoppelt nichts.

**Fertig:** Bankstand, Zweckverteilung und offene Ausgleiche stimmen gleichzeitig.

### Schritt 6 – Korrekturen, Erstattungen und Kontostandsabgleich

**Voraussetzung:** 5.

**Implementieren:** Verknüpfte Korrektur/Storno; tatsächliche Erstattung zum Ursprung; manueller Bankstandsabgleich mit dokumentierter Differenz. Auswirkungen auf Splits und Ausgleiche atomar nachführen. Revisionskonflikt als Fehler zurückgeben.

**Prüfen:** Ausgabe 85 € auf 80 € korrigieren, Splits passend anpassen; keine zweite Ausgabe. Teilrückzahlung 10 € erhöht Konto und Ursprungspool. Eine Änderung nach bereits beglichenem Ausgleich erzeugt nachvollziehbaren neuen Ausgleichsbedarf. Abgleichdifferenz wird nicht als normales Gehalt gezählt.

**Fertig:** Fehlerhafte Eingaben können korrigiert werden, ohne Salden manuell zu überschreiben.

### Schritt 7 – Produktive API und erste Desktop-Eingabe

**Voraussetzung:** 3–6.

**Implementieren:** Versionierte REST-Endpunkte mit grundlegender lokaler Authentifizierung, DTOs, Validierungsfehler, API-Client und kleines Desktop-Ausgabeformular mit Liste einschließlich Steuerkennzeichnung und Kategorievorbelegung. Minimaler Einrichtungsdialog für Anfangsbestände. Bereits bestätigte Buchungen über definierte Korrekturanwendungsfälle ändern.

**Prüfen:** Referenzfälle über API ausführen und Ergebnisse auslesen. Ausgaben per Browser anlegen und Host neu starten. Protokoll-/Schemafehler verständlich anzeigen; keine frei übermittelten Salden als Wahrheit übernehmen.

**Fertig:** Alle bisherigen Kernfunktionen sind über API und einfache Oberfläche überprüfbar.

### Schritt 7a – PDF-/Bildnachweise in der Hauptanwendung

**Voraussetzung:** 7.

**Implementieren:** Eigene Attachment-Metadaten und lokaler Dateispeicher auf dem Host. PDF, JPEG und PNG über die Hauptoberfläche hochladen, anzeigen/herunterladen und einer Ausgabe zuordnen. Optional einzelne Splits verknüpfen; ein Beleg kann die gesamte Split-Ausgabe nachweisen. Originaldatei erhalten. Dokumente können nachträglich angehängt werden. Ein fehlender Beleg blockiert keine Buchung.

Datei-ID, Name, Typ, Größe, Prüfsumme und Buchungsbezug speichern; Dateien außerhalb der öffentlich ausgelieferten Oberfläche ablegen und nur über authentifizierte Endpunkte abrufen. Uploadgrenzen festlegen, Dateityp prüfen und Dateipfade serverseitig erzeugen. Erst nach erfolgreicher dauerhafter Ablage einen verwendbaren Anhang bestätigen. Uploadfehler verändern keine Finanzbuchung; unvollständige Ablagen werden kontrolliert bereinigt. Das Entfernen einer Zuordnung verändert keinen Geldbetrag. Gleiche Dateien dürfen nicht unbeabsichtigt andere Belegzuordnungen verlieren.

**Prüfen:** PDF und Bild an eine Ausgabe anhängen, Host neu starten und öffnen/herunterladen. Mehrere Dateien an derselben Rechnung; ein Beleg mit mehreren Splits. Fehler während Upload hinterlässt keine sichtbare Verknüpfung auf eine fehlende Datei. Ungültiger Typ, zu große Datei und unberechtigter Abruf werden abgewiesen. Beleg entfernen: Konten und Pools unverändert.

**Fertig:** Vorhandene digitale Nachweise sind bereits ohne mobile Belegerfassung in der Hauptanwendung verwaltbar. Originaldateien und Metadaten gehören ab diesem Schritt zum Sicherungsumfang; vollständige Restore-Abnahme folgt in Schritt 17.

### Schritt 8 – Produktive mobile Offline-Erfassung

**Voraussetzung:** 2 und 7.

**Implementieren:** Ausgabeformular als primäre mobile Ansicht; Betrag, Konto, Pool, Kategorie, Splits, optional Vorhaben/Notiz. Steuerkennzeichnung schnell per Schalter beziehungsweise Kategorievorbelegung setzen und später ergänzen können. Stammdatensnapshot, lokale Entwürfe und Outbox. Ausgabe plus Operation in einer SQLite-Transaktion speichern. Eigene unsynchronisierte Eingaben anzeigen, ändern und verwerfen.

**Prüfen:** 20 Ausgaben inklusive Splits ohne Host erfassen, App hart beenden und neu starten. Doppeltippen erzeugt nicht zwei unabhängige Eingaben. Eine noch nicht gesendete verworfene Eingabe wird nicht später hochgeladen. Ungültige Eingabe landet nicht in der Outbox.

**Fertig:** Erfassung benötigt nach Einrichtung weder Host noch Internet.

### Schritt 9 – Synchronisation neuer Ausgaben

**Voraussetzung:** 7–8.

**Implementieren:** Gerätekopplung, widerrufbare Zugangsdaten, Bootstrap mit konsistentem Snapshot/Cursor, Push von Operations-ID plus Payloadnachweis, atomare Annahme inklusive ChangeLog, Quittierung und Pull. Datenbank-/Protokollgeneration von Anfang an speichern.

**Prüfen:** Alle 20 Eingaben aus Schritt 8 einmal übertragen. Derselbe Request dreimal ergibt dieselben Buchungen. Verbindung nach Servercommit vor Antwort abbrechen; Wiederholung bestätigt statt doppelt zu buchen. Gleiche Operations-ID mit verändertem Payload wird abgelehnt. Token widerrufen: Sync scheitert, lokale Erfassung bleibt möglich.

**Fertig:** Erste vollständige Strecke Einrichtung → offline erfassen → LAN synchronisieren → korrekte Desktop-Salden.

### Schritt 10 – Sync von Änderungen und Konflikten

**Voraussetzung:** 6 und 9.

**Implementieren:** Basisrevisionen, geordnete Operationen je Buchung, lokale Änderungsüberlagerung auf dem Serverstand, Storno-/Archivübertragung, Konfliktliste und kontrollierte erneute Einreichung. Nicht quittierte Operationen bei Pull erhalten. Verarbeitetes Operationsarchiv für spätere Wiederabstimmung aufbewahren.

**Prüfen:** Desktop und Handy ändern dieselbe Ausgabe: Konflikt sichtbar, keine stille Überschreibung. Änderung an offline inzwischen archivierter Kategorie: historischer Bezug bleibt gültig. Neue Ausgabe und anschließende Korrektur vor der ersten Synchronisation werden geordnet verarbeitet. Unterbrochener Pull setzt Cursor nicht vorzeitig fort.

**Fertig:** Konflikte und Verbindungsabbrüche gefährden keine bereits gespeicherten Eingaben.

### Schritt 11 – Wiederkehrende Regeln und geplante Vorgänge

**Voraussetzung:** 7.

**Implementieren:** Regeln mit Gültigkeit ab Datum, Rhythmus, Fälligkeit, Ende/Pause, erwarteten Einnahmen/Ausgaben/Transfers; eindeutige PlannedOccurrence je Regel und Termin. Nach Programmstillstand fehlende Vorgänge erzeugen. Bestätigung mit abweichendem tatsächlichem Betrag verknüpfen. Steuer-Voreinstellungen aus der Regel am erzeugten Posten übernehmen; bestätigte historische Kennzeichnungen nicht stillschweigend ändern.

**Prüfen:** Zweimaliges Generieren erzeugt keine Duplikate. Monatstag 31 in Februar korrekt. Quartal/Jahr korrekt. Wohngeld ab Januar ändern: Dezember unverändert. Nach zwei Monaten ausgeschaltetem Host fehlen keine Fälligkeiten. Bestätigter Vorgang wird nicht erneut offen erzeugt.

**Fertig:** Planung existiert getrennt vom tatsächlichen Buchungsjournal.

### Schritt 12 – Monatsdeckung, Einkommen und Flex-Anzeige

**Voraussetzung:** 5 und 11.

**Implementieren:** Finanzierungsmonat je Einkommen, Deckungsbindungen, Unterdeckung, feste Kalenderrichtwerte, Flexbestand/reserviert/unverplant und laufende Nettoausgaben. Planungshorizont aus Abschnitt 3; zukünftige Vorschau getrennt. Gehaltszuweisung automatisch Folgemonat, andere Einnahmen mit konfigurierbarer Regel.

**Prüfen:** Gehalt 3.000 € am 28. Oktober für November, Novemberverpflichtungen 2.000 €: zusätzlicher freier Flex-Anteil 1.000 €, Kontozugang 3.000 €. Derselbe Eingang am 30. Oktober ändert den Monatsbeginn nicht. Zweiter Teileingang bindet Fixkosten nicht doppelt. Zahlung einer gebundenen Rechnung senkt Bestand und löst dieselbe Bindung, freie Mittel sinken nicht nochmals um den vollen Rechnungsbetrag. Erwartete Steuererstattung erhöht den Bestand nicht. Dezembervorschau bindet nicht automatisch alle Dezemberkosten.

**Fertig:** Dashboard zeigt tatsächliche freie Mittel getrennt von Richtwert und Prognose. Dieser Schritt macht die Anwendung fachlich als Monatsbegleiter brauchbar.

### Schritt 13 – Ansparungen und regelmäßige Zuweisungen

**Voraussetzung:** 11–12.

**Implementieren:** Monatliche Zielzuweisungen für Freizeit, Jahreskosten und Rücklagen; optional zugehöriger Kontotransfer. Regeländerungen ab Monat X. Jahreskosten mit nächster Fälligkeit, vorhandenem Guthaben und erforderlicher Sonderzuführung. Centgenaue Verteilung.

**Prüfen:** Jahresrechnung 600 €: monatlich 50 €. Quartalsrechnung 90 €: monatlich 30 €. 100 € auf 12 Zuführungen verteilen ergibt exakt 100 €. Zuführung plus Rechnung zählt nicht als doppelte Ausgabe. Kurz vor Fälligkeit fehlen Mittel: Unterdeckung anzeigen, nicht vollständig angespart vortäuschen. Wiederholter Monatsaufbau verdoppelt keine Zuweisung.

**Fertig:** Ansparung und Banktransfer sind gemeinsam bedienbar, aber fachlich getrennt.

### Schritt 14 – Vorhaben und zweckerhaltendes Parken

**Voraussetzung:** 5, 10 und 12.

**Implementieren:** Projekt, Reservierungsbewegungen, Kontoverteilung, Teilfreigabe, Zusatzfinanzierung, Ausgabezuordnung, Abschluss/Abbruch. Vorhabenstammdaten mobil verfügbar machen. Großausgaben vom laufenden Richtwert getrennt halten.

**Prüfen:** 2.000 € reservieren, 1.500 € auf verzinstes Konto verschieben, zurücküberweisen, 1.750 € bezahlen und abschließen: 250 € frei, keine Zusatzbuchung beim Abschluss. Ausgabe über Reservierung benötigt erkennbare Zusatzdeckung. Abschluss während Handy offline und späterer Vorhabenausgabe erzeugt Klärungsbedarf; Eingabe bleibt erhalten. Erstattung nach Abschluss fließt in den Herkunftspool.

**Fertig:** Reservierungen bleiben über Konten hinweg erhalten und lassen sich korrekt auflösen.

### Schritt 15 – Wohnungsbereich

**Voraussetzung:** 11–14.

**Implementieren:** Eigene Detailansicht mit Kaltmiete, Nebenkostenvorauszahlung, Kredit/Wohngeld/Erbbauzins, Zahlungsüberschuss, Ansparung und Hauptkontozuschuss. Generische Transfers und Poolzuweisungen verwenden. Nebenkostenabrechnung als geplante/tatsächliche Einmalzahlung erfassbar, keine vollständige Mietverwaltungssoftware.

**Prüfen:** 1.100 € Eingang, 958,68 € Kosten auf Hauptkonto, Transfer 800 €: Wohnungskonto +300 €, Hauptkonto −158,68 €, Gesamtüberschuss 141,32 €. Transfer 700/900 € liefert die vereinbarten Varianten. Ansparung ist nicht freies Flex-Geld. Wohngeldänderung gilt ab gewähltem Monat; Haussondertilgung aus Rücklage korrekt zuordnen.

**Fertig:** Eigene Wohnungsansicht ohne parallele Sonderberechnung.

### Schritt 16 – Vollständige Einrichtung und Auswertungen

**Voraussetzung:** 12–15.

**Implementieren:** Wizard für Konten/Poolzuweisung, Einkommen, Verpflichtungen, Ansparziele, Richtwert, Anfangsreservierungen; anpassbare Startkategorien. Berichte nach Zeitraum/Kategorie/Pool/Konto/Vorhaben, getrennte Transfers und Finanzierungssicht. Steuerrelevante gezahlte Teilbeträge unter eigenen Steuerkategorien/Unterkategorien nach Zahlungsjahr anzeigen, filtern und summieren; Nachweise öffnen, fehlende Belege filtern, CSV und Belegpaket exportieren (Abschnitt 9). Abgleichhinweise und offene Aktionen übersichtlich darstellen.

**Prüfen:** Leere Installation anhand eines fiktiven Haushalts vollständig einrichten. Wizard fortsetzen statt duplizieren. Monatswechsel übernimmt Bestand, setzt Richtwertauswertung neu auf. Berichte stimmen mit Kernabfragen überein und zeigen Bruttoausgaben, Erstattungen sowie Netto separat.

**Fertig:** Anwendung lässt sich ohne manuelle Datenbankeingriffe einrichten und bedienen.

### Schritt 17 – Backup, Restore und Sync-Wiederabstimmung

**Voraussetzung:** Grundbackup ab Schritt 3; vollständige Abnahme nach 10 und 16.

**Implementieren:** Konsistente Sicherung von SQLite und allen referenzierten Belegdateien als zusammengehöriger Stand, Export, konfigurierbares Sicherungsziel, Wiederherstellung, Schema-/Appversion, neue Restoregeneration; mobiler Export offener Operationen. Nach Restore vor neuer Synchronisation Unterschiede zwischen Serverstand und mobilen Bestätigungen klären. Normale Syncgeneration und Datenbankgeneration getrennt führen.

**Prüfen:** Serverbackup erstellen, danach mobile Ausgabe übertragen, Server auf älteres Backup zurücksetzen. Handy erkennt neue Generation und wirft bereits bestätigte spätere Eingabe nicht weg. Wiederabstimmung liefert den benötigten fehlenden Vorgang nach beziehungsweise zeigt ihn zur Entscheidung, ohne bereits enthaltene Buchungen zu verdoppeln. Lokaler Geräteexport rettet unsynchronisierte Eingaben. Restore unter Testbedingungen liefert dieselben Salden.

**Fertig:** Restore ist kein stiller Datenverlust. Der Wiederabstimmungsprozess bewahrt auch korrigierte Buchungen und verwirft nicht automatisch den neueren Stand eines Geräts.

### Schritt 18 – Linux-/Android-Auslieferung und Gesamtabnahme

**Voraussetzung:** Alle vorherigen Schritte.

**Implementieren:** Reproduzierbares Linux-Paket/Startskript, optionaler Dienstbetrieb, signierter Androidbuild, Update-/Schemastrategie, kurze Bedienanleitung und Testdatensatz. Android-Signierschlüssel sicher aufbewahren, damit Appupdates bestehende lokale Eingaben behalten. Eine private APK-Installation reicht für die erste Version.

**Prüfen:** Frische Linuxinstallation starten; Android-App installieren, koppeln, offline erfassen und synchronisieren. Appupdate mit offenen Eingaben, Serverupdate mit vorhandenem Datenbestand und Backup/Restore durchführen. Einen vollständigen Monat inklusive Folgemonatgehalt, Fixkosten, Splits, Rücklagen, Wohnung, Erstattung und Vorhabenabschluss nachspielen.

**Fertig:** Alle Referenzfälle bestanden; kein Datenverlust bei Neustart, Syncwiederholung oder Update. Automatisierte Kerntests und dokumentierte echte Gerätetests liegen vor.

## 6. Reihenfolge und erste nutzbare Stände

Die Schritte werden grundsätzlich in der nummerierten Reihenfolge umgesetzt; Schritt 7a folgt direkt auf Schritt 7. Insgesamt sind es jetzt 20 einzeln abnehmbare Abschnitte. Schritt 2 ist bewusst ein früher technischer Nachweis. Änderungen am Finanzkern warten nicht auf eine fertige Oberfläche; SQLite-/API-Tests prüfen ihn direkt. Mobile Stammdaten und Syncverträge werden bei späteren Features jeweils mit erweitert.

- Nach Schritt 9: voll funktionsfähige manuelle Ausgabenerfassung mobil und auf dem Hauptrechner; Salden korrekt, aber noch ohne vollständige Monatsplanung.
- Nach Schritt 12: erste fachlich nutzbare Version mit Einkommen, Verpflichtungen, festem Kalendermonat und Flexanzeige.
- Nach Schritt 16: vereinbarter funktionaler Umfang einschließlich Wohnung und Vorhaben.
- Nach Schritt 18: geprüfte Auslieferung und Sicherungs-/Wiederherstellungsstrecke.

Eine funktionierende Ausgabenerfassung nach Schritt 9 ist noch keine abgeschlossene Haushaltsplanung. Diese Grenze muss im Entwicklungsauftrag und in Zwischenständen sichtbar bleiben.

## 7. Übergabevorlage für das lokale Coding-LLM

Für jeden Schritt diese Vorlage mit dessen konkreten Angaben verwenden:

> Implementiere ausschließlich Schritt N aus Finanzplanung_Implementierungsplan.md. Beachte das Fachkonzept, die Architektur und die neueren Entscheidungen im Implementierungsplan. Prüfe zuerst den vorhandenen Stand dieses neuen Projekts. Implementiere den beschriebenen Umfang einschließlich notwendiger Schemaänderungen und sinnvoller Tests für die Abnahmekriterien. Verwende EUR-Centbeträge, feste Kalendermonate und das zentrale Buchungsmodell. Ändere keine späteren Funktionen vorzeitig. Führe die relevanten Tests aus und berichte Änderungen, Testresultate, manuell noch zu prüfende Punkte und offene Abweichungen. Eine Etappe ist nur fertig, wenn ihre angegebenen Abnahmen erfüllt sind; behaupte keine ausgeführten Gerätetests, wenn kein Gerät verfügbar ist.

Der erste Auftrag ist Schritt 0. Mit Architekturdiagrammen allein beginnen wir keine umfangreiche Implementierung, solange die Konto-/Pool-/Ausgleichsbeispiele nicht eindeutig festgelegt sind.

## 8. Spätere Erweiterung: Belege in der mobilen App

Vorhandene PDFs und Bilder können bereits im Grundumfang am Hauptrechner angehängt werden. Die spätere mobile Erweiterung ergänzt das Fotografieren und den digitalen Import direkt auf dem Handy sowie den Offline-Dateitransfer. Sie nutzt dasselbe Attachment-Modell. Die mobile erste Version bleibt bei der schnellen manuellen Ausgabenerfassung; die Schritte 0–18 einschließlich 7a hängen nicht von mobilen Belegfunktionen ab.

### Bereits im Grundumfang – vorhandene Dateien am Hauptrechner

Schritt 7a implementiert das Anhängen vorhandener PDFs und Bilder, die Verknüpfung zu Ausgaben und deren Anzeige. Schritt 16 verknüpft diese Belege mit der Steueransicht. Das Dateimodell wird dort erstmals benötigt und implementiert; es ist keine vorsorgliche leere Erweiterung.

### Spätere mobile Ausbaustufe – Beleg aufnehmen, importieren und übertragen

- Kameraaufnahme und Import digitaler Bilder/PDFs unterstützen. Übernahme aus der Android-Teilen-Funktion kann ergänzt werden; konkrete weitere Belegformate werden beim Ausbau festgelegt.
- Belege offline dauerhaft auf dem Gerät speichern, ansehen und einer Ausgabe zuordnen. Ein mehrseitiger Beleg kann mehrere Dateien/Bilder enthalten. Ein Beleg kann die gesamte aufgeteilte Ausgabe mit mehreren Teilposten dokumentieren.
- Dateien und ihre Zuordnungen bei späterer LAN-Synchronisation zum Hauptprogramm übertragen. Fehlende Dateiübertragung darf eine bereits gespeicherte Ausgabe nicht verlieren lassen oder erneut buchen.
- Geldbuchung und Dateitransfer haben getrennte Status. Stabile Beleg-IDs, Prüfsummen und bestätigte Übertragung verhindern doppelte Anhänge. Wiederholung nach Abbruch ist möglich.
- Belege auch in der Hauptanwendung anzeigen und in Sicherung/Wiederherstellung einschließen. Mobil noch nicht synchronisierte Dateien müssen beim Geräteexport berücksichtigt werden.

### Weitere Ausbaustufe – Erfassung aus dem Beleg vorschlagen

- Optional Datum, Händler, Gesamtbetrag und gegebenenfalls einzelne Positionen aus Foto oder digitalem Dokument auslesen.
- Daraus einen bearbeitbaren Ausgabeentwurf erstellen. Kategorie, Pool und Aufteilung können vorgeschlagen werden, werden jedoch vom Nutzer geprüft und bestätigt.
- Unleserliche oder widersprüchliche Angaben dürfen keine automatische Finanzbuchung erzeugen. Manuelle Eingabe bleibt jederzeit möglich.
- Für Texterkennung, Formate und eine etwaige Modellnutzung wird erst zu diesem Zeitpunkt eine Technologie ausgewählt. Ein Cloud-Dienst ist keine Voraussetzung; ein Upload persönlicher Belege an externe Dienste ist nicht Teil des bisherigen Plans.

### Abnahme der Erweiterung

Offline ein Belegfoto einer Split-Ausgabe zuordnen, App neu starten und später im LAN übertragen. Ein digitales PDF importieren und in der Hauptanwendung öffnen. Verbindung während Dateitransfer unterbrechen und wiederholen: keine verlorene Ausgabe, keine doppelten Anhänge. Backup mit Belegdateien wiederherstellen. Für die optionale Erkennung einen falschen Betrag korrigieren und erst nach Bestätigung buchen.

### Architekturanschluss

Transaction-/Expense-IDs bleiben der Verknüpfungspunkt. Attachment-Metadaten und Hostdateispeicher werden in Schritt 7a umgesetzt. Der spätere mobile Ausbau ergänzt lokalen Gerätedateispeicher und wiederaufnehmbaren Dateitransfer neben dem Datensatz-Sync. Binärdateien werden nicht als große Base64-Felder in Buchungsoperationen eingebaut. Kamera-/OCR-Code und mobile Dateitransfertechnik werden erst im Erweiterungsauftrag implementiert.


## 9. Steuerkategorien, relevante Zahlungen und Nachweise

Diese Funktion gehört zum Grundumfang. Sie unterstützt die Sammlung und Auswertung eigener Angaben für die Steuerunterlagen; sie berechnet weder Steuerbeträge noch entscheidet sie verbindlich über die Abzugsfähigkeit.

### Eigene Steuerkategorien

Die Steueransicht besitzt eigene, frei anlegbare Kategorien und Unterkategorien. Sie sind unabhängig von Alltagskategorien, Konten und Pools. Eine Zahlung kann beispielsweise unter Haus → Renovierung in der normalen Auswertung stehen und zusätzlich einer gewählten Steuerkategorie zugeordnet sein. Je relevantem Split wird eine Steuerkategorie gewählt; verschiedene steuerliche Zuordnungen derselben Rechnung werden durch Splits beziehungsweise aufgeteilte relevante Beträge abgebildet. Keine automatische Zuordnung zu verbindlichen Steuerformularfeldern.

Die Oberfläche zeigt je Zahlungsjahr einen Kategoriebaum beziehungsweise gruppierte Listen mit zugehörigen Teilbeträgen, Summen und Beleglinks. Eine übergeordnete Kategorie fasst Unterkategorien zusammen, ohne denselben Posten mehrfach in die Gesamtsumme aufzunehmen. Auch relevante Posten ohne gewählte Steuerkategorie sind in einer eigenen Liste sichtbar. Bereits verwendete Steuerkategorien werden archiviert statt gelöscht.

### Kennzeichnung und Voreinstellungen

- Pro Ausgabe beziehungsweise Split: steuerlich relevant ja/nein, frei definierbare Steuerkategorie mit optionalen Unterkategorien (etwa Werbungskosten → Arbeitsmittel oder Vermietung → Instandhaltung), optional Notiz und ein gekennzeichneter Teilbetrag innerhalb des Splitbetrags. Der Teilbetrag darf den zugehörigen Zahlbetrag nicht übersteigen.
- Kennzeichnung ist unabhängig von Konto, Pool und normaler Ausgabenkategorie. Sie verändert keinen Saldo und keinen Richtwert.
- Kategorien/Bereiche können die Kennzeichnung und Steuerkategorie vorbelegen. Eine Voreinstellung auf Wohnung mit Vererbung auf Unterkategorien ermöglicht die gewünschte bereichsweite Markierung. Pro Unterkategorie oder einzelner Ausgabe lässt sie sich ausdrücklich übersteuern; ein explizites Nein darf nicht wieder durch Vererbung ersetzt werden.
- Bei Erstellung wird die wirksame Vorbelegung in den Posten übernommen. Spätere Änderungen einer Kategorievorbelegung ändern historische Kennzeichnungen nicht stillschweigend. Eine bewusste Sammelbearbeitung bestehender Posten ist separat möglich.
- Wiederkehrende Ausgaben können die Kennzeichnung in ihre erzeugten Vorgänge übernehmen. Erst die tatsächliche Zahlung erscheint in der Liste gezahlter Beträge.
- Der Wohnungsbereich kann vollständig als relevant vorbelegt sein, ohne dass dadurch sämtliche Beträge automatisch als abzugsfähige Werbungskosten bezeichnet werden. Die Auswertung heißt Sammlung steuerrelevanter Zahlungen, nicht berechneter Steuerabzug.

### Auswertung und Export

Filter nach Zahlungsjahr/-datum, Steuerkategorie, normaler Ausgabenkategorie, Vorhaben und Kennzeichnung. Eine Ergebnisliste zeigt Datum, Empfänger beziehungsweise vorhandene Beschreibung, gesamten Zahlbetrag, markierten Teilbetrag, Kategorie, Steuerkategorie und Notiz. Bereits im Grundumfang werden vorhandene PDFs/Bilder aus Schritt 7a direkt verlinkt; fehlende Nachweise sind filterbar. Der Nutzer kann Nachweise anzeigen und die Originaldateien herunterladen.

Summen berücksichtigen ausschließlich wirksame tatsächlich gezahlte Posten und deren markierte Teilbeträge, keine geplanten Rechnungen, Kontotransfers oder Umwidmungen. Erstattungen sind mit ihrer ursprünglichen Zahlung verknüpft und in den Zahlungsjahren nachvollziehbar separat darzustellen. Bruttozahlungen, Erstattungen und rechnerischer Nettobetrag werden getrennt ausgewiesen; eine jahresübergreifende Erstattung verschiebt die ursprüngliche Zahlung nicht stillschweigend in ein anderes Jahr.

CSV-Export enthält dieselben nachvollziehbaren Angaben einschließlich IDs/Verknüpfungen. Zusätzlich können die Belege der gewählten Zahlungen als ZIP-Paket mit Originaldateien und einer Zuordnungsliste exportiert werden; fehlende Anhänge werden in der Liste ausgewiesen. Ein Beleg, der mehrere Splits dokumentiert, bleibt in der Zuordnung nachvollziehbar und wird im Paket nicht unnötig mehrfach kopiert. Das Zahlungsjahr ist ein Such- und Auswertungsfilter, keine automatisch verbindliche steuerrechtliche Jahreszuordnung. Abweichende Zuordnungen können als Notiz dokumentiert werden.

### Einordnung in die Entwicklung

Schritt 3: separate Steuerkategorien/Unterkategorien und Voreinstellungen für Kategorien/Bereiche. Schritt 4: Kennzeichnung und Teilbetrag speichern und validieren. Schritte 7–8: Bedienung am Desktop und mobil; die mobile Eingabe bleibt schnell und die Kennzeichnung kann später ergänzt werden. Schritte 9–10: Felder und nachträgliche Kennzeichnungsänderungen synchronisieren. Schritt 11: Übernahme in wiederkehrende Vorgänge. Schritt 7a: vorhandene PDFs/Bilder anhängen. Schritt 16: Steuerkategorieansicht, Filter, Summen, fehlende Nachweise, CSV und Belegpaketexport. Schritt 17: Sicherung/Wiederherstellung einschließlich aller verknüpften Originaldateien.

### Abnahme

Ausgabe 120 € mit 80 € markiertem Teilbetrag: Finanzsaldo sinkt um 120 €, Steuersammlung zeigt 80 €. Wohnungsvoreinstellung wird für eine neue Ausgabe übernommen; explizites Nein bleibt wirksam. Änderung der Voreinstellung überschreibt keine alten Posten. Geplante Rechnung fehlt in der Liste gezahlter Beträge und erscheint erst nach Bestätigung. Gemischter Einkauf summiert nur relevante Splits. Teilrückzahlung wird verknüpft und separat sichtbar. Mobile Nachkennzeichnung wird genau einmal synchronisiert. Die Markierung umfasst keine automatische Aussage zur Abzugsfähigkeit. Export stimmt mit dem Filterergebnis überein. Unterkategorien werden in ihrer Oberkategorie exakt einmal summiert. PDF/Bild am richtigen Posten öffnen; fehlenden Nachweis filtern. Ein Beleg über mehrere Splits erscheint in der Exportzuordnung korrekt. ZIP-Paket enthält die Originaldateien und Zuordnungsliste, ohne zusätzliche Finanzbuchung zu erzeugen.
