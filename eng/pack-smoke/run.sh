#!/usr/bin/env bash
# Smoke dei pacchetti NuGet di Filemaster, come li userebbe un cliente: i 4 .nupkg appena impacchettati si installano da un
# feed locale in applicazioni console usa-e-getta che chiamano davvero un server HTTP finto (piano, job `pack-smoke` di
# .github/workflows/ci.yml; lezione 2: "fatto" si verifica sull'artefatto).
#
# Uso:      eng/pack-smoke/run.sh [--keep] <cartella-pacchetti> <versione>
# Esempio:  eng/pack-smoke/run.sh artifacts 0.0.0-ci.42
#   --keep  non cancella la cartella di lavoro temporanea (ne stampa il percorso): per ispezionare build e cache. Lo stesso
#           con la variabile d'ambiente PACK_SMOKE_KEEP=1.
# Esito:    0 smoke superato; altrimenti l'exit code del passo fallito (1 se e' il consumatore a fallire un controllo);
#           64 uso errato o prerequisito mancante (argomenti, dotnet, cartella, i 4 .nupkg di quella versione).
#
# Cosa fa:
#   1. rende assoluta la cartella dei pacchetti e controlla che ci siano Filemaster, .Domain, .Application e .Infrastructure
#      alla versione data;
#   2. copia i consumatori (eng/pack-smoke/consumers) in una cartella temporanea FUORI dal repo: dentro l'albero si
#      applicherebbero Directory.Build.props/Directory.Packages.props della radice (LangVersion, Nullable, CPM: lezione
#      T0.1). Accanto: un nuget.config con <clear/>, il feed locale (solo per Filemaster e Filemaster.*, grazie al package
#      source mapping: mai da nuget.org) e nuget.org per le dipendenze; NUGET_PACKAGES nella cartella temporanea, cosi'
#      una versione di CI (0.0.0-ci.N) non entra mai nella cache dell'utente; global.json del repo (stesso SDK della CI);
#      le 3 fixture catturate servite dal server finto;
#   3. build del consumatore net8.0+net10.0 (Modern: AddFilemaster + factory) e di quello net48 (Net48: C# 7.3 di default,
#      factory e tipi di input valorizzati con i setter: un `init` o `required` in un tipo di input rompe questo build).
#      net48 si compila ovunque (reference assembly dal pacchetto implicito), ma si esegue solo su Windows;
#   4. controlla in project.assets.json di net48 che le dipendenze con floor 8.0.x si risolvano esattamente al floor
#      dichiarato nel nuspec del pacchetto installato (System.Text.Json, Microsoft.Bcl.AsyncInterfaces,
#      Microsoft.Bcl.TimeProvider, Microsoft.Extensions.Logging.Abstractions, Microsoft.Extensions.Http);
#   5. esegue i consumatori: ognuno avvia il server finto (TcpListener su 127.0.0.1, porta 0), fa GET /tenant,
#      POST /documents (multipart) e GET /documents con filtri, controlla risposte e richieste (X-API-Key, parte file) e
#      che gli assembly caricati vengano dalla cartella lib/ giusta (TargetFrameworkAttribute) alla versione data.
# Alla fine stampa un riepilogo dei passi; la cartella temporanea si cancella sempre all'uscita (salvo --keep).
#
# Portabile: bash 3.2 (macOS) e 5 (Ubuntu, Git Bash su Windows), awk POSIX (BSD awk, mawk, gawk); niente declare -A,
# mapfile, readlink -f, sed -i, grep -P. Su Git Bash i percorsi assoluti consegnati a dotnet passano da `cygpath -w`; gli
# altri sono relativi alla cartella di lavoro.
#
# Controllo statico (0 avvisi): lo stesso shellcheck di eng/*.sh (digest in eng/docker-replay.sh).

set -euo pipefail

PACKAGE_IDS="Filemaster Filemaster.Domain Filemaster.Application Filemaster.Infrastructure"
FIXTURES="03-tenant.json 47-doc-upload.json 71-docs-list-limit1-page1.json"

usage() {
  echo "Uso: eng/pack-smoke/run.sh [--keep] <cartella-pacchetti> <versione>"
}

# Git Bash/MSYS/Cygwin su Windows (dove net48 si puo' eseguire).
is_windows() {
  if [ "${OS:-}" = "Windows_NT" ]; then
    return 0
  fi
  case "$(uname -s 2> /dev/null || echo unknown)" in
    MINGW* | MSYS* | CYGWIN*) return 0 ;;
    *) return 1 ;;
  esac
}

# Percorso assoluto nella forma che capisce dotnet (C:\... su Git Bash; invariato altrove).
native_path() {
  if command -v cygpath > /dev/null 2>&1; then
    cygpath -w "$1"
  else
    printf '%s\n' "$1"
  fi
}

# declared_floor <nuspec> <id>: la versione con cui il nuspec dichiara la dipendenza <id> (prima occorrenza), vuoto se assente.
# NuGet scrive sempre `<dependency id="X" version="Y" ...`. awk e non `grep | head`: con pipefail head darebbe SIGPIPE (lezione T5a).
declared_floor() {
  awk -v id="$2" '
    {
      key = "id=\"" id "\" version=\""
      p = index($0, key)
      if (p > 0) {
        s = substr($0, p + length(key))
        print substr(s, 1, index(s, "\"") - 1)
        exit
      }
    }
  ' "$1"
}

# resolved_version <project.assets.json> <id>: la versione risolta (chiave "Id/versione"), vuoto se il pacchetto non c'e'.
resolved_version() {
  awk -v id="$2" '
    {
      key = "\"" id "/"
      p = index($0, key)
      if (p > 0) {
        s = substr($0, p + length(key))
        print substr(s, 1, index(s, "\"") - 1)
        exit
      }
    }
  ' "$1"
}

# --- argomenti ----------------------------------------------------------------------------------------------------

KEEP=${PACK_SMOKE_KEEP:-0}
while [ "$#" -gt 0 ]; do
  case "$1" in
    --keep) KEEP=1; shift ;;
    -h | --help) usage; exit 0 ;;
    --) shift; break ;;
    -*) echo "ERRORE: opzione sconosciuta: $1" >&2; usage >&2; exit 64 ;;
    *) break ;;
  esac
done
if [ "$#" -ne 2 ]; then
  usage >&2
  exit 64
fi
PKG_ARG=$1
VERSION=$2

case "$VERSION" in
  "" | *[!0-9A-Za-z.+-]*)
    echo "ERRORE: versione non valida: '$VERSION'" >&2
    exit 64
    ;;
esac
if [ ! -d "$PKG_ARG" ]; then
  echo "ERRORE: la cartella dei pacchetti non esiste: $PKG_ARG" >&2
  exit 64
fi
if ! command -v dotnet > /dev/null 2>&1; then
  echo "ERRORE: dotnet non e' nel PATH" >&2
  exit 64
fi

PKG_DIR=$(cd "$PKG_ARG" && pwd -P)
SCRIPT_DIR=$(cd "$(dirname "$0")" && pwd -P)
REPO_ROOT=$(cd "$SCRIPT_DIR/../.." && pwd -P)
CAPTURED="$REPO_ROOT/tests/Filemaster.UnitTests/Wire/Fixtures/captured"

MISSING=""
for id in $PACKAGE_IDS; do
  if [ ! -f "$PKG_DIR/$id.$VERSION.nupkg" ]; then
    MISSING="$MISSING $id.$VERSION.nupkg"
  fi
done
if [ -n "$MISSING" ]; then
  echo "ERRORE: in $PKG_DIR mancano i pacchetti della versione '$VERSION':$MISSING" >&2
  echo "Pacchetti presenti:" >&2
  FOUND=0
  for f in "$PKG_DIR"/*.nupkg; do
    [ -e "$f" ] || continue
    FOUND=1
    echo "  ${f##*/}" >&2
  done
  if [ "$FOUND" -eq 0 ]; then echo "  (nessun .nupkg)" >&2; fi
  exit 64
fi
for f in $FIXTURES; do
  if [ ! -f "$CAPTURED/$f" ]; then
    echo "ERRORE: manca la fixture $CAPTURED/$f" >&2
    exit 64
  fi
done

# --- cartella di lavoro fuori dal repo ----------------------------------------------------------------------------

TMP_BASE=${RUNNER_TEMP:-${TMPDIR:-/tmp}}
if command -v cygpath > /dev/null 2>&1; then
  TMP_BASE=$(cygpath -u "$TMP_BASE")
fi
WORK=$(mktemp -d "${TMP_BASE%/}/filemaster-pack-smoke.XXXXXX")
WORK=$(cd "$WORK" && pwd -P)

# shellcheck disable=SC2329 # chiamata dalla trap EXIT
cleanup() {
  if [ "$KEEP" = "1" ]; then
    echo "Cartella di lavoro conservata (--keep): $WORK"
  elif ! rm -rf "$WORK"; then
    # Mai cambiare l'esito dello smoke per la pulizia (su Windows un file puo' restare bloccato per qualche istante).
    echo "AVVISO: cartella di lavoro non cancellata del tutto: $WORK" >&2
  fi
}
trap cleanup EXIT

case "$WORK/" in
  "$REPO_ROOT"/*)
    echo "ERRORE: la cartella temporanea $WORK e' dentro il repo: le Directory.*.props della radice si applicherebbero" >&2
    exit 64
    ;;
esac

cp -R "$SCRIPT_DIR/consumers/." "$WORK/"
rm -rf "$WORK"/*/bin "$WORK"/*/obj
cp "$REPO_ROOT/global.json" "$WORK/global.json"
mkdir "$WORK/fixtures"
for f in $FIXTURES; do
  cp "$CAPTURED/$f" "$WORK/fixtures/$f"
done

PKG_NATIVE=$(native_path "$PKG_DIR")
case "$PKG_NATIVE" in
  *[\&\<\>\"]*)
    echo "ERRORE: il percorso dei pacchetti contiene caratteri non ammessi in un attributo XML: $PKG_NATIVE" >&2
    exit 64
    ;;
esac
cat > "$WORK/nuget.config" << EOF
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="filemaster-locale" value="$PKG_NATIVE" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" protocolVersion="3" />
  </packageSources>
  <packageSourceMapping>
    <packageSource key="filemaster-locale">
      <package pattern="Filemaster" />
      <package pattern="Filemaster.*" />
    </packageSource>
    <packageSource key="nuget.org">
      <package pattern="*" />
    </packageSource>
  </packageSourceMapping>
</configuration>
EOF

NUGET_PACKAGES=$(native_path "$WORK/nuget-packages")
export NUGET_PACKAGES
export DOTNET_NOLOGO=1 DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1

# global.json e nuget.config si cercano a partire dalla cartella corrente/del progetto: da qui in poi tutto e' relativo a $WORK.
cd "$WORK"

echo "Pack smoke: versione $VERSION"
echo "  pacchetti:          $PKG_DIR"
echo "  cartella di lavoro: $WORK"
echo "  cache NuGet:        $NUGET_PACKAGES"
echo "  SDK:                $(dotnet --version)"

# --- passi --------------------------------------------------------------------------------------------------------

STEP_NO=0
SUMMARY=""

print_summary() {
  printf '\n=== Riepilogo pack smoke %s (%s) ===\n%s' "$VERSION" "$1" "$SUMMARY"
}

# run_step <nome> <comando...>: esegue e cronometra; al primo fallimento stampa il riepilogo ed esce con il suo codice.
run_step() {
  local name=$1 start rc=0
  shift
  STEP_NO=$((STEP_NO + 1))
  printf '\n=== Passo %d: %s\n$ %s\n' "$STEP_NO" "$name" "$*"
  start=$SECONDS
  "$@" || rc=$?
  if [ "$rc" -eq 0 ]; then
    SUMMARY="$SUMMARY$(printf '  OK       %2d. %-58s %4ds' "$STEP_NO" "$name" "$((SECONDS - start))")"$'\n'
    return 0
  fi
  SUMMARY="$SUMMARY$(printf '  FALLITO  %2d. %-58s %4ds (exit %d)' "$STEP_NO" "$name" "$((SECONDS - start))" "$rc")"$'\n'
  print_summary "FALLITO al passo $STEP_NO"
  exit "$rc"
}

skip_step() {
  STEP_NO=$((STEP_NO + 1))
  printf '\n=== Passo %d: %s: SALTATO (%s)\n' "$STEP_NO" "$1" "$2"
  SUMMARY="$SUMMARY$(printf '  SALTATO  %2d. %-58s (%s)' "$STEP_NO" "$1" "$2")"$'\n'
}

# Le dipendenze con floor 8.0.x del grafo net48 si risolvono esattamente al floor dichiarato dal pacchetto installato.
check_net48_floors() {
  local assets="Net48/obj/project.assets.json" lc pkg dep nuspec floor resolved bad=0
  lc=$(printf '%s' "$VERSION" | tr '[:upper:]' '[:lower:]')
  if [ ! -f "$assets" ]; then
    echo "ERRORE: manca $assets (restore di net48 non eseguito?)"
    return 1
  fi
  while read -r pkg dep; do
    [ -n "$pkg" ] || continue
    nuspec="$WORK/nuget-packages/$pkg/$lc/$pkg.nuspec"
    if [ ! -f "$nuspec" ]; then
      echo "ERRORE: manca il nuspec installato $nuspec"
      bad=1
      continue
    fi
    floor=$(declared_floor "$nuspec" "$dep")
    resolved=$(resolved_version "$assets" "$dep")
    case "$floor" in
      8.0.*) ;;
      *) echo "ERRORE: $pkg dichiara $dep '$floor', atteso un floor 8.0.x"; bad=1; continue ;;
    esac
    if [ "$resolved" = "$floor" ]; then
      echo "OK      $dep $resolved (floor di $pkg)"
    else
      echo "ERRORE: $dep risolto a '$resolved', il floor dichiarato da $pkg e' $floor"
      bad=1
    fi
  done << EOF
filemaster.domain System.Text.Json
filemaster.application Microsoft.Bcl.AsyncInterfaces
filemaster.application Microsoft.Bcl.TimeProvider
filemaster.infrastructure Microsoft.Extensions.Logging.Abstractions
filemaster Microsoft.Extensions.Http
EOF
  return "$bad"
}

BUILD_FLAGS="-c Release --disable-build-servers -nologo -p:FilemasterVersion=$VERSION"

# shellcheck disable=SC2086 # BUILD_FLAGS va diviso in argomenti (nessuno contiene spazi: la versione e' validata sopra)
run_step "build consumatore net8.0 + net10.0 (AddFilemaster, factory)" dotnet build Modern/PackSmoke.Modern.csproj $BUILD_FLAGS
# shellcheck disable=SC2086
run_step "build consumatore net48 (C# 7.3, setter)" dotnet build Net48/PackSmoke.Net48.csproj $BUILD_FLAGS
run_step "net48: dipendenze risolte al floor 8.0.x" check_net48_floors
run_step "esecuzione net8.0" dotnet Modern/bin/Release/net8.0/PackSmoke.Modern.dll "$VERSION" fixtures
run_step "esecuzione net10.0" dotnet Modern/bin/Release/net10.0/PackSmoke.Modern.dll "$VERSION" fixtures
if is_windows; then
  run_step "esecuzione net48" ./Net48/bin/Release/net48/PackSmoke.Net48.exe "$VERSION" fixtures
else
  skip_step "esecuzione net48" "non Windows: solo compilato"
fi

print_summary "superato"
