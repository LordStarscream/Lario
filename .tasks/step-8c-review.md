# Review — Schritt 8, Batch C (Snapshot + Setup)

**Datum:** 27.10.2026 · **Geprüft:** Commit `2f5402d` auf Branch `step/8c-snapshot-setup` (Worktree `/tmp/lario-step8c`, HEAD `2c722f7`).
**Umfang:** 11 Dateien (7 neu, 4 geändert) — exakt die Spec-Dateiliste. Die späteren Merges von main (`e558db1`, `5097afd`, `2c722f7`) enthalten nur `.tasks/*`-Doku, keine Code-Änderungen. Working Tree sauber.

## Unabhängig reproduziert (27.10.)

| Prüfung | Ergebnis |
|---|---|
| `npm run lint` (host + mobile) | ✅ grün |
| `npm test` | ✅ 283 grün (host 130 + mobile 153; mobile +52 vs. Batch B) |
| `npm run build` + `npm run build:mobile` | ✅ grün |
| Diff-Umfang | ✅ exakt 11 Spec-Dateien; `src/generated/` unverändert, keine Dependency-Änderung |

## Spec-Konformität (`.tasks/worker-step8c-task.md`)

- **Dateiliste:** exakt 11 Dateien ✅
- **statements.ts:** SQL-Strings 1:1 zur Spec ✅
- **SnapshotRepository:** `replace` in EINER `executeSet` (all-or-nothing); `fetched_at`-Meta als Marker für `loadSnapshot`/`referenceCatalog`/`fetchedAt`; leere Tabellen = gültiger leerer Snapshot (nicht null) ✅
- **HostClient:** Fluss `BASE → authLogin → TOKEN → 4 parallele Lists → Maps`; Fehlerklassifikation `http_error`/`http_unreachable`/`bad_response`; `fetchMasterData` ohne DB-Schrieb; `logout` best-effort (Re-Login + authLogout, alle Fehler swallowing) ✅
- **BookingsRepository:** 7 neue Reasons; `validateSnapshotReferences` nach `validateExpense`; in `doSave` VOR `getOrCreateDeviceId` (Referenzfehler erzeugt garantiert keine device.id-Meta); in `update` VOR der Schreibtransaktion; `taxCategoryId` bewusst nicht geprüft (Host-Regel B6) ✅
- **Setup-Component:** zoneless, Signals, Doppeltipp-Sperre bei `saveAndLoad`; auth-Meta NUR nach erfolgreichem Replace; `disconnect` behält Snapshot lokal (Offline-Erfassung) ✅
- **Spec-Tests alle vorhanden:** host.client 8, snapshot.repository 7 (+1), bookings Batch C 9, setup.component 8 ✅
- **Fake-Erweiterung:** 4 Snapshot-Tabellen inkl. Transaction-Snapshot/Restore, `simulateRestart`-Persistenz, `reset`, INSERT/DELETE/SELECT-Matcher ✅

## Vertrags-Abgleich (mobil ↔ Host)

- Statuswerte `'active'`/`'archived'` = Host-`EntityStatus`-Serialisierung (`StatusToString`) ✅
- DTO-Felder stimmen mit dem generierten Client überein; `expiresInMinutes`/`baseRevision` via `Number()` ✅
- `GET /api/health` existiert auf dem Host (`HealthResponse(status, dataDirectory)`) ✅

## Befunde: kein P1, kein P2, 3× P3

### P3-1 — Widersprüchliche Anzeige bei Health-Fehler
`checkHealth()` setzt `message`, aber nicht `status`. Nach einem erfolgreichen `saveAndLoad` (`status='success'`) zeigt ein Health-Fehler „Host nicht erreichbar" mit Success-Styling (`[attr.data-status]="status()"` in `setup.html`), während der Health-Block (`data-health`) denselben Inhalt als Fehler darstellt — dieselbe Info doppelt, widersprüchlich gefärbt. Kein Funktions-/Dateneinfluss.
**Fix-Vorschlag:** im Health-Fehlerzweig `status` mit setzen oder `message` nicht setzen (Health-Block genügt).

### P3-2 — Fehlformatiertes `expiresInMinutes` → irreführender Fehler nach erfolgreichem Laden
Host-Vertragsverletzung (nicht-numerisches `expiresInMinutes`) → `Number(...)` = NaN → `new Date(...).toISOString()` in `saveAndLoad` wirft RangeError, **nachdem** das Snapshot-Replace bereits committet ist. Sichtbar: „Invalid time value", auth-Meta nicht gemerkt (Zugangsdaten erneut eingeben), kein Logout. Nächster Versuch funktioniert.
**Fix-Vorschlag:** `expiresInMinutes` in `HostClient.fetchMasterData` validieren (nicht endlich → `bad_response`) — konsistent mit der bestehenden Token-/Array-Prüfung.

### P3-3 — Kleine Robustheits-/Doku-Kleinigkeiten (Bundle)
a) `checkHealth`/`disconnect` setzen kein `busy` (Buttons werden nur über `busy()` deaktiviert): Während einer laufenden Health-Prüfung können „Verbinden" und „Abmelden" geklickt werden; Doppeltipp „Abmelden" kann nach erfolgreichem Logout „Nicht verbunden" anzeigen. Alle Flüsse sind idempotent/best-effort — kein Dateneinfluss.
b) `loadLocalState()` schützt nur `getDeviceId()` per try/catch; ein Wurf aus `loadAuthMeta()`/`fetchedAt()` wäre eine unhandled rejection (View bleibt nutzbar).
c) `HostClient`-JSDoc (Zeilen 16–18) behauptet für die ganze Klasse „DB-schreibfrei … die auth-Meta ebenfalls", aber `clearAuthMeta()` führt DB-Write (Meta-Delete) aus. Die Spec-Trennschärfe ist auf `fetchMasterData` begrenzt; die Klassendoku übergeneralisiert.

## Fazit

Batch C ist speckonform, alle DoD-Checks grün und reproduzierbar, keine Befunde zur Datenintegrität oder Finanzregeln (kein P1/P2). Die 3 P3s sind Anzeige-/Robustheits-Kleinigkeiten — für einen kleinen Nachgang-Commit geeignet, **kein Merge-Blocker**.

**Offene manuelle/Geräte-Abnahmen (unverändert, Batch E):** echte Android-Gerätetests (dauerhafte Offline-Speicherung, App-Neustart, HTTPS/LAN, Synchronisation).
