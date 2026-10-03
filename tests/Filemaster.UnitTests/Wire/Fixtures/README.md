# Fixture golden del livello wire

Questa cartella contiene i corpi e le intestazioni che il server **Sharp-a-File** (ramo `dev`, commit
`8aec8bb78d34f70150cf7be970c4a712a9847546`) ha restituito davvero, con i dati sensibili sostituiti, piu' alcune risposte
**derivate dal codice del server** dove non esiste una cattura. Sono dati: i test li leggono dalla cartella accanto alla DLL
(`Wire/Fixtures/...`, percorso relativo a `AppContext.BaseDirectory`) e li confrontano con valori scritti a mano.

## `captured/` - catture reali, scrubbate

- **Da dove vengono.** Una cattura del 2026-10-01 (spike T0.3) contro un Sharp-a-File vero, costruito dal suo `Dockerfile` al commit
  sopra, con un ente e tre chiavi usa-e-getta, su Azure SQL Edge (arm64: SQL Server 2022 non gira in emulazione su quel Mac). Le risposte
  HTTP sono output vero del server; **vanno ricatturate alla prima esecuzione su Ubuntu con SQL Server 2022** (T6.3) prima di dirle
  definitive. Il ramo e' `dev`, non `master`: `master` non ha contatti, `PATCH /folders`, `created_from`/`created_to` ne' `has_content`.
- **Che cosa c'e'.** Per ogni cattura il nome e' quello originale (`47-doc-upload`):
  `.json` = corpo della risposta (byte per byte, nessuna ri-serializzazione); `.txt` = corpo non JSON (`/healthz`);
  `.request` = metodo, percorso (con la query come e' stata spedita) e, dopo `# curl: -d`, il corpo JSON che il server ha accettato
  (e' l'oracolo dei costruttori di richieste); `.status` = riga di stato; `.headers` = intestazioni della risposta (solo per i download).
  `INDEX.tsv` (accanto a questo file) riassume ogni cattura: nome, metodo, percorso, status, tipo, byte.
- **Selezione.** Un insieme minimo ma completo di risposte di successo: ente, cartelle (crea, figlio, elenco, rinomina, cambio di codice, elenco
  vuoto), caricamenti (con e senza metadati, deduplicato, testo, nome non ASCII), dettagli di documento (con e senza cartella, nome non ASCII),
  elenchi (default, due pagine con cursore, ultima pagina, filtri), verifica, verifica e spostamento in blocco, sonde di salute (anche il 503 di
  `/readyz`), intestazioni dei download (200, 206 nelle tre forme, 416, multi-range ignorato, nome non ASCII, anteprima). Nessuna risposta
  d'errore problem+json (le copre T4.1) e nessuna cattura di chiavi API, account, webhook, audit o export.
- **Come sono state scrubbate.** Con uno script (`scrub42.py`, fuori dal repo: la versione vera e' T6.3) che lavora sui byte e conta le
  sostituzioni per regola: chiavi API (qualunque valore col prefisso delle chiavi) -> `saf_FakeKeyForTestsOnly_0123456789abcdef`; segreti webhook (idem) ->
  `whsec_NotARealSecretTestVectorOnly0123`; email -> `user@example.test`; slug dell'ente -> `acme-test` e nome -> `Acme Test`; percorsi locali delle
  prove (`/var/folders/...`) -> `fixtures/`; il proprietario di prova (un nome proprio dell'ambiente, non un segreto) -> `maria`, ovunque (corpi,
  moduli, query). Gli id (`doc_`, `fld_`, `ten_`) restano: sono ULID validi di un ambiente usa-e-getta e servono a provare le regole degli id.
  I `sha256` e le dimensioni restano veri: servono a provare le regole sui contenuti. Dopo lo scrub lo script controlla che non resti nessun percorso
  locale, nome utente, email, prefisso di chiave o letterale della lista dei valori vietati.
- **Valori e fatti che i test danno per assodati** (da leggere nei file, non da ricalcolare): `created_at` e' UTC con la `Z` e da 0 a 6 decimali
  (gli zeri finali sono tagliati); i null sono omessi; `metadata` e' sempre presente (`{}` se vuoto) e `has_content` e' presente; un documento
  di un elenco non ha `contacts`, quello di un dettaglio si' (`[]`); l'ultima pagina non ha `next_cursor`; il cursore e' base64url senza padding.

## `derived/` - risposte derivate, NON catturate

Per queste forme il server `dev` non ha restituito nessun corpo nelle catture (nessun contatto, categoria o webhook nei dati di prova, nessun
documento senza contenuto, nessun contatto collegato a un documento). Sono scritte a mano a partire dai DTO e dal codice del server
(`Api/Dtos.cs`, `Domain/Contact.cs`, `Domain/Webhook.cs`, `Domain/Enums.cs` al commit sopra), con la stessa politica del server
(`snake_case`, null omessi) e valori inventati (ULID validi, email `@example.test`). **Vanno sostituite da catture vere** appena T6.3 / `e2e.yml`
le producono: finche' non succede sono "derivate, non catturate" e i test che le usano lo dicono.

| File | Forma | Fonte nel server |
| --- | --- | --- |
| `doc-detail-with-contacts.json` | dettaglio di un documento con `contacts` (`role`, `id`, `name`) | `DocumentDto`, `DocumentContactDto`, `ContactRole` |
| `doc-detail-without-content.json` | documento importato con i soli metadati: niente `sha256`, `size_bytes` 0, `has_content` false | `Document.CreateWithoutContent`, `DocumentDto` |
| `contacts-page.json` | pagina di contatti (uno completo con i due contatori, uno minimo) con `next_cursor` | `ContactDto`, `ItemsDto`, `Wire<ContactKind>` |
| `contact-detail.json` | dettaglio di un contatto: nessun contatore dei documenti | `ContactDto` |
| `contact-categories.json` | elenco non paginato delle categorie | `ContactCategoryDto` |
| `webhook-document-uploaded.json`, `webhook-document-deleted.json`, `webhook-document-integrity-failed.json` | buste webhook `{event, delivery_id, occurred_at, payload}` (il payload non omette i null e usa `filename`) | `WebhookDelivery`, `ActivityRecorder` (letti in T3.3) |

### Confronto con le catture vere (T6.3, 2026-10-03)

Le catture `captured/301-304` (pagina di contatti, dettaglio di un contatto, categorie, documento senza contenuto collegato a tre contatti) vengono
dai dati seminati da `eng/e2e/seed.sh` sullo stesso commit `dev`, su Azure SQL Edge; le email sono scrubbate come sopra. Le fixture derivate restano
l'oracolo dei test di dettaglio (hanno piu' campi valorizzati), ma `CapturedContactsTests` controlla che i lettori leggano i corpi veri e che
**ogni nome di campo mandato dal server esista nella fixture derivata** (per il dettaglio di un contatto l'oracolo e' il contatto completo della
pagina derivata). Esito: nessuna differenza di forma. Fatto nuovo: le API mandano il non ASCII come UTF-8 grezzo (`Citt\xc3\xa0`), non come escape.
**Restano derivate e mai confrontate** le tre buste webhook: per catturarle serve un ricevitore e l'amministrazione dei webhook (Fase B).

## Come si usano

- `WireFixtures` (in `tests/Filemaster.UnitTests/Wire/WireTestSupport.cs`) legge i file; `Variants` ne deriva una variante togliendo o cambiando UNA
  proprieta' con `System.Text.Json.Nodes`, cosi' l'oracolo resta il file e non il codice sotto prova.
- Il csproj dei test li copia accanto alla DLL con `<None Update="Wire\Fixtures\**" CopyToOutputDirectory="PreserveNewest" />`.
- Prima di aggiungere una cattura: passarla dallo scrubbing (stesse regole sopra), cercare i prefissi delle chiavi e dei segreti webhook in tutto il repo e controllare che restino solo i valori
  finti, e rileggere i valori scritti nei test (gli id, gli hash e le date sono quelli del file).
