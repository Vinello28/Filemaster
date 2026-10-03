using Filemaster.Application;
using Filemaster.Domain;
using static Filemaster.IntegrationTests.Live.LiveServer;

namespace Filemaster.IntegrationTests.Live;

/// <summary>Cartelle contro il server vero: creazione, elenco dei figli, rinomina e cambio di codice, conflitti, cancellazione.</summary>
[Trait("Category", "Live")]
public sealed class LiveFolderTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_folder_tree_is_created_listed_renamed_recoded_and_deleted()
    {
        using var client = Client();
        await LiveCleanup.WithCleanupAsync(client, async cleanup =>
        {
            var parent = UniqueFolder("FLD");
            var parentName = "Cartella " + RunId;
            var created = await client.Folders.CreateAsync(new CreateFolderRequest(parent, parentName), Ct);
            cleanup.Folder(parent);
            Assert.Equal(parent, created.Id);
            Assert.Null(created.ParentId);
            Assert.Equal(parentName, created.Name);
            Assert.Equal(TimeSpan.Zero, created.CreatedAt.Offset);

            var child = UniqueFolder("SUB");
            var sub = await client.Folders.CreateAsync(new CreateFolderRequest(child, "Figlia") { ParentId = parent }, Ct);
            cleanup.Folder(child);
            Assert.Equal(parent, sub.ParentId);

            Assert.Contains(await client.Folders.ListChildrenAsync(null, Ct), f => f.Id == parent && f.Name == parentName);
            Assert.Equal(child, Assert.Single(await client.Folders.ListChildrenAsync(parent, Ct)).Id);

            var renamed = await client.Folders.UpdateAsync(child, new UpdateFolderRequest { Name = "Figlia rinominata" }, Ct);
            Assert.Equal(child, renamed.Id);
            Assert.Equal("Figlia rinominata", renamed.Name);

            // Un documento nella cartella segue il cambio di codice.
            var uploaded = await UploadAsync(client, cleanup, UniqueBytes(600), "in-cartella.pdf", r => r.FolderId = child);
            var newCode = UniqueFolder("NEW");
            var recoded = await client.Folders.UpdateAsync(child, new UpdateFolderRequest { NewCode = newCode }, Ct);
            cleanup.Renamed(child, newCode);
            Assert.Equal(newCode, recoded.Id);
            Assert.Equal(parent, recoded.ParentId);
            Assert.Equal("Figlia rinominata", recoded.Name);
            Assert.Equal(newCode, Assert.Single(await client.Folders.ListChildrenAsync(parent, Ct)).Id);
            Assert.Equal(newCode, (await client.Documents.GetAsync(uploaded.Document.Id, Ct)).FolderId);
            await Assert.ThrowsAsync<NotFoundException>(() => client.Folders.UpdateAsync(child, new UpdateFolderRequest { Name = "x" }, Ct));

            // Una cartella con figli non si cancella.
            var notEmpty = await Assert.ThrowsAsync<ConflictException>(() => client.Folders.DeleteAsync(parent, Ct));
            Assert.Equal(409, notEmpty.StatusCode);
            Assert.Equal("conflict", notEmpty.ProblemType);

            await client.Documents.DeleteAsync(uploaded.Document.Id, Ct);
            await client.Folders.DeleteAsync(newCode, Ct);
            await client.Folders.DeleteAsync(parent, Ct);
            await Assert.ThrowsAsync<NotFoundException>(() => client.Folders.DeleteAsync(parent, Ct));
        });
    }

    [Fact]
    public async Task A_duplicate_code_or_a_duplicate_name_in_the_same_place_is_a_conflict()
    {
        using var client = Client();
        await LiveCleanup.WithCleanupAsync(client, async cleanup =>
        {
            var code = UniqueFolder("DUP");
            var name = "Duplicata " + RunId;
            await client.Folders.CreateAsync(new CreateFolderRequest(code, name), Ct);
            cleanup.Folder(code);

            var sameCode = await Assert.ThrowsAsync<ConflictException>(
                () => client.Folders.CreateAsync(new CreateFolderRequest(code, "Altro nome " + RunId), Ct));
            Assert.Equal(409, sameCode.StatusCode);
            Assert.Equal("conflict", sameCode.ProblemType);
            Assert.NotNull(sameCode.RequestId);

            var otherCode = UniqueFolder("DUP");
            cleanup.Folder(otherCode); // se il server la creasse, la pulizia la toglie
            await Assert.ThrowsAsync<ConflictException>(() => client.Folders.CreateAsync(new CreateFolderRequest(otherCode, name), Ct));
        });
    }

    [Fact]
    public async Task A_folder_under_a_parent_that_does_not_exist_is_not_found()
    {
        using var client = Client();
        await LiveCleanup.WithCleanupAsync(client, async cleanup =>
        {
            var code = UniqueFolder("ORF");
            cleanup.Folder(code);

            await Assert.ThrowsAsync<NotFoundException>(() => client.Folders.CreateAsync(
                new CreateFolderRequest(code, "Orfana") { ParentId = UniqueFolder("MISSING") },
                Ct));
        });
    }
}
