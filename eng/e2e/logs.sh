#!/usr/bin/env bash
# Salva i log dei contenitori del progetto compose "filemaster-e2e" (server e database) in una cartella, un file per
# servizio. Serve a e2e.yml quando un passo fallisce; in locale basta anche `docker compose ... logs`.
#
#   logs.sh <cartella di uscita>
# Non fallisce se il progetto non c'e' (o e' gia' stato rimosso): scrive solo cio' che trova.
set -euo pipefail
# shellcheck source=_common.sh
. "$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/_common.sh"
need docker

[ $# -eq 1 ] || die "uso: logs.sh <cartella di uscita>"
OUT="$1"
mkdir -p "$OUT"
load_state
for service in app mssql; do
  dc logs --no-color --timestamps "$service" > "$OUT/$service.log" 2>&1 || log "log di $service non disponibili"
done
dc ps -a > "$OUT/ps.txt" 2>&1 || true
log "log in $OUT"
