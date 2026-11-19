#!/usr/bin/env bash
set -euo pipefail

# Ausschließlich synthetischer Testhost auf Loopback; temporäre Daten erstellt/löscht die Spec.
task_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$task_root"
dotnet build src/Lario.Host/Lario.Host.csproj --no-restore -m:1
export LARIO_SYNC_HOST="http://127.0.0.1:${LARIO_SYNC_SMOKE_PORT:-18089}"
export LARIO_SYNC_HOST_BINARY="$task_root/src/Lario.Host/bin/Debug/net10.0/Lario.Host"
cd frontend
npx ng test mobile --watch=false --include='**/sync-host.integration.spec.ts'
