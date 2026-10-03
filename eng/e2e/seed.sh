#!/usr/bin/env bash
# Semina nel database di Sharp-a-File, via SQL, cio' che le API non sanno creare:
#   - un ente e tre chiavi API (read, write, admin): non esiste un comando CLI ne' una API per ente e prime chiavi;
#   - l'anagrafica: una categoria, tre contatti (uno per genere: external, user, group) e un documento SENZA contenuto
#     collegato ai contatti (document_contacts): /contacts e /contact-categories sono in sola lettura, e un documento
#     senza contenuto nasce solo dall'import ARXivar.
#
# Schema (migrazioni EF di Sharp-a-File @8aec8bb, nomi snake_case di EFCore.NamingConventions):
#   tenants (id 'ten_'+ULID, slug UNIQUE, name, status 'active'|'suspended', created_at datetime2(6))      [InitialSchema]
#   api_keys(id 'key_'+ULID, tenant_id, name, key_hash 'sha256:<hex minuscolo>' UNIQUE,
#            scope 'read'|'write'|'admin', status 'active'|'revoked', created_at, last_used_at NULL)      [InitialSchema]
#   contact_categories(tenant_id, id = codice scelto (regola di FolderCode), name, id_arxivar int NULL;
#            PK (tenant_id, id), id_arxivar UNIQUE per ente se non NULL)                                    [Contacts]
#   contacts(id 'con_'+ULID, tenant_id, id_arxivar NULL (UNIQUE per ente), category_id NULL (FK sulla categoria),
#            kind 'external'|'user'|'group', code, name, address, postal_code, city, province, country, email, pec,
#            phone, fax, mobile, vat_number, tax_code, ipa_code, office_code, notes, created_at)               [Contacts]
#   document_contacts(document_id, role 'sender'|'recipient', contact_id, tenant_id; PK (document_id, role, contact_id))
#   documents(id 'doc_'+ULID, tenant_id, folder_id NULL, original_filename, mime_type, sha256 NULL, size_bytes,
#            owner, tag, sender, recipient, metadata (oggetto JSON, default '{}'), created_at;
#            CHECK sha256 IS NOT NULL OR size_bytes = 0)                         [InitialSchema, DocumentsWithoutContent]
#
# Idempotente: le chiavi in chiaro stanno in state/keys.env (0600) e si riusano; se il database e' nuovo si reinseriscono
# le stesse; se state/keys.env e' andato perso le vecchie righe e2e-* vengono revocate e se ne creano di nuove.
# L'anagrafica si riconosce dal codice (contatti, categoria) e da owner + nome file (documento): non si duplica mai.
# I valori che la suite live si aspetta (codici, nomi, numeri ARXivar) sono anche in
# tests/Filemaster.IntegrationTests/Live/LiveSeed.cs: cambiarli in entrambi i posti.
#
# Uso: [E2E_DB=<database>] seed.sh   (richiede state/compose.env e un SQL Server raggiungibile gia' migrato)
set -euo pipefail
# shellcheck source=_common.sh
. "$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/_common.sh"
require_state
need python3

KEYS_FILE="$E2E_STATE/keys.env"
TENANT_SLUG="${E2E_TENANT_SLUG:-e2e}"
DB="${E2E_DB:-sharpafile}"   # database di destinazione (default quello del demone)
TENANT_NAME="Filemaster E2E"

# ULID canonico: 48 bit di millisecondi + 80 bit casuali = 128 bit, codificati in 26 caratteri Crockford maiuscoli
# (alfabeto senza I L O U; il primo carattere e' quindi 0-7). Il server rifiuta qualunque altra forma
# (Ids.IsValid fa il round-trip con Ulid.TryParse): non si possono scegliere 26 caratteri a caso.
gen_ulid() {
  python3 - <<'PY'
import os, time
A = "0123456789ABCDEFGHJKMNPQRSTVWXYZ"
n = (int(time.time() * 1000) << 80) | int.from_bytes(os.urandom(10), "big")
print("".join(A[(n >> (5 * i)) & 31] for i in range(25, -1, -1)))
PY
}

# Chiave API: "saf_" + base64url(32 byte casuali) senza padding (Secrets.NewApiKey).
gen_key() {
  python3 -c 'import os,base64;print("saf_"+base64.urlsafe_b64encode(os.urandom(32)).rstrip(b"=").decode())'
}

# Hash: "sha256:" + hex minuscolo dello SHA-256 della chiave in UTF-8 (Secrets.HashApiKey).
hash_key() {
  python3 -c 'import sys,hashlib;print("sha256:"+hashlib.sha256(sys.argv[1].encode()).hexdigest())' "$1"
}

if [ ! -f "$KEYS_FILE" ]; then
  log "genero ente e chiavi ($KEYS_FILE)"
  {
    echo "E2E_TENANT_ID=ten_$(gen_ulid)"
    for s in READ WRITE ADMIN; do
      echo "E2E_KEY_${s}_ID=key_$(gen_ulid)"
      echo "E2E_KEY_${s}=$(gen_key)"
    done
  } | write_private "$KEYS_FILE"
fi
# shellcheck disable=SC1090
set -a
# shellcheck disable=SC1090 # file di stato scritto qui sopra, a runtime
. "$KEYS_FILE"
set +a

H_READ="$(hash_key "$E2E_KEY_READ")"
H_WRITE="$(hash_key "$E2E_KEY_WRITE")"
H_ADMIN="$(hash_key "$E2E_KEY_ADMIN")"
# Id nuovi a ogni run: si usano solo se la riga corrispondente non c'e' ancora.
C_EXT="con_$(gen_ulid)"
C_USR="con_$(gen_ulid)"
C_GRP="con_$(gen_ulid)"
D_SEED="doc_$(gen_ulid)"

# Tutti i valori interpolati sono generati qui (alfabeto [A-Za-z0-9_:-]) o costanti: nessuna iniezione possibile.
# Il nome del contatto esterno ha una lettera accentata (NCHAR(224) = a con l'accento grave): il file resta ASCII e la
# suite live prova il round-trip di un nome non ASCII.
seed_sql() {
  cat <<EOSQL
SET NOCOUNT ON; SET XACT_ABORT ON; BEGIN TRAN;
IF NOT EXISTS (SELECT 1 FROM tenants WHERE slug = N'$TENANT_SLUG')
  INSERT INTO tenants (id, slug, name, status, created_at)
  VALUES (N'$E2E_TENANT_ID', N'$TENANT_SLUG', N'$TENANT_NAME', N'active', SYSUTCDATETIME());
DECLARE @t nvarchar(32) = (SELECT id FROM tenants WHERE slug = N'$TENANT_SLUG');
UPDATE api_keys SET status = N'revoked'
  WHERE tenant_id = @t AND name IN (N'e2e-read', N'e2e-write', N'e2e-admin')
    AND key_hash NOT IN (N'$H_READ', N'$H_WRITE', N'$H_ADMIN');
IF NOT EXISTS (SELECT 1 FROM api_keys WHERE key_hash = N'$H_READ')
  INSERT INTO api_keys (id, tenant_id, name, key_hash, scope, status, created_at, last_used_at)
  VALUES (N'$E2E_KEY_READ_ID', @t, N'e2e-read', N'$H_READ', N'read', N'active', SYSUTCDATETIME(), NULL);
IF NOT EXISTS (SELECT 1 FROM api_keys WHERE key_hash = N'$H_WRITE')
  INSERT INTO api_keys (id, tenant_id, name, key_hash, scope, status, created_at, last_used_at)
  VALUES (N'$E2E_KEY_WRITE_ID', @t, N'e2e-write', N'$H_WRITE', N'write', N'active', SYSUTCDATETIME(), NULL);
IF NOT EXISTS (SELECT 1 FROM api_keys WHERE key_hash = N'$H_ADMIN')
  INSERT INTO api_keys (id, tenant_id, name, key_hash, scope, status, created_at, last_used_at)
  VALUES (N'$E2E_KEY_ADMIN_ID', @t, N'e2e-admin', N'$H_ADMIN', N'admin', N'active', SYSUTCDATETIME(), NULL);
UPDATE api_keys SET status = N'active' WHERE key_hash IN (N'$H_READ', N'$H_WRITE', N'$H_ADMIN');

IF NOT EXISTS (SELECT 1 FROM contact_categories WHERE tenant_id = @t AND id = N'E2E-FORNITORI')
  INSERT INTO contact_categories (tenant_id, id, name, id_arxivar)
  VALUES (@t, N'E2E-FORNITORI', N'Fornitori E2E', 9001);
IF NOT EXISTS (SELECT 1 FROM contacts WHERE tenant_id = @t AND code = N'E2E-EXT')
  INSERT INTO contacts (id, tenant_id, id_arxivar, category_id, kind, code, name, address, postal_code, city, province,
                        country, email, pec, phone, vat_number, tax_code, notes, created_at)
  VALUES (N'$C_EXT', @t, 9101, N'E2E-FORNITORI', N'external', N'E2E-EXT', N'Fornitore E2E Citt' + NCHAR(224) + N' Srl',
          N'Via Roma 1', N'10100', N'Torino', N'TO', N'IT', N'fornitore.e2e@example.com', N'fornitore.e2e@pec.example.com',
          N'+39 011 0000000', N'01234567890', N'01234567890', N'seminato da eng/e2e/seed.sh', SYSUTCDATETIME());
IF NOT EXISTS (SELECT 1 FROM contacts WHERE tenant_id = @t AND code = N'E2E-USR')
  INSERT INTO contacts (id, tenant_id, id_arxivar, category_id, kind, code, name, email, created_at)
  VALUES (N'$C_USR', @t, 9102, NULL, N'user', N'E2E-USR', N'Utente E2E', N'utente.e2e@example.com', SYSUTCDATETIME());
IF NOT EXISTS (SELECT 1 FROM contacts WHERE tenant_id = @t AND code = N'E2E-GRP')
  INSERT INTO contacts (id, tenant_id, id_arxivar, category_id, kind, code, name, created_at)
  VALUES (N'$C_GRP', @t, NULL, NULL, N'group', N'E2E-GRP', N'Gruppo E2E', SYSUTCDATETIME());
IF NOT EXISTS (SELECT 1 FROM documents WHERE tenant_id = @t AND owner = N'e2e-seed' AND original_filename = N'e2e-seed-senza-contenuto.pdf')
  INSERT INTO documents (id, tenant_id, folder_id, original_filename, mime_type, sha256, size_bytes, owner, tag, sender,
                         recipient, metadata, created_at)
  VALUES (N'$D_SEED', @t, NULL, N'e2e-seed-senza-contenuto.pdf', N'application/pdf', NULL, 0, N'e2e-seed', N'e2e-seed',
          N'Fornitore E2E', N'Utente E2E', N'{"e2e":{"seed":true}}', SYSUTCDATETIME());
DECLARE @d nvarchar(32) = (SELECT id FROM documents WHERE tenant_id = @t AND owner = N'e2e-seed' AND original_filename = N'e2e-seed-senza-contenuto.pdf');
INSERT INTO document_contacts (document_id, role, contact_id, tenant_id)
  SELECT @d, x.role, c.id, @t
  FROM (VALUES (N'sender', N'E2E-EXT'), (N'recipient', N'E2E-USR'), (N'recipient', N'E2E-GRP')) AS x(role, code)
  JOIN contacts c ON c.tenant_id = @t AND c.code = x.code
  WHERE NOT EXISTS (SELECT 1 FROM document_contacts dc WHERE dc.document_id = @d AND dc.role = x.role AND dc.contact_id = c.id);
COMMIT;
SELECT CONCAT(N'tenant=', @t,
  N' keys=', (SELECT COUNT(*) FROM api_keys WHERE tenant_id = @t AND status = N'active'),
  N' categories=', (SELECT COUNT(*) FROM contact_categories WHERE tenant_id = @t),
  N' contacts=', (SELECT COUNT(*) FROM contacts WHERE tenant_id = @t),
  N' seed_document=', @d,
  N' links=', (SELECT COUNT(*) FROM document_contacts WHERE document_id = @d));
EOSQL
}
SQL="$(seed_sql)"

out="$(sqlcmd_run -b -h -1 -W -d "$DB" -Q "$SQL")" || die "seed SQL fallito: $out"
log "seed ok: $(printf '%s' "$out" | tail -n 1)"

# Se l'ente esisteva gia' con un altro id (state perso), riallinea state/keys.env all'id reale.
real_id="$(sqlcmd_run -b -h -1 -W -d "$DB" -Q "SET NOCOUNT ON; SELECT id FROM tenants WHERE slug = N'$TENANT_SLUG'" | tr -d '[:space:]')"
if [ -n "$real_id" ] && [ "$real_id" != "$E2E_TENANT_ID" ]; then
  sed "s/^E2E_TENANT_ID=.*/E2E_TENANT_ID=$real_id/" "$KEYS_FILE" | write_private "$KEYS_FILE"
fi
