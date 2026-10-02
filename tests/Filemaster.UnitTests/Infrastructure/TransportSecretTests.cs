using Filemaster.Domain;
using Filemaster.Infrastructure;
using Filemaster.UnitTests.Application;

namespace Filemaster.UnitTests.Infrastructure;

/// <summary>
/// La chiave API non esce dal trasporto: non compare in nessuna eccezione (nemmeno nelle cause interne, che <c>ToString()</c>
/// include), in nessun messaggio di log (testo, valori strutturati ed eccezione) ne' in <c>ToString()</c> delle opzioni. Si prova su
/// una chiave inventata e distintiva, attraverso ogni famiglia di esito.
/// </summary>
public sealed class TransportSecretTests
{
    private static async Task<Exception> Capture(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception exception)
        {
            return exception;
        }

        throw new InvalidOperationException("la chiamata non e' fallita");
    }

    // Un trasporto per scenario: ogni scenario lascia un'eccezione e il suo log, e tutto dev'essere pulito.
    private static async Task AssertCleanAsync(Action<TransportRig> script, Func<TransportRig, Task> act, int? maxBuffered = null, bool expectLog = false)
    {
        using var rig = new TransportRig(o => o.Retry.MaxAttempts = 2, maxBufferedBytes: maxBuffered);
        script(rig);

        var exception = await Capture(() => act(rig));

        Assert.DoesNotContain(TransportRig.Key, exception.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(TransportRig.Key, exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(TransportRig.Key, rig.Log.Everything, StringComparison.Ordinal);
        if (expectLog)
        {
            Assert.NotEmpty(rig.Log.Records); // il log del ritentativo c'e': il controllo non passa a vuoto
        }
    }

    [Fact]
    public async Task The_key_is_in_no_exception_and_no_log_line_whatever_the_outcome()
    {
        var get = TransportRig.Get("documents/doc_X");
        var entered = Waiting.NewSignal();

        await AssertCleanAsync(rig => rig.Handler.Then(Reply.Problem(401, "unauthorized", "chiave non valida")), rig => rig.Transport.SendBufferedAsync(get, default));
        await AssertCleanAsync(rig => rig.Handler.Then(Reply.Text(500, "boom", "text/plain")), rig => rig.Transport.SendBufferedAsync(get, default));
        await AssertCleanAsync(
            rig => rig.Handler.ThenFail(new HttpRequestException("rete")).ThenFail(new IOException("rete 2")),
            rig => rig.Transport.SendBufferedAsync(get, default),
            expectLog: true);
        await AssertCleanAsync(
            rig => rig.Handler.Then(Reply.Text(503)).Then(Reply.Text(503)),
            rig => rig.Transport.SendBufferedAsync(get, default),
            expectLog: true);
        await AssertCleanAsync(rig => rig.Handler.Then(Reply.Bytes(200, new byte[11], declaredLength: null)), rig => rig.Transport.SendBufferedAsync(get, default), maxBuffered: 10);
        await AssertCleanAsync(rig => rig.Handler.Then(Reply.Problem(409, "content-unavailable")), rig => rig.Transport.SendDownloadAsync(get, default));
        await AssertCleanAsync(rig => rig.Handler.ThenFail(new HttpRequestException("rete")), rig => rig.Transport.SendUploadAsync(TransportRig.Post("documents"), default));
        await AssertCleanAsync(
            rig => rig.Handler.Then(Waiting.Hang(entered)),
            async rig =>
            {
                var call = rig.Transport.SendBufferedAsync(get, default);
                await entered.Task;
                rig.Time.Advance(rig.Options.RequestTimeout);
                await call;
            });
    }

    [Fact]
    public async Task The_key_is_not_in_the_exceptions_of_a_download_that_breaks()
    {
        using var rig = new TransportRig();
        var dropping = new ScriptedStream(StreamReading.Pattern(100), maxChunk: 10);
        dropping.FailAtRead[2] = new IOException("rete");
        rig.Handler.Then(Reply.Streamed(200, dropping, declaredLength: 100)).Then(Reply.Streamed(200, new ScriptedStream(new byte[5]), declaredLength: 50));
        var truncated = new List<Exception>();

        var first = await rig.Transport.SendDownloadAsync(TransportRig.Get("documents/doc_X/content"), default);
        truncated.Add(await Capture(() => StreamReading.ReadToEndAsync(first.Content, ReadApi.Async, 64)));
        truncated.Add(await Capture(() => StreamReading.ReadToEndAsync(first.Content, ReadApi.Async, 64))); // verdetto sticky
        first.Content.Dispose();
        var second = await rig.Transport.SendDownloadAsync(TransportRig.Get("documents/doc_X/content"), default);
        truncated.Add(await Capture(() => StreamReading.ReadToEndAsync(second.Content, ReadApi.Async, 64)));
        second.Content.Dispose();

        Assert.All(truncated, exception => Assert.IsType<ContentIntegrityException>(exception));
        Assert.All(truncated, exception => Assert.DoesNotContain(TransportRig.Key, exception.ToString(), StringComparison.Ordinal));
        Assert.DoesNotContain(TransportRig.Key, rig.Log.Everything, StringComparison.Ordinal);
    }

    [Fact]
    public void The_key_is_not_in_the_options_text_and_a_rejected_key_is_not_echoed()
    {
        var options = new FilemasterOptions { BaseAddress = new Uri("https://filemaster.example.test/"), ApiKey = TransportRig.Key };
        Assert.DoesNotContain(TransportRig.Key, options.ToString(), StringComparison.Ordinal);

        options.ApiKey = TransportRig.Key + " with space";
        var exception = Assert.Throws<ArgumentException>(options.Validate);
        Assert.DoesNotContain(TransportRig.Key, exception.ToString(), StringComparison.Ordinal);
    }
}
