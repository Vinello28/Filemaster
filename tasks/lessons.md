# Lezioni apprese (tasks/lessons.md)

Ogni correzione dell'utente, o errore scoperto durante il lavoro, aggiunge una voce: pattern + regola per non ripeterlo.
Le prime voci sono ereditate da `Sharp-a-File/tasks/lessons.md` (quelle che valgono anche qui) piu' due specifiche del
progetto emerse in pianificazione.

## Ereditate dai progetti gemelli

1. **Pattern CI/build dai progetti gemelli.** Prima di introdurre un tool esterno per CI, build o release, guardare come
   lo risolvono Sharp-a-File e Pack-a-File (workflow, Makefile) e replicare quel pattern. Filemaster non ha precedenti
   per `pack`/`push` NuGet: li ho decisi io in pianificazione (Trusted Publishing OIDC), non sono "ereditati".
2. **"Fatto" si verifica sull'artefatto, non sulla nota.** Si controlla il `.nupkg`, non il csproj; il workflow, non
   il commento che lo descrive.
3. **Ogni identificatore crittografico si copia con un comando dalla fonte.** Digest d'immagine, SHA di una action,
   checksum: `grep` sul file del progetto gemello, `git ls-remote` (SHA del commit "peeled", `^{}`) o
   `docker buildx imagetools inspect`. Mai scritto a mano, nemmeno in parte. Prima di salvare un file con `@sha256:` o
   `uses: ...@<sha>`, confrontare il valore con l'output di un comando lanciato nella stessa sessione.
4. **Fine riga LF.** Il repo ha `* text=auto eol=lf`; i file scritti da script su Windows finiscono in CRLF e
   `dotnet format --verify-no-changes` fallisce. Scrivere con il tool Edit/Write oppure
   `open(p, 'w', encoding='utf-8', newline='\n')`. Prima di chiudere una fase: `dotnet format --verify-no-changes` e
   `git ls-files --eol`.
5. **Replay del workflow su Linux prima di dire "verificato".** Stessi comandi, stesso ordine, in un container
   dell'immagine del runner partendo da un clone pulito. Un workflow YAML non si verifica rileggendolo.
6. **Testo con backslash (regex, LaTeX, `\n`) si scrive con Edit, mai dentro uno script o un heredoc.** Dopo ogni
   scrittura con script: `grep $'\t\|\r'` sui file toccati e rilettura delle righe spezzate (Dockerfile, shell, `run:`).
7. **Non sovrascrivere mai un file esistente senza averlo letto.** Prima ls/Read, poi si estende.
8. **Aspettare un segnale esplicito, non un intervallo tarato sul comportamento attuale** (test con attese temporizzate).

## Specifiche di Filemaster

9. **`global.json` di Sharp-a-File non e' copiabile.** Pinna l'SDK 10.0.401, che sul Mac di sviluppo (SDK 9.0.305 +
   10.0.103) non si risolve: `latestFeature` non scende mai di feature band. Filemaster pinna `10.0.103`.
   **SUPERATA il 2026-10-01 (vedi 12):** il Mac ora ha SDK 10.0.401 + 8.0.425, `global.json` pinna `10.0.400`.
10. **Nessun commit/push senza richiesta esplicita dell'utente; una pubblicazione NuGet e' irreversibile** (le versioni
    sono immutabili, si puo' solo de-listare). Il primo rilascio e' un `-rc` e lo spinge l'utente.
11. **Spike e prove usa-e-getta stanno fuori dal repo** (cartella temporanea per-utente), perche' le `Directory.*.props`
    della radice si applicherebbero anche a loro. Si cancellano a fine prova.
    **Eccezione per cio' che non si puo' ricreare a basso costo** (script e fixture catturate dal server): copia in
    `.scratch/` (gitignorato), SENZA `state/` (ha `secrets.env`) e `work/`. `$TMPDIR` di macOS viene ripulito.
12. **L'ambiente cambia tra una sessione e l'altra: rifare la baseline PRIMA di costruire.** Il 2026-10-01 (dopo la nota
    di T1.2) il Mac e' passato a SDK 10.0.401 + 8.0.425 con runtime 8.0.31 + 10.0.12 e `global.json` a `10.0.400`.
    Effetti: i test net8 girano **in locale** (niente piu' container per net8; il flag `-f net10.0` resta utile solo per
    scegliere un TFM); le note T0.1 su "net8 fallisce senza `-f`" e sul pin `10.0.103` sono superate; la verifica di
    T1.2 era stata fatta su 10.0.103 e va riletta su 10.0.401. Rifatta il 2026-10-01: restore, build `--no-incremental`
    0 avvisi, test 8/8 su net10 e net8, `dotnet format --verify-no-changes` x2, `CI=true dotnet pack` = 4 nupkg + 4 snupkg,
    range `[x]` esatti, 3 cartelle `lib/` ciascuno.
13. **`cmd | tail; echo $?` misura `tail`, non `cmd`.** Per un'accettazione ("format exit 0") redirigere su file e leggere
    `$?` subito dopo, oppure `set -o pipefail`.
14. **Piu' subagent sullo stesso albero senza commit = corse.** `dotnet build/test/format` in parallelo si pestano su
    `obj/` e `project.assets.json`, e le modifiche concorrenti ai file condivisi (`Directory.Packages.props`, csproj di
    test, `.slnx`) si sovrascrivono; senza commit `isolation: worktree` non e' disponibile. Regola: la catena di codice
    (T2 -> T3 -> T4) gira **in serie**, un subagent alla volta; in parallelo solo lavoro su file disgiunti che non invoca
    `dotnet` (T5: workflow, `eng/*.sh`, dependabot). Dopo ogni subagent: build, format x2 e test li rilancio io; il suo
    rapporto e' un'ipotesi finche' non gira sul repo.
15. **Il tool Write/Edit decodifica `\uXXXX` in caratteri veri** (misurato in T2a). Per un escape Unicode in un sorgente C#
    scrivere `\U0000XXXX` (8 cifre: resta com'e'). Dopo ogni scrittura controllare ASCII, CR, TAB e newline finale con un
    piccolo script Python in sola lettura (`grep -P` **non esiste** su macOS/BSD grep: il comando fallisce e sembra "nessun
    risultato"). I file generati in `obj/` hanno CR: il controllo va fatto solo su `src/` e `tests/` senza `obj/` e `bin/`.
16. **Un `record struct` validato non e' mai posizionale**: darebbe un `init` su `Value` e `id with { Value = "x" }` aggirerebbe
    il costruttore. Costruttore esplicito, campo privato, `Value => _value ?? string.Empty`, `IsEmpty`. `default(XId)` e'
    l'id vuoto (non valido): **chi riceve un id (T3) deve rifiutare `IsEmpty` con `ArgumentException`**, altrimenti
    `default(DocumentId)` costruisce silenziosamente l'URL `/documents/`.
17. **Analyzer nei test**: CA2249 spinge a `string.Contains(char)` che non esiste su net48 -> `HashSet<char>`; xUnit2029 vuole
    `Assert.DoesNotContain` al posto di `Assert.Empty` su una lista filtrata; `Assert.Throws<ArgumentException>` vuole il tipo
    esatto (null e' `ArgumentNullException`, va testato a parte). In zsh `grep --include=*.cs` senza apici abortisce l'intero
    comando ("no matches found"): quotare `--include='*.cs'`.

## Esiti degli spike

### T0.1 — toolchain multi-target (SDK 10.0.103, macOS arm64) — 2026-10-01

Spike in `$TMPDIR/filemaster-spikes/toolchain` (fuori dal repo). Tutto verificato con SDK 10.0.103. **Da ririprovare nel
repo vero in T1.2** (un report di un sub-agente e' un'ipotesi finche' non gira sul codice reale).

Cosa e' risultato diverso dal piano:
- **NETSDK1138 non rompe la build con `TreatWarningsAsErrors`** (e SDK 10.0.103 non lo emette ancora per net8.0); si
  attiva solo per `OutputType != Library` => `CheckEolTargetFramework=false` va in `tests/Directory.Build.props`,
  **non** in `src/` (li' sarebbe un no-op).
- **NU1510 non scatta se `netstandard2.0` e' nel set di TFM** anche senza condizioni: la condizione `ns2.0` sui
  pacchetti polyfill resta come igiene, non come necessita'.
- **`NoWarn CS0436` non serve** con PolySharp 1.16.0 (i polyfill hanno `EmbeddedAttribute`, Roslyn li ignora tra assembly).
- **Un `Directory.Build.props` annidato NON eredita quello della radice**: `src/` e `tests/` devono importarlo con
  `<Import Project="$([MSBuild]::GetPathOfFileAbove('Directory.Build.props', '$(MSBuildThisFileDirectory)../'))" />`.
- **Il progetto PackSmoke dentro l'albero del repo eredita `LangVersion`, `Nullable` e CPM** (NU1008): serve un
  `Directory.Build.props`/`Directory.Packages.props` vuoti e un `nuget.config` con `<clear/>`, oppure stare fuori albero.
- **`dotnet format` processa ogni file solo sotto il PRIMO TFM**: secondo passaggio con
  `TargetFrameworks=net10.0 dotnet format --verify-no-changes --no-restore` (funziona perche' `src/Directory.Build.props`
  imposta i TFM con `Condition="'$(TargetFrameworks)' == ''"`). Tenere gli `#if` rari.
- Mancano su ns2.0, oltre alla lista del piano: `string.IndexOf(char, StringComparison)`, `DateTimeOffset.UnixEpoch`,
  `RuntimeHelpers.GetSubArray` (range su array). **CA1510** scatta su net8/net10 per un `throw new
  ArgumentNullException(nameof(x))` scritto a mano: usare un `Guard.NotNull([NotNull] object? v,
  [CallerArgumentExpression(nameof(v))] string? name = null)` interno (uno per assembly).
- Test contro l'asset ns2.0: `SetTargetFramework="TargetFramework=netstandard2.0"` va su **OGNI** ProjectReference (se solo
  sull'ultima, Domain resta net10 senza avvisi) e il progetto di test deve referenziare `Microsoft.Bcl.TimeProvider` e
  `Microsoft.Bcl.AsyncInterfaces`. Autoverifica: un test che legge `TargetFrameworkAttribute` degli assembly caricati.
- xunit.v3 lanciato direttamente (`dotnet X.dll`) e' il runner nativo e rifiuta `--coverage`; `dotnet test` lo porta in
  modalita' MTP. Nel container runtime:8.0 flag nativi: `-noColor -noLogo -result-trx <file>`; copertura li' non utile.
- Test TFM net48: **mai** un `PackageVersion` per `Microsoft.NETFramework.ReferenceAssemblies` (NU1009, e' implicito);
  `ImplicitUsings` non include `System.Net.Http` su net48 (`<Reference Include="System.Net.Http" />` + `<Using>`);
  serve PolySharp anche nel progetto di test; `FakeTimeProvider` avvisa su net48 -> `SuppressTfmSupportBuildWarnings`.
  Il grafo di test net48 risolve STJ **10.0.10**: il job Windows NON prova il floor 8.0.x, lo prova il consumatore net48
  del PackSmoke (risolve STJ 8.0.5 / AsyncInterfaces 8.0.0 / TimeProvider 8.0.1).
- **C# 7.3 dimostrato** (net48, LangVersion di default): `init` -> CS8370, `with` -> CS8370, `await foreach` -> CS8370;
  costruttore + `get; set;`, lettura di record posizionali, `TimeProvider` e un ciclo manuale `GetAsyncEnumerator()`
  compilano a 0 warning (il ciclo manuale e' il percorso C# 7.3 per `EnumerateAsync`).
- **Job vulnerabilita' fail-open**: con una sorgente irraggiungibile il JSON ha `"problems"` ed exit 1, ma `... | tee`
  senza `pipefail` (default `bash -e` di GitHub) lo maschera; la stessa logica e' nel job di Sharp-a-File. Qui:
  `set -o pipefail` e fallire anche su `"problems"`. Il JSON pulito non contiene `"vulnerabilities"` (il grep non da' falsi positivi).
- `AssemblyVersion` segue la versione del pacchetto: ogni release richiede nuovi binding redirect su net48 (automatici SDK-style).
- SourceLink: il commit finisce nel nuspec solo se `origin` e' un URL github.com (in locale senza remote non c'e'); in CI si'.
  Con `CI=true` dll e pdb sono byte-identici da percorsi diversi.

Versioni risolte da comandi sul flat container nuget.org: PolySharp **1.16.0**, System.Text.Json **8.0.5** (8.0.6 e' la piu'
recente 8.x; 8.0.5 risulta pulita), Microsoft.Bcl.AsyncInterfaces **8.0.0** (unica 8.x), Microsoft.Bcl.TimeProvider **8.0.1**,
Microsoft.Extensions.Logging.Abstractions **8.0.3**, Microsoft.Extensions.Http **8.0.1**, xunit.v3 **4.0.1**,
Microsoft.Testing.Extensions.CodeCoverage **18.11.2**, Microsoft.Extensions.TimeProvider.Testing **10.10.0**.

Comandi che funzionano (da `Directory.Build.targets`/props definitivi in T1.1):
```
dotnet restore
dotnet format --verify-no-changes --no-restore          # + 2o passaggio: TargetFrameworks=net10.0 dotnet format --verify-no-changes --no-restore
dotnet build -c Release --no-restore
dotnet test --project tests/<P> -c Release -f net10.0 --no-build --coverage --coverage-output-format cobertura --coverage-output <P>.cobertura.xml
dotnet test --project tests/<P> -c Release -f net10.0 -p:UseNs20Asset=true          # asset netstandard2.0
CI=true dotnet pack -c Release --no-restore -p:Version=<v> -o artifacts
docker run --rm -v "<proj>/tests/<P>/bin/Release/net8.0":/w -w /w mcr.microsoft.com/dotnet/runtime:8.0 dotnet <P>.dll -noColor -noLogo -result-trx /w/TestResults/net8.trx
```
Sul Mac senza `-f` il target net8.0 fallisce con "You must install or update .NET" (exit 150, "Zero tests ran"):
si usa `-f net10.0` in locale e il container per net8. `DOTNET_ROLL_FORWARD=Major` funziona ma esegue net8 su 9.0.9
(solo smoke, non una prova su net8).

### T0.3 — server Sharp-a-File vero — 2026-10-01

Un Sharp-a-File **reale** e' stato costruito dal suo `Dockerfile`, inizializzato, seminato e interrogato (217 richieste su
`master`, 224 su `dev`). Artefatti in `$TMPDIR/filemaster-spikes/e2e/out/` (script + `fixtures/{master,dev}/`).

**Decisione di riferimento (da confermare con l'utente a fine lavoro):** Filemaster mira al ramo **`dev`** di Sharp-a-File
(`8aec8bb78d34f70150cf7be970c4a712a9847546`, non ancora rilasciato). `master` (`f76f335`, release `v1.0.x`) NON ha: codici cartella
utente (le cartelle sono `fld_`+ULID), `PATCH /folders/{id}`, `/contacts`, `/contact-categories`, `created_from/created_to`,
`has_content`, `content-unavailable`, `without_content`. Per questo `e2e.yml` fissa lo **SHA** di `dev` (non il ramo mobile) finche'
non esiste un tag con queste funzioni. `FolderCode` (`^[A-Za-z0-9][A-Za-z0-9_.-]*$`, 1..50) accetta comunque anche `fld_<ULID>` (30
caratteri), quindi le letture funzionano pure contro `master`.

**Database su questo Mac:** `mssql/server:2022-latest` (solo amd64) sotto emulazione QEMU di Docker Desktop crasha (segfault, 3/3
tentativi). Funziona **Azure SQL Edge** (arm64 nativo, ritirato ma ancora scaricabile) + una `sed` su una COPIA dei sorgenti
(`ISJSON(metadata, OBJECT) = 1` -> `ISJSON(metadata) = 1` in `20260923172733_InitialSchema.cs`: la forma a 2 argomenti e' solo
SQL 2022). Le risposte HTTP sono output vero del server; **da ricatturare alla prima esecuzione su Ubuntu con SQL Server 2022
vero** (percorso di default degli script, non provato qui) prima di chiamarle "golden" definitive. Rosetta (impostazione di Docker
Desktop, ora disattivata) non provata: probabile soluzione pulita. Tempi: SQL 2022 pull 4m22s, `docker build` a freddo 3m33s,
`run-e2e.sh` a cache calda ~12 s, cattura completa ~25-30 s; ~10 cicli down/up senza errori.

**Script** (`$OUT/run-e2e.sh --src <clone> --ref <ref> [--mac]`, `capture-fixtures.sh --set <nome>`, `down.sh [--purge]`, `seed.sh`,
`docker-compose.e2e.yml` + `docker-compose.e2e.mac.yml`): `git archive <ref>` in una cartella di lavoro (il clone resta intatto),
`compose build migrate`, `up --wait mssql`, `migrate`, `bootstrap-admin -generate` (solo se non c'e'), `storage-password`
(`-generate` solo se "Nessuna password"; **va impostata PRIMA di `run`**, senza ogni upload e' 503), `seed.sh`, `up app`, attesa su
`/healthz` `/readyz` `GET /tenant`; stampa `FILEMASTER_E2E_URL`, `_KEY` (write), `_READ_KEY`, `_ADMIN_KEY` (e le aggiunge a
`$GITHUB_ENV`). Segreti in `state/secrets.env` (0600, **da gitignorare**). `MSSQL_ACCEPT_EULA=Y` deve arrivare dal chiamante.
`MSSQL_PID` vuoto uccide Edge: tenere `Developer`. Usare `docker rm -v` (3 volumi anonimi per container Edge).

**Schema per il seeding:** `tenants(id nvarchar(32) 'ten_'+ULID, slug nvarchar(63) unico, name, status 'active'|'suspended',
created_at datetime2(6))`; `api_keys(id 'key_'+ULID, tenant_id FK, name, key_hash nvarchar(71) unico = 'sha256:'+hex minuscolo
dello SHA-256 della chiave in chiaro, scope **stringa** 'read'|'write'|'admin' con CHECK, status 'active'|'revoked', created_at,
last_used_at NULL)`. Chiave in chiaro = `saf_`+base64url(32 byte) senza padding (47 caratteri). L'admin di piattaforma e' una riga
`accounts` con `tenant_id IS NULL`.

**Divergenze dal contratto assunto in pianificazione (da riflettere nel client e in `docs/api-contract.md`):**
- **L'header `X-Request-ID` e' ASSENTE su tutte le risposte problem+json** (`Response.Clear()`): il request id va letto dal campo
  `request_id` del corpo (che riporta il valore ripulito inviato dal client). Sui successi l'header c'e'.
- `created_at`: UTC con `Z`, **0-6 decimali con gli zeri finali tagliati** (`...05Z`, `.5Z`, `.12Z`, `.844Z`, `.511018Z`).
- `detail` assente su ogni 405, su 401 con chiave sbagliata e su 415 slug `error`; i dettagli possono contenere inglese di ASP.NET e
  nomi di tipo .NET (non farci logica).
- **415 ha due slug**: `unsupported-media-type` (anteprima non PDF) ma anche `error` senza detail (JSON con `text/plain`) e
  `validation-error` con status 415 (Content-Type mancante) => mappare per **slug, con ripiego sullo status** (415 -> tipo non
  supportato). **413 ha due percorsi** (limite dello store "contenuto troppo grande"; limite Kestrel "richiesta troppo grande",
  `Connection: close`).
- 404/200: `GET /documents?folder_id=doc_abc` e' 404 su master, 200 (vuoto) su dev. `created_from` senza offset: 400 su dev, ignorato
  su master. Un 416 per range non soddisfacibile (`bytes */590`); range suffisso/aperto -> 206; multi-range -> 200 intero;
  `If-Modified-Since` -> 304; niente ETag; `HEAD` -> 405 ovunque; `Accept-Ranges: bytes`, `Last-Modified` (secondi) e `Content-Length` presenti.
- `POST /documents` -> 201 **senza** `Location`; `deduplicated:true` = il blob (stessi byte, stesso ente) esisteva gia', anche con altro
  nome file; il documento nuovo c'e' sempre. Nome file in upload: `filename="perche' e'.pdf"` grezzo UTF-8, `filename*=utf-8''...` da solo
  o entrambi danno `original_filename` corretto. Download: `attachment; filename="perch_ _.pdf"; filename*=UTF-8''perch%C3%A9%20%C3%A8.pdf`.
  Sniffing MIME su `application/octet-stream`/assente; un tipo dichiarato vince.
- Cancellazioni: `DELETE /api-keys/{id}` due volte -> 204, 204; `DELETE /webhooks/{id}` due volte -> 204, **404**. `bulk/verify` con un
  id ignoto -> 404 sull'intero lotto; `bulk/move` ignora gli ignoti (`{"moved":N}`). `PATCH /documents/{id}/folder` con `{}` -> 204 (radice).
- **Bug del server (non del client)**: `POST /documents/bulk` con corpo multipart o ZIP corrotto -> 500 `internal-error` (l'errore EOCD
  sfugge al try/catch). Il client deve trattarlo come errore del server, non ritentare.
- `/readyz` 503 = JSON semplice (non problem+json); `/audit` senza cursore, `before` = `created_at` dell'ultimo elemento; contatti e
  rinomina cartella solo su dev. Escape JSON: `'` e `"` come `'`/`"` (STJ li decodifica).
- Export ZIP: voci `<doc_id>_<filename>` + `manifest.json` `{"files":{voce:{owner,tag,sender,recipient,folder_id,metadata}}}`, null omessi,
  non-ASCII come `\uXXXX`; l'import prende lo ZIP **grezzo** nel corpo (non multipart); un export vuoto contiene solo il manifest.

**Lasciati sul Mac dallo spike** (pulizia a fine lavoro, se vuoi): immagini `mssql/server:2022` (2,34 GB, inutilizzabile qui),
`azure-sql-edge` (2,5 GB), SDK, 2x `sharpafile-e2e:*` (354 MB). Nessun container o volume in piu'.

### T2b — enum, entita', Arxivar, eventi webhook (Domain) — 2026-10-01

- **Un record posizionale genera `init` per ogni parametro** (in conflitto con "record posizionali" + "niente `init`"). Rimedio: costruttore posizionale, `Deconstruct` e
  uguaglianza restano, e ogni proprieta' si **ridichiara `{ get; } = X`**; una posizionale senza `<param>` non da' CS1591. CS8907 ("parametro non letto") prende la
  proprieta' ridichiarata dimenticata, ma NON lo scambio di una coppia (`City`/`Province`): serve il test di round-trip per reflection. Il test sull'`init` guarda il
  nome completo del modreq (`System.Runtime.CompilerServices.IsExternalInit`), non `typeof`, altrimenti sull'asset ns2.0 passerebbe a vuoto.
- **`JsonElement` in un record: l'uguaglianza NON e' per valore** (identita' del `JsonDocument` + posizione; un `Clone()` non e' uguale all'originale). Un elemento di un
  `JsonDocument` smaltito lancia su ogni lettura, anche con `ToString()` del record. `default(JsonElement).ValueKind` e' `Undefined` e non lancia.
- Con chiavi JSON duplicate `TryGetProperty` restituisce l'ultima. `TryGetInt32("61617.0")` = false, `TryGetDouble` = true; su net10 `TryGetDouble("1e400")` = true con
  infinito: servono i limiti espliciti. `DateOnly` non esiste su ns2.0: `DateTime` Unspecified con `TryParseExact("yyyy-MM-dd", InvariantCulture)`.
- **`docnumber` Arxivar**: accetta un numero JSON con valore intero in range di `int` (`61617`, `61617.0`, `6.1617e4`; anche `0` e negativi: il server non valida il segno),
  rifiuta stringa, decimale, fuori range, null, bool. Coerente col filtro `metadata={"arxivar":{"docnumber":N}}` (contenimento JSON, numeri per valore).
- **Fatti del server `dev`**: `GET /tenant` = `{id, slug, name, status, created_at}` (niente scope: compare solo in `/api-keys`); verify = `ok: bool` + `detail` (nessun enum);
  la busta webhook e' `{event, delivery_id, occurred_at, payload}` (nessun tenant ne' altro id), `delivery_id` (`whd_`+ULID) e' la chiave di idempotenza e resta uguale tra i
  ritentativi; `occurred_at` e' il momento di accodamento; **i DTO delle API omettono i null, il payload dei webhook no** (`"sha256":null`); gli elenchi non hanno `contacts`
  e i contatori di `Contact` sono solo negli elenchi; `bulk/verify` = `{total, verified, failed, without_content}`; la fonte di `metadata.arxivar` non e' `Arxivar.cs`
  ma `ArxivarMapping.Metadata` (Application) + `JsonContainment` (Infrastructure). Un ente sospeso risponde 403 a tutto: `TenantStatus.Suspended` e' praticamente irraggiungibile.
- Nei test: raw string literal `"""` per il JSON, `\U000000E0` per i caratteri non ASCII; il tool Write puo' lasciare `\n` letterali negli script: ricontrollare dopo ogni scrittura.

### T5a — script `eng/` (verify-packages, docker-replay) — 2026-10-01

- **Gli script girano in tre ambienti diversi**: macOS (bash 3.2, awk BSD, niente `readlink -f`), `sdk:10.0` (Ubuntu 24.04, **mawk**, **niente `unzip`**: lo
  installa il replay), runner GitHub (bash 5, gawk). Quindi awk POSIX, niente `declare -A`/`mapfile`/`readlink -f`. Provato: stesso output (251 righe) su
  bash 3.2+BSD awk e bash 5.2+mawk.
- **`set -o pipefail` + `unzip -p | grep -q` dai falsi 141 (SIGPIPE)**: scrivere su file e fare grep sul file.
- **Exit code**: `dotnet format` restituisce gia' 2; gli script di orchestrazione usano 64 per "uso errato/prerequisiti".
- shellcheck 0.11: SC2329 per funzioni chiamate via `"$@"`/`trap` -> `# shellcheck disable=SC2329` motivato.
- **Uno script comunicato ad altri agenti diventa un'interfaccia**: `--require-commit` e' stato adottato da `ci.yml`/`release.yml` appena comparso nell'header.
  Prima di rimuovere/rinominare un'opzione, `grep` dei chiamanti in `.github/`.
- La verifica "nessun altro `.nupkg`" e' compatibile con `dotnet pack -o artifacts` sull'intera soluzione (i test hanno `IsPackable=false`);
  `SHA256SUMS.txt` si crea dopo la verifica. Senza `.git` (replay) il nuspec non ha `commit`: `--require-commit` solo nei job con checkout vero.
- **Il repo non ha commit**, quindi il "clone pulito" del replay e' `git ls-files -co --exclude-standard` (tracciati + non tracciati non ignorati).

### T5b — workflow GitHub — 2026-10-01

- **`setup-dotnet` con `global.json` e `rollForward: latestFeature` installa il canale `major.minor` (l'ultimo SDK, oggi 10.0.401), non il
  numero scritto nel pin**; `global-json-file` + `dotnet-version: 8.0.x` si sommano (la seconda e' "una versione aggiuntiva").
- **`dotnet nuget push`**: un `.nupkg` su nuget.org spinge da solo l'`.snupkg` gemello; per avere l'ordine voluto: `.nupkg` con `--no-symbols`, poi gli
  `.snupkg` **senza** `--no-symbols` (con quel flag, o su una sorgente senza simboli, l'`.snupkg` viene saltato in silenzio con exit 0). Elenco
  esplicito, non glob (`Filemaster.0.1.0.nupkg` ordinerebbe prima di `Filemaster.Application...`).
- **Tag leggeri vs annotati**: i 6 pin attuali sono tag leggeri (lo SHA di `ls-remote` e' il commit), ma `softprops/action-gh-release` v3.0.3 e' annotato
  (SHA del tag object diverso da quello del commit): controllare **sempre** anche `^{}`. Il tag flottante `NuGet/login@v1` NON punta a v1.2.0.
- **`softprops/action-gh-release` v2.6.2 gira su node20**, le altre azioni pinnate su node24. Pin copiato da Sharp-a-File (lezione 1), quindi lasciato;
  esiste la v3.0.3 (node24, stessi input/output): da valutare insieme a Sharp-a-File se node20 viene dismesso sui runner. Se si passa, lo SHA va
  **ririsolto con un comando** (`git ls-remote ... 'refs/tags/v3.0.3^{}'`), non copiato da questa nota.
- **`shell: bash` esplicito = `-eo pipefail`, il default di `run:` no**: stessa riga, comportamento diverso (i job "fail-open" nascono cosi').
  Qui `defaults.run.shell: bash` a livello radice.
- **`$GITHUB_REF_NAME`, mai `${{ github.ref_name }}` dentro un `run:`**: un nome di tag e' input controllato da chi spinge. In `with:` va bene.
- `dotnet test` (MTP): zero test selezionati = exit 8; `--ignore-exit-code 8` per i segnaposto, poi `--minimum-expected-tests 1`.
  `--coverage-output X.xml` scrive in `./TestResults/` della directory corrente, non accanto alla DLL.
- **Dependabot `ignore` con nome esatto, non glob**: `Microsoft.Extensions.*` prenderebbe anche `Microsoft.Extensions.TimeProvider.Testing` (solo test).
- Dipendenza non risolta: **NU1900** (dati sulle vulnerabilita' non scaricabili) non e' in `WarningsNotAsErrors` (solo NU1901-NU1904), come nei repo gemelli;
  valutato e non cambiato (senza nuget.org il restore fallirebbe comunque).
- **zsh**: `set -- $spec` non divide le parole (e' successo anche a me: usare `while read -r a b`); `$1:ref` e' un modificatore, scrivere `${1}`;
  un glob senza corrispondenze annulla l'intero comando; `timeout` non esiste su macOS.

### T2a — ID, FolderCode, eccezioni (Domain) — 2026-10-01

Misurato con spike usa-e-getta fuori dal repo (Ulid 1.4.1, ogni carattere BMP in ogni posizione + 2 milioni di stringhe casuali): 0 differenze
tra la regola del server e il ciclo del client; test con 5 mutazioni (tutte catturate).
- **Regola degli ID** = prefisso + 26 caratteri, **primo carattere solo `0`..`7`** (26 cifre base32 = 130 bit, un ULID ne ha 128: `8`+ e'
  overflow), gli altri 25 in `0-9A-HJKMNP-TV-Z`. `Ulid.TryParse` da solo e' permissivo (minuscolo, I/L/O/U decodificati a caso, overflow
  troncato): a rifiutarli e' solo il confronto con `ToString()`. Il server fa `Length == 26 && TryParse && rest == ulid.ToString()`.
- **`FolderCode`**: l'unica divergenza dal server e' il newline finale (`"a\n"`): il `$` della regex del server lo accetta, il client no (voluto:
  nessun trim, nessun "aggiustamento" silenzioso).
- **Slug problem+json realmente emessi** (da codice e fixture 4xx/5xx): `not-found` 404, `conflict` 409, `content-unavailable` 409 (stesso status del
  conflitto: si distingue **solo per slug**), `validation-error` 400 (ma anche altri status via `BadHttpRequestException`: la fixture 36 e' 415),
  `unauthorized` 401, `forbidden` 403, **`request-too-large`** 413 (non `too-large`), `unsupported-media-type` 415, `storage-not-configured` 503,
  `internal-error` 500, `method-not-allowed` 405, `error` generico con qualunque status (anche 415 nelle fixture 35/37/143). **Il server `dev` non
  emette mai 422.** `type` e' `/problems/{slug}`: `ProblemType` tiene lo slug nudo. Un 416 non ha corpo. Il 413 del limite Kestrel puo' arrivare
  come errore di connessione. `content-unavailable` esce anche da verify e dall'export bulk.
- **Mappatura proposta per T4** (scritta nel doc XML di `FilemasterException`): slug prima, status come ripiego, **ma per il 415 vale sempre
  lo status** -> `UnsupportedMediaTypeException`; 405, 416 senza corpo, slug `error` con altri status e status imprevisti ->
  `UnexpectedResponseException`; il 503 diverso da `storage-not-configured` -> `ServerErrorException`; l'annullamento del chiamante resta
  `OperationCanceledException`, solo il timeout del client diventa `FilemasterTimeoutException`.
- Il Domain espone `ContentIntegrityException(bool isTruncated, int statusCode, ...)` (bool e non enum: la decisione "niente enum" per i
  membri del Domain vale solo per i nomi wire, ma qui un bool si legge meglio con l'argomento con nome).
