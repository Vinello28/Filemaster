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
- [x] **T4.3a** *(fatto 2026-10-02, subagent)* Adapter HTTP dei documenti, tutto internal in `src/Filemaster.Infrastructure/Documents/`: `HttpDocumentStore` (i 10 metodi di
  `IDocumentStore` su `FilemasterTransport` + livello wire) e `UploadStreamContent` (parte file del multipart: scrive lo stream dell'utente dalla posizione corrente, a pezzi da 80 KiB,
  senza bufferizzarlo e **senza chiuderlo**). Test in `tests/Filemaster.UnitTests/Infrastructure/Documents/` (5 file: supporto con parser multipart indipendente e fixture di download dalle
  `.headers` catturate; test JSON, upload, download, argomenti/annullamento). **Prova (sull'albero vero)**: restore + build `--no-incremental` 0 avvisi; `dotnet test -c Release --no-build`
  **5050** (erano 4808: +117 unit per TFM, 2521 su net10 e su net8, + integration); asset ns2.0 2521/2521; format x2 exit 0; pack 4+4 + `verify-packages.sh` ok; 176 `.cs` senza CR/TAB/non-ASCII;
  test di superficie pubblica di Infrastructure verde senza modifiche. **Mutazioni** (38, su copia `mutroot`, net10, watchdog 240 s): run 1 = 36 presi, **M10** (senza intervallo accettato ogni 2xx) e
  **M29** (upload con `SendBufferedAsync`, cioe' `RequestTimeout` al posto di `TransferTimeout`) NON presi -> 5 test nuovi (2xx diversi con e senza intervallo; tempo dell'upload); run 2 = **38/38 presi**,
  nessuno fermato dal compilatore, nessuno stallo. Coperti: ordine delle parti e file per ultimo, `Range` (assente/sbagliato/ignorato), PATCH, metodi di delete/verify, percorsi, stream utente chiuso
  (override di Dispose, `StreamContent`), dispose della risposta su intestazioni illeggibili, status attesi, validazione sincrona vs nel Task, `Accept`/`Content-Type`, tipo del file inventato, lunghezza
  (mai nota, intera, negativa), riavvolgimento, copia in un solo pezzo, token non inoltrato (get/upload/download), campi non UTF-8, `multipart/mixed`, filtri e cartella persi.
  **Decisioni da riportare**: (1) **eccezioni di argomento dentro il Task** (metodi `async`), sempre prima di qualunque richiesta, coerente con T3.2 (eager solo `EnumerateAsync`); (2) upload: lunghezza dichiarata
  (`Length - Position`, minimo 0, fotografata alla creazione del messaggio) solo se lo stream e' `CanSeek`, altrimenti `chunked`; mai letto per misurarlo; (3) parte file senza `Content-Type` se
  `UploadDocumentRequest.ContentType` e' null (il server riconosce il tipo); campi di testo UTF-8 grezzo senza `Content-Type`; `MultipartContent("form-data")` con intestazioni delle parti via
  `TryAddWithoutValidation` (non `MultipartFormDataContent`, che riscrive `Content-Disposition`); (4) `FileName` con surrogato isolato -> `ArgumentException` con `ParamName` `FileName` (non `fileName` di
  `ContentDispositionHeader`); (5) status attesi: JSON = ogni 2xx; contenuto/anteprima senza intervallo **solo 200**, con intervallo 200 o 206; un 206 non chiesto, un 204 o un 416 -> `UnexpectedResponseException`
  con lo status vero (il 416 passa da `ProblemMapper`, gia' allineato al doc della porta); (6) `DocumentContent.owner` = null: il `DownloadStream` possiede gia' la risposta HTTP (smaltire il contenuto la
  rilascia, provato con un contatore); se `DownloadHeaders.Read` lancia si smaltisce lo stream prima di rilanciare; (7) `Accept: application/json` su tutte le chiamate JSON e sull'upload, nessun `Accept`
  sui download; verify singola = `POST` senza corpo. **Contro il server @8aec8bb** (`DocumentEndpoints.cs`): metodi/percorsi/status confermati (201 upload, 204 delete/move, 200 il resto, contenuto e anteprima
  con `enableRangeProcessing`). Le catture del brief 21, 22, 28, 44 sono di **cartelle** (T4.3b), non di documenti; per i documenti valgono 47-54, 70-89, 98, 108-122, 126, 129, 133, 134, 136, 138 (lezione 40).
  Nessuna cattura di `DELETE /documents/{id}` ne' dei corpi dei download (solo intestazioni): i corpi dei test di download sono sintetici.
  **Obblighi per T4.3b/T4.4**: stesso schema (`async` + validazione nel Task, `Accept` JSON, `IsExpectedStatus` esplicito dove serve, 200|503 per `/readyz`); T4.4 costruisce `HttpDocumentStore(transport)` e
  l'`HttpClient` con `Timeout` infinito, `AllowAutoRedirect=false`, niente decompressione; sul gestore net48 valutare `AllowWriteStreamBuffering`.
  **Non verificato**: net48 (solo compilato: buffering dell'upload di `HttpClientHandler`, serializzazione del multipart col gestore vero); nessuna richiesta contro un server vero (il multipart e' confrontato
  con un parser scritto nei test e con i valori della cattura 47, non con il `MultipartReader` di ASP.NET); mutazioni solo su net10; `CreateContentReadStreamAsync` di `UploadStreamContent` resta quello della
  base (bufferizza) ma nessun gestore lo usa per inviare (usano `SerializeToStreamAsync`).
- [x] **T4.3b** *(fatto 2026-10-02, subagent)* Adapter HTTP di cartelle, contatti, tenant e health, tutti `internal sealed` su `FilemasterTransport` + livello wire:
  `Folders/HttpFolderCatalog` (`IFolderCatalog`), `Contacts/HttpContactDirectory` (`IContactDirectory`), `Tenants/HttpTenantInfo` (`ITenantInfo`), `Health/HttpFilemasterHealth` (`IFilemasterHealth`)
  in `src/Filemaster.Infrastructure/`, piu' `Transport/JsonCalls.cs` (invio JSON bufferizzato con `Accept: application/json` e corpo `WireJson.ContentType`) e la proprieta' internal
  `TransportRequest.OmitApiKey` (il trasporto non mette la chiave su nessun tentativo; default false). Test in `tests/Filemaster.UnitTests/Infrastructure/Resources/` (`ResourceRig` sopra lo `StoreRig`
  di T4.3a + 5 file: cartelle 21, contatti 11, tenant 5, health 18, comuni 13 metodi di test) + 2 test di `OmitApiKey` in `TransportCommonTests`.
  **Prova (sull'albero vero)**: restore + build `--no-incremental` 0 avvisi; `dotnet test -c Release --no-build` **5362** (erano 5050: +156 unit per TFM, 2677 su net10 e su net8, + integration);
  asset ns2.0 2677/2677 (poi restore/build normali rifatti); format x2 exit 0; pack 4+4 + `verify-packages.sh` ok; 187 `.cs` senza CR/TAB/non-ASCII.
  **Mutazioni** (37, su copia `mutroot`, net10, watchdog): run 1 = 35/35 presi + **B10** fermato dal compilatore (CS0121, `Map(int, null, ...)` ambiguo) -> rifatto come **B10b** (`(byte[]?)null`);
  run 2 = B10b preso. **Nessun sopravvissuto, nessuno stallo** (B15, JSON con `SendUploadAsync`, preso in 81 s per i Task appesi fino a `Waiting.Within`). Coperti: chiave sulle sonde (e sul retry),
  `OmitApiKey` ignorato/invertito, healthz che accetta ogni 2xx, 503 di readyz non atteso, 503 non-sonda lasciato `UnexpectedResponseException`, request id e corpo persi nel 503 non-sonda,
  `Accept`/`Content-Type`, modalita' del trasporto (upload al posto di bufferizzato), corpo vuoto inventato, PATCH/POST/DELETE scambiati, `ParamName` del wire al posto di quello della porta,
  ordine null/codice nell'update, padre ignorato, update senza codice nel percorso, delete non async, pagina/filtri persi, percorsi e metodi di contatti/tenant, dettaglio letto come pagina.
  **Decisioni da riportare**: (1) **le sonde `/healthz` e `/readyz` non mandano la chiave** (`OmitApiKey`): sul server sono `AllowAnonymous`, ma con un solo schema registrato quello diventa il default e
  gira su ogni richiesta; una chiave presente costa lookup sul DB (`FindByHashAsync`, `tenants.GetAsync`, `TouchLastUsed`) e col DB giu' `/readyz` darebbe 500 invece di 503 (letto dal codice, non misurato:
  lezione 44); le catture 01/02/224 sono senza chiave; (2) `/healthz`: solo **200** con testo `ok` (`Accept: text/plain`), GET ritentabile; un 503 di proxy e' ritentato e alla fine `ServerErrorException`;
  (3) `/readyz`: 200 e 503 sono **esiti** (`IsExpectedStatus` 200||503, mai eccezione, mai retry, `Accept` JSON); un 503 il cui corpo non e' la sonda passa da `ProblemMapper` (`ServerErrorException`,
  o l'eccezione dello slug se c'e', con request id) e non e' ritentato, come dice il doc della porta; 502/504 di proxy ritentati come ogni GET; (4) cartelle: `ListChildrenAsync(null)` = radice,
  `FolderCode` vuoto -> `ArgumentException` con `ParamName` della porta (`id`, `parentId`: lezione 45), 409 = `ConflictException` mai ritentato (POST/PATCH/DELETE mai ritentati); update = PATCH
  fatto a mano su `Routes.Folder(id)`; (5) contatti paginati come i documenti (ordine della query del wire, cursore rimandato tale e quale), categorie = lista; (6) tenant = GET di `tenant` con la chiave
  (sonda di autenticazione; 401/403 non ritentati); (7) eccezioni di argomento dentro il Task, prima di qualunque richiesta, come T4.3a. **Contro il server @8aec8bb**: metodi/percorsi/status delle catture
  21, 22, 24, 26, 28, 44, 46, 150-152, 215, 01-03, 224 (INDEX.tsv riletto, lezione 40); `contacts-page`/`contact-detail`/`contact-categories` sono fixture derivate.
  **Obblighi per T4.4**: costruire i quattro adapter (e `HttpDocumentStore`) sullo **stesso** `FilemasterTransport`; `HttpClient` con `Timeout` infinito, `AllowAutoRedirect=false`, niente decompressione;
  aggiornare `docs/api-contract.md` (qui vietato): la mappatura del 503 non-sonda di `/readyz` risulta ancora "non ancora scritta" e manca l'omissione della chiave sulle sonde.
  Duplicazione nota: `JsonCalls` e il `SendJsonAsync` privato di `HttpDocumentStore` fanno la stessa cosa (T4.3a non toccato; unificabile in T4.4 o dopo).
  **Non verificato**: nessuna richiesta contro un server vero (in particolare il 500 di `/readyz` con chiave e DB giu' e' dedotto dal codice); net48 solo compilato; mutazioni solo su net10.
- [x] **T4.4** *(fatto 2026-10-02, subagent)* Composizione: `FilemasterHttp` (Infrastructure), root `Filemaster` (DI + factory). **API pubblica nuova**:
  `Filemaster.Infrastructure.FilemasterHttp.CreateClient(HttpClient, FilemasterOptions, ILoggerFactory? = null, TimeProvider? = null) -> IFilemasterClient`;
  `Microsoft.Extensions.DependencyInjection.FilemasterServiceCollectionExtensions.AddFilemaster(this IServiceCollection, Action<FilemasterOptions>) -> IHttpClientBuilder` + `const string HttpClientName = "Filemaster"`;
  `Filemaster.FilemasterClientFactory.Create(FilemasterOptions, HttpMessageHandler? = null, ILoggerFactory? = null) -> FilemasterClient`; `Filemaster.FilemasterClient : IFilemasterClient, IDisposable` (costruttore internal).
  Internal: `HttpFilemasterClient` (5 porte su UN trasporto), `FilemasterTransport.FromSource(Func<HttpClient>, ...)` (costruttore privato: un overload accessibile avrebbe reso ambiguo `new FilemasterTransport(null!, ...)` dei test),
  root `FilemasterHandlers` (primario + controllo), `FilemasterOptionsValidator` (`IValidateOptions`), `FilemasterHandlerCheck` (`IHttpMessageHandlerBuilderFilter`). `HttpDocumentStore.SendJsonAsync` ora delega a `JsonCalls.SendAsync`
  (e `JsonCalls.AcceptJson` sull'upload): test di T4.3a invariati e verdi. `InternalsVisibleTo`: Infrastructure -> `Filemaster` (sicuro: i 4 pacchetti si dipendono con `[x.y.z]`), root -> UnitTests.
  **Prova (sull'albero vero)**: restore exit 0; build `--no-incremental` 0 avvisi; `dotnet test -c Release --no-build` **5462** (erano 5362: +48 unit per TFM in `tests/Filemaster.UnitTests/Composition/` (5 file), +2 integration per TFM
  in `GenericHostTests`), exit 0; asset ns2.0 2731/2731 su net10 (unit + integration, poi restore normale rifatto); format x2 exit 0; `CI=true dotnet pack` 4+4 + `verify-packages.sh` exit 0; nuspec di `Filemaster`: 3 gruppi,
  fratelli `[0.0.0-dev]`, unica esterna `Microsoft.Extensions.Http` 8.0.1 (nessuna dipendenza diretta nuova in `src/`: `verify-packages.sh` non toccato); 209 file senza CR/TAB/non-ASCII. Test di superficie: `TODO(T4.4)` tolto,
  ora UGUAGLIANZA `{FilemasterHttp, FilemasterOptions, FilemasterRetryOptions}` (anche `WireShapeTests` aggiornato: elencava due tipi). Test: `Microsoft.Extensions.Hosting` **8.0.1** (ultima 8.0.x dal flat container) solo in
  IntegrationTests; `ServiceCollection` arriva gia' transitivo (M.E.Http -> M.E.Logging -> M.E.DependencyInjection 8.0.1), nessun riferimento aggiunto agli UnitTests.
  **Mutazioni** (34, copia `mutroot44`, net10, unit + integration, watchdog 240 s): 33 prese al primo giro, **N13** (filtro non registrato) fermato da IDE0005 (using rimasto inutile) -> **N13b** in forma che compila, presa.
  Nessun sopravvissuto, nessuno stallo. Coperti: redirect/decompressione/PooledConnectionLifetime del primario, catena di `DelegatingHandler` non discesa, i due controlli del primario, Timeout del client nominato e della
  factory, primario di default, redazione (nessuna, solo chiave, solo Authorization), `ValidateOnStart`, validatore assente/permissivo/su ogni nome, filtro assente/su ogni client/prima delle configurazioni, client e porte
  transient o mancanti, `AddSingleton` al posto di `TryAdd`, client HTTP catturato una volta, `TimeProvider`/`ILoggerFactory` del contenitore ignorati, Timeout finito accettato (DI e `FilemasterHttp`), ordine
  opzioni/Timeout, piu' di un trasporto, categoria del logger, dispose dell'handler (utente smaltito, proprio non smaltito), handler utente non controllato.
  **Decisioni**: (1) **client nominato, non tipizzato**: `IFilemasterClient` e le 5 porte sono **singleton** e il trasporto chiede un `HttpClient` a `IHttpClientFactory` **a ogni tentativo** (sorgente `Func<HttpClient>`):
  iniettabili ovunque, anche in un singleton, senza il problema del typed client transient catturato (gestore mai ruotato); la rotazione della factory (2 min) vale anche su .NET Framework, dove `HttpClientHandler` non segue il DNS;
  (2) **`FilemasterHttp.CreateClient` LANCIA** `ArgumentException` (`ParamName` `httpClient`) se `Timeout` non e' infinito: con il default di 100 s i trasferimenti lunghi fallirebbero solo in esercizio e solo con file grandi; le
  opzioni si controllano prima; il client HTTP non si smaltisce ne' si modifica (provato). In DI un `Timeout` finito messo dall'utente dopo `AddFilemaster` = `InvalidOperationException` alla risoluzione;
  (3) **opzioni**: opzioni nominate `"Filemaster"`, `IValidateOptions` che chiama `Validate()` (messaggi senza chiave, provato) + `ValidateOnStart` -> con l'host generico l'avvio fallisce (`OptionsValidationException`, provato con
  `Host.CreateEmptyApplicationBuilder`), senza host alla prima risoluzione del client o di una porta; lette una volta (nessun ricaricamento); (4) **primario**: `SocketsHttpHandler` (`AllowAutoRedirect=false`, decompressione None,
  `PooledConnectionLifetime` 2 min) su net8/net10, `HttpClientHandler` (stesse regole, `MaxConnectionsPerServer` alzato ad ALMENO 32, mai abbassato) su ns2.0; **un filtro `IHttpMessageHandlerBuilderFilter` rifiuta** un primario
  sostituito dall'utente che segue i redirect o decomprime (`InvalidOperationException`, solo per il client `"Filemaster"`), e la factory rifiuta lo stesso con `ArgumentException` (`handler`), scendendo i `DelegatingHandler`;
  un gestore di altro tipo non e' ispezionabile e passa (documentato; l'asset ns2.0 non conosce `SocketsHttpHandler`); (5) redazione `X-API-Key` e `Authorization` (provata con log Trace in memoria, anche con un `Authorization`
  aggiunto da un gestore dell'utente; le altre intestazioni restano leggibili); (6) **factory**: `FilemasterClient` possiede l'`HttpClient`; il gestore passato dall'utente **non** si smaltisce mai (nessun parametro per cambiarlo:
  lo smaltisce chi l'ha creato), quello della factory si'; un'istanza a vita dell'applicazione (doc con esempio C# 7.3 e VB.NET); (7) `TryAdd*` ovunque: una porta registrata prima di `AddFilemaster` (un fake) resta;
  (8) logger e `TimeProvider` dal contenitore; `Retry.MaxAttempts = 1` per chi aggancia Polly (doc XML di `AddFilemaster`); una seconda `AddFilemaster` aggiunge una `configure` alle stesse opzioni (documentato).
  **Obblighi per T5.5/T6.1**: T5.5: il consumatore net48 usa `FilemasterClientFactory.Create(options)` con le proprieta' (niente `init`), e un consumatore net8 usa `AddFilemaster`; i tipi pubblici del root sono
  `FilemasterClient`, `FilemasterClientFactory`, `FilemasterServiceCollectionExtensions` (namespace `Microsoft.Extensions.DependencyInjection`). T6.1: col server loopback provare il gestore VERO (redirect davvero non seguito,
  `Content-Length` con il primario reale, upload in streaming), `MaxConnectionsPerServer` e `AllowWriteStreamBuffering` su net48 (Windows CI), la rotazione dei gestori durante un download aperto.
  **Non verificato**: net48 (solo l'asset ns2.0 su .NET 10): il minimo di 32 connessioni si vede solo su .NET Framework (su .NET il default e' gia' illimitato: un mutante li' e' equivalente); un download aperto mentre la factory
  ruota e smaltisce il gestore scaduto (si conta sul fatto che la factory smaltisce solo i gestori non piu' raggiungibili: letto, non misurato); nessun gestore vero verso un server (i test usano gestori finti e la pipeline vera della factory);
  mutazioni solo su net10. `docs/api-contract.md` non toccato (vietato qui): resta da scrivere anche la composizione.
  **Decisioni di T4.1 riportate nel brief e rispettate**: upload in streaming (net48 non verificato), download `ResponseHeadersRead`, retry solo GET e prima della consegna, test di superficie scritto in T4.1 (ora uguaglianza).
- [x] **T5.1** `ci.yml`, **T5.2** `release.yml`, **T5.4** `dependabot.yml` *(fatti 2026-10-01 da subagent senza `dotnet` sul repo; **verificato da me**:
  actionlint `rhysd/actionlint@sha256:b1934ee5...` rc=0 su entrambi i workflow, i 6 SHA riletti con `git ls-remote` (checkout, setup-dotnet,
  softprops identici a Sharp-a-File), lettura integrale di `release.yml`/`ci.yml`/`dependabot.yml`. **Non verificato**: esecuzione reale su GitHub,
  replay Linux in container (T8.1), net48, scambio OIDC con nuget.org.)* Caveat: **la CI non e' verde al primo push finche' non esiste T5.5**
  (il job `pack-smoke` chiama `eng/pack-smoke/run.sh`, `# TODO(T5.5)`); il test di memoria 200 MB e' tenuto verde con `--ignore-exit-code 8`
  (`# TODO(T6.1)`: **chiuso da T6.1** il 2026-10-03, ora `--minimum-expected-tests 1`). Il piano diceva "il commit del tag deve *discendere*
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
- [x] **T6.1** *(fatto 2026-10-03, subagent)* Server loopback + integration test contro il gestore VERO. `tests/Filemaster.IntegrationTests/Loopback/` (7 file): `LoopbackServer` (HTTP/1.1 a mano su
  `TcpListener`, `IPAddress.Loopback` porta 0, compila su net48; registra metodo/percorso/intestazioni/corpo, oltre `keepBodyBytes` solo conteggio + SHA-256; risposte programmabili: status, intestazioni, corpo, chunked,
  troncamento, chiusura (FIN), reset (RST), attesa su segnale, redirect; **ogni attesa ha un limite DENTRO l'helper** (20 s), anche `Dispose` (5 s)), `HttpWireReader`, `LoopbackExchange`, `RecordedRequest`, `GeneratedStream`
  (deterministico, seekable o no, sa se e' stato chiuso), `MultipartParts` (parser indipendente), `LoopbackSupport`. Test (6 file, client da `FilemasterClientFactory.Create` = primario vero, o `AddFilemaster` nell'host vero):
  `LoopbackUploadTests` (ordine delle parti con il file ultimo, `filename*=utf-8''` per nomi non ASCII, stream utente non chiuso, seekable -> `Content-Length`, non seekable -> chunked, partenza dalla posizione corrente),
  `LoopbackDownloadTests` (intero, Range 206 con `Content-Range`, Range ignorato -> 200, troncamento -> `ContentIntegrityException(IsTruncated)` anche ripetuto, chunked intero e tagliato, gzip non decompresso e niente
  `Accept-Encoding`, JSON gzip -> `UnexpectedResponseException`, 4 download aperti + una lista = 5 connessioni), `LoopbackWireTests` (sonde senza chiave, ogni altra chiamata con chiave + `X-Request-ID` 32 hex unico + `User-Agent`;
  301/302/303/307/308 non seguiti: il secondo server non riceve NESSUNA connessione, anche per DELETE e download), `LoopbackResilienceTests` (GET ritentato su 503 non-problem e su connessione chiusa, stesso request id;
  POST/DELETE/upload mai ritentati su 503 ne' su chiusura; `RequestTimeout` -> `FilemasterTimeoutException`; annullamento -> `OperationCanceledException`; `TransferTimeout` di download e di upload fermo; reset a meta'
  upload -> `ConnectionException`; 413 + chiusura -> `RequestTooLargeException` o `ConnectionException`), `LoopbackHostTests` (DI col primario della libreria: redirect, `Content-Length`; **rotazione dei gestori**),
  `LoopbackMemoryTests` (`[Trait("Category","Memory")]`, 200 MB generati, seekable e no).
  **Difetto di produzione trovato e corretto** (riprodotto prima con `A_write_or_a_verify_is_sent_once_when_the_connection_closes_before_the_response`, rosso per delete/verify/folder-delete: 4 richieste invece di 1):
  `SocketsHttpHandler` (.NET 8 e 10) rimanda da solo fino a 4 volte una richiesta SENZA `Content` se la connessione si chiude prima della risposta, quindi DELETE e POST verify erano ritentati malgrado "mai ritentati".
  Fix minimo in `FilemasterTransport.CreateMessage`: ogni non-GET senza corpo parte con corpo vuoto (`Content-Length: 0`), che il gestore non rimanda (commento + remarks). Unit: 2 test di delete aggiornati
  (`Body` vuoto e `Content-Length` 0 invece di `null`), +4 in `TransportCommonTests` (POST/DELETE/PATCH col corpo vuoto, GET senza corpo e corpo della richiesta conservato).
  **Prova (sull'albero vero)**: restore 0; build `--no-incremental` 0 avvisi 0 errori; `dotnet test -c Release --no-build` **5550** (erano 5462: +4 unit e +40 integration per TFM; i 2 test Memory sono INCLUSI nel run
  di soluzione, ~150 ms ciascuno), exit 0; asset ns2.0 su net10 2729 + 46 exit 0 (poi restore normale); format x2 0; `CI=true dotnet pack` 4+4 + `verify-packages.sh` 0; 214 `.cs` senza CR/TAB/non-ASCII;
  compilazione net48 (`-p:IncludeNet48=true -f net48`) 0 avvisi. Integration 3 volte di fila senza Memory: net10 11.7/11.7/11.8 s, net8 12.0/11.7/11.7 s (44/44; i ~10 s sono il ciclo di pulizia della factory nel test di rotazione).
  **Memoria** (crescita del picco di memoria gestita, soglia < 64 MB): net10 seekable +1.6 MB, non seekable +3.0 MB; net8 +1.6 / +3.1 MB; working set +0.0..+0.5 MB.
  **Mutazioni** (21, copia `mutroot61`, net10, integration, watchdog 300 s): 21 prese, 0 sopravvissuti, 0 stalli: redirect e decompressione del primario, Timeout finito (factory e DI), primario di default in DI, upload
  bufferizzato (`LoadIntoBufferAsync` e `MemoryStream`: preso solo dal test di memoria), chiave alle sonde, chunked forzato, retry di POST/DELETE e dell'upload, `ResponseContentRead`, fix T6.1 tolto, GET mai ritentati,
  `IOException` non tradotta, scadenza del download che non rilascia la risposta, User-Agent/X-Request-ID assenti, request id nuovo a ogni tentativo, Range non mandato, `MaxConnectionsPerServer = 2`.
  **Workflow**: in `ci.yml` e `release.yml` il passo di memoria net48 ora ha `--minimum-expected-tests 1` (tolto `--ignore-exit-code 8` e il `TODO(T6.1)`; provato in locale: trait sbagliato -> exit 8);
  actionlint `rhysd/actionlint@sha256:b1934ee5...` (v1.7.12, digest da `docker images --digests`) rc=0.
  **Decisioni**: client col primario vero, mai un gestore finto; rotazione provata con `SetHandlerLifetime(1 s)` e i log della factory (`HandlerExpired`, poi `CleanupCycleEnd` con `DisposedCount >= 1`, GC a ogni giro): il download
  aperto sul gestore smaltito arriva intero; 413 + chiusura accetta i due esiti (su net10/macOS e' `ConnectionException`, broken pipe); i tempi dei timeout si asseriscono con limiti larghi.
  **Osservazioni (non difetti)**: dentro OGNI tentativo del trasporto un GET puo' essere rimandato dal gestore fino a 4 volte (innocuo: idempotente); la `TransferTimeout` di un download fermo scatta dopo ~3 s con 1 s
  configurato (rilascio della risposta = `ResponseDrainTimeout` 2 s).
  **Non verificato**: net48 eseguito (solo compilato: `MaxConnectionsPerServer` e `AllowWriteStreamBuffering` di `HttpClientHandler` li prova il passo di memoria su Windows CI); asset ns2.0 su .NET Framework; Linux/amd64;
  mutazioni solo su net10.
  **Obblighi per T5.5/T6.2**: T5.5 puo' riusare l'idea del server (non il codice: e' nei test) per lo smoke dei pacchetti; T6.2 deve riprovare contro il server vero i punti che il loopback simula (413 di Kestrel,
  Range, redirect assenti) e il comportamento di DELETE con `Content-Length: 0` (il server lo accetta? da verificare: nelle catture i DELETE non avevano corpo).
- [ ] **T6.2** harness e2e (`eng/e2e/*`, `e2e.yml`, riusa il T0.3), **T6.3** cattura fixture dal
  server reale (`eng/e2e/capture-fixtures.sh`, con scrubbing di chiavi ed email).
- [ ] **T7.1** Docs: README (it), `docs/architecture.md`, `docs/publishing.md` (la tua checklist), `docs/api-contract.md`
  (stranezze: 404 su id malformato, body rigidi, 409 ambiguo, validazione tardiva dell'upload).

**Fase B — completamento API** (dopo che la A e' verde): audit, account, API key, webhook admin, import/export ZIP,
`IApiKeyProvider`. **Decisione dell'utente (2026-10-02): la Fase B va DOPO v0.1.0** (diventa la 0.2.0): prima si chiude la Fase A
e la Fase 8 fino al tag rc.

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

## Ripresa 2026-10-03 (sessione di chiusura della Fase A)

Baseline rifatta prima di costruire (lezioni 12/30/39): SDK 10.0.401 + 8.0.425, runtime 8.0.31 + 10.0.12 (invariati); `main` = `512d764`
("last update", commit dell'utente: T3.2/T3.3/T4.1/T4.2) + modifiche non committate di T4.3-T6.1; Sharp-a-File `dev` ancora `8aec8bb`; nessun processo
residuo; restore 0, build `--no-incremental` 0 avvisi, **5550 test verdi** (= fine T6.1). Docker era spento: avviato.

Ordine (in serie per tutto cio' che invoca `dotnet`, lezione 14; i documenti possono andare in parallelo):
- [x] **R1 = T5.5** *(fatto 2026-10-03, subagent; **verificato da me**: pack 0.0.0-smoke.7 exit 0, `verify-packages.sh` 0, `run.sh` 0: net8/net10 OK via DI e factory,
  net48 compilato in C# 7.3, floor 8.0.x dal `project.assets.json`)* `eng/pack-smoke/run.sh [--keep] <cartella> <versione>` + `consumers/{Modern,Net48,Shared}` (server finto
  `TcpListener` con le risposte catturate 03/47/71, feed locale con package source mapping, `NUGET_PACKAGES` isolato). Mutanti su copia: `init` -> CS8370 al build net48;
  versione assente -> 64; `X-API-Key` non mandata -> 1; parte file rinominata -> 1; M.E.Http 9 -> floor rotti, 1. `TODO(T5.5)` tolto da `ci.yml`.
  **Non verificato**: esecuzione net48 su Windows, Git Bash, Ubuntu/mawk, shellcheck/actionlint (Docker spento: in R6).
- [x] **R2 = T7.1 (parte 1)** *(fatto 2026-10-03, subagent; **verificato da me**)* README (516 righe, e' anche il readme NuGet), `docs/architecture.md`, `docs/publishing.md`.
  Ogni blocco di codice del README e' una regione di un campione in scratchpad `docs-samples/` (README rigenerato da `build_readme.py`: `cmp` identico). **Compilati da me
  contro i pacchetti 0.0.0-smoke.7** (feed locale, cache isolata, warning-as-errors): C# 7.3 net48 (9 file), VB net48 `Option Strict On` (2), Web net8+net10 (3): exit 0, 0 avvisi.
  Corretti 2 doc XML segnalati dal subagent: `IFilemasterClient` (diceva "non possiede niente da rilasciare", ma il `FilemasterClient` della factory e' `IDisposable`) e
  `FilemasterException` (slug `error` + 5xx = `ServerErrorException`, come fa `ProblemMapper`); build 0 avvisi, format x2 0. Il piano B di `publishing.md` e' **manuale**
  (`release.yml` non legge `NUGET_API_KEY`); i parametri delle porte si chiamano `cancellationToken` (il piano diceva `ct`).
- [x] **R3 = T6.2** *(codice fatto 2026-10-03, subagent; **verificato da me**: build `--no-incremental` 0 avvisi, `dotnet test` 5612 = 5550 superati + 62 ignorati (31 Live x net8/net10), exit 0;
  pin delle azioni di `e2e.yml` identici a `ci.yml`; SHA del server = `git rev-parse dev`. File: `eng/e2e/{_common,run-e2e,seed,down,logs}.sh` + compose + README, `.e2e/` in `.gitignore`
  (stato con segreti 0600), `e2e.yml` (dispatch + notturno, `FILEMASTER_E2E_REQUIRED=1`: in CI un URL mancante fallisce invece di saltare), `tests/.../Live/` 31 test `Category=Live`
  (`ContractDriftTests` compresa la prova DELETE/verify con `Content-Length: 0`); seed SQL di categoria, 3 contatti e un documento senza contenuto collegato.
  **Corsa live fatta da me dopo lo sblocco di Docker**: primo avvio fallito sul seed (`Msg 1934`, `QUOTED_IDENTIFIER`: sqlcmd parte OFF e i contatti hanno indici filtrati)
  -> `-I` in `sqlcmd_run`; poi server su (`--mac`), **31/31 su net10 e net8, cinque corse di fila sullo stesso server** con `FILEMASTER_E2E_REQUIRED=1`; log del server: nessun 5xx,
  DELETE 204 e verify 200 con `Content-Length: 0` (obbligo T6.1 chiuso). shellcheck (`-x -P SCRIPTDIR`, digest da `docker-replay.sh`) 0 dopo 3 correzioni (variabili inutili, direttive
  SC1090/SC1091 sui file di stato), actionlint (digest da `docker images --digests`) 0 su 3 workflow.)* `eng/e2e/*` (dagli script in `.scratch/spikes-e2e`, senza `state/`), `e2e.yml` pinnato a `8aec8bb`, suite live opt-in
  (`FILEMASTER_E2E_URL`/`_KEY`/`_READ_KEY`, `Assert.SkipWhen`), eseguita davvero in locale (Azure SQL Edge, `--mac`). Verifica anche `DELETE` con `Content-Length: 0` (obbligo T6.1).
- [x] **R4 = T6.3** *(fatto 2026-10-03, da me)* `eng/e2e/capture-fixtures.sh` promosso dallo spike (uscita in `.e2e/fixtures/`, nome non ASCII scritto con escape ottali,
  guardia: rifiuta un server con limite di upload < 8 MiB, perche' il limite basso della suite live trasformava le catture da 5 MiB in 413: 2 MISMATCH al primo giro). Due catture complete
  (223 richieste, `0 MISMATCH`, 5 MiB byte-esatti) confrontate con le golden per nome/status/campi: **nessuna differenza di contratto** (7 differenze, tutte di dati). Le golden restano;
  aggiunte le catture vere `captured/301-304` (contatti, dettaglio, categorie, documento senza contenuto con 3 contatti) + `CapturedContactsTests` (8 test: lettori sui corpi veri e
  "ogni campo del server esiste nella fixture derivata"; provato con un mutante sulla copia in `bin/`). **Non fatto**: ricattura su SQL Server 2022 (e' `e2e.yml` su Ubuntu); buste webhook (fase B).
  Testo originale:  `eng/e2e/capture-fixtures.sh` con scrubbing; sostituire le fixture derivate che il server sa produrre (documento senza contenuto, `metadata` assente...);
  quelle non riproducibili restano marcate "derivate".
- [x] **R5 = T7.1 (parte 2)** *(fatto 2026-10-03)* `api-contract.md`: stato, sezione "Composizione e gestore HTTP", "Test contro il server vero", "Non verificato" riscritto;
  `architecture.md`/`publishing.md` aggiornati (pack-smoke e live esistono, nomi dei job). Testo originale: `docs/api-contract.md` allineato a T4.3b/T4.4/T6 (503 non-sonda di `/readyz`, sonde senza chiave, composizione, esiti e2e).
- [x] **R6 = Fase 8 (parte locale)** *(2026-10-03)* T8.1 `eng/docker-replay.sh` da copia pulita in `sdk:10.0` (arm64): 10/10 passi, 2737 unit per TFM; T8.2 `verify-packages.sh` 0;
  T8.3 pack-smoke 0; T8.4 actionlint/shellcheck 0; T8.5 suite live verde (R3). Restano: prima CI su GitHub (job Windows net48 mai visto), `e2e.yml` su Ubuntu/SQL 2022, T8.6. Testo originale:: T8.1 replay `sdk:10.0`, T8.2 nupkg, T8.3 pack-smoke, T8.4 actionlint, T8.5 run live; T8.6 resta all'utente (tag rc, commit/push).
- [x] **R7 = prima CI net48 rossa** *(2026-10-03)* 2728/2729: `PercentEncodingTests` autoverificava che `Uri.EscapeDataString` sostituisse un surrogato isolato con U+FFFD (vero su .NET 8/10); su net48
  lancia `UriFormatException`. Il codice era giusto (rifiuta prima con `ArgumentException`, gli 8 test sui surrogati verdi su net48): corretto il test con `#if NETFRAMEWORK` (rinominato
  `The_framework_mishandles_a_lone_surrogate_which_is_why_it_is_refused_first`), doc di `PercentEncoding` e "Non verificato" di `api-contract.md`. Verificato in locale: build con
  `IncludeNet48=true` 0 avvisi, 2737/2737 su net8 e net10, format x2 0, ASCII/LF. **Non verificato**: il verde su net48 (nessun runtime net48 sul Mac) -> rilanciare la CI.
- [x] **R8 = seconda CI net48 rossa** *(2026-10-03)* 1/75 negli IntegrationTests: `A_non_seekable_stream_is_sent_chunked_without_Content_Length`. Causa: `HttpClientHandler` di
  .NET Framework, con corpo di lunghezza ignota e senza `TransferEncodingChunked`, carica tutto il corpo in memoria (`LoadIntoBufferAsync`) e manda `Content-Length`: difetto vero
  (upload grandi non riposizionabili in RAM), non del test. Fix in `HttpDocumentStore.UploadAsync`: `TransferEncodingChunked = true` se `Content.Headers.ContentLength` e' null
  (su .NET e' gia' il default); remark di `UploadStreamContent` corretto. Verificato in locale: build `--no-incremental` e `IncludeNet48=true` 0 avvisi, `dotnet test` 5628 exit 0,
  test di memoria net10 2/2, format x2 0, ASCII/LF. **Non verificato**: net48 (nessun runtime sul Mac) -> rilanciare la CI, compreso il passo "Test di memoria" (mai arrivato
  a girare: il suo caso non riposizionabile era colpito dallo stesso difetto).

## Review

### Stato al 2026-10-03 (fine della Fase A)

**Verificato da me sul repo**: build `--no-incremental` 0 avvisi; `dotnet test` 5628 (5566 superati + 62 Live ignorati senza server), exit 0; format x2 0; replay Linux 10/10;
pack + `verify-packages.sh` + pack-smoke 0; campioni del README compilati (C# 7.3, VB, Web); suite live 31/31 su net8/net10 contro Sharp-a-File `dev` 8aec8bb vero; actionlint e shellcheck 0;
nessun segreto reale fra i 470 file tracciabili (`.e2e/` ignorata), tutto ASCII/LF.

**Da fare all'utente**: commit/push delle modifiche (nessun commit fatto da me); guardare la prima CI su GitHub (job Windows net48 e pack-smoke Windows: mai eseguiti); lanciare `e2e.yml` a mano una
volta; i passi una tantum di `docs/publishing.md`; poi il tag `v0.1.0-rc.1`. **Segnalazione per Sharp-a-File** (non per il client): cartella con padre inesistente e cartella non vuota
producono log `Error` con stack di eccezioni del database pur rispondendo 404/409.


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


---

# Verifica end-to-end del filtro documenti (piano del 2026-10-09)

> Obiettivo: provare che Filemaster, usato come lo userebbe un gestionale, restituisce l'elenco di documenti filtrato dai
> parametri dell'utente (`DocumentQuery` -> `IDocumentStore.ListAsync/EnumerateAsync` -> `GET /documents` -> Sharp-a-File).

## Cosa ho trovato leggendo (prima di toccare qualunque cosa)

- Esiste gia' l'harness live (`eng/e2e/*`, 31 test `Category=Live`), verde il 2026-10-03 contro Sharp-a-File `dev` **8aec8bb**.
- Da allora Sharp-a-File e' andato avanti di 5 commit (`master` 541f378, `dev` 7ca0e7e, 2026-10-08/09): commit
  **7ca0e7e "now the app use autoinc IDs"**: gli id passano da `doc_<ULID>` a interi IDENTITY (`int`; `bigint` per documenti
  e consegne), 156 file modificati. Filemaster (`DocumentId`, `ContactId`, `TenantId`, seed, fixture, docs) assume ancora i
  prefissi ULID: **contro il server attuale mi aspetto che fallisca**, non per i filtri ma per gli id.
- I filtri del server (`QueryParsing.DocumentQuery`): `folder_id, owner, tag, filename, sender, recipient, metadata_query,
  q, metadata, sender_id, recipient_id, created_from, created_to`. `DocumentQuery` li copre tutti; il test live
  `Filters_pages_of_one_and_the_enumeration_...` va controllato filtro per filtro (copertura da misurare, non da presumere).

## Decisione dell'utente (2026-10-09)
Adeguare Filemaster al server **attuale** (Sharp-a-File `master` 541f378: id numerici) e verificare i filtri su quello.

## Esito dell'analisi (3 subagent in sola lettura, nessun file toccato)

**Contratto server 8aec8bb -> 541f378.** Rotte invariate. Id come numeri JSON: `int` per tenant/contatto/account/chiave/webhook,
`long` per documento e consegna webhook. Cartelle e categorie restano codici stringa. URL/query: decimale canonico (`042`,
`+42`, `doc_...` = 404). Corpi bulk: `{"document_ids":[42,43]}` (le stringhe `doc_...` = 400). Cursore: `id` numerico,
i vecchi = 400. Webhook: `delivery_id` numero; `document_id` e' numero in `uploaded`/`integrity_failed` ma **stringa** in
`deleted` (accettare entrambi). Altro: 413 `request-too-large` anche sui JSON, `X-Request-ID` ora anche nell'header degli errori,
multipart troncato = 400 (era 500), `folder.parent_id == id` = 400. **Filtri di `GET /documents`: semantica invariata**, cambia
solo il formato di `sender_id`/`recipient_id` (int canonico).

**Impatto su Filemaster.** Sorgenti: `PrefixedId` + `DocumentId`/`ContactId`/`TenantId`; `WireObject.RequiredId/OptionalId`,
`DocumentWire.WriteIds` (deve scrivere numeri), `VerifyWire`, `ContactWire`, `TenantWire`; `WebhookEventParser`
(`delivery_id`, `document_id`). Query/URL (`Routes`, `QueryBuilder`) restano uguali se `Value` resta `string`. Test: ~97 file con
736 id letterali (IdTests da riscrivere, ~40 unit, 8 live/loopback, 53-61 fixture), `pack-smoke/Smoke.cs` (canarino per net48/VB),
`seed.sh`, `capture-fixtures.sh`, docs (`api-contract.md`, `architecture.md`, README, `Fixtures/README.md`).

**Design scelto (minimo impatto, lezione 16).** `DocumentId`/`ContactId`/`TenantId` restano `readonly record struct` non
posizionali con costruttore esplicito; dentro un numero (`long` documento, `int` contatto/tenant), `default` = 0 = id vuoto e
non valido; `Value` resta `string` (decimale canonico, invariant culture) cosi' URL/query/`.Value` non si rompono; validazione =
solo cifre ASCII, niente segno/zeri iniziali/spazi, intervallo `1..int.MaxValue` o `1..long.MaxValue`, ciclo di char (niente
regex ne' `int.TryParse` da solo). Aggiunte additive: factory statica da `long`/`int` e accessor numerico (niente secondo
costruttore: `PublicShapeTests` pretende un solo ctor). `WebhookEvent.DeliveryId` resta `string` (chiave di dedup), il parser
accetta un numero JSON.

**Harness su HEAD.** Fattibile: SQL Server/Edge, chiavi (`saf_`+base64url, hash `sha256:<hex>`), env e Dockerfile invariati.
Da cambiare: `seed.sh` (niente `id` negli INSERT di tenants/api_keys/contacts/documents, `@t int`/`@d bigint`, id riletti per
chiave naturale, **non** `SCOPE_IDENTITY()` dopo `IF NOT EXISTS`; via `gen_ulid` e `E2E_*_ID`), `capture-fixtures.sh`
(placeholder `doc_abc`, `con_01...`, `key_...` -> `abc`/`0`/`042`/overflow; INSERT chiave senza `id`; `$D1` senza apici nel JSON),
`run-e2e.sh:119` (migrazione rinominata `20261008193649_InitialSchema.cs`, patch ISJSON da rivalidare), pin SHA in `e2e.yml`,
`README`, `ContractDriftTests`. Il DB va ricreato (`down.sh` con `-v`: InitialSchema riscritta, cartella tenant ora `/data/<int>/`).
Il seed resta in SQL: tenant, contatti, categorie e documento senza contenuto non sono creabili dall'API.

## Piano (ogni fase: build `--no-incremental` 0 avvisi, test, format x2; la catena di codice gira in serie, lezione 14)

- [x] **P0 Baseline** *(fatto 2026-10-09: build --no-incremental 0 avvisi; test 5628 = 5566 superati + 62 Live ignorati; SDK 10.0.401+8.0.425; Docker 29.8.2 arm64)* (lezione 12): SDK/Docker, build, `dotnet test` senza server; `git status`. Pin del server = SHA di `master`.
- [x] **P1 Harness su HEAD** *(fatto 2026-10-09, verificato da me: migrazione applicata su Azure SQL Edge, seed ok e idempotente (tenant=1, 3 chiavi, 3 contatti, doc 1), GET /tenant 200; id malformati 404, cursore non valido 400)* (prima del client, perche' la cattura reale e' la fonte di verita'): `seed.sh`, `run-e2e.sh`, pin; il
      server parte e `GET /tenant` risponde con la chiave seminata. Se Azure SQL Edge rifiuta la nuova migrazione: stop e re-plan.
- [x] **P2 Cattura** *(fatto 2026-10-09, subagent, verificato da me: `.e2e/fixtures/t64`, 291 richieste, 0 MISMATCH, 1159 file, 0 chiavi; stati tutti invariati rispetto a t63b; cambia solo string->number sugli id. Novita': il server accetta anche stringhe numeriche in `document_ids` (`["30017"]` = 200); `X-Request-ID` ora anche nell'header degli errori (la nota T0.3 non vale piu'); `sender_id`/`recipient_id` valido ma ignoto = 200 lista vuota; `bulk/verify` con id <= 0 = 404 (non ignorati), `bulk/move` li ignora; export ZIP: voci `<id>_<nome>`). Procedura: `FILEMASTER_E2E_URL=http://127.0.0.1:18083 FILEMASTER_E2E_MAX_UPLOAD_BYTES=104857600 eng/e2e/capture-fixtures.sh --set t64` su istanza usa-e-getta* `capture-fixtures.sh` adeguato; ricatturare e confrontare con quanto assunto sopra (id numerici ovunque?
      404 vs 400 sugli id malformati? cursore?). **Quello che la cattura smentisce vince sul report dei subagent.**
- [x] **P3 Domain** *(fatto 2026-10-09, subagent, verificato da me: build 0 avvisi; test Domain 700 = 350 x net8/net10, 0 falliti; 28 mutanti, 27 uccisi e 1 equivalente voluto; `NumericId` internal, `Number`, `From(long|int)`; i test fuori Domain falliscono come previsto: 866 righe, lista in P4)*: id numerici, `IdTests` riscritti, `PublicShapeTests`; mutazioni sui confini (0, 1, `int.MaxValue`+1, segno, zero iniziale, spazi).
- [x] **P4 Wire + Application** *(fatto 2026-10-09, subagent, verificato da me: build 0 avvisi; 6030 test = 5968 superati + 62 Live ignorati, 0 falliti; format 0; lettori id solo da numeri JSON (stringa/float/negativo/zero/overflow rifiutati), `WriteIds` numerico, parser webhook `delivery_id` numero o stringa e `document_id` numero o stringa di cifre; fixture `captured/` rigenerate da t64 (63 catture), `derived/` riscritte; ~16 mutanti uccisi)* Testo originale:: lettura id come numeri, `WriteIds` numerico, parser webhook (numero e stringa), fixture derivate.
- [x] **P5 Test e smoke** *(fatto 2026-10-09: suite live adeguata, 10 rotture iniziali tutte lato test; `pack-smoke` aggiornato (nella nuova cattura l'upload e' `deduplicated:true`); pack + `verify-packages.sh` 4 pacchetti OK; pack-smoke net8/net10 OK, net48 solo compilato (non Windows); shellcheck 0 (immagine pinnata; un SC2034 corretto in capture-fixtures.sh), actionlint 0; docs/README/e2e.yml aggiornati, pin = 541f3789037f6543746de11d02d8388ff9b9a215)*: unit/loopback/live, `pack-smoke` (C# 7.3 + VB), docs e README.
- [x] **P6 Verifica dei filtri end-to-end** *(fatto 2026-10-09, vedi Review)* (l'obiettivo vero): mappa filtro -> test; un test live per ciascuno dei 13 filtri +
      AND + paginazione + `EnumerateAsync` + filtro senza risultati, con oracolo calcolato a mano (insieme esatto di id), documenti
      seminati ad hoc; ogni test nuovo provato con una mutazione (lezione 20); cinque corse di fila su net8/net10 con `REQUIRED=1`.
- [x] **P7 Review** *(qui sotto; lezioni 57-64)* qui + lezioni nuove (id numerici, seed idempotente per chiave naturale).

## Risposte dell'utente (2026-10-09)
1. Nessuna versione e' ancora uscita (NuGet/GitHub non configurati): nessun vincolo di compatibilita' pubblica. 2. Pin del server = ultimo `master` (541f378).

## (superato) Da decidere / segnalare prima di scrivere codice
1. **Compatibilita' pubblica**: `git tag` mostra **`v1.0.0`** nel repo Filemaster, mentre il piano originale prevedeva solo un `-rc`.
   Se qualcuno ha gia' pubblicato/consumato 1.0.0, `new DocumentId("doc_...")` che ora lancia e' una rottura: servirebbe una
   versione maggiore (o `0.x`/`-rc`). Non lo verifico io su nuget.org senza che me lo dica l'utente.
2. Il job `e2e.yml` e il pin diventano `master` 541f378: va bene? (Il README dice che `master` non aveva i codici cartella; ora li ha.)


### Review della verifica end-to-end dei filtri (2026-10-09)

**Risposta all'obiettivo.** Filemaster, contro un Sharp-a-File vero (`master` 541f378, SQL su Azure SQL Edge, immagine costruita dal suo
Dockerfile), restituisce l'elenco filtrato esattamente come chiedono i parametri. Per ciascuno dei 13 filtri di `DocumentQuery`
(`folder_id, owner, tag, filename, sender, recipient, sender_id, recipient_id, q, metadata_query, metadata, created_from, created_to`)
c'e' un test live con l'insieme esatto di id atteso calcolato a mano (`LiveDocumentFilterTests`, 17 test), piu' AND, esclusione reciproca,
ordine (piu' recente prima), paginazione di una query filtrata, `EnumerateAsync`, valori solo-spazi ignorati, validazione lato client
(intervallo rovesciato = `ArgumentException` senza richiesta) e un caso come lo vivrebbe un gestionale: `AddFilemaster` + `IDocumentStore`
con i campi di un "modulo di ricerca" in stringa. **Nessun bug in Filemaster ne' nel server sui filtri.**

**Prove (verificate da me sul repo)**: build `--no-incremental` 0 avvisi; `dotnet test` 6064 = 5968 superati + 96 Live ignorati senza server,
0 falliti; suite live con server: 96/96 (48 test x net8/net10) in due corse mie dopo le cinque del subagent, `FILEMASTER_E2E_REQUIRED=1`,
server lasciato pulito (solo il documento 1, nessuna cartella); `dotnet format` x2 0; shellcheck e actionlint 0; pack + verify-packages +
pack-smoke OK. Mutanti: 72 sul client per i filtri (tutti uccisi, 2 equivalenti dimostrati), ~28 sul Domain, ~16 su wire e parser.

**Cosa e' cambiato**: id numerici (Domain `NumericId`, `DocumentId/ContactId/TenantId`), lettori JSON solo da numeri, `WriteIds` numerico,
parser webhook (`delivery_id` numero o stringa; `document_id` numero o stringa di cifre), fixture ricatturate (t64, 291 richieste, 0 MISMATCH),
harness (`seed.sh` senza id, migrazione cercata per nome, pin 541f378), docs/README, 3 commenti in `src/`.

**Non fatto / da sapere**
- Il job Windows net48 e il pack-smoke Windows non girano su questo Mac (net48 solo compilato). Va guardata la prima CI su GitHub.
- La ricattura e' su Azure SQL Edge (SQL Server 15): la patch `ISJSON(metadata, OBJECT)` vale solo per la copia `--mac`. L'esito su
  SQL Server 2022 e' di `e2e.yml` su Ubuntu (non ancora eseguito).
- Gli esempi della sezione "Gli id" del README usano solo API gia' provate (`From`, `Number`, `Value`, `TryParse`) ma non sono stati compilati a parte.
- Webhook: nessuna cattura reale, `derived/` resta scritto a mano sulla forma letta nel codice del server.
- Nessun commit/push e nessuna pubblicazione NuGet: aspettano un "vai" esplicito dell'utente (lezione 10).
- L'istanza `filemaster-e2e` (app + SQL) e' ancora attiva su 127.0.0.1:18080: `eng/e2e/down.sh` la rimuove.
