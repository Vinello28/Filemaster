# Fixture golden del livello wire

Questa cartella contiene i corpi e le intestazioni che il server **Sharp-a-File** (ramo `master`, commit `541f378`) ha restituito davvero,
con i dati sensibili sostituiti, piu' alcune risposte **derivate dal codice del server** dove non esiste una cattura. Sono dati: i test li
leggono dalla cartella accanto alla DLL (`Wire/Fixtures/...`, percorso relativo a `AppContext.BaseDirectory`) e li confrontano con valori
scritti a mano.

## `captured/` - catture reali, scrubbate

- **Da dove vengono.** La cattura `t64` del 2026-10-09 (`.e2e/fixtures/t64`, fuori da questa cartella) contro un Sharp-a-File vero al commit
  `541f378` di `master`, con i dati seminati da `eng/e2e/seed.sh`. Le risposte HTTP sono output vero del server. Il server ha ormai tutto
  quello che serviva (contatti, `PATCH /folders`, `created_from`/`created_to`, `has_content`): il vecchio ramo `dev` (commit `8aec8bb`, cattura
  del 2026-10-01) e' superato. **Gli id sono NUMERI**, non piu' testi con prefisso: documenti e consegne dei webhook sono `bigint`, contatti ed
  ente `int`; sul filo sono numeri JSON (`"id":30017`) e negli indirizzi sono decimali canonici (`/documents/30017`). Restano testi scelti
  dall'utente i codici di cartella (`FATTURE`, `FATTURE.2026`) e di categoria di contatto (`E2E-FORNITORI`).
- **Che cosa c'e'.** Per ogni cattura il nome e' quello originale della cattura `t64` (`47-doc-upload`):
  `.json` = corpo della risposta (byte per byte, nessuna ri-serializzazione); `.txt` = corpo non JSON (`/healthz`);
  `.request` = metodo, percorso (con la query come e' stata spedita) e, dopo `# curl: -d`, il corpo JSON che il server ha accettato
  (e' l'oracolo dei costruttori di richieste); `.status` = riga di stato; `.headers` = intestazioni della risposta (solo per i download).
  `INDEX.tsv` (accanto a questo file) riassume ogni cattura: nome, metodo, percorso, status, tipo, byte. Sono 63 catture.
- **Selezione.** Le stesse risposte della selezione precedente (ente, cartelle, caricamenti, dettagli, elenchi con cursore e filtri, verifica,
  verifica e spostamento in blocco, sonde di salute, intestazioni dei download), ricavate dalla `t64` con gli stessi nomi, piu' quelle che ora
  esistono davvero: contatti (`150`, `151`, `152`, e i dati seminati `258` e `259`), i filtri `sender_id`/`recipient_id` (`265`, `266`) e il 503 di
  `/readyz`, che nella `t64` ha il numero `223` (era `224`). Le catture `301-304` della selezione vecchia non esistono piu': i contatti veri sono
  `258` e `259`. Nessuna risposta d'errore problem+json (le copre T4.1) e nessuna cattura di chiavi API, account, webhook, audit o export.
- **Come sono state scrubbate.** Con uno script fuori dal repo, che lavora sui byte, con le stesse regole di prima (riprodotte byte per byte
  sul vecchio insieme prima di applicarle alla `t64`): percorsi locali delle prove (`.../.e2e/work/capture-assets/`) -> `fixtures/`; slug
  dell'ente `e2e` -> `acme-test` e nome `Filemaster E2E` -> `Acme Test`; il proprietario di prova (un nome proprio dell'ambiente, non un segreto)
  -> `maria`, ovunque (corpi, moduli, query); `"pec":"user@example.com"` -> `"pec":"pec@example.test"`; ogni altra email -> `user@example.test`.
  Gli id numerici e le date restano come sono: sono di un ambiente usa-e-getta. I `sha256` e le dimensioni restano veri. Il non ASCII resta UTF-8
  grezzo (`Citt` + a accentata nel nome del fornitore `258`).
- **Fatti della `t64` che i test danno per assodati** (da leggere nei file, non da ricalcolare):
  - `created_at` e' UTC con la `Z` e da 0 a 6 decimali (gli zeri finali sono tagliati); i null sono omessi; `metadata` e' sempre presente (`{}`
    se vuoto) e `has_content` e' presente; un documento di un elenco non ha `contacts`; l'ultima pagina non ha `next_cursor`; il cursore e'
    base64url senza padding (documenti: `v1|<ticks>|<id>`; contatti: `n1|<id>|<nome>`).
  - **Tutti i caricamenti hanno `"deduplicated":true`**, anche il primo (`47`): i byte del PDF c'erano gia' nel magazzino di una corsa precedente
    sullo stesso database. Il caso `false` non e' catturato: i test lo provano con una variante della `47`.
  - L'elenco di default (`70`) ha **13 documenti**: i 12 della corsa piu' il documento seminato `1` (`e2e-seed-senza-contenuto.pdf`), senza `sha256`
    (campo assente) e con `size_bytes` 0 e `has_content` false, in fondo perche' piu' vecchio. `73` ha `limit=13`, `75` ha `limit=12`.
  - I documenti della corsa hanno id `30017`-`30028`; i dati seminati hanno i contatti `1` (fornitore), `2` (utente), `3` (gruppo) e la
    categoria `E2E-FORNITORI` (`151`).
  - L'intestazione `Last-Modified` dei download e' `Fri, 09 Oct 2026 11:11:07 GMT`.

## `derived/` - risposte derivate, NON catturate

Per queste forme la `t64` non ha nessun corpo: nessun webhook (serve un ricevitore), nessun dettaglio di documento con contatti, nessun
documento con id oltre `int.MaxValue`. Sono scritte a partire dalle catture piu' vicine e dal codice del server (`Api/Dtos.cs`,
`Domain/Contact.cs`, `Domain/Webhook.cs`, `Services/DocumentService.cs`, `Services/WebhookDispatcher.cs` al commit sopra), con la stessa politica
del server (`snake_case`, null omessi) e valori inventati (email `@example.test`). **Vanno sostituite da catture vere** appena esistono: finche'
non succede sono "derivate, non catturate" e i test che le usano lo dicono.

| File | Forma | Fonte nel server |
| --- | --- | --- |
| `doc-detail-with-contacts.json` | dettaglio di un documento con `contacts` (`role`, `id` numero, `name`) | `DocumentDto`, `DocumentContactDto`, `ContactRole` |
| `doc-detail-without-content.json` | documento importato con i soli metadati, id `5000000001` (oltre `int.MaxValue`): niente `sha256`, `size_bytes` 0, `has_content` false | `Document.CreateWithoutContent`, `DocumentDto` |
| `doc-detail-without-content-with-contacts.json` | lo stesso documento collegato a tre contatti (cattura `265` piu' i contatti seminati) | `DocumentDto` |
| `contacts-page.json` | pagina di contatti (uno completo con i due contatori, uno minimo) con `next_cursor` | `ContactDto`, `ItemsDto`, `Wire<ContactKind>` |
| `contact-detail.json` | dettaglio di un contatto: nessun contatore dei documenti | `ContactDto` |
| `contact-categories.json` | elenco non paginato delle categorie | `ContactCategoryDto` |
| `webhook-document-uploaded.json`, `webhook-document-deleted.json`, `webhook-document-integrity-failed.json` | buste webhook `{event, delivery_id, occurred_at, payload}` | `WebhookDispatcher.BuildBody`, `DocumentService` |

**La forma degli id nelle buste webhook** (letta nel codice del server, non catturata): `delivery_id` e' un numero JSON (l'intestazione
`X-SharpAFile-Delivery` ha le stesse cifre come testo); nel payload `document_id` e' un **numero** in `document.uploaded` e
`document.integrity_failed`, ma il **testo delle cifre** in `document.deleted` (`DocumentService.DeleteAsync` pubblica l'id della rotta, una stringa).
Le tre fixture lo riproducono, e il parser le legge tutte.

### Confronto con le catture vere

`CapturedContactsTests` controlla che i lettori leggano i corpi veri (`258`, `259`, `150`, `151`) e che **ogni nome di campo mandato dal server
esista nella fixture derivata** (per il dettaglio di un contatto l'oracolo e' il contatto completo della pagina derivata). Le fixture derivate
restano l'oracolo dei test di dettaglio (hanno piu' campi valorizzati). **Restano derivate e mai confrontate** le tre buste webhook, il
dettaglio di un documento con contatti e il documento con id oltre `int.MaxValue`.

## Come si usano

- `WireFixtures` (in `tests/Filemaster.UnitTests/Wire/WireTestSupport.cs`) legge i file; `Variants` ne deriva una variante togliendo o cambiando UNA
  proprieta' con `System.Text.Json.Nodes` (i numeri restano con le cifre originali), cosi' l'oracolo resta il file e non il codice sotto prova.
- Il csproj dei test li copia accanto alla DLL con `<None Update="Wire\Fixtures\**" CopyToOutputDirectory="PreserveNewest" />`.
- Prima di aggiungere una cattura: passarla dallo scrubbing (stesse regole sopra), cercare i prefissi delle chiavi e dei segreti webhook in tutto il repo e controllare che restino solo i valori
  finti, e rileggere i valori scritti nei test (gli id, gli hash e le date sono quelli del file).
