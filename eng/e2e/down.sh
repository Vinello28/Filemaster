#!/usr/bin/env bash
# Ferma e rimuove SOLO cio' che appartiene al progetto compose "filemaster-e2e" (contenitori, volumi, rete).
# Non tocca le immagini ne' .e2e/state (chiavi, segreti). Con --purge cancella anche .e2e/state e .e2e/work.
#
#   down.sh [--purge]
# Si puo' lanciare anche quando run-e2e.sh e' fallito a meta' (o non e' mai partito): rimuove solo cio' che trova.
set -euo pipefail
# shellcheck source=_common.sh
. "$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/_common.sh"
need docker

PURGE=0
case "${1:-}" in
  '') ;;
  --purge) PURGE=1 ;;
  -h|--help) sed -n '2,6p' "${BASH_SOURCE[0]}"; exit 0 ;;
  *) die "argomento sconosciuto: $1" ;;
esac

# Contenitori ausiliari con etichetta propria (istanze extra del server, per la cattura delle fixture): vanno via per
# primi, altrimenti la rete del progetto risulta "in uso".
extra="$(docker ps -aq --filter "label=filemaster.e2e=extra")"
# shellcheck disable=SC2086 # elenco di id da dividere
[ -z "$extra" ] || docker rm -f -v $extra >&2
if [ -f "$E2E_STATE/compose.env" ]; then
  load_state
  dc down -v --remove-orphans >&2 || true
fi
# Rete di sicurezza: tutto cio' che porta l'etichetta del progetto (anche se state/ e' andato perso).
ids="$(docker ps -aq --filter "label=com.docker.compose.project=$E2E_PROJECT")"
# shellcheck disable=SC2086
[ -z "$ids" ] || docker rm -f -v $ids >&2
vols="$(docker volume ls -q --filter "label=com.docker.compose.project=$E2E_PROJECT")"
# shellcheck disable=SC2086
[ -z "$vols" ] || docker volume rm $vols >&2
nets="$(docker network ls -q --filter "label=com.docker.compose.project=$E2E_PROJECT")"
# shellcheck disable=SC2086
[ -z "$nets" ] || docker network rm $nets >&2
log "progetto $E2E_PROJECT rimosso"

if [ "$PURGE" = "1" ]; then
  rm -rf "$E2E_STATE" "$E2E_WORK"
  log "$E2E_STATE e $E2E_WORK cancellati"
fi
