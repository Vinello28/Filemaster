# Architettura di Filemaster

Come e' fatta la libreria e perche'. Per l'uso si parte dal [README](../README.md); per il comportamento del server dal
[contratto HTTP](api-contract.md). Quello che qui e' detto "non verificato" lo e' davvero: vedi l'ultima sezione.

## Quattro livelli, quattro pacchetti

```
Filemaster  (composizione: AddFilemaster, FilemasterClientFactory)
    |
    v
Filemaster.Infrastructure  (adapter HTTP: trasporto, wire, errori)
    |
    v
Filemaster.Application  (porte, richieste, estensioni, webhook)
    |
    v
Filemaster.Domain  (ID tipizzati, entita', eccezioni, eventi)
```

**Regola delle dipendenze.** Ogni livello conosce solo quelli sotto di se'. Il Domain non dipende da altri pacchetti
Filemaster e non conosce HTTP. L'Application dipende dal Domain e non ha riferimenti a `System.Net.Http`. Solo
l'Infrastructure parla HTTP. Il pacchetto `Filemaster` e' il solo che conosce `Microsoft.Extensions.Http` e
`IHttpClientFactory`. La regola non resta scritta solo qui: la controllano i test di forma (vedi
[Strategia di test](#strategia-di-test)) e `eng/verify-packages.sh`, che controlla i `.nupkg` prodotti e che le
dipendenze fra pacchetti Filemaster seguano il layering.

**Perche' quattro pacchetti e non uno.**

- Uno strato applicativo (un servizio di dominio del gestionale, un ricevitore di webhook) referenzia solo
  `Filemaster.Application`. Programma contro le porte e nei test le sostituisce con dei finti. Non si porta dietro
  `HttpClient`, `Microsoft.Extensions.Http` o la configurazione.
- Il progetto host referenzia `Filemaster`, che porta tutto il resto.
- `Filemaster.Infrastructure` espone solo `FilemasterOptions`, `FilemasterRetryOptions` e `FilemasterHttp`. Un test
  confronta la sua superficie pubblica con esattamente quei tre tipi. Chi vuole comporre il client su un `HttpClient`
  suo usa `FilemasterHttp.CreateClient` senza toccare la DI.

I pacchetti escono sempre insieme, con la stessa versione. Fra di loro si richiedono con range esatto (`[x.y.z]`), una
regola imposta da `Directory.Build.targets` e controllata da `eng/verify-packages.sh`. Una versione mista
(per esempio Application 0.1.0 con Infrastructure 0.1.1) potrebbe caricarsi e poi fallire a runtime con
`TypeLoadException` o `MissingMethodException`. Il range esatto la trasforma in un errore di restore.

**Dipendenze esterne.** Restano minime e hanno come minimo la serie 8.0.x, cosi' funzionano anche con un host .NET 8:

- `System.Text.Json`, solo su netstandard2.0;
- `Microsoft.Bcl.AsyncInterfaces` e `Microsoft.Bcl.TimeProvider`, solo su netstandard2.0;
- `Microsoft.Extensions.Logging.Abstractions`;
- `Microsoft.Extensions.Http`, solo nel pacchetto `Filemaster`.

`PolySharp` serve solo in compilazione (`PrivateAssets=all`) e non compare fra le dipendenze.

**Target.**

- `netstandard2.0`, per .NET Framework 4.8;
- `net8.0`, che serve anche .NET 9;
- `net10.0`.

## Pattern

| Pattern | Dove | Perche' |
| --- | --- | --- |
| Porte e adapter (esagonale) | Porte in `Filemaster.Application` (`IDocumentStore`, `IFolderCatalog`, `IContactDirectory`, `ITenantInfo`, `IFilemasterHealth`); adapter `Http*` interni all'Infrastructure | Il codice dell'applicazione non dipende da HTTP e si testa con dei finti |
| Facade | `IFilemasterClient` / `FilemasterClient`: un oggetto con `Documents`, `Folders`, `Contacts`, `Tenant`, `Health` | Un solo punto d'ingresso da registrare o da tenere in un campo statico |
| Value object | `DocumentId`, `ContactId`, `TenantId`, `FolderCode` (`readonly record struct`), `ByteRange`, `PageRequest` | Un id malformato e' un `ArgumentException` locale, non un 404 dal server; niente stringhe scambiate fra loro |
| Decorator | `VerifiedContentStream` intorno allo stream di un download | Controllo di dimensione e SHA-256 senza bufferizzare, trasparente per chi legge |
| Iterator | `EnumerateAsync` su documenti e contatti (`IAsyncEnumerable<T>`) | Pagine e cursori nascosti a chi puo' usare `await foreach`; da C# 7.3 restano `ListAsync` e il cursore |
| Factory | `FilemasterClientFactory.Create` (senza DI), `FilemasterHttp.CreateClient` (su un `HttpClient` dato) | Il gestore HTTP giusto per runtime e un client completo in una riga, anche da VB.NET |
| Options | `FilemasterOptions` + `FilemasterRetryOptions`, con `Validate()`; in DI `IOptions` nominate e `ValidateOnStart` | Configurazione esplicita, errori all'avvio e non alla prima chiamata |

I tipi di input sono classi con costruttore e `get; set;`, senza `init` e senza `required`, cosi' si usano da C# 7.3 e
da VB.NET. I tipi di output sono record immutabili.

## Il trasporto

Un solo componente interno (`FilemasterTransport`) parla HTTP. Gli adapter delle risorse gli chiedono di inviare una
richiesta in una di tre modalita':

- **bufferizzata**: JSON letto intero, con un tetto;
- **download**: risposta consegnata come stream;
- **upload**: corpo in streaming.

### Una scadenza per chiamata

`RequestTimeout` (default 30 s) e' **una sola scadenza per tutta la chiamata**. Copre tutti i tentativi e le attese fra
l'uno e l'altro, non ogni tentativo separatamente: chi chiama sa quanto aspettera' al massimo.

`TransferTimeout` (default 30 min) vale invece:

- per l'intero upload;
- per il corpo di un download, dopo le intestazioni.

Per questo `HttpClient.Timeout` deve essere infinito: con il default di 100 s un caricamento lungo verrebbe troncato.
`FilemasterHttp.CreateClient` lo pretende, e la factory e `AddFilemaster` lo impostano da soli.

L'annullamento del chiamante resta sempre `OperationCanceledException`. Ogni altra cancellazione diventa
`FilemasterTimeoutException`. Le due si distinguono dallo stato del token del chiamante, non dal tipo dell'eccezione.

### Ritentativi solo sui GET, e perche'

Il client ritenta solo le richieste `GET`, e solo in questi casi:

- su errori di rete;
- su 408 e 429 (rispettando `Retry-After` fino a `MaxDelay`);
- su 502, 503 o 504 che **non** sono problem+json, cioe' errori di un proxy e non del server.

Ritenta solo prima di consegnare la risposta a chi chiama, e mai su uno status che la richiesta dichiara atteso (il 503
di `/readyz`). L'attesa fra i tentativi e' esponenziale con jitter.

Non si ritentano mai:

- `POST`, `PATCH` e `DELETE`;
- gli upload;
- le verifiche, che scrivono nello storico ed emettono webhook;
- un 500;
- un 409;
- un timeout del client.

Il motivo e' che Sharp-a-File non ha chiavi di idempotenza. Dopo un errore di rete su una scrittura l'esito e' ignoto:

- ritentare un caricamento crea un secondo documento;
- ritentare una cancellazione riuscita restituisce un 404 fuorviante.

Per una scrittura e' meglio un errore onesto (`ConnectionException`) che un effetto doppio.

Ogni tentativo usa un `HttpRequestMessage` nuovo. `X-Request-ID` (32 esadecimali) e' lo stesso per tutti i tentativi
di una chiamata, cosi' nei log del server si ritrovano insieme. La traduzione in eccezioni avviene una volta sola,
sull'ultima risposta.

### Corpo vuoto sulle richieste non GET

Le richieste non `GET` senza corpo, come `DELETE` e le verifiche, partono con un corpo vuoto (`Content-Length: 0`).

Il motivo e' un comportamento misurato in T6.1 su .NET 8 e 10 con il server di loopback. `SocketsHttpHandler` rimanda
**da solo** una richiesta senza contenuto quando la connessione si chiude prima del primo byte della risposta: fino a 4
invii in tutto, anche su connessioni nuove (lezione 48 in `tasks/lessons.md`). Con un contenuto, anche vuoto, non la
rimanda. Senza questa precauzione, la regola "mai ritentato" del nostro codice non varrebbe sul filo.

Un `GET` invece puo' essere rimandato anche dal gestore, dentro un nostro tentativo, ed e' innocuo.

### Download: troncamenti rilevati

Il gestore HTTP non segnala allo stesso modo un corpo finito prima del previsto su .NET Framework e su .NET moderno.
Per questo lo stream dei download (`DownloadStream`) conta i byte rispetto a `Content-Length`:

| Cosa succede | Risultato |
| --- | --- |
| Arrivano meno byte del dichiarato | `ContentIntegrityException` con `IsTruncated` vero |
| Arrivano piu' byte del dichiarato | `ContentIntegrityException` con `IsTruncated` falso; i byte in eccesso non vengono consegnati |
| Errore di rete durante la lettura | `ContentIntegrityException` con `IsTruncated` vero |

Con un corpo `chunked`, cioe' senza lunghezza, l'errore di rete e' l'unico segnale possibile. Il verdetto e' "sticky":
le letture successive rilanciano.

Lo SHA-256 lo controlla `VerifiedContentStream`, che decora lo stream e si apre con `OpenVerifiedContentAsync`. Lo
confronta con il valore registrato alla fine della lettura.

Lo stream possiede la risposta HTTP: va smaltito, altrimenti la connessione resta occupata. Allo scadere di
`TransferTimeout` rilascia la risposta, cosi' anche una lettura bloccata su .NET Framework (che ignora il token di
lettura) termina.

### Upload in streaming

Il corpo multipart (`UploadStreamContent`) si scrive a blocchi di 80 KiB direttamente dallo stream dell'utente:

- non lo copia in memoria;
- non lo riavvolge;
- non lo chiude;
- mette la parte del file per ultima, dopo i campi.

Il trasporto non legge ne' copia il corpo.

Su .NET Framework, `HttpClientHandler` potrebbe comunque bufferizzare la richiesta. Non e' verificato: vedi l'ultima
sezione.

### Redirect e decompressione rifiutati

Il gestore primario deve avere `AllowAutoRedirect = false` e `AutomaticDecompression = None`, per due motivi:

- un redirect seguito in automatico ricopierebbe `X-API-Key` verso il nuovo indirizzo;
- la decompressione renderebbe falso il confronto dei byte con `Content-Length` e con lo SHA-256.

I gestori che crea la libreria sono gia' configurati cosi'. Su un gestore passato da fuori il controllo cambia a
seconda del percorso:

- `FilemasterClientFactory.Create` lancia `ArgumentException` (ParamName `handler`) se riceve un `HttpClientHandler` o
  un `SocketsHttpHandler` (anche in fondo a una catena di `DelegatingHandler`) che li abilita;
- con `AddFilemaster`, un filtro di `IHttpMessageHandlerBuilderFilter` lancia `InvalidOperationException` alla
  creazione del client.

Un gestore di altro tipo non si puo' ispezionare: la regola resta a chi lo scrive.

### Sonde senza chiave

`/healthz` e `/readyz` partono senza `X-API-Key` (`TransportRequest.OmitApiKey`). Se l'header e' presente, il gestore
di autenticazione del server cerca la chiave nel database anche sulle rotte anonime. Con il database giu', `/readyz`
risponderebbe 500, o aspetterebbe il timeout SQL, invece del suo 503. Questo comportamento e' stato letto nel codice del
server, non misurato (lezione 44).

### Intestazioni e gestori

- La chiave va in `X-API-Key` **per richiesta**, mai fra le intestazioni di default dell'`HttpClient`.
- La chiave non compare mai nei log, nelle eccezioni o in `ToString()`. In DI, `X-API-Key` e `Authorization` sono
  oscurate nei log di `IHttpClientFactory` (`RedactLoggedHeaders`).
- `User-Agent` e' `Filemaster/versione (runtime; sistema)`.
- Su .NET 8 e 10 il gestore primario e' `SocketsHttpHandler` con `PooledConnectionLifetime` di 2 minuti, cosi' le
  connessioni si rinnovano e seguono i cambi di DNS.
- Su netstandard2.0 il gestore primario e' `HttpClientHandler` con almeno 32 connessioni per server. Il default di .NET
  Framework e' 2.
- In DI c'e' un client nominato (`"Filemaster"`), e il client e le porte sono singleton. Il trasporto chiede un
  `HttpClient` a `IHttpClientFactory` **a ogni tentativo**, cosi' la rotazione dei gestori funziona anche dentro un
  singleton.
- Le opzioni hanno `ValidateOnStart`, e ogni registrazione usa `TryAdd`.

## Il livello wire

La conversione fra JSON e modelli e' scritta a mano (`Wire/`):

- si legge con `JsonElement`;
- si scrive con `Utf8JsonWriter`;
- niente DTO, niente reflection, nessuna `JsonSerializerOptions` condivisa (`WireJson.cs`).

I motivi:

- **Ogni errore di forma e' un'eccezione precisa.** Una risposta che non si sa leggere diventa sempre
  `UnexpectedResponseException`, con lo status vero. Un campo assente, un `null` e un tipo sbagliato si distinguono.
- **I metadati restano validi.** Sono copiati (`Clone()`) prima che il documento JSON sia smaltito.
- **Lo stesso comportamento su ogni target.** Il risultato e' identico su netstandard2.0, net8.0 e net10.0. Un
  serializzatore a reflection cambia con la versione di `System.Text.Json`.
- **Nessun campo in piu'.** Il server rifiuta con 400 le proprieta' sconosciute, quindi si scrivono esattamente i campi
  che accetta.

Gli errori `problem+json` diventano eccezioni in `ProblemMapper`. La tabella completa e' in
[api-contract.md](api-contract.md).

## Strategia di test

I test usano xUnit v3 su Microsoft.Testing.Platform. Girano su net8.0 e net10.0, e su net48 nel job Windows della CI.

- **Unit test** (`tests/Filemaster.UnitTests`), organizzati per livello: `Domain`, `Application` (compresi i
  webhook), `Infrastructure` (trasporto, opzioni, mappatura errori, adapter `Documents` e `Resources`), `Wire` e
  `Composition`. Il trasporto si prova con gestori finti: ritentativi, scadenze, segreti, download e upload.
- **Fixture golden** (`tests/Filemaster.UnitTests/Wire/Fixtures/`):
  - 174 risposte **catturate** da un Sharp-a-File vero (ramo `dev`, commit `8aec8bb`) e ripulite dai dati sensibili;
  - 8 risposte **derivate** dal codice del server, dove una cattura non esiste.

  I test di lettura confrontano le fixture con valori scritti a mano. I costruttori di richieste si confrontano con il
  corpo che il server ha accettato. Il `README.md` della cartella spiega provenienza e scrub.
- **Test di forma (architettura)**:
  - `ApplicationShapeTests`: niente `init` ne' `required`, niente `System.Net.Http`, token di annullamento con default;
  - `InfrastructureShapeTests`: assembly referenziati e superficie pubblica uguale a
    `{FilemasterHttp, FilemasterOptions, FilemasterRetryOptions}`;
  - `WireShapeTests`;
  - `Domain/PublicShapeTests`.

  Ognuna di queste regole e' stata provata con una mutazione: violata a mano, il suo test fallisce.
- **Integrazione su loopback** (`tests/Filemaster.IntegrationTests`): un server HTTP minimo su `TcpListener` e il
  gestore **vero** del runtime, non un finto. Coprono:
  - corpi e intestazioni sul filo, chunked, troncamenti, connessioni chiuse;
  - upload in streaming;
  - il rinvio automatico di `SocketsHttpHandler` (lezione 48);
  - l'host generico (`GenericHostTests`).

  I test di memoria (tratto `Category=Memory`: un upload da 200 MB, con stream posizionabile e non) controllano che
  il corpo non venga bufferizzato.
- **Mutation testing come pratica**: per una regola nuova (limiti, forma, ritentativi) si applicano a mano mutazioni
  mirate e si controlla che un test le prenda. Si lavora su una **copia** del repository e solo su net10 (lezioni 20 e
  31). Non e' un passo automatico della CI.
- **CI** (`.github/workflows/ci.yml`):
  - su Linux: formato, build, test net8 e net10, pack e `eng/verify-packages.sh`;
  - su Windows: net48, compresi i test di memoria;
  - controllo non bloccante delle vulnerabilita';
  - uno smoke test dei pacchetti con consumatori veri (`eng/pack-smoke/run.sh`): i `.nupkg` installati da un feed locale in
    console net8/net10 (DI e factory) e net48 in C# 7.3 (solo setter: un `init` rompe la build), contro un server finto con
    risposte catturate; controlla anche che net48 risolva le dipendenze al floor 8.0.x.

  `eng/docker-replay.sh` ripete il job Linux in un container, partendo da una copia pulita.
- **End-to-end live** (`tests/Filemaster.IntegrationTests/Live/`, `Category=Live`): una suite opzionale contro un
  Sharp-a-File vero, attivata da `FILEMASTER_E2E_URL` (+ `_KEY`, `_READ_KEY`, `_ADMIN_KEY`). Il server lo porta su
  `eng/e2e/run-e2e.sh`; `.github/workflows/e2e.yml` la esegue di notte. Verde su net8 e net10 il 2026-10-03 (macOS, Azure SQL
  Edge); i risultati sono in [api-contract.md](api-contract.md), "Test contro il server vero".

## Cosa non e' ancora verificato

- **Runtime net48.** In locale (macOS) il target netstandard2.0 si compila, ma non si esegue su .NET Framework. Gira
  solo nel job Windows della CI. In particolare non e' verificato che `HttpClientHandler` non bufferizzi un upload
  grande: esiste il test di memoria da 200 MB, ma deve ancora girare su Windows.
- **Progetti .NET Framework con `packages.config`.** Il pacchetto e' provato con `PackageReference`. I binding redirect
  per `System.Text.Json` e per i pacchetti `Microsoft.Bcl.*` con `packages.config` non sono provati.
- **Fixture e suite live su SQL Server 2022.** Catture e suite live sono state eseguite su Azure SQL Edge (arm64); il
  percorso con SQL Server 2022 su Ubuntu e' `e2e.yml`, non ancora eseguito su GitHub.
- **Comportamenti del server letti nel codice e non misurati.** Esempi: `/readyz` con il database giu' (lezione 44) e
  gli scenari elencati in [api-contract.md](api-contract.md).
- **Server di riferimento.** Il client e' scritto contro Sharp-a-File ramo `dev` (`8aec8bb`). Il ramo `master`
  (`v1.0.x`) non ha codici cartella, `PATCH /folders`, contatti, `created_from`/`created_to`, `has_content` e
  `content-unavailable`.
