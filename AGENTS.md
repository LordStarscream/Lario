# MoneyMap – Anweisungen für Coding-Agenten

Diese Datei gilt für das gesamte Repository und ist der Einstieg für pi. Starte pi im Repository-Hauptverzeichnis. Arbeite mit den vorhandenen Werkzeugen; zusätzliche Agenten, Erweiterungen oder externe Dienste sind nicht erforderlich. Nutzeraufträge haben Vorrang vor diesen Projektkonventionen.

## Auftrag und maßgebliche Dokumente

MoneyMap (Kurzform MMap) ist eine lokale Finanzplanung mit unabhängiger Android-Ausgabenerfassung. Greenfield: keine Altdatenmigration, keine Kompatibilität zur früheren Python-Anwendung GSpend.

Lies vor der ersten Implementierung:
1. `docs/Finanzplanung_Implementierungsplan.md`
2. `docs/Finanzplanung_Entwicklungskonzept.md`
3. `docs/Finanzplanung_Architektur_und_Umsetzungsplan.md`

Bei dokumentierten Abweichungen gelten die aktuellen Entscheidungen im Implementierungsplan. Diese Datei fasst Regeln zusammen und ersetzt die Abnahmekriterien nicht. Änderungen des Nutzers an Anforderungen müssen in betroffenen Dokumenten nachgeführt werden. Bei ungeklärten fachlichen Widersprüchen konkrete Befunde nennen und die abhängige Entscheidung klären; unabhängige Arbeit fortsetzen.

## Arbeitsweise mit pi und lokalen Modellen

- Kommunikation, Zusammenfassungen und Projektdokumentation auf Deutsch; Typen, Member und technische Bezeichner auf Englisch.
- Zuerst `git status --short --branch` und betroffene Dateien lesen. Änderungen anderer erhalten. Mit `rg` gezielt suchen.
- Nur den beauftragten Abschnitt implementieren. Ohne konkreten Abschnitt zuerst Stand prüfen und den nächsten notwendigen Schritt bestimmen; bei leerem Projekt mit Schritt 0 beginnen.
- Reihenfolge 0–18 einschließlich 7a beachten; 7a folgt direkt auf 7. Keine Folgeetappen automatisch beginnen.
- Kurz Ziel, Voraussetzungen und geplante Prüfungen nennen; anschließend den beauftragten Umfang einschließlich Prüfungen fertigstellen.
- Kleine, überprüfbare Änderungen bevorzugen. Keine umfassenden Refactorings, zusätzlichen Frameworks oder vorgezogenen Funktionen ohne konkreten Bedarf.
- Nach Neustart oder Kontextkürzung den aktuellen Auftrag, `git diff` und relevante Planabschnitte erneut prüfen. Session-Erinnerung ist kein Beleg für bestandene Tests.
- Keine lokalen Provider, API-Schlüssel oder globale pi-Konfiguration verändern. Modellwahl bleibt beim Nutzer. Keine LLM-Laufzeit in MoneyMap einbauen.
- Routineentscheidungen eigenständig treffen. Fehlende Werkzeuge, gesperrte Downloads und nicht verfügbare Gerätetests klar benennen; nicht als erfolgreiche Abnahme darstellen.

## Architektur und Technologie

Modularer Monolith, Linux als primäre Plattform:
- .NET 10 / ASP.NET Core, EF Core und SQLite.
- Angular 22 für Web; Angular/Capacitor 8 für Android, eigener dauerhafter SQLite-Speicher.
- Kompatible Patchstände und Plugin-Versionen vor Festlegung prüfen und in `global.json`, Projektdateien und Lockfiles festhalten. Keine stillen Major-Upgrades.
- `MoneyMap.Domain`: Werte, Regeln und Invarianten; keine HTTP-, EF- oder UI-Abhängigkeit.
- `MoneyMap.Application`: Anwendungsfälle, Domain und gezielte Schnittstellen.
- `MoneyMap.Infrastructure`: EF/SQLite, Speicher, Migrationen und Schnittstellenimplementierungen.
- `MoneyMap.Host`: API, Authentifizierung, Konfiguration und statische Weboberfläche.

C# ist die maßgebliche Finanzengine. UI und mobile App teilen Eingabekomponenten und generierte OpenAPI-Verträge, keine unabhängig rechnende vollständige Saldenengine. Wohnung ist eine Ansicht derselben Fachlogik. Keine Microservices, Broker oder generische Repository-Abstraktion für jede Tabelle.

Vorgesehene Ordner, sobald der jeweilige Schritt sie benötigt: `src/` für .NET, `tests/` für .NET-Tests, `frontend/` für den gemeinsamen Angular-Workspace, `docs/` für Pläne und Betriebsanleitungen, `assets/branding/` für das Icon. Bestehende sinnvolle Struktur erhalten. Keine leeren Zukunftsprojekte anlegen.

## Verbindliche Finanzregeln

- EUR-Beträge als ganzzahlige Centwerte: .NET long / SQLite INTEGER; TypeScript nur validierte sichere Ganzzahlen. Kein float/double für Geld. Prozentrechnung mit decimal und expliziter Rundung.
- Konto, Pool, Kategorie, Steuerkategorie und Vorhaben sind getrennte Begriffe.
- Anfangsbestände sind keine Einnahmen. Interne Transfers sind weder Einkommen noch Ausgaben; Umwidmung ist keine Kontobewegung.
- Bestätigte Buchungen mit atomaren Konto-/Poolbewegungen und nachvollziehbaren Korrekturen führen. Abgeleitete Salden müssen rekonstruierbar bleiben.
- Splitbeträge summieren sich exakt zum Gesamtbetrag. Tatsächliche Ausgaben reduzieren den finanzierenden Pool sofort, auch bei Zahlung über ein anderes Konto.
- Signierte Konto-Pool-Zuordnung und offene interne Ausgleiche erhalten. Zeilensummen entsprechen Kontoständen, Spaltensummen Poolbeständen. Gesamtverfügbarkeit und kontobezogene Liquidität getrennt behandeln.
- Reservieren verändert keinen Kontostand. Transfers erhalten Reservierungen. Ausgaben reduzieren die verbleibende Reservierung; Abschluss gibt nur den Rest an den Ursprungspool frei.
- Kalenderbudget vom 1. bis Monatsende; Fachdatum getrennt von technischen UTC-Zeitpunkten. Lokale Planung in Europe/Berlin.
- Gehalt am Monatsende finanziert standardmäßig den Folgemonat. Tatsächlicher Eingang und Finanzierungsmonat sind unabhängig.
- Erwartetes Geld ist ausschließlich Vorschau. Deckungsperiode: offene vergangene Vorgänge, laufender Monat, ausdrücklich finanzierte zukünftige Monate.
- Offene Verpflichtung und spätere Zahlung niemals doppelt abziehen; gesondert finanzierte Jahreskosten nicht erneut von Flex abziehen.
- Flexrest bleibt erhalten. Richtwert, Kontostand und freie Mittel nicht gleichsetzen. Unterdeckungen sichtbar machen, keine stillen Poolverschiebungen.
- Storno, Händlererstattung und Kontostandskorrektur sind verschiedene Anwendungsfälle.
- Regeln ab Datum versionieren; verwendete Stammdaten archivieren. Historische Werte und wirksame Steuermarkierungen nicht durch spätere Änderungen umschreiben.
- Steuerkennzeichnung am Split verändert keine Finanzsalden. Markierter Teilbetrag höchstens Splitbetrag; keine verbindliche Steuerberechnung.

Die konkreten Zahlen, Gegenbeispiele und Fertigkriterien stehen im Implementierungsplan. Als automatisierte Referenzfälle verwenden, nicht nur im Kommentar wiederholen.

## Offline, Synchronisation und Datenhaltung

- Mobile Oberfläche ist vollständig eingebettet; keine produktive `server.url`. Nach Einrichtung ohne Host und ohne Internet starten und speichern.
- Eingabe und Outbox in derselben lokalen SQLite-Transaktion; UI bestätigt erst nach Commit.
- Gerätegenerierte stabile IDs, eindeutige Operations-IDs, Payload-Nachweis und erwartete Basisrevision verwenden.
- Host speichert Fachbuchung, Änderungseintrag und Idempotenznachweis atomar; Unique Constraints gegen parallele Duplikate.
- Quittieren nur nach expliziter Bestätigung. Cursor erst nach lokaler atomarer Übernahme fortschreiben.
- Wiederholung nach verloren gegangener Antwort erzeugt keine zweite Buchung. Gleiche Operations-ID mit anderem Inhalt ist ein Fehler.
- Konflikte sichtbar behandeln, kein stilles Last-write-wins. Download überschreibt keine offenen lokalen Eingaben.
- Manuelle LAN-Synchronisation als erste Version. Keine Cloud oder dauerhafte Hintergrundausführung voraussetzen.
- Hostdatenbank auf lokalem Datenträger; LAN-Zugriff nur über API, keine geteilte SQLite-Datei.
- HTTPS-Vertrauen gezielt konfigurieren, Zertifikatsprüfung nicht abschalten. Gerätezugangsdaten sicher speichern und widerrufbar halten.
- Belegoriginale außerhalb öffentlicher Webressourcen speichern; Zugriff über authentifizierte API, Dateityp-/Größenprüfung und serverbestimmte Pfade.
- Desktop-PDF-/Bildanhänge gehören zum Grundumfang (7a); mobile Kamera, Dateiimport und OCR sind spätere Erweiterungen. Ebenso Portfolios/Aktien.
- Backup umfasst konsistente Datenbank und referenzierte Belege. Restore erzeugt neue Datenbankgeneration und kontrollierte Geräteabstimmung; neuere mobile Eingaben nicht verwerfen.

## Prüfungen und Fertigmeldung

Vor Änderungen die Abnahmen des beauftragten Schritts in konkrete Prüfungen übersetzen. Tests für Finanzinvarianten, Datenverlust, Idempotenz und Konflikte sind verbindlich. Keine sinnlosen Tests, die nur Implementierungsdetails spiegeln.

- Domain: xUnit mit Referenzzahlen, Grenzfällen und Rundungsfällen.
- Datenbank/API: echte SQLite-Datenbank; EF-InMemory ist kein Ersatz für Transaktionen, Constraints und SQLite-Verhalten.
- Sync: Mehrfachsendung, parallele Operationen, Abbruch nach Servercommit vor Antwort, Revisionkonflikt und Neustart mit offener Outbox.
- UI: gezielte Prüfungen für den geänderten Ablauf.
- Android: echte Gerätetests für dauerhafte Offline-Speicherung, Appneustart, HTTPS/LAN und Synchronisation. Browser- oder Emulatorprüfung ausdrücklich als solche kennzeichnen.

Sobald ein Gerüst existiert, passende Befehle aus Solution und `package.json` verwenden, etwa `dotnet build <solution>`, `dotnet test <solution>`, `npm ci` und vorhandene Build-/Test-/Lint-Skripte. Keine nicht existierenden Skripte oder erfolgreichen Ergebnisse erfinden. Reproduzierbare Befehle beim Gerüstaufbau in README dokumentieren.

Zum Abschluss:
1. Implementierter Schritt und resultierendes Verhalten.
2. Betroffene Dateien und begründete Abweichungen.
3. Tatsächlich ausgeführte Prüfungen mit Ergebnis.
4. Noch offene manuelle/gerätebezogene Abnahmen und Blocker.
5. Kurze Übergabe für das externe Review.

Ein Schritt mit offenen Pflichtabnahmen ist nicht vollständig abgenommen. Tests nicht entfernen oder abschwächen, um Fehler zu verdecken. Vor Übergabe Diff auf unbeabsichtigte Änderungen prüfen.

## Git und vertrauliche Daten

- Für Implementierung möglichst separater Branch `step/<nummer>-<kurzname>`; vorhandenen Nutzerbranch respektieren.
- Nur auftragsbezogene Änderungen stagen. Kleine aussagekräftige Commits.
- Push, PR und Merge nur soweit im jeweiligen Nutzerauftrag autorisiert. Keine automatische Zusammenführung mit main.
- Kein `reset --hard`, `clean -fd` oder Force-Push ohne ausdrücklichen Auftrag.
- Keine echten Finanzdaten, Belege, DB-/Backupdateien, Tokens, TLS-Privatschlüssel, Android-Signierschlüssel oder pi-Sessionexporte committen. Synthetische Testdaten verwenden; erforderliche .gitignore beim Gerüstaufbau ergänzen.
- Keine automatische Sitzungspublikation. Entwicklungsbefehle und Abhängigkeiten nachvollziehbar halten.
