# Contratto HTTP di Sharp-a-File (come e' davvero) e come lo gestisce Filemaster

Questo documento descrive l'API HTTP del server Sharp-a-File **come la implementa il codice**, con le sue stranezze, e per
ognuna la scelta di Filemaster. Serve a chi mantiene la libreria e a chi deve capire un errore ricevuto da un'applicazione.

- **Server letto**: `Sharp-a-File` ramo `master`, commit `541f378` ("minor correction", da
  `git -C ../Sharp-a-File log -1 --oneline`). E' lo stesso commit delle fixture (`tests/Filemaster.UnitTests/Wire/Fixtures/README.md`)
  e del pin dell'harness e2e (`.github/workflows/e2e.yml`, `eng/e2e/README.md`). Dal commit `7ca0e7e` ("now the app use autoinc IDs")
  gli id non sono piu' ULID con prefisso (`doc_...`) ma **interi positivi** assegnati dal database; il vecchio riferimento, il ramo
  `dev` al commit `8aec8bb`, e' superato (vedi "ID numerici" fra le stranezze).
- **Fonte di verita'**: il codice. I percorsi dei file del server sono relativi a `Sharp-a-File/src/`; `Web/` sta per
  `SharpAFile.Web/`, `App/` per `SharpAFile.Application/`, `Domain/` per `SharpAFile.Domain/`. Il README del server e' in parte
  obsoleto (vedi "Divergenze fra le fonti"). I numeri di riga di `Web/Hosting/WebSurface.cs`, `Web/Http/Problem.cs` e
  `Domain/Ids.cs` sono riletti su `541f378`; quelli degli altri file sono stati presi da `8aec8bb` e, dove il diff li ha
  toccati (per esempio `App/Documents/DocumentService.cs`, `App/Common/Paging.cs`, `Web/Api/MultipartUpload.cs`), possono essere
  scivolati di qualche riga: fa fede il nome del file e della funzione.
- **Stato di Filemaster**: la fase A e' completa (trasporto, livello wire, adapter delle porte, composizione `AddFilemaster` e
  factory). La suite live (`tests/Filemaster.IntegrationTests/Live/`, 48 test) e' verde su net8 e net10 il 2026-10-09 contro
  `541f378` (id numerici), vedi "Test contro il server vero". Dove una scelta
  vive solo nel piano (`tasks/todo.md`) lo si dice.

## Panoramica

| Tema | Come e' il server | Riferimento |
| --- | --- | --- |
| Descrizione | Nessun OpenAPI, nessun prefisso ne' versione nell'URL: le rotte stanno su `MapGroup("")` | `Web/Hosting/WebSurface.cs:126` |
| JSON | `snake_case` (`SnakeCaseLower`), null **omessi** in uscita, proprieta' sconosciute **rifiutate** in ingresso | `WebSurface.cs:55-60` |
| Enum | Per nome esplicito (`[WireName]`: `active`, `tenant-admin`, `external`...), mai per numero | `Domain/Enums.cs:5-54` |
| Autenticazione | `X-API-Key: saf_...`, in alternativa `Authorization: Bearer saf_...`; l'ente e' sempre quello della chiave | `Web/Api/ApiKeyAuthentication.cs:34-41` |
| Scope | Gerarchici: `read` (1) < `write` (2) < `admin` (3); passa chi ha uno scope >= al minimo | `Domain/Enums.cs:77-81`, `ApiKeyAuthentication.cs:95-99` |
| Correlazione | `X-Request-ID`: se il client lo manda si tengono solo `[A-Za-z0-9._-]`, al massimo 64; altrimenti 16 esadecimali casuali | `Web/Http/HttpPipeline.cs:16-21`, `58-74` |
| Errori | `application/problem+json` `{type:"/problems/<slug>", title, status, detail?, request_id}` | `Web/Http/Problem.cs:13-35` |
| ID | **Interi positivi** assegnati dal database (IDENTITY da 1): `int` per ente, account, chiave API, contatto e webhook, `bigint` per documento e consegna webhook. Numeri JSON nei corpi; decimale canonico (`42`, mai `042` o `+42`) nel percorso e nella query. Le cartelle e le categorie di contatti hanno invece un codice di testo scelto dall'utente | `Domain/Ids.cs`, `Domain/FolderCodes.cs` |
| Date in uscita | UTC con `Z`, da 0 a 6 decimali (zeri finali tagliati) | fixture catturate, `tasks/lessons.md` T0.3 |

Filemaster manda su ogni richiesta `X-API-Key`, `X-Request-ID` (32 esadecimali minuscoli, **lo stesso** per tutti i tentativi di
una chiamata, quindi il server lo tiene com'e') e `User-Agent: Filemaster/<versione> (...)`, mai impostati sull'`HttpClient`
(`src/Filemaster.Infrastructure/Transport/FilemasterTransport.cs`, doc della classe). Usa solo `X-API-Key`.

**Request id sugli errori**: `Problem.WriteAsync` chiama `Response.Clear()` (`Problem.cs:19`), che cancella anche l'intestazione
`X-Request-ID` messa dal middleware; da `541f378` la riscrive subito dopo (`Problem.cs:20-21`), quindi sulle risposte problem+json
l'id e' sia nell'intestazione sia nel campo `request_id` del corpo, con lo stesso valore (misurato nella cattura `t64`). Fino a
`8aec8bb` l'intestazione mancava sugli errori. `ProblemMapper` legge il campo del corpo, con ripiego sull'intestazione
(`Errors/ProblemMapper.cs:65`): vale su entrambe le versioni del server.

## Endpoint della fase A

Scope minimo dalle `RequireAuthorization` di `Web/Api/DocumentEndpoints.cs:19-30` e `Web/Api/ManagementEndpoints.cs:22-54`.
Porta = interfaccia dell'Application (`src/Filemaster.Application/`).

| Metodo | Percorso | Successo | Scope | Porta Filemaster |
| --- | --- | --- | --- | --- |
| `POST` | `/documents` (multipart) | 201, nessun `Location` | write | `IDocumentStore.UploadAsync` |
| `GET` | `/documents` | 200 `{items, next_cursor?}` | read | `IDocumentStore.ListAsync` (+ `EnumerateAsync`, `FindByArxivarDocnumberAsync`) |
| `GET` | `/documents/{id}` | 200 (con `contacts`) | read | `IDocumentStore.GetAsync` |
| `GET` | `/documents/{id}/content` | 200 / 206; 416 senza corpo | read | `IDocumentStore.OpenContentAsync` (+ `OpenVerifiedContentAsync`) |
| `GET` | `/documents/{id}/preview` | 200 / 206, solo tipi ammessi inline | read | `IDocumentStore.OpenPreviewAsync` |
| `DELETE` | `/documents/{id}` | 204 | write | `IDocumentStore.DeleteAsync` |
| `PATCH` | `/documents/{id}/folder` | 204 | write | `IDocumentStore.MoveAsync` |
| `POST` | `/documents/{id}/verify` | 200 `{document_id, sha256, ok, detail?, checked_at}` | **read** | `IDocumentStore.VerifyAsync` |
| `POST` | `/documents/bulk/move` | 200 `{moved}` | write | `IDocumentStore.MoveManyAsync` |
| `POST` | `/documents/bulk/verify` | 200 `{total, verified, failed, without_content}` | **read** | `IDocumentStore.VerifyManyAsync` |
| `POST` | `/folders` | 201 | write | `IFolderCatalog.CreateAsync` |
| `PATCH` | `/folders/{id}` | 200 | write | `IFolderCatalog.UpdateAsync` |
| `GET` | `/folders?parent_id=` | 200 `{items}`, non paginato | read | `IFolderCatalog.ListChildrenAsync` |
| `DELETE` | `/folders/{id}` | 204 | write | `IFolderCatalog.DeleteAsync` |
| `GET` | `/contacts` | 200 `{items, next_cursor?}` | read | `IContactDirectory.ListAsync` (+ `EnumerateAsync`) |
| `GET` | `/contacts/{id}` | 200 | read | `IContactDirectory.GetAsync` |
| `GET` | `/contact-categories` | 200 `{items}`, non paginato | read | `IContactDirectory.ListCategoriesAsync` |
| `GET` | `/tenant` | 200 `{id, slug, name, status, created_at}` | read | `ITenantInfo.GetAsync` |
| `GET` | `/healthz` | 200 `text/plain` `ok` | anonimo | `IFilemasterHealth.CheckLivenessAsync` |
| `GET` | `/readyz` | 200 `{"status":"ready"}` / **503** `{"status":"unavailable","error":...}` | anonimo | `IFilemasterHealth.CheckReadinessAsync` |

Note: `/healthz` e `/readyz` sono `AllowAnonymous` (`WebSurface.cs:116-124`; `/readyz` interroga il database con 3 s di limite).
**Filemaster non manda `X-API-Key` alle sonde**: il server registra un solo schema di autenticazione (`WebSurface.cs:31-32`), che
diventa quello di default e gira su ogni richiesta (`UseAuthentication`, `WebSurface.cs:113`) anche sugli endpoint anonimi; una
chiave inviata verrebbe cercata nel database, e con il database giu' `/readyz` risponderebbe 500 invece di 503 (dedotto dal
codice, non misurato).
`/tenant` e' la sonda di connettivita' + autenticazione. Fase B (non coperta ora): `GET/POST /documents/bulk` (export/import
ZIP), `/audit`, `/accounts`, `/api-keys`, `/webhooks` (`ManagementEndpoints.cs:56-110`, quasi tutti `admin`).

## Errori: status e slug -> eccezione Filemaster

Il server traduce le eccezioni in `ApiExceptionHandler.Map` (`HttpPipeline.cs:100-114`); gli stati senza corpo passano da
`UseStatusCodePages` (`WebSurface.cs:85-112`: 404 -> `not-found`, 405 -> `method-not-allowed`, 413 -> `request-too-large`, ogni altro -> slug generico `error`).
Filemaster applica `ProblemMapper` (`src/Filemaster.Infrastructure/Errors/ProblemMapper.cs`): **prima lo slug, poi lo status** se lo
slug manca o e' sconosciuto, **tranne il 415 che va sempre per status**. Corpo d'errore letto al massimo per 16 KiB
(`ProblemBody.MaxBytes`).

| Status | Slug | Origine nel server | Eccezione Filemaster |
| --- | --- | --- | --- |
| 400 | `validation-error` | `InvalidInputException`, `JsonException`, `BadHttpRequestException` | `InvalidRequestException` |
| 408, 431, altri | `validation-error` | `BadHttpRequestException` con il suo status (`HttpPipeline.cs:111`) | `InvalidRequestException` (vince lo slug, status vero) |
| 401 | `unauthorized` | chiave assente o non valida (`ApiKeyAuthentication.cs:69-73`; con chiave sbagliata niente `detail`) | `UnauthorizedException` |
| 403 | `forbidden` | scope insufficiente o ente sospeso (`ApiKeyAuthentication.cs:113-121`) | `ForbiddenException` |
| 404 | `not-found` | risorsa assente, di un altro ente, **id malformato**, rotta inesistente | `NotFoundException` |
| 405 | `method-not-allowed` | metodo non mappato (anche `HEAD`) | `UnexpectedResponseException` |
| 409 | `conflict` | duplicati, cartella non vuota, lock ARXivar occupato | `ConflictException` |
| 409 | `content-unavailable` | documento senza file (`Domain/Errors.cs:28-29`) | `ContentUnavailableException` |
| 413 | `request-too-large` | limite dello store o limite Kestrel (`HttpPipeline.cs:104`, `110`) | `RequestTooLargeException` (o `ConnectionException`, vedi sotto) |
| 415 | `unsupported-media-type` | anteprima non ammessa (`Errors.cs:32`) | `UnsupportedMediaTypeException` |
| 415 | `validation-error` | Content-Type mancante su un endpoint JSON | `UnsupportedMediaTypeException` (415 per status) |
| 415 | `error` | Content-Type sbagliato (es. `text/plain`), senza `detail` | `UnsupportedMediaTypeException` (415 per status) |
| 416 | nessuno | range non soddisfacibile: **nessun corpo**, `Content-Range: bytes */N` | `UnexpectedResponseException` (`StatusCode` 416) |
| 500 | `internal-error` | ogni eccezione non prevista, `VerifyIoException` compresa | `ServerErrorException` |
| 503 | `storage-not-configured` | password dell'archivio non impostata (`HttpPipeline.cs:109`) | `StorageNotConfiguredException` |
| 5xx | `error` / nessuno / non JSON | proxy, bilanciatore, altri stati | `ServerErrorException` |
| altri | `error` / nessuno | qualunque status non previsto, anche < 400 | `UnexpectedResponseException` |
| 503 | non problem+json | **`/readyz`** con database irraggiungibile | nessuna eccezione: esito della sonda (`HealthProbeResult`) |

Senza risposta: `ConnectionException` (rete, DNS, connessione chiusa) e `FilemasterTimeoutException` (scadenza del client), entrambe
con `StatusCode` 0. L'annullamento del chiamante resta `OperationCanceledException`. Un download riuscito ma troncato o con hash
diverso e' `ContentIntegrityException` (200/206). Una risposta di successo non interpretabile e' `UnexpectedResponseException` con lo
status vero. Le violazioni lato client (id vuoto, campo troppo lungo) sono `ArgumentException` prima della rete.

Il server non emette mai 422 (`tasks/lessons.md` T2a). Il 416 non ha corpo perche' la risposta ha gia' un `Content-Type` e
`UseStatusCodePages` non la tocca (fixture `112-doc-content-range-unsatisfiable`). Il `detail` puo' contenere inglese di ASP.NET e
nomi di tipo .NET: si mostra, non si usa per decidere.

## Limiti del server

| Limite | Valore | Riferimento | Effetto |
| --- | --- | --- | --- |
| Corpo JSON (default Kestrel) | 1 MiB | `Web/Hosting/AppBuilder.cs:18`, `54` | 413 `request-too-large`; vale anche per `bulk/move` e `bulk/verify` (da 50.000 a oltre 100.000 id per chiamata: ogni id pesa le sue cifre, 1-19, piu' la virgola; la stima nel doc di `IDocumentStore.MoveManyAsync` e' stata riscritta di conseguenza) |
| Singolo file | 500 MiB (`SHARPAFILE_MAX_UPLOAD_SIZE_BYTES`) | `Web/Configuration/SharpAFileOptions.cs:16`, `App/Documents/DocumentService.cs:29` | 413 dallo store |
| Corpo dell'upload | file + 1 MiB di margine | `DocumentEndpoints.cs:12`, `35` | oltre: Kestrel chiude |
| Campo multipart | 4096 byte (`metadata`: 64 KiB + 1) | `Web/Api/MultipartUpload.cs:18-19`, `99-100` | 400 |
| Intestazioni di una parte multipart | 16 KiB (default di `MultipartReader`, il server non lo cambia) | `MultipartUpload.cs:67` | 400 `multipart non valido` |
| `metadata` | oggetto JSON <= 64 KiB; filtro <= 64 nodi, <= 16 livelli | `Domain/DocumentMetadata.cs:10-19` | 400 |
| `owner`, `tag` | 255 caratteri dopo il trim | `Domain/Document.cs:214-215` | 400 |
| Pagina | default 50, massimo 200, `limit` <= 0 o > 200 = 400 | `App/Common/Paging.cs:81-98` | 400 |
| Import ZIP (fase B) | 2 GiB | `SharpAFileOptions.cs:17` | 413 |

Filemaster replica i limiti dei campi in `UploadDocumentRequest.Validate()` e `DocumentQuery.Validate()`, chiamati prima di aprire la
connessione (`tasks/todo.md` T3.1). **Non** limita la lunghezza di `FileName`: un nome molto lungo, percent-codificato in
`filename*`, puo' superare i 16 KiB di intestazione della parte (non verificato).

## Stranezze e soluzioni

### ID numerici: 404 su id malformato o non canonico

Dal commit `7ca0e7e` gli id di ente, account, chiave API, documento, contatto e webhook sono interi positivi assegnati dal database
(IDENTITY da 1; un'entita' non ancora salvata ha id 0). Fuori dal processo l'id e' testo e vale solo la forma canonica: cifre decimali
senza segno ne' zeri iniziali (`Ids.TryParse`, `Domain/Ids.cs:15-22`: `NumberStyles.None`, maggiore di zero, e il testo deve
coincidere con la riscrittura del numero). `Ids.Require` (`Ids.cs:26-28`) trasforma un id malformato in `NotFoundException`, non
in 400: "non esiste" come un id di un altro ente. Quindi `/documents/042`, `/documents/+42`, `/documents/0` e il vecchio
`/documents/doc_01...` sono tutti 404 `not-found`. Lo stesso per i codici cartella (`FolderCodes.Require`, `FolderCodes.cs:38-39`) e
per `folder_id`, `sender_id`, `recipient_id`, `category_id` nelle query (`Web/Api/QueryParsing.cs:14-24`, `73`): un `sender_id` o
`recipient_id` ben formato ma sconosciuto non e' un errore, da' 200 con l'elenco vuoto.
Nei corpi JSON gli id sono **numeri** (`{"id":30017}`), mai testi; `bulk/move` e `bulk/verify` vogliono `{"document_ids":[42,43]}`
(le stringhe `doc_...` sono 400; il server tollera anche stringhe numeriche come `["42"]`, ma Filemaster manda numeri). Il cursore
degli elenchi porta l'id numerico: un cursore vecchio (con `doc_...`) e' 400. In `bulk/move` gli id minori o uguali a zero e gli
sconosciuti sono ignorati; in `bulk/verify` bastano uno sconosciuto o un id minore o uguale a zero per avere 404 sull'intero lotto.
**Filemaster**: `DocumentId`, `ContactId` e `TenantId` sono `readonly record struct` che reggono un numero (`long` il documento, `int`
gli altri due). Il costruttore `new DocumentId("42")` rifiuta con `ArgumentException` tutto cio' che il server non accetterebbe
(vuoto, segno, zeri iniziali, spazi, lettere, il vecchio `doc_...`, fuori da `1`..`long.MaxValue` / `int.MaxValue`; null e'
`ArgumentNullException`), quindi un 404 che arriva e' davvero "non trovato". `Value` e' il decimale canonico (testo, come nell'URL),
`Number` il numero, `DocumentId.From(42)` lo crea da un numero (`<= 0` e' `ArgumentOutOfRangeException`); `default` e' l'id vuoto e
non valido (`Value` = stringa vuota). In scrittura i corpi di bulk portano numeri, in lettura si accettano solo numeri interi JSON
(un id come testo, decimale, negativo, zero o fuori intervallo e' una risposta non interpretabile). `FolderCode` resta un testo:
unica divergenza voluta dal server, rifiuta il newline finale che il `$` della regex del server accetta (`tasks/lessons.md` T2a).
Un id `default` (vuoto) e' `ArgumentException` (`Wire/Routes.cs`).

### Corpi JSON rigidi

`UnmappedMemberHandling.Disallow` (`WebSurface.cs:59`) + `ThrowOnBadRequest` (`WebSurface.cs:27`): una proprieta' in piu' e' 400
`validation-error` "corpo JSON non valido". Un server piu' vecchio rifiuta quindi i campi che non conosce.
**Filemaster**: scrive i corpi a mano con `Utf8JsonWriter`, solo i campi del DTO di quella richiesta (`Wire/WireJson.cs`,
`Wire/DocumentWire.cs`); per la radice manda `{"folder_id":null}` esplicito. In lettura legge con `JsonElement` e ignora i campi in
piu' (il server puo' aggiungerne).

### 409 ambiguo

Lo stesso 409 copre casi diversi: slug `conflict` per codice o nome di cartella gia' usato (`SharpAFile.Infrastructure/Persistence/Repositories.cs:224-227`),
cartella non vuota (`:237`), e **lock del collegamento ARXivar occupato** ("riprova tra poco") su eliminazione o cambio di codice di
una cartella (`App/Folders/FolderService.cs:62`, `77`, `87-89`), che e' transitorio; slug `content-unavailable` per un documento senza
file. **Filemaster**: `content-unavailable` -> `ContentUnavailableException`, ogni altro 409 -> `ConflictException`; senza slug un 409 e'
sempre `ConflictException`. Un 409 non si ritenta mai (e le scritture non si ritentano comunque): il motivo e' solo nel `Detail`.

### Validazione tardiva dell'upload

Il server prima scrive l'intero file in un temporaneo (`MultipartUpload.cs:91`), poi valida i campi (`DocumentEndpoints.cs:40-50`) e
solo al commit scopre una cartella inesistente (404, `Persistence/DocumentRepository.cs:130`). I campi possono arrivare anche dopo il
file (`MultipartUpload.cs:13`). Un errore su `owner`, `tag`, `metadata` o `folder_id` arriva quindi solo dopo aver trasmesso tutto.
Sul 413 lo store si ferma a limite + 1 e il server drena il resto per far leggere il 413 (`MultipartUpload.cs:109-116`), ma oltre il
limite di Kestrel (file + 1 MiB) la connessione viene chiusa: il client vede un errore di rete, non il 413.
**Filemaster**: `Validate()` prima di aprire la connessione e di leggere lo stream; parte `file` per ultima (`tasks/todo.md` T4.2);
un 413 a meta' trasferimento puo' emergere come `ConnectionException` (status 0), documentato nel doc di `ConnectionException` e del
trasporto. Lo stesso drenaggio vale per il 503 `storage-not-configured` durante lo staging.

### Nessuna idempotenza sull'upload

`POST /documents` crea **sempre** un documento nuovo; `deduplicated: true` dice solo che il blob (stessi byte, stesso ente) esisteva
gia' (`App/Documents/DocumentService.cs:34-61`). Nessuna chiave di idempotenza, nessun `Location`. Ogni upload scrive audit ed evento
`document.uploaded`. **Filemaster**: l'upload non si ritenta mai (`SendUploadAsync`, `canRetry: false`); dopo un errore di rete
l'esito e' ignoto e la decisione resta a chi chiama (per esempio una ricerca per `metadata`).

### Bulk move ignora, bulk verify fallisce tutto

`MoveManyAsync` scarta in silenzio gli id minori o uguali a zero, sconosciuti o di altri enti e risponde `{moved: N}`
(`DocumentService.cs:176-194`); la cartella di destinazione inesistente e' invece 404 prima di spostare. `VerifyManyAsync` vuole
**tutti** gli id esistenti: un id sconosciuto o minore o uguale a zero e' 404 sull'intero lotto, prima di toccare il disco
(`DocumentService.cs:209-222`; misurato sul server vero a `541f378`). Gli id sono numeri JSON: Filemaster non puo' mandare un id
malformato, perche' `DocumentId` lo rifiuta prima.
Lista vuota: 400 in entrambi. Il `PATCH` singolo invece da' 404 se il documento non c'e' (`DocumentService.cs:166-173`).
**Filemaster**: `MoveManyAsync` restituisce il conteggio (chi vuole sapere quali confronta); `VerifyManyAsync` documenta il 404 di lotto;
gli id ripetuti valgono una volta in entrambi; nessun frazionamento automatico.

### Verify ha effetti: mai ritentata

Ogni verifica registra un `IntegrityCheck` nello storico; se l'esito e' negativo scrive anche l'audit `document.verify.failed` (`Domain/AuditEvent.cs:63`) ed emette
il webhook `document.integrity_failed` (`DocumentService.cs:301-311`). La verifica di lotto scrive sempre un evento di audit
(`:239-240`). Basta lo scope `read`. Un errore di I/O che non e' corruzione diventa 500 (`VerifyIoException`, `Errors.cs:42`).
**Filemaster**: verify e bulk verify sono `POST` e non si ritentano; un hash diverso e' un esito (`IntegrityCheck.Ok` falso), non
un'eccezione; `content-unavailable` -> `ContentUnavailableException`.

### Cursori opachi

Documenti: base64url di `v1|<microsecondi unix>|<id>` (keyset su `created_at`, `id` decrescenti); contatti: `n1|<id>|<nome>`
(`App/Common/Paging.cs:11-79`); l'`id` e' il numero del documento o del contatto. Un cursore non decodificabile, o con un id
nel vecchio formato `doc_...`, e' 400 "cursore non valido" (fixture `94-err-400-cursor-bad`). Il server non
lega il cursore ai filtri: cambiarli fra una pagina e l'altra da' risultati incoerenti senza errore. L'ultima pagina non ha `next_cursor`.
**Filemaster**: il cursore e' una stringa passata cosi' com'e'; `EnumerateAsync` rilancia gli stessi filtri e si ferma con
`UnexpectedResponseException` su un `next_cursor` vuoto, ripetuto o gia' visto. Cartelle e categorie non sono paginate; `/audit`
(fase B) usa `before`, non un cursore.

### Date con e senza fuso

`created_from`/`created_to` (`QueryParsing.cs:32-68`) accettano due forme: una data `yyyy-MM-dd`, letta come giorno intero nel fuso del
server (Europe/Rome, UTC se assente: `Web/LocalTime.cs`) con `created_to` **incluso**; oppure un istante RFC 3339 **con fuso**, con
`created_to` **escluso**. Un istante senza fuso e' 400 (fixture `87-docs-list-created-from-no-offset`). `before` di `/audit` (fase B)
usa invece `DateTimeOffset.TryParse` e accetta anche un istante senza fuso (`QueryParsing.cs:96-107`).
**Filemaster**: `DocumentQuery.CreatedFrom`/`CreatedBefore` sono `DateTimeOffset` e si scrivono sempre in UTC con `Z` e cultura
invariante (`Wire/WireDates.cs`); la forma solo-data non e' esprimibile di proposito (dipende dal fuso del server). In lettura accetta
solo i due formati con fuso del server, mai l'ora locale.

### Metadati arxivar

I documenti collegati da ARXivar hanno in `metadata` un oggetto `arxivar` con `docnumber` (numero) e, se presenti, `categoria`,
`oggetto`, `stato`, `numero`, `data_documento`, `protocollo`, `anno`, `revisione`, `impronta` (`App/Arxivar/ArxivarMapping.cs:81-93`).
Un profilo senza file diventa un documento con `has_content: false` e senza `sha256`: download, anteprima e verifica rispondono 409
`content-unavailable`. Il filtro `metadata` e' un contenimento JSON (numeri confrontati per valore).
**Filemaster**: `GetArxivarMetadata` / `ArxivarMetadata.From` (tollerante, non lancia mai, anche su surrogati isolati);
`FindByArxivarDocnumberAsync` filtra con `metadata={"arxivar":{"docnumber":N}}` e restituisce una lista; `docnumber <= 0` e'
rifiutato dal client anche se il server non controlla il segno (`tasks/todo.md` T3.2). `has_content` si ignora: `HasContent` deriva
da `Sha256`.

### Altre forme da sapere

- Download: `Accept-Ranges: bytes`, `Last-Modified`, `Content-Length`, nessun `ETag`; range suffisso o aperto -> 206, multi-range ->
  200 intero; `Content-Disposition` con ripiego ASCII e `filename*=UTF-8''...` (`Web/Http/ContentDisposition.cs`). Filemaster legge
  `filename*` prima di `filename`, pretende `Content-Range` solo sul 206 e conta i byte contro `Content-Length` (troncamento ->
  `ContentIntegrityException`); la factory (T4.4) non deve abilitare la decompressione automatica.
- `GET /documents/{id}` ha `contacts` (anche `[]`), gli elenchi no; `metadata` assente o `null` diventa `{}` nel client.
- I payload dei webhook non omettono i null, a differenza delle API.

## Composizione e gestore HTTP

- `AddFilemaster(options => ...)` registra un client nominato `"Filemaster"`, le opzioni validate all'avvio (`ValidateOnStart`) e
  `IFilemasterClient` + le 5 porte come singleton; il trasporto chiede un `HttpClient` alla factory **a ogni tentativo**, quindi la
  rotazione dei gestori vale anche per un singleton. `FilemasterClientFactory.Create(options, handler?, loggerFactory?)` e' la strada senza
  DI (.NET Framework): il `FilemasterClient` restituito e' `IDisposable` e va tenuto per tutta la vita dell'applicazione.
- Il gestore primario **non segue i redirect** (la chiave `X-API-Key` verrebbe copiata verso un altro host) e **non decomprime** (il
  conteggio dei byte contro `Content-Length` presuppone il corpo grezzo); un gestore dell'utente che lo fa viene rifiutato.
  L'`HttpClient` deve avere `Timeout` infinito (le scadenze sono del trasporto): altrimenti `ArgumentException`/`InvalidOperationException`.
- **Ogni richiesta non-GET senza corpo parte con un corpo vuoto (`Content-Length: 0`)**: `SocketsHttpHandler` rimanda da solo fino a 4
  volte una richiesta senza `Content` se la connessione si chiude prima della risposta, e cosi' un `DELETE` o un `POST .../verify`
  sarebbero ritentati malgrado la politica qui sotto (`tasks/lessons.md` 48). Il server vero accetta `DELETE` e `verify` con
  `Content-Length: 0` (204 e 200, provato dalla suite live e da `ContractDriftTests`).

## Politica di retry del client

Da `Transport/RetryPolicy.cs` e `Transport/FilemasterTransport.cs`; opzioni in `FilemasterRetryOptions` (`MaxAttempts` 3, da 1 a 10,
il primo compreso; `InitialDelay` 500 ms; `MaxDelay` 10 s).

- Si ritenta **solo un `GET`**, solo **prima** di consegnare la risposta (mai a meta' di uno stream gia' restituito).
- Cause ritentabili: errore di rete, 408, 429, e 502/503/504 **non** problem+json (un proxy). Un 503 problem+json e' il server
  (`storage-not-configured`) e non cambia da solo.
- Mai: 500, 409, ogni altro 4xx, uno status dichiarato atteso dalla richiesta (il 503 di `/readyz`), upload, `DELETE` (un secondo
  tentativo dopo un successo darebbe un 404 fuorviante), `PATCH`, `POST` (verify compresa), timeout del client.
- Attesa: backoff esponenziale `InitialDelay * 2^(n-1)` limitato a `MaxDelay`, con jitter "equal"; `Retry-After` (secondi o data)
  rispettato fino a `MaxDelay`.
- `RequestTimeout` (30 s) e' **una** scadenza per l'intera chiamata, tentativi e attese compresi; `TransferTimeout` (30 min) copre
  download dopo le intestazioni e upload. L'`HttpClient` deve avere `Timeout` infinito.
- Il server non emette 429 sulle API: i test 429 sono derivati.

## Webhook

| Elemento | Valore | Riferimento |
| --- | --- | --- |
| Intestazioni | `X-SharpAFile-Signature`, `X-SharpAFile-Event`, `X-SharpAFile-Delivery` | `App/Common/Secrets.cs:36-38` |
| Firma | `t=<secondi unix>,v1=<HMAC-SHA256 esadecimale minuscolo>` | `Secrets.cs:40-48` |
| Messaggio firmato | byte UTF-8 di `"<t>."` seguiti dai byte **grezzi** del corpo | `Secrets.cs:42-45` |
| Chiave | UTF-8 dell'**intero** segreto, prefisso `whsec_` compreso, non decodificato | `Secrets.cs:11`, `18`, `46` |
| `t` | l'ora dell'invio di ogni tentativo (si rifirma a ogni ritentativo) | `App/Webhooks/WebhookDispatcher.cs:65` |
| Busta | `{event, delivery_id, occurred_at, payload}`; `delivery_id` e' un **numero JSON** (`bigint`, prima era `whd_`+ULID), uguale fra i tentativi | `tasks/lessons.md` T2b, `WebhookDispatcher.BuildBody` |
| Tentativi del server | al massimo 8 | `Domain/Webhook.cs:71` |

Eventi: `document.uploaded` `{document_id, filename, sha256, deduplicated}` (`filename`, non `original_filename`),
`document.deleted` `{document_id, sha256}`, `document.integrity_failed` `{document_id, sha256, detail}` (`DocumentService.cs:59-60`,
`162`, `308-309`). **`document_id` e' un numero JSON in `uploaded` e `integrity_failed`, ma il testo delle cifre (`"42"`) in
`deleted`**, perche' `DeleteAsync` pubblica l'id della rotta, che e' una stringa (letto nel codice, non catturato: servirebbe un
ricevitore). L'intestazione `X-SharpAFile-Delivery` ha le stesse cifre di `delivery_id`, come testo.

**Filemaster** (`src/Filemaster.Application/Webhooks/`): `WebhookSignatureVerifier(secret, tolerance = 5 min, TimeProvider?)` ->
`Valid | MissingHeader | MalformedHeader | TimestampOutOfTolerance | SignatureMismatch`; firma controllata prima della finestra;
tutti i candidati `v1` confrontati a tempo costante; parti sconosciute dell'intestazione tollerate; il messaggio usa il testo grezzo di
`t`; tolleranza in secondi interi, nei due versi. `WebhookEventParser`: un evento noto con payload malformato diventa
`UnknownWebhookEvent` (mai un'eccezione che farebbe ritentare il server); `occurred_at` senza fuso = busta non conforme; per
deduplicare si usa `delivery_id` del corpo verificato, non l'intestazione (non firmata). `WebhookEvent.DeliveryId` resta una
**stringa** (e' solo una chiave di deduplicazione): il parser accetta `delivery_id` come numero JSON (la forma del server) o come
stringa non vuota, e `document_id` come numero o come stringa di cifre canoniche (le due forme del server, vedi sopra); un `document_id`
che non e' un id valido rende l'evento `UnknownWebhookEvent`.

## Divergenze fra le fonti

- **README del server vs codice**: a `8aec8bb` il README diceva che "ogni risposta porta `X-Request-ID`", ma le risposte problem+json
  non lo avevano (`Response.Clear()`, confermato dalle catture); a `541f378` il codice rimette l'intestazione (`Problem.cs:20-21`) e
  la divergenza e' chiusa. Il vecchio README elencava anche il prefisso `fld_` fra gli ID, che nel codice di `dev` non esisteva (le
  cartelle hanno un codice utente), e a `541f378` nessun prefisso e' piu' in uso: gli id sono numeri. Il README non dice che la chiave
  HMAC e' il segreto **con** `whsec_`.
- **Piano vs codice del server su verify**: `tasks/todo.md` (Architettura) dice "verify scrive audit e webhook"; per la verifica
  singola audit e webhook ci sono solo se l'esito e' negativo, lo storico sempre (`DocumentService.cs:301-311`). La conclusione (non
  ritentare) non cambia; il doc di `IDocumentStore.VerifyAsync` e' esatto.
- **Piano su `/readyz`**: `tasks/todo.md` (Architettura) dice "retry solo per GET (e `healthz`/`readyz`)", T4.1 aggiunge "mai su un 503
  di `/readyz`": il 503 di `/readyz` e' uno status atteso e non si ritenta; gli errori di rete su `/readyz` si'.
- **503 di `/readyz` che non e' la risposta della sonda** (deciso in T4.3b): passa da `ProblemMapper` (di norma
  `ServerErrorException`, o l'eccezione dello slug, con il request id) e non si ritenta; il 503 `{"status":"unavailable"}` resta un
  esito. I 502/504 di un proxy davanti a `/readyz` si ritentano come ogni `GET`.

## Test contro il server vero

- **Harness**: `eng/e2e/` (vedi `eng/e2e/README.md`) costruisce Sharp-a-File dal suo `Dockerfile` al commit fissato, lo porta su con
  SQL Server (Azure SQL Edge su Apple Silicon con `--mac`), crea ente e tre chiavi (`read`, `write`, `admin`) e semina via SQL i dati che le
  API non sanno creare: una categoria, tre contatti (uno per tipo, un nome non ASCII) e un documento senza contenuto collegato ai tre.
  `.github/workflows/e2e.yml` fa lo stesso su Ubuntu con SQL Server 2022 (manuale e notturno, non bloccante).
- **Suite live** (`Category=Live`, opt-in con `FILEMASTER_E2E_URL`; `FILEMASTER_E2E_REQUIRED=1` trasforma lo "skip" in errore):
  round-trip completo dei documenti, cartelle, contatti, ARXivar, sonde, contratto degli errori e `ContractDriftTests` (forme grezze).
  Ultimo esito, del 2026-10-09 su macOS/Azure SQL Edge contro `541f378` (id numerici): **48 test, 96/96 esecuzioni su net10 e su net8,
  cinque corse di fila sullo stesso server** (i test puliscono cio' che creano: alla fine resta solo il documento seminato), nessuna
  risposta 5xx. Fra i 48 c'e' `LiveDocumentFilterTests` (17 test): ogni filtro di `DocumentQuery` ha un test con l'insieme esatto di id
  atteso, calcolato a mano, piu' AND, ordine, paginazione, `EnumerateAsync` e un caso via `AddFilemaster`. Provati con 72 mutanti del
  client (filtri tolti o scambiati, date troncate, cursore ignorato, ...): tutti uccisi, tranne 2 equivalenti dimostrati (il server fa
  gia' il trim e confronta senza distinguere maiuscole). Fino al 2026-10-03 la stessa suite (31 test) era verde contro il server
  vecchio (`8aec8bb`, id con prefisso).
- **Catture**: `eng/e2e/capture-fixtures.sh`. La cattura `t64` del 2026-10-09 contro `541f378` ha 291 richieste e `0 MISMATCH` fra stato atteso
  e vero; confrontata con la precedente (`t63b`, stesso contratto a `8aec8bb`) ha gli stessi status e gli stessi campi: cambia solo
  il tipo degli id, da testo a numero. Le catture vere di contatti, categorie e documento senza contenuto (`captured/258`, `259`, `150`, `151`)
  confermano le forme delle fixture derivate (`CapturedContactsTests`). Le fixture golden del livello wire sono rigenerate da `t64`.
- **Osservazioni sul server** (non difetti del client): una cartella con padre inesistente (404) e l'eliminazione di una cartella non vuota
  (409) passano da un'eccezione del database che il server registra a livello `Error` con lo stack completo; le API mandano il testo non
  ASCII come UTF-8 grezzo, i webhook come escape `\uXXXX`; entrambi i percorsi del 413 (limite dello store e limite del corpo) sono arrivati
  come 413 sul loopback, mai come connessione chiusa (su una rete vera resta possibile, il client lo tratta).

## Non verificato

- Le catture e la suite live sono state eseguite su **Azure SQL Edge** (arm64); SQL Server 2022 su Ubuntu e' il percorso di `e2e.yml`, non
  ancora eseguito su GitHub.
- **net48 eseguito**: compilato ovunque, ma eseguito solo dal job Windows della CI (test unit, memoria 200 MB con il buffering di
  `HttpClientHandler`, pack-smoke net48 con i floor 8.0.x). Prima corsa dei test unit (2026-10-03): 2728/2729 verdi; l'unico rosso era
  un'autoverifica scritta su .NET 10 (`EscapeDataString` su un surrogato isolato lancia `UriFormatException` su net48 invece di dare U+FFFD),
  corretta nel test e non ancora rivista verde. Restano da leggere nel log di quella corsa: `Uri.Query` vuota (mutante M30),
  `EscapeDataString` oltre 65.519 caratteri, STJ 8.0.5 a runtime.
- Le buste **webhook** restano derivate dal codice (catturarle richiede un ricevitore e l'amministrazione dei webhook, fase B).
- Il limite di 16 KiB sulle intestazioni di parte con un `FileName` molto lungo; nessun 429 reale; confronto a tempo costante (non
  misurabile da un test); il 500 di `/readyz` con una chiave e il database giu' (dedotto dal codice: le sonde partono senza chiave).
