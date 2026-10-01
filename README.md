# Filemaster

Filemaster e' la libreria client .NET per l'API HTTP di Sharp-a-File, il server di archiviazione documentale. Nasconde
l'API "nuda" (JSON snake_case, ID prefissati, cursori opachi, upload multipart, errori `problem+json`, webhook firmati)
dietro interfacce tipizzate. Si rivolge a .NET 8, .NET 10 e .NET Framework 4.8 (tramite `netstandard2.0`). Il progetto e'
in sviluppo: l'API pubblica non e' ancora disponibile.

## Installazione

```
dotnet add package Filemaster
```

## Documentazione

La documentazione completa sara' nella cartella [docs](https://github.com/Vinello28/Filemaster/tree/main/docs).

## Licenza

Apache-2.0, vedi [LICENSE](https://github.com/Vinello28/Filemaster/blob/main/LICENSE).
