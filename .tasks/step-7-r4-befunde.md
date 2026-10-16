# Schritt 7 — Review R4 (externes Review, PR #10) — offene Befunde

Quelle: PR-Comments von LordStarscream (16. Okt. 2026, 09:57 UTC, Review von `96f5329`).
R3 hat 3 der 6 Befunde von Runde 1 nur teilweise behoben; 3 waren unverändert.
R4 = Runde 2. Alle 6 sind offen, Stand: PR #10, Branch `step/7-api-desktop-eingabe`.

## Befunde

### F1 [P1, teilweise] — pending-operation.ts:42
Unklaren Commit bei Payload-Wechsel nicht in neue Operation verwandeln.
- Gleiche Payload → dieselbe Id (OK, R3-Fix).
- Aber: `outcomeUncertain=true` + geänderte Notiz → `nextId` erzeugt automatisch neue Id → zweite Buchung möglich.
- Auch ein späteres 401/4xx beweist nicht, dass der vorangegangene unbekannte Versuch uncommitted war.
- **Fix-Anforderung:** Bei outcomeUncertain Id + ursprüngliche Payload festhalten; abweichende Einreichung blockieren, bis die alte Operation quittiert/abgeglichen ist; bewusster neuer Vorgang erst nach geklärtem Zustand (manueller Reset).
- Test: Komponenten-Ablauf (OpeningBalance.submit): 1. Request wirft Netzwerkfehler, Notiz ändern, 2. Submit → gleicher Id-Nutzung blockiert (kein zweiter API-Aufruf mit anderer Id).

### F2 [P2, unverändert] — master-data.ts
Erfolgsfall der Stammdatenaktionen hält busy dauerhaft auf true.
- createAccount/createPool/createCategory/createTaxCategory, setArchived, submitPreset, removePreset: kein busy.set(false) nach Erfolg; nur submitRename hat finally.
- **Fix:** finally-Block (busy.set(false)) für alle betroffenen Aktionen.
- Test: Konto anlegen → busy=false → Pool/Kategorie ohne Seitenreload anlegen.

### F3 [P2, teilweise] — bookings-list.ts:302 (refundValid/submitRefund)
Nur ausgewählte Pools erstatten.
- Aggregation + Restdeckel: OK (R3-Fix).
- Aber: refundValid verlangt amount>0 für JEDE Poolzeile; submitRefund sendet alle Zeilen → einseitige Erstattung blockiert.
- **Fix:** Leere/0-Zeilen = nicht ausgewählt, beim Senden auslassen; mindestens eine positive gültige Zeile verlangen; nichtleere ungültige Eingaben und Überschreitungen weiter ablehnen.
- Test: Komponenten-/Requesttest mit zwei offenen Pools, nur eine positive Erstattungszeile.

### F4 [P2, unverändert] — expense-form.ts:364 (inherit-Modus)
Steuer-Overrides im Modus „Wie Voreinstellung“ gehen verloren.
- Bei relevanter Voreinstellung sind Steuerkategorie/Teilmengenerfassung/Teilbetrag sichtbar editierbar, taxMode bleibt inherit; buildCommand sendet tax=null; splitIssue validiert Teilbetrag nicht.
- **Fix (eine der beiden Optionen):** Feldbearbeitung wechselt auf explizite Kennzeichnung ODER Felder im inherit-Modus ausschließlich schreibgeschützt. Eingaben, die sichtbar bearbeitbar sind, müssen validiert und gesendet werden.
- Test: Formularzustand → API-Payload: Teilbetrag 5 € → markedAmount=500; geänderte Steuerkategorie bleibt erhalten (bzw. schreibgeschützte Darstellung prüfen).

### F5 [P2, teilweise] — expense-form.ts:372 (Split-Ersatz bei Korrektur)
Unbearbeitete Steuerdaten auch beim Split-Ersatz erhalten.
- Skalar-Pfad + Split-Ids: OK (R3-Fix).
- Aber: ändert sich ein Split (z. B. Notiz) → submitCorrection sendet alle Splits; deren tax.note wird pauschal null gesetzt (geht verloren); ursprünglich unmarkierte Splits können über tax=null aktuelle Kategoriepresets neu materialisieren.
- **Fix:** Vollständige ursprüngliche Steuerdaten je bestehendem Split halten; unveränderte Kennzeichnungen beim Ersatz unverändert weitergeben; bewusste Steueränderung/Neuvorbelegung von sonstigen Splitänderungen unterscheiden.
- Test: ≥2 Splits, Notiz/Betrag eines ändern → Steuer-Notiz und Nichtmarkierung des anderen bleiben erhalten.

### F6 [P2, unverändert] — expense-form.ts:243 (Zeilenschlüssel)
Doppelte Zeilenschlüssel reproduzierbar.
- newSplitRow addiert index + keyBase; nach Löschungen Kollisionen: Start [1], +3 → [1,2,4,6], −2 → [4,6], +1 → [4,6,6]; removeSplit(6) löscht beide.
- **Fix:** Schlüssel ausschließlich aus monotonem Zähler (key = nextKey(), auch für initiale Zeile konsumieren), keine Addition der Listenposition.
- Test: Hinzufügen-/Löschenfolge, eindeutige ngModel-Namen, Entfernen genau einer Zeile.

## Bemerkung aus dem Review
„Die neuen Clienttests prüfen Hilfsfunktionen (Poolaufbereitung/Fingerprint), nicht diese vollständigen Formularabläufe. … Bitte die offenen Fälle als tatsächliche Komponenten-/Ablauftests ergänzen.“ → Die R4-Tests müssen die echten Komponenten-/Ablaufpfade testen (TestBed oder Klassenmethoden mit simulierten Abhängigkeiten), nicht nur Hilfsfunktionen.

## Status (16. Okt. 2026, nach Server-Fix)

- **F5 Server: behoben** (dieser Commit): `CorrectionSpec.BuildUpdatedBooking` — `tax = null` = „unverändert“: ein bestehender Split behält die vollständige Original-Kennzeichnung (inkl. Steuer-Notiz und bisheriger Nichtmarkierung), ein neuer Split wird wie bei der Neuanlage aus der Kategorie-Voreinstellung materialisiert; eine explizite Steueränderung ersetzt die bisherige Kennzeichnung. DTO-Dokumentation + OpenAPI-Beispiel aktualisiert.
  - **Zusatzbefund (bei der Verifikation):** In-place-Korrektur verschwand mit einer bestehenden Steuerkennzeichnung: `ReplaceChildCollection` (löschen + neu anlegen) + Datenbank-Cascade + weitergeführte Instanz (Schlüssel gesetzt → „Modified“) → `tax = null` im Detail trotz Markierung. Fix: `BookingRepository.ReplaceSplits`/`ReconcileSplitTax` — Teilposten-Zeilen werden in-place rekonsiliert (gleiche Id → Feld-Update inkl. 1:1-Kennzeichnungs-Zeile, neu → anlegen, entfallen → löschen); dafür Teilposten-/Kennzeichnungs-Eigenschaften im Domain-Modell setbar.
  - Regressionstests: `Step7ReviewR4Tests` (5 Domain + 4 Api mit echter SQLite-Integration); der Api-Test zur Kennzeichnungs-Erhaltung schlägt auf dem Stand vor dem Fix fehl. 444/444 Tests, 0 Build-Warnungen.
- **F1–F4, F6 + F5-Client-Teil: behoben** (16. Okt. 2026, Commit `f8bf24a`, gepusht):
  - **F1:** `PendingOperation.nextId` liefert bei `outcomeUncertain` + geänderter Payload `null` (Einreichung blockiert, kein zweiter API-Aufruf mit neuer Id); Abgleich in der Buchungsliste, dann manuelles `resetPendingOperation`. ExpenseForm und OpeningBalance zeigen die Blockade mit dem Ausgangshinweis an.
  - **F2:** `finally { busy.set(false) }` in allen Stammdaten-Schreibpfaden. **Zusatzbefund (Verifikation):** `reload()` löschte die Erfolgsmeldung synchron nach `ok()` — das Feedback war dadurch unsichtbar; Fix: Erfolg bleibt bis zur nächsten Aktion (fail() ersetzt Erfolg, ok() Fehler).
  - **F3:** Erstattungs-Dialog: leere/0-Zeilen sind nicht ausgewählt und werden beim Senden ausgelassen; mindestens eine positive Zeile erforderlich; ungültige nicht-leere Einträge und Deckelüberschreitung bleiben abgelehnt.
  - **F4:** Option „Reset auf Voreinstellung“: `onTaxModeChange('inherit')` inaktiviert die explizit gewählte Steuerkategorie/Teilmenge (Feld leert, Teilbetrag verwirft) — die Voreinstellung der Kategorie wird wieder wirksam.
  - **F5-Client:** `buildCommand` sendet je Split die vollständige Steuerkennzeichnung (inkl. `note`); inherit-Modus sendet `tax = null` (= „unverändert“ nach der Server-Konvention aus dem F5-Server-Fix); Steuer-Notiz ist Teil des Korrektur-Fingerprints und wird beim Vorladen aus dem Detail übernommen.
  - **F6:** Zeilen-Keys ausschließlich aus monotonem Zähler (`keyBase`, `nextKey()` — auch für die initiale Zeile), keine Listenposition-Addition; Kollisionsfolge aus dem Befund ist damit nicht mehr reproduzierbar.
- **Ablauf-Tests (Review-Bemerkung umgesetzt):** vier neue host-Specs mit echter Komponentenklasse (TestBed, nur API-Statik und Injections simuliert) — Commit `9ba83c1`, gepusht: `expense-form.spec` (F1: idempotentes Wiedersenden, Blocken bei geänderten Daten, neue Id nach 4xx, corr-Präfix; F4; F5; F6), `bookings-list-refund.spec` (F3), `master-data.spec` (F2), `opening-balance.spec` (F1 + Validierung). **Prüfung:** `ng test host` 7/7 Files, 39 Tests; `ng test mobile` 2/2 Files, 23 Tests; Production-Build und Lint ohne Befund. .NET unverändert: 444/444.
- **Offen:** keine Befunde — R4 ist vollständig umgesetzt; auf das weitere externe Review (PR #10) warten.
