# Manuelle Gesamt-Abnahme (Stand: main `52e07a4`, Schritte 0–7.5)

Durchlaufender Test von einer **leeren Datenbank** aus. Pro Block: Was tun → Erwartetes Ergebnis.
Die Salden-Tabelle unten ist die Referenz — nach jedem Block prüfen, ob Panel/Liste dazu passen.

**Legende:** 🖥 = Browser-UI · 🔌 = API per `curl` (nur dort anlegbar)
**Globale Prüfpunkte (immer):** DevTools-Konsole frei (0 Fehler) · Salden-Panel passt zur Tabelle.

**Hinweis (21.10.2026):** Block 6/7 sind **API-only**, weil die Desktop-Transfer-UI noch
nicht existiert. Die Browser-Abnahme ist jetzt verbindlich **Schritt 12** zugeordnet
(Planlücke D2 geschlossen, 21.10.2026; siehe `docs/Finanzplanung_Implementierungsplan.md`,
Schritt 12 + UI-Zuordnungstabelle). Bis dahin wird die fachliche Transfer-Funktionalität
über die API geprüft — API-Prüfung und spätere Browser-Abnahme werden getrennt
dokumentiert; ein gelungener API-Test ersetzt **nicht** die Abnahme der späteren Formulare.
Befunde und Diskussionsthemen (L1–L3, D1–D4) liegen konsolidiert in
`.tasks/step-7-manuelle-abnahme-befunde.md`.

## Salden-Tabelle (Referenz)

| # | Schritt | Giro | Haus | Flex | Freizeit | Rücklage | KontenΣ | PoolsΣ |
|---|---------|------|------|------|----------|----------|---------|--------|
| 0 | Start (leer) | 0,00 | 0,00 | 0,00 | 0,00 | 0,00 | 0,00 | 0,00 |
| 1 | Anfangsbestand Giro 1.000,00 → Flex | 1.000,00 | 0,00 | 1.000,00 | 0,00 | 0,00 | 1.000,00 | 1.000,00 |
| 2 | Anfangsbestand Haus 300,00 → Freizeit | 1.000,00 | 300,00 | 1.000,00 | 300,00 | 0,00 | 1.300,00 | 1.300,00 |
| 3 | Einnahme 2.000,00 → Giro/Flex | 3.000,00 | 300,00 | 3.000,00 | 300,00 | 0,00 | 3.300,00 | 3.300,00 |
| 4 | Ausgabe 85,00 (65 Flex + 20 Freizeit) | 2.915,00 | 300,00 | 2.935,00 | 280,00 | 0,00 | 3.215,00 | 3.215,00 |
| 5 | Ausgabe 50,00 → Flex (steuerkenngezeichnet) | 2.865,00 | 300,00 | 2.885,00 | 280,00 | 0,00 | 3.165,00 | 3.165,00 |
| 6 | Transfer 20,00 Haus → Giro | 2.885,00 | 280,00 | 2.885,00 | 280,00 | 0,00 | 3.165,00 | 3.165,00 |
| 7 | Umwidmung 100,00 Flex → Rücklage | 2.885,00 | 280,00 | 2.785,00 | 280,00 | 100,00 | 3.165,00 | 3.165,00 |
| 8 | Kontostandskorrektur +5,00 Giro | 2.890,00 | 280,00 | 2.785,00 | 280,00 | 100,00 | 3.170,00 | 3.165,00 |
| 9 | Korrektur: Ausgabe 85,00 → 90,00 (65 Flex + 25 Freizeit) | 2.885,00 | 280,00 | 2.785,00 | 275,00 | 100,00 | 3.165,00 | 3.160,00 |
| 10 | Storno der 50,00-Ausgabe (50 → Flex zurück) | 2.935,00 | 280,00 | 2.835,00 | 275,00 | 100,00 | 3.215,00 | 3.210,00 |
| 11 | Erstattung 20,00 an Flex (von 85er-Ausgabe) | 2.955,00 | 280,00 | 2.855,00 | 275,00 | 100,00 | 3.235,00 | 3.230,00 |

Ab Schritt 8 weichen KontenΣ und PoolsΣ um **+5,00 €** ab (dokumentierte Bankdifferenz, Schritt 8) —
dies ist die erwartete „Differenz“ im Salden-Panel, kein Fehler.

---

## Block 0 — Vorbereitung & Login

- Host läuft mit `Lario__DataDirectory` auf dem **leeren** Verzeichnis. Browser: `http://127.0.0.1:8080`.
- 🖥 Login-Seite erscheint (kein Login gespeichert).
- 🔌 Für die API-Blöcke einmalig Token holen (im Terminal):
  ```bash
  export BASE=http://127.0.0.1:8080/api/v1
  export TOKEN=$(curl -s -X POST $BASE/auth/login \
    -H 'Content-Type: application/json' \
    -d '{"username":"lario","password":"<PASSWORT-AUS-der-Konsole>"}' \
    | python3 -c 'import sys,json;print(json.load(sys.stdin)["token"])')
  export AUTH="Authorization: Bearer $TOKEN"
  echo "Token: ${TOKEN:0:8}…"
  ```
- **Erwartet:** Login erfolgreich → Buchungsliste. Ohne Token: `curl -s $BASE/bookings` → HTTP 401.

## Block 1 — Stammdaten anlegen 🖥 (Seite „Stammdaten“)

1. **Konten:** `Giro` (Typ Bank), `Haus` (Typ Bank) anlegen.
2. **Pools:** `Flex`, `Freizeit`, `Rücklage` anlegen.
3. **Kategorien:** `Lebensmittel`, `Freizeit` anlegen.
4. **Steuerkategorien:** `Abzugsfähig` anlegen.
5. **Umbenennen (Probe):** Kategorie `Freizeit` → `Freizeit2` → zurück zu `Freizeit`.
6. **Archivieren (Probe):** neues Konto `Temp` anlegen → archivieren → prüfen, dass es in der
   Konto-Auswahl des Ausgabeformulars **nicht** mehr auftaucht → wieder entarchivieren → `Temp`
   nicht weiter verwenden.

**Erwartet:** alle Einträge in den vier Listen sichtbar; Umbenennen wirkt sofort; archiviertes
Konto fehlt in den Auswahl-Dropdowns; nach Entarchivierung wieder da. Salden-Panel: alle 0,00 €.

## Block 2 — Anfangsbestände (Einrichtungsvorgang) 🖥 (Seite „Anfangsbestände“)

1. Salden-Panel steht **auch hier** (Konten/Pools/Differenz), alles 0,00 €.
2. **Anfangsbestand Giro:** Stichtag z. B. `2026-10-01`, Konto `Giro`, Betrag **`1.000,00`**
   (mit Tausenderpunkt — bewusstes EingabefORMAT-Test), Verteilung: **Flex 1.000,00**, Notiz
   `Übernahme Giro`. Speichern.
3. **Erwartet nach Speichern:** man **bleibt auf der Seite**, Erfolgs-Banner, Formular
   zurückgesetzt (Konto/Betrag/Verteilung leer, **Stichtag bleibt**), Giro **fällt aus der
   Konto-Auswahl**, Eintragsliste zeigt den Eintrag (Datum, Konto, 1.000,00 €, „Flex: 1.000,00 €“),
   Salden-Panel: Giro 1.000,00 / Flex 1.000,00 (→ Zeile 1 der Tabelle).
4. **Anfangsbestand Haus:** Betrag `300,00`, Verteilung: **Freizeit 300,00**. Speichern.
5. **Erwartet:** Salden wie Zeile 2 der Tabelle.
6. **All-Configured-Anzeige:** jetzt alle Konten belegt → statt des Formulars eine
   Zustandsanzeige („Alle aktiven Konten haben bereits einen Anfangsbestand …“).
7. **Doppelanlage (409):** 🔌
   ```bash
   curl -s -o /dev/null -w '%{http_code}\n' -X POST $BASE/opening-balances -H "$AUTH" \
     -H 'Content-Type: application/json' \
     -d '{"id":"ob-dup-1","date":"2026-10-01","accountId":"<GIRO-ID>","totalAmount":5000,"allocations":[],"note":"dup"}'
   ```
   (Konto-Id aus Block 3/4 oder `curl -s $BASE/accounts -H "$AUTH"` lesen.)
   **Erwartet:** `409` + Meldung, dass der Anfangsbestand bereits existiert und über die
   Korrektur zu ändern ist.
8. **Liste (A4):** die Eintragsliste zeigt beide Einträge mit Datum, Konto, Gesamtbetrag,
   Verteilung, Notiz; jeder Eintrag hat einen **Korrektur**-Button.

## Block 3 — Einnahme 🔌 (nur API)

> Einnahmen-Formular in der Browser-UI: **noch offen** (offene Entscheidung in der
> UI-Zuordnungstabelle, Vorschlag Schritt 12). Bis dahin nur API.

```bash
curl -s -X POST $BASE/income -H "$AUTH" -H 'Content-Type: application/json' \
  -d '{"id":"inc-1","date":"2026-10-28","accountId":"<GIRO-ID>","amount":200000,"allocations":[{"poolId":"<FLEX-ID>","amount":200000}],"fundingMonth":"2026-11","note":"Gehalt Oktober"}' | python3 -m json.tool | head -20
```
> **Wichtig (Korrektur 21.10.2026):** `POST /income` erwartet `allocations` (Liste
> `{"poolId": …, "amount": …}`) — ein oberes `poolId` wird **nicht** gebunden und
> scheitert mit 400 (leere Verteilung). `fundingMonth` ist optional; weglassen =
> Standardregel „Gehalt am Monatsende finanziert den Folgemonat“.
**Erwartet:** `201` + Buchung; Buchungsliste (Filter „Einnahmen“): Zeile mit 2026-10-28,
2.000,00 €, **Korrektur-Button ABSENT**, Storno-Button vorhanden. Salden: Zeile 3 der Tabelle.

## Block 4 — Ausgabe mit Split 🖥 (Seite „Neue Ausgabe“)

1. Datum `2026-10-31`, Konto `Giro`, Gesamtbetrag **`85,00`**, Notiz `Supermarkt + Freizeit`.
2. Split 1: **65,00** — Pool `Flex` — Kategorie `Lebensmittel` (Steuer: nicht relevant).
3. Split 2: **20,00** — Pool `Freizeit` — Kategorie `Freizeit` (Steuer: nicht relevant).
4. Zeile „Summe der Aufteilung“ muss **85,00** zeigen (== Gesamtbetrag, sonst kein Speichern).
5. Speichern.

**Erwartet:** 201; Buchungsliste zeigt die Ausgabe; Salden: Zeile 4 der Tabelle.
**Grenzfall (kurz):** neue Ausgabe anfangen, Split-Summe `84,99` bei Gesamt `85,00` →
Speichern **blockiert** (Validierung). Abbrechen.

## Block 5 — Ausgabe mit Steuerkennzeichnung 🖥

1. Datum `2026-10-31`, Konto `Giro`, Gesamtbetrag **`50,00`**, ein Split: 50,00 → `Flex`,
   Kategorie `Lebensmittel`.
2. Steuer: **relevant** ankreuzen → Steuerkategorie `Abzugsfähig`, markierte Teilmenge **`30,00`**
   (≤ Splitbetrag 50,00), Steuer-Notiz `Testmarke`.
3. Speichern.

**Erwartet:** 201; Salden: Zeile 5 der Tabelle (Steuermarkierung ändert **keine** Salden);
Buchungsliste zeigt die Ausgabe.
**Grenzfall:** markierte Teilmenge `60,00` (> 50,00) → **blockiert**. Abbrechen.

## Block 6 — Transfer 🔌 (Kontotransfer)

> Die Desktop-Transfer-UI ist noch nicht umgesetzt; die Browser-Abnahme ist ausdrücklich
> **Schritt 12** zugeordnet (21.10.2026, Planlücke D2). Bis dahin wird die fachliche
> Transfer-Funktionalität hier über die API geprüft. **API-Prüfung und spätere
> Browser-Abnahme werden getrennt dokumentiert; ein gelungener API-Test ersetzt nicht
> die Abnahme der späteren Formulare.**

```bash
curl -s -X POST $BASE/transfers -H "$AUTH" -H 'Content-Type: application/json' \
  -d '{"id":"tr-1","date":"2026-11-01","fromAccountId":"<HAUS-ID>","toAccountId":"<GIRO-ID>","amount":2000,"note":"Ausgleich"}' | python3 -m json.tool | head -10
```
**Erwartet:** 201; Salden: Zeile 6 der Tabelle (Pools **unverändert** — Transfer ist weder
Einnahme noch Ausgabe). Buchungsliste (Filter „Kontotransfers“): Zeile mit 20,00 €.

## Block 7 — Umwidmung 🔌 (Poolwechsel ohne Kontobewegung)

> Wie Block 6: Desktop-UI noch nicht umgesetzt; Browser-Abnahme ausdrücklich
> **Schritt 12** (21.10.2026, D2). API-Prüfung und spätere Browser-Abnahme
> getrennt dokumentiert — der API-Test ersetzt nicht die Formulare-Abnahme.

```bash
curl -s -X POST $BASE/reassignments -H "$AUTH" -H 'Content-Type: application/json' \
  -d '{"id":"um-1","date":"2026-11-01","fromPoolId":"<FLEX-ID>","toPoolId":"<RUECK-ID>","amount":10000,"note":"Rücklage aufbauen"}' | python3 -m json.tool | head -10
```
**Erwartet:** 201; Salden: Zeile 7 (Konten **unverändert**, Flex −100 / Rücklage +100).

## Block 8 — Kontostandskorrektur 🔌 (Bankdifferenz dokumentieren)

```bash
curl -s -X POST $BASE/balance-corrections -H "$AUTH" -H 'Content-Type: application/json' \
  -d '{"id":"bc-1","accountId":"<GIRO-ID>","amount":500,"date":"2026-11-02","note":"Bankdifferenz Test"}' | python3 -m json.tool | head -10
```
**Erwartet:** 201; Salden: Zeile 8 — Giro +5,00, Pools unverändert → **Differenz +5,00 €**
im Salden-Panel-Kopf (KontenΣ 3.170,00 / PoolsΣ 3.165,00). Das ist die erwartete, sichtbare
Differenz (kein stiller Ausgleich).

## Block 9 — Korrektur einer Ausgabe 🖥 (Buchungsliste → „Korrektur“)

1. In der Buchungsliste die **85,00-€-Ausgabe** (Block 4) wählen → **Korrektur**.
2. Formular ist **vorgefüllt** (Datum, Konto, Notiz, beide Splits).
3. Gesamtbetrag auf **`90,00`** ändern; Split 2 (Freizeit) von 20,00 auf **`25,00`** ändern
   (Split 1 bleibt 65,00 — Summe der Aufteilung muss 90,00 zeigen).
4. Speichern → zurück zur Buchungsliste, Buchung zeigt **90,00 €**.

**Erwartet:** in-place-Korrektur (gleiche Buchung, keine zweite Zeile); Salden: Zeile 9 der
Tabelle (Giro −5,00, Freizeit −5,00, Flex unverändert).
**Notiz-Test (R7-5):** Korrektur erneut öffnen, nur die Notiz leeren, speichern → Notiz ist
**gelöscht**; erneut öffnen, Notiz unverändert lassen, nur Datum 1 Tag verschieben, speichern →
Notiz bleibt leer (nicht wieder da).
**Grenzfall:** Konto-Wechsel auf ein bereits für diese Buchung belegtes Konto → 409/Meldung.

## Block 10 — Storno 🖥 (Buchungsliste → „Storno“)

1. **50,00-€-Ausgabe** (Block 5) → **Storno** → Notiz `Teststorno` → bestätigen.
2. **Erwartet:** Erfolgsmeldung; neue Zeile „Storno“ (−50,00 €) in der Liste; die 50,00-€-Zeile
   zeigt jetzt **keine** Aktion-Buttons mehr (Korrektur/Storno deaktiviert); Salden: Zeile 10
   (Giro +50,00, Flex +50,00 — die Pools erhalten den Betrag zurück).

## Block 11 — Erstattung 🖥 (Buchungsliste → „Erstattung“)

1. **90,00-€-Ausgabe** (Block 4, nach Korrektur) → **Erstattung**.
2. Dialog zeigt die Ursprungs-Pools mit „Verbleibend (Max.)“: Flex max. 65,00, Freizeit max. 25,00.
3. Gutschriftskonto `Giro`, Datum wählen, nur **Flex 20,00** eintragen → **Erstattung buchen**.
4. **Erwartet:** 201; neue Zeile „Erstattung“ (+20,00 €); Salden: Zeile 11 (Giro +20,00,
   Flex +20,00).
5. Erstattung erneut öffnen → Flex zeigt „Erstattet 20,00 / Verbleibend max. 45,00“
   (Deckel = ursprünglicher Split minus bereits Erstattetes). Abbrechen.

## Block 12 — Buchungsliste: Filter & Typ-Buttons 🖥

1. Filter durchgehen: **Alle Typen**, dann je Typ (Ausgaben, Einnahmen, Kontotransfers,
   Umwidmungen, Stornos, Erstattungen, Anfangsbestände, Korrektur Kontostand) → jeweils nur
   passende Zeilen; Datum von/bis einschränken (z. B. nur 2026-11) → nur November-Zeilen.
2. **Typgerechte Buttons** (Kern-Check):
   - Ausgabe → **Korrektur + Storno + Erstattung**
   - Anfangsbestand → **Korrektur** (führt auf `anfangsbestaende/:id/korrektur`) **+ Storno? NEIN** —
     Anfangsbestände sind designgemäß **nicht stornierbar** (Korrekturweg statt Storno).
   - Einnahme / Transfer / Umwidmung / Kontostandskorrektur → **nur Storno** (kein Korrektur-Button)
   - Storno-Zeile → keine Buttons (kein Storno des Stornos)
3. **Anfangsbestände-Korrektur** (A3): Seite „Anfangsbestände“ → Eintrag Giro → **Korrektur** →
   vorgefülltes Formular (Datum, Konto, Betrag 1.000,00, Verteilung Flex 1.000,00, Notiz),
   Salden-Panel sichtbar. Probe: Verteilung auf **Flex 900,00 + Freizeit 100,00** umstellen →
   speichern → Eintrag zeigt neue Verteilung, Gesamtbetrag unverändert, Salden: Flex 2.785,00 →
   **2.685,00**, Freizeit 275,00 → **375,00**, Konten unverändert, Differenz bleibt +5,00.
   (Tabelle danach: Giro 2.955 / Haus 280 | Flex 2.685 / Freizeit 375 / Rücklage 100, Differenz +5,00.)
4. **Unbekannte Korrektur-Id:** URL `…/anfangsbestaende/unbekannt/korrektur` von Hand öffnen →
   Fehlerbanner „nicht gefunden“ (kein Formular).

## Block 13 — Konsistenz & Abmelden 🖥

1. **Salden-Panel:** Kopf-KontenΣ/PoolsΣ/Differenz stimmen mit der Tabelle überein; Panel
   ein-/ausklappbar; Sektion „Unzugeordnete Anfangsbestände“ zeigt **keine** Einträge (alles
   verteilt) — sonst nur, wenn Reste offen sind.
2. **Rekonstruktion (Stichprobe):** 🔌 `curl -s $BASE/balances -H "$AUTH"` → Werte = Salden-Panel
   = Tabelle (serverseitig abgeleitet, nicht clientseitig berechnet).
3. **Neustart-Check:** Host `Ctrl+C` stoppen, neu starten (gleiche Umgebungsvariablen) → Login →
   alle Daten und Salden unverändert vorhanden.
4. **Abmelden:** Button „Abmelden“ → Login-Seite; alte URL direkt öffnen → zurück zum Login.

---

## Offene / nicht manuell prüfbare Punkte (automatisiert abgedeckt)

- **Revisionskonflikte** (parallele Änderungen): 104 Api-Tests + 158 Infrastructure-Tests;
  manuell nur schwer deterministisch herzustellen.
- **Upgrade-Pfad A2.1** (Legacy-Anfangsbestände vor 7.5): nur mit echter Alt-DB sinnvoll.
- **Mobile/Android-App:** ab Schritt 8 (noch nicht umgesetzt).

## Fortschritt (vom Nutzer abhaken)

- [ ] Block 0 Login
- [ ] Block 1 Stammdaten
- [ ] Block 2 Anfangsbestände
- [ ] Block 3 Einnahme (API)
- [ ] Block 4 Ausgabe + Split
- [ ] Block 5 Ausgabe + Steuer
- [ ] Block 6 Transfer (API — Browser-Abnahme ist Schritt 12)
- [ ] Block 7 Umwidmung (API — Browser-Abnahme ist Schritt 12)
- [ ] Block 8 Kontostandskorrektur (API)
- [ ] Block 9 Korrektur
- [ ] Block 10 Storno
- [ ] Block 11 Erstattung
- [ ] Block 12 Filter + Typ-Buttons + OB-Korrektur
- [ ] Block 13 Konsistenz + Neustart + Abmelden
- [ ] Konsole durchgehend 0 Fehler
