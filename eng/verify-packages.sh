#!/usr/bin/env bash
# Verifica i pacchetti NuGet di Filemaster SULL'ARTEFATTO: lezione 2 di tasks/lessons.md ("fatto" si controlla sul
# .nupkg, non sul csproj). Elenca TUTTI i problemi trovati (non si ferma al primo) ed esce con 1 se ce n'e' almeno uno.
#
# Uso:      eng/verify-packages.sh [--require-commit] <cartella-pacchetti> <versione>
# Esempio:  eng/verify-packages.sh artifacts 0.0.0-ci.0
# Esito:    0 tutto a posto; 1 almeno un problema; 2 uso errato o strumenti mancanti.
#
# --require-commit  pretende anche l'attributo commit="..." nell'elemento <repository> del nuspec. Lo scrive SourceLink
#                   solo se la cartella di build ha un'origin github.com: lo chiedono i job che costruiscono da un
#                   checkout vero (ci.yml, release.yml), non il replay in container (copia senza .git).
#
# Cosa si asserisce (decisioni del piano, sezione "Scelte tecniche"):
#   - 4 ID (Filemaster, .Domain, .Application, .Infrastructure): <id>.<versione>.nupkg e .snupkg, nessun altro
#     .nupkg/.snupkg nella cartella;
#   - nupkg: lib/{netstandard2.0,net8.0,net10.0}/<id>.dll + <id>.xml non vuoti, nessun'altra cartella lib/, nessuna dll
#     "ospite", README.md presente;
#   - nuspec: id, version, license expression Apache-2.0, readme README.md, repository git + URL di GitHub (il commit
#     solo con --require-commit);
#   - dipendenze: un gruppo per ciascuno dei 3 TFM; verso i fratelli Filemaster.* SOLO quelli ammessi dal layering e
#     SEMPRE con range esatto [versione] in OGNI gruppo; verso il resto SOLO quelle della tabella qui sotto (floor 8.0.x
#     di proposito), e mai PolySharp (PrivateAssets=all);
#   - snupkg: nuspec con packageType SymbolsPackage e un .pdb per ciascun TFM.
# Delle dipendenze esterne si asseriscono gli ID e il floor 8.0.x, non la patch esatta: un aggiornamento di patch
# voluto non deve rompere il controllo. Se un pacchetto GUADAGNA di proposito una dipendenza, si aggiorna la tabella
# in expected_external(): e' il punto in cui il piano e' scritto.
#
# Portabile: solo bash (anche 3.2 di macOS), unzip, grep e awk POSIX (BSD awk e mawk); niente dotnet, jq, grep -P,
# readlink -f, sed -i.
#
# Controllo statico: shellcheck 0 avvisi (immagine koalaman/shellcheck, vedi il digest in eng/docker-replay.sh).

set -euo pipefail

PACKAGE_IDS="Filemaster Filemaster.Domain Filemaster.Application Filemaster.Infrastructure"
TFM_DIRS="netstandard2.0 net8.0 net10.0"
REPO_URL="https://github.com/Vinello28/Filemaster"

usage() {
  echo "Uso: eng/verify-packages.sh [--require-commit] <cartella-pacchetti> <versione>"
}

# Nome del TFM nei gruppi di dipendenze del nuspec.
moniker_of() {
  case "$1" in
    netstandard2.0) echo ".NETStandard2.0" ;;
    *) echo "$1" ;;
  esac
}

# Fratelli Filemaster.* che il pacchetto DEVE dichiarare (in ogni gruppo).
required_siblings() {
  case "$1" in
    Filemaster) echo "Filemaster.Domain Filemaster.Application Filemaster.Infrastructure" ;;
    Filemaster.Infrastructure) echo "Filemaster.Application" ;;
    Filemaster.Application) echo "Filemaster.Domain" ;;
    *) echo "" ;;
  esac
}

# Fratelli Filemaster.* che il pacchetto PUO' dichiarare (regola di dipendenza verso l'interno; Domain non dipende da nessuno).
allowed_siblings() {
  case "$1" in
    Filemaster) echo "Filemaster.Domain Filemaster.Application Filemaster.Infrastructure" ;;
    Filemaster.Infrastructure) echo "Filemaster.Application Filemaster.Domain" ;;
    Filemaster.Application) echo "Filemaster.Domain" ;;
    *) echo "" ;;
  esac
}

# Dipendenze NON Filemaster attese, per pacchetto e cartella lib/ (ID, floor 8.0.x).
expected_external() {
  case "$1:$2" in
    Filemaster:*) echo "Microsoft.Extensions.Http" ;;
    Filemaster.Domain:netstandard2.0) echo "System.Text.Json" ;;
    Filemaster.Application:netstandard2.0) echo "Microsoft.Bcl.AsyncInterfaces Microsoft.Bcl.TimeProvider" ;;
    Filemaster.Infrastructure:netstandard2.0) echo "Microsoft.Bcl.AsyncInterfaces Microsoft.Bcl.TimeProvider Microsoft.Extensions.Logging.Abstractions" ;;
    Filemaster.Infrastructure:*) echo "Microsoft.Extensions.Logging.Abstractions" ;;
    *) echo "" ;;
  esac
}

# word_in <parola> <elenco separato da spazi>
word_in() {
  case " $2 " in
    *" $1 "*) return 0 ;;
    *) return 1 ;;
  esac
}

# --- argomenti ----------------------------------------------------------------------------------------------------

REQUIRE_COMMIT=0
while [ "$#" -gt 0 ]; do
  case "$1" in
    --require-commit) REQUIRE_COMMIT=1; shift ;;
    -h | --help) usage; exit 0 ;;
    --) shift; break ;;
    -*) echo "ERRORE: opzione sconosciuta: $1" >&2; usage >&2; exit 2 ;;
    *) break ;;
  esac
done
if [ "$#" -ne 2 ]; then
  usage >&2
  exit 2
fi
PKG_DIR=$1
VERSION=$2

case "$VERSION" in
  "" | *[!0-9A-Za-z.+-]*)
    echo "ERRORE: versione non valida: '$VERSION'" >&2
    exit 2
    ;;
esac
if [ ! -d "$PKG_DIR" ]; then
  echo "ERRORE: la cartella dei pacchetti non esiste: $PKG_DIR" >&2
  exit 2
fi
for tool in unzip grep awk mktemp; do
  if ! command -v "$tool" > /dev/null 2>&1; then
    echo "ERRORE: strumento mancante: $tool" >&2
    exit 2
  fi
done

TMP_ROOT=$(mktemp -d "${TMPDIR:-/tmp}/filemaster-verify.XXXXXX")
trap 'rm -rf "$TMP_ROOT"' EXIT

PROBLEMS=0
CURRENT="cartella"
problem() {
  PROBLEMS=$((PROBLEMS + 1))
  echo "ERRORE [$CURRENT]: $*"
}

# --- lettura del nuspec -------------------------------------------------------------------------------------------

# Trasforma il nuspec in righe di token (parole separate da spazio; "-" = attributo assente):
#   id <testo> | version <testo> | license <type> <testo> | readme <testo> | repository <type> <url> <commit>
#   group <tfm> | dep <tfm> <id> <versione>
# Il file si spezza sul carattere "<", quindi non conta come e' impaginato l'XML (il BOM iniziale finisce in un
# record ignorato). Solo awk POSIX: match a 2 argomenti, substr, index, sub, gsub.
nuspec_tokens() {
  awk '
    function attr(s, name,    pat) {
      pat = " " name "=\"[^\"]*\""
      if (match(s, pat)) {
        return substr(s, RSTART + length(name) + 3, RLENGTH - length(name) - 4)
      }
      return "-"
    }
    BEGIN { RS = "<"; indeps = 0; grp = "" }
    {
      rec = $0
      gsub(/[\r\n\t]+/, " ", rec)
      sub(/ +$/, "", rec)
      if (substr(rec, 1, 1) == "/") {
        if (substr(rec, 1, 6) == "/group") { grp = "" }
        if (substr(rec, 1, 13) == "/dependencies") { indeps = 0 }
        next
      }
      name = rec
      sub("[ />].*$", "", name)
      text = ""
      p = index(rec, ">")
      if (p > 0) { text = substr(rec, p + 1) }
      gsub(/^ +| +$/, "", text)
      selfclosed = (substr(rec, length(rec) - 1) == "/>")
      if (name == "id") { print "id", text }
      else if (name == "version") { print "version", text }
      else if (name == "license") { print "license", attr(rec, "type"), text }
      else if (name == "readme") { print "readme", text }
      else if (name == "repository") { print "repository", attr(rec, "type"), attr(rec, "url"), attr(rec, "commit") }
      else if (name == "dependencies") { indeps = selfclosed ? 0 : 1 }
      else if (name == "group" && indeps) {
        grp = attr(rec, "targetFramework")
        print "group", grp
        if (selfclosed) { grp = "" }
      }
      else if (name == "dependency" && indeps) {
        print "dep", (grp == "" ? "-" : grp), attr(rec, "id"), attr(rec, "version")
      }
    }
  ' "$1"
}

# tok_get <chiave> <file token>: il resto della prima riga con quella chiave (vuoto se assente).
tok_get() {
  awk -v k="$1" '$1 == k { sub(/^[^ ]+ ?/, ""); print; exit }' "$2"
}

# --- controlli per pacchetto --------------------------------------------------------------------------------------

# has_entry <voce>: la voce e' nel nupkg che si sta controllando (elenco in $ENTRIES).
has_entry() {
  grep -Fxq -- "$1" "$ENTRIES"
}

# entry_size <voce>: dimensione non compressa in byte (da `unzip -l`, in $SIZES), vuoto se la voce non c'e'.
entry_size() {
  awk -v n="$1" '$4 == n { print $1; exit }' "$SIZES"
}

# require_entry <voce>: esiste ed e' non vuota.
require_entry() {
  if ! has_entry "$1"; then
    problem "manca la voce $1 nel pacchetto"
  elif [ "$(entry_size "$1")" = "0" ]; then
    problem "la voce $1 e' vuota"
  fi
}

check_dependencies() {
  local id=$1 tok=$2 dir moniker required allowed external deps ids dep ver sib group groups known
  required=$(required_siblings "$id")
  allowed=$(allowed_siblings "$id")

  groups=$(awk '$1 == "group" { print $2 }' "$tok")
  while read -r group; do
    [ -n "$group" ] || continue
    known=0
    for dir in $TFM_DIRS; do
      if [ "$group" = "$(moniker_of "$dir")" ]; then known=1; fi
    done
    if [ "$known" -eq 0 ]; then
      problem "gruppo di dipendenze inatteso nel nuspec: $group"
    fi
  done <<EOF
$groups
EOF
  if grep -q '^dep - ' "$tok"; then
    problem "dipendenza dichiarata fuori da un gruppo di TFM"
  fi

  for dir in $TFM_DIRS; do
    moniker=$(moniker_of "$dir")
    if ! grep -Fxq -- "group $moniker" "$tok"; then
      problem "manca il gruppo di dipendenze $moniker nel nuspec"
      continue
    fi
    external=$(expected_external "$id" "$dir")
    deps=$(awk -v g="$moniker" '$1 == "dep" && $2 == g { print $3, $4 }' "$tok")
    ids=$(awk -v g="$moniker" '$1 == "dep" && $2 == g { printf "%s ", $3 }' "$tok")

    while read -r dep ver; do
      [ -n "$dep" ] || continue
      case "$dep" in
        Filemaster | Filemaster.*)
          if ! word_in "$dep" "$allowed"; then
            problem "[$moniker] dipendenza verso $dep non ammessa dal layering"
          elif [ "$ver" != "[$VERSION]" ]; then
            problem "[$moniker] $dep ha versione '$ver', atteso il range esatto '[$VERSION]'"
          fi
          ;;
        PolySharp)
          problem "[$moniker] PolySharp compare tra le dipendenze (deve essere PrivateAssets=all)"
          ;;
        *)
          if ! word_in "$dep" "$external"; then
            problem "[$moniker] dipendenza inattesa: $dep $ver"
          else
            case "$ver" in
              "["* | "("* | *,*) problem "[$moniker] $dep ha un range invece di un floor semplice: '$ver'" ;;
              8.0.*) ;;
              *) problem "[$moniker] $dep ha floor '$ver', atteso 8.0.x" ;;
            esac
          fi
          ;;
      esac
    done <<EOF
$deps
EOF

    for sib in $required $external; do
      if ! word_in "$sib" "$ids"; then
        problem "[$moniker] manca la dipendenza attesa $sib"
      fi
    done
  done
}

check_nupkg() {
  local id=$1 nupkg=$2 work=$3 dir entry e tfm
  local tok="$work/nuspec.tok" nuspec="$work/$id.nuspec"
  ENTRIES="$work/entries.txt"
  SIZES="$work/sizes.txt"

  if [ ! -f "$nupkg" ]; then
    problem "manca il file ${nupkg##*/}"
    return 0
  fi
  if ! unzip -Z1 "$nupkg" > "$ENTRIES" 2> /dev/null || ! unzip -l "$nupkg" > "$SIZES" 2> /dev/null; then
    problem "${nupkg##*/} non e' uno zip leggibile"
    return 0
  fi

  # Contenuto: 3 TFM, dll + xml di documentazione, README.
  for dir in $TFM_DIRS; do
    require_entry "lib/$dir/$id.dll"
    require_entry "lib/$dir/$id.xml"
  done
  require_entry "README.md"
  while IFS= read -r entry; do
    case "$entry" in
      */) continue ;;
      lib/*)
        tfm=${entry#lib/}
        tfm=${tfm%%/*}
        if ! word_in "$tfm" "$TFM_DIRS"; then
          problem "cartella lib/ inattesa: $entry"
        fi
        ;;
    esac
    case "$entry" in
      lib/*/"$id.dll") ;;
      *.dll) problem "dll inattesa nel pacchetto: $entry" ;;
    esac
  done < "$ENTRIES"

  # Metadati del nuspec.
  if ! has_entry "$id.nuspec"; then
    problem "manca la voce $id.nuspec nel pacchetto"
    return 0
  fi
  unzip -p "$nupkg" "$id.nuspec" > "$nuspec"
  nuspec_tokens "$nuspec" > "$tok"

  e=$(tok_get id "$tok")
  if [ "$e" != "$id" ]; then problem "<id> e' '$e', atteso '$id'"; fi
  e=$(tok_get version "$tok")
  if [ "$e" != "$VERSION" ]; then problem "<version> e' '$e', attesa '$VERSION'"; fi
  e=$(tok_get license "$tok")
  if [ "$e" != "expression Apache-2.0" ]; then problem "<license> e' '$e', atteso 'expression Apache-2.0'"; fi
  e=$(tok_get readme "$tok")
  if [ "$e" != "README.md" ]; then problem "<readme> e' '$e', atteso 'README.md'"; fi

  e=$(tok_get repository "$tok")
  if [ -z "$e" ]; then
    problem "manca l'elemento <repository>"
  else
    case "$e" in
      "git $REPO_URL "*) ;;
      *) problem "<repository> e' '${e% *}', atteso type=git url=$REPO_URL" ;;
    esac
    if [ "$REQUIRE_COMMIT" -eq 1 ] && [ "${e##* }" = "-" ]; then
      problem "<repository> non ha l'attributo commit (richiesto da --require-commit)"
    fi
  fi

  check_dependencies "$id" "$tok"
}

check_snupkg() {
  local id=$1 snupkg=$2 work=$3 dir e
  ENTRIES="$work/sentries.txt"
  SIZES="$work/ssizes.txt"

  if [ ! -f "$snupkg" ]; then
    problem "manca il file ${snupkg##*/}"
    return 0
  fi
  if ! unzip -Z1 "$snupkg" > "$ENTRIES" 2> /dev/null || ! unzip -l "$snupkg" > "$SIZES" 2> /dev/null; then
    problem "${snupkg##*/} non e' uno zip leggibile"
    return 0
  fi
  for dir in $TFM_DIRS; do
    require_entry "lib/$dir/$id.pdb"
  done
  if ! has_entry "$id.nuspec"; then
    problem "${snupkg##*/}: manca la voce $id.nuspec"
    return 0
  fi
  unzip -p "$snupkg" "$id.nuspec" > "$work/s.nuspec"
  nuspec_tokens "$work/s.nuspec" > "$work/s.tok"
  e=$(tok_get id "$work/s.tok")
  if [ "$e" != "$id" ]; then problem "${snupkg##*/}: <id> e' '$e', atteso '$id'"; fi
  e=$(tok_get version "$work/s.tok")
  if [ "$e" != "$VERSION" ]; then problem "${snupkg##*/}: <version> e' '$e', attesa '$VERSION'"; fi
  if ! grep -Fq 'packageType name="SymbolsPackage"' "$work/s.nuspec"; then
    problem "${snupkg##*/}: il nuspec non dichiara packageType SymbolsPackage"
  fi
}

# --- esecuzione ---------------------------------------------------------------------------------------------------

echo "Verifica dei pacchetti in '$PKG_DIR', versione '$VERSION'"

# Nessun .nupkg/.snupkg oltre agli 8 attesi (es. avanzi di una versione precedente).
EXPECTED_FILES=""
for id in $PACKAGE_IDS; do
  EXPECTED_FILES="$EXPECTED_FILES $id.$VERSION.nupkg $id.$VERSION.snupkg"
done
CURRENT="cartella"
for f in "$PKG_DIR"/*.nupkg "$PKG_DIR"/*.snupkg; do
  [ -e "$f" ] || continue
  if ! word_in "${f##*/}" "$EXPECTED_FILES"; then
    problem "file inatteso: ${f##*/}"
  fi
done

ENTRIES=""
SIZES=""
for id in $PACKAGE_IDS; do
  CURRENT=$id
  before=$PROBLEMS
  work="$TMP_ROOT/$id"
  mkdir -p "$work"
  check_nupkg "$id" "$PKG_DIR/$id.$VERSION.nupkg" "$work"
  check_snupkg "$id" "$PKG_DIR/$id.$VERSION.snupkg" "$work"
  if [ "$PROBLEMS" -eq "$before" ]; then
    echo "OK     [$id] nupkg + snupkg"
  else
    echo "FALLITO [$id] $((PROBLEMS - before)) problemi"
  fi
done

if [ "$PROBLEMS" -eq 0 ]; then
  echo "Verifica superata: 4 pacchetti (nupkg + snupkg) alla versione $VERSION."
  exit 0
fi
echo "Verifica FALLITA: $PROBLEMS problemi."
exit 1
