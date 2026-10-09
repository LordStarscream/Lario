# MoneyMap Schritt 0 — Gemeinsame JSON-Beispieldaten (Testfixtures)

Gemeinsame, maschinenlesbare JSON-Beispieldaten für **Schritt 0** des Implementierungsplans. Ab Schritt 3 werden diese Dateien als **Testfixtures** wiederverwendet: Referenzfälle (Batch C, `../referenzfaelle/`) nehmen sie als Startpunkt, die Schrittt-3- bis -14-Tests instanziieren daraus Kontostände, Salden und Sync-Zustände.

**Verbindliche Spezifikation:** [`../rechenvertrag.md`](../rechenvertrag.md) — alle Regeln, Begriffe und Zahlen dieser Dateien stammen von dort. Bei Abweichung gilt der Rechenvertrag.

## Dateien

| Datei | Inhalt | Genutzt in Schritt |
|---|---|---|
| `stammdaten.json` | Startzustand aller Referenzfälle: 4 Konten, 6 Pools, Vorhaben `proj-urlaub` (status `reserviert`), Alltagskategorien (2 Ebenen), eigenständiger Steuerkategorie-Baum, steuerliche Voreinstellungen (Vererbung/explizites Nein), Anfangsbestände mit Stichtag | **3** (Stammdaten, Anfangsbestand, Idempotenz), 4, 5, 8 (Sync-Operationen mobile), 9 (Sync-Protokoll), 14 (Vorhaben) |
| `buchung.json` | Drei bestätigte Buchungen: Ausgabe mit Splits (Plan-Kernfall 85 €: 65 € Flex + 20 € Freizeit), Einnahme mit Finanzierungsmonat (Gehalt 28.10. → finanziert 11/2026), reiner Kontotransfer als Ausgleich | **4** (Buchung/Postings, Splitregel), 5, 8, 9 |
| `zuordnung_ausgleich.json` | Signierte Konto-Pool-Zuordnung (AccountPoolAllocation) in drei Zuständen (vor Ausgabe / nach Ausgabe / nach Transfer), offene Ausgleichsposition `set-0001` mit Begleichung, Liquiditäts-Notiz | **5** (Zuordnung/Ausgleich, Regelanker), 8, 9 |
| `sync-operation.json` | Sync-Operationen als reines Datenmodell: createBooking, updateBooking, idempotente Wiederholung (gleiche operationId + gleicher payloadHash); Sync-Regeln als JSON-Liste | **8** (Sync-Operationen mobile), **9** (Sync-Protokoll — Endpoints/Cursor/Quittungen) |

## Wichtige Hinweise

- **Alle Beträge in EUR-Cent (ganzzahlig).** Kein float/double irgendwo im Geldpfad (rechenvertrag.md §2.1). Beispiel: `8500` = 85,00 €.
- **Startzustand = Plan-Kernfall (Regelanker §3.4):** Hauptkonto/Flex 1.000 € (`acc-haupt` 100000, komplett `pool-flex`), Freizeitkonto/Freizeit 300 € (`acc-freizeit` 30000, komplett `pool-freizeit`). In `stammdaten.json` ist das die Distribution `pool-flex: 100000` für `init-2026-01-01` (nicht 700/300 — das ist der andere Fall „Teilweise Zuordnung“ aus Plan-Schritt 3).
- **`acc-spar` und `acc-wohnung` starten leer:** kein Anfangsbestand, Startsaldo 0 €. Das ergibt sich aus dem Fehlen in `initialBalances` (dort stehen nur `acc-haupt` und `acc-freizeit`).
- **`payloadHash` ist Platzhalter** (Format `sha256:<64-hex>`, in `sync-operation.json` dokumentiert über `payloadHashHinweis`); der echte Hash wird bei Erzeugung der jeweiligen Operation aus dem fachlichen Payload berechnet.
- **Konsistenzprüfung:** In jedem Zustand gilt Zeilensumme je Konto = Kontostand und Spaltensumme je Pool = Poolbestand (rechenvertrag.md §3.4), und **Summe aller Konten = Summe aller Pools**: Start **130000 Cent (1.300,00 €)**; nach der Ausgabe bk-2026-1101 und nach dem Transfer bk-2026-1103 **121500 Cent (1.215,00 €)** — die Ausgabe verlässt das System um 85,00 € (130000 − 8500), der Transfer ist eine reine Umverteilung und ändert die Gesamthöhe nicht.
- **Anfangsbestand ist keine Einnahme:** keine Finanzierungsmonat-, keine Deckungsberechnung, keine Einnahme-Zuweisung (rechenvertrag.md §3.7).
- **Buchung `bk-2026-1102` (Gehalt)** dient nur der Strukturdemonstration (Finanzierungsmonat). Die Zuordnungs-Matrix in `zuordnung_ausgleich.json` zeigt ausschließlich Startzustand + `bk-2026-1101` + `bk-2026-1103` (der Regelanker); das Gehalt wird in den Referenzfällen ab dem Journal-Stand nach dem 28.10.2026 angewendet (Hinweis in `buchung.json`).
- **Weitere Buchungstypen** (umwidmung, storno, erstatung, korrektion, reservierung) werden in den Referenzfällen (Batch C, `../referenzfaelle/`) instanziiert — hier nur die drei Kern-Typen.
- **Sync:** `sync-operation.json` dokumentiert nur das Datenmodell; Endpoints, Cursor-Verfahren, Generationen und Quittungsprotokoll sind Schritt 9/10.
