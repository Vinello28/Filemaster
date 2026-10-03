#!/usr/bin/env bash
# Cattura risposte HTTP REALI di Sharp-a-File come fixture "golden" per Filemaster.
#
#   capture-fixtures.sh [--set <nome>]          (default: nome del ref preso da state/compose.env)
#   env: FILEMASTER_E2E_URL, FILEMASTER_E2E_KEY (write), FILEMASTER_E2E_READ_KEY, FILEMASTER_E2E_ADMIN_KEY
#        (se mancano si leggono da .e2e/state/keys.env, scritto da run-e2e.sh/seed.sh)
#
# Per ogni richiesta NN-nome: NN-nome.request (cosa e' stato inviato, chiavi oscurate), .status (riga di stato),
# .headers (intestazioni di risposta), .json | .txt | .bin (corpo). Tutto passa dallo scrubbing: chiavi API
# (saf_...), segreti webhook (whsec_...) ed email diventano segnaposto della stessa lunghezza dove possibile.
# I corpi binari (PDF, ZIP, file casuali) non si toccano. index.tsv elenca tutto.
#
# Il server cambia contratto fra ref (master vs dev: contatti, codici cartella, created_from...): lo script rileva il
# profilo con GET /contact-categories (200 = dev) e usa il corpo giusto per le cartelle; le chiamate a rotte che il
# ref non ha restano catturate (il 404/405 e' un risultato).
# Gira con bash 3.2 (macOS) e con bash 5 (Ubuntu). Richiede curl, jq, python3, unzip.
set -euo pipefail
# shellcheck source=_common.sh
. "$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/_common.sh"
load_state
need curl; need jq; need python3; need unzip

SET=""
while [ $# -gt 0 ]; do
  case "$1" in
    --set) SET="$2"; shift 2 ;;
    -h|--help) sed -n '2,15p' "${BASH_SOURCE[0]}"; exit 0 ;;
    *) die "argomento sconosciuto: $1" ;;
  esac
done
if [ -f "$E2E_STATE/keys.env" ]; then
  set -a
  # shellcheck disable=SC1091 # file di stato scritto a runtime da seed.sh
  . "$E2E_STATE/keys.env"
  set +a
fi
BASE="${FILEMASTER_E2E_URL:-http://127.0.0.1:${E2E_PORT:-18080}}"
K_W="${FILEMASTER_E2E_KEY:-${E2E_KEY_WRITE:-}}"
K_R="${FILEMASTER_E2E_READ_KEY:-${E2E_KEY_READ:-}}"
K_A="${FILEMASTER_E2E_ADMIN_KEY:-${E2E_KEY_ADMIN:-}}"
[ -n "$K_W" ] && [ -n "$K_R" ] && [ -n "$K_A" ] || die "mancano le chiavi (FILEMASTER_E2E_KEY/_READ_KEY/_ADMIN_KEY o state/keys.env)"
[ -n "$SET" ] || SET="${E2E_REF_SHA:-unknown}"
# Le catture caricano e scaricano 5 MiB (random5m.bin): con il limite basso che run-e2e.sh usa per la suite live (4 MiB)
# diventerebbero dei 413 e le fixture direbbero un'altra cosa. Serve un server portato su con un limite largo.
[ "${E2E_MAX_UPLOAD_BYTES:-0}" -ge 8388608 ] || die "limite di upload del server ${E2E_MAX_UPLOAD_BYTES:-?} < 8 MiB: rilancia run-e2e.sh con E2E_MAX_UPLOAD_BYTES=104857600"
FIX="$E2E_DIR/fixtures/$SET"
ASSETS="$E2E_WORK/capture-assets"
rm -rf "$FIX"; mkdir -p "$FIX" "$ASSETS"
: > "$FIX/index.tsv"
N=0
STATUS=""; BODY=""; HDRS=""

# ------------------------------------------------------------------------------------------------ scrubbing ---
scrub() {
  python3 -c '
import re, sys
t = sys.stdin.buffer.read().decode("utf-8", "surrogateescape")
t = re.sub(r"saf_[A-Za-z0-9_-]{43}", lambda m: "saf_" + "X" * 43, t)
t = re.sub(r"whsec_[A-Za-z0-9_-]{43}", lambda m: "whsec_" + "X" * 43, t)
t = re.sub(r"[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}", "user@example.com", t)
sys.stdout.buffer.write(t.encode("utf-8", "surrogateescape"))
'
}

# ------------------------------------------------------------------------------------------------- req NAME KEY METHOD PATH [curl args...] ---
# KEY: w | r | a | none | bad | <valore letterale della chiave>. Dopo la chiamata: $STATUS (codice), $BODY (file grezzo), $HDRS.
# Un -H extra con X-API-Key o Authorization sostituisce quello di KEY.
req() {
  local name="$1" keyspec="$2" method="$3" path="$4"; shift 4
  N=$((N + 1))
  local id; id="$(printf '%02d-%s' "$N" "$name")"
  local key=""
  case "$keyspec" in
    w) key="$K_W" ;; r) key="$K_R" ;; a) key="$K_A" ;;
    none) key="" ;; bad) key="saf_0000000000000000000000000000000000000000000" ;;
    *) key="$keyspec" ;;
  esac
  HDRS="$ASSETS/last.headers"; BODY="$ASSETS/last.body"
  local args=(-sS --max-time 180 -X "$method" -D "$HDRS" -o "$BODY" -w '%{http_code}')
  [ -z "$key" ] || args+=(-H "X-API-Key: $key")
  # HEAD con -X HEAD fa attendere curl un corpo: -I e' la forma giusta.
  if [ "$method" = "HEAD" ]; then args=(-sS --max-time 60 -I -D "$HDRS" -o "$BODY" -w '%{http_code}'); [ -z "$key" ] || args+=(-H "X-API-Key: $key"); fi
  local code rc=0
  code="$(curl "${args[@]}" "$@" "$BASE$path")" || rc=$?
  STATUS="$code"
  [ "$rc" = 0 ] || { STATUS="curl-exit-$rc"; : > "$HDRS"; : > "$BODY"; }

  # .request: metodo, percorso, intestazioni/corpo "di interesse" (le chiavi oscurate dallo scrub; nome della chiave a parte).
  {
    echo "$method $path"
    case "$keyspec" in w) echo "# auth: X-API-Key (scope write)";; r) echo "# auth: X-API-Key (scope read)";; a) echo "# auth: X-API-Key (scope admin)";;
      none) echo "# auth: nessuna";; bad) echo "# auth: X-API-Key non valida";; *) echo "# auth: X-API-Key (chiave creata dal test)";; esac
    local a
    for a in "$@"; do printf '%s\n' "# curl: $a"; done
  } | scrub > "$FIX/$id.request"

  # .status e .headers (CRLF -> LF, ultimo blocco: con redirect/100-continue curl ne scrive piu' di uno).
  tr -d '\r' < "$HDRS" | awk 'BEGIN{b=""} /^HTTP\//{b=$0 "\n"; next} {b=b $0 "\n"} END{printf "%s", b}' > "$ASSETS/last.hblock" || true
  tr -d '\r' < "$HDRS" | awk '/^HTTP\//{l=$0} END{print l}' > "$FIX/$id.status"
  tr -d '\r' < "$HDRS" | awk '/^HTTP\//{h=""; next} NF{h=h $0 "\n"} END{printf "%s", h}' | scrub > "$FIX/$id.headers"

  # corpo: .json (se JSON valido), .txt (testo), .bin (altro). Vuoto = nessun file.
  local ctype size ext=""
  ctype="$(tr -d '\r' < "$HDRS" | awk -F': ' 'tolower($1)=="content-type"{v=$2} END{print v}')"
  size="$(wc -c < "$BODY" | tr -d ' ')"
  if [ "$size" -gt 0 ]; then
    case "$ctype" in
      *json*) if jq -e . "$BODY" >/dev/null 2>&1; then ext=json; else ext=txt; fi ;;
      text/*) ext=txt ;;
      *) ext=bin ;;
    esac
    if [ "$size" -gt 1048576 ]; then
      # corpo > 1 MiB (file casuali da 5 MB, ZIP completi): si conserva solo hash e dimensione, non i byte
      printf '%s  %s bytes\n' "$(sha "$BODY")" "$size" > "$FIX/$id.bin.sha256"
    elif [ "$ext" = bin ]; then cp "$BODY" "$FIX/$id.bin"
    elif [ "$ext" = json ]; then scrub < "$BODY" > "$FIX/$id.json"
    else scrub < "$BODY" > "$FIX/$id.$ext"; fi
  fi
  printf '%s\t%s\t%s\t%s\t%s\t%s\n' "$id" "$method" "$path" "$STATUS" "${ctype:--}" "$size" >> "$FIX/index.tsv"
  log "$id -> $STATUS"
}
jbody() { jq -r "$1" "$BODY"; }                       # campo dal corpo dell'ultima risposta
jfix()  { jq -r "$2" "$FIX/$1.json"; }                # campo da una fixture gia' scritta (NN-nome)
urlenc() { python3 -c 'import sys,urllib.parse;print(urllib.parse.quote(sys.argv[1], safe=""))' "$1"; }
J=(-H 'Content-Type: application/json')

# ------------------------------------------------------------------------------------------------ asset di test ---
python3 - "$ASSETS" <<'PY'
import os, sys
d = sys.argv[1]
def pdf(text):
    objs = []
    objs.append(b"<< /Type /Catalog /Pages 2 0 R >>")
    objs.append(b"<< /Type /Pages /Kids [3 0 R] /Count 1 >>")
    objs.append(b"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 100] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>")
    stream = b"BT /F1 12 Tf 20 50 Td (" + text + b") Tj ET"
    objs.append(b"<< /Length " + str(len(stream)).encode() + b" >>\nstream\n" + stream + b"\nendstream")
    objs.append(b"<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>")
    out = b"%PDF-1.4\n"; offs = []
    for i, o in enumerate(objs, 1):
        offs.append(len(out)); out += str(i).encode() + b" 0 obj\n" + o + b"\nendobj\n"
    x = len(out)
    out += b"xref\n0 " + str(len(objs) + 1).encode() + b"\n0000000000 65535 f \n"
    for o in offs: out += ("%010d 00000 n \n" % o).encode()
    out += b"trailer\n<< /Size " + str(len(objs) + 1).encode() + b" /Root 1 0 R >>\nstartxref\n" + str(x).encode() + b"\n%%EOF\n"
    return out
open(os.path.join(d, "fattura.pdf"), "wb").write(pdf(b"Fattura di prova"))
open(os.path.join(d, "altro.pdf"), "wb").write(pdf(b"Altro documento"))
open(os.path.join(d, "note.txt"), "wb").write("Appunti di prova\n".encode())
open(os.path.join(d, "random5m.bin"), "wb").write(os.urandom(5 * 1024 * 1024))
PY
sha() { python3 -c 'import hashlib,sys;print(hashlib.sha256(open(sys.argv[1],"rb").read()).hexdigest())' "$1"; }

# Corpo multipart scritto a mano (curl -F non sa fare filename*=): mp BOUNDARY FILE_PART_HEADERS FILE_PATH [campo=valore...]
# La parte file e' quella indicata dopo; i campi di testo vengono prima (o dopo con "after:").
mp() {
  python3 - "$@" <<'PY'
import sys
out, boundary, disp, ctype, path = sys.argv[1:6]
fields = sys.argv[6:]
b = boundary.encode(); body = b""
def field(k, v): return b"--" + b + b"\r\nContent-Disposition: form-data; name=\"" + k.encode() + b"\"\r\n\r\n" + v.encode() + b"\r\n"
before = [f for f in fields if not f.startswith("after:")]
after = [f[6:] for f in fields if f.startswith("after:")]
for f in before:
    k, v = f.split("=", 1); body += field(k, v)
if disp != "-":
    h = b"--" + b + b"\r\nContent-Disposition: " + disp.encode("utf-8") + b"\r\n"
    if ctype != "-": h += b"Content-Type: " + ctype.encode() + b"\r\n"
    body += h + b"\r\n" + open(path, "rb").read() + b"\r\n"
for f in after:
    k, v = f.split("=", 1); body += field(k, v)
body += b"--" + b + b"--\r\n"
open(out, "wb").write(body)
PY
}
MPH=(-H 'Content-Type: multipart/form-data; boundary=FMBOUND')

# ======================================================================================================== 0. sonde
req healthz none GET /healthz
req readyz none GET /readyz
req tenant r GET /tenant
req tenant-bearer none GET /tenant -H "Authorization: Bearer $K_R"
req tenant-request-id-echo r GET /tenant -H 'X-Request-ID: abc-123'
req tenant-request-id-bad r GET /tenant -H 'X-Request-ID: bad id/<>;;"x'
req tenant-request-id-long r GET /tenant -H "X-Request-ID: $(python3 -c 'print("a"*100)')"
req tenant-json-ct r GET /tenant -H 'Content-Type: application/json; charset=utf-8'
req contact-categories-probe r GET /contact-categories
PROFILE=master; [ "$STATUS" = 200 ] && PROFILE=dev
log "profilo rilevato: $PROFILE"

req err-404-request-id-echo r GET /documents/doc_abc -H 'X-Request-ID: abc-123'
req err-401-request-id-echo none GET /tenant -H 'X-Request-ID: abc-123'
req err-400-request-id-bad r GET '/documents?limit=0' -H 'X-Request-ID: bad id/<>;;"x'

# ======================================================================================================== 1. errori di autenticazione
req err-401-nokey none GET /tenant
req err-401-badkey bad GET /tenant
req err-401-bearer-bad none GET /tenant -H 'Authorization: Bearer saf_nonvalida'
req err-401-basic-scheme none GET /tenant -H 'Authorization: Basic Zm9vOmJhcg=='
req err-403-read-post-folders r POST /folders "${J[@]}" -d '{"name":"NO"}'
req err-403-read-audit r GET /audit
req err-403-write-audit w GET /audit
req err-403-read-apikeys r GET /api-keys

# ======================================================================================================== 2. cartelle
if [ "$PROFILE" = dev ]; then
  FOLDER_PARENT_BODY='{"id":"FATTURE","name":"Fatture"}'
  req folders-create-parent w POST /folders "${J[@]}" -d "$FOLDER_PARENT_BODY"
  F1="FATTURE"
  req folders-create-child w POST /folders "${J[@]}" -d '{"id":"FATTURE.2026","parent_id":"FATTURE","name":"Fatture 2026"}'
  F2="FATTURE.2026"
else
  FOLDER_PARENT_BODY='{"name":"FATTURE"}'
  req folders-create-parent w POST /folders "${J[@]}" -d "$FOLDER_PARENT_BODY"
  F1="$(jbody .id)"
  req folders-create-child w POST /folders "${J[@]}" -d "{\"name\":\"FATTURE.2026\",\"parent_id\":\"$F1\"}"
  F2="$(jbody .id)"
fi
if [ "$PROFILE" = dev ]; then CH_BODY='{"id":"CHARSET","name":"Charset"}'; else CH_BODY='{"name":"CHARSET"}'; fi
req folders-create-json-charset w POST /folders -H 'Content-Type: application/json; charset=utf-8' -d "$CH_BODY"
CH_ID="$(jbody .id)"
req folders-list r GET /folders
req folders-delete-charset w DELETE "/folders/$CH_ID"
req folders-list-children r GET "/folders?parent_id=$F1"
req folders-list-children-ct-json r GET "/folders?parent_id=$F1" -H 'Content-Type: application/json; charset=utf-8'
req folders-patch-name w PATCH "/folders/$F2" "${J[@]}" -d '{"name":"Fatture 2026 rinominata"}'
req folders-list-after-patch r GET "/folders?parent_id=$F1"
req err-409-folder-duplicate w POST /folders "${J[@]}" -d "$FOLDER_PARENT_BODY"
req err-400-folder-missing-name w POST /folders "${J[@]}" -d '{}'
req err-400-folder-unknown-property w POST /folders "${J[@]}" -d '{"name":"X","colore":"rosso"}'
req err-400-folder-invalid-json w POST /folders "${J[@]}" -d '{"name":'
req err-400-folder-empty-body w POST /folders "${J[@]}"
req err-415-folder-text-plain w POST /folders -H 'Content-Type: text/plain' -d '{"name":"X"}'
req err-415-folder-no-content-type w POST /folders -d '{"name":"X"}' -H 'Content-Type:'
req folders-patch-text-plain w PATCH "/folders/$F2" -H 'Content-Type: text/plain' -d '{"name":"X"}'
if [ "$PROFILE" = dev ]; then
  req err-404-folder-parent-unknown w POST /folders "${J[@]}" -d '{"id":"ORFANA","name":"Orfana","parent_id":"NESSUNA"}'
  req err-400-folder-invalid-code w POST /folders "${J[@]}" -d '{"id":"a b","name":"X"}'
  req err-400-folder-missing-code w POST /folders "${J[@]}" -d '{"name":"X"}'
  req err-400-folder-patch-empty w PATCH "/folders/$F2" "${J[@]}" -d '{}'
  req err-404-folder-patch-unknown w PATCH /folders/NESSUNA "${J[@]}" -d '{"name":"x"}'
  req err-409-folder-patch-code-taken w PATCH "/folders/$F2" "${J[@]}" -d '{"id":"FATTURE"}'
  req folders-patch-code w PATCH "/folders/$F2" "${J[@]}" -d '{"id":"FATTURE.2027","name":"Fatture 2027"}'
  F2="FATTURE.2027"
  req folders-list-after-patch-code r GET "/folders?parent_id=$F1"
else
  req err-404-folder-parent-unknown w POST /folders "${J[@]}" -d '{"name":"Orfana","parent_id":"fld_01ARZ3NDEKTSV4RRFFQ69G5FAV"}'
fi
req err-404-folder-list-parent-malformed r GET "/folders?parent_id=%20zz%2F"

# ======================================================================================================== 3. documenti: upload
META='{"arxivar":{"docnumber":12345,"categoria":"X"}}'
UP=(-F "file=@$ASSETS/fattura.pdf;type=application/pdf;filename=fattura.pdf" -F "folder_id=$F1" -F 'owner=gabriele' -F 'tag=fattura' -F 'sender=Acme Srl' -F 'recipient=Beta Spa' -F "metadata=$META")
req doc-upload w POST /documents "${UP[@]}"
[ "$STATUS" = 201 ] || die "upload di prova fallito (HTTP $STATUS): senza password dell'archivio e' 503 (storage-password -generate); corpo: $(cat "$BODY")"
D1="$(jbody .id)"
req doc-upload-dedup w POST /documents "${UP[@]}"
D2="$(jbody .id)"
req doc-upload-no-metadata w POST /documents -F "file=@$ASSETS/altro.pdf;type=application/pdf;filename=altro.pdf"
D3="$(jbody .id)"
req doc-upload-text w POST /documents -F "file=@$ASSETS/note.txt;type=text/plain;filename=note.txt" -F 'owner=gabriele' -F 'tag=nota'
D4="$(jbody .id)"
req doc-upload-italian-field-aliases w POST /documents -F "file=@$ASSETS/note.txt;type=text/plain;filename=alias.txt" -F 'mittente=Acme' -F 'destinatario=Beta'

# non-ASCII: curl -F manda i byte UTF-8 grezzi in filename="..." (non e' RFC 5987)
cp "$ASSETS/altro.pdf" "$ASSETS/perche.pdf"; printf 'x' >> "$ASSETS/perche.pdf"      # contenuto diverso: niente dedup
NONASCII_NAME=$'perch\303\251 \303\250.pdf'   # "perche' e'.pdf" con e acuta ed e grave, in UTF-8 (il file resta ASCII)
req doc-upload-nonascii-raw-filename w POST /documents -F "file=@$ASSETS/perche.pdf;type=application/pdf;filename=$NONASCII_NAME"
D6="$(jbody .id)"
# filename*= (RFC 5987/6266): cio' che il client C# si propone di mandare
cp "$ASSETS/altro.pdf" "$ASSETS/star.pdf"; printf 'yy' >> "$ASSETS/star.pdf"
mp "$ASSETS/body-star.bin" FMBOUND "form-data; name=\"file\"; filename*=utf-8''perch%C3%A9%20%C3%A8%20star.pdf" application/pdf "$ASSETS/star.pdf" "owner=gabriele"
req doc-upload-filename-star-only w POST /documents "${MPH[@]}" --data-binary "@$ASSETS/body-star.bin"
D7="$(jbody .id)"
cp "$ASSETS/altro.pdf" "$ASSETS/both.pdf"; printf 'zzz' >> "$ASSETS/both.pdf"
mp "$ASSETS/body-both.bin" FMBOUND "form-data; name=\"file\"; filename=\"perche_ e_ both.pdf\"; filename*=utf-8''perch%C3%A9%20%C3%A8%20both.pdf" application/pdf "$ASSETS/both.pdf"
req doc-upload-filename-and-star w POST /documents "${MPH[@]}" --data-binary "@$ASSETS/body-both.bin"
# sniffing: PDF con Content-Type della parte application/octet-stream, e parte senza Content-Type
cp "$ASSETS/altro.pdf" "$ASSETS/sniff1.pdf"; printf 'a1' >> "$ASSETS/sniff1.pdf"
req doc-upload-octet-stream-sniff w POST /documents -F "file=@$ASSETS/sniff1.pdf;type=application/octet-stream;filename=sniff1.pdf"
D9="$(jbody .id)"
cp "$ASSETS/altro.pdf" "$ASSETS/sniff2.pdf"; printf 'a2' >> "$ASSETS/sniff2.pdf"
mp "$ASSETS/body-noct.bin" FMBOUND 'form-data; name="file"; filename="sniff2.pdf"' - "$ASSETS/sniff2.pdf"
req doc-upload-no-part-content-type w POST /documents "${MPH[@]}" --data-binary "@$ASSETS/body-noct.bin"
cp "$ASSETS/altro.pdf" "$ASSETS/liar.pdf"; printf 'a3' >> "$ASSETS/liar.pdf"
req doc-upload-declared-type-wins w POST /documents -F "file=@$ASSETS/liar.pdf;type=text/plain;filename=liar.pdf"
D11="$(jbody .id)"
# file grande (5 MB casuali): hash e download byte-esatto
req doc-upload-5mb w POST /documents -F "file=@$ASSETS/random5m.bin;type=application/octet-stream;filename=random5m.bin" -F 'tag=grande'
D12="$(jbody .id)"
LOCAL_SHA="$(sha "$ASSETS/random5m.bin")"
echo "sha256_locale=$LOCAL_SHA sha256_server=$(jbody .sha256) size_server=$(jbody .size_bytes) -> $([ "$LOCAL_SHA" = "$(jbody .sha256)" ] && echo UGUALI || echo DIVERSI)" > "$FIX/5mb-check.txt"
req doc-content-5mb r GET "/documents/$D12/content"
cmp -s "$ASSETS/random5m.bin" "$BODY" && echo "download 5 MB byte-esatto: SI" >> "$FIX/5mb-check.txt" || echo "download 5 MB byte-esatto: NO" >> "$FIX/5mb-check.txt"

# ======================================================================================================== 4. documenti: errori di upload
req err-400-upload-missing-file w POST /documents -F 'owner=gabriele'
mp "$ASSETS/body-nofn.bin" FMBOUND 'form-data; name="file"' text/plain "$ASSETS/note.txt"
req err-400-upload-file-without-filename w POST /documents "${MPH[@]}" --data-binary "@$ASSETS/body-nofn.bin"
req err-400-upload-not-multipart w POST /documents "${J[@]}" -d '{"a":1}'
req err-400-upload-metadata-not-object w POST /documents -F "file=@$ASSETS/note.txt;type=text/plain;filename=m1.txt" -F 'metadata=[1,2,3]'
req err-400-upload-metadata-invalid-json w POST /documents -F "file=@$ASSETS/note.txt;type=text/plain;filename=m2.txt" -F 'metadata={"a":'
# stesso errore ma con un file di 5 MB e il campo metadata DOPO il file: la validazione scatta a lettura completata
mp "$ASSETS/body-late-meta.bin" FMBOUND 'form-data; name="file"; filename="late.bin"' application/octet-stream "$ASSETS/random5m.bin" 'after:metadata="non-un-oggetto"'
req err-400-upload-metadata-not-object-after-5mb w POST /documents "${MPH[@]}" --data-binary "@$ASSETS/body-late-meta.bin"
req err-404-upload-folder-malformed w POST /documents -F "file=@$ASSETS/note.txt;type=text/plain;filename=f1.txt" -F 'folder_id=doc_abc'
req err-404-upload-folder-unknown w POST /documents -F "file=@$ASSETS/note.txt;type=text/plain;filename=f2.txt" -F 'folder_id=fld_01ARZ3NDEKTSV4RRFFQ69G5FAV'
req err-403-upload-read-key r POST /documents -F "file=@$ASSETS/note.txt;type=text/plain;filename=f3.txt"
req err-401-upload-nokey none POST /documents -F "file=@$ASSETS/note.txt;type=text/plain;filename=f4.txt"

# ======================================================================================================== 5. documenti: lettura, paginazione
req docs-list-default r GET /documents
req docs-list-limit1-page1 r GET '/documents?limit=1'
C1="$(jbody '.next_cursor // empty')"
req docs-list-limit1-page2 r GET "/documents?limit=1&cursor=$(urlenc "$C1")"
# ultima pagina: tutti i documenti con limit=200 non ha next_cursor; poi con limit = totale esatto
TOTAL="$(curl -sS -H "X-API-Key: $K_R" "$BASE/documents?limit=200" | jq '.items | length')"
req docs-list-last-page-exact r GET "/documents?limit=$TOTAL"
req docs-list-limit-total-minus-1 r GET "/documents?limit=$((TOTAL - 1))"
C2="$(jbody '.next_cursor // empty')"
req docs-list-last-page-cursor r GET "/documents?limit=$((TOTAL - 1))&cursor=$(urlenc "$C2")"
req docs-list-limit200 r GET '/documents?limit=200'
req docs-list-filter-metadata r GET "/documents?metadata=$(urlenc '{"arxivar":{"docnumber":12345}}')"
req docs-list-filter-metadata-nomatch r GET "/documents?metadata=$(urlenc '{"arxivar":{"docnumber":99999}}')"
req docs-list-filter-folder r GET "/documents?folder_id=$(urlenc "$F1")"
req docs-list-filter-owner-tag r GET '/documents?owner=gabriele&tag=fattura'
req docs-list-filter-sender r GET '/documents?sender=Acme%20Srl'
req docs-list-filter-sender-alias r GET '/documents?mittente=Acme'
req docs-list-filter-filename r GET '/documents?filename=fattura'
req docs-list-filter-q r GET '/documents?q=fattura'
req docs-list-filter-metadata-query r GET '/documents?metadata_query=X'
req docs-list-unknown-param-ignored r GET '/documents?foo=bar'
req docs-list-created-from-no-offset r GET '/documents?created_from=2026-10-01T00:00:00'
req docs-list-created-from-date r GET '/documents?created_from=2026-01-01&created_to=2026-12-31'
req docs-list-created-from-z r GET '/documents?created_from=2026-01-01T00:00:00Z'
req err-400-limit-0 r GET '/documents?limit=0'
req err-400-limit-201 r GET '/documents?limit=201'
req err-400-limit-abc r GET '/documents?limit=abc'
req err-400-limit-negative r GET '/documents?limit=-1'
req err-400-cursor-bad r GET '/documents?cursor=%21%21%21'
req err-400-cursor-valid-base64-wrong-format r GET "/documents?cursor=$(printf 'hello|world' | base64 | tr '+/' '-_' | tr -d '=')"
req err-400-metadata-filter-not-object r GET "/documents?metadata=$(urlenc '[1]')"
req docs-list-filter-folder-doc-prefix r GET '/documents?folder_id=doc_abc'

req doc-get r GET "/documents/$D1"
req doc-get-no-folder r GET "/documents/$D4"
req doc-get-nonascii-name r GET "/documents/$D6"
req doc-get-star-filename r GET "/documents/$D7"
req doc-get-sniffed-pdf r GET "/documents/$D9"
req doc-get-declared-text r GET "/documents/$D11"
req err-404-doc-malformed r GET /documents/doc_abc
req err-404-doc-unknown-valid-looking r GET /documents/doc_01ARZ3NDEKTSV4RRFFQ69G5FAV
req err-404-doc-lowercase-ulid r GET "/documents/$(echo "$D1" | tr '[:upper:]' '[:lower:]')"
req err-404-doc-wrong-prefix r GET "/documents/fld_$(echo "$D1" | cut -c5-)"

req doc-content r GET "/documents/$D1/content"
LM="$(tr -d '\r' < "$HDRS" | awk -F': ' 'tolower($1)=="last-modified"{print $2}')"
req doc-content-range-0-9 r GET "/documents/$D1/content" -H 'Range: bytes=0-9'
req doc-content-range-suffix r GET "/documents/$D1/content" -H 'Range: bytes=-10'
req doc-content-range-open r GET "/documents/$D1/content" -H 'Range: bytes=5-'
req doc-content-range-unsatisfiable r GET "/documents/$D1/content" -H 'Range: bytes=999999-'
req doc-content-range-multi r GET "/documents/$D1/content" -H 'Range: bytes=0-1,4-5'
req doc-content-if-modified-since r GET "/documents/$D1/content" -H "If-Modified-Since: $LM"
req doc-content-nonascii-name r GET "/documents/$D6/content"
req doc-content-star-name r GET "/documents/$D7/content"
req doc-content-sniffed r GET "/documents/$D9/content"
req doc-content-text r GET "/documents/$D4/content"
req err-405-head-content r HEAD "/documents/$D1/content"
req err-404-content-unknown r GET /documents/doc_01ARZ3NDEKTSV4RRFFQ69G5FAV/content
req doc-preview-pdf r GET "/documents/$D1/preview"
req doc-preview-range r GET "/documents/$D1/preview" -H 'Range: bytes=0-9'
req err-415-preview-text r GET "/documents/$D4/preview"
req err-415-preview-5mb-octet r GET "/documents/$D12/preview"
req err-404-preview-unknown r GET /documents/doc_01ARZ3NDEKTSV4RRFFQ69G5FAV/preview

# ======================================================================================================== 6. verifica, spostamento, cancellazione
req doc-verify r POST "/documents/$D1/verify"
req doc-verify-with-body r POST "/documents/$D1/verify" "${J[@]}" -d '{}'
req err-404-verify-unknown r POST /documents/doc_01ARZ3NDEKTSV4RRFFQ69G5FAV/verify
req docs-bulk-verify r POST /documents/bulk/verify "${J[@]}" -d "{\"document_ids\":[\"$D1\",\"$D3\"]}"
req docs-bulk-verify-unknown-id r POST /documents/bulk/verify "${J[@]}" -d "{\"document_ids\":[\"$D1\",\"doc_01ARZ3NDEKTSV4RRFFQ69G5FAV\"]}"
req docs-bulk-verify-empty r POST /documents/bulk/verify "${J[@]}" -d '{"document_ids":[]}'
req err-400-bulk-verify-unknown-property r POST /documents/bulk/verify "${J[@]}" -d '{"ids":["x"]}'
req docs-bulk-move w POST /documents/bulk/move "${J[@]}" -d "{\"document_ids\":[\"$D3\",\"$D4\"],\"folder_id\":\"$F2\"}"
req docs-bulk-move-to-root w POST /documents/bulk/move "${J[@]}" -d "{\"document_ids\":[\"$D3\"],\"folder_id\":null}"
req doc-get-after-bulk-move r GET "/documents/$D4"
req doc-move-to-folder w PATCH "/documents/$D1/folder" "${J[@]}" -d "{\"folder_id\":\"$F2\"}"
req err-409-folder-delete-not-empty w DELETE "/folders/$F2"
req doc-move-to-null w PATCH "/documents/$D1/folder" "${J[@]}" -d '{"folder_id":null}'
req doc-move-empty-object w PATCH "/documents/$D1/folder" "${J[@]}" -d '{}'
req err-404-move-unknown-doc w PATCH /documents/doc_01ARZ3NDEKTSV4RRFFQ69G5FAV/folder "${J[@]}" -d '{"folder_id":null}'
req err-404-move-unknown-folder w PATCH "/documents/$D1/folder" "${J[@]}" -d '{"folder_id":"fld_01ARZ3NDEKTSV4RRFFQ69G5FAV"}'
req err-400-move-unknown-property w PATCH "/documents/$D1/folder" "${J[@]}" -d '{"folderId":null}'
req err-415-move-text-plain w PATCH "/documents/$D1/folder" -H 'Content-Type: text/plain' -d '{"folder_id":null}'
req doc-delete w DELETE "/documents/$D2"
req err-404-doc-delete-again w DELETE "/documents/$D2"
req doc-get-after-delete r GET "/documents/$D2"
req doc-get-dedup-sibling-still-ok r GET "/documents/$D1/content" -H 'Range: bytes=0-3'
req err-404-doc-delete-malformed w DELETE /documents/doc_abc
req err-403-doc-delete-read-key r DELETE "/documents/$D1"

# ======================================================================================================== 7. contatti (solo dev) e rotte inesistenti
req contacts-list r GET /contacts
req contact-categories-list r GET /contact-categories
req contacts-list-q r GET '/contacts?q=acme'
req err-404-contact-unknown r GET /contacts/con_01ARZ3NDEKTSV4RRFFQ69G5FAV
req err-404-unknown-route r GET /nope
req err-404-unknown-route-nested r GET /documents/doc_01ARZ3NDEKTSV4RRFFQ69G5FAV/nope
req err-404-unknown-route-anon none GET /nope
req err-405-delete-tenant a DELETE /tenant
req err-405-put-documents a PUT /documents
req err-405-post-tenant a POST /tenant
req err-405-head-tenant r HEAD /tenant
req err-405-post-healthz none POST /healthz
req err-405-patch-documents-id w PATCH "/documents/$D1" "${J[@]}" -d '{}'
req err-405-get-verify r GET "/documents/$D1/verify"
req err-405-delete-audit a DELETE /audit

# ======================================================================================================== 8. chiavi API
req apikeys-create a POST /api-keys "${J[@]}" -d '{"name":"capture-key","scopes":["read"]}'
AK_ID="$(jbody .id)"; AK_KEY="$(jbody .key)"
req apikeys-create-multi-scope a POST /api-keys "${J[@]}" -d '{"name":"capture-key-2","scopes":["read","write"]}'
AK2_ID="$(jbody .id)"
req apikeys-list a GET /api-keys
req apikeys-use-new-key "$AK_KEY" GET /tenant
req apikeys-new-key-forbidden-on-write "$AK_KEY" POST /folders "${J[@]}" -d '{"name":"NO"}'
req apikeys-revoke a DELETE "/api-keys/$AK_ID"
req apikeys-revoke-again a DELETE "/api-keys/$AK_ID"
req apikeys-use-revoked-key "$AK_KEY" GET /tenant
req apikeys-revoke-2 a DELETE "/api-keys/$AK2_ID"
req apikeys-list-after-revoke a GET /api-keys
req err-400-apikeys-no-scopes a POST /api-keys "${J[@]}" -d '{"name":"x"}'
req err-400-apikeys-bad-scope a POST /api-keys "${J[@]}" -d '{"name":"x","scopes":["root"]}'
req err-400-apikeys-no-name a POST /api-keys "${J[@]}" -d '{"scopes":["read"]}'
req err-404-apikeys-revoke-malformed a DELETE /api-keys/key_abc
req err-404-apikeys-revoke-unknown a DELETE /api-keys/key_01ARZ3NDEKTSV4RRFFQ69G5FAV

# ======================================================================================================== 9. webhook
req webhooks-create a POST /webhooks "${J[@]}" -d '{"url":"https://example.com/hook","events":["document.uploaded","document.deleted"]}'
WH_ID="$(jbody .id)"
req webhooks-list a GET /webhooks
req webhooks-delete a DELETE "/webhooks/$WH_ID"
req webhooks-delete-again a DELETE "/webhooks/$WH_ID"
req err-400-webhooks-bad-url a POST /webhooks "${J[@]}" -d '{"url":"ftp://example.com/x","events":["document.uploaded"]}'
req err-400-webhooks-bad-event a POST /webhooks "${J[@]}" -d '{"url":"https://example.com/hook","events":["nope"]}'
req err-400-webhooks-no-events a POST /webhooks "${J[@]}" -d '{"url":"https://example.com/hook"}'
req err-403-webhooks-write-key w GET /webhooks

# ======================================================================================================== 10. account
req accounts-create a POST /accounts "${J[@]}" -d '{"email":"capture.user@example.com","password":"password-di-prova-123","role":"user"}'
ACC_ID="$(jbody .id)"
req accounts-list a GET /accounts
req err-409-accounts-duplicate-email a POST /accounts "${J[@]}" -d '{"email":"capture.user@example.com","password":"password-di-prova-123","role":"user"}'
req err-400-accounts-short-password a POST /accounts "${J[@]}" -d '{"email":"short@example.com","password":"corta","role":"user"}'
req err-400-accounts-bad-role a POST /accounts "${J[@]}" -d '{"email":"role@example.com","password":"password-di-prova-123","role":"god"}'
req accounts-delete a DELETE "/accounts/$ACC_ID"
req accounts-list-after-delete a GET /accounts

# ======================================================================================================== 11. audit (due pagine con before)
req audit-limit2-page1 a GET '/audit?limit=2'
BEFORE="$(jq -r '.items[-1].created_at' "$BODY")"
req audit-limit2-page2-before a GET "/audit?limit=2&before=$(urlenc "$BEFORE")"
req audit-default a GET /audit
req err-400-audit-before-bad a GET '/audit?before=ieri'
req err-400-audit-limit-0 a GET '/audit?limit=0'

# ======================================================================================================== 12. export / import ZIP
req bulk-export r GET /documents/bulk
cp "$BODY" "$ASSETS/export.zip"
unzip -l "$ASSETS/export.zip" 2>&1 | sed 's|^Archive:.*|Archive:  export.zip|' > "$FIX/$(printf '%02d' "$N")-bulk-export.unzip-l.txt" || true
mkdir -p "$ASSETS/export-unzipped" && rm -rf "$ASSETS/export-unzipped"/* && unzip -q -o "$ASSETS/export.zip" -d "$ASSETS/export-unzipped" 2>/dev/null || true
[ -f "$ASSETS/export-unzipped/manifest.json" ] && scrub < "$ASSETS/export-unzipped/manifest.json" > "$FIX/$(printf '%02d' "$N")-bulk-export.manifest.json"
req bulk-export-filtered r GET "/documents/bulk?tag=fattura"
unzip -l "$BODY" 2>&1 | sed 's|^Archive:.*|Archive:  export.zip|' > "$FIX/$(printf '%02d' "$N")-bulk-export-filtered.unzip-l.txt" || true
req bulk-export-empty r GET '/documents/bulk?tag=nessuno'
unzip -l "$BODY" 2>&1 | sed 's|^Archive:.*|Archive:  export.zip|' > "$FIX/$(printf '%02d' "$N")-bulk-export-empty.unzip-l.txt" || true
req bulk-export-bad-filter r GET '/documents/bulk?metadata=%5B1%5D'
req bulk-import w POST /documents/bulk -H 'Content-Type: application/zip' --data-binary "@$ASSETS/export.zip"
req bulk-import-again w POST /documents/bulk -H 'Content-Type: application/zip' --data-binary "@$ASSETS/export.zip"
req err-400-bulk-import-not-zip w POST /documents/bulk -H 'Content-Type: application/zip' --data-binary 'questo non e uno zip'
req bulk-import-multipart-body w POST /documents/bulk -F "file=@$ASSETS/export.zip;type=application/zip"
req err-403-bulk-import-read-key r POST /documents/bulk -H 'Content-Type: application/zip' --data-binary "@$ASSETS/export.zip"
req docs-list-after-import r GET '/documents?limit=200'

# ======================================================================================================== 12b. limite di default (50) e paginazione oltre una pagina
LIMDIR="$ASSETS/bulk52"; mkdir -p "$LIMDIR"
for i in $(seq 1 52); do printf 'documento di paginazione numero %s\n' "$i" > "$LIMDIR/p$i.txt"
  curl -sS -o /dev/null -H "X-API-Key: $K_W" -F "file=@$LIMDIR/p$i.txt;type=text/plain;filename=p$i.txt" -F 'tag=bulk52' "$BASE/documents"; done
req docs-list-default-limit-50 r GET '/documents?tag=bulk52'
DL_C="$(jbody '.next_cursor // empty')"
echo "items=$(jbody '.items|length') next_cursor=$([ -n "$DL_C" ] && echo presente || echo assente)" > "$FIX/default-limit-check.txt"
req docs-list-default-limit-50-page2 r GET "/documents?tag=bulk52&cursor=$(urlenc "$DL_C")"
echo "page2 items=$(jbody '.items|length') next_cursor=$(jbody '.next_cursor // "assente"')" >> "$FIX/default-limit-check.txt"
for id in $(curl -sS -H "X-API-Key: $K_R" "$BASE/documents?tag=bulk52&limit=200" | jq -r '.items[].id'); do
  curl -sS -o /dev/null -X DELETE -H "X-API-Key: $K_W" "$BASE/documents/$id"
done

# ======================================================================================================== 13. cartelle: pulizia e cancellazione
# svuota F2 (documenti spostati e reimportati dallo ZIP) senza catturare: serve solo a poter cancellare la cartella
for id in $(curl -sS -H "X-API-Key: $K_R" "$BASE/documents?folder_id=$(urlenc "$F2")&limit=200" | jq -r '.items[].id'); do
  curl -sS -o /dev/null -X DELETE -H "X-API-Key: $K_W" "$BASE/documents/$id"
done
for id in $(curl -sS -H "X-API-Key: $K_R" "$BASE/documents?folder_id=$(urlenc "$F1")&limit=200" | jq -r '.items[].id'); do
  curl -sS -o /dev/null -X DELETE -H "X-API-Key: $K_W" "$BASE/documents/$id"
done
req err-409-folder-delete-parent-with-child w DELETE "/folders/$F1"
req folders-delete-child w DELETE "/folders/$F2"
req folders-delete-parent w DELETE "/folders/$F1"
req folders-list-after-delete r GET /folders
req err-404-folder-delete-unknown w DELETE /folders/fld_01ARZ3NDEKTSV4RRFFQ69G5FAV
req err-404-folder-delete-malformed w DELETE /folders/zz

# ======================================================================================================== 14. timestamp con 0/2/4 decimali (righe seminate via SQL)
# Il server taglia gli zeri finali (System.Text.Json): per avere fixture reali con 0, 2 e 4 cifre si inseriscono, via SQL,
# chiavi revocate con created_at/last_used_at controllati, si elencano con GET /api-keys e si cancellano.
if [ "${E2E_CAPTURE_TS:-1}" = 1 ]; then
  th() { python3 -c 'import os;print("sha256:"+os.urandom(32).hex())'; }
  TSQL="SET NOCOUNT ON;
DECLARE @t nvarchar(32) = (SELECT id FROM tenants WHERE slug = N'${E2E_TENANT_SLUG:-e2e}');
INSERT INTO api_keys (id, tenant_id, name, key_hash, scope, status, created_at, last_used_at) VALUES
 (N'key_00000000000000000000000TS0', @t, N'e2e-ts-0', N'$(th)', N'read', N'revoked', '2026-01-02T03:04:05', NULL),
 (N'key_00000000000000000000000TS2', @t, N'e2e-ts-2', N'$(th)', N'read', N'revoked', '2026-01-02T03:04:05.120000', '2026-02-03T04:05:06.500000'),
 (N'key_00000000000000000000000TS4', @t, N'e2e-ts-4', N'$(th)', N'read', N'revoked', '2026-01-02T03:04:05.123400', '2026-02-03T04:05:06.000100');"
  if load_secrets 2>/dev/null && sqlcmd_run -b -d "${E2E_DB:-sharpafile}" -Q "$TSQL" >/dev/null 2>&1; then
    req apikeys-list-timestamps a GET /api-keys
    sqlcmd_run -b -d "${E2E_DB:-sharpafile}" -Q "DELETE FROM api_keys WHERE name LIKE N'e2e-ts-%'" >/dev/null 2>&1 || true
  else
    log "timestamp seminati: sqlcmd non disponibile, salto"
  fi
fi

# ======================================================================================================== 15. istanze extra: 413, 503 (archivio non configurato), /readyz 503
if [ "${E2E_CAPTURE_EXTRA:-1}" = 1 ] && [ -n "${E2E_APP_IMAGE:-}" ] && docker image inspect "$E2E_APP_IMAGE" >/dev/null 2>&1; then
  NET="${E2E_PROJECT}_default"
  conn() { echo "Server=mssql,1433;Database=$1;User Id=sa;Password=$MSSQL_SA_PASSWORD;TrustServerCertificate=True"; }
  extra_app() { # nome porta database [-e VAR=VAL ...]
    local name="$1" port="$2" db="$3"; shift 3
    docker rm -f -v "$name" >/dev/null 2>&1 || true
    docker run -d --name "$name" --label filemaster.e2e=extra --network "$NET" -p "127.0.0.1:$port:8080" \
      -e SHARPAFILE_DB_CONNECTION_STRING="$(conn "$db")" -e SHARPAFILE_SESSION_SECRET="$SHARPAFILE_SESSION_SECRET" \
      -e SHARPAFILE_STORAGE_MASTER_KEY="$SHARPAFILE_STORAGE_MASTER_KEY" -e SHARPAFILE_ENV=dev "$@" "$E2E_APP_IMAGE" run >/dev/null
    local i; for i in $(seq 1 60); do curl -fsS -o /dev/null "http://127.0.0.1:$port/healthz" 2>/dev/null && return 0; sleep 1; done
    die "istanza extra $name non risponde"
  }
  MAIN_BASE="$BASE"

  # 413: limite di upload 1 KiB (il corpo massimo di Kestrel e' limite + 1 MiB)
  extra_app fm-e2e-extra-small 18081 sharpafile -e SHARPAFILE_MAX_UPLOAD_SIZE_BYTES=1024 -e SHARPAFILE_MAX_BULK_IMPORT_SIZE_BYTES=1024
  BASE="http://127.0.0.1:18081"
  head -c 2000 /dev/urandom > "$ASSETS/rnd2k.bin"
  req err-413-upload-over-limit-small w POST /documents -F "file=@$ASSETS/rnd2k.bin;type=application/octet-stream;filename=rnd2k.bin"
  req err-413-upload-over-limit-5mb w POST /documents -F "file=@$ASSETS/random5m.bin;type=application/octet-stream;filename=random5m.bin"
  req err-413-bulk-import-over-limit w POST /documents/bulk -H 'Content-Type: application/zip' --data-binary "@$ASSETS/export.zip"
  docker rm -f -v fm-e2e-extra-small >/dev/null 2>&1 || true

  # 503 storage-not-configured: database nuovo, migrato e seminato, senza storage-password
  docker run --rm --network "$NET" -e SHARPAFILE_DB_CONNECTION_STRING="$(conn sharpafile_nostorage)" "$E2E_APP_IMAGE" migrate >/dev/null
  E2E_DB=sharpafile_nostorage "$E2E_HERE/seed.sh"
  extra_app fm-e2e-extra-nostorage 18082 sharpafile_nostorage
  BASE="http://127.0.0.1:18082"
  req err-503-upload-storage-not-configured w POST /documents -F "file=@$ASSETS/note.txt;type=text/plain;filename=note.txt"
  req tenant-no-storage r GET /tenant
  docker rm -f -v fm-e2e-extra-nostorage >/dev/null 2>&1 || true
  BASE="$MAIN_BASE"

  # /readyz 503: si ferma SQL Server per qualche secondo (gli altri passi sono finiti)
  dc stop mssql >/dev/null 2>&1 || true
  req readyz-503-db-down none GET /readyz
  dc start mssql >/dev/null 2>&1 || true
  for i in $(seq 1 60); do sqlcmd_run -b -Q 'SELECT 1' >/dev/null 2>&1 && break; sleep 2; done
  sqlcmd_run -b -Q "IF DB_ID('sharpafile_nostorage') IS NOT NULL BEGIN ALTER DATABASE sharpafile_nostorage SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE sharpafile_nostorage; END" >/dev/null 2>&1 || true
else
  log "istanze extra saltate (E2E_CAPTURE_EXTRA=0 o immagine del server assente)"
fi

# ======================================================================================================== autocontrollo
# Convenzione: "NN-err-CCC-..." garantisce lo stato CCC (se il ref lo produce); gli altri nomi con stato non 2xx/3xx sono
# elencati come INFO (dipendono dal ref, o dimostrano proprio un errore: es. contacts-list = 404 su master).
awk -F'\t' '{ id=$1; st=$4; if (match(id, /^[0-9]+-err-[0-9][0-9][0-9]-/)) { split(id, a, "-"); if (st != a[3]) printf "MISMATCH %s: il nome dice %s, stato %s\n", id, a[3], st } else if (st !~ /^[23]/) printf "INFO %s: stato %s\n", id, st }' "$FIX/index.tsv" > "$FIX/selfcheck.txt"
log "autocontrollo nome/stato: $(grep -c '^MISMATCH' "$FIX/selfcheck.txt" || true) MISMATCH, $(grep -c '^INFO' "$FIX/selfcheck.txt" || true) INFO (vedi selfcheck.txt)"
grep '^MISMATCH' "$FIX/selfcheck.txt" >&2 || true

echo "$PROFILE" > "$FIX/PROFILE"
echo "${E2E_REF_SHA:-unknown}" > "$FIX/SERVER_SHA"
log "fixture: $(wc -l < "$FIX/index.tsv" | tr -d ' ') richieste -> $FIX"
