# Lario Schritt 0 — Referenzfälle (8 manuelle Rechenfälle bis auf Cent)

Die 8 Referenzfälle aus **Schritt 0** des Implementierungsplans (Finanzplanung_Implementierungsplan.md, §5): „Acht Referenzfälle manuell bis auf Cent durchrechnen: Anfangsbestand, Einkommen, Split, Ausgleich, Kontotransfer, Umwidmung, Fixkostendeckung und Vorhabenabschluss. Pro Fall erwartete Konto-, Pool-, Reservierungs- und freien Salden festhalten.“

- **Verbindliche Spezifikation:** [`../rechenvertrag.md`](../rechenvertrag.md) — alle Regeln und Wirkungen der Fälle.
- **Gemeinsame Stammdaten:** [`../data/`](../data/) (stammdaten.json, buchung.json, zuordnung_ausgleich.json) — Startzustand, IDs, Kategorien, Pools, Konten.
- **Verwendung:** Diese JSON-Dateien sind **Testfixtures** und werden ab **Schritt 3** als xUnit-Testfälle 1:1 übernommen (rechenvertrag.md §6).

## Konventionen

- **Alle Beträge in EUR-Cent (ganzzahlig).** Kein float/double. Beispiel: `8500` = 85,00 €, `100000` = 1.000,00 €.
- **Buchungs-IDs:** `bk-2026-<JJMM>-<nn>`; neue Buchungen beginnen bei `bk-2026-1201` (kollisionsfrei mit den Bestandsbuchungen `bk-2026-1101`…`bk-2026-1103` in `../data/buchung.json`).
- **Split-IDs:** `sp-<n>`; **Ausgleichspositionen:** `set-<nn>` (hier `set-0010`, fortlaufend hinter `set-0001` aus `../data/zuordnung_ausgleich.json`).
- **Konsistenzinvariante:** In jedem Zustand gilt **Summe Konten = Summe Pools** sowie Zeilensummen der Zuordnung = Kontostände, Spaltensummen = Poolbestände (rechenvertrag.md §3.4).
- **Einzelne Fälle, nicht kumulativ:** Jeder Fall startet aus `stammdaten.json` (bzw. einem dort dokumentierten abweichenden Start) — **Ausnahme:** Fall 4 baut explizit auf dem Endzustand von Fall 3 auf (dort in `startzustand.anpassungen` dokumentiert).

## Die 8 Fälle

| Nr | Datei | Fall | Kernaussage (1 Satz) | Genutzt in Schritt |
|---|---|---|---|---|
| 1 | `01-anfangsbestand.json` | Anfangsbestand | Der Anfangsbestand ist keine Einnahme (kein Finanzierungsmonat, keine Deckungsberechnung), wird mit Stichtag explizit auf Pools verteilt und ist idempotent anlegbar. | 0 (Referenz), 3 (Stammdaten, Anfangsbestand, Idempotenz) |
| 2 | `02-einkommen.json` | Einkommen mit Finanzierungsmonat | Buchungsdatum und Finanzierungsmonat sind getrennt: Gehalt am 28.10. finanziert standardmäßig den November; die Einnahme erzeugt allein noch keine Bindung. | 0 (Referenz), 4 (Einnahme/Postings), 12 (Finanzierungsmonat) |
| 3 | `03-split.json` | Split mit negativer Zuordnung (Regelanker) | Gemischter Einkauf 85 € (65 € Flex + 20 € Freizeit) vom Hauptkonto ergibt Konten 915/300 €, Pools 935/280 €, negative Zuordnung −20 € und offene Ausgleichsposition set-0010. | 0 (Referenz), 4 (Splitregel), 5 (Zuordnung) |
| 4 | `04-ausgleich.json` | Interner Ausgleich | Der 20-€-Kontotransfer (freizeit → haupt, Rückfluss der Vorfinanzierung) begleicht set-0010: Pools unverändert, Konten 935/280 €, negative Zuordnung aufgelöst — der erste Kernfall der Kontoverteilung. | 0 (Referenz), 5 (interne Ausgleiche, Teilbegleichung) |
| 5 | `05-kontotransfer.json` | Reiner Kontotransfer | 800 € haupt → wohnung ist weder Einnahme noch Ausgabe: keine Poolbewegung, Zuordnung (flex 80.000 Cent) wandert mit, Reservierungen blieben erhalten. | 0 (Referenz), 5 (Transfers) |
| 6 | `06-umwidmung.json` | Umwidmung | 500 € flex → rücklage ist keine Kontobewegung: Konten unverändert, nur Pool−/Pool+, keine Richtwertausgabe; Kontotransfer kann später separat bestätigt werden. | 0 (Referenz), 5 (Umwidmung), 13 (Ansparungen/Zuweisungen) |
| 7 | `07-fixkostendeckung.json` | Fixkostendeckung | Gehalt 3.000 € (Finanzierungsmonat 11/2026) bindet Miete 900 € + Versicherung 1.100 € = 2.000 € einmalig; zusätzlicher freier Flex-Anteil 1.000 €, keine Doppelverrechnung bei Zahlung oder Teileingang. | 0 (Referenz), 12 (Monatsdeckung, Flex-Anzeige) |
| 8 | `08-vorhabenabschluss.json` | Vorhabenabschluss | 2.000 € aus pool-flex für proj-urlaub reservieren (Anspruch im Herkunftspool, kein Pooltransfer: pool-flex behält Bestand 2.000 €, freier Anteil sinkt auf 0), 1.500 € zweckerhaltend auf das Sparkonto parken (Reservierung und Zuordnung wandern mit) und zurückholen, 1.750 € als Vorhabenausgabe bezahlen und abschließen: 250 € frei an pool-flex, keine Zusatzbuchung beim Abschluss (abgewandelte Variante: Start acc-haupt 2.000 € statt 1.000 €, damit die Vorgänge ohne negatives Konto passen). | 0 (Referenz), 14 (Vorhaben und zweckerhaltendes Parken) |

## Abweichungen / TODO

- **Fall 8, Startzustand:** `acc-haupt` startet mit **200000** statt 100000 (in `08-vorhabenabschluss.json` unter `startzustand.anpassungen` dokumentiert) — die vorgegebenen Vorgänge (Reservierung 200000, Transfer 150000, Ausgabe 175000) würden sonst ein negatives Hauptkonto erzeugen.
- **Fall 7, `pl-ver-2026-11`:** Kategorie der Jahresversicherung ist im Auftrag nicht vorgegeben → Platzhalter `cat-sonstiges`, im JSON als `"_todo"`-Feld markiert.
- **Fall 7, `pl-woh-miete-2026-11`:** Die Ziel-Pools der Verpflichtungen (Miete → pool-wohnung, Versicherung → pool-jahreskosten) sind vorgegeben; die Finanzierung erfolgt aus pool-flex über die Bindungen (Bindungen reduzieren den freien Flex-Anteil, nicht die Ziel-Pools — siehe rechenvertrag.md §4).
