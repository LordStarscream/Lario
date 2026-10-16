# Schritt 7 — Review R1 Befundabarbeitung (PR #9)

**Review:** PR #9 Review 1 (Commit `8c856bc`, Stand T1–T5), 3 Befunde:

1. **P1** Auth-Pfadprüfung `Ordinal` vs. case-insensitives Routing → Bypass über `/API/V1/...`
2. **P2** Command-Collections (`null`/`[null]`) → 500 statt 400
3. **P2** Storno-`totalAmount` zählt Konto- und Poolachse doppelt

## Umsetzung

- [ ] Fix 1: `ApiAuth.cs` segmentbasiert + `OrdinalIgnoreCase` (Login-Ausnahme ebenfalls)
- [ ] Fix 2: `JournalWriteEndpoints.cs` zentrale `RequiredList`/`OptionalList`-Transportvalidierung (400 validation_error)
- [ ] Fix 3: `JournalDtoMapper.ComputeTotal(Booking, BookingJournal)` — Reversal = Negation des Original-Totals über `ReversesBookingId` (inkl. Widerruf)
- [ ] Regressionstests `Step7ReviewR1Tests.cs` (P2 + P3) + Mixed-Case-Auth-Tests in `AuthApiTests.cs`
- [ ] Build + volle Testlauf (dotnet pro Projekt)
- [ ] Commit (nur Review-Fix; T6-Working-Tree bleibt unberührt) + Push + Review-Antwort
- [ ] Task-Dateien/ Index nachtragen

## Abnahmekriterien des Reviews (Regressionen)

- P1: GET + POST ohne Token in gemischter Schreibweise → 401, Daten unverändert; Login bleibt erreichbar (case-insensitiv)
- P2: fehlende Liste, explizites null und `[null]` → 400, keine neue Buchung/Revision
- P3: totalAmount für Ausgabe-/Einnahmestorno, Transfer-/Umwidmungsstorno, Widerruf
