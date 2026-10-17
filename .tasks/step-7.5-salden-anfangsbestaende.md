# Zwischenstep 7.5 – Salden-Übersicht und Anfangsbestände

**Status:** aktiv
**Branch:** `step/7.5-salden-anfangsbestaende`
**Voraussetzung:** Schritt 7 (T1–T6 + Reviews R1–R6, merged auf `main` als `63cfe45`, PR #10)
**Beauftragt vom Nutzer:** 17. Okt. 2026 (manuelle T8-Abnahme pausiert)
**Plan-Bezug:** `docs/Finanzplanung_Implementierungsplan.md` §5 „Schritt 7" (Abschlusskriterium „Alle bisherigen Kernfunktionen sind über API und einfache Oberfläche überprüfbar" — wird durch diesen Schritt konkretisiert)

## Auftrag (Nutzer, 17. Okt. 2026)

> Wir stoppen an der Stelle die manuellen tests und machen einen zwischen entwicklungsschritt:
> Die Salden in der Buchungsliste sind gut, aber wir sollten die aktuellen Konten und Pool Salden
> mit angeben. Die Anfangsbestände sollten einmalig pro Konto sein. Wenn ein Fehlerer Anfangsbestand
> gemacht wurde muss das sauber über einen klar erkennbaren fachlich korrekten Korrekturweg
> auflösbar sein. Zudem benötigen wir die Liste mit den vorhandenen Anfangsbeständen.

## Abnahmekriterien

- **A1 (Salden):** Die Buchungsliste zeigt die aktuellen Salden aller Konten **und** aller Pools an
  (Namen + Cent-Betrag). Salden kommen ausschließlich aus dem abgeleiteten
  `GET /api/v1/journal/balances` (Rechenvertrag: keine frei übermittelten Salden), nicht clientseitig
  berechnet.
- **A2 (Einmaligkeit):** Pro Konto existiert höchstens ein (nicht wirkungsgestornter) Anfangsbestand.
  Ein zweiter Versuch wird mit 409 `conflict` abgewiesen, mit verständlicher Meldung, dass der
  Anfangsbestand des Kontos bereits existiert und über die Korrektur zu ändern ist. Die Regel gilt
  datenbankseitig (eindeutiger Index), nicht nur als Anwendungs-Prüfung.
- **A3 (Korrekturweg):** Ein Anfangsbestand ist über den definierten
  Korrektur-Anwendungsfall (`POST /api/v1/bookings/{id}/corrections`) korrigierbar: Datum, Notiz,
  Konto, Gesamtbetrag und Verteilung auf Pools. In-place-Änderung mit Basisrevision++ und
  Korrektur-Historieneintrag (Rechenvertrag §5.4/§5.5) — keine zweite Buchung, kein Storno
  (Anfangsbestände sind designgemäß nicht stornierbar: Rechenvertrag §3.7,
  `ReversalService.IsReversible`). Korrektur auf ein bereits belegtes Konto → 409.
- **A4 (Liste):** Die Anfangsbestände-Seite zeigt die vorhandenen Anfangsbestände
  (Datum, Konto, Gesamtbetrag, Verteilung, Notiz) und führt auf den Korrekturweg.

## Design-Entscheidungen

1. **Einmaligkeit:** Neue nullable Spalte `Bookings.OpeningBalanceAccountId` (gesetzt nur bei
   `Type = OpeningBalance`; Invariante in `Booking.ValidateTypeSpecific`) + **partieller eindeutiger
   Index** `WHERE OpeningBalanceAccountId IS NOT NULL`. Einzig robuste DB-Garantie (SQLite:
   Anwendungs-Checks ohne Lock-Neuprüfung sind nicht atomar). Korrektur mit Konto-Wechsel setzt die
   Spalte um; belegt → UNIQUE-Verletzung → 409.
2. **Korrekturweg:** `CorrectionSpec`/`CorrectionService` werden um den Typ `OpeningBalance`
   erweitert (heute: „nur Ausgaben und Einnahmen … über Storno + Neuausgabe" — für Anfangsbestände
   ein toter Pfad, weil sie nicht stornierbar sind; `ReversalService` dokumentiert bereits „er wird
   nicht rückgängig, sondern korrigiert"). Wirksamer Inhalt wird wie eine Neuanlage validiert
   (dieselbe `OpeningBalanceSpec`-Prüfung). `Splits` bleiben ausgeschlossen; `Allocations` tragen
   die Verteilung (API-DTO `CorrectionCommand` bleibt unverändert).
3. **Salden-Endpunkt:** `GET /api/v1/journal/balances` liefert `{accounts: [{accountId, accountName,
   balanceCents}], pools: [{poolId, poolName, balanceCents}]}` aus `BalanceQueryService` + Katalog
   (abgeleitet, wie Rechenvertrag vorschreibt).
4. **UI:** Salden-Panel über der Buchungsliste; Anfangsbestände-Seite bekommt Liste +
   Korrektur-Button; neue Korrektur-Route `anfangsbestaende/:id/korrektur` (eigene Form-Komponente,
   Muster `expense-form.ts`); „Korrektur"-Button in der Liste weicht je Typ auf die richtige Route aus.

## Tasks

- [x] T1: Backend-Kern — `Booking.OpeningBalanceAccountId` + Invariante,
      `OpeningBalanceSpec`/`ToBooking`, `OpeningBalanceService`-Einmaligkeits-Check (409 mit
      Korrektur-Hinweis), `CorrectionSpec`/`CorrectionService` um `OpeningBalance` erweitern
      (Validierung, `BuildUpdatedBooking`, Belegungs-Check bei Konto-Wechsel) + Domain-Tests
- [x] T2: Infrastruktur — Entity-Spalte + partieller Unique-Index, Migration, Repository-Mapping +
      SQLite-Tests (Einmaligkeit, Korrektur, Idempotenz, Revision, Konto-Wechsel)
- [x] T3: Host-API — `GET /api/v1/journal/balances` (Accounts + Pools mit Namen), OpenAPI neu
      generieren + Api-Tests (Referenzzahlen, 409, Korrektur-Fluss)
- [x] T4: Frontend — Salden-Panel in der Buchungsliste, Anfangsbestände-Liste + Korrektur-Form
      (Route `anfangsbestaende/:id/korrektur`), Listen-Buttons je Typ, Client neu generieren +
      host-Specs
- [x] T5: Abschluss — alle Tests (dotnet + npm) grün, Lint, Build, Doku, Übergabevorlage

## Status

**Abgeschlossen (17. Okt. 2026), alle Abnahmekriterien A1–A4 umgesetzt.**

### Umsetzung

- **A1 (Salden):** `GET /api/v1/journal/balances` liefert Accounts + Pools mit Namen und
  Cent-Saldo (abgeleitet, `BalanceQueryService` + Katalog); Salden-Panel über der Buchungsliste
  (`bookings-list.ts` ruft den Endpunkt, keine Client-Rechnung).
- **A2 (Einmaligkeit):** `Bookings.OpeningBalanceAccountId` (nullable, nur bei
  `Type = OpeningBalance`) + partieller eindeutiger Index `WHERE OpeningBalanceAccountId IS NOT NULL`
  (Migration `20261017105209_Step7_5OpeningBalanceAccountId`); Invariante in
  `Booking.ValidateTypeSpecific`; `OpeningBalanceService` wirft 409 `conflict` mit
  Korrektur-Hinweis. DB-seitig, nicht nur Anwendungs-Check.
- **A3 (Korrekturweg):** `CorrectionSpec`/`CorrectionService` um `OpeningBalance` erweitert
  (Datum, Notiz, Konto, Gesamtbetrag, Allocations; Splits ausgeschlossen; wirksamer Inhalt wie
  Neuanlage validiert; Konto-Wechsel auf belegtes Konto → 409); In-place mit Basisrevision++;
  Storno bleibt für Anfangsbestände ausgeschlossen (`ReversalService.IsReversible`).
- **A4 (Liste):** `GET /api/v1/journal/opening-balances` + Seite `anfangsbestaende` (Liste mit
  Datum, Konto, Betrag, Verteilung, unverteilt, Notiz; „Korrektur“-Button) + Route
  `anfangsbestaende/:id/korrektur` (vorgefülltes Formular, Fingerprint für „unverändert“-Erkennung,
  Pending-Operation-Idempotenz wie Ausgaben-Formular).
- **Bugfix während der Abnahme (E2E-Smoke):** `parseEuroToCents` akzeptiert jetzt Tausenderpunkte
  („1.000,50“), wie sie `formatCents` (de-DE) anzeigt — zuvor wurde genau das AnzeigefORMAT
  als ungültig verworfen. Reiner Supersatz: bisher gültige Eingaben behalten ihren Wert.
  (`money.ts` + 4 neue Tests in `money.spec.ts`)
- **Bugfix (E2E-Smoke):** `goCorrection` navigierte relativ (löst gegen Wurzel auf → falsche
  Route); jetzt absoluter Pfad, Konvention wie Ausgaben-Korrektur.

### Review-Fixes (17. Okt. 2026, nach externem Review beauftragt)

1. **Note-Semantik in der Korrektur (R7-5, gleicher Befund in Anfangsbestand und Ausgabe):**
   Vorher wurde eine im Korrektur-Formular geleerte Notiz als „unverändert“ behandelt und
   stumm zurückgesetzt (der Server hielt die Original-Notiz). Jetzt: `null` = „unverändert"
   (Original-Notiz bleibt), leere String = explizite Löschung, Text = neue Notiz.
   - Domain: `CorrectionSpec.EffectiveNote(Booking)` — `null` → Original, nur-Leerzeichen →
     `null`, sonst `Trim()`; in beiden Korrektur-Pfaden (Anfangsbestand + Ausgabe/Einnahme)
     angesetzt; XML-Doku von `CorrectionSpec.Note` und `CorrectionCommand` angepasst.
   - API-Test: `Korrektur_Notiz_null_halte_Original_leerer_String_loescht_Review_R7_5`
     (null hält die Original-Notiz, `""` entfernt sie — gegen echte SQLite-DB).
   - Frontend `opening-balance.ts`: `snapshot()` trägt die rohe Notiz (Fingerprint
     unterscheidet „gelöscht“ von „unverändert“); Korrektur sendet `null` nur bei
     `noteUnchanged` (Vergleich gegen `target?.note ?? ''`), Neuanlage sendet `null` bei leerer
     Notiz. `expense-form.ts`: `originalNote` (Vorbelegung) + `noteForCommand()` mit derselben
     Semantik.
   - Frontend-Tests: 2 Tests in `opening-balance.spec.ts` (geleert → `""`, unverändert → `null`)
     und 2 Tests in `expense-form.spec.ts` (gleicher Vertrag).
2. **Einrichtungsvorgang für Anfangsbestände (R7-3):** Die Neuanlage-Ansicht führt jetzt durch
   den Vorgang „pro Konto genau ein Anfangsbestand“:
   - `availableAccounts`: bereits eingerichtete Konte fallen aus der Konto-Auswahl heraus
     (Korrektur-Modus behält den vollen Katalog; ein belegtes Zielkonto lehnt der Server mit
     409-Hinweis ab).
   - `allConfigured` (Katalog geladen + ≥ 1 aktives Konto + alle Konten belegt): statt des
     Neuanlage-Formulars eine sichtbare Zustandsanzeige („Alle aktiven Konten haben bereits
     einen Anfangsbestand — weitere Neuanlagen sind nicht möglich … über die Korrektur").
   - `CatalogService`: bisher privates `loaded`-Flag jetzt öffentlicher `loaded`-Signal
     (Zustandsanzeigen dürfen nur auf geladenem Katalog aufbauen).
   - 3 Tests in `opening-balance.spec.ts` (Filter, Zustandsanzeige, Guard bei ungeladenem
     Katalog).
3. **Veralterter Test erwartetwert (vorhandener Defekt seit Commit 536d2f0):**
   `öffnet pro Eintrag die Korrektur-Route` erwartete noch den relativen Pfad
   `['ob-1','korrektur']`, während `goCorrection` im selben Commit auf den absoluten Pfad
   geändert wurde (dokumentiert: relative Navigation trifft die falsche Route). Der
   Erwartungswert wurde an das dokumentierte Verhalten angepasst — der Test war seit
   536d2f0 rot (der bisherige „71/71 grün“-Nachweis war damit unvollständig).

### Tatsächlich ausgeführte Prüfungen

- `~/.dotnet/dotnet test` (nach Review-Fixes) → **474/474** (Domain 214, Infrastructure 156,
  Api 104; Api.Tests mit `~/.dotnet/dotnet`, da der systemweite `dotnet` ohne ASP.NET-Runtime
  den TestHost für Web-Tests nicht starten kann).
- `ng test host` → **78/78** (vorher 71; +7 neue Tests für Note-Semantik und
  Einrichtungsvorgang); `ng test mobile` → 23/23 (unverändert); `npm run lint` → alle Files
  grün; `ng build host` ohne Fehler.
- **E2E-Smoke-Test** (17. Okt. 2026, headless Chromium gegen echten Host + frische SQLite-DB
  unter `/tmp/lario-smoke`): **21/21** Bestätigungen, keine Konsolen-/Seitenfehler —
  leere Liste → Anlage per Formular (mit Tausenderpunkt „1.000,50“) → Zeile in der Liste mit
  korrektem Format → Korrektur-Route (Banner, Vorbelegung) → Korrektur absenden (Eintrag
  aktualisiert, weiterhin genau ein Eintrag) → Doppelanlage zeigt 409-Banner → unbekannte
  Korrektur-Id zeigt „nicht gefunden“-Banner. Salden-Panel auf `/` rendert Konten- und
  Pool-Salden (Giro/Haus sichtbar).
- Hinweis: Browser-Abnahme ist eine headless-Chromium-Prüfung (kein echtes Gerät, kein
  manueller Klick-Lauf) — die manuelle Abnahme bleibt beim Nutzer.

### Übergabe (externes Review)

Branch `step/7.5-salden-anfangsbestaende` auf Basis `63cfe45` (Merge PR #10). Kernfragen:
(1) Ist der partielle Unique-Index das richtige DB-Instrument für die Einmaligkeit (Design 1)?
(2) Korrektur von Anfangsbeständen über `CorrectionService` mit `Allocations` statt `Splits` —
ist die Erweiterung von `CorrectionSpec` sauber genug getrennt? (3) `parseEuroToCents`-Supersatz
inkl. „1.234“ → 1234 € (deutsche Tausenderkonvention) akzeptabel? (4) UI: Korrektur-Route als
eigene Form-Variante derselben Komponente (Muster `expense-form.ts`).
