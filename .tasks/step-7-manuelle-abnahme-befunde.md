# Schritt 7 — Befunde aus der manuellen Abnahme (20./21. Okt. 2026)

Quelle: manuelle Abnahme des gemergten Stands (main `52e07a4`) durch den Nutzer,
frisches Datenverzeichnis `/tmp/lario-manuell`, Browser (kein headless-Smoke).
Testbereiche bisher: Login (7), danach Stammdaten + Buchungs-UI/Anfangsbestände (7.5).

**Nachgeführt 21.10.2026:** D1 und D4 (bisher nur im Gespräch besprochen) dokumentiert;
D2 mit der Schritt-12-Entscheidung geschlossen; Nummerierung D1–D4 vereinheitlicht.
D1/D3 sind Diskussionsthemen nach Testabschluss; D4 hängt an Schritt 11/12.

**Nummerierung:** D1 = Verfügbarkeit, D2 = Transfer-UI, D3 = Gesamtbetrag-Autofill,
D4 = Gehaltssatz 4.000 € (wiederkehrende Einnahme).

## L1 [P1] — Fehlgeschlagener Login: Fehlermeldung erscheint verzögert, UI wirkt „hängend“

**Symptom (Nutzerbeobachtung):** Falsches Passwort eingeben → „Anmelden“ klicken →
keine Fehlermeldung; die Seite macht den Anschein, sie versuche weiter sich
anzumelden (Button bleibt deaktiviert, Text „Anmeldung läuft …“). Erst nachdem man
nochmal in ein Feld klickt, taucht der Banner „Anmeldung fehlgeschlagen“ auf.

**Reproduktion:**
1. Host starten (frische DB), Login-Seite öffnen.
2. Benutzername korrekt, **Passwort falsch** → „Anmelden“.
3. Erwartet: sofort „Anmeldung fehlgeschlagen: …“ + Button wieder aktiv.
4. Ist: UI bleibt im busy-Zustand; erst ein Klick in ein Feld zeigt den Banner.

**Ursache (Codeanalyse):**
- Angular 22.2: **Zoneless Change Detection ist der Default** (`app.config.ts` hat
  keinen Zone-Provider, das Host-Bundle enthält kein zone.js — in
  `dist/host/browser/index.html` verifiziert).
- `apps/host/src/app/auth/login.ts` ist der **einzige** Host-Component, der
  asynchrone Zustände als **plain class properties** führt (`busy`, `error`) —
  alle anderen Seiten (`expense-form`, `master-data`, `opening-balance`,
  `bookings-list`, `balances-panel`) verwenden `signal()`.
- Abfolge: Klick → `submit()` setzt synchron `busy = true` (wird gerendert,
  weil der Click-Handler einen CD-Tick triggert) → `await this.auth.login(...)`
  wird mit 401 abgelehnt → der Promise-Continuation setzt `error` und
  `busy = false` — in Zoneless-Modus **markiert ein Plain-Property-Write die
  Ansicht nicht als dirty** → kein CD-Tick → UI bleibt bei „Anmeldung läuft …“.
  Klick in ein Feld → `ngModel`-Input-Event → CD-Tick → Banner erscheint.
- Erfolgspfad funktioniert deshalb trotzdem: `router.navigate(...)` triggert
  eigene Lifecycle-/CD-Abläufe. (Der E2E-Smoke hat nur erfolgreiche Logins
  getestet — daher nicht auffallend.)

**Fix-Anforderung (beim nächsten Auftrag, nicht bereits umgesetzt):**
- `Login.busy` und `Login.error` zu `signal`s umstellen (Konsistenz mit dem
  Rest der Host-App); Template auf `busy()`/`error()` umstellen
  (`login.html`: `[disabled]="busy() || …"`, `{{ busy() ? … }}`,
  `@if (error(); as err)`).
- `username`/`password` können als plain properties bleiben (ngModel,
  synchron mit dem Event verdrahtet).
- **Neuer Komponenten-Test** (heute existiert kein `login.spec.ts`):
  abgelehnter Login (401) → `error()` gesetzt, `busy()` false, Banner rendert;
  fehlgeschlagener Login blockiert kein erneutes Submit (busy-frei).
- Prüfen, ob `ApiErrorHandler.handle()` im 401-Fall die erwartete Meldung
  liefert (Meldungstext aus dem Smoke: „Anmeldung fehlgeschlagen: …“).

**Abnahmekriterium für den Fix:** Falsches Passwort → Banner erscheint
**ohne** weitere Benutzerinteraktion; Button wird sofort wieder aktiv.

### L2 [P2] — Formular-Validierung zeigt rote Fehlerzeile ohne jeden Benutzerfehler

**Nutzerbefund (21. Okt. 2026, Browser):** Im Anfangsbestände-Formular
erscheint die rote Zeile (z. B. „Gesamtbetrag fehlt oder ungültig.“) sofort,
sobald ein Konto gewählt und der Betrag noch leer ist — und direkt nach dem
Speichern des ersten Anfangsbestands (das Formular wird für das nächste
Konto zurückgesetzt). „Momentan irritiert es … man meint, man hätte beim
ersten Bestand etwas falsch gemacht.“ Erwartung des Nutzers: Fehler erst
anzeigen, wenn auf „Anfangsbestand buchen“ geklickt wird und Felder fehlen;
in dem Fall wird nichts angelegt.

**Analyse:**
- `issue()` wird live berechnet und im Template ungefiltert gerendert
  (`@if (issueText)`), ein *komplett frisches* Formular ist also bereits
  „ungültig“ (z. B. „Konto fehlt.“) — die rote Zeile erscheint ohne jede
  Benutzereingabe, auch beim ersten Laden der Seite.
- Nach dem Speichern des ersten Eintrags wird das Formular zurückgesetzt
  (Konto/Betrag leer) → rote Zeile erscheint sofort, bevor der zweite
  Eintrag begonnen wurde.
- Der Absenden-Button ist zwar `[disabled]`, solange `issue` gesetzt ist
  (es wird nichts angelegt — korrekt), aber der Fehler „triggert“ nie über
einen Klick; er ist einfach immer da.
- Derselbe Musterfehler in `expense-form.ts/.html` (Ausgaben-/Korrektur-
  formular): frisches Formular → rote Zeile vom Start an.

**Betroffene Dateien:** `frontend/apps/host/src/app/opening/opening-balance.ts`
(`issue()`, Template `@if (issueText)`), `frontend/apps/host/src/app/form/expense-form.ts`
(`formIssue()`, Template `@if (issue)`).

**Empfohlene Behebung (nach Nutzerfreigabe):**
- `submitAttempted`-Flag in beiden Formularen; wird beim Klick auf den
  Buchungs-Button gesetzt.
- Hinweis nur bei `submitAttempted && issue` anzeigen; danach live
  aktualisieren (Feld korrigiert → Hinweis verschwindet).
- Button nicht mehr durch `issue` deaktivieren, sondern beim Klick
  validieren: Problem → `submitAttempted` setzen, nichts senden (es wird
  nichts angelegt).
- Host-UI-Tests: kein Hinweis vor erstem Submit; Hinweis nach Submit mit
  fehlenden Feldern; Hinweis verschwindet bei korrekter Eingabe; keine
  Anfrage bei unvollständigem Formular.

**Status:** offen (Befund festgehalten 21. Okt. 2026; Fix nicht beauftragt —
Nutzer: Befunde sammeln, Fixes bündeln)

### L3 [P3] — Anfangsbestand 0 €: UI blockt, API akzeptiert (Inkonsistenz)

**Nutzerfrage (21. Okt. 2026):** „Anfangsbestände müssen eigentlich größer als 0 €
sein, oder? — 0,00 wird abgewiesen mit 'Gesamtbetrag fehlt oder ungültig' —
ist das so gewollt?“

**Befund:**
- **UI** (`opening-balance.ts` `issue()`): `total === null || total <= 0` →
  0 € wird abgewiesen (auch 0 € in einer Verteilungszeile).
- **Server/Domain** (`OpeningBalanceSpec`): nur *negative* Beträge werden
  abgewiesen (`Total.Cents < 0`); **0 € wird vom API akzeptiert**.
- **Keine dokumentierte Entscheidung** in Doku/Implementierungsplan.

**Risiko, wenn 0 € via API erlaubt bleibt:** ein 0-€-Anfangsbestand hat keine
finanzielle Wirkung, belegt aber den einmaligen Konto-Slot (partieller
Unique-Index) dauerhaft → das Konto könnte danach nie mehr einen echten
Anfangsbestand erhalten (409).

**Empfehlung (bei späterem Fix):** Domain-Regel auf `Total > 0` nachschärfen
(+ Tests), UI-Meldung klarer („Der Gesamtbetrag muss größer als 0 € sein“).
UI-Sperre bleibt.

**Status:** offen (Befund festgehalten 21. Okt. 2026; Fix nicht beauftragt —
Nutzer: Befunde sammeln, Fixes bündeln)

### Anmerkung: Einnahmen-UI noch nicht vorhanden (kein Befund)

Nutzerfrage: „Wo stelle ich monatliche Einkünfte ein?“ — im aktuellen Stand
(0–7.5) ist das beabsichtigt so: geplante/wiederkehrende Einkünfte +
Monatsdeckung/Flex-Anzeige sind **Schritt 11/12** (noch nicht umgesetzt).
Tatsächliche Einnahmen gehen aktuell nur per API (`POST /api/v1/income`);
ein Einnahmen-Formular in der UI existiert noch nicht. Nutzer: „dann passt
das erst mal so“.

## Diskussionsthemen (nach den Tests klären, noch nicht beauftragt)

### D1 [Darstellung] — Verfügbarkeit: Salden-Panel zeigt keine „freien Mittel“

**Nutzertema (21. Okt. 2026, aus dem Gespräch dokumentiert):** Frage nach der
Darstellung tatsächlicher Verfügbarkeit im Salden-Panel. Das Panel (7.5) zeigt
heute nur Konten-/Pool-Salden, Summen und die Konten-/Pool-Differenz
(`GET /balances`: `TotalAccountBalance`, `TotalPoolBalance`, `AccountPoolGap`).
Eine Zahl „freie/verfügbare Mittel“ (Pool- bzw. Deckungsperspektive) existiert
aktuell **nicht** — fachlich ist das korrekt, denn Richtwert, Kontostand und
freie Mittel sind verschiedene Begriffe (AGENTS.md) und das Flex-/Deckungs-
Dashboard gehört zu Schritt 12.

**Diskussionspunkte (noch offen):**
- Reicht der Schritt-12-Flexbestand („tatsächliche freie Mittel getrennt von
  Richtwert und Prognose“) als Antwort, oder soll das Salden-Panel (7.5) bereits
  eine zweite Kennzahl (z. B. „verfügbare Mittel“ je Konto oder gesamt) zeigen?
- Wenn ja: welche Definition (Summe der Kontensalden? Minimum je Konto?) —
  Gesamtverfügbarkeit und kontobezogene Liquidität müssen getrennt behandelt
  werden (AGENTS.md).

**Status:** offen — Diskussionsthema nach Testabschluss (21. Okt. 2026);
entscheidet sich mit der Schritt-12-Flex-Anzeige.

### D2 [Planlücke] — Kontotransfer-/Umwidmung-UI: wann vorgesehen?

**Nutzerbeobachtung (21. Okt. 2026):** „Bei Kontotransfer fehlt mir noch ein
Frontend — fehlt das einfach noch oder wurde es übersehen?“

**Beantwortung:** weder übersehen noch vergessen — **bewusste Design-Ent-
scheidung D5** im Schritt-7-Designpaket (`.tasks/step-7-design-paket.md`):
- UI-Scope Schritt 7: Ausgabeformular + Buchungsliste + Anfangsbestände +
  Stammdaten-minimal + Korrektur-/Storno-Aktionen.
- **Nicht im UI, aber in der API und getestet:** Einnahmen, Transfers,
  Umwidmung, Transfer-Umwidmung, Kontostandskorrektur.
- Die Transfer-Funktionalität selbst ist vollständig abgenommen (Schritt 5,
  Merge `a004d10`): `POST /api/v1/transfers`, `/reassignments`,
  `/transfer-reassignments` inkl. Tests.

**Planlücke:** In `Finanzplanung_Implementierungsplan.md` ist eine
spezielle Eingabe-UI für Transfers/Umwidmung vor Schritt 16 **nicht
explizit einem Schritt zugewiesen**; Schritt 16 („Anwendung lässt sich ohne
manuelle Datenbankeingriffe einrichten und bedienen“) impliziert sie aber.
→ Diskussion: Transfer-/Umwidmung-Formular einem früheren Schritt zuordnen
(z. B. 9 oder 12) oder erst mit 16?

**Entscheidung (21.10.2026):** Die Desktop-UI für einmalige interne Geldbewegungen
wird **verbindlich Schritt 12** zugeordnet und im Implementierungsplan
konkretisiert (`docs/Finanzplanung_Implementierungsplan.md`):
- Schritt-12-Titel: „Monatsdeckung, Einkommen, Flex-Anzeige **und Transfer-/Umwidmungserfassung**“.
- Umfang: Kontotransfer, Pool-Umwidmung, kombinierter Konto-/Poolwechsel (als
  klar erkennbare Transfer-Variante, kein eigenes Formular) und Begleichung
  offener interner Ausgleiche inkl. Teilbegleichung.
- Browser-Referenzfälle mit konkreten Start-/Endwerten aus
  `docs/step-0/referenzfaelle/` (Fall 4, 5, 6) und Idempotenz-Abnahme im Prüfen-Text.
- Abgrenzung: wiederkehrende Transfers bleiben regelbasiert (Schritt 11/13);
  Abhängigkeiten Schritte 13–16 im Plan nachgeführt (14/15 verwenden die
  Transfererfassung aus 12).
- UI-Zuordnungstabelle („UI-Zuordnung der Kernfunktionen“) im Plan dokumentiert
  alle bestehenden API-Anwendungsfälle; Einnahmen-Formular, Einnahmen-Korrektur
  und Kontostandskorrektur-Formular bleiben als **offene Entscheidungen** mit
  Vorschlag (Schritt 12) und Begründung sichtbar.

**Status:** **geschlossen** (21.10.2026, Planlücke geschlossen).

### D3 [Simplification] — Ausgaben-Formular: Gesamtbetrag automatisch in erste Split-Zeile übernehmen

**Nutzerwunsch (21. Okt. 2026):** „Wenn man den Gesamtbetrag eingibt, soll
dieser automatisch als Gesamtbetrag in den Pool der ersten Zeile eingetragen
werden“ — der häufige Fall „Ausgabe komplett auf einen Pool“ spart so ein
Feld.

**Diskussionspunkte (noch offen):**
- Nur vorbefüllen (wert wird gesetzt, Nutzer kann ihn ändern/ergänzen), oder
  die Zeile erst auf Eingabe des Gesamtbetrags erzeugen?
- Bei mehreren Split-Zeilen: bei Änderung des Gesamtbetrags die erste Zeile
  anpassen (Differenz = Rest der anderen Zeilen) oder nur beim ersten Mal?
- Zusammen mit L2 (Validierung erst nach Klick) klären — die automatische
  Zuweisung eliminiert zugleich den häufigsten „Aufteilung deckt Gesamtbetrag
  nicht“-Fehler.
- Betroffen: `frontend/apps/host/src/app/form/expense-form.ts` (und später die
  mobile Eingabekomponente — gleiche Komponente, Doku-Abschnitt „UI und
  mobile App teilen Eingabekomponenten“).

**Status:** Diskussionsthema, nach manueller Abnahme klären.

### D4 [Wunsch] — Gehaltssatz 4.000 €: wiederkehrende monatliche Einnahme

**Nutzertema (21. Okt. 2026, aus dem Gespräch dokumentiert):** Der Nutzer hat
gefragt, wo monatliche Einkünfte eingerichtet werden (z. B. sein Gehaltssatz
von 4.000 €). Beantwortet: wiederkehrende Einkommensregeln und die
Monatsdeckung/Flex-Anzeige sind **Schritt 11/12** (noch nicht umgesetzt);
aktuell geht ein Gehaltseintrag nur per API (`POST /api/v1/income`, inkl.
`allocations` und optionalem `fundingMonth`). Nutzer: „wenn das in Schritt 11
geplant ist, dann passt das erst mal so“ — Befund sammeln, nicht jetzt ändern.

**Offene Punkte (für Schritt 11/12):**
- Soll die Einnahmen-Formular-UI (offene Entscheidung in der UI-Zuordnungstabelle,
  Vorschlag Schritt 12) auch die Wiederholungsregeln (Schritt 11) bedienbar machen,
  oder bleibt die Regelverwaltung eine eigene Ansicht?
- Der Referenzwert im Implementierungsplan lautet 3.000 € Gehalt (Referenzfall);
  4.000 € ist der vom Nutzer genannte reale Wert — für konsistente
  Referenzfälle/Tests klären, welcher Wert Standard bleibt.

**Status:** offen — hängt an Schritt 11/12 (21. Okt. 2026).

## Status

- L1: offen (Befund festgehalten 20. Okt. 2026; Fix nicht beauftragt).
- L2: offen (Befund festgehalten 21. Okt. 2026; Fix nicht beauftragt).
- L3: offen (Befund festgehalten 21. Okt. 2026; Fix nicht beauftragt).
- D1 (Verfügbarkeit): offen — Diskussionsthema nach Testabschluss.
- D2 (Transfer-UI): **geschlossen 21.10.2026** — verbindlich Schritt 12
  zugeordnet, im Implementierungsplan konkretisiert.
- D3 (Gesamtbetrag-Autofill): offen — Diskussionsthema nach Testabschluss.
- D4 (Gehaltssatz 4.000 €): offen — hängt an Schritt 11/12.
- Nutzer-Anweisung: Befunde sammeln, Fixes erst bündeln und dann
  beauftragen. Weitere Befunde aus der manuellen Abnahme werden in dieser
  Datei fortgeführt (aktueller Bereich: Stammdaten/Buchungs-UI).
