# Filemaster

Filemaster e' la libreria client .NET per l'API HTTP di **Sharp-a-File**, il server di archiviazione documentale (con le
funzioni di migrazione da ARXivar). Nasconde l'API "nuda" del server (JSON snake_case, ID numerici, cursori opachi,
upload multipart, errori `problem+json`, webhook firmati HMAC) dietro interfacce tipizzate, con tempi, ritentativi e
controlli d'integrita' gia' decisi. E' pensata per i gestionali che archiviano documenti: ASP.NET Core, ma anche
applicazioni .NET Framework 4.8 in C# 7.3 o VB.NET.

> **Stato: 0.x.** L'API pubblica puo' ancora cambiare tra una versione minore e l'altra. Il client e' scritto contro
> Sharp-a-File ramo `master` (commit `541f378`), dove documenti, contatti ed enti hanno id **numerici** (interi positivi,
> non piu' gli ULID con prefisso `doc_...`, `con_...`): `DocumentId`, `ContactId` e `TenantId` sono numeri e rifiutano il
> vecchio formato. Vedi [Stato e roadmap](#stato-e-roadmap).

## Compatibilita'

| Applicazione | Asset del pacchetto | Note |
| --- | --- | --- |
| .NET 10 | `net10.0` | |
| .NET 8 (e 9) | `net8.0` | .NET 8 esce dal supporto Microsoft il 10 novembre 2026 |
| .NET Framework 4.8 | `netstandard2.0` | Test nel job Windows della CI; meglio un progetto con `PackageReference` |
| Altri runtime compatibili con `netstandard2.0` | `netstandard2.0` | Non provati |

**Da C# 7.3** (il default di un progetto .NET Framework) e **da VB.NET** si usa tutto, con due differenze:

- `await foreach` non esiste (C# 8) e VB non ha un equivalente: invece di `EnumerateAsync` si scorrono le pagine con
  `ListAsync` e il cursore (esempio sotto).
- Non serve `init` ne' `with`: i tipi di input (`FilemasterOptions`, `UploadDocumentRequest`, `DocumentQuery`,
  `ContactQuery`, richieste delle cartelle) sono classi con costruttore e proprieta' `get; set;`; i risultati sono
  in sola lettura. Un test di forma impedisce `init` sui tipi pubblici dell'Application.

**Server**: Sharp-a-File ramo `master`, commit `541f378` (contratto descritto in
[docs/api-contract.md](https://github.com/Vinello28/Filemaster/blob/main/docs/api-contract.md)). Gli id sono numeri: un
Sharp-a-File con il vecchio formato (`doc_...`, ULID con prefisso) non e' supportato.

## Quale pacchetto, dove

I quattro pacchetti escono sempre insieme con la stessa versione e si richiedono a vicenda con versione esatta
(`[x.y.z]`): non si mescolano versioni diverse.

| Pacchetto | Contenuto | Chi lo referenzia |
| --- | --- | --- |
| `Filemaster` | `AddFilemaster` e `FilemasterClientFactory`; porta gli altri tre | Il progetto host (sito, servizio, desktop) |
| `Filemaster.Application` | Porte, richieste, estensioni, verifica e lettura dei webhook | Strato applicativo, ricevitori di webhook |
| `Filemaster.Domain` | ID tipizzati, entita', eccezioni, eventi webhook | Arriva con `Application` |
| `Filemaster.Infrastructure` | Adapter HTTP: pubblici solo le opzioni e `FilemasterHttp` | Arriva con `Filemaster` |

Lo strato applicativo dipende solo dalle porte (`IFilemasterClient`, `IDocumentStore`, `IFolderCatalog`,
`IContactDirectory`, `ITenantInfo`, `IFilemasterHealth`), che nei test si sostituiscono con dei finti; un ricevitore di
webhook non ha bisogno di `HttpClient`. `FilemasterHttp.CreateClient` serve solo a chi vuole comporre il client su un
proprio `HttpClient`.

## Installazione

```
dotnet add package Filemaster
```

In Visual Studio con un progetto .NET Framework: `Install-Package Filemaster` dalla Package Manager Console. Per una
versione `-rc` aggiungere `--prerelease` (o `-Prerelease`).

## Avvio rapido

### ASP.NET Core (iniezione delle dipendenze)

`AddFilemaster` registra `IFilemasterClient` e le cinque porte come singleton: si iniettano ovunque, anche in altri
singleton. Opzioni non valide fanno fallire l'avvio dell'host.

```csharp
var builder = WebApplication.CreateBuilder(args);

builder.Services.AddFilemaster(options =>
{
    options.BaseAddress = new Uri(builder.Configuration["Filemaster:BaseAddress"]!);
    options.ApiKey = builder.Configuration["Filemaster:ApiKey"];
});

var app = builder.Build();

app.MapGet("/documenti/{id}", async (string id, IDocumentStore documents, CancellationToken ct) =>
{
    if (!DocumentId.TryParse(id, out var documentId))
    {
        return Results.NotFound();
    }

    Document document = await documents.GetAsync(documentId, ct);
    return Results.Ok(new { id = document.Id.Number, document.OriginalFilename, document.SizeBytes });
});

app.Run();
```

La chiave API va in un segreto (user secrets, variabile d'ambiente, vault), non in `appsettings.json` sotto controllo di
versione.

### .NET Framework, WinForms, servizi Windows (senza DI)

Un client **per applicazione**, creato all'avvio e smaltito alla chiusura. C# 7.3:

```csharp
// All'avvio dell'applicazione (Main, Application_Start, OnStart del servizio), una volta sola.
var options = new FilemasterOptions();
options.BaseAddress = new Uri("https://archivio.example.test/");
options.ApiKey = apiKey;
_client = FilemasterClientFactory.Create(options); // campo statico: lo stesso client per tutta l'applicazione

// Alla chiusura dell'applicazione, una volta sola.
_client.Dispose();
```

Lo stesso in VB.NET:

```vb
Public Client As FilemasterClient ' lo stesso client per tutta l'applicazione

Public Sub Startup(apiKey As String) ' all'avvio, una volta sola
    Dim options As New FilemasterOptions With {
        .BaseAddress = New Uri("https://archivio.example.test/"),
        .ApiKey = apiKey
    }
    Client = FilemasterClientFactory.Create(options)
End Sub

Public Sub Shutdown() ' alla chiusura, una volta sola
    Client.Dispose()
End Sub
```

## Operazioni comuni

Tutti i metodi sono asincroni e accettano un `CancellationToken` come ultimo argomento (omesso negli esempi). Gli
esempi C# senza `await foreach` compilano anche come C# 7.3.

### Caricare un documento

```csharp
using (FileStream file = File.OpenRead(@"C:\scansioni\fattura-2026-0042.pdf"))
using (JsonDocument metadata = JsonDocument.Parse("{\"cliente\":\"C0042\",\"anno\":2026}"))
{
    var request = new UploadDocumentRequest(file, "fattura-2026-0042.pdf");
    request.ContentType = "application/pdf";
    request.FolderId = new FolderCode("FATTURE");
    request.Owner = "amministrazione";
    request.Tag = "fattura-attiva";
    request.Metadata = metadata.RootElement;

    UploadResult result = await client.Documents.UploadAsync(request);
    Console.WriteLine(result.Document.Id + (result.Deduplicated ? " (contenuto gia' presente)" : ""));
}
```

Il client non chiude lo stream e lo legge dalla posizione corrente, in streaming. `Validate()` della richiesta (limiti
del server: owner e tag 255 caratteri, campi 4096 byte, metadati 64 KiB) parte prima di aprire la connessione. Un
caricamento **non si ritenta mai** e il server non ha idempotenza: dopo un errore di rete l'esito e' ignoto, e
ricaricare crea un secondo documento (con `Deduplicated` vero).

### Elencare con filtri e pagine

Con C# 8 o successivo, `EnumerateAsync` scorre da solo tutte le pagine:

```csharp
var query = new DocumentQuery
{
    FolderId = new FolderCode("FATTURE"),
    Tag = "fattura-attiva",
    CreatedFrom = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.FromHours(1)),
};

await foreach (Document document in client.Documents.EnumerateAsync(query, pageSize: 200))
{
    Console.WriteLine(document.Id + " " + document.OriginalFilename);
}
```

Da C# 7.3, con il cursore:

```csharp
var query = new DocumentQuery();
query.FolderId = new FolderCode("FATTURE");
query.Tag = "fattura-attiva";
query.CreatedFrom = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.FromHours(1));

string cursor = null;
do
{
    Page<Document> page = await client.Documents.ListAsync(query, new PageRequest(cursor, 200));
    foreach (Document document in page.Items)
    {
        Console.WriteLine(document.Id + " " + document.OriginalFilename);
    }

    cursor = page.NextCursor;
}
while (cursor != null);
```

In VB.NET:

```vb
Dim query As New DocumentQuery With {
    .FolderId = New FolderCode("FATTURE"),
    .Tag = "fattura-attiva",
    .CreatedFrom = New DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.FromHours(1))
}

Dim cursor As String = Nothing
Do
    Dim result As Page(Of Document) = Await client.Documents.ListAsync(query, New PageRequest(cursor, 200))
    For Each doc As Document In result.Items
        Console.WriteLine(doc.Id.Value & " " & doc.OriginalFilename)
    Next
    cursor = result.NextCursor
Loop While cursor IsNot Nothing
```

I filtri sono in `AND`. `CreatedFrom` e' incluso, `CreatedBefore` escluso; entrambi sono istanti con fuso
(`DateTimeOffset`). Il cursore e' opaco: si ripassa cosi' com'e', con gli stessi filtri. Pagina da 1 a 200 elementi
(default del server 50). I documenti di un elenco non hanno i contatti: per quelli si usa `GetAsync`.

### Leggere e scaricare

```csharp
Document document = await client.Documents.GetAsync(new DocumentId(id));
if (!document.HasContent)
{
    return; // importato da ARXivar con i soli metadati: niente da scaricare (409 content-unavailable)
}

// 1) Tutto il contenuto; dimensione e SHA-256 controllati alla fine (ContentIntegrityException: file da buttare).
using (DocumentContent content = await client.Documents.OpenVerifiedContentAsync(document))
using (FileStream output = File.Create(Path.Combine(@"C:\temp", Path.GetFileName(document.OriginalFilename))))
{
    await content.Content.CopyToAsync(output);
}

// 2) Solo i primi 4 KiB (HTTP Range): head.IsPartial e head.Range dicono cosa e' arrivato.
using (DocumentContent head = await client.Documents.OpenContentAsync(document.Id, ByteRange.Between(0, 4095)))
{
    Console.WriteLine(head.IsPartial ? head.Range.TotalLength + " byte in tutto" : "contenuto intero");
}
```

`DocumentContent` va **sempre** smaltito: finche' e' aperto tiene occupata una connessione. Un download interrotto a
meta' lancia `ContentIntegrityException` (con `IsTruncated` vero) durante la lettura, su ogni runtime;
`OpenVerifiedContentAsync` controlla anche lo SHA-256 e la dimensione registrati, ma solo alla fine della lettura.
L'anteprima PDF si apre con `OpenPreviewAsync`.

Per inoltrare un file al browser da ASP.NET Core senza copiarlo in memoria:

```csharp
app.MapGet("/documenti/{id}/file", async (
    string id, IDocumentStore documents, HttpContext http, CancellationToken ct) =>
{
    if (!DocumentId.TryParse(id, out var documentId))
    {
        return Results.NotFound();
    }

    DocumentContent content = await documents.OpenContentAsync(documentId, cancellationToken: ct);
    http.Response.RegisterForDispose(content); // rilascia la connessione verso Sharp-a-File a fine risposta
    return Results.Stream(content.Content, content.ContentType, content.FileName);
});
```

### Spostare, verificare, cancellare

```csharp
var id = new DocumentId(idText);
await client.Documents.MoveAsync(id, new FolderCode("ARCHIVIO-2025"));
await client.Documents.MoveAsync(id, null); // di nuovo nella radice

IntegrityCheck check = await client.Documents.VerifyAsync(id); // registra l'esito sul server
Console.WriteLine(check.Ok ? "integro" : "alterato o mancante: " + check.Detail);

BulkVerifyResult bulk = await client.Documents.VerifyManyAsync(new[] { id }); // anche MoveManyAsync

await client.Documents.DeleteAsync(id); // non idempotente: la seconda volta e' NotFoundException
```

La verifica ha effetti sul server (storico e, se negativa, evento webhook `document.integrity_failed`): come ogni
scrittura non si ritenta da sola. `MoveManyAsync` ignora gli id sconosciuti e restituisce quanti ne ha spostati;
`VerifyManyAsync` fallisce l'intero lotto con `NotFoundException` se un id non esiste.

### Gli id

Il server identifica documenti, contatti ed enti con interi positivi (`bigint` per i documenti, `int` per gli altri): sul filo
sono numeri JSON, negli indirizzi decimali canonici (`/documents/42`). `DocumentId`, `ContactId` e `TenantId` li incapsulano e
rifiutano subito, con `ArgumentException` e senza toccare la rete, tutto cio' che il server risponderebbe con un 404: testo vuoto,
segno, zeri iniziali (`042`), spazi, lettere, il vecchio formato `doc_...`, zero e valori fuori intervallo.

```csharp
DocumentId id = DocumentId.From(42);        // da un numero maggiore di zero (altrimenti ArgumentOutOfRangeException)
long number = id.Number;                    // 42
string text = id.Value;                     // "42": il decimale canonico, quello degli indirizzi
bool valid = DocumentId.TryParse("042", out id); // falso: zeri iniziali; id diventa default (vuoto, non valido)
```

In VB.NET:

```vb
Dim id As DocumentId = DocumentId.From(42)
Dim number As Long = id.Number
Dim text As String = id.Value
```

`default(DocumentId)` e' l'id vuoto (`Value` e' la stringa vuota) e non si puo' passare ai metodi. Le cartelle e le categorie di
contatti non sono numeri: hanno un codice di testo scelto da chi le crea (`FolderCode`).

### Cartelle e contatti

```csharp
var invoices = new FolderCode("FATTURE");
var year = new CreateFolderRequest(new FolderCode("FATTURE-2026"), "Fatture 2026");
year.ParentId = invoices;
await client.Folders.CreateAsync(year);
IReadOnlyList<Folder> children = await client.Folders.ListChildrenAsync(invoices); // null = primo livello

var rename = new UpdateFolderRequest();
rename.Name = "Fatture attive";
await client.Folders.UpdateAsync(invoices, rename);
await client.Folders.DeleteAsync(new FolderCode("FATTURE-2026")); // ConflictException se non e' vuota

var contacts = new ContactQuery();
contacts.Text = "rossi";
contacts.Kind = ContactKind.External;
Page<Contact> page = await client.Contacts.ListAsync(contacts);
IReadOnlyList<ContactCategory> categories = await client.Contacts.ListCategoriesAsync();
```

Le cartelle hanno un **codice** scelto da chi le crea (`FolderCode`: fino a 50 caratteri ASCII, il primo alfanumerico,
poi alfanumerici, `_`, `.` e `-`; maiuscole e minuscole distinte) e un nome per le persone. I contatti sono in sola
lettura.

### Sonde: server e chiave

```csharp
HealthProbeResult ready = await client.Health.CheckReadinessAsync(); // anonima: non manda la chiave
Console.WriteLine(ready.IsHealthy ? "server pronto" : "server non pronto: " + ready.Detail);

Tenant tenant = await client.Tenant.GetAsync(); // UnauthorizedException se la chiave e' sbagliata
Console.WriteLine("Collegato all'ente " + tenant.Name + " (" + tenant.Status + ")");
```

`CheckLivenessAsync` (`/healthz`) e `CheckReadinessAsync` (`/readyz`) sono anonime: dicono se il server risponde, non
se la chiave e' valida. Un server non pronto e' un risultato (`IsHealthy` falso), non un'eccezione.
`client.Tenant.GetAsync()` e' la prova di configurazione da fare all'avvio.

## Documenti ARXivar

I documenti importati da ARXivar hanno nei metadati una sezione `arxivar` con il `DOCNUMBER` e, se presenti, protocollo,
categoria, oggetto, data e altri campi del profilo.

```csharp
IReadOnlyList<Document> found = await client.Documents.FindByArxivarDocnumberAsync(61617);
foreach (Document document in found)
{
    ArxivarMetadata arxivar = document.GetArxivarMetadata();
    if (arxivar != null)
    {
        Console.WriteLine(arxivar.Docnumber + " prot. " + arxivar.Protocol + " del " + arxivar.DocumentDate);
    }
}
```

`FindByArxivarDocnumberAsync` filtra `metadata={"arxivar":{"docnumber":N}}`, legge tutte le pagine e restituisce una
lista (un documento caricato via API con gli stessi metadati compare anche lui). `GetArxivarMetadata` non lancia mai:
restituisce null se la sezione manca o non e' valida. Un profilo importato senza file ha `HasContent` falso: si cerca,
si sposta e si cancella, ma non si scarica ne' si verifica.

## Webhook

Il server firma ogni consegna con HMAC-SHA256 (header `X-SharpAFile-Signature`). La firma e' calcolata sui **byte
grezzi** del corpo: il corpo si legge come `byte[]`, si verifica, e solo dopo si interpreta. Un model binding JSON prima
della verifica (o una ricodifica del testo) rompe la firma. Basta il pacchetto `Filemaster.Application`.

In ASP.NET Core, la registrazione del verificatore (il segreto `whsec_...` intero, prefisso compreso):

```csharp
builder.Services.AddSingleton(new WebhookSignatureVerifier(builder.Configuration["Filemaster:WebhookSecret"]!));
```

e l'endpoint:

```csharp
app.MapPost("/webhooks/sharp-a-file", async (
    HttpRequest request, WebhookSignatureVerifier verifier, CancellationToken ct) =>
{
    // I byte grezzi, prima di qualunque lettura come JSON: la firma e' calcolata su quei byte.
    byte[] body;
    using (var buffer = new MemoryStream())
    {
        await request.Body.CopyToAsync(buffer, ct);
        body = buffer.ToArray();
    }

    WebhookSignatureResult check = verifier.Verify(request.Headers[WebhookHeaders.Signature], body);
    if (!check.IsValid)
    {
        return Results.Unauthorized(); // check.Failure dice perche' (da registrare nel log)
    }

    if (!WebhookEventParser.TryParse(body, out WebhookEvent? evt))
    {
        return Results.BadRequest();
    }

    switch (evt) // consegna "almeno una volta": deduplicare per evt.DeliveryId prima di agire
    {
        case DocumentUploadedEvent uploaded:
            // uploaded.DocumentId, uploaded.OriginalFilename, uploaded.Sha256 ...
            break;
        case DocumentIntegrityFailedEvent failed:
            // failed.DocumentId, failed.Detail ...
            break;
    }

    return Results.Ok(); // anche per UnknownWebhookEvent, altrimenti il server ritenta
});
```

Fuori da ASP.NET Core (per esempio un handler ASP.NET classico in VB.NET) i passi sono gli stessi: leggere
`Request.InputStream` per intero in un `Byte()`, chiamare `Verify` con l'header e i byte, poi `TryParse`.

Regole da tenere:

- la consegna e' "almeno una volta": si deduplica per `DeliveryId`, letto dal corpo verificato (l'header
  `X-SharpAFile-Delivery` non e' coperto dalla firma); `DeliveryId` e' un testo (le cifre del numero che il server manda in
  `delivery_id`), mentre `DocumentId` degli eventi e' un `DocumentId` numerico;
- un evento sconosciuto o con un payload inatteso arriva come `UnknownWebhookEvent` e va confermato con un 2xx,
  altrimenti il server ritenta;
- la tolleranza sull'orario della firma e' 5 minuti per default
  (`WebhookSignatureVerifier(secret, tolerance, timeProvider)`);
- la creazione degli abbonamenti non e' ancora coperta dal client (fase B, vedi sotto).

## Errori

Ogni errore dopo il contatto con il server e' una sottoclasse di `FilemasterException` (namespace `Filemaster.Domain`),
con `StatusCode` (0 se non c'e' risposta), `ProblemType` (lo slug del server), `RequestId` (da dare a chi gestisce il
server) e `Detail` (da mostrare o registrare, non da confrontare). Gli argomenti non validi sono `ArgumentException`,
lanciate prima di toccare la rete; l'annullamento del chiamante resta `OperationCanceledException`.

| Eccezione | Quando |
| --- | --- |
| `NotFoundException` | 404: risorsa assente, di un altro ente, gia' cancellata |
| `ConflictException` | 409: codice gia' in uso, cartella non vuota |
| `ContentUnavailableException` | 409 `content-unavailable`: documento senza file |
| `InvalidRequestException` | 400: il server rifiuta una regola che il client non conosce |
| `UnauthorizedException` | 401: chiave assente, errata o revocata |
| `ForbiddenException` | 403: scope insufficiente o ente sospeso |
| `RequestTooLargeException` | 413: file oltre il limite del server (default 500 MiB) |
| `UnsupportedMediaTypeException` | 415: anteprima di un documento che non e' PDF |
| `StorageNotConfiguredException` | 503: il server non ha la password dell'archivio |
| `ServerErrorException` | 5xx |
| `ConnectionException` | Nessuna risposta: rete, DNS, connessione chiusa |
| `FilemasterTimeoutException` | Scaduto `RequestTimeout` o `TransferTimeout` |
| `ContentIntegrityException` | Download troncato o con SHA-256 diverso |
| `UnexpectedResponseException` | Risposta non prevista o illeggibile (anche 416: intervallo oltre la fine) |

```csharp
try
{
    await client.Documents.DeleteAsync(new DocumentId(idText));
}
catch (NotFoundException)
{
    // non esiste, e' di un altro ente, oppure e' gia' stato cancellato
}
catch (ConnectionException)
{
    // nessuna risposta: per una scrittura l'esito e' ignoto (il client non l'ha ritentata)
}
catch (FilemasterException ex)
{
    Console.WriteLine(ex.GetType().Name + " " + ex.StatusCode + " " + ex.ProblemType
        + " request-id " + ex.RequestId + ": " + ex.Detail);
}
```

La mappatura completa, con le stranezze del server, e' in
[docs/api-contract.md](https://github.com/Vinello28/Filemaster/blob/main/docs/api-contract.md).

## Opzioni

Si impostano nella lambda di `AddFilemaster` o sull'oggetto passato a `FilemasterClientFactory.Create`.

| Opzione | Default | Significato |
| --- | --- | --- |
| `BaseAddress` | obbligatoria | `http` o `https`, senza query; ammesso un prefisso di percorso (reverse proxy) |
| `ApiKey` | obbligatoria | Mandata come `X-API-Key`; mai nei log, nelle eccezioni o in `ToString()` |
| `RequestTimeout` | 30 s | Una scadenza per l'**intera** chiamata, ritentativi e attese compresi |
| `TransferTimeout` | 30 min | Intero upload, o corpo di un download; anche `Timeout.InfiniteTimeSpan` |
| `Retry.MaxAttempts` | 3 | Tentativi delle sole letture (`GET`), da 1 a 10; `1` spegne il ritentativo |
| `Retry.InitialDelay` | 500 ms | Prima attesa; poi esponenziale con jitter |
| `Retry.MaxDelay` | 10 s | Attesa massima, anche quando il server chiede di piu' con `Retry-After` |

Il client ritenta **solo i `GET`**, solo su errori di rete, 408, 429 e 502/503/504 di un proxy, e mai dopo aver
consegnato la risposta. Upload, cancellazioni, spostamenti e verifiche non si ritentano mai.

Con una propria politica (Polly) sul client HTTP restituito da `AddFilemaster`, spegnere la nostra:

```csharp
IHttpClientBuilder http = builder.Services.AddFilemaster(options =>
{
    options.BaseAddress = new Uri(builder.Configuration["Filemaster:BaseAddress"]!);
    options.ApiKey = builder.Configuration["Filemaster:ApiKey"];
    options.Retry.MaxAttempts = 1; // spegne i ritentativi del client: li fa la politica esterna
});
// http.AddStandardResilienceHandler(...);  // pacchetto Microsoft.Extensions.Http.Resilience
```

Una politica esterna vede ogni richiesta, anche i `POST` e i `DELETE` che il client non ritenta mai, e il suo timeout si
somma ai nostri: va configurata per ritentare solo i `GET` e con tempi compatibili con `TransferTimeout`, altrimenti
interrompe gli upload lunghi. Il gestore primario deve restare senza redirect e senza decompressione automatici: la
libreria rifiuta un `HttpClientHandler` o `SocketsHttpHandler` che li abiliti.

## Durata del client

- **Un client per applicazione**, thread-safe, riusato per tutta la vita del processo. Mai un client per richiesta, mai
  `Dispose` dopo ogni chiamata: si esauriscono le connessioni.
- Con `AddFilemaster` la durata la gestisce il contenitore; il client chiede un `HttpClient` a `IHttpClientFactory`
  a ogni tentativo, quindi la rotazione delle connessioni (e dei DNS) funziona anche dentro un singleton.
- Con `FilemasterClientFactory.Create` il `FilemasterClient` si smaltisce una volta, alla chiusura. Un `handler` passato
  alla factory resta di chi lo ha creato e non viene smaltito dal client.
- Su .NET Framework la factory usa un `HttpClientHandler` con almeno 32 connessioni per server (il default di .NET
  Framework e' 2). Un caricamento molto grande potrebbe essere bufferizzato in memoria dal gestore di sistema: non e'
  ancora verificato; il job Windows della CI ha un test di memoria con un upload da 200 MB su net48.

## Stato e roadmap

- **0.1.x (fase A)**: documenti, cartelle, contatti (sola lettura), ente, sonde di salute, ricezione dei webhook.
  Contratto del server: Sharp-a-File `master` `541f378`, con id numerici. Il passaggio dagli ULID con prefisso agli interi ha
  cambiato `DocumentId`, `ContactId` e `TenantId` (ora numeri: `Value`, `Number`, `From`) e il parser dei webhook (`delivery_id`
  e `document_id` numerici); nessuna versione era ancora uscita, quindi non c'e' una compatibilita' da mantenere.
- **0.2.0 (fase B, pianificata)**: amministrazione (account, chiavi API, abbonamenti webhook), audit, import ed export
  ZIP, rotazione della chiave API senza riavvio.
- Finche' la libreria e' in 0.x, aggiungere un metodo a una porta e' una modifica incompatibile per chi la implementa
  (per esempio un fake nei test): lo dira' il changelog della release.

## Documentazione

- [docs/architecture.md](https://github.com/Vinello28/Filemaster/blob/main/docs/architecture.md): livelli, pattern,
  trasporto, strategia di test.
- [docs/api-contract.md](https://github.com/Vinello28/Filemaster/blob/main/docs/api-contract.md): il contratto HTTP di
  Sharp-a-File com'e' davvero, errori e limiti.
- [docs/publishing.md](https://github.com/Vinello28/Filemaster/blob/main/docs/publishing.md): come si pubblica una
  versione.

## Licenza

Apache-2.0, vedi [LICENSE](https://github.com/Vinello28/Filemaster/blob/main/LICENSE).
