# Harness e2e: Filemaster contro un Sharp-a-File vero

Questi script portano su un Sharp-a-File **reale** (immagine costruita dal `Dockerfile` di un suo clone, con SQL Server),
lo preparano e stampano le variabili che accendono la suite live di Filemaster
(`tests/Filemaster.IntegrationTests/Live`, trait `Category=Live`). In CI li usa `.github/workflows/e2e.yml` (a mano e ogni
notte, mai sulle pull request).

| Script | Cosa fa |
| --- | --- |
| `run-e2e.sh` | `git archive` del ref in `.e2e/work/` -> `docker compose build` -> SQL Server -> `migrate` -> `bootstrap-admin` -> `storage-password -generate` -> `seed.sh` -> `run` -> attesa di `/healthz`, `/readyz` e `GET /tenant`. Idempotente. |
| `seed.sh` | Via SQL, cio' che le API non sanno creare: ente `e2e`, tre chiavi (read, write, admin), una categoria di contatti, tre contatti (external, user, group) e un documento senza contenuto collegato ai contatti. Gli id li assegna il database (vedi "Gli id nel seed"). |
| `logs.sh <cartella>` | Log di server e database, un file per servizio (e2e.yml li carica quando qualcosa fallisce). |
| `down.sh [--purge]` | Rimuove contenitori, volumi e rete del progetto compose `filemaster-e2e`; con `--purge` anche `.e2e/`. Non tocca le immagini. |

Requisiti: Docker con `docker compose` v2, `git`, `curl`, `python3`, bash (anche la 3.2 di macOS). La licenza di SQL Server
la accetta chi lancia lo script: `MSSQL_ACCEPT_EULA=Y`.

## Il server di riferimento

Filemaster mira al ramo `master` di Sharp-a-File al commit `541f3789037f6543746de11d02d8388ff9b9a215`: ha i codici cartella, `PATCH
/folders`, i contatti, `created_from`/`created_to`, `has_content` e, dal commit `7ca0e7e`, gli **id numerici** (interi positivi
assegnati dal database; prima erano ULID con prefisso `doc_...`, e il vecchio riferimento era il ramo `dev` al commit `8aec8bb`, ora
superato). `e2e.yml` lo fissa in `SHARPAFILE_SHA` (una corsa deve essere ripetibile, quindi lo SHA e non il ramo mobile); in locale
si passa con `--ref`. Passando un commit precedente a `7ca0e7e` lo schema, il seed e le suite non corrispondono piu'.

## Gli id nel seed

Dal commit `7ca0e7e` ente, chiavi, contatti e documenti hanno id `IDENTITY` (`int`; `bigint` per i documenti), quindi `seed.sh` non
scrive piu' gli id negli `INSERT`: li rilegge dal database per chiave naturale (slug dell'ente, hash della chiave, codice del
contatto, proprietario + nome file del documento) e mai con `SCOPE_IDENTITY()` dopo un `IF NOT EXISTS`, che a riga gia' presente
darebbe un valore vecchio. Il seed resta idempotente. Le categorie dei contatti e le cartelle hanno un codice di testo scelto da noi
(`E2E-FORNITORI`, `FATTURE`). La suite live non assume id fissi: riconosce i dati seminati per codice, nome o id ARXivar (`LiveSeed.cs`) e legge gli id dalle risposte. Lo schema e' la migrazione unica
`20261008193649_InitialSchema`, riscritta da zero nel server: un database creato con le migrazioni vecchie non serve, va ricreato
(`down.sh`, che toglie anche i volumi del progetto compose).

## Linux (x86_64)

```bash
git clone https://github.com/Vinello28/Sharp-a-File.git ../Sharp-a-File
MSSQL_ACCEPT_EULA=Y bash eng/e2e/run-e2e.sh --src ../Sharp-a-File --ref 541f3789037f6543746de11d02d8388ff9b9a215
set -a; . .e2e/state/live.env; set +a
dotnet test --project tests/Filemaster.IntegrationTests -c Release -f net10.0 --filter-trait "Category=Live"
dotnet test --project tests/Filemaster.IntegrationTests -c Release -f net8.0 --filter-trait "Category=Live"
bash eng/e2e/down.sh
```

## macOS su Apple Silicon (`--mac`)

L'immagine `mssql/server:2022` e' solo amd64 e sotto l'emulazione QEMU di Docker Desktop si ferma con un segfault. Con
`--mac` il database diventa **Azure SQL Edge** (arm64 nativo, motore SQL Server 15.0) e lo script corregge, solo nella copia
dei sorgenti in `.e2e/work/`, l'unica riga che Edge non capisce (`ISJSON(metadata, OBJECT)` della migrazione iniziale).
`sqlcmd` per il seed gira in un contenitore client amd64 usa-e-getta.

```bash
MSSQL_ACCEPT_EULA=Y bash eng/e2e/run-e2e.sh --src ../Sharp-a-File --ref 541f3789037f6543746de11d02d8388ff9b9a215 --mac
set -a; . .e2e/state/live.env; set +a
dotnet test --project tests/Filemaster.IntegrationTests -c Release -f net10.0 --filter-trait "Category=Live"
bash eng/e2e/down.sh
```

Un esito su Edge e' una buona prova, non quella definitiva: la CI Ubuntu usa SQL Server 2022 vero.

## Variabili

Stampate da `run-e2e.sh`, scritte in `.e2e/state/live.env` (0600) e, su GitHub Actions, in `$GITHUB_ENV` (chiavi mascherate):

| Variabile | Uso |
| --- | --- |
| `FILEMASTER_E2E_URL` | indirizzo del server (`http://127.0.0.1:18080`). Senza, i test live sono **saltati**. |
| `FILEMASTER_E2E_KEY` | chiave con scope write |
| `FILEMASTER_E2E_READ_KEY` | chiave con scope read (403 sulle scritture) |
| `FILEMASTER_E2E_ADMIN_KEY` | chiave con scope admin |
| `FILEMASTER_E2E_MAX_UPLOAD_BYTES` | limite di upload del server (default 4 MiB, per i due 413) |
| `FILEMASTER_E2E_SERVER_SHA` | commit del server sotto prova (solo informativo) |
| `FILEMASTER_E2E_REQUIRED=1` | (non la stampa lo script) i test live **falliscono** invece di essere saltati se manca l'indirizzo: la imposta `e2e.yml` |

Opzioni di `run-e2e.sh`: `--port N` (default 18080), `--no-build` (riusa l'immagine gia' costruita per quel commit),
`E2E_MAX_UPLOAD_BYTES`, `E2E_PROJECT` (nome del progetto compose), `E2E_DIR` (al posto di `.e2e/`).

## Catturare le fixture (T6.3)

`capture-fixtures.sh [--set <nome>]` manda ~290 richieste vere (successi, errori, intestazioni dei download) e scrive in
`.e2e/fixtures/<nome>/` corpo, stato, intestazioni e richiesta di ognuna, con chiavi, segreti webhook ed email gia' scrubbati, piu'
`index.tsv` (l'elenco) e `selfcheck.txt` (lo stato atteso dal nome contro quello vero: deve dire `0 MISMATCH`). Carica e scarica 5 MiB,
quindi **pretende un server con un limite di upload di almeno 8 MiB** (`FILEMASTER_E2E_MAX_UPLOAD_BYTES` o, dallo stato di
`run-e2e.sh`, `E2E_MAX_UPLOAD_BYTES`) e si rifiuta di partire altrimenti. Con gli id numerici le richieste sui casi di id (zero, `042`,
`+42`, fuori intervallo, il vecchio `doc_...`) stanno in fondo allo script: gli id presi dalle risposte sono numeri nudi e nei corpi si
scrivono senza virgolette; il nome interno `dev` del profilo rilevato (da `GET /contact-categories`) vuol dire "il server ha i
contatti e i codici cartella", cioe' `541f378`.

Va lanciata su un'**istanza usa-e-getta**: oltre alle richieste, lo script scrive e cancella righe di prova nel database, avvia due
istanze extra del server (sulla porta 18081 con il limite di upload a 1 KiB, sulla 18082 con un database senza password dell'archivio),
**ferma e riavvia SQL Server** del progetto compose per catturare il 503 di `/readyz`, e cancella i documenti che ha creato. Il
progetto compose e le chiavi sono quelli di `.e2e/state/` (`run-e2e.sh`). La cattura `t64` del 2026-10-09 (291 richieste,
`0 MISMATCH`) e' stata fatta contro un'istanza sulla porta 18083 con il limite di upload largo:

```
FILEMASTER_E2E_URL=http://127.0.0.1:18083 FILEMASTER_E2E_MAX_UPLOAD_BYTES=104857600 eng/e2e/capture-fixtures.sh --set t64
```

(`FILEMASTER_E2E_URL` e `FILEMASTER_E2E_MAX_UPLOAD_BYTES`, se impostate dal chiamante, puntano lo script a un'istanza diversa da quella di
`run-e2e.sh` e vincono sullo stato: servono a non riavviare quella principale per allargare il limite. Le chiavi si leggono da
`.e2e/state/keys.env` o dalle variabili `FILEMASTER_E2E_KEY`, `_READ_KEY` e `_ADMIN_KEY`.) In alternativa si porta su l'istanza
principale con il limite largo e si lancia lo script senza variabili:

```
E2E_MAX_UPLOAD_BYTES=104857600 MSSQL_ACCEPT_EULA=Y bash eng/e2e/run-e2e.sh --src ../Sharp-a-File --ref 541f3789037f6543746de11d02d8388ff9b9a215 [--mac]
bash eng/e2e/capture-fixtures.sh --set <nome>
```

Le catture non finiscono da sole nel repo: le fixture golden in `tests/Filemaster.UnitTests/Wire/Fixtures/captured/` sono una selezione
scelta a mano (vedi il README di quella cartella). Una nuova cattura serve a confrontare forme e status con quelle, per esempio dopo aver
spostato lo SHA del server.

## Rilanci e pulizia

- `run-e2e.sh` si puo' rilanciare sullo stesso server: non rigenera admin, password dell'archivio, chiavi ne' anagrafica.
- Ogni test live usa nomi, codici e contenuti unici per esecuzione e cancella cio' che crea: la suite si rilancia contro lo
  stesso server. I dati seminati da `seed.sh` sono in sola lettura per i test.
- `.e2e/` (gitignorata) contiene segreti (`state/secrets.env`, `keys.env`, `live.env`, `admin.txt`, tutti 0600) e le copie dei
  sorgenti del server: non va copiata altrove. `down.sh --purge` la cancella.
- Le immagini (`sharpafile-e2e:<commit>`, SQL Server, Azure SQL Edge) restano: si tolgono a mano con `docker image rm`.
