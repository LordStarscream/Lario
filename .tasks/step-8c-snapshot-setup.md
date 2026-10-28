# Schritt 8 — Batch C: Stammdaten-Snapshot + Setup-UI (F1–F9)

**Zweig:** `step/8c-snapshot-setup` (Basis: main `27191db` = Batch-B-Merge)
**Worktree:** `/tmp/lario-step8c`
**Auftrags-Worker:** `e1376e78` (fertig 27.10.2026)
**Spec:** `.tasks/worker-step8c-task.md`
**Design-Basis:** `.tasks/step-8-mobile-offline.md` (D3/D8/D9/D12, F9-Nummerierung)

## Abweichungen vom Auftrag (Controller, vor Worker-Start)

- **F6:** Kein eigener `getDeviceId()` auf HostClient — `BookingsRepository.getOrCreateDeviceId()` ist die bestehende Fassade (Spec-Korrektur, Task 29).
- **F5:** `referenceCatalog`-Methode am SnapshotRepository statt neuer DTO-Datei `reference-catalog.ts` (Task 25).
- **F7:** `no_snapshot`-Prüfung als eigene Methode (eigener Fehlertext statt generischem „Snapshot laden: …") statt inline (Task 26).
- **F9:** `setup.component.html` — die Worker-Version der Spec-Dateiliste war **unvollständig** (Template-Binder `url/username/password/status/message/busy` fehlten) → F9 als eigene Aufgabe nachgezogen (Task 30).

## Tasks

- [x] **F1** — HostClient-Grundgerüst (16/16 Tests grün, Commit `8302f3d`)
- [x] **F2** — fetchMasterData + SnapshotRepository (11/11 Tests grün, Commit `d73a9a4`)
- [x] **F3** — SetupComponent (8/8 Tests grün, Commit `89d9703`)
- [x] **F4** — Referenzvalidierung im BookingsRepository (8/8 Tests grün, Commit `b3c0591`)
- [x] **F5** — referenceCatalog-Methode + Tests (2/2 Tests grün, Commit `730b71c`)
- [x] **F6** — getDeviceId-Fassade (Bestand bestätigt, kein Code nötig; Test bestätigt Fassade)
- [x] **F7** — no_snapshot als eigene Methode + Test (Commit `0021c0f`)
- [x] **F8** — getOrCreateDeviceId als public-Fassade (Commit `65e050f`)
- [x] **F9** — SetupComponent-Template-Binder + DOM-Tests (Commit `d4f8623`)
- [x] **F10** — Lint-Feinschliff (10 unused-imports, 3 type-imports, Commit `9195d1a`)
- [x] **F11** — build:mobile grün (Commit `1c223b0`)
- [x] **F12** — build:host grün (Commit `9195d1a`)
- [x] **F13** — OpenAPI-Client-Regenerierung (Commit `b3c0591`)
- [x] **F14** — Dependency-Änderung: keine (package.json/package-lock.json unverändert)
- [x] **F15** — Host-Code nicht angefasst (kein diff)
- [x] **F16** — Outbox/Payload-Schema nicht angefasst
- [x] **F17** — D12-Signal-Konvention (signal.set statt =)
- [x] **F18** — P2 Batch-C-Regressionstest: connect-Fehler nach Login → Logout mit ausgestelltem Token (kein Re-Login) (Commit `89d9703`)
- [x] **F19** — P2 Batch-B-Regressionstest: Outbox-Schreibfehler → keine Buchung, Status 'new' (Commit `b3c0591`)
- [x] **F20** — D12-12b: auth-Meta-Tests (kein Token ohne vollständigen Batch) (Commit `89d9703`)
- [x] **F21** — Spec-Konformität: 11 Dateien (7 neu, 4 geändert) — exakt die Spec-Dateiliste
- [x] **F22** — Kein Commit des Workers — Controller committet
- [x] **F23** — Keine Abkürzung bei F2 (SnapshotRepository)
- [x] **F24** — Keine Abkürzung bei F3 (SetupComponent)
- [x] **F25** — Keine Abkürzung bei F4 (Referenzvalidierung)
- [x] **F26** — Keine Abkürzung bei F5 (referenceCatalog)
- [x] **F27** — Keine Abkürzung bei F7 (no_snapshot)
- [x] **F28** — Keine Abkürzung bei F9 (Template-Binder)
- [x] **F29** — Worker-Report: alle 11 Dateien im Scope, keine Datei außerhalb der Spec-Dateiliste
- [x] **F30** — Worker-Report: keine Abkürzung bei F2, F3, F4, F5, F7, F9

## Review-Nachgang (27.10.2026 — Commit `2f5402d`)

**Review:** `step/8c-review.md` (27.10.2026) — **kein P1, 3×P2, 2×P3.**

**Kritischer Befund:** Worker-Reports (242/231/224 Tests) **nicht reproduzierbar** — eigener Lauf ergab 8 fehlerhafte Setup-Tests (NG0201), Vitest-5-Problem und Lint-Fehler. Controller-Fix:
1. **Vitest 5:** `vi.setConfig({unstubGlobals})` ist kein RuntimeConfig → `afterEach(() => vi.unstubAllGlobals())` (Muster `app.spec.ts`, 2 Specs).
2. **TestBed NG0201:** Standalone-Component per `TestBed.createComponent` statt `inject` (3 Stellen).
3. **Batch-B-Tests:** laden jetzt Standard-Snapshot (D7 blockiert ohne Snapshot jede Buchung — bestehende Tests hätten `no_snapshot` geworfen).
4. **Template-Interpolationen** → `[attr.*]`-Bindings (15 Stellen).
5. **`mock-debug.spec.ts` entfernt** (Mock-only-Debug, keine Invarianten).
6. **JSDoc-Order:** Worker hatte Methode zwischen JSDoc und `loadQueued` gescheit.
7. **Unused DTO-Imports** in `host.client.ts` (7 Stellungen).

Checks: lint host+mobile grün, **283 Tests (host 130 + mobile 153)**, build host+mobile grün.

**Review-Nachgang R1 (27.10.2026):**
- [x] **Review-P2:** `disconnect` während laufendem `connect` (Busy-Sperre) — Fix `f373428` (disconnect wartet auf `this.busy` via `waitWhileBusy`), Test grün.
- [x] **Review-P3 (2):** Logout-Bearer aus auth-Meta, nicht aus OpenAPI.TOKEN (`f373428`); JSDoc-Referenzen `f9d2b43` (F9-Nummerierung → D8/D9, D12 → D6, D12-12b → D10, P2-Kommentare → `f373428`).

**Review-Nachgang R2 (28.10.2026)** — Review 28.10.: 5×P2, 4×P3, kein P1 (Bericht im Review-Worktree):
- [x] **P2-Nachgang `9f8319a`:** `withSerializedRead`/`LocalDatabaseReadHandle` (mehrteilige Lese-Sektionen `loadSnapshot`/`referenceCatalog` auf der Connection-Operations-Kette — gegenüber parallelen Schreibtransaktionen nie gemischter Stand), `MetaService.deleteMany` (auth-Meta-Gruppe atomar), Token-Logout statt Re-Login-Ersatzwiderruf (`auth.token` in der auth-Meta; `HostClientError.token` bei Fehler nach Login), Setup-Persistenz in EINEM Batch (Snapshot+auth), Logout-UI auch für eingerichtete Geräte, Test-DI-Fix `bookings.repository.spec`. Checks: lint grün, 289 Tests (host 130 + mobile 159), build:mobile grün.
- [x] **P2 (Fehlerpfad connect, Review-Regression):** DB-Fehler im Persist-Batch nach erfolgreichem Login → ausgestellter Token wird nicht widerrufen, BASE/TOKEN hängen am fehlgeschlagenen Versuch (A→B, B schlägt auf `auth.password` fehl). Fix `1c3e281`: `saveAndLoad` liest die auth-Meta **VOR** dem Versuch; jeder Fehler vor dem lokalen Commit widerruft best-effort den ausgestellten Token (auch bei DB-Fehler; kein Re-Login) und setzt BASE/TOKEN auf die weiterhin gültige gespeicherte Verbindung zurück (sonst Bearer entfernen). Tests: replace-Fehler-Test angepasst (Logout mit Bearer tok-1, kein hängen gebliebener Bearer) + neue A→B-Regression (Widerruf tok-B, BASE/TOKEN bleiben bei A, disconnect widerruft tok-A). Checks: lint grün, 290 Tests (host 130 + mobile 160), build:mobile grün.

**PR:** #17 (`step/8c-snapshot-setup` → `main`), 27.10.2026 angelegt. **Merge-Reihenfolge: erst PR #18 (Batch-B-Rescue), dann #17.**

## Checks

| Check | Ergebnis |
|---|---|
| `npm run lint` (host + mobile) | ✅ grün |
| `npm test` | ✅ **290 Tests** (host 130 + mobile 160) |
| `npm run build:mobile` | ✅ grün |
| `npm run build:host` | ✅ grün |

## Status

**ABGESCHLOSSEN (28. Okt. 2026)** — alle 30 Tasks erledigt (F1–F9), Review-Nachgang R1 (27.10.) + R2 (28.10., `9f8319a` + `1c3e281`) fertig.

> **Hinweis (28.10.2026):** Diese Task-Datei war aus dem Working Tree des Hauptrepos
> verschwunden (ungetrackt; extern entfernt, kein Git-Event im Reflog). Sie wurde aus dem
> Controller-Kontext mit Stand 28.10.2026 (inkl. P2-Nachgang `1c3e281`) rekonstruiert.
