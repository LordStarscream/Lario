#!/usr/bin/env bash
# Lario-Host-DB-Snapshot für Smoke Tests:
# zählt die Zeilen aller Tabellen plus Migrationshistorie und schreibt
# das Ergebnis in eine Textdatei. Zwei Snapshots (vorher/nachher) per
# `diff` vergleichen — außer dem Zeitstempel muss der Diff leer sein.
#
# Verwendung:
#   tools/lario-snapshot.sh /tmp/vor.txt
#   # ... Host beenden + neu starten ...
#   tools/lario-snapshot.sh /tmp/nach.txt
#   diff /tmp/vor.txt /tmp/nach.txt
#
# Optional: andere DB-Datei über 2. Argument oder LARIO_DB:
#   tools/lario-snapshot.sh /tmp/vor.txt /tmp/lario-smoke/lario.sqlite

set -euo pipefail

OUT="${1:?Ziel-Datei angeben, z. B. /tmp/vor.txt}"
DB="${2:-${LARIO_DB:-$HOME/.local/share/lario/lario.sqlite}}"

if [[ ! -f "$DB" ]]; then
  echo "Fehler: Datenbank nicht gefunden: $DB" >&2
  exit 1
fi

{
  echo "# $(date -Is)"
  echo "# db=$DB"
  echo "migrations=$(sqlite3 "$DB" 'SELECT COUNT(*) FROM __EFMigrationsHistory;')"
  while IFS= read -r t; do
    printf '%s=%s\n' "$t" "$(sqlite3 "$DB" "SELECT COUNT(*) FROM \"$t\";")"
  done < <(sqlite3 "$DB" "SELECT name FROM sqlite_master WHERE type='table' ORDER BY name;")
} > "$OUT"

echo "Snapshot geschrieben: $OUT"
