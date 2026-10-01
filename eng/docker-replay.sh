#!/usr/bin/env bash
# Replay autorevole del job `build` di .github/workflows/ci.yml in un container Linux, da una copia PULITA dell'albero
# (lezione 5 di tasks/lessons.md: un workflow non si verifica rileggendolo ne' provandolo solo sul Mac).
#
# Uso:      eng/docker-replay.sh [--src <cartella>] [--keep]
#   --src   cartella da replicare (default: la radice di questo repository, ricavata dalla posizione dello script e non
#           dalla cartella corrente). L'elenco dei file e' `git ls-files -co --exclude-standard` (tracciati + non tracciati ma
#           non ignorati; il repo puo' non avere commit). Se la cartella NON e' la radice di un repository git (per es. una
#           copia senza .git), l'elenco si ottiene con un git dir temporaneo + --work-tree: stessi .gitignore, nessuna
#           scrittura nella cartella e nessun repository genitore che si infila.
#   --keep  non cancella la copia temporanea dei sorgenti (ne stampa il percorso): utile per ispezionare cosa e' stato provato.
# Esito:    0 tutti i passi superati; altrimenti l'exit code del passo fallito (es. 2 se dotnet format trova differenze);
#           64 se l'uso e' errato o manca un prerequisito (docker, git, --src, immagine non costruibile): non confondibile
#           con un passo.
#
# Cosa fa, nello stesso ordine di ci.yml:
#   copia pulita -> verifica ambiente (SDK da global.json, runtime 8.0) -> restore -> dotnet format --verify-no-changes
#   (+ 2o passaggio con TargetFrameworks=net10.0) -> build Release -> test UnitTests + IntegrationTests su net8.0 e poi su
#   net10.0 con copertura -> `dotnet pack -p:Version=0.0.0-ci.0 -o artifacts` -> bash eng/verify-packages.sh artifacts 0.0.0-ci.0
# I passi rispecchiano il job `build` di ci.yml cosi' com'era il 2026-10-01: se ci.yml cambia, va riallineato anche questo.
# Non scrive nulla nel repository: i sorgenti vanno in una cartella temporanea, montata in SOLA LETTURA nel container, che
# se ne fa una copia locale prima di compilare (bin/obj/artifacts restano nel container, che e' --rm). La cache NuGet e'
# quella del container (nessun mount dalla macchina ospite): ogni replay riscarica i pacchetti, come un runner pulito.
#
# Immagini pinnate per DIGEST dell'indice multi-arch, quindi valgono sia per arm64 sia per amd64. I digest NON si scrivono a
# mano: si leggono (e si sostituiscono con un comando) cosi', poi si rifa' il replay:
#   docker buildx imagetools inspect mcr.microsoft.com/dotnet/sdk:10.0     | awk '/^Digest:/ {print $2; exit}'
#   docker buildx imagetools inspect mcr.microsoft.com/dotnet/runtime:8.0  | awk '/^Digest:/ {print $2; exit}'
# Letti il 2026-10-01 (sdk:10.0 = SDK 10.0.401 su Ubuntu 24.04; global.json pinna 10.0.400 con rollForward latestFeature).
SDK_IMAGE="mcr.microsoft.com/dotnet/sdk:10.0@sha256:35d40304542c8689331f8cab17c65926cdf48fe711e289321d71924b230a7d29"
RUNTIME8_IMAGE="mcr.microsoft.com/dotnet/runtime:8.0@sha256:37466ea190f696105c1c3ae67c15e32d4e199face9a0b2ad5b9a37c464db8f30"
#
# Perche' il runtime 8.0 arriva da un COPY --from e non da dotnet-install.sh: l'immagine sdk:10.0 non ha .NET 8 e servono i
# test su net8.0. Con COPY --from=runtime:8.0@sha256 l'input e' pinnato per digest e non servono rete ne' script esterni a
# build time; dotnet-install.sh scaricherebbe uno script non pinnato e "l'8.0 piu' recente di oggi". Unico input NON pinnato:
# il layer `apt-get install unzip` (l'immagine sdk non ha unzip, che serve a eng/verify-packages.sh; i runner Ubuntu di
# GitHub lo hanno gia').
#
# Controllo statico (0 avvisi): docker run --rm -v "$PWD:/mnt" -w /mnt koalaman/shellcheck@sha256:bb596a0d169b85ddd81d8b6d3a2ff6d5baf5fca10b97f575ebc647c3dff62b3d eng/*.sh
# (digest dell'indice di koalaman/shellcheck:stable, letto il 2026-10-01 con lo stesso comando imagetools inspect).
#
# Differenze note rispetto a ci.yml su GitHub: il replay gira su linux/arm64 se l'host e' un Mac Apple Silicon (CI: amd64);
# il runtime 8.0.x e' quello dell'immagine pinnata (setup-dotnet 8.0.x prende l'ultimo); la copia non ha .git; gira come
# root; non replica i job windows, pack-smoke, vulnerabilita' ne' upload degli artefatti.

set -euo pipefail

CI_VERSION="0.0.0-ci.0"

usage() {
  echo "Uso: eng/docker-replay.sh [--src <cartella>] [--keep]"
}

# --- modalita' interna: gira DENTRO il container ------------------------------------------------------------------

STEP_NO=0
SUMMARY=""
TOTAL_START=0

print_summary() {
  printf '\n=== Riepilogo del replay (%s) ===\n%s' "$1" "$SUMMARY"
  printf 'Tempo totale nel container: %ds\n' "$((SECONDS - TOTAL_START))"
}

# run_step <nome> <comando...>: esegue, cronometra, e al primo fallimento stampa il riepilogo ed esce con il suo codice.
run_step() {
  local name=$1 start rc=0 secs
  shift
  STEP_NO=$((STEP_NO + 1))
  printf '\n=== Passo %d: %s\n$ %s\n' "$STEP_NO" "$name" "$*"
  start=$SECONDS
  "$@" || rc=$?
  secs=$((SECONDS - start))
  if [ "$rc" -eq 0 ]; then
    SUMMARY="$SUMMARY$(printf '  OK       %2d. %-66s %5ds' "$STEP_NO" "$name" "$secs")"$'\n'
    return 0
  fi
  SUMMARY="$SUMMARY$(printf '  FALLITO  %2d. %-66s %5ds (exit %d)' "$STEP_NO" "$name" "$secs" "$rc")"$'\n'
  print_summary "FALLITO al passo $STEP_NO: $name"
  exit "$rc"
}

# shellcheck disable=SC2329 # invocata indirettamente da run_step ("$@")
copy_tree() {
  mkdir -p /work
  cp -a /src/. /work/
}

# shellcheck disable=SC2329 # invocata indirettamente da run_step ("$@")
check_environment() {
  local sdk runtimes
  sdk=$(dotnet --version)
  echo "SDK risolto da global.json: $sdk"
  runtimes=$(dotnet --list-runtimes)
  echo "$runtimes"
  case "$runtimes" in
    *"Microsoft.NETCore.App 8.0."*) ;;
    *) echo "ERRORE: manca il runtime Microsoft.NETCore.App 8.0.x (servono i test net8.0)"; return 1 ;;
  esac
  case "$runtimes" in
    *"Microsoft.NETCore.App 10.0."*) ;;
    *) echo "ERRORE: manca il runtime Microsoft.NETCore.App 10.0.x"; return 1 ;;
  esac
}

# Come ci.yml ("Test net8.0" e "Test net10.0 con copertura"): entrambi i progetti di test, ogni TFM un passo solo.
# Dentro le funzioni invocate da run_step set -e non agisce: il fallimento si propaga con `|| return`.
# shellcheck disable=SC2329 # invocata indirettamente da run_step ("$@")
test_net8() {
  local p
  for p in UnitTests IntegrationTests; do
    dotnet test --project "tests/Filemaster.$p" -c Release -f net8.0 --no-build || return $?
  done
}

# shellcheck disable=SC2329 # invocata indirettamente da run_step ("$@")
test_net10_coverage() {
  local p
  for p in UnitTests IntegrationTests; do
    dotnet test --project "tests/Filemaster.$p" -c Release -f net10.0 --no-build \
      --coverage --coverage-output-format cobertura --coverage-output "$p.cobertura.xml" || return $?
  done
}

# Come ci.yml: `bash eng/verify-packages.sh` della copia. Solo se la copia non lo ha (per es. uno scheletro precedente a
# eng/) ripiega su quello che accompagna questo script, dichiarandolo.
# shellcheck disable=SC2329 # invocata indirettamente da run_step ("$@")
verify_packages() {
  local verifier=eng/verify-packages.sh
  if [ ! -e "$verifier" ]; then
    echo "NOTA: la copia non ha $verifier: uso quello che accompagna lo script di replay"
    verifier=/replay/verify-packages.sh
  fi
  bash "$verifier" artifacts "$CI_VERSION"
}

run_inside() {
  TOTAL_START=$SECONDS
  # Come i runner di GitHub, che impostano CI=true per ogni passo (lo passa il chiamante con -e CI=true).
  run_step "copia dell'albero in /work" copy_tree
  cd /work
  run_step "ambiente: SDK da global.json e runtime 8.0" check_environment
  run_step "dotnet restore" dotnet restore
  run_step "dotnet format --verify-no-changes (passaggio 1)" dotnet format --verify-no-changes --no-restore
  run_step "dotnet format --verify-no-changes (TargetFrameworks=net10.0)" env TargetFrameworks=net10.0 dotnet format --verify-no-changes --no-restore
  run_step "dotnet build -c Release" dotnet build -c Release --no-restore
  run_step "test net8.0 (UnitTests, IntegrationTests)" test_net8
  run_step "test net10.0 con copertura (UnitTests, IntegrationTests)" test_net10_coverage
  run_step "dotnet pack (Version=$CI_VERSION)" dotnet pack -c Release --no-restore "-p:Version=$CI_VERSION" -o artifacts
  run_step "bash eng/verify-packages.sh artifacts $CI_VERSION" verify_packages
  echo
  ls -l artifacts
  print_summary "SUPERATO"
}

if [ "${1:-}" = "--inside" ]; then
  run_inside
  exit 0
fi

# --- modalita' normale: gira sull'host ----------------------------------------------------------------------------

SCRIPT_SOURCE=${BASH_SOURCE[0]}
SCRIPT_DIR=$(cd "$(dirname "$SCRIPT_SOURCE")" && pwd -P)
SCRIPT_PATH="$SCRIPT_DIR/$(basename "$SCRIPT_SOURCE")"
SRC=$(cd "$SCRIPT_DIR/.." && pwd -P)
KEEP=0

while [ "$#" -gt 0 ]; do
  case "$1" in
    --src)
      if [ "$#" -lt 2 ]; then
        echo "ERRORE: --src richiede una cartella" >&2
        exit 64
      fi
      if [ ! -d "$2" ]; then
        echo "ERRORE: la cartella --src non esiste: $2" >&2
        exit 64
      fi
      SRC=$(cd "$2" && pwd -P)
      shift 2
      ;;
    --keep) KEEP=1; shift ;;
    -h | --help) usage; exit 0 ;;
    *) echo "ERRORE: argomento sconosciuto: $1" >&2; usage >&2; exit 64 ;;
  esac
done

for tool in docker git; do
  if ! command -v "$tool" > /dev/null 2>&1; then
    echo "ERRORE: strumento mancante: $tool" >&2
    exit 64
  fi
done
if ! docker info > /dev/null 2>&1; then
  echo "ERRORE: il demone Docker non risponde (docker info fallisce)" >&2
  exit 64
fi
VERIFIER="$SCRIPT_DIR/verify-packages.sh"
if [ ! -f "$VERIFIER" ]; then
  echo "ERRORE: manca $VERIFIER (accanto a questo script)" >&2
  exit 64
fi

WORK=$(mktemp -d "${TMPDIR:-/tmp}/filemaster-replay.XXXXXX")
TREE="$WORK/tree"
CTX="$WORK/context"
mkdir -p "$TREE" "$CTX"
# shellcheck disable=SC2329 # invocata dal trap EXIT
cleanup() {
  if [ "$KEEP" -eq 1 ]; then
    echo "Copia temporanea conservata (--keep): $TREE"
  else
    rm -rf "$WORK"
  fi
}
trap cleanup EXIT
trap 'exit 130' INT
trap 'exit 143' TERM

# Elenco dei file (separati da NUL). Con un repository vero si usa il suo indice e i suoi esclusi; altrimenti un git dir
# temporaneo vuoto con --work-tree=$SRC (git ls-files non scrive nel work tree).
TOPLEVEL=$(git -C "$SRC" rev-parse --show-toplevel 2> /dev/null || true)
if [ -n "$TOPLEVEL" ] && [ "$(cd "$TOPLEVEL" && pwd -P)" = "$SRC" ]; then
  GIT_ARGS=(-C "$SRC")
else
  echo "NOTA: $SRC non e' la radice di un repository git: elenco i file con un git dir temporaneo (solo .gitignore, niente indice)"
  git init -q "$WORK/gitdir"
  GIT_ARGS=(--git-dir="$WORK/gitdir/.git" --work-tree="$SRC")
fi

# Copia pulita: file tracciati + non tracciati-non-ignorati, senza bin/obj/artifacts/.scratch/.git/TestResults (qualunque
# profondita') e senza voci d'indice cancellate dal disco (`-c` le elenca ancora).
echo "Copia pulita di $SRC"
COUNT=0
while IFS= read -r -d '' f; do
  case "/$f/" in
    */bin/* | */obj/* | */artifacts/* | */.scratch/* | */.git/* | */TestResults/*) continue ;;
  esac
  if [ ! -e "$SRC/$f" ] && [ ! -L "$SRC/$f" ]; then
    continue
  fi
  mkdir -p "$TREE/$(dirname "$f")"
  cp -pP "$SRC/$f" "$TREE/$f"
  COUNT=$((COUNT + 1))
done < <(git "${GIT_ARGS[@]}" ls-files -co --exclude-standard -z)
if [ "$COUNT" -eq 0 ]; then
  echo "ERRORE: nessun file da copiare da $SRC" >&2
  exit 64
fi
echo "  $COUNT file in $TREE"

# Immagine di replay: sdk:10.0 + runtime 8.0 + unzip, tutto da Dockerfile su stdin (nessun file nel repository).
SDK_HEX=${SDK_IMAGE##*sha256:}
RT8_HEX=${RUNTIME8_IMAGE##*sha256:}
IMAGE_TAG="filemaster-replay:${SDK_HEX:0:12}-${RT8_HEX:0:12}"
PREP_START=$SECONDS
echo "Immagine di replay $IMAGE_TAG (sdk e runtime 8.0 pinnati per digest)"
BUILD_RC=0
docker build --progress=plain -t "$IMAGE_TAG" -f - "$CTX" << DOCKERFILE || BUILD_RC=$?
FROM $RUNTIME8_IMAGE AS runtime8
FROM $SDK_IMAGE
COPY --from=runtime8 /usr/share/dotnet/shared/Microsoft.NETCore.App/ /usr/share/dotnet/shared/Microsoft.NETCore.App/
RUN apt-get update && DEBIAN_FRONTEND=noninteractive apt-get install -y --no-install-recommends unzip && rm -rf /var/lib/apt/lists/*
DOCKERFILE
if [ "$BUILD_RC" -ne 0 ]; then
  echo "ERRORE: costruzione dell'immagine di replay fallita (docker build exit $BUILD_RC)" >&2
  exit 64
fi
PREP_SECS=$((SECONDS - PREP_START))

RUN_START=$SECONDS
RC=0
docker run --rm --init \
  -e CI=true \
  -e DOTNET_CLI_TELEMETRY_OPTOUT=1 \
  -e DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1 \
  -v "$TREE:/src:ro" \
  -v "$SCRIPT_PATH:/replay/docker-replay.sh:ro" \
  -v "$VERIFIER:/replay/verify-packages.sh:ro" \
  "$IMAGE_TAG" bash /replay/docker-replay.sh --inside || RC=$?
RUN_SECS=$((SECONDS - RUN_START))

echo
echo "Preparazione dell'immagine: ${PREP_SECS}s; esecuzione nel container: ${RUN_SECS}s."
if [ "$RC" -eq 0 ]; then
  echo "REPLAY SUPERATO"
else
  echo "REPLAY FALLITO (exit $RC)"
fi
exit "$RC"
