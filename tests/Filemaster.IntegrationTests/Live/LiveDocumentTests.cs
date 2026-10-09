using System.Text.Json;
using Filemaster.Application;
using Filemaster.Domain;
using static Filemaster.IntegrationTests.Live.LiveServer;

namespace Filemaster.IntegrationTests.Live;

/// <summary>
/// Documenti contro il server vero: upload, lettura, contenuto intero e parziale, verifica, deduplica, spostamenti, elenchi,
/// operazioni multiple, anteprima, cancellazione. Ogni test crea i suoi documenti (contenuto e nomi unici per esecuzione) e
/// li cancella alla fine.
/// </summary>
[Trait("Category", "Live")]
public sealed class LiveDocumentTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task An_upload_round_trips_fields_content_range_verify_and_delete()
    {
        using var client = Client();
        await LiveCleanup.WithCleanupAsync(client, async cleanup =>
        {
            var folder = UniqueFolder("DOC");
            await client.Folders.CreateAsync(new CreateFolderRequest(folder, "Documenti " + RunId), Ct);
            cleanup.Folder(folder);
            var bytes = UniqueBytes(70_000);
            var owner = Unique("owner");
            using var metadata = JsonDocument.Parse("""{"progetto":"e2e","numero":42,"annidato":{"ok":true}}""");

            var uploaded = await UploadAsync(client, cleanup, bytes, "fattura-e2e.pdf", r =>
            {
                r.FolderId = folder;
                r.Owner = owner;
                r.Tag = "e2e";
                r.Sender = "Acme Srl";
                r.Recipient = "Beta Spa";
                r.Metadata = metadata.RootElement;
            });

            Assert.False(uploaded.Deduplicated);
            var document = uploaded.Document;
            Assert.False(document.Id.IsEmpty);
            Assert.True(document.Id.Number >= 1);
            Assert.Equal(folder, document.FolderId);
            Assert.Equal("fattura-e2e.pdf", document.OriginalFilename);
            Assert.Equal("application/pdf", document.MimeType);
            Assert.Equal(Sha256Hex(bytes), document.Sha256);
            Assert.Equal(bytes.Length, document.SizeBytes);
            Assert.Equal(owner, document.Owner);
            Assert.Equal("e2e", document.Tag);
            Assert.Equal("Acme Srl", document.Sender);
            Assert.Equal("Beta Spa", document.Recipient);
            Assert.Equal(42, document.Metadata.GetProperty("numero").GetInt32());
            Assert.True(document.Metadata.GetProperty("annidato").GetProperty("ok").GetBoolean());
            Assert.True(document.HasContent);
            Assert.Equal(TimeSpan.Zero, document.CreatedAt.Offset);

            var fetched = await client.Documents.GetAsync(document.Id, Ct);
            Assert.Equal(document.Id, fetched.Id);
            Assert.Equal(document.FolderId, fetched.FolderId);
            Assert.Equal(document.Sha256, fetched.Sha256);
            Assert.Equal(document.SizeBytes, fetched.SizeBytes);
            Assert.Equal(document.CreatedAt, fetched.CreatedAt);
            Assert.Equal(owner, fetched.Owner);
            Assert.Equal("e2e", fetched.Metadata.GetProperty("progetto").GetString());
            Assert.Empty(fetched.Contacts);

            using (var content = await client.Documents.OpenContentAsync(document.Id, cancellationToken: Ct))
            {
                Assert.False(content.IsPartial);
                Assert.Equal(bytes.Length, content.ContentLength);
                Assert.Equal("application/pdf", content.ContentType);
                Assert.Equal("fattura-e2e.pdf", content.FileName);
                Assert.NotNull(content.LastModified);
                Assert.Equal(Sha256Hex(bytes), Sha256Hex(await ReadAllAsync(content.Content)));
            }

            using (var part = await client.Documents.OpenContentAsync(document.Id, ByteRange.Between(100, 1_099), Ct))
            {
                Assert.True(part.IsPartial);
                Assert.Equal(100, part.Range!.FirstByte);
                Assert.Equal(1_099, part.Range.LastByte);
                Assert.Equal(bytes.Length, part.Range.TotalLength);
                Assert.Equal(1_000, part.ContentLength);
                Assert.Equal(bytes.Skip(100).Take(1_000).ToArray(), await ReadAllAsync(part.Content));
            }

            using (var tail = await client.Documents.OpenContentAsync(document.Id, ByteRange.Suffix(10), Ct))
            {
                Assert.True(tail.IsPartial);
                Assert.Equal(bytes.Length - 10, tail.Range!.FirstByte);
                Assert.Equal(bytes.Skip(bytes.Length - 10).ToArray(), await ReadAllAsync(tail.Content));
            }

            using (var verified = await client.Documents.OpenVerifiedContentAsync(fetched, Ct))
            {
                Assert.Equal(bytes, await ReadAllAsync(verified.Content));
            }

            // POST /verify e DELETE partono senza corpo applicativo ma con Content-Length: 0 (fix di T6.1): il server li accetta.
            var check = await client.Documents.VerifyAsync(document.Id, Ct);
            Assert.True(check.Ok, check.Detail);
            Assert.Equal(document.Id, check.DocumentId);
            Assert.Equal(document.Sha256, check.Sha256);

            await client.Documents.DeleteAsync(document.Id, Ct);
            var again = await Assert.ThrowsAsync<NotFoundException>(() => client.Documents.DeleteAsync(document.Id, Ct));
            Assert.Equal(404, again.StatusCode);
            Assert.Equal("not-found", again.ProblemType);
            await Assert.ThrowsAsync<NotFoundException>(() => client.Documents.GetAsync(document.Id, Ct));
            await Assert.ThrowsAsync<NotFoundException>(() => client.Documents.VerifyAsync(document.Id, Ct));
        });
    }

    [Fact]
    public async Task A_non_ascii_file_name_survives_upload_metadata_and_download()
    {
        const string name = "Fattura perch\U000000E9 \U000000E8 \U000020AC 2026.pdf";
        using var client = Client();
        await LiveCleanup.WithCleanupAsync(client, async cleanup =>
        {
            var bytes = UniqueBytes(2_000);

            var uploaded = await UploadAsync(client, cleanup, bytes, name);

            Assert.Equal(name, uploaded.Document.OriginalFilename);
            Assert.Equal(name, (await client.Documents.GetAsync(uploaded.Document.Id, Ct)).OriginalFilename);
            using var content = await client.Documents.OpenContentAsync(uploaded.Document.Id, cancellationToken: Ct);
            Assert.Equal(name, content.FileName);
            Assert.Equal(bytes, await ReadAllAsync(content.Content));
        });
    }

    [Fact]
    public async Task The_same_bytes_uploaded_twice_are_deduplicated_into_a_new_document()
    {
        using var client = Client();
        await LiveCleanup.WithCleanupAsync(client, async cleanup =>
        {
            var bytes = UniqueBytes(5_000);

            var first = await UploadAsync(client, cleanup, bytes, "primo.pdf");
            var second = await UploadAsync(client, cleanup, bytes, "secondo.pdf");

            Assert.False(first.Deduplicated);
            Assert.True(second.Deduplicated);
            Assert.NotEqual(first.Document.Id, second.Document.Id);
            Assert.Equal(first.Document.Sha256, second.Document.Sha256);
            Assert.Equal("secondo.pdf", second.Document.OriginalFilename);

            // Il blob e' condiviso: cancellare il primo documento non toglie il contenuto al secondo.
            await client.Documents.DeleteAsync(first.Document.Id, Ct);
            using var content = await client.Documents.OpenContentAsync(second.Document.Id, cancellationToken: Ct);
            Assert.Equal(bytes, await ReadAllAsync(content.Content));
        });
    }

    [Fact]
    public async Task A_document_moves_into_a_folder_and_back_to_the_root()
    {
        using var client = Client();
        await LiveCleanup.WithCleanupAsync(client, async cleanup =>
        {
            var folder = UniqueFolder("MOVE");
            await client.Folders.CreateAsync(new CreateFolderRequest(folder, "Spostamenti " + RunId), Ct);
            cleanup.Folder(folder);
            var uploaded = await UploadAsync(client, cleanup, UniqueBytes(1_000), "sposta.pdf");
            Assert.Null(uploaded.Document.FolderId);

            await client.Documents.MoveAsync(uploaded.Document.Id, folder, Ct);
            Assert.Equal(folder, (await client.Documents.GetAsync(uploaded.Document.Id, Ct)).FolderId);
            var inFolder = await client.Documents.ListAsync(new DocumentQuery { FolderId = folder }, cancellationToken: Ct);
            Assert.Equal(uploaded.Document.Id, Assert.Single(inFolder.Items).Id);

            await client.Documents.MoveAsync(uploaded.Document.Id, null, Ct);
            Assert.Null((await client.Documents.GetAsync(uploaded.Document.Id, Ct)).FolderId);
            Assert.Empty((await client.Documents.ListAsync(new DocumentQuery { FolderId = folder }, cancellationToken: Ct)).Items);
        });
    }

    [Fact]
    public async Task Filters_pages_of_one_and_the_enumeration_see_exactly_the_documents_of_the_run()
    {
        using var client = Client();
        await LiveCleanup.WithCleanupAsync(client, async cleanup =>
        {
            var owner = Unique("lista");
            var batch = Unique("lotto");
            using var lotto = JsonDocument.Parse("{\"lotto\":\"" + batch + "\"}");
            var a = await UploadAsync(client, cleanup, UniqueBytes(500), "a.pdf", r => { r.Owner = owner; r.Tag = "alfa"; });
            var b = await UploadAsync(client, cleanup, UniqueBytes(500), "b.pdf", r => { r.Owner = owner; r.Tag = "beta"; });
            var c = await UploadAsync(client, cleanup, UniqueBytes(500), "c.pdf", r =>
            {
                r.Owner = owner;
                r.Tag = "beta";
                r.Metadata = lotto.RootElement;
            });
            var expected = Numbers(new[] { a.Document.Id, b.Document.Id, c.Document.Id });

            var byOwner = await client.Documents.ListAsync(new DocumentQuery { Owner = owner }, cancellationToken: Ct);
            Assert.Equal(expected, Numbers(byOwner.Items));

            var byTag = await client.Documents.ListAsync(new DocumentQuery { Owner = owner, Tag = "beta" }, cancellationToken: Ct);
            Assert.Equal(Numbers(new[] { b.Document.Id, c.Document.Id }), Numbers(byTag.Items));

            var byMetadata = await client.Documents.ListAsync(new DocumentQuery { Metadata = lotto.RootElement }, cancellationToken: Ct);
            Assert.Equal(c.Document.Id, Assert.Single(byMetadata.Items).Id);

            var byTime = await client.Documents.ListAsync(
                new DocumentQuery
                {
                    Owner = owner,
                    CreatedFrom = a.Document.CreatedAt.AddMinutes(-1),
                    CreatedBefore = DateTimeOffset.UtcNow.AddHours(1),
                },
                cancellationToken: Ct);
            Assert.Equal(expected, Numbers(byTime.Items));
            var future = await client.Documents.ListAsync(
                new DocumentQuery { Owner = owner, CreatedFrom = DateTimeOffset.UtcNow.AddHours(1) },
                cancellationToken: Ct);
            Assert.Empty(future.Items);

            var first = await client.Documents.ListAsync(new DocumentQuery { Owner = owner }, new PageRequest(limit: 1), Ct);
            Assert.Single(first.Items);
            Assert.NotNull(first.NextCursor);
            var second = await client.Documents.ListAsync(new DocumentQuery { Owner = owner }, new PageRequest(first.NextCursor, 1), Ct);
            Assert.Single(second.Items);
            Assert.NotEqual(first.Items[0].Id, second.Items[0].Id);

            var enumerated = new List<Document>();
            await foreach (var document in client.Documents.EnumerateAsync(new DocumentQuery { Owner = owner }, 1, Ct))
            {
                enumerated.Add(document);
            }

            Assert.Equal(expected, Numbers(enumerated));
        });
    }

    [Fact]
    public async Task MoveMany_and_VerifyMany_act_on_the_whole_batch()
    {
        using var client = Client();
        await LiveCleanup.WithCleanupAsync(client, async cleanup =>
        {
            var folder = UniqueFolder("BULK");
            await client.Folders.CreateAsync(new CreateFolderRequest(folder, "Lotto " + RunId), Ct);
            cleanup.Folder(folder);
            var a = await UploadAsync(client, cleanup, UniqueBytes(800), "uno.pdf");
            var b = await UploadAsync(client, cleanup, UniqueBytes(900), "due.pdf");
            var ids = new[] { a.Document.Id, b.Document.Id };

            Assert.Equal(2, await client.Documents.MoveManyAsync(ids, folder, Ct));
            var inFolder = await client.Documents.ListAsync(new DocumentQuery { FolderId = folder }, cancellationToken: Ct);
            Assert.Equal(Numbers(ids), Numbers(inFolder.Items));

            var result = await client.Documents.VerifyManyAsync(ids, Ct);
            Assert.Equal(2, result.Total);
            Assert.Equal(2, result.Verified);
            Assert.Equal(0, result.Failed);
            Assert.Equal(0, result.WithoutContent);

            // Un id sconosciuto: move lo ignora (conta solo i documenti spostati), verify rifiuta l'intero lotto con 404.
            var unknown = DocumentId.From(long.MaxValue);
            Assert.Equal(2, await client.Documents.MoveManyAsync(new[] { a.Document.Id, b.Document.Id, unknown }, null, Ct));
            Assert.Empty((await client.Documents.ListAsync(new DocumentQuery { FolderId = folder }, cancellationToken: Ct)).Items);
            await Assert.ThrowsAsync<NotFoundException>(() => client.Documents.VerifyManyAsync(new[] { a.Document.Id, unknown }, Ct));
        });
    }

    [Fact]
    public async Task A_pdf_has_a_preview_and_any_other_type_is_an_unsupported_media_type()
    {
        using var client = Client();
        await LiveCleanup.WithCleanupAsync(client, async cleanup =>
        {
            var pdf = UniqueBytes(4_000);
            var withPreview = await UploadAsync(client, cleanup, pdf, "anteprima.pdf");
            var text = await UploadAsync(client, cleanup, UniqueBytes(300), "nota.txt", r => r.ContentType = "text/plain");

            using (var preview = await client.Documents.OpenPreviewAsync(withPreview.Document.Id, Ct))
            {
                Assert.Equal("application/pdf", preview.ContentType);
                Assert.Equal(pdf, await ReadAllAsync(preview.Content));
            }

            Assert.Equal("text/plain", text.Document.MimeType);
            var error = await Assert.ThrowsAsync<UnsupportedMediaTypeException>(() => client.Documents.OpenPreviewAsync(text.Document.Id, Ct));
            Assert.Equal(415, error.StatusCode);
            Assert.Equal("unsupported-media-type", error.ProblemType);
        });
    }

    [Fact]
    public async Task An_arxivar_document_is_found_by_docnumber_and_its_metadata_is_read()
    {
        // Un DOCNUMBER per esecuzione (da un Guid, sempre > 0 e < int.MaxValue): la ricerca trova solo il documento di questo test.
        var docnumber = 100_000_000 + (BitConverter.ToInt32(Guid.NewGuid().ToByteArray(), 0) & 0x3FFFFFFF);
        using var client = Client();
        await LiveCleanup.WithCleanupAsync(client, async cleanup =>
        {
            using var metadata = JsonDocument.Parse(
                "{\"arxivar\":{\"docnumber\":" + docnumber.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    + ",\"categoria\":\"FATTURA\",\"oggetto\":\"Prova e2e\",\"data_documento\":\"2026-10-03\",\"revisione\":2}}");
            var uploaded = await UploadAsync(client, cleanup, UniqueBytes(1_500), "arxivar.pdf", r => r.Metadata = metadata.RootElement);
            await UploadAsync(client, cleanup, UniqueBytes(1_500), "altro.pdf");

            var found = await client.Documents.FindByArxivarDocnumberAsync(docnumber, Ct);

            var document = Assert.Single(found);
            Assert.Equal(uploaded.Document.Id, document.Id);
            var arxivar = document.GetArxivarMetadata();
            Assert.NotNull(arxivar);
            Assert.Equal(docnumber, arxivar!.Docnumber);
            Assert.Equal("FATTURA", arxivar.Category);
            Assert.Equal("Prova e2e", arxivar.Subject);
            Assert.Equal(new DateTime(2026, 10, 3), arxivar.DocumentDate);
            Assert.Equal(2, arxivar.Revision);
            Assert.Null(arxivar.Protocol);
        });
    }

    [Fact]
    public async Task Above_the_store_limit_the_upload_is_a_request_too_large()
    {
        var max = MaxUploadBytes;
        using var client = Client();
        await LiveCleanup.WithCleanupAsync(client, async cleanup =>
        {
            // Oltre il limite dell'archivio ma sotto quello di Kestrel (limite + 1 MiB): 413 "contenuto troppo grande" del server.
            var bytes = UniqueBytes(checked((int)max + (64 * 1024)));

            var error = await Assert.ThrowsAsync<RequestTooLargeException>(() => UploadAsync(client, cleanup, bytes, "troppo.pdf"));

            Assert.Equal(413, error.StatusCode);
            Assert.Equal("request-too-large", error.ProblemType);
        });
    }

    [Fact]
    public async Task Above_the_kestrel_limit_the_upload_fails_as_too_large_or_as_a_closed_connection()
    {
        var max = MaxUploadBytes;
        using var client = Client();
        await LiveCleanup.WithCleanupAsync(client, async cleanup =>
        {
            // Oltre il limite di Kestrel (limite + 1 MiB): 413 "richiesta troppo grande" e connessione chiusa. Il client puo' vedere
            // il 413 o, se la chiusura arriva mentre sta ancora scrivendo, un errore di connessione (come col server di loopback).
            var bytes = UniqueBytes(checked((int)max + (2 * 1024 * 1024)));

            var error = await Assert.ThrowsAnyAsync<FilemasterException>(() => UploadAsync(client, cleanup, bytes, "enorme.pdf"));

            Assert.True(
                error is RequestTooLargeException { StatusCode: 413 } or ConnectionException,
                error.GetType().Name + ": " + error.Message);
        });
    }
}
