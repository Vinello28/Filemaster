#!/usr/bin/env bash
# Funzioni condivise dagli script di eng/e2e (da "sourcare", non da eseguire).
# Gira con bash 3.2 (macOS) e bash 5 (Ubuntu, runner GitHub): niente declare -A, mapfile, readlink -f.
# shellcheck shell=bash

E2E_HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
E2E_REPO="$(cd "$E2E_HERE/../.." && pwd)"
# Tutto cio' che l'harness genera sta in .e2e/ alla radice del repo (gitignorata):
#   .e2e/state/ = segreti e chiavi (0700, file 0600): secrets.env, compose.env, keys.env, live.env, admin.txt, ...
#   .e2e/work/  = copie dei sorgenti di Sharp-a-File usate come contesto di build (una per commit).
E2E_DIR="${E2E_DIR:-$E2E_REPO/.e2e}"
E2E_STATE="${E2E_STATE:-$E2E_DIR/state}"
E2E_WORK="${E2E_WORK:-$E2E_DIR/work}"
E2E_PROJECT="${E2E_PROJECT:-filemaster-e2e}"
export E2E_PROJECT

log() { printf '[e2e] %s\n' "$*" >&2; }
die() { printf '[e2e] ERRORE: %s\n' "$*" >&2; exit 1; }
need() { command -v "$1" >/dev/null 2>&1 || die "manca il comando '$1'"; }

# Scrive stdin in un file leggibile solo dall'utente (0600), passando da un temporaneo: chi lo legge non vede mai un
# file a meta'. L'umask 077 resta nel sottoshell: il resto dello script (la copia dei sorgenti, il contesto di build)
# mantiene i permessi normali.
write_private() {
  (umask 077; cat > "$1.tmp")
  chmod 600 "$1.tmp"
  mv "$1.tmp" "$1"
}

# state/secrets.env = segreti stabili (generati una volta); state/compose.env = secrets.env + variabili del run
# (E2E_MAC, E2E_PORT, E2E_APP_IMAGE, E2E_SRC_DIR, ...), riscritto a ogni run-e2e.sh. E' anche l'--env-file di compose.
# load_state lo carica nell'ambiente corrente (se c'e'); require_state pretende che ci sia.
load_state() {
  if [ -f "$E2E_STATE/compose.env" ]; then
    set -a
    # shellcheck disable=SC1091 # file di stato scritto a runtime da run-e2e.sh
    . "$E2E_STATE/compose.env"
    set +a
  fi
}

require_state() {
  [ -f "$E2E_STATE/compose.env" ] || die "$E2E_STATE/compose.env mancante: lancia prima run-e2e.sh"
  load_state
}

# docker compose del progetto, con i file giusti (override Mac incluso) e le variabili segrete di state/.
dc() {
  local args=(-p "$E2E_PROJECT")
  if [ -f "$E2E_STATE/compose.env" ]; then args+=(--env-file "$E2E_STATE/compose.env"); fi
  args+=(-f "$E2E_HERE/docker-compose.e2e.yml")
  if [ "${E2E_MAC:-0}" = "1" ]; then args+=(-f "$E2E_HERE/docker-compose.e2e.mac.yml"); fi
  docker compose "${args[@]}" "$@"
}

# sqlcmd verso il SQL Server del progetto. Due modi:
#  - default (CI Ubuntu): `compose exec` nel contenitore mssql (sqlcmd e' nell'immagine ufficiale);
#  - E2E_SQLCMD_IMAGE valorizzata (Mac con azure-sql-edge, che non ha sqlcmd): contenitore client usa-e-getta
#    sulla rete del progetto (puo' essere amd64 emulato: un client solo non soffre i problemi del server).
# Argomenti: quelli di sqlcmd (-Q ..., -h -1 ...). La password arriva a sqlcmd da SQLCMDPASSWORD, non con -P.
# -I = QUOTED_IDENTIFIER ON: sqlcmd parte con OFF e SQL Server rifiuta le scritture su tabelle con indici filtrati
# (contatti, Msg 1934), come fa invece EF/ADO.NET che lo accende sempre.
sqlcmd_run() {
  : "${MSSQL_SA_PASSWORD:?MSSQL_SA_PASSWORD non impostata (state/compose.env)}"
  local path="${E2E_SQLCMD_PATH:-/opt/mssql-tools18/bin/sqlcmd}"
  if [ -n "${E2E_SQLCMD_IMAGE:-}" ]; then
    docker run --rm -i ${E2E_SQLCMD_PLATFORM:+--platform "$E2E_SQLCMD_PLATFORM"} \
      --network "${E2E_PROJECT}_default" -e SQLCMDPASSWORD="$MSSQL_SA_PASSWORD" \
      --entrypoint "$path" "$E2E_SQLCMD_IMAGE" -C -I -S mssql -U sa "$@"
  else
    dc exec -T -e SQLCMDPASSWORD="$MSSQL_SA_PASSWORD" mssql "$path" -C -I -S localhost -U sa "$@"
  fi
}
