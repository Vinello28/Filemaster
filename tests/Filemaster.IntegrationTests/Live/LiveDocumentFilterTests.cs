using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using Filemaster.Application;
using Filemaster.Domain;
using Filemaster.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using static Filemaster.IntegrationTests.Live.LiveServer;

namespace Filemaster.IntegrationTests.Live;

/// <summary>
/// Il filtro dei documenti end-to-end: <c>DocumentQuery</c> -> <c>IDocumentStore.ListAsync/EnumerateAsync</c> -> <c>GET /documents</c>
/// -> Sharp-a-File vero. Ogni test carica da se' i suoi documenti, con valori che portano un token unico per esecuzione e per
/// test, cosi' un filtro singolo e' esatto anche senza restringere al proprietario (i documenti degli altri test, dell'altro
/// runtime e del seed non contengono mai il token). L'oracolo e' sempre l'INSIEME ESATTO degli id attesi (calcolato a mano, con i
/// membri che devono restare fuori), confrontato come lista ordinata di numeri: mai "contiene almeno". Le semantiche attese sono
/// quelle del server (<c>DocumentRepository.SearchAsync</c>): tutti i filtri in AND; <c>owner</c> e <c>tag</c> esatti (maiuscole e
/// accenti distinti, il server toglie gli spazi ai bordi); <c>filename</c>, <c>sender</c>, <c>recipient</c>, <c>q</c> e
/// <c>metadata_query</c> "contiene" senza distinguere le maiuscole ma distinguendo gli accenti, con <c>%</c>, <c>_</c> e <c>[</c>
/// letterali; <c>metadata</c> contenimento JSON; <c>created_from</c> incluso e <c>created_to</c> escluso; ordine dal piu' recente.
/// </summary>
[Trait("Category", "Live")]
public sealed class LiveDocumentFilterTests
{
    // a con l'accento grave, minuscola e maiuscola (come escape: il file resta ASCII).
    private const string Grave = "\U000000E0";
    private const string GraveUpper = "\U000000C0";

    // Le otto varianti di un testo cercato: maiuscole, parola nel mezzo, accento si'/no, e i tre caratteri speciali di LIKE.
    // La variante i e' il valore del campo cercato nel documento i di ContainsScenarioAsync.
    private static readonly string[] Variants =
    {
        "-Alfa",
        "-alfa-beta",
        "-Citta",
        "-Citt" + Grave,
        "-50%",
        "-50x",
        "-a_b[c]",
        "-aXbc",
    };

    private static readonly JsonSerializerOptions RelaxedJson = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private enum TextField
    {
        FileName,
        Sender,
        Recipient,
        MetadataText,
    }

    [Fact]
    public async Task FolderId_selects_exactly_that_folder_not_its_subfolders_and_follows_moves()
    {
        using var client = Client();
        await LiveCleanup.WithCleanupAsync(client, async cleanup =>
        {
            var owner = Unique("fld");
            var top = UniqueFolder("FT");
            var other = UniqueFolder("FO");
            var child = UniqueFolder("FC");
            await client.Folders.CreateAsync(new CreateFolderRequest(top, "Top " + top.Value), Ct);
            cleanup.Folder(top);
            await client.Folders.CreateAsync(new CreateFolderRequest(other, "Altra " + other.Value), Ct);
            cleanup.Folder(other);
            await client.Folders.CreateAsync(new CreateFolderRequest(child, "Figlia " + child.Value) { ParentId = top }, Ct);
            cleanup.Folder(child);
            var a = await PutAsync(client, cleanup, "a.pdf", owner: owner, folder: top);
            var b = await PutAsync(client, cleanup, "b.pdf", owner: owner, folder: top);
            var c = await PutAsync(client, cleanup, "c.pdf", owner: owner, folder: other);
            var d = await PutAsync(client, cleanup, "d.pdf", owner: owner, folder: child);
            var e = await PutAsync(client, cleanup, "e.pdf", owner: owner);

            // Senza filtro per cartella nessuna restrizione: null non vuol dire "la radice".
            await AssertSelectsAsync(client.Documents, new DocumentQuery { Owner = owner }, a, b, c, d, e);

            // Esattamente quella cartella, non le sue sottocartelle (d e' nella figlia di top).
            await AssertSelectsAsync(client.Documents, new DocumentQuery { FolderId = top }, a, b);
            await AssertSelectsAsync(client.Documents, new DocumentQuery { FolderId = other }, c);
            await AssertSelectsAsync(client.Documents, new DocumentQuery { FolderId = child }, d);
            await AssertSelectsAsync(client.Documents, new DocumentQuery { FolderId = top, Owner = owner }, a, b);

            // I campi letti dall'elenco sono quelli caricati.
            foreach (var listed in (await client.Documents.ListAsync(new DocumentQuery { FolderId = top }, cancellationToken: Ct)).Items)
            {
                AssertListed(listed, owner: owner, folder: top);
            }

            AssertListed(Assert.Single((await client.Documents.ListAsync(new DocumentQuery { FolderId = child }, cancellationToken: Ct)).Items), owner: owner, folder: child);
            AssertListed(Assert.Single((await client.Documents.ListAsync(new DocumentQuery { FolderId = other }, cancellationToken: Ct)).Items), owner: owner, folder: other);

            // Un codice valido ma inesistente: elenco vuoto, non un errore.
            await AssertSelectsAsync(client.Documents, new DocumentQuery { FolderId = UniqueFolder("NONE") });

            // L'elenco segue gli spostamenti.
            await client.Documents.MoveAsync(d.Id, other, Ct);
            d = await client.Documents.GetAsync(d.Id, Ct);
            await AssertSelectsAsync(client.Documents, new DocumentQuery { FolderId = other }, c, d);
            await AssertSelectsAsync(client.Documents, new DocumentQuery { FolderId = child });
            await AssertSelectsAsync(client.Documents, new DocumentQuery { FolderId = top }, a, b);
            await client.Documents.MoveAsync(a.Id, null, Ct);
            a = await client.Documents.GetAsync(a.Id, Ct);
            await AssertSelectsAsync(client.Documents, new DocumentQuery { FolderId = top }, b);
            await AssertSelectsAsync(client.Documents, new DocumentQuery { Owner = owner }, a, b, c, d, e);
        });
    }

    [Fact]
    public async Task Owner_is_an_exact_match_with_case_prefix_and_special_characters()
    {
        using var client = Client();
        await LiveCleanup.WithCleanupAsync(client, async cleanup =>
        {
            var o = Unique("own");
            const string special = "-a&b=c#d+e f%41/" + "\U000000E9";
            var mario1 = await PutAsync(client, cleanup, "1.pdf", owner: o + "-Mario");
            var lower = await PutAsync(client, cleanup, "2.pdf", owner: o + "-mario");
            var mario2 = await PutAsync(client, cleanup, "3.pdf", owner: o + "-Mario");
            var longer = await PutAsync(client, cleanup, "4.pdf", owner: o + "-Mario Rossi");
            var bare = await PutAsync(client, cleanup, "5.pdf", owner: o);
            await PutAsync(client, cleanup, "6.pdf", tag: o + "-Mario");
            var encoded = await PutAsync(client, cleanup, "7.pdf", owner: o + special);
            var decoded = await PutAsync(client, cleanup, "8.pdf", owner: o + "-a&b=c#d+e f A/" + "\U000000E9");

            await AssertSelectsAsync(client.Documents, new DocumentQuery { Owner = o + "-Mario" }, mario1, mario2);
            await AssertSelectsAsync(client.Documents, new DocumentQuery { Owner = o + "-mario" }, lower);
            await AssertSelectsAsync(client.Documents, new DocumentQuery { Owner = o + "-MARIO" });
            await AssertSelectsAsync(client.Documents, new DocumentQuery { Owner = o }, bare);
            await AssertSelectsAsync(client.Documents, new DocumentQuery { Owner = o + "-Mar" });
            await AssertSelectsAsync(client.Documents, new DocumentQuery { Owner = o + "-Mario Rossi" }, longer);

            // Il server toglie gli spazi ai bordi del filtro (e di cio' che e' stato caricato).
            await AssertSelectsAsync(client.Documents, new DocumentQuery { Owner = "  " + o + "-Mario  " }, mario1, mario2);

            // Spazi, &, =, #, +, % e un accento nel valore arrivano interi: non diventano un altro parametro, uno spazio o un altro carattere.
            await AssertSelectsAsync(client.Documents, new DocumentQuery { Owner = o + special }, encoded);
            await AssertSelectsAsync(client.Documents, new DocumentQuery { Owner = o + "-a&b=c#d+e f A/" + "\U000000E9" }, decoded);
        });
    }

    [Fact]
    public async Task Tag_is_an_exact_match_distinct_from_owner_with_case_prefix_and_accents()
    {
        using var client = Client();
        await LiveCleanup.WithCleanupAsync(client, async cleanup =>
        {
            var t = Unique("tag");
            var red1 = await PutAsync(client, cleanup, "1.pdf", tag: t + "-red");
            var capital = await PutAsync(client, cleanup, "2.pdf", tag: t + "-Red");
            var red2 = await PutAsync(client, cleanup, "3.pdf", tag: t + "-red");
            var longer = await PutAsync(client, cleanup, "4.pdf", tag: t + "-red-1");
            var bare = await PutAsync(client, cleanup, "5.pdf", tag: t);
            var plain = await PutAsync(client, cleanup, "6.pdf", tag: t + "-citta");
            var accented = await PutAsync(client, cleanup, "7.pdf", tag: t + "-citt" + Grave);
            var ownerOnly = await PutAsync(client, cleanup, "8.pdf", owner: t + "-red");
            var padded = await PutAsync(client, cleanup, "9.pdf", tag: "  " + t + "-pad  ");

            await AssertSelectsAsync(client.Documents, new DocumentQuery { Tag = t + "-red" }, red1, red2);
            await AssertSelectsAsync(client.Documents, new DocumentQuery { Tag = t + "-Red" }, capital);
            await AssertSelectsAsync(client.Documents, new DocumentQuery { Tag = t + "-RED" });
            await AssertSelectsAsync(client.Documents, new DocumentQuery { Tag = t }, bare);
            await AssertSelectsAsync(client.Documents, new DocumentQuery { Tag = t + "-re" });
            await AssertSelectsAsync(client.Documents, new DocumentQuery { Tag = t + "-red-1" }, longer);
            await AssertSelectsAsync(client.Documents, new DocumentQuery { Tag = t + "-citta" }, plain);
            await AssertSelectsAsync(client.Documents, new DocumentQuery { Tag = t + "-citt" + Grave }, accented);
            await AssertSelectsAsync(client.Documents, new DocumentQuery { Tag = t + "-CITT" + GraveUpper });
            await AssertSelectsAsync(client.Documents, new DocumentQuery { Tag = "  " + t + "-pad" }, padded);

            // Il proprietario non e' l'etichetta: il documento 8 ha il valore in owner, non in tag.
            await AssertSelectsAsync(client.Documents, new DocumentQuery { Owner = t + "-red" }, ownerOnly);
            AssertListed(Assert.Single((await client.Documents.ListAsync(new DocumentQuery { Owner = t + "-red" }, cancellationToken: Ct)).Items), owner: t + "-red");
            foreach (var listed in (await client.Documents.ListAsync(new DocumentQuery { Tag = t + "-red" }, cancellationToken: Ct)).Items)
            {
                AssertListed(listed, tag: t + "-red");
            }
        });
    }

    [Fact]
    public Task FileName_contains_the_text_ignoring_case_and_not_the_accent_with_literal_wildcards() =>
        ContainsScenarioAsync(TextField.FileName);

    [Fact]
    public Task Sender_text_contains_the_text_ignoring_case_and_not_the_accent_with_literal_wildcards() =>
        ContainsScenarioAsync(TextField.Sender);

    [Fact]
    public Task Recipient_text_contains_the_text_ignoring_case_and_not_the_accent_with_literal_wildcards() =>
        ContainsScenarioAsync(TextField.Recipient);

    [Fact]
    public Task MetadataText_contains_the_text_of_the_stored_json_ignoring_case_and_not_the_accent_with_literal_wildcards() =>
        ContainsScenarioAsync(TextField.MetadataText);

    [Fact]
    public async Task MetadataText_matches_keys_and_nested_values_of_the_stored_json_but_not_other_fields()
    {
        using var client = Client();
        await LiveCleanup.WithCleanupAsync(client, async cleanup =>
        {
            var k = Unique("mtx");
            var key = await PutAsync(client, cleanup, "k.pdf", metadata: "{\"" + k + "-key\":1}");
            var deep = await PutAsync(client, cleanup, "d.pdf", metadata: "{\"a\":{\"b\":[\"" + k + "-deep\"]}}");
            var value = await PutAsync(client, cleanup, "v.pdf", metadata: "{\"a\":\"" + k + "-val\"}");
            var number = await PutAsync(client, cleanup, "n.pdf", metadata: "{\"" + k + "-num\":123456789}");
            await PutAsync(client, cleanup, k + "-name.pdf", owner: k + "-owner", tag: k + "-tag", sender: k + "-s", recipient: k + "-r");

            await AssertSelectsAsync(client.Documents, new DocumentQuery { MetadataText = k }, key, deep, value, number);
            await AssertSelectsAsync(client.Documents, new DocumentQuery { MetadataText = k + "-key" }, key);
            await AssertSelectsAsync(client.Documents, new DocumentQuery { MetadataText = k + "-DEEP" }, deep);
            await AssertSelectsAsync(client.Documents, new DocumentQuery { MetadataText = k + "-val" }, value);
            await AssertSelectsAsync(client.Documents, new DocumentQuery { MetadataText = k + "-num\":123456789" }, number);
            await AssertSelectsAsync(client.Documents, new DocumentQuery { MetadataText = k + "-name" });
            await AssertSelectsAsync(client.Documents, new DocumentQuery { MetadataText = k + "-owner" });
            await AssertSelectsAsync(client.Documents, new DocumentQuery { MetadataText = k + "-s" });
        });
    }

    [Fact]
    public async Task Text_searches_file_name_sender_recipient_and_metadata_but_not_owner_or_tag()
    {
        using var client = Client();
        await LiveCleanup.WithCleanupAsync(client, async cleanup =>
        {
            var k = Unique("qq");
            var inName = await PutAsync(client, cleanup, k + "-in-name.pdf");
            var inSender = await PutAsync(client, cleanup, "s.pdf", sender: k + "-in-sender");
            var inRecipient = await PutAsync(client, cleanup, "r.pdf", recipient: k + "-in-recipient");
            var inValue = await PutAsync(client, cleanup, "v.pdf", metadata: "{\"note\":\"" + k + "-in-value\"}");
            var inKey = await PutAsync(client, cleanup, "k.pdf", metadata: "{\"" + k + "-in-key\":true}");
            var upper = await PutAsync(client, cleanup, "u.pdf", sender: k.ToUpperInvariant() + "-UP");
            var plain = await PutAsync(client, cleanup, k + "-Citta.pdf");
            var accented = await PutAsync(client, cleanup, "g.pdf", sender: k + "-Citt" + Grave);
            var percent = await PutAsync(client, cleanup, "p.pdf", recipient: k + "-5%");
            var percentNot = await PutAsync(client, cleanup, "px.pdf", recipient: k + "-5x");
            var underscore = await PutAsync(client, cleanup, "u2.pdf", recipient: k + "-a_b");
            var underscoreNot = await PutAsync(client, cleanup, k + "-aXb.pdf");
            await PutAsync(client, cleanup, "decoy.pdf", owner: k + "-in-owner", tag: k + "-in-tag");
            await PutAsync(client, cleanup, "nothing.pdf");

            await AssertSelectsAsync(
                client.Documents,
                new DocumentQuery { Text = k },
                inName, inSender, inRecipient, inValue, inKey, upper, plain, accented, percent, percentNot, underscore, underscoreNot);
            await AssertSelectsAsync(
                client.Documents,
                new DocumentQuery { Text = k.ToUpperInvariant() },
                inName, inSender, inRecipient, inValue, inKey, upper, plain, accented, percent, percentNot, underscore, underscoreNot);
            await AssertSelectsAsync(client.Documents, new DocumentQuery { Text = k + "-in-name" }, inName);
            await AssertSelectsAsync(client.Documents, new DocumentQuery { Text = k + "-in-sender" }, inSender);
            await AssertSelectsAsync(client.Documents, new DocumentQuery { Text = k + "-in-recipient" }, inRecipient);
            await AssertSelectsAsync(client.Documents, new DocumentQuery { Text = k + "-IN-VALUE" }, inValue);
            await AssertSelectsAsync(client.Documents, new DocumentQuery { Text = k + "-in-key" }, inKey);
            await AssertSelectsAsync(client.Documents, new DocumentQuery { Text = k + "-up" }, upper);

            // Owner e tag non fanno parte della ricerca libera.
            await AssertSelectsAsync(client.Documents, new DocumentQuery { Text = k + "-in-owner" });
            await AssertSelectsAsync(client.Documents, new DocumentQuery { Text = k + "-in-tag" });

            // Accento distinto, % e _ letterali.
            await AssertSelectsAsync(client.Documents, new DocumentQuery { Text = k + "-citta" }, plain);
            await AssertSelectsAsync(client.Documents, new DocumentQuery { Text = k + "-citt" + Grave }, accented);
            await AssertSelectsAsync(client.Documents, new DocumentQuery { Text = k + "-5%" }, percent);
            await AssertSelectsAsync(client.Documents, new DocumentQuery { Text = k + "-a_b" }, underscore);
            await AssertSelectsAsync(client.Documents, new DocumentQuery { Text = k + "-nothing" });
        });
    }

    [Fact]
    public async Task Metadata_selects_by_json_containment_with_nested_objects_arrays_and_typed_numbers()
    {
        using var client = Client();
        await LiveCleanup.WithCleanupAsync(client, async cleanup =>
        {
            var s = Unique("meta");
            var q = JsonSerializer.Serialize(s, RelaxedJson);
            var m0 = await PutAsync(client, cleanup, "0.pdf", metadata: "{\"scope\":" + q + ",\"k\":{\"a\":1,\"b\":{\"c\":\"x\"}}}");
            var m1 = await PutAsync(client, cleanup, "1.pdf", metadata: "{\"scope\":" + q + ",\"k\":{\"a\":1}}");
            var m2 = await PutAsync(client, cleanup, "2.pdf", metadata: "{\"scope\":" + q + ",\"k\":{\"a\":2,\"b\":{\"c\":\"y\"}}}");
            var m3 = await PutAsync(client, cleanup, "3.pdf", metadata: "{\"scope\":" + q + ",\"tags\":[\"red\",\"green\",\"blue\"]}");
            var m4 = await PutAsync(client, cleanup, "4.pdf", metadata: "{\"scope\":" + q + ",\"tags\":[\"red\"]}");
            var m5 = await PutAsync(client, cleanup, "5.pdf", metadata: "{\"scope\":" + q + ",\"n\":1}");
            var m6 = await PutAsync(client, cleanup, "6.pdf", metadata: "{\"scope\":" + q + ",\"n\":\"1\"}");
            var m7 = await PutAsync(client, cleanup, "7.pdf", metadata: "{\"scope\":" + q + ",\"n\":1.0,\"flag\":true}");
            var m8 = await PutAsync(client, cleanup, "8.pdf", metadata: "{\"other\":" + q + "}");
            string Filter(string rest) => "{\"scope\":" + q + (rest.Length == 0 ? string.Empty : "," + rest) + "}";

            async Task SelectsAsync(string filter, params Document[] expected)
            {
                using var json = JsonDocument.Parse(filter);
                await AssertSelectsAsync(client.Documents, new DocumentQuery { Metadata = json.RootElement }, expected);
            }

            // Solo la chiave scope: i documenti del test, non quello con il valore sotto un'altra chiave.
            await SelectsAsync(Filter(string.Empty), m0, m1, m2, m3, m4, m5, m6, m7);
            using (var scope = JsonDocument.Parse(Filter(string.Empty)))
            {
                var listed = (await client.Documents.ListAsync(new DocumentQuery { Metadata = scope.RootElement }, cancellationToken: Ct)).Items;
                Assert.All(listed, d => Assert.Equal(s, d.Metadata.GetProperty("scope").GetString()));
                Assert.Equal(1, listed.Single(d => d.Id == m5.Id).Metadata.GetProperty("n").GetInt32());
                Assert.Equal("1", listed.Single(d => d.Id == m6.Id).Metadata.GetProperty("n").GetString());
            }

            await SelectsAsync("{\"other\":" + q + "}", m8);

            // Oggetti annidati.
            await SelectsAsync(Filter("\"k\":{\"a\":1}"), m0, m1);
            await SelectsAsync(Filter("\"k\":{\"a\":2}"), m2);
            await SelectsAsync(Filter("\"k\":{\"b\":{\"c\":\"x\"}}"), m0);
            await SelectsAsync(Filter("\"k\":{\"a\":1,\"b\":{\"c\":\"x\"}}"), m0);
            await SelectsAsync(Filter("\"k\":{\"a\":1,\"b\":{\"c\":\"y\"}}"));
            await SelectsAsync(Filter("\"k\":{\"a\":3}"));

            // Array: ogni elemento del filtro in almeno un elemento del documento (ordine e duplicati non contano).
            await SelectsAsync(Filter("\"tags\":[\"red\"]"), m3, m4);
            await SelectsAsync(Filter("\"tags\":[\"red\",\"blue\"]"), m3);
            await SelectsAsync(Filter("\"tags\":[\"blue\",\"red\",\"red\"]"), m3);
            await SelectsAsync(Filter("\"tags\":[\"green\"]"), m3);
            await SelectsAsync(Filter("\"tags\":[\"red\",\"yellow\"]"));

            // Tipi: 1 e "1" sono diversi, 1 e 1.0 sono lo stesso numero, i booleani contano.
            await SelectsAsync(Filter("\"n\":1"), m5, m7);
            await SelectsAsync(Filter("\"n\":1.0"), m5, m7);
            await SelectsAsync(Filter("\"n\":\"1\""), m6);
            await SelectsAsync(Filter("\"n\":2"));
            await SelectsAsync(Filter("\"flag\":true"), m7);
            await SelectsAsync(Filter("\"flag\":false"));

            // Chiavi e testi si confrontano byte per byte; una chiave assente esclude tutto.
            await SelectsAsync("{\"SCOPE\":" + q + "}");
            await SelectsAsync("{\"scope\":" + JsonSerializer.Serialize(s.ToUpperInvariant(), RelaxedJson) + "}");
            await SelectsAsync(Filter("\"nokey\":1"));
        });
    }

    [Fact]
    public async Task SenderId_and_RecipientId_select_by_the_contact_role_not_by_the_free_text()
    {
        using var client = Client();
        await LiveCleanup.WithCleanupAsync(client, async cleanup =>
        {
            var contacts = await client.Contacts.ListAsync(new ContactQuery { Text = LiveSeed.SearchText }, cancellationToken: Ct);
            var external = Assert.Single(contacts.Items, c => c.Code == LiveSeed.ExternalCode).Id;
            var user = Assert.Single(contacts.Items, c => c.Code == LiveSeed.UserCode).Id;
            var group = Assert.Single(contacts.Items, c => c.Code == LiveSeed.GroupCode).Id;
            var seeded = Assert.Single((await client.Documents.ListAsync(new DocumentQuery { Owner = LiveSeed.DocumentOwner }, cancellationToken: Ct)).Items);

            // Un documento con gli stessi NOMI come testo libero ma senza collegamenti: i filtri per id non lo vedono.
            var o = Unique("ids");
            var textOnly = await PutAsync(client, cleanup, "solo-testo.pdf", owner: o, sender: LiveSeed.ExternalName, recipient: LiveSeed.UserName);

            // Mittente: solo il contatto esterno, destinatari: solo utente e gruppo (senza alcun altro filtro).
            await AssertSelectsAsync(client.Documents, new DocumentQuery { SenderId = external }, seeded);
            await AssertSelectsAsync(client.Documents, new DocumentQuery { RecipientId = user }, seeded);
            await AssertSelectsAsync(client.Documents, new DocumentQuery { RecipientId = group }, seeded);

            // Il ruolo conta: un destinatario non e' un mittente e viceversa.
            await AssertSelectsAsync(client.Documents, new DocumentQuery { SenderId = user });
            await AssertSelectsAsync(client.Documents, new DocumentQuery { SenderId = group });
            await AssertSelectsAsync(client.Documents, new DocumentQuery { RecipientId = external });

            // Un id valido ma sconosciuto: elenco vuoto, non un errore.
            await AssertSelectsAsync(client.Documents, new DocumentQuery { SenderId = ContactId.From(int.MaxValue) });
            await AssertSelectsAsync(client.Documents, new DocumentQuery { RecipientId = ContactId.From(int.MaxValue) });

            // In AND fra loro e con gli altri filtri.
            await AssertSelectsAsync(client.Documents, new DocumentQuery { SenderId = external, RecipientId = user }, seeded);
            await AssertSelectsAsync(client.Documents, new DocumentQuery { SenderId = external, RecipientId = external });
            await AssertSelectsAsync(client.Documents, new DocumentQuery { SenderId = external, Owner = LiveSeed.DocumentOwner }, seeded);
            await AssertSelectsAsync(client.Documents, new DocumentQuery { SenderId = external, Owner = o });
            await AssertSelectsAsync(client.Documents, new DocumentQuery { RecipientId = group, Tag = o });

            // Il testo libero e' un'altra cosa: trova il documento senza collegamenti (qui, ristretto al suo proprietario).
            await AssertSelectsAsync(client.Documents, new DocumentQuery { Sender = LiveSeed.ExternalName, Owner = o }, textOnly);
            await AssertSelectsAsync(client.Documents, new DocumentQuery { Recipient = LiveSeed.UserName.ToUpperInvariant(), Owner = o }, textOnly);
            await AssertSelectsAsync(client.Documents, new DocumentQuery { Sender = LiveSeed.ExternalName, Owner = LiveSeed.DocumentOwner });
        });
    }

    [Fact]
    public async Task CreatedFrom_is_inclusive_and_CreatedBefore_is_exclusive_with_any_offset()
    {
        var micro = TimeSpan.FromTicks(10);
        using var client = Client();
        await LiveCleanup.WithCleanupAsync(client, async cleanup =>
        {
            var o = Unique("dt");
            var d0 = await PutAsync(client, cleanup, "0.pdf", owner: o);
            var d1 = await PutAsync(client, cleanup, "1.pdf", owner: o);
            var d2 = await PutAsync(client, cleanup, "2.pdf", owner: o);
            var d3 = await PutAsync(client, cleanup, "3.pdf", owner: o);
            var c0 = d0.CreatedAt;
            var c1 = d1.CreatedAt;
            var c2 = d2.CreatedAt;
            var c3 = d3.CreatedAt;
            Assert.True(c0 < c1 && c1 < c2 && c2 < c3, "gli istanti di creazione devono essere strettamente crescenti");

            // created_from incluso.
            await AssertSelectsAsync(client.Documents, new DocumentQuery { Owner = o, CreatedFrom = c1 }, d1, d2, d3);
            await AssertSelectsAsync(client.Documents, new DocumentQuery { Owner = o, CreatedFrom = c1 + micro }, d2, d3);
            await AssertSelectsAsync(client.Documents, new DocumentQuery { Owner = o, CreatedFrom = c1 - micro }, d1, d2, d3);
            await AssertSelectsAsync(client.Documents, new DocumentQuery { Owner = o, CreatedFrom = c3 }, d3);
            await AssertSelectsAsync(client.Documents, new DocumentQuery { Owner = o, CreatedFrom = c3 + micro });

            // created_to escluso.
            await AssertSelectsAsync(client.Documents, new DocumentQuery { Owner = o, CreatedBefore = c2 }, d0, d1);
            await AssertSelectsAsync(client.Documents, new DocumentQuery { Owner = o, CreatedBefore = c2 + micro }, d0, d1, d2);
            await AssertSelectsAsync(client.Documents, new DocumentQuery { Owner = o, CreatedBefore = c0 });
            await AssertSelectsAsync(client.Documents, new DocumentQuery { Owner = o, CreatedBefore = c0 + micro }, d0);

            // Insieme: [inizio, fine).
            await AssertSelectsAsync(client.Documents, new DocumentQuery { Owner = o, CreatedFrom = c1, CreatedBefore = c3 }, d1, d2);
            await AssertSelectsAsync(client.Documents, new DocumentQuery { Owner = o, CreatedFrom = c1, CreatedBefore = c1 + micro }, d1);
            await AssertSelectsAsync(client.Documents, new DocumentQuery { Owner = o, CreatedFrom = c1 + micro, CreatedBefore = c2 });
            await AssertSelectsAsync(client.Documents, new DocumentQuery { Owner = o, CreatedFrom = c0.AddDays(-1), CreatedBefore = c3.AddDays(1) }, d0, d1, d2, d3);

            // Lo stesso istante scritto con un altro fuso e' lo stesso istante.
            var plus2 = TimeSpan.FromHours(2);
            var minus5 = TimeSpan.FromHours(-5);
            await AssertSelectsAsync(client.Documents, new DocumentQuery { Owner = o, CreatedFrom = c1.ToOffset(plus2) }, d1, d2, d3);
            await AssertSelectsAsync(client.Documents, new DocumentQuery { Owner = o, CreatedBefore = c3.ToOffset(minus5) }, d0, d1, d2);
            await AssertSelectsAsync(
                client.Documents,
                new DocumentQuery { Owner = o, CreatedFrom = c1.ToOffset(minus5), CreatedBefore = c3.ToOffset(plus2) },
                d1,
                d2);
            await AssertSelectsAsync(client.Documents, new DocumentQuery { Owner = o, CreatedFrom = c1.ToOffset(plus2) + micro }, d2, d3);

            // Un intervallo valido come istanti ma "rovesciato" se si guardasse l'ora scritta (inizio +02:00, fine -05:00).
            await AssertSelectsAsync(
                client.Documents,
                new DocumentQuery { Owner = o, CreatedFrom = c1.ToOffset(plus2), CreatedBefore = c3.ToOffset(minus5) },
                d1,
                d2);

            // Senza altri filtri: un intervallo nel lontano passato o nel lontano futuro non contiene nessun documento.
            await AssertSelectsAsync(client.Documents, new DocumentQuery { CreatedBefore = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero) });
            await AssertSelectsAsync(client.Documents, new DocumentQuery { CreatedFrom = new DateTimeOffset(2100, 1, 1, 0, 0, 0, TimeSpan.Zero) });
        });
    }

    [Fact]
    public async Task Filters_combine_in_AND_and_each_added_filter_only_narrows_the_result()
    {
        using var client = Client();
        await LiveCleanup.WithCleanupAsync(client, async cleanup =>
        {
            // Tabella di verita': 8 documenti, bit 1 = proprietario (pari O1, dispari O2), bit 2 = etichetta (T1/T2),
            // bit 4 = cartella (F/radice); mittente "Acme" per gli indici multipli di 3, "Beta" per gli altri.
            var n = Unique("and");
            var o1 = n + "-ann";
            var o2 = n + "-bob";
            var t1 = n + "-red";
            var t2 = n + "-blue";
            var folder = UniqueFolder("AND");
            await client.Folders.CreateAsync(new CreateFolderRequest(folder, "AND " + folder.Value), Ct);
            cleanup.Folder(folder);
            var d = new Document[8];
            for (var i = 0; i < d.Length; i++)
            {
                d[i] = await PutAsync(
                    client,
                    cleanup,
                    n + "-d" + i.ToString(CultureInfo.InvariantCulture) + ".pdf",
                    owner: (i & 1) == 0 ? o1 : o2,
                    tag: (i & 2) == 0 ? t1 : t2,
                    sender: n + (i % 3 == 0 ? " Acme" : " Beta"),
                    folder: (i & 4) == 0 ? folder : (FolderCode?)null);
            }

            // Un filtro per volta, poi a coppie, a terne, a quattro e a cinque: ogni passo restringe.
            await AssertSelectsAsync(client.Documents, new DocumentQuery { FileName = n }, d);
            await AssertSelectsAsync(client.Documents, new DocumentQuery { Owner = o1 }, d[0], d[2], d[4], d[6]);
            await AssertSelectsAsync(client.Documents, new DocumentQuery { Owner = o1, Tag = t1 }, d[0], d[4]);
            await AssertSelectsAsync(client.Documents, new DocumentQuery { Owner = o1, Tag = t1, FolderId = folder }, d[0]);
            await AssertSelectsAsync(client.Documents, new DocumentQuery { Owner = o2, Tag = t2, FolderId = folder }, d[3]);
            AssertListed(
                Assert.Single((await client.Documents.ListAsync(new DocumentQuery { Owner = o2, Tag = t2, FolderId = folder }, cancellationToken: Ct)).Items),
                owner: o2,
                tag: t2,
                sender: n + " Acme",
                folder: folder);
            AssertListed(
                Assert.Single((await client.Documents.ListAsync(new DocumentQuery { Owner = o2, Tag = t2, FileName = "-d7" }, cancellationToken: Ct)).Items),
                owner: o2,
                tag: t2,
                sender: n + " Beta");
            await AssertSelectsAsync(client.Documents, new DocumentQuery { Owner = o2, Tag = t2 }, d[3], d[7]);
            await AssertSelectsAsync(client.Documents, new DocumentQuery { Tag = t2, FolderId = folder }, d[2], d[3]);
            await AssertSelectsAsync(client.Documents, new DocumentQuery { Owner = o1, Tag = t2 }, d[2], d[6]);
            await AssertSelectsAsync(client.Documents, new DocumentQuery { Owner = o1, Tag = t2, Sender = "acme" }, d[6]);
            await AssertSelectsAsync(client.Documents, new DocumentQuery { Owner = o1, Tag = t2, Sender = "acme", FileName = n + "-D6" }, d[6]);
            await AssertSelectsAsync(
                client.Documents,
                new DocumentQuery { Owner = o1, Tag = t2, Sender = "acme", FolderId = null, CreatedFrom = d[6].CreatedAt },
                d[6]);
            await AssertSelectsAsync(client.Documents, new DocumentQuery { Sender = n + " Acme" }, d[0], d[3], d[6]);
            await AssertSelectsAsync(client.Documents, new DocumentQuery { Sender = n + " Acme", Tag = t1 }, d[0]);
            await AssertSelectsAsync(client.Documents, new DocumentQuery { Sender = n + " Acme", Tag = t1, Owner = o1, FolderId = folder }, d[0]);

            // Filtri che si escludono a vicenda: nessun documento, e nessun errore.
            await AssertSelectsAsync(client.Documents, new DocumentQuery { Owner = o1, FileName = n + "-d1" });
            await AssertSelectsAsync(client.Documents, new DocumentQuery { Owner = o2, Tag = t1, Sender = "acme" });
            await AssertSelectsAsync(client.Documents, new DocumentQuery { Owner = o1, Tag = t2, Sender = "acme", CreatedFrom = d[6].CreatedAt + TimeSpan.FromTicks(10) });
            await AssertSelectsAsync(client.Documents, new DocumentQuery { Owner = o1, FolderId = folder, CreatedBefore = d[0].CreatedAt });
            await AssertSelectsAsync(client.Documents, new DocumentQuery { Owner = o1, Tag = n + "-nope", FolderId = folder });
        });
    }

    [Fact]
    public async Task A_text_filter_made_only_of_whitespace_or_empty_is_ignored()
    {
        using var client = Client();
        await LiveCleanup.WithCleanupAsync(client, async cleanup =>
        {
            var n = Unique("ws");
            var a = await PutAsync(client, cleanup, n + "-a.pdf", owner: n + "-x", tag: n + "-t", sender: "alfa", recipient: "beta", metadata: "{\"w\":1}");
            var b = await PutAsync(client, cleanup, n + "-b.pdf", owner: n + "-x", tag: n + "-t", sender: "gamma", recipient: "delta");
            var c = await PutAsync(client, cleanup, n + "-c.pdf", owner: n + "-x", tag: n + "-t");
            await PutAsync(client, cleanup, n + "-d.pdf", owner: n + "-y", tag: n + "-u");

            // Il filtro di soli spazi, di soli a capo/tabulazione o vuoto vale "nessun filtro": stesso risultato della ricerca ristretta.
            foreach (var blank in new[] { "   ", " \t\r\n ", string.Empty })
            {
                await AssertSelectsAsync(client.Documents, new DocumentQuery { Owner = n + "-x", FileName = blank }, a, b, c);
                await AssertSelectsAsync(client.Documents, new DocumentQuery { Owner = n + "-x", Sender = blank }, a, b, c);
                await AssertSelectsAsync(client.Documents, new DocumentQuery { Owner = n + "-x", Recipient = blank }, a, b, c);
                await AssertSelectsAsync(client.Documents, new DocumentQuery { Owner = n + "-x", Text = blank }, a, b, c);
                await AssertSelectsAsync(client.Documents, new DocumentQuery { Owner = n + "-x", MetadataText = blank }, a, b, c);
                await AssertSelectsAsync(client.Documents, new DocumentQuery { Owner = n + "-x", Tag = blank }, a, b, c);
                await AssertSelectsAsync(client.Documents, new DocumentQuery { Tag = n + "-t", Owner = blank }, a, b, c);
                await AssertSelectsAsync(
                    client.Documents,
                    new DocumentQuery { Owner = n + "-x", FileName = blank, Sender = blank, Recipient = blank, Text = blank, MetadataText = blank, Tag = blank },
                    a,
                    b,
                    c);
            }

            // Un filtro vero accanto a uno vuoto resta in vigore.
            await AssertSelectsAsync(client.Documents, new DocumentQuery { Owner = n + "-x", Sender = "  gamma ", Tag = " " }, b);
            await AssertSelectsAsync(client.Documents, new DocumentQuery { Owner = n + "-x", FileName = "   ", Recipient = "BETA" }, a);
        });
    }

    [Fact]
    public async Task Results_come_newest_first_and_a_filtered_page_walk_reaches_exactly_the_oracle_in_order()
    {
        using var client = Client();
        await LiveCleanup.WithCleanupAsync(client, async cleanup =>
        {
            // Nove documenti creati in alternanza fra due proprietari: filtrando per x, ogni pagina deve saltare quelli di y.
            var x = Unique("pgx");
            var y = Unique("pgy");
            var docs = new List<Document>();
            for (var i = 0; i < 9; i++)
            {
                var mine = i % 2 == 0;
                docs.Add(await PutAsync(
                    client,
                    cleanup,
                    "pg-" + i.ToString(CultureInfo.InvariantCulture) + ".pdf",
                    owner: mine ? x : y,
                    tag: mine && i % 4 == 0 ? x + "-four" : y));
            }

            // Dal piu' recente: per x gli indici 8, 6, 4, 2, 0; con l'etichetta "four" 8, 4, 0.
            var expectedX = new[] { docs[8], docs[6], docs[4], docs[2], docs[0] };
            var expectedFour = new[] { docs[8], docs[4], docs[0] };
            var byOwner = new DocumentQuery { Owner = x };
            var byOwnerAndTag = new DocumentQuery { Owner = x, Tag = x + "-four" };

            var all = (await client.Documents.ListAsync(byOwner, cancellationToken: Ct)).Items;
            Assert.Equal(InOrder(expectedX), InOrder(all));
            Assert.True(all.Zip(all.Skip(1), (newer, older) => newer.CreatedAt > older.CreatedAt).All(ok => ok));
            var four = (await client.Documents.ListAsync(byOwnerAndTag, cancellationToken: Ct)).Items;
            Assert.Equal(InOrder(expectedFour), InOrder(four));
            var others = (await client.Documents.ListAsync(new DocumentQuery { Owner = y }, cancellationToken: Ct)).Items;
            Assert.Equal(InOrder(new[] { docs[7], docs[5], docs[3], docs[1] }), InOrder(others));

            // Una pagina per documento: il cursore porta al successivo e l'ultima non ne ha.
            var singles = await WalkAsync(client.Documents, byOwner, 1);
            Assert.Equal(new[] { new[] { docs[8].Id.Number }, new[] { docs[6].Id.Number }, new[] { docs[4].Id.Number }, new[] { docs[2].Id.Number }, new[] { docs[0].Id.Number } }, singles);

            // Con due filtri.
            var singlesFour = await WalkAsync(client.Documents, byOwnerAndTag, 1);
            Assert.Equal(new[] { new[] { docs[8].Id.Number }, new[] { docs[4].Id.Number }, new[] { docs[0].Id.Number } }, singlesFour);

            // Altre dimensioni di pagina: l'ultima pagina piena non ha cursore.
            Assert.Equal(new[] { new[] { docs[8].Id.Number, docs[6].Id.Number }, new[] { docs[4].Id.Number, docs[2].Id.Number }, new[] { docs[0].Id.Number } }, await WalkAsync(client.Documents, byOwner, 2));
            Assert.Equal(new[] { new[] { docs[8].Id.Number, docs[6].Id.Number, docs[4].Id.Number, docs[2].Id.Number }, new[] { docs[0].Id.Number } }, await WalkAsync(client.Documents, byOwner, 4));
            Assert.Equal(new[] { InOrder(expectedX) }, await WalkAsync(client.Documents, byOwner, 5));
            Assert.Equal(new[] { InOrder(expectedX) }, await WalkAsync(client.Documents, byOwner, PageRequest.MaxLimit));

            // La prima pagina e la seconda, a mano, con il cursore della prima.
            var first = await client.Documents.ListAsync(byOwner, new PageRequest(limit: 2), Ct);
            Assert.Equal(new[] { docs[8].Id.Number, docs[6].Id.Number }, InOrder(first.Items));
            Assert.NotNull(first.NextCursor);
            var second = await client.Documents.ListAsync(byOwner, new PageRequest(first.NextCursor, 2), Ct);
            Assert.Equal(new[] { docs[4].Id.Number, docs[2].Id.Number }, InOrder(second.Items));
            Assert.NotNull(second.NextCursor);
            Assert.NotEqual(first.NextCursor, second.NextCursor);

            // EnumerateAsync con filtro, a pagine di 1, 2, 3, 5 e quella di default: stessa sequenza, stesso ordine.
            foreach (var size in new int?[] { 1, 2, 3, 5, null })
            {
                var enumerated = new List<Document>();
                await foreach (var document in client.Documents.EnumerateAsync(byOwner, size, Ct))
                {
                    enumerated.Add(document);
                }

                Assert.Equal(InOrder(expectedX), InOrder(enumerated));
                var enumeratedFour = new List<Document>();
                await foreach (var document in client.Documents.EnumerateAsync(byOwnerAndTag, size, Ct))
                {
                    enumeratedFour.Add(document);
                }

                Assert.Equal(InOrder(expectedFour), InOrder(enumeratedFour));
            }

            // Un filtro senza risultati: nessun elemento, nessun cursore, nessun errore.
            var none = await client.Documents.ListAsync(new DocumentQuery { Owner = x + "-none" }, new PageRequest(limit: 1), Ct);
            Assert.Empty(none.Items);
            Assert.Null(none.NextCursor);
            await foreach (var unexpected in client.Documents.EnumerateAsync(new DocumentQuery { Owner = x + "-none" }, 1, Ct))
            {
                Assert.Fail("Il filtro non doveva selezionare nulla, ma ha dato il documento " + unexpected.Id.Value);
            }
        });
    }

    [Fact]
    public async Task A_range_that_ends_before_it_starts_is_rejected_by_the_client_before_any_request()
    {
        _ = Url; // salta (o fallisce con REQUIRED=1) senza server, come ogni test live
        var options = new FilemasterOptions
        {
            BaseAddress = new Uri("http://127.0.0.1:1/", UriKind.Absolute),
            ApiKey = ReadKey,
            RequestTimeout = TimeSpan.FromSeconds(5),
        };
        options.Retry.MaxAttempts = 1;
        using var client = FilemasterClientFactory.Create(options);
        var now = DateTimeOffset.UtcNow;

        // Controprova: verso questo indirizzo una richiesta valida si prova a fare e fallisce per la connessione.
        await Assert.ThrowsAsync<ConnectionException>(
            () => client.Documents.ListAsync(new DocumentQuery { CreatedFrom = now }, cancellationToken: Ct));

        foreach (var inverted in new[]
        {
            new DocumentQuery { CreatedFrom = now, CreatedBefore = now.AddSeconds(-1) },
            new DocumentQuery { CreatedFrom = now, CreatedBefore = now },
            new DocumentQuery { CreatedFrom = now, CreatedBefore = now.AddHours(-1).ToOffset(TimeSpan.FromHours(5)) },
            new DocumentQuery { CreatedFrom = now.ToOffset(TimeSpan.FromHours(2)), CreatedBefore = now.ToOffset(TimeSpan.FromHours(-5)) },
        })
        {
            var error = await Assert.ThrowsAsync<ArgumentException>(() => client.Documents.ListAsync(inverted, cancellationToken: Ct));
            Assert.Equal(nameof(DocumentQuery.CreatedBefore), error.ParamName);
            Assert.Throws<ArgumentException>(() => client.Documents.EnumerateAsync(inverted, 1, Ct));
        }
    }

    [Fact]
    public async Task An_application_resolving_IDocumentStore_from_AddFilemaster_gets_exactly_what_its_search_form_selects()
    {
        var url = Url;
        var key = WriteKey;
        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());
        var recorder = new RecordingHandler();
        builder.Services.AddFilemaster(o =>
        {
            o.BaseAddress = url;
            o.ApiKey = key;
        }).AddHttpMessageHandler(() => recorder);
        using var host = builder.Build();
        await host.StartAsync(Ct);
        try
        {
            var client = host.Services.GetRequiredService<IFilemasterClient>();
            var store = host.Services.GetRequiredService<IDocumentStore>();
            var folders = host.Services.GetRequiredService<IFolderCatalog>();
            var directory = host.Services.GetRequiredService<IContactDirectory>();
            await LiveCleanup.WithCleanupAsync(client, async cleanup =>
            {
                var n = Unique("app");
                var owner = n + "-owner";
                var folder = UniqueFolder("APP");
                await folders.CreateAsync(new CreateFolderRequest(folder, "App " + folder.Value), Ct);
                cleanup.Folder(folder);
                var tag = n + "-tag";

                // Otto documenti, in quest'ordine di creazione: j0 e j7 fuori dall'intervallo di date, j3 nella radice,
                // j4 con un'altra etichetta, j5 con un altro nome (j6 e' l'estremo escluso della data finale).
                var j = new List<Document>();
                for (var i = 0; i < 8; i++)
                {
                    j.Add(await PutAsync(
                        client,
                        cleanup,
                        n + (i == 5 ? "-offer-" : "-invoice-") + i.ToString(CultureInfo.InvariantCulture) + ".pdf",
                        owner: owner,
                        tag: i == 4 ? n + "-other" : tag,
                        folder: i == 3 ? (FolderCode?)null : folder));
                }

                // Il modulo di ricerca di un gestionale: campi di testo, tutti stringhe; i vuoti restano vuoti.
                var form = new Dictionary<string, string>
                {
                    ["owner"] = "  " + owner + " ",
                    ["tag"] = tag,
                    ["name"] = "INVOICE",
                    ["folder"] = folder.Value,
                    ["from"] = j[1].CreatedAt.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffffK", CultureInfo.InvariantCulture),
                    ["to"] = j[6].CreatedAt.ToOffset(TimeSpan.FromHours(2)).ToString("yyyy-MM-dd'T'HH:mm:ss.ffffffK", CultureInfo.InvariantCulture),
                    ["sender"] = string.Empty,
                };
                var query = FromForm(form);
                await AssertSelectsAsync(store, query, j[1], j[2]);
                Assert.Equal(new[] { j[2].Id.Number, j[1].Id.Number }, InOrder((await store.ListAsync(query, cancellationToken: Ct)).Items));
                recorder.Clear();
                var enumerated = new List<Document>();
                await foreach (var document in store.EnumerateAsync(query, 1, Ct))
                {
                    enumerated.Add(document);
                }

                Assert.Equal(new[] { j[2].Id.Number, j[1].Id.Number }, InOrder(enumerated));

                // Sul filo: due richieste da un documento, entrambe con i filtri, la seconda col cursore.
                var wire = recorder.Requests;
                Assert.Equal(2, wire.Count);
                Assert.All(wire, line => Assert.Contains("limit=1", line, StringComparison.Ordinal));
                Assert.All(wire, line => Assert.Contains("owner=" + Uri.EscapeDataString(form["owner"]), line, StringComparison.Ordinal));
                Assert.All(wire, line => Assert.Contains("folder_id=" + folder.Value, line, StringComparison.Ordinal));
                Assert.DoesNotContain("cursor=", wire[0], StringComparison.Ordinal);
                Assert.Contains("cursor=", wire[1], StringComparison.Ordinal);

                // Senza dimensione di pagina: una sola richiesta, senza limit.
                recorder.Clear();
                var withDefault = new List<Document>();
                await foreach (var document in store.EnumerateAsync(query, null, Ct))
                {
                    withDefault.Add(document);
                }

                Assert.Equal(new[] { j[2].Id.Number, j[1].Id.Number }, InOrder(withDefault));
                var single = Assert.Single(recorder.Requests);
                Assert.DoesNotContain("limit=", single, StringComparison.Ordinal);

                // Gli stessi campi con i filtri facoltativi svuotati: restano solo proprietario e nome.
                form["tag"] = string.Empty;
                form["folder"] = " ";
                form["from"] = string.Empty;
                form["to"] = string.Empty;
                await AssertSelectsAsync(store, FromForm(form), j[0], j[1], j[2], j[3], j[4], j[6], j[7]);

                // Il contatto scelto da un elenco a tendina arriva come testo: "id del mittente" del documento seminato.
                var contacts = await directory.ListAsync(new ContactQuery { Text = LiveSeed.SearchText }, cancellationToken: Ct);
                var external = Assert.Single(contacts.Items, c => c.Code == LiveSeed.ExternalCode);
                var bySender = FromForm(new Dictionary<string, string>
                {
                    ["owner"] = LiveSeed.DocumentOwner,
                    ["sender"] = external.Id.Value,
                });
                var seeded = Assert.Single((await store.ListAsync(new DocumentQuery { Owner = LiveSeed.DocumentOwner }, cancellationToken: Ct)).Items);
                await AssertSelectsAsync(store, bySender, seeded);
                await AssertSelectsAsync(
                    store,
                    FromForm(new Dictionary<string, string> { ["owner"] = owner, ["sender"] = external.Id.Value }));
            });
        }
        finally
        {
            await host.StopAsync(CancellationToken.None);
        }
    }

    // Un campo di testo del modulo diventa un filtro solo se non e' vuoto (come fa un gestionale); le date e gli id arrivano come testo.
    private static DocumentQuery FromForm(Dictionary<string, string> form)
    {
        string? Field(string name) =>
            form.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value) ? value : null;

        DateTimeOffset? Instant(string name) =>
            Field(name) is { } text
                ? DateTimeOffset.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal)
                : null;

        return new DocumentQuery
        {
            Owner = Field("owner"),
            Tag = Field("tag"),
            FileName = Field("name"),
            FolderId = Field("folder") is { } folder ? new FolderCode(folder.Trim()) : null,
            CreatedFrom = Instant("from"),
            CreatedBefore = Instant("to"),
            SenderId = Field("sender") is { } sender ? ContactId.Parse(sender.Trim()) : null,
        };
    }

    // Il banco di prova dei filtri "contiene": otto documenti con un valore diverso (Variants) nel campo cercato e un'esca che ha il
    // token in tutti gli altri campi (proprietario, etichetta, nome file, mittente, destinatario, metadati). Il documento i ha
    // Variants[i]; i fallimenti puntano alla riga dell'asserzione che non torna.
    private static async Task ContainsScenarioAsync(TextField field)
    {
        using var client = Client();
        await LiveCleanup.WithCleanupAsync(client, async cleanup =>
        {
            var k = Unique(field switch
            {
                TextField.FileName => "fn",
                TextField.Sender => "snd",
                TextField.Recipient => "rcp",
                _ => "mq",
            });
            var docs = new Document[Variants.Length];
            for (var i = 0; i < docs.Length; i++)
            {
                var value = k + Variants[i];
                docs[i] = await PutAsync(
                    client,
                    cleanup,
                    field == TextField.FileName ? value + ".pdf" : "plain-" + i.ToString(CultureInfo.InvariantCulture) + ".pdf",
                    sender: field == TextField.Sender ? value : null,
                    recipient: field == TextField.Recipient ? value : null,
                    metadata: field == TextField.MetadataText ? "{\"v\":" + JsonSerializer.Serialize(value, RelaxedJson) + "}" : null);
            }

            var decoyValue = k + "-alfa-decoy";
            await PutAsync(
                client,
                cleanup,
                field == TextField.FileName ? "decoy.pdf" : decoyValue + ".pdf",
                owner: decoyValue,
                tag: decoyValue,
                sender: field == TextField.Sender ? null : decoyValue,
                recipient: field == TextField.Recipient ? null : decoyValue,
                metadata: field == TextField.MetadataText ? null : "{\"v\":\"" + decoyValue + "\"}");

            DocumentQuery Query(string text) =>
                field switch
                {
                    TextField.FileName => new DocumentQuery { FileName = text },
                    TextField.Sender => new DocumentQuery { Sender = text },
                    TextField.Recipient => new DocumentQuery { Recipient = text },
                    _ => new DocumentQuery { MetadataText = text },
                };

            // Il token da solo: tutti e otto, mai l'esca; poi maiuscole e minuscole.
            await AssertSelectsAsync(client.Documents, Query(k), docs);
            var listedAll = (await client.Documents.ListAsync(Query(k), cancellationToken: Ct)).Items;
            for (var i = 0; i < docs.Length; i++)
            {
                var value = k + Variants[i];
                var got = listedAll.Single(d => d.Id == docs[i].Id);
                Assert.Equal(field == TextField.FileName ? value + ".pdf" : "plain-" + i.ToString(CultureInfo.InvariantCulture) + ".pdf", got.OriginalFilename);
                AssertListed(got, sender: field == TextField.Sender ? value : null, recipient: field == TextField.Recipient ? value : null);
                if (field == TextField.MetadataText)
                {
                    Assert.Equal(value, got.Metadata.GetProperty("v").GetString());
                }
            }

            await AssertSelectsAsync(client.Documents, Query(k.ToUpperInvariant()), docs);
            await AssertSelectsAsync(client.Documents, Query(k + "-alfa"), docs[0], docs[1]);
            await AssertSelectsAsync(client.Documents, Query(k.ToUpperInvariant() + "-ALFA"), docs[0], docs[1]);
            await AssertSelectsAsync(client.Documents, Query(k + "-a"), docs[0], docs[1], docs[6], docs[7]);

            // "Contiene": il testo puo' stare nel mezzo del valore (qui la coda del token e l'inizio della variante).
            await AssertSelectsAsync(client.Documents, Query(k.Remove(0, 3) + "-alfa-b"), docs[1]);

            // Nessuna corrispondenza.
            await AssertSelectsAsync(client.Documents, Query(k + "-zzz"));
            await AssertSelectsAsync(client.Documents, Query(k + "-alfa-beta-gamma"));

            // L'accento distingue, la maiuscola no.
            await AssertSelectsAsync(client.Documents, Query(k + "-citta"), docs[2]);
            await AssertSelectsAsync(client.Documents, Query(k + "-CITTA"), docs[2]);
            await AssertSelectsAsync(client.Documents, Query(k + "-citt" + Grave), docs[3]);
            await AssertSelectsAsync(client.Documents, Query(k + "-CITT" + GraveUpper), docs[3]);

            // Percento, sottolineatura e parentesi quadra valgono se stessi: nessun carattere jolly.
            await AssertSelectsAsync(client.Documents, Query(k + "-50%"), docs[4]);
            await AssertSelectsAsync(client.Documents, Query(k + "-50"), docs[4], docs[5]);
            await AssertSelectsAsync(client.Documents, Query(k + "-a_b"), docs[6]);
            await AssertSelectsAsync(client.Documents, Query(k + "-a_b[c]"), docs[6]);
            await AssertSelectsAsync(client.Documents, Query(k + "-aXb"), docs[7]);
            await AssertSelectsAsync(client.Documents, Query(k + "-aXb[c]"));
            await AssertSelectsAsync(client.Documents, Query(k + "-5_"));
            await AssertSelectsAsync(client.Documents, Query(k + "-%"));

            // Il testo cercato arriva intero anche con spazi ai bordi (il server li toglie).
            await AssertSelectsAsync(client.Documents, Query("  " + k + "-citta  "), docs[2]);
        });
    }

    private static async Task<Document> PutAsync(
        IFilemasterClient client,
        LiveCleanup cleanup,
        string fileName,
        string? owner = null,
        string? tag = null,
        string? sender = null,
        string? recipient = null,
        string? metadata = null,
        FolderCode? folder = null)
    {
        using var json = metadata is null ? null : JsonDocument.Parse(metadata);
        var uploaded = await UploadAsync(client, cleanup, UniqueBytes(256), fileName, request =>
        {
            request.Owner = owner;
            request.Tag = tag;
            request.Sender = sender;
            request.Recipient = recipient;
            request.FolderId = folder;
            if (json is not null)
            {
                request.Metadata = json.RootElement;
            }
        });
        return uploaded.Document;
    }

    // L'elenco intero in una pagina (senza cursore: il risultato e' completo) con esattamente i documenti attesi, ciascuno con gli stessi
    // campi del caricamento.
    private static async Task AssertSelectsAsync(IDocumentStore store, DocumentQuery query, params Document[] expected)
    {
        var page = await store.ListAsync(query, new PageRequest(limit: PageRequest.MaxLimit), Ct);
        Assert.Null(page.NextCursor);
        Assert.Equal(Numbers(expected), Numbers(page.Items));
        foreach (var want in expected)
        {
            var got = page.Items.Single(d => d.Id == want.Id);
            Assert.Equal(want.OriginalFilename, got.OriginalFilename);
            Assert.Equal(want.FolderId, got.FolderId);
            Assert.Equal(want.MimeType, got.MimeType);
            Assert.Equal(want.Sha256, got.Sha256);
            Assert.Equal(want.SizeBytes, got.SizeBytes);
            Assert.Equal(want.Owner, got.Owner);
            Assert.Equal(want.Tag, got.Tag);
            Assert.Equal(want.Sender, got.Sender);
            Assert.Equal(want.Recipient, got.Recipient);
            Assert.Equal(want.CreatedAt, got.CreatedAt);
            Assert.Equal(want.Metadata.GetRawText(), got.Metadata.GetRawText());
        }
    }

    // I campi di testo di un documento letto dall'elenco sono quelli passati al caricamento (null = assente), non quelli che il lettore
    // dell'elenco ha confuso fra loro.
    private static void AssertListed(
        Document got,
        string? owner = null,
        string? tag = null,
        string? sender = null,
        string? recipient = null,
        FolderCode? folder = null)
    {
        Assert.Equal(owner, got.Owner);
        Assert.Equal(tag, got.Tag);
        Assert.Equal(sender, got.Sender);
        Assert.Equal(recipient, got.Recipient);
        Assert.Equal(folder, got.FolderId);
    }

    // I numeri degli id nell'ordine in cui arrivano (l'ordine fa parte dell'oracolo delle liste ordinate).
    private static long[] InOrder(IEnumerable<Document> documents) => documents.Select(d => d.Id.Number).ToArray();

    // Scorre tutte le pagine di dimensione `limit` col cursore e restituisce i numeri degli id di ciascuna pagina; si ferma sul primo
    // cursore nullo e fallisce se il server non smette di dare cursori.
    private static async Task<long[][]> WalkAsync(IDocumentStore store, DocumentQuery query, int limit)
    {
        var pages = new List<long[]>();
        string? cursor = null;
        do
        {
            var page = await store.ListAsync(query, new PageRequest(cursor, limit), Ct);
            pages.Add(InOrder(page.Items));
            cursor = page.NextCursor;
            Assert.True(pages.Count <= 50, "la paginazione non finisce");
        }
        while (cursor is not null);

        return pages.ToArray();
    }

    // Registra metodo e percorso con query di ogni richiesta che passa per il client della DI (dopo la costruzione dell'URL, prima della rete).
    private sealed class RecordingHandler : DelegatingHandler
    {
        private readonly List<string> _requests = new();

        internal IReadOnlyList<string> Requests
        {
            get
            {
                lock (_requests)
                {
                    return _requests.ToArray();
                }
            }
        }

        internal void Clear()
        {
            lock (_requests)
            {
                _requests.Clear();
            }
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (_requests)
            {
                _requests.Add(request.RequestUri!.PathAndQuery);
            }

            return base.SendAsync(request, cancellationToken);
        }
    }
}
