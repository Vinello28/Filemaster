# Pubblicare una versione

I quattro pacchetti (`Filemaster.Domain`, `Filemaster.Application`, `Filemaster.Infrastructure`, `Filemaster`) si
pubblicano insieme, con la stessa versione, da `.github/workflows/release.yml`. Il workflow si attiva solo quando una
persona spinge un tag `v*`. Su nuget.org non c'e' una chiave API a lunga durata: si usa **Trusted Publishing**. Il token
OIDC di GitHub Actions viene scambiato con una chiave temporanea, valida un'ora.

> **Le versioni su nuget.org sono immutabili.** Una versione pubblicata non si sovrascrive e non si cancella: si puo'
> solo de-listare, cioe' nasconderla dalla ricerca (chi la referenzia la scarica ancora), o deprecare. Un errore si
> corregge con una versione nuova (`0.1.1`, `0.1.0-rc.2`).
>
> Per lo stesso motivo **non si sposta e non si rispinge un tag gia' pubblicato**. Il workflow ricostruirebbe i
> pacchetti e aggiornerebbe la Release su GitHub, ma su nuget.org `--skip-duplicate` salterebbe la versione esistente.
> Su GitHub e su nuget.org resterebbero due binari diversi con lo stesso numero.

## Da fare una volta (a mano)

Questi passi richiedono i tuoi account, quindi nessun agente li puo' eseguire.

1. **Repository GitHub.** Crea il repository pubblico `Vinello28/Filemaster` vuoto, senza README ne' licenza. Poi
   collega il remote e spingi `main`:

   ```
   git remote add origin https://github.com/Vinello28/Filemaster.git
   git push -u origin main
   ```

   Il `README.md` del pacchetto punta a `https://github.com/Vinello28/Filemaster/blob/main/...`, e SourceLink scrive il
   commit nel nuspec solo se l'origin e' su github.com.

2. **Environment `nuget`.** In *Settings -> Environments* crea un environment chiamato esattamente `nuget`.
   - Nelle *deployment branches and tags* ammetti i tag `v*`. Se resta "protected branches only", un job lanciato da un
     tag viene rifiutato.
   - Facoltativo: aggiungi te stesso come *required reviewer*. Cosi' il push su nuget.org aspetta un tuo clic, dopo che
     verifiche, pacchetti e Release GitHub sono gia' pronti.

3. **Secret `NUGET_USER`.** In *Settings -> Secrets and variables -> Actions* crea il secret `NUGET_USER`. Il valore e'
   il nome utente (il nome del profilo) di nuget.org, **non** l'email. `NuGet/login` lo passa come `user`.

4. **Policy di Trusted Publishing su nuget.org.** Nel menu del tuo nome utente scegli *Trusted Publishing* e aggiungi
   una policy per GitHub Actions:

   | Campo | Valore |
   | --- | --- |
   | Policy owner | Il tuo utente (o l'organizzazione che possiedera' i pacchetti) |
   | Repository Owner | `Vinello28` |
   | Repository | `Filemaster` |
   | Workflow File | `release.yml` (solo il nome del file, senza `.github/workflows/`) |
   | Environment | `nuget` |

   Se la pagina offre l'ambito della policy (*Policy Scopes*), scegli "nuovi pacchetti e nuove versioni" con il glob
   `Filemaster*`. Una policy puo' nascere "temporaneamente attiva" per 7 giorni (succede tipicamente con i repository
   privati) e diventare permanente dopo la prima pubblicazione riuscita: in quel caso fai il primo rilascio entro quel
   tempo.

   Il workflow e l'environment sono i due dati che nuget.org confronta con il token OIDC. Se rinomini `release.yml` o
   il job smette di girare nell'environment `nuget`, la pubblicazione viene rifiutata.

5. **Protezioni.** Consigliate.
   - Su `main`: una regola (ruleset) che richiede le pull request e i controlli della CI. I nomi attuali dei job sono
     "Build e test (Linux)", "Build e test (Windows, net48)", "Pack smoke (ubuntu-latest)" e "Pack smoke (windows-latest)". Un
     controllo si puo' rendere obbligatorio solo dopo che ha girato almeno una volta.
   - Sui tag `v*`: una regola che impedisce di cancellarli e di spostarli, e che ne limita la creazione a te.
     Il workflow pubblica qualunque tag `v*` che punta a un commit di `main`.

6. **Primo rilascio come prova generale.** Usa un tag di prerelease:

   ```
   git tag v0.1.0-rc.1
   git push origin v0.1.0-rc.1
   ```

   E' una pubblicazione vera e irreversibile, ma produce una `-rc` invece di una stabile mentre si collauda la pipeline.
   Verificala (sezione sotto), poi ripeti con `v0.1.0`. La libreria resta in `0.x` finche' la suite end-to-end contro
   un server vero non e' verde.

## Cosa fa `release.yml`

Si attiva al push di un tag `v*`. I permessi di base sono `contents: read`, e ogni job dichiara quelli in piu' che gli
servono. Il nome del tag si legge sempre da `$GITHUB_REF_NAME`, mai interpolato nello script.

Non c'e' un blocco `concurrency`, di proposito: ogni tag e' una corsa separata con i suoi artefatti.

1. **`verifica` (Linux).**
   - Esegue il checkout con tutta la storia.
   - Controlla che il tag abbia la forma `vX.Y.Z` o `vX.Y.Z-prerelease`, con la regex
     `^v[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z.-]+)?$`.
   - Controlla che il commit del tag sia raggiungibile da `origin/main` (`git merge-base --is-ancestor`): si rilascia
     solo da `main`.
   - Fa il restore.
   - Controlla la formattazione in due passaggi, uno per il TFM di default e uno per `net10.0`.
   - Compila in Release.
   - Esegue unit test e test di integrazione su `net8.0` e `net10.0`.

2. **`verifica-windows`.**
   - Compila.
   - Esegue i test su `net8.0`, `net10.0` e `net48`, esclusi quelli di memoria.
   - Esegue a parte il test di memoria (upload da 200 MB) su `net48`, con `--minimum-expected-tests 1`.

   I controlli si rifanno qui, e non si riprendono dalla CI, perche' un tag puo' puntare a un commit che la CI non ha
   mai visto.

3. **`pack`** parte solo se le due verifiche passano.
   - Esegue `dotnet pack -c Release -p:Version=<tag senza v> -o artifacts`. La versione viene dal tag e rientra anche
     nella versione degli assembly.
   - Esegue `eng/verify-packages.sh --require-commit`, che controlla:
     - i 4 `.nupkg` e i 4 `.snupkg`;
     - le tre cartelle `lib/`;
     - i range esatti fra i pacchetti fratelli e le dipendenze esterne attese;
     - licenza, readme e repository, con il commit.
   - Scrive `SHA256SUMS.txt`.
   - Carica tutto come artefatto `pacchetti`, conservato 30 giorni.

4. **`github-release`** (`contents: write`).
   - Crea la Release del tag con le note generate e allega gli 8 pacchetti e `SHA256SUMS.txt`.
   - La Release e' una *prerelease* se il tag contiene un trattino.
   - Rilanciato, aggiorna la Release esistente.

5. **`publish-nuget`** (environment `nuget`, `id-token: write`).
   - Scarica l'artefatto e lo ricontrolla con `sha256sum -c`.
   - Scambia il token OIDC con una chiave temporanea tramite `NuGet/login` (`user: secrets.NUGET_USER`).
   - Spinge i `.nupkg` in quest'ordine: `Filemaster.Domain`, `Filemaster.Application`, `Filemaster.Infrastructure`,
     `Filemaster`. Usa `--no-symbols --skip-duplicate`.
   - Poi spinge i `.snupkg`, nello stesso ordine, su `https://api.nuget.org/v3/index.json`.
   - Grazie a `--skip-duplicate`, se il job fallisce a meta' si puo' rilanciare: salta cio' che e' gia' stato
     pubblicato.

La Release su GitHub nasce **prima** del push su nuget.org. Se il push fallisce, la Release esiste gia': si rilancia
solo il job `publish-nuget` (*Re-run failed jobs*), oppure si passa al piano B.

## Verificare una release

**Su GitHub**, nella pagina *Releases* del tag, devono esserci 9 asset:

- 4 `.nupkg`;
- 4 `.snupkg`;
- `SHA256SUMS.txt`.

Una `-rc` deve avere il segno *Pre-release*. Per controllare i file scaricati:

```
sha256sum -c SHA256SUMS.txt          # Linux
shasum -a 256 -c SHA256SUMS.txt      # macOS
```

**Su nuget.org**, ogni pacchetto deve avere la versione nuova:

- https://www.nuget.org/packages/Filemaster.Domain
- https://www.nuget.org/packages/Filemaster.Application
- https://www.nuget.org/packages/Filemaster.Infrastructure
- https://www.nuget.org/packages/Filemaster

Dopo il push, nuget.org valida e indicizza i pacchetti. Per qualche minuto una versione puo' risultare "non ancora
disponibile" o mancare dalla ricerca. Anche i simboli (`.snupkg`) passano una validazione separata. Per controllare
senza browser (ID in minuscolo):

```
curl -s https://api.nuget.org/v3-flatcontainer/filemaster.domain/index.json
```

Sulla pagina di ogni pacchetto controlla anche:

- il README;
- la licenza Apache-2.0;
- il link al repository con il commit;
- le dipendenze verso gli altri tre pacchetti con versione esatta.

La prova finale e' un progetto nuovo che fa `dotnet add package Filemaster --version <versione>` (con `--prerelease`
per una `-rc`) e compila.

## Piano B: una chiave API a breve scadenza (procedura manuale)

Serve solo se Trusted Publishing rifiuta la prima pubblicazione, per esempio per gli ID nuovi.

**`release.yml` non ha un ripiego su un secret `NUGET_API_KEY`.** Il nome `NUGET_API_KEY` nel workflow e' solo la
variabile d'ambiente in cui finisce la chiave temporanea di `NuGet/login`. Un secret con quel nome non viene letto da
nessuna parte. Il piano B e' quindi una procedura manuale: usa i pacchetti gia' costruiti e verificati dal workflow,
senza modificarlo.

1. **Crea la chiave.** Su nuget.org, in *API Keys -> Create*:
   - nome descrittivo;
   - scadenza la piu' breve disponibile (1 giorno);
   - ambito "Push new packages and package versions";
   - glob pattern `Filemaster*`.

2. **Scarica i pacchetti** dagli asset della Release GitHub del tag (o dall'artefatto `pacchetti` della corsa) e
   controllali con `sha256sum -c SHA256SUMS.txt`. Non ricostruirli in locale: i pacchetti giusti sono quelli verificati
   dalla pipeline.

3. **Spingi i pacchetti** dalla cartella che li contiene, nello stesso ordine del workflow. Leggi la chiave senza
   scriverla nella cronologia della shell:

   ```
   read -rs NUGET_API_KEY && export NUGET_API_KEY
   version=0.1.0-rc.1
   source=https://api.nuget.org/v3/index.json
   for id in Filemaster.Domain Filemaster.Application Filemaster.Infrastructure Filemaster; do
     dotnet nuget push "$id.$version.nupkg" --no-symbols --skip-duplicate --source "$source" --api-key "$NUGET_API_KEY"
   done
   for id in Filemaster.Domain Filemaster.Application Filemaster.Infrastructure Filemaster; do
     dotnet nuget push "$id.$version.snupkg" --skip-duplicate --source "$source" --api-key "$NUGET_API_KEY"
   done
   unset NUGET_API_KEY
   ```

   Non aggiungere `--no-symbols` al secondo ciclo: NuGet salterebbe gli `.snupkg` senza errore.

4. **Cancella la chiave** su nuget.org subito dopo il push, anche se non e' ancora scaduta.

5. **Controlla la policy di Trusted Publishing.** Dopo la prima pubblicazione gli ID esistono e sono tuoi: le versioni
   successive dovrebbero passare dal workflow. Verificalo con la prossima `-rc`.

`tasks/todo.md` descrive il piano B come una chiave salvata nel secret `NUGET_API_KEY`. Per usarla dal workflow
servirebbe una modifica a `release.yml` che oggi non c'e'. La procedura manuale qui sopra evita di tenere una chiave
nei secret di GitHub.
