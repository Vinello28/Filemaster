#!/usr/bin/env bash
# Porta su un Sharp-a-File REALE (immagine costruita dal Dockerfile del clone) + SQL Server, lo prepara e stampa
# le variabili per la suite live di Filemaster (tests/Filemaster.IntegrationTests/Live).
#
#   run-e2e.sh --src <clone di Sharp-a-File> [--ref <git ref>] [--mac] [--port N] [--no-build]
#   (env equivalenti: E2E_SRC, E2E_REF, E2E_MAC=1, E2E_PORT, E2E_NO_BUILD=1; richiede MSSQL_ACCEPT_EULA=Y)
#   E2E_MAX_UPLOAD_BYTES: limite di upload del server (default 4194304 = 4 MiB, per provare i 413 con pochi MiB)
#
# Passi: git archive del ref -> docker build -> mssql up -> migrate -> bootstrap-admin -> storage-password -generate
#        -> seed.sh (ente, 3 chiavi, anagrafica) -> run -> attesa /healthz + /readyz + GET /tenant con la chiave read.
# Idempotente: rilanciarlo con lo stesso ref non rifa cio' che e' gia' fatto (admin, password dell'archivio, chiavi, seed).
# --mac: Apple Silicon senza Rosetta (vedi docker-compose.e2e.mac.yml). Su Ubuntu CI NON passarlo.
# Uscita: le variabili FILEMASTER_E2E_* su stdout e in .e2e/state/live.env (0600); su GitHub Actions anche in $GITHUB_ENV.
set -euo pipefail
# shellcheck source=_common.sh
. "$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/_common.sh"

SRC="${E2E_SRC:-}"
REF="${E2E_REF:-HEAD}"
MAC="${E2E_MAC:-0}"
PORT="${E2E_PORT:-18080}"
NO_BUILD="${E2E_NO_BUILD:-0}"
MAX_UPLOAD="${E2E_MAX_UPLOAD_BYTES:-4194304}"   # limite di upload del server (SHARPAFILE_MAX_UPLOAD_SIZE_BYTES)
while [ $# -gt 0 ]; do
  case "$1" in
    --src) [ $# -ge 2 ] || die "--src vuole un percorso"; SRC="$2"; shift 2 ;;
    --ref) [ $# -ge 2 ] || die "--ref vuole un ref git"; REF="$2"; shift 2 ;;
    --mac) MAC=1; shift ;;
    --port) [ $# -ge 2 ] || die "--port vuole un numero"; PORT="$2"; shift 2 ;;
    --no-build) NO_BUILD=1; shift ;;
    -h|--help) sed -n '2,13p' "${BASH_SOURCE[0]}"; exit 0 ;;
    *) die "argomento sconosciuto: $1" ;;
  esac
done

[ -n "$SRC" ] || die "indica il clone di Sharp-a-File: --src <percorso> (o E2E_SRC)"
[ -d "$SRC/.git" ] || [ -f "$SRC/.git" ] || die "$SRC non e' un repository git"
case "$PORT" in ''|*[!0-9]*) die "porta non valida: $PORT" ;; esac
case "$MAX_UPLOAD" in ''|*[!0-9]*) die "E2E_MAX_UPLOAD_BYTES non valido: $MAX_UPLOAD" ;; esac
[ "${MSSQL_ACCEPT_EULA:-}" = "Y" ] || die "imposta MSSQL_ACCEPT_EULA=Y dopo aver letto la licenza di SQL Server"
need docker; need git; need tar; need curl; need python3
docker compose version >/dev/null 2>&1 || die "serve 'docker compose' (v2)"

SHA="$(git -C "$SRC" rev-parse --verify "${REF}^{commit}")" || die "ref sconosciuto: $REF"
SHORT="${SHA:0:12}"
LABEL="$SHORT"; [ "$MAC" = "1" ] && LABEL="$SHORT-edge"
CTX="$E2E_WORK/$LABEL"
IMAGE="sharpafile-e2e:$LABEL"
mkdir -p "$E2E_STATE" "$E2E_WORK"; chmod 700 "$E2E_STATE"

# --- segreti stabili (>= 32 byte, tutti diversi; la password di SA non puo' contenere ';') ---------------------------
SECRETS="$E2E_STATE/secrets.env"
if [ ! -f "$SECRETS" ]; then
  log "genero i segreti locali ($SECRETS)"
  python3 - <<'PY' | write_private "$SECRETS"
import secrets
print("MSSQL_SA_PASSWORD=Aa1-" + secrets.token_hex(12))
print("SHARPAFILE_SESSION_SECRET=" + secrets.token_hex(32))
print("SHARPAFILE_STORAGE_MASTER_KEY=" + secrets.token_hex(32))
PY
fi

# --- variabili del run (anche --env-file di compose) -----------------------------------------------------------------
# Il digest di mssql/server:2022 e' quello di deploy/docker-compose.yml di Sharp-a-File (copiato con un comando, lezione 3).
{
  cat "$SECRETS"
  echo "MSSQL_ACCEPT_EULA=Y"
  echo "E2E_MAC=$MAC"
  echo "E2E_PORT=$PORT"
  echo "E2E_MAX_UPLOAD_BYTES=$MAX_UPLOAD"
  echo "E2E_SRC_DIR=\"$CTX\""
  echo "E2E_APP_IMAGE=$IMAGE"
  echo "E2E_COMMIT=$SHORT"
  echo "E2E_REF_SHA=$SHA"
  if [ "$MAC" = "1" ]; then
    echo "E2E_SQLCMD_IMAGE=${E2E_SQLCMD_IMAGE:-mcr.microsoft.com/mssql/server:2022-latest@sha256:4402d880dd4c34bfa7d8705e56a86cd6c88da80a1f6bbbe741f999e76264a090}"
    echo "E2E_SQLCMD_PLATFORM=${E2E_SQLCMD_PLATFORM:-linux/amd64}"
  fi
} | write_private "$E2E_STATE/compose.env"
load_state

# --- contesto di build: copia pulita del ref (il clone non si tocca) -------------------------------------------------
if [ "$NO_BUILD" != "1" ]; then
  log "sorgente: $SHA ($REF) -> $CTX"
  rm -rf "$CTX"; mkdir -p "$CTX"
  git -C "$SRC" archive "$SHA" | tar -x -C "$CTX"
  if [ "$MAC" = "1" ]; then
    # azure-sql-edge e' SQL Server 15.0: non conosce ISJSON(x, OBJECT) (SQL Server 2022). Solo nella COPIA: la
    # migrazione iniziale usa quel vincolo CHECK; il modello/snapshot EF restano invariati (EF confronta modello e
    # snapshot, non il database), quindi `migrate` non rileva differenze.
    migration="$CTX/src/SharpAFile.Infrastructure/Persistence/Migrations/20260923172733_InitialSchema.cs"
    grep -q 'ISJSON(metadata, OBJECT) = 1' "$migration" || die "--mac: ISJSON(metadata, OBJECT) non trovato in $migration"
    sed -i.bak 's/ISJSON(metadata, OBJECT) = 1/ISJSON(metadata) = 1/' "$migration"
    rm -f "$migration.bak"
    log "--mac: ISJSON(metadata, OBJECT) reso compatibile con SQL Server 15.0 nella copia"
  fi
  log "docker compose build (Dockerfile del server: il suo global.json resta dentro l'immagine sdk)"
  SECONDS=0
  dc build migrate >&2
  log "build: ${SECONDS}s"
else
  [ -d "$CTX" ] || die "--no-build: manca $CTX (lancia una volta senza --no-build)"
fi

# --- SQL Server -------------------------------------------------------------------------------------------------------
log "avvio SQL Server"
SECONDS=0
dc up -d --wait --no-deps mssql >&2 || { dc logs --tail 40 mssql >&2 || true; die "SQL Server non e' diventato healthy"; }
i=0
until sqlcmd_run -b -Q 'SELECT 1' >/dev/null 2>&1; do
  i=$((i + 1))
  [ "$i" -lt 90 ] || die "SQL Server non risponde a SELECT 1"
  sleep 2
done
log "SQL Server pronto in ${SECONDS}s"

# --- migrate / bootstrap-admin / storage-password -------------------------------------------------------------------
log "migrate"
dc run --rm --no-deps -T migrate >&2

admins="$(sqlcmd_run -b -h -1 -W -d sharpafile -Q 'SET NOCOUNT ON; SELECT COUNT(*) FROM accounts WHERE tenant_id IS NULL' | tr -d '[:space:]')"
if [ "$admins" = "0" ]; then
  log "bootstrap-admin e2e@example.com (password generata in $E2E_STATE/admin.txt)"
  dc run --rm --no-deps -T migrate bootstrap-admin -email e2e@example.com -generate | write_private "$E2E_STATE/admin.txt"
else
  log "bootstrap-admin: gia' presente, salto"
fi

# Senza password dell'archivio ogni upload risponde 503. Va impostata PRIMA di avviare `run` (un demone gia' attivo
# la rileva entro 30 s). Non si rigenera se esiste (ogni -generate crea una nuova versione e una ricifratura).
status_out="$(dc run --rm --no-deps -T app storage-password -status 2>&1 || true)"
if printf '%s' "$status_out" | grep -q 'Nessuna password'; then
  log "storage-password -generate"
  dc run --rm --no-deps -T app storage-password -generate | write_private "$E2E_STATE/storage-password.txt"
else
  log "storage-password: gia' impostata, salto"
fi

# --- ente, chiavi, anagrafica ------------------------------------------------------------------------------------------
"$E2E_HERE/seed.sh"
# shellcheck disable=SC1091
. "$E2E_STATE/keys.env"

# --- demone ---------------------------------------------------------------------------------------------------------
log "avvio app (run) su 127.0.0.1:$PORT"
SECONDS=0
dc up -d --no-deps app >&2
URL="http://127.0.0.1:$PORT"
i=0
until curl -fsS -o /dev/null "$URL/healthz" 2>/dev/null; do
  i=$((i + 1))
  [ "$i" -lt 90 ] || { dc logs --tail 60 app >&2 || true; die "/healthz non risponde su $URL"; }
  sleep 1
done
i=0
until curl -fsS -o /dev/null "$URL/readyz" 2>/dev/null; do
  i=$((i + 1))
  [ "$i" -lt 30 ] || { dc logs --tail 60 app >&2 || true; die "/readyz non e' ready"; }
  sleep 1
done
code="$(curl -sS -o "$E2E_STATE/tenant.json" -w '%{http_code}' -H "X-API-Key: $E2E_KEY_READ" "$URL/tenant")"
[ "$code" = "200" ] || { cat "$E2E_STATE/tenant.json" >&2; dc logs --tail 60 app >&2 || true; die "GET /tenant con la chiave read -> HTTP $code (atteso 200)"; }
log "app pronta in ${SECONDS}s; GET /tenant = 200"

# Su GitHub Actions le chiavi (usa-e-getta, ma pur sempre chiavi) non devono comparire nei log.
if [ "${GITHUB_ACTIONS:-}" = "true" ]; then
  for k in "$E2E_KEY_WRITE" "$E2E_KEY_READ" "$E2E_KEY_ADMIN"; do echo "::add-mask::$k"; done
fi

{
  echo "FILEMASTER_E2E_URL=$URL"
  echo "FILEMASTER_E2E_KEY=$E2E_KEY_WRITE"
  echo "FILEMASTER_E2E_READ_KEY=$E2E_KEY_READ"
  echo "FILEMASTER_E2E_ADMIN_KEY=$E2E_KEY_ADMIN"
  echo "FILEMASTER_E2E_MAX_UPLOAD_BYTES=$MAX_UPLOAD"
  echo "FILEMASTER_E2E_SERVER_SHA=$SHA"
} | write_private "$E2E_STATE/live.env"
cat "$E2E_STATE/live.env"
# Stesse variabili per i passi successivi di GitHub Actions.
if [ -n "${GITHUB_ENV:-}" ]; then
  cat "$E2E_STATE/live.env" >> "$GITHUB_ENV"
fi
log "per la suite live in questa shell: set -a; . $E2E_STATE/live.env; set +a"
