# Filemaster — piano di lavoro (tasks/todo.md)

> Copia del piano approvato il 2026-10-01 (fonte: sessione di pianificazione). Questo file e' la lista viva: si spuntano
> le voci man mano, e in fondo c'e' la sezione "Review" con i risultati verificati.

## Contesto

Sharp-a-File (server .NET 10, port C# di Pack-a-File, con funzioni di migrazione dal DB Arxivar) espone una API HTTP
"nuda": nessun OpenAPI, nessuna versione/prefisso URL, JSON snake_case con nomi enum espliciti, ID prefissati
(`doc_` + ULID maiuscolo), cursori opachi, upload multipart senza idempotenza, errori `problem+json`, webhook firmati
HMAC. Ogni applicazione che la usa dovrebbe riscrivere tutto questo. **Filemaster** e' la libreria client che lo
nasconde dietro interfacce tipizzate, con clean architecture, pubblicata su NuGet tramite GitHub Release, con pipeline
CI e Release. Destinazione: `Filemaster/` (oggi solo `AGENTS.md` + `.git` senza commit).

Fonti verificate: contratto HTTP letto dal codice del server (`Api/*.cs`, `Http/*.cs`, `Domain/*.cs`); convenzioni
CI/test/stile dei repo gemelli; ambiente locale (SDK 9.0.305 + 10.0.103, runtime 9.0.9 + 10.0.3, Docker 29, **niente
`gh`**, **niente runtime .NET 8**, SQL Server arm64 non garantito); documentazione NuGet Trusted Publishing.
Sharp-a-File e Pack-a-File sono repo **pubblici** (verificato con `git ls-remote` anonimo).

## Decisioni gia' prese (tue)

| Tema | Scelta |
|---|---|
| Target framework | `netstandard2.0;net8.0;net10.0` (raggiunge anche .NET Framework 4.8) |
| Licenza | Apache-2.0 (`LICENSE` + `PackageLicenseExpression`) |
| Rilascio | push di un tag `v*` -> verifica, pack, GitHub Release con asset, push su nuget.org |
| Repo | pubblico, `github.com/Vinello28/Filemaster` |
| Package ID (liberi su nuget.org, verificato) | `Filemaster`, `Filemaster.Domain`, `Filemaster.Application`, `Filemaster.Infrastructure` |

Assunzioni mie: XML doc/commenti/README in **italiano** con apostrofi ASCII (`e'`) come Sharp-a-File, identificatori
in inglese; eccezioni e non `Result<T>` (come il server); **nessun commit/push senza tua richiesta**; copertura
dell'**intera** API HTTP, in due fasi (A = nucleo, B = amministrazione + import/export ZIP).

> **Da sapere su `net8.0`**: .NET 8 (e 9) esce dal supporto il **10 novembre 2026**, tra 40 giorni. Con `net8.0` nei
> TFM l'SDK 10 emettera' NETSDK1138 (che con warning-as-errors rompe la build): lo spengo con
> `CheckEolTargetFramework=false` e un commento. Il target resta come hai deciso, ma rimuoverlo e' una riga in
> `src/Directory.Build.props` (gli utenti .NET 8 userebbero comunque l'asset `netstandard2.0`).

## Cosa devi fare TU (una tantum; io non ho `gh` ne' accesso ai tuoi account)

1. **GitHub**: crea il repo pubblico `Vinello28/Filemaster` *vuoto* (niente README/licenza). Poi
   `git remote add origin https://github.com/Vinello28/Filemaster.git && git push -u origin main` (i commit li preparo
   solo se me lo chiedi).
2. **Settings -> Environments**: crea `nuget` (facoltativo: tu come "required reviewer" per un secondo click; le
   deployment rules devono ammettere i tag `v*`).
3. **Settings -> Secrets and variables -> Actions**: secret `NUGET_USER` = *username/profile name* di nuget.org
   (NON l'email).
4. **nuget.org -> tuo utente -> Trusted Publishing -> Add policy**: owner `Vinello28`, repository `Filemaster`,
   workflow file `release.yml` (solo il nome), environment `nuget`, scope "nuovi package + nuove versioni", glob
   `Filemaster*`. Nessuna API key a lunga durata: il token OIDC viene scambiato con una chiave temporanea di 1 ora.
   *Piano B* se la prima pubblicazione di ID nuovi venisse rifiutata: API key con glob `Filemaster*`, scadenza breve,
   come secret `NUGET_API_KEY`, da cancellare dopo il primo push (descritto in `docs/publishing.md`).
5. **Primo rilascio = prova generale**: tag **`v0.1.0-rc.1`** (`git tag v0.1.0-rc.1 && git push origin v0.1.0-rc.1`).
   E' una pubblicazione vera e le versioni NuGet sono immutabili, ma da' un rc invece di una stabile mentre
   si collauda la pipeline. Poi `v0.1.0`. Restiamo in 0.x finche' la suite live e' verde.
6. (Consigliato) Branch protection su `main` con CI obbligatoria; protezione dei tag `v*`.

## Architettura

Quattro assembly, regola di dipendenza verso l'interno, **4 pacchetti NuGet con versioni in lockstep**:

```
Filemaster (composition root: AddFilemaster, FilemasterClientFactory)  -> Domain, Application, Infrastructure, M.E.Http
Filemaster.Infrastructure (adapter HTTP, trasporto, JSON/wire)         -> Application, Domain, M.E.Logging.Abstractions
Filemaster.Application (porte + modelli richiesta + estensioni)        -> Domain
Filemaster.Domain (ID tipizzati, entita', errori)                      -> nulla (System.Text.Json solo su netstandard2.0)
```

Perche' 4 pacchetti: l'applicazione del cliente puo' referenziare solo `Filemaster.Application` nel proprio strato
applicativo (porte fakeabili nei suoi test) e solo l'host referenzia `Filemaster`; un ricevitore di webhook puo'
usare `Application` senza HttpClient. Application resta sottile di proposito: niente classi "service", handler o
mediator.

| Pattern / principio | Dove |
|---|---|
| Ports & Adapters (DIP) | porte in Application, adapter HTTP internal in Infrastructure |
| Facade | `IFilemasterClient` espone le porte come proprieta' (non le eredita) |
| ISP | una porta per risorsa (`IDocumentStore`, `IFolderCatalog`, ...) |
| Value Object | `DocumentId`, `ContactId`, `TenantId`, `FolderCode` (`readonly record struct`, validazione canonica) |
| Decorator | `VerifiedContentStream` sopra `Stream` (conta byte e SHA-256) |
| Iterator | `EnumerateAsync` -> `IAsyncEnumerable` sopra i cursori |
| Factory | `FilemasterClientFactory` (senza DI, per app .NET Framework) |
| Options | `FilemasterOptions` validate all'avvio |
| Strategy | `IApiKeyProvider` (fase B, rotazione chiavi senza riavvio) |

**Scartato, con motivo**: decorator di validazione (40 metodi di inoltro per 5 controlli, aggirabili usando
l'adapter) -> le invarianti stanno nei Value Object e in `Validate()` delle richieste, chiamato *prima* di aprire
lo stream; catena di `DelegatingHandler` -> un `FilemasterTransport` interno che ricostruisce la richiesta a ogni
tentativo (una `HttpRequestMessage` non si reinvia; `Properties` e' obsoleto); query builder fluente -> classi semplici
con costruttore + proprieta' `get; set;` (compatibili con C# 7.3, vedi sotto); chunking dei bulk -> il server non ha
un tetto sugli id.

### API pubblica (schizzo; ogni metodo async termina con `CancellationToken ct = default`, `ConfigureAwait(false)`)

**Domain** — `DocumentId`/`ContactId`/`TenantId` (+ `AccountId`/`ApiKeyId`/`WebhookId` fase B): prefisso + 26 caratteri
Crockford maiuscoli, rifiuto del minuscolo (il server risponde 404 a un id non canonico), controllo a cicli di
caratteri, **mai regex** (`$` accetta il newline finale, come nel `FolderCodes` del server). `FolderCode`:
`^[A-Za-z0-9][A-Za-z0-9_.-]*$`, 1..50, case-sensitive. Entita' immutabili: `Document` (con `JsonElement Metadata`,
`Contacts` vuoto se assente), `DocumentContact`, `Folder`, `Contact`, `ContactCategory`, `Tenant`, `IntegrityCheck`,
`BulkVerifyResult`, `ArxivarMetadata` (+ `From(JsonElement)`), eventi webhook (`DocumentUploadedEvent`,
`DocumentDeletedEvent`, `DocumentIntegrityFailedEvent`, `UnknownWebhookEvent`). Enum con valore `Unknown`: gli
sconosciuti non lanciano mai. Nomi wire **non** in Domain (vivono in Infrastructure).
Eccezioni: `FilemasterException` (`StatusCode`, `ProblemType`, `RequestId`, `Detail`) -> `NotFoundException`,
`ConflictException`, `ContentUnavailableException`, `InvalidRequestException`, `UnauthorizedException`,
`ForbiddenException`, `RequestTooLargeException`, `UnsupportedMediaTypeException`, `StorageNotConfiguredException`,
`ServerErrorException`, `ConnectionException`, `FilemasterTimeoutException`, `UnexpectedResponseException`,
`ContentIntegrityException`. Violazioni lato client = `ArgumentException`; il 400 del server = `InvalidRequestException`.

**Application (fase A)** — `IDocumentStore` (`UploadAsync(UploadDocumentRequest)` -> `UploadResult(Document, Deduplicated)`,
`ListAsync(DocumentQuery, PageRequest)`, `GetAsync`, `OpenContentAsync(id, ByteRange?)`, `OpenPreviewAsync`, `DeleteAsync`,
`MoveAsync(id, FolderCode?)`, `VerifyAsync`, `MoveManyAsync`, `VerifyManyAsync`), `IFolderCatalog`
(`Create/Update/ListChildren/Delete`), `IContactDirectory` (`List/Get/ListCategories`), `ITenantInfo` (sonda
connettivita'+auth), `IFilemasterHealth` (`/healthz`, `/readyz` il cui 503 NON e' problem+json), `IFilemasterClient`.
Modelli di **input** = classi con costruttore a argomenti obbligatori + proprieta' `get; set;` per gli opzionali
(**niente `init`, niente `required`**): `UploadDocumentRequest(Stream, FileName)` (ContentType, FolderId, Owner<=255,
Tag<=255, Sender/Recipient<=4096 byte, `Metadata` oggetto <=64 KiB) con `Validate()` che non tocca lo stream;
`DocumentQuery`, `ContactQuery`. Modelli di **output** = record posizionali (si leggono da qualunque C#): `Page<T>`,
`PageRequest(Cursor, Limit 1..200)`, `ByteRange`; `DocumentContent : IDisposable`.
Estensioni: `EnumerateAsync` (auto-paginazione), `FindByArxivarDocnumberAsync`
(`metadata={"arxivar":{"docnumber":N}}`), `GetArxivarMetadata`, `OpenVerifiedContentAsync` (solo contenuto intero).
Webhook: `WebhookSignatureVerifier(secret, tolerance=5min, TimeProvider?)` -> `Valid|MissingHeader|Malformed|
TimestampOutOfTolerance|SignatureMismatch` (chiave = UTF-8 dell'intero segreto con `whsec_`, messaggio `"<t>." + corpo
grezzo`, confronto a tempo costante in-house, parti sconosciute tollerate) e `WebhookEventParser`.
**Fase B**: `IAuditLog`, `IAccountAdmin`, `IApiKeyAdmin` (segreto una tantum), `IWebhookAdmin`, import/export ZIP,
`IApiKeyProvider`, enumerazione audit (`before` + dedupe per id), `EnsureExistsAsync` sulle cartelle.

**Infrastructure** — superficie pubblica ridotta a `FilemasterOptions` (`BaseAddress`, `ApiKey`, `RequestTimeout` 30 s,
`TransferTimeout` 30 min, `Retry`), `FilemasterRetryOptions`, `FilemasterHttp.CreateClient(HttpClient, options, ...)`.
Adapter e DTO wire **internal** (un DTO per richiesta, campi esatti: il server rifiuta le proprieta' sconosciute).
Il trasporto: `HttpClient.Timeout` infinito + CTS per chiamata; `BaseAddress` normalizzato con `/` finale; avviso una
tantum su `http` non-loopback; `X-API-Key`, `X-Request-ID`, `User-Agent`; **retry solo per GET** (e `healthz`/`readyz`)
su errori di connessione, 408, 429 (con `Retry-After`) e 502/503/504 *non* problem+json — mai su upload, DELETE,
PATCH, POST (un DELETE ritentato dopo un successo darebbe un 404 fuorviante; verify scrive audit e webhook); 500 e
409 non si ritentano; mapping problem+json -> eccezioni dopo il ciclo di retry; upload multipart in streaming con
`filename*=utf-8''...` + fallback ASCII; PATCH creato a mano (`HttpMethod.Patch` non esiste su ns2.0).

**Filemaster (root)** — `AddFilemaster(this IServiceCollection, Action<FilemasterOptions>)` -> `IHttpClientBuilder`
(i consumatori ci agganciano Polly/handler; la nostra retry sta fuori, `Retry.MaxAttempts = 1` la spegne; header
`X-API-Key`/`Authorization` oscurati nei log); `FilemasterClientFactory.Create(options, handler?, loggerFactory?)`
(`SocketsHttpHandler` con `PooledConnectionLifetime` su net5+, `HttpClientHandler` su netfx con
`MaxConnectionsPerServer` alzato: il default 2 blocca un download + una list).

## Scelte tecniche netstandard2.0 / multi-target

- **Dipendenze** (versioni da risolvere con comandi sul feed, mai a mano): Domain = `System.Text.Json` 8.0.5 (solo ns2.0)
  + PolySharp (`PrivateAssets=all`); Application = `Microsoft.Bcl.AsyncInterfaces` 8.0.0, `Microsoft.Bcl.TimeProvider`
  8.0.1 (solo ns2.0) + PolySharp; Infrastructure = come Application + `Microsoft.Extensions.Logging.Abstractions` 8.0.3;
  Filemaster = `Microsoft.Extensions.Http` 8.0.1. Pacchetti polyfill **condizionati a ns2.0** (altrimenti NU1510 su
  net10 con warning-as-errors). Floor 8.0.x di proposito (floor piu' alti danno NU1605 a chi li fissa); Dependabot con
  `ignore` su questi.
- **Versioni tra i 4 pacchetti**: `dotnet pack` emette `>= x.y.z` per i ProjectReference; un mix tra versioni darebbe
  `TypeLoadException` (niente default interface members su ns2.0). Target in `Directory.Build.targets` che porta i
  range a `[x.y.z]`; accettazione `unzip -p X.nupkg '*.nuspec' | grep 'version="\['` (spike T0.1; piano B: nuspec
  controllato a mano).
- **API assenti su ns2.0 -> helper interni minimi**: `Convert.ToHexString`, `CryptographicOperations.FixedTimeEquals`,
  `SHA256.HashData`, `ArgumentNullException.ThrowIfNull`, `Random.Shared`, `Stream.ReadAsync(Memory)`, overload con
  token di `HttpContent`. Niente `[GeneratedRegex]`.
- **Chi chiama da C# 7.3** (default di un progetto net48, proprio l'utente per cui esiste l'asset ns2.0): non puo'
  impostare proprieta' `init` ne' usare `with`. Quindi **nessun `init`/`required` sui tipi di input** (richieste, query,
  `FilemasterOptions` con `set`); i record di output si leggono senza problemi. `EnumerateAsync` richiede C# 8
  (`await foreach`): per C# 7.3 il percorso e' `ListAsync` con cursore (lo scrivo nel README). Un test di architettura
  blocca i setter `init` sui tipi di input.
- **netfx**: STJ porta molte dipendenze transitive -> servono progetti SDK-style (binding redirect automatici); `HttpClient`
  su netfx **bufferizza il corpo della richiesta** (upload grandi = memoria; documentato; handler iniettabile);
  troncamento dei download rilevato dal nostro wrapper su ogni TFM.
- **Analyzer per libreria pubblica** (diff rispetto a `Sharp-a-File/.editorconfig`): CA2007 (`ConfigureAwait`) `error`
  in `src`; cadono le soppressioni "da servizio" (CA1054/1055/1056 si risolvono con `Uri`, CA1002/1515/1000); CA1716 attiva
  (chiamanti VB.NET); `IDE0005` warning; `Guard.NotNull` sui punti d'ingresso pubblici. `GenerateDocumentationFile` +
  CS1591 errore sull'API pubblica.
- **Props**: radice = contenuto di Sharp-a-File senza `TargetFramework`; `src/` = TFM, XML doc, packaging
  (`Apache-2.0`, `PackageReadmeFile`, SourceLink integrato nell'SDK, snupkg, `PublishRepositoryUrl`,
  `EmbedUntrackedSources`, `CheckEolTargetFramework=false`); `tests/` = `IsPackable=false`, TFM `net8.0;net10.0` (+`net48`
  solo su Windows), `NoWarn CS0436` (PolySharp + `InternalsVisibleTo` verso UnitTests). `EnablePackageValidation` con
  baseline dal 1.0.0.
- **`global.json`**: SDK `10.0.400` (aggiornato il 2026-10-01 dopo l'upgrade dell'ambiente: SDK 10.0.401 + 8.0.425, runtime 8.0.31 +
  10.0.12; prima era `10.0.103`, vedi `lessons.md` 12), `rollForward: latestFeature`, runner `Microsoft.Testing.Platform`.

## Pipeline (progetto)

**`ci.yml`** — `push` su `main` + `pull_request`; `permissions: contents: read`; `concurrency` con cancellazione. Azioni
pinnate per SHA completo + commento col tag: checkout/setup-dotnet/softprops copiati dai workflow di Sharp-a-File con
`grep`; upload/download-artifact e `NuGet/login` con `git ls-remote` (SHA del commit "peeled", mai digitati a mano).
- `build` (ubuntu): setup-dotnet (`global-json-file` + `dotnet-version: 8.0.x` per il runtime) -> restore -> `dotnet format
  --verify-no-changes --no-restore` -> build Release -> test net8+net10 (copertura solo come riga di log, nessuna
  soglia) -> `dotnet pack` con `Version=0.0.0-ci.$GITHUB_RUN_NUMBER` -> `eng/verify-packages.sh` -> upload artefatti.
- `windows`: build + test su net8/net10/**net48** (e' l'unico modo di provare davvero l'asset ns2.0 sul runtime reale:
  binding redirect, buffering) + test di memoria su upload da 200 MB. Niente job macOS: nessun codice dipendente
  dall'OS, e' coperto dal Mac di sviluppo.
- `pack-smoke`: installa i `.nupkg` da un feed locale (cache NuGet isolata, versione prerelease unica per run) in una
  console usa-e-getta (net8/net10, net48 su Windows) ed esegue una chiamata contro un server finto. Il progetto net48
  **non sovrascrive `LangVersion`** (resta C# 7.3) e costruisce davvero una `UploadDocumentRequest` e una
  `DocumentQuery` con i campi opzionali valorizzati: cosi' un `init` rimasto per sbaglio rompe il job.
- `vulnerabilita` (non bloccante): `dotnet list package --vulnerable --include-transitive --format json`.
- `.github/dependabot.yml`: `github-actions` e `nuget` (con `ignore` sui floor 8.0.x).
- `e2e.yml` (`workflow_dispatch` + notturno, non bloccante): clona Sharp-a-File (pubblico, ref fissato) e ne **costruisce
  l'immagine dal suo `Dockerfile`/compose** (cosi' non serve un secondo SDK e si aggira il pin 10.0.401 del suo
  `global.json`), SQL Server 2022, `migrate` -> `bootstrap-admin` -> `storage-password -generate` -> tenant + chiave
  seminati via SQL (`api_keys.key_hash = sha256:<hex>`, nomi colonna dalle migrazioni EF snake_case), poi la suite live.
  Stessa procedura del T0.3.

**`release.yml`** — `push` di tag `v*`; permessi di default `contents: read`, `contents: write` solo sul job release,
`id-token: write` solo su quello di publish.
- `verifica`: regex del tag `^v[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z.-]+)?$`; il commit del tag deve discendere da
  `origin/main` (**checkout con `fetch-depth: 0`**: il default e' shallow e il controllo fallirebbe al primo giro;
  il `release.yml` di Sharp-a-File non lo ha e non va copiato in questo punto); restore/format/build/test (ubuntu + job windows net48) — un tag puo' puntare a un commit che la CI
  non ha mai visto.
- `pack`: `-p:Version=${tag#v}`, `eng/verify-packages.sh` (3 cartelle `lib/`, range esatti tra sibling, licenza,
  readme, snupkg), `SHA256SUMS.txt`, upload artefatti.
- `github-release`: `softprops/action-gh-release` (SHA di Sharp-a-File) con note generate e asset; `prerelease` se il
  tag contiene `-`.
- `publish-nuget` (environment `nuget`): `NuGet/login@v1` (`user: ${{ secrets.NUGET_USER }}`) -> `dotnet nuget push`
  `--skip-duplicate`, foglie prima (Domain, Application, Infrastructure, Filemaster), tutti i `.nupkg` prima degli
  `.snupkg`. Idempotente: se fallisce dopo la release, si rilancia il job.

## Strategia di test

- **Unit** (`tests/Filemaster.UnitTests`; xunit.v3 + MTP, `Assert` semplice, fake scritti a mano, `FakeTimeProvider`,
  nomi inglesi con underscore come Sharp-a-File): Domain (id, `FolderCode`, Arxivar), Application (validazione
  richieste con stream intatto, paginazione, `VerifiedContentStream`, verifier webhook con finestra e retry,
  parser), Infrastructure con `HttpMessageHandler` finto + **fixture JSON "golden" catturate dal server reale**
  (snake_case, null omessi, enum sconosciuti, date con 0/3/6 decimali, tutti gli slug problem+json, retry, header,
  parti multipart ispezionate via `MultipartFormDataContent`).
- **Architettura** (senza NetArchTest): `Assembly.GetReferencedAssemblies()` con allowlist per layer (Application non
  referenzia `System.Net.Http` ne' `Microsoft.Extensions.*`), scansione dei csproj, superficie pubblica di
  Infrastructure == `{FilemasterOptions, FilemasterRetryOptions, FilemasterHttp}`, e nessun setter `init` (modreq
  `IsExternalInit`) sui tipi di input (compatibilita' C# 7.3).
- **Integration** (`tests/Filemaster.IntegrationTests`): server HTTP "loopback" su `TcpListener` (~150 righe; puo' troncare,
  resettare, usare chunked; gira anche su net48): troncamento download, reset su upload eccessivo, Range 206/200,
  connessioni concorrenti, timeout, nomi file non-ASCII, memoria. **Live** (opt-in `FILEMASTER_E2E_URL`/`_KEY`,
  `Assert.SkipWhen`): round-trip upload/get/contenuto/verify/dedup/delete, cartelle, contratto errori (401/403/404/415),
  paginazione `limit=1`, contatti, `ContractDriftTests`.
- **Locale su Mac**: `dotnet test --project tests/Filemaster.UnitTests -f net10.0 -c Release` e `-f net8.0` (dal 2026-10-01
  il runtime 8.0.31 e' installato in locale; il container `runtime:8.0` resta solo per il replay in CI); asset ns2.0 su Linux/Mac con configurazione di test
  `SetTargetFramework=netstandard2.0`; replay autorevole = `eng/docker-replay.sh` (clone pulito in `sdk:10.0`, digest da
  `docker buildx imagetools inspect`). Live e2e locale = best effort (SQL Server e' amd64: emulazione).

## Task (diventano `Filemaster/tasks/todo.md`; subagent per task, uno per compito)

**Fase 0 — spike (sequenziale, blocca tutto)**
- [x] **T0.1** *(eseguito il 2026-10-01; esiti e **correzioni al piano** in `tasks/lessons.md` -> "T0.1"; da ririprovare
  nel repo vero in T1.2)* Spike nello scratchpad (non nel repo): libreria `netstandard2.0;net8.0;net10.0` + test xunit.v3 con
  `global.json` 10.0.103 + MTP. Verifica: `dotnet test --project X -f net10.0`; restore con warning-as-errors e
  `latest-recommended` (NU1510, NETSDK1138, NU190x); PolySharp + `InternalsVisibleTo` (CS0436); `SetTargetFramework`;
  range esatti nel nuspec; dll di test sul runtime 8 in container; `dotnet list package --vulnerable`; coverage su net48.
  Il progetto net48 di prova usa il `LangVersion` di default e consuma una classe con `get; set;` e un record di output.
  Esito + comandi esatti in `tasks/lessons.md`.
- [x] **T0.3** *(eseguito il 2026-10-01: server vero funzionante, 224 richieste catturate su `dev`; esiti, divergenze dal contratto e
  **decisione "il client mira a Sharp-a-File `dev` 8aec8bb"** in `tasks/lessons.md` -> "T0.3")* Spike del **server vero** (e' sul percorso critico: da lui dipendono le fixture, quindi il livello wire):
  costruire Sharp-a-File dal suo `Dockerfile`/compose (`--platform linux/amd64`, emulazione su Apple Silicon), `migrate` ->
  `bootstrap-admin` -> `storage-password -generate`, tenant + chiave seminati via SQL, e `curl -H "X-API-Key: ..." /tenant`
  deve dare 200. Se funziona: comandi in `eng/e2e/*` e `tasks/lessons.md`. **Se fallisce** (SQL Server su arm64):
  le fixture si *derivano* da `Api/Dtos.cs` + `DocumentApiTests` del server, marcate come derivate, e si sostituiscono
  alla prima esecuzione di `e2e.yml` su Ubuntu; lo scopro prima di scrivere il livello wire, non dopo.
- [x] **T0.2** Copiare questo piano in `tasks/todo.md`; seminare `tasks/lessons.md` con le lezioni ereditate (SHA per
  comando, scrivere con LF, replay su Linux prima di dire "verificato", "fatto" si verifica sull'artefatto).
  *(fatto: `tasks/todo.md` e `tasks/lessons.md` creati, LF verificato; spike in `$TMPDIR/filemaster-spikes/`)*

**Fase 1 — scheletro**
- [x] **T1.1** File radice: `LICENSE`, props/targets, `.editorconfig`, `.gitattributes`, `.gitignore`, `global.json`, `.slnx`.
  *(fatto 2026-10-01: dallo spike T0.1; differenza funzionale: `tests/Directory.Build.props` accetta `TargetFrameworks` da fuori, come `src/`.)*
- [x] **T1.2** Quattro progetti `src` + due `tests` + test di fumo. Accettazione: build 0 warning, `dotnet format
  --verify-no-changes`, test verdi (net10 locale, net8 in container), `dotnet pack` = 4 nupkg + 4 snupkg,
  `git ls-files --eol` tutto `lf`.
  *(fatto 2026-10-01: build 0 warning, format x2, test net10 + net8 Docker + asset ns2.0, pack 4+4 con range esatti: tutto verificato sul repo.)*

**Fase 2 — codice, in parallelo dopo T1.2** (ogni passo con i suoi test, scritti prima)
- [x] **T2a** *(= parte di T2.1; fatto 2026-10-01)* Domain: `DocumentId`/`ContactId`/`TenantId`, `FolderCode`, 15 eccezioni (`FilemasterException`
  astratta + 14 foglie `sealed`), test in `tests/Filemaster.UnitTests/Domain/`. **Verificato da me sul repo**: build `--no-incremental` 0 avvisi/0 errori,
  176 test su net10 e su net8, integration 4/4, `dotnet format` x2 senza violazioni, nessun CR/TAB/non-ASCII in `src`/`tests`. Prova ns2.0
  (`UseNs20Asset`): 176/176 riportata dal subagent, da rieseguire in T8. Esiti e fatti di contratto in `tasks/lessons.md` 15-17 e "T2a".
- [x] **T2b** *(= resto di T2.1, T2.2, T2.3; fatto 2026-10-01)* Domain: 3 enum (`TenantStatus`, `ContactKind`, `ContactRole`, tutti con `Unknown = 0`), `Document`,
  `DocumentContact`, `Folder`, `Contact`, `ContactCategory`, `Tenant`, `IntegrityCheck`, `BulkVerifyResult`, `ArxivarMetadata` (+ `From(JsonElement)`, ritorna
  null se non valido), `WebhookEvent` astratto + 3 eventi + `UnknownWebhookEvent`. **Verificato da me**: build `--no-incremental` 0 avvisi, **288 test su net10 e su net8**,
  integration 4/4 (net10 e net8), format x2, **asset ns2.0 288/288** (poi albero ripristinato), 51 file `.cs` senza CR/TAB/non-ASCII. Record posizionali ma con
  proprieta' ridichiarate `{ get; } = X` (nessun `init`, lettura da C# 7.3 provata dal subagent con uno spike fuori repo). Test di forma per reflection (niente
  setter/`init`, `Unknown` unico 0, ogni membro pubblico con `<summary>` nel file XML). **Fonte del contratto webhook: solo il codice del server, nessun corpo
  catturato** (e nessun contatto con dati nelle fixture): le forme di `Contact`, `DocumentContact`, `ContactCategory`, della busta webhook e di `occurred_at` sono
  da confermare con la cattura reale (T6.3 / e2e).
  **Obblighi che il Domain impone a T3/T4** (sono scritti nel doc XML): (1) Infrastructure fa `Clone()` di `Document.Metadata` prima di consegnarlo; (2) un `metadata`
  assente sul filo diventa `{}` (mai `default`; banco di prova: fixture 49 e 99); (3) Infrastructure ignora `has_content` (`HasContent` e' derivato da `Sha256`);
  (4) un `contacts` assente si passa come null (diventa lista vuota); (5) il parser T3.3 fa `Clone()` di `UnknownWebhookEvent.Payload`; (6) **ogni adapter rifiuta
  `IsEmpty` con `ArgumentException`** (lezione 16); (7) il mapping `event` -> tipo CLR e' del parser (il Domain non ha stringhe wire).
  **Decisioni mie, prese ora** (le segnalo a fine fase): `ContactCategory.Id`/`Contact.CategoryId` e `WebhookEvent.DeliveryId` restano `string` (nessun nuovo tipo forte non
  pianificato); **un evento di tipo noto con payload malformato diventa `UnknownWebhookEvent`** col payload grezzo (coerente con "gli sconosciuti non lanciano mai",
  e un 500 del ricevitore innescherebbe ritentativi inutili del server: la consegna e' "almeno una volta"); `Deduplicated` vive in `UploadResult` (T3), non in `Document`.
- [x] **T3.1** *(fatto 2026-10-02; il subagent era caduto per rate limit a meta': ho ispezionato lo stato parziale, trovato completo salvo i test di forma, e li ho scritti io)*
  Application: 5 porte (`IDocumentStore` 10 metodi, `IFolderCatalog` 4, `IContactDirectory` 3, `ITenantInfo` 1, `IFilemasterHealth` 2), facciata `IFilemasterClient`
  (5 proprieta', non eredita le porte, non e' `IDisposable`), input a costruttore + `set` con `Validate()` (`UploadDocumentRequest`, `DocumentQuery`, `ContactQuery`,
  `CreateFolderRequest`, `UpdateFolderRequest`), output `Page<T>`, `PageRequest`, `ByteRange`, `ContentRange`, `UploadResult`, `DocumentContent : IDisposable`,
  `HealthProbeResult`, `RequestChecks` interno. **Verificato da me**: build `--no-incremental` 0 avvisi su 3 TFM, **478 test su net10 e net8**, integration 4/4,
  format x2, **asset ns2.0 478/478**, nessun CR/TAB/non-ASCII. **Limiti riletti contro il server @8aec8bb** (non fidandomi del brief): owner/tag 255 caratteri dopo il trim
  (`Validation.MaxLength`), campi multipart 4096 byte con confronto `>` (`ReadLimitedAsync`), metadati 64 KiB (`DocumentMetadata.ParseObject`), filtro 64 nodi / 16 livelli,
  pagina default 50 / max 200, PATCH cartella `{id,name}` con "almeno uno". **Controllo di mutazione**: 3 violazioni di forma (token senza default, `init`, `System.Net.Http`)
  prese ciascuna dal suo test; 6 mutazioni dei limiti (255->256, 4096->4097, nodi 64->65, limit 200->201, profondita' 16->17, `>`->`>=` sui 64 KiB) prese da 1-6 test ciascuna.
  Nuovo `tests/Filemaster.UnitTests/Application/ApplicationShapeTests.cs`: niente `init`/`required`; setter pubblici solo sui 5 input (classi sealed con `Validate()`);
  ogni metodo delle porte e' `Task` + `...Async` + `CancellationToken` finale con default; la facciata espone le 5 porte e basta; Application non referenzia `System.Net.Http`,
  `Microsoft.Extensions.*` ne' i livelli sopra; ogni tipo/proprieta'/campo/metodo con `<summary>` nel file XML.
  **Non verificato**: l'uso reale da C# 7.3 / net48 (lo prova T5.5 sul pacchetto). **Obblighi per T3.2/T4**: (1) l'adapter chiama `Validate()` **prima** di aprire la connessione e
  prima di leggere lo stream; (2) l'adapter manda ogni testo cosi' com'e' (il limite e' misurato sul valore dell'utente); (3) `OpenContentAsync` con 416 -> `UnexpectedResponseException`
  (`StatusCode` 416); (4) `DocumentContent` riceve la risposta HTTP come `owner`; (5) `HealthProbeResult` per 200 **e** 503 (il 503 di `/readyz` non e' problem+json);
  (6) le scritture e le verifiche non si ritentano, solo i GET; (7) i nomi dei parametri sono quelli canonici (`filename`, `sender`...), non gli alias italiani del server.
  **Confronti col server chiusi dopo il parere (2026-10-02), per la serializzazione della query string in T4.3**: `kind` sul filo e' `external|user|group`, confronto esatto
  (`Wire<ContactKind>`, ordinale, minuscolo); `category_id` passa da `FolderCodes.Optional` (stessa regola di `FolderCode`); `q` e' un testo trimmato, vuoto = assente;
  `created_from`/`created_to` accettano **solo** `yyyy-MM-dd` o un istante RFC 3339 **con fuso** (`...Z` o `...+02:00`; un istante senza fuso e' 400): l'adapter serializza
  il `DateTimeOffset` in uno di quei due formati con `CultureInfo.InvariantCulture`, mai `ToString()`; elenco cartelle = `GET /folders?parent_id=` (non paginato, `{items:[...]}`);
  categorie = `GET /contact-categories` (non paginato). Resta **non verificato** il corpo JSON dei contatti (nessuna fixture reale: T6.3/e2e).
- [x] **T3.3** *(fatto 2026-10-02, subagent; verificato da me)* `WebhookSignatureVerifier` + `WebhookSignatureResult`/`WebhookSignatureFailure` + `WebhookHeaders` + `WebhookEventParser` in
  `src/Filemaster.Application/Webhooks/`, 273 test nuovi in `tests/Filemaster.UnitTests/Application/Webhooks/`. **Verificato da me sull'albero**: build `--no-incremental` 0 avvisi, **751 test su net10 e net8**,
  integration 4/4, format x2, **asset ns2.0 751/751**, pack 4+4 + `verify-packages.sh` ok, nuspec di Application: `Microsoft.Bcl.*` solo nel gruppo netstandard2.0 (8.0.0 / 8.0.1), nessun CR/TAB/non-ASCII.
  Letto per intero il codice del verificatore: tutti i candidati `v1` confrontati senza uscita anticipata, firma prima della finestra, limiti con somme (nessun overflow con `t` vicino a `long.MaxValue`), parsing ASCII a mano.
  **Mutazione**: il subagent ha fatto 35 mutanti (33 presi, 1 equivalente, 1 non misurabile: i tempi del confronto costante); io ne ho rifatti 2 (`>`->`>=` sul futuro: preso da 4 test; uscita al primo match: equivalente, non preso, come dichiarato).
  Vettori di firma da **openssl** su file (non circolari), segreto fittizio `whsec_NotARealSecretTestVectorOnly0123`; corpi con ASCII, UTF-8 grezzo con CRLF, byte non UTF-8, vuoto, BOM.
  **Fatti letti dal server**: payload `document.uploaded` = `{document_id, filename, sha256, deduplicated}` (**`filename`**, non `original_filename` come l'API), `document.deleted` = `{document_id, sha256}` (sha256 anche null),
  `document.integrity_failed` = `{document_id, sha256, detail}` (detail anche null); payload con `JsonSerializerDefaults.Web` (snake_case), null non omessi; corpi veri ASCII puro (encoder STJ di default); `occurred_at` UTC con `Z`, 0-6 decimali.
  **Decisioni del subagent che accetto (da segnalare a fine fase)**: (1) `TryParse(null)` lancia `ArgumentNullException` (il brief diceva "mai eccezione": un body null e' un errore di programmazione, come in `Verify`; body vuoto/non valido -> false);
  (2) segreto di soli spazi rifiutato, mai trimmato; (3) header null/vuoto/solo spazi = `MissingHeader`, elemento vuoto/virgola finale/senza `=` = `MalformedHeader`; (4) il messaggio firmato usa il testo **grezzo** di `t` (`t=01700000000` non e'
  `t=1700000000`); (5) tolleranza a secondi interi (500 ms vale 0 s ma e' positiva); (6) chiavi dell'header case-sensitive, spazi solo ai bordi dell'elemento (SP/HTAB), non attorno a `=`; (7) **BOM UTF-8 tollerato** dal parser (STJ lo rifiuta);
  (8) **`occurred_at` senza `Z`/offset = busta non conforme** (STJ lo leggerebbe come ora locale della macchina: misurato +02:00); (9) `delivery_id` vuoto/solo spazi = busta non conforme, `payload` assente accettato (`Undefined`);
  (10) per gli eventi noti **anche un campo nullable assente e' malformato -> `UnknownWebhookEvent`**; `null` esplicito ammesso solo per `deleted.sha256` e `integrity_failed.detail`; `uploaded.sha256` null e' malformato; campi extra ignorati;
  (11) `UnknownWebhookEvent.EventType` per un noto malformato e' il nome noto; (12) UTF-8 non valido/surrogati isolati: busta -> false/`FormatException`, dentro il payload di un noto -> `Unknown`; (13) nessun overload `string` per il corpo.
  **Non verificato**: nessun corpo catturato dal server vero; confronto a tempo costante non misurabile; net48 solo compilato (0 avvisi), non eseguito; STJ 8.0.5 del pacchetto su net48 non provato; mutazioni solo su net10.
- [x] **T3.2** *(fatto 2026-10-02, subagent; verificato da me)* Estensioni dell'Application: `Common/Pagination.cs` (internal: ciclo condiviso), `Documents/DocumentStoreExtensions.cs` (`EnumerateAsync`,
  `FindByArxivarDocnumberAsync`, `OpenVerifiedContentAsync`), `Documents/DocumentExtensions.cs` (`GetArxivarMetadata`), `Documents/VerifiedContentStream.cs`, `Contacts/ContactDirectoryExtensions.cs` (`EnumerateAsync`);
  197 test nuovi + supporti (`FakeDocumentStore`, `FakeContactDirectory`, `ScriptedStream`, `PagedScript`, `TestData`); `ApplicationShapeTests` esteso (setter ereditati da `Stream` non contano; nuova regola
  sui metodi statici async: `CancellationToken` finale con default). **Verificato da me sull'albero**: build `--no-incremental` 0 avvisi, **948 test su net10 e net8**, integration 4/4, format x2,
  **asset ns2.0 948/948**, pack 4+4 + `verify-packages.sh` ok, nuspec di Application invariato (Bcl.* solo nel gruppo netstandard2.0), 51 file `.cs` senza CR/TAB/non-ASCII. Letto il codice di paginazione,
  estensioni e `VerifiedContentStream`. **Mutazione del subagent**: 62 mutanti su net10 + 4 su ns2.0, 61 presi dai test, 1 dal compilatore (CS1591), nessuno sopravvissuto (tabella in scratchpad `mut32/FINAL-results-62.txt`).
  **Decisioni del subagent che accetto (da segnalare a fine fase)**: `docnumber <= 0` rifiutato con `ArgumentOutOfRangeException` (il server importa solo `DOCNUMBER > 0`, ma il suo filtro non valida il segno: rifiutare ora e' reversibile,
  accettare no; `ArxivarMetadata.From` in lettura resta tollerante); `FindByArxivarDocnumberAsync` legge a pagine da 200 e restituisce **lista**; cursore ripetuto/gia' visto/**vuoto** -> `UnexpectedResponseException` (status 200) *dopo* gli
  elementi della pagina e senza altre richieste; validazione eager solo per `EnumerateAsync` (le altre estensioni async lanciano dentro il Task, come gli adapter), annullamento controllato prima di ogni pagina; `VerifiedContentStream`: una
  lunghezza diversa e' verdetto negativo anche con hash giusto (`isTruncated` solo se meno byte), il rilancio dopo il verdetto e' un'eccezione nuova con la prima come `InnerException`, un errore dello stream interno non e' un verdetto,
  dispose anticipato = nessun verdetto; `OpenVerifiedContentAsync` usa `leaveOpen: true` con `owner: original` (lo stream si smaltisce una volta, poi la risposta HTTP) e `expectedLength = document.SizeBytes`; risposta parziale ->
  `UnexpectedResponseException` con status **206**.
  **Non verificato**: net48 solo compilato; uso reale da C# 7.3 (T5.5); nessun test contro un server vero (nessuna fixture di `/documents/{id}/content`: T4/T6); thread concorrenti non provati (lo stream e' documentato non thread-safe);
  confronto a tempo costante non misurabile.
- [ ] **Fatto nuovo (2026-10-02): il repo `Filemaster` ora ha il commit `init` (9997cfb) e `main` e' allineato a `origin/main` (github.com/Vinello28/Filemaster).** Non l'ho fatto io (nessun `git` in scrittura da parte mia ne' dei
  subagent): l'ha creato e pubblicato l'utente tra due mie verifiche. Controllato: 103 file tracciati, **nessun segreto** (`.scratch/` e' ignorato, nessuna fixture/chiave tracciata). Il commit contiene T1-T3.1 e le prove di T5; le modifiche successive
  (T3.1 test di forma, T3.2, T3.3, todo/lessons) sono non committate. Conseguenze: la CI di GitHub puo' essere gia' partita su `init` ed essere **rossa per `pack-smoke`** (manca `eng/pack-smoke/run.sh`, `# TODO(T5.5)`, noto): non posso leggerla da qui
  (`gh` non installato). Il primo segnale reale su Windows/net48 verra' da li'.
- [ ] **Ordine deciso con il parere (2026-10-02): T3.3 -> T3.2 -> T4.x, tutto in SERIE** (Infrastructure compila Application: anche cartelle "diverse" si pestano, lezione 14), **un subagent per
  compito, ogni brief con punti di controllo verdi e nota di avanzamento nello scratchpad** (un 429 deve lasciare un albero ripartibile, lezione 19).
  Ogni compito di codice finisce con `CI=true dotnet pack` + `eng/verify-packages.sh` + lettura dei gruppi di dipendenze del nuspec.
  **Dipendenze di pacchetto**: `Microsoft.Bcl.AsyncInterfaces` (per `IAsyncEnumerable`) e `Microsoft.Bcl.TimeProvider` sono GIA' su Application (solo ns2.0) e in `expected_external()`:
  verificato 2026-10-02, nessuna modifica attesa; compaiono solo nel gruppo netstandard2.0 del nuspec, con floor 8.0.x (ignore di dependabot). Il test di forma non le controlla: le controlla il verify.
  **Divisione dei compiti sugli stream (da scrivere in entrambi i brief)**: T3.2 `VerifiedContentStream` possiede la **verifica dello SHA-256**: solo contenuto intero (rifiuta un `ByteRange`),
  verdetto solo a EOF, dispose anticipato = nessun verdetto e nessuna eccezione. T4 possiede il **rilevamento del troncamento**: un wrapper conta i byte contro `Content-Length` (non ci si fida del
  gestore HTTP, che su net48 e .NET moderno non si comporta allo stesso modo su un EOF prematuro) e lancia `ContentIntegrityException(isTruncated: true)`.
- [x] **T4.1** *(fatto 2026-10-02, subagent caduto per 429 dopo il punto di controllo 4, ripreso e **verificato da me**)* Trasporto dell'Infrastructure:
  `FilemasterOptions` (`BaseAddress`, `ApiKey`, `RequestTimeout` 30 s, `TransferTimeout` 30 min, `Retry`; `Validate()` non muta `BaseAddress`, `ToString` nasconde la chiave) e
  `FilemasterRetryOptions` (`MaxAttempts` 3, 1..10; `InitialDelay` 500 ms; `MaxDelay` 10 s), `Errors/{ProblemBody,ProblemMapper}` (slug prima, status ripiego, 415 sempre per status),
  `Transport/{FilemasterTransport,DownloadStream,Deadline,RetryPolicy,BodyReading,NetworkFailures,TransportRequest,TransportResponse,TransportHooks}` (tutto internal; tre modalita'
  `SendBufferedAsync`/`SendDownloadAsync`/`SendUploadAsync`). **Prova**: restore + build `--no-incremental` 0 avvisi; **1544 unit** (net10, net8, asset ns2.0) + 4 integration verdi;
  format x2 exit 0; 31 file `.cs` senza CR/TAB/non-ASCII; pack 4+4 + `verify-packages.sh` ok, nuspec Infrastructure: Bcl.* solo nel gruppo ns2.0 (8.0.0 / 8.0.1), Logging.Abstractions 8.0.3.
  **Mutazioni** (39 mutanti su copia del repo, lezione 31): tutte prese tranne **M30** (`?`/`#` vuoti nell'indirizzo base: equivalente su .NET 10, dove `Uri.Query` restituisce `?` anche vuota; il test
  c'e' e conta su net48, **non verificato in locale**); 3 fermate dal compilatore (M15, M22, M23) e rifatte in altra forma (M15b, M22b, M23b: prese); M24 e M35 NON prese al primo giro -> aggiunti 2 test, poi prese
  (`A_response_request_id_that_is_too_long...`, `A_download_that_is_not_a_GET_is_never_retried...`). M10, M16, M33, M34 mandavano **in stallo** sei test (lettura mai rilasciata, nessun limite): **corretto** con un limite di 10 s dentro `HangingStream` (e `ReadStarted` via `Waiting.Within`, lezione 34); rilanciati, falliscono in pochi secondi senza watchdog. Test di superficie pubblica provato con M36/M37 (`RetryPolicy`, `ProblemMapper` resi `public`): preso da `Every_public_type_of_the_assembly_is_one_of_the_allowed_ones...`. Il subagent aveva aggiunto 3 test dopo l'ultimo punto di controllo (1539 -> 1542, `TransportTimeoutTests`, corpi di errore che si fermano): riletti, asserzioni forti. Tutto verificato di nuovo dopo l'ultima modifica: build `--no-incremental` 0 avvisi, 1544 unit su net10/net8/ns2.0, 4 integration, format x2, ASCII.
  **Decisioni del subagent da riportare**: `RequestTimeout` = UNA scadenza per l'intera chiamata (tentativi e attese compresi); un timeout del client NON si ritenta; `Retry-After` rispettato fino a
  `MaxDelay`; il trasporto copia le opzioni nel costruttore; qualunque cancellazione diversa dal token del chiamante e' `FilemasterTimeoutException` (anche `HttpClient.Timeout` finito); corpo di
  errore letto al massimo 16 KiB; slug `error` con 5xx -> `ServerErrorException`, con altri status -> `UnexpectedResponseException`; `X-Request-ID` e' lo stesso per tutti i tentativi (32 esadecimali).
  **Obblighi per T4.3/T4.4**: l'`HttpClient` del trasporto deve avere `Timeout` infinito; il trasporto smaltisce il messaggio => uno `StreamContent` chiuderebbe lo stream dell'utente (serve un
  contenuto che non lo chiude); `AllowAutoRedirect=false` nella factory (la chiave `X-API-Key` si copia nei redirect); **non** abilitare `AutomaticDecompression` (il conteggio dei byte contro
  `Content-Length` presuppone corpo non compresso); su net48 `HttpClientHandler` puo' bufferizzare l'upload (`AllowWriteStreamBuffering`) = "non verificato in locale, Windows CI/T6.1";
  la superficie pubblica ora e' solo `{FilemasterOptions, FilemasterRetryOptions}` (`FilemasterHttp` arriva in T4.4: `TODO(T4.4)` nel test di forma).
  **Non verificato**: net48, comportamento di `DownloadStream` col vero gestore HTTP (i test usano un gestore finto), il 413 con connessione chiusa a meta' upload (puo' arrivare come
  `ConnectionException`: documentato), nessun 429 reale (il server non ne emette sulle API: i test 429 sono derivati).
- [x] **T4.2** *(fatto 2026-10-02, subagent `a412a3bd`, **verificato da me**)* Livello wire dell'Infrastructure, tutto internal, in `src/Filemaster.Infrastructure/Wire/` (17 file): lettura manuale con
  `JsonElement` + scrittura con `Utf8JsonWriter` (niente DTO, niente reflection, nessuna `JsonSerializerOptions`; scelta documentata in `WireJson.cs`). Lettori `Folder`, `Tenant`, `Document` (+ contatti,
  `UploadResult`), `IntegrityCheck`/`BulkVerifyResult`, `Contact`, `ContactCategory`, `Page<T>`, `HealthWire` (aggiunto, non era nel brief); costruttori di query (`DocumentQuery`/`ContactQuery`/cartelle +
  `PageRequest`), corpi JSON (crea/aggiorna cartella, move, move/verify in blocco), campi dell'upload, `Routes`; `ContentDispositionHeader` (lettura `filename*` > `filename`; scrittura con ripiego ASCII +
  `filename*=utf-8''`), `ContentRangeHeader` (rigido), `DownloadHeaders`. I lettori prendono `(byte[]? body, WireContext)`; ogni risposta non interpretabile e' `UnexpectedResponseException` con lo status vero
  (mai `JsonException`/`InvalidOperationException`/`KeyNotFoundException`; UTF-8 non valido rifiutato all'ingresso). Fixture: `tests/Filemaster.UnitTests/Wire/Fixtures/` (`captured/` 174 file scrubbati da
  `dev` 8aec8bb, `derived/` 8 file, `README.md`, `INDEX.tsv`; il csproj dei test le copia accanto alla DLL). **Prova (io, sull'albero vero)**: restore + build `--no-incremental` 0 avvisi; **2400 unit** (net10,
  net8, asset ns2.0) + 4 integration verdi; format x2 exit 0; 169 `.cs` senza CR/TAB/non-ASCII; pack 4+4 + `verify-packages.sh` ok; `grep -rIFl -f secret-literals.txt` sul repo: **nessun risultato**; `whsec_`/`saf_`
  nelle fixture solo i due valori finti del README; nessun file ignorato da git in `Wire/` (205 file); il test di superficie pubblica di T4.1 e' rimasto verde senza modifiche (nessun tipo wire e' pubblico:
  `WireShapeTests`). Conteggio: 1544 -> 2387 (+843 del subagent) -> 2400 (+13 miei, vedi sotto).
  **Mutazioni** (subagent, su copia, net10, watchdog): 69 mutanti, 67 presi, W11 fermato dal compilatore e rifatto (W11b, preso), **W56 NON preso ed equivalente** (`JsonDocument` non smaltito: una perdita non e'
  osservabile da un test). **W07b** (`Z` letta senza `AssumeUniversal`) e' preso solo perche' la macchina e' in CEST: **in UTC sarebbe equivalente** (CI Ubuntu non lo vedrebbe; lezione 32).
  **Lacuna del Domain trovata dal wire e CHIUSA da me**: `ArxivarMetadata.From` prometteva "non lancia mai" ma `GetString` e `TryGetProperty` lanciano `InvalidOperationException` su un surrogato isolato
  (`"a\ud800"`, JSON valido che il server conserva) in un valore o in un NOME di proprieta'. Ora le ricerche passano da `TryGetProperty`/`ReadString` privati che trattano "illeggibile" come "assente"; 2 test
  nuovi (1 con asserzione esatta sui valori, 1 Theory da 12 casi sui nomi: contratto = non lancia, mai un docnumber diverso). Mutanti D1-D5 su copia: D1, D2, D5 presi subito; **D3 e D4 (ricerche di `arxivar`
  e `docnumber` non protette) NON presi al primo giro** perche' il confronto di STJ procede carattere per carattere e parte dall'ULTIMA proprieta': serve un nome cattivo che venga DOPO quello cercato e che
  inizi col surrogato -> 2 casi aggiunti, poi presi (lezione 35).
  **Decisioni del subagent da riportare**: `sha256` deve essere 64 cifre hex minuscole; `size_bytes` non negativo; `next_cursor` presente e vuoto = errore; `deduplicated` obbligatorio solo nella risposta di un
  upload; `metadata` non oggetto = errore, `null`/assente = `{}`; `has_content` ignorato anche col tipo sbagliato; enum assente o nuovo = `Unknown`, tipo sbagliato = errore; date lette con i due formati esatti del
  server e `AssumeUniversal`, scritte sempre da `UtcDateTime` con la `Z` (`12:00+02:00` esce `...T10%3A00%3A00Z`, mai la forma solo-data); surrogato isolato in query/corpi/campi multipart/nome file =
  `ArgumentException` col nome del parametro (`Uri.EscapeDataString` lo sostituirebbe in silenzio con U+FFFD); valori delle query as-is (vuoto parte, null no, nessun trim), ordine dei parametri stabile, solo nomi
  canonici; radice nei corpi di spostamento = `{"folder_id":null}` esplicito; `Content-Range` rifiuta `*/n`, `a-b/*`, `ultimo >= totale`, overflow, cifre Unicode, e `DownloadHeaders.Read` lo pretende solo con 206
  controllando `Content-Length == ultimo - primo + 1`; `IsHealthy` dipende dallo status HTTP, mai dal testo. **Il brief citava le fixture 80-82 per `created_from`: quelle giuste sono 87-89** (lezione 40).
  **Per T4.3**: usare `Routes.*` (relativi, senza `/` iniziale); corpo JSON con `WireJson.ContentType`; PATCH con `new HttpMethod("PATCH")`; upload = `DocumentWire.UploadFields(request)` + parte file con
  `ContentDispositionHeader.FilePart("file", request.FileName)` via `TryAddWithoutValidation("Content-Disposition", ...)`, file per ULTIMO; download = `DownloadHeaders.Read(downloadResponse).ToContent(stream, owner)`.
  Un 503 di `/readyz` non JSON esce da `HealthWire` come `UnexpectedResponseException(503)`: la mappatura finale (es. `ServerErrorException`) la decide **T4.3b**.
  **Non verificato**: net48 (solo compilato: ne' i test ne' `EscapeDataString` oltre 65.519 caratteri ne' `HttpContentHeaders` reale); mutazioni solo su net10 (non su net8/ns2.0); nessuna richiesta provata contro un
  server vero (confrontate con i corpi e i percorsi che il server ha accettato nelle catture 21, 22, 28, 44, 54, 129, 133, 134, 136, 138); **limite di 16 KiB del `MultipartReader` del server sulle intestazioni di
  parte**: un `FileName` molto lungo (percent-codificato) potrebbe superarlo e l'Application non limita `FileName`; **fixture derivate, non catturate** (contatti, categorie, documento senza contenuto, contatti su un
  documento, buste webhook, `metadata` assente) da sostituire con le catture di T6.3; fixture di `dev` catturate su Azure SQL Edge: ricatturare su SQL Server 2022.
- [ ] **T4.3a..T4.4** *(T4.2 e' fatto, voce sopra)* **T4.3a** adapter documenti -> **T4.3b** adapter cartelle/contatti/tenant/health -> **T4.4** root `Filemaster`
  (almeno quattro lanci, ciascuno con i suoi punti di controllo). **Decisioni di T4.1 da scrivere nel brief, non rimandare a T6.1**: il corpo dell'upload e' in streaming senza buffering anche
  su net48 (provabile solo su Windows CI: "non verificato in locale"); i download usano `ResponseHeadersRead`; il retry vale solo per i GET e solo **prima** di consegnare la risposta
  al chiamante (mai su uno stream gia' restituito, mai su un 503 di `/readyz`, come promette il doc di `IFilemasterHealth`); il **test di architettura sulla superficie pubblica di Infrastructure**
  (solo `FilemasterOptions`, `FilemasterRetryOptions`, `FilemasterHttp`) si scrive in T4.1, non dopo, altrimenti tre compiti possono far trapelare tipi pubblici.
  **T4.2** *(soddisfatto)*: "fatto" = copie scrubbate in `tests/` + `grep` su `tests/` senza risultati per prefissi di chiave API, `whsec_`, password, slug dell'ente e hostname reali (le sorgenti in
  `.scratch/spikes-e2e/` contengono segreti e restano fuori).
- [x] **T5.1** `ci.yml`, **T5.2** `release.yml`, **T5.4** `dependabot.yml` *(fatti 2026-10-01 da subagent senza `dotnet` sul repo; **verificato da me**:
  actionlint `rhysd/actionlint@sha256:b1934ee5...` rc=0 su entrambi i workflow, i 6 SHA riletti con `git ls-remote` (checkout, setup-dotnet,
  softprops identici a Sharp-a-File), lettura integrale di `release.yml`/`ci.yml`/`dependabot.yml`. **Non verificato**: esecuzione reale su GitHub,
  replay Linux in container (T8.1), net48, scambio OIDC con nuget.org.)* Caveat: **la CI non e' verde al primo push finche' non esiste T5.5**
  (il job `pack-smoke` chiama `eng/pack-smoke/run.sh`, `# TODO(T5.5)`); il test di memoria 200 MB e' tenuto verde con `--ignore-exit-code 8`
  (`# TODO(T6.1)`: a test scritto, togliere il flag e mettere `--minimum-expected-tests 1`). Il piano diceva "il commit del tag deve *discendere*
  da `origin/main`": alla lettera e' l'opposto; implementato "raggiungibile da `origin/main`" (`git merge-base --is-ancestor HEAD origin/main`).
- [x] **T5.3** `eng/verify-packages.sh [--require-commit] <cartella> <versione>` + `eng/docker-replay.sh [--src <cartella>] [--keep]` *(fatti 2026-10-01;
  **verificato da me**: shellcheck `koalaman/shellcheck@sha256:bb596a0d...` rc=0; i 3 digest nello script (sdk:10.0, runtime:8.0, shellcheck) ririsolti con
  `docker buildx imagetools inspect` = uguali; `verify-packages.sh` passa sui 4+4 pacchetti veri, fallisce su copia con range `>=` e su `--require-commit` senza
  remote (il subagent riporta 27 manomissioni su 27 rilevate, non ririeseguite da me); replay Linux su `snapshot-skeleton`: 10/10 passi, 20 s, albero
  vivo intatto. **Non verificato**: host amd64, albero vivo con T2b in corso.)* **Per T4**: `expected_external()` in cima a `verify-packages.sh` e' un insieme
  esatto di dipendenze esterne per pacchetto e TFM: ogni nuova `PackageReference` diretta in `src/` lo fa fallire finche' non si aggiorna (voluto).
  Il replay segue `ci.yml` (UnitTests + IntegrationTests su net8 e net10 con copertura): se `ci.yml` cambia, riallinearlo a mano.
- [ ] **T5.5** `eng/pack-smoke/run.sh <cartella-pacchetti> <versione>` + progetto console usa-e-getta (net8/net10, net48 su Windows) che installa
  i nupkg da un feed locale e chiama un server finto; il consumatore net48 NON sovrascrive `LangVersion` e costruisce davvero
  `UploadDocumentRequest`/`DocumentQuery` (serve T4 + T6.1). Referenziato da `ci.yml` (job `pack-smoke`) con `# TODO(T5.5)`.
- [ ] **T6.1** Server loopback, **T6.2** harness e2e (`eng/e2e/*`, `e2e.yml`, riusa il T0.3), **T6.3** cattura fixture dal
  server reale (`eng/e2e/capture-fixtures.sh`, con scrubbing di chiavi ed email).
- [ ] **T7.1** Docs: README (it), `docs/architecture.md`, `docs/publishing.md` (la tua checklist), `docs/api-contract.md`
  (stranezze: 404 su id malformato, body rigidi, 409 ambiguo, validazione tardiva dell'upload).

**Fase B — completamento API** (dopo che la A e' verde): audit, account, API key, webhook admin, import/export ZIP,
`IApiKeyProvider`. Si puo' tagliare dal primo rilascio se preferisci.

**Fase 8 — verifica finale (nessun "fatto" prima di questa)**
- [ ] **T8.1** Replay in clone pulito dentro `sdk:10.0`; **T8.2** ispezione dei nupkg (3 TFM, range, metadata);
  **T8.3** `PackSmoke` su net8/net10 (net48 in CI Windows); **T8.4** `actionlint` (Docker, immagine per digest) sui
  workflow; **T8.5** una esecuzione live contro il Sharp-a-File reale (locale con emulazione, oppure `e2e.yml` dopo il
  tuo push); **T8.6** a te: tag `v0.1.0-rc.1`.

## Rischi (in ordine) e come ridurli

1. **Trusted Publishing con ID nuovi alla prima pubblicazione** -> rc come prova generale; piano B con API key a scadenza breve.
2. **Deriva di versione tra i 4 pacchetti** -> range esatti + asserzione nel nuspec; piano B nuspec controllato.
3. **ns2.0 su .NET Framework** (binding redirect, buffering, 2 connessioni) -> job Windows net48, test memoria 200 MB, handler iniettabile.
4. **Deriva dell'ambiente** (NETSDK1138 dal 10/11/2026, NU1510, CS0436, SDK 10.0.103 vs 10.0.401) -> spike T0.1.
5. **MTP + xunit.v3 su net48 / coverage su ns2.0** -> piano B: copertura solo sul job net10 Linux.
6. **Deriva del contratto** (niente OpenAPI, README del server obsoleto: `fld_`, errori in italiano, 409 ambiguo) -> fixture golden
   + `ContractDriftTests` + `docs/api-contract.md`.
7. **Streaming** (download troncati, reset su 413, nomi non-ASCII) -> suite loopback.
8. **E2E** (SQL Server arm64, ref del server) -> spike T0.3 *prima* del livello wire; Ubuntu CI come primario, ref fissato.
9. **Chi chiama da C# 7.3** non puo' usare `init`/`with` -> input a costruttore + `get; set;`, PackSmoke net48 senza `LangVersion`.
10. **API 0.x**: aggiungere un membro a una porta rompe chi la implementa (ns2.0 senza default interface members) ->
   restare in 0.x; `EnablePackageValidation` dal 1.0.0.

## Verifica end-to-end (come dimostro che funziona)

1. `dotnet build -c Release` senza warning e `dotnet format --verify-no-changes` (T1.2 e a ogni fase).
2. Test unit + architettura su net10 (locale), net8 (runtime in container), ns2.0 (configurazione `SetTargetFramework`).
3. `eng/verify-packages.sh` sui 4 nupkg: cartelle `lib/netstandard2.0|net8.0|net10.0`, range `[x.y.z]`, licenza, readme, snupkg.
4. `PackSmoke` dal feed locale; `eng/docker-replay.sh` da clone pulito; `actionlint` sui due workflow.
5. Suite live contro un Sharp-a-File vero (Docker) — la prova che il client parla davvero col server.
6. Dopo il tuo push: CI verde su GitHub; poi `v0.1.0-rc.1` e controllo su nuget.org (4 pacchetti, snupkg, Release con asset).

## Review

### Stato al 2026-10-01 (lavoro interrotto per limite d'uso)

**Fatto e verificato da me sul repo vero:** Fase 0 (T0.1, T0.2, T0.3) e Fase 1 (T1.1, T1.2). Verifiche rieseguite: `dotnet restore`,
`dotnet format --verify-no-changes` (due passaggi), build Release `--no-incremental` = 0 avvisi / 0 errori, 12 DLL
(4 progetti x 3 TFM), test net10 8 + 4 verdi, `dotnet pack` = 4 nupkg + 4 snupkg con licenza Apache-2.0, 3 cartelle `lib/` ciascuno e
range esatti tra i pacchetti (0 / 3 / 6 / 9). Nessun commit: `git status` mostra solo file non tracciati.

**Da fare, in quest'ordine:** Fase 2 in parallelo -> **T2** Domain (ID tipizzati, `FolderCode`, eccezioni, entita', Arxivar, eventi webhook),
**T5** workflow `ci.yml`/`release.yml`/`dependabot.yml` + `eng/verify-packages.sh`/`docker-replay.sh` (SHA gia' risolti con
`git ls-remote`: upload-artifact v7.0.1 `043fb46d1a93c77aae656e7c1c64a875d1fc6a0a`, download-artifact v8.0.1
`3e5f45b2cfb9172054b4087a40e8e0b5a5461e7c`, NuGet/login v1.2.0 `8d196754b4036150537f80ac539e15c2f1028841`; checkout, setup-dotnet e
softprops si copiano dai workflow di Sharp-a-File; **ririsolvere con un comando prima di scrivere**), **T6.1** server loopback;
poi **T3** Application, **T4** Infrastructure (fixture reali in `$TMPDIR/filemaster-spikes/e2e/out/fixtures/dev/`, da copiare
selezionate nel repo), **T6.2** `eng/e2e/*` + `e2e.yml` (script gia' pronti nello stesso `out/`, pin allo SHA di `dev`), **T7** docs,
Fase B, Fase 8. Gli spike stanno in `$TMPDIR/filemaster-spikes/` (fuori dal repo): se la cartella sparisce, gli esiti restano in
`tasks/lessons.md`, ma gli script e le fixture vanno ricreati.
