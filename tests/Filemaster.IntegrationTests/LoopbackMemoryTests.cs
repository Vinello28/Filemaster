using System.Diagnostics;
using System.Globalization;
using Filemaster.Application;
using Filemaster.IntegrationTests.Loopback;
using static Filemaster.IntegrationTests.Loopback.LoopbackSupport;

namespace Filemaster.IntegrationTests;

/// <summary>
/// Upload da 200 MB generati al volo verso il server di loopback, che conta i byte senza tenerli: la memoria gestita non deve crescere
/// come il corpo. Fuori dalla suite generale (<c>--filter-not-trait "Category=Memory"</c>); la CI lo lancia da solo su net48, dove
/// <c>HttpClientHandler</c> potrebbe bufferizzare il corpo della richiesta.
/// </summary>
/// <remarks>
/// <b>Soglia: 64 MB di crescita del picco di memoria gestita</b> rispetto alla base misurata dopo un caricamento di riscaldamento e un
/// GC completo. Un client in streaming tiene un buffer di copia (80 KiB) e quelli dei socket: pochi MB. Un client che bufferizza
/// tiene l'intero corpo (200 MB, o 256 MB per un <c>MemoryStream</c> che raddoppia): almeno tre volte la soglia. Il margine assorbe la
/// generazione 0 non ancora raccolta, che <see cref="GC.GetTotalMemory(bool)"/> senza raccolta conta. Il working set del processo si
/// misura e si scrive nell'output (contiene anche il runtime e i buffer nativi: non e' una soglia affidabile fra sistemi).
/// </remarks>
[Trait("Category", "Memory")]
public sealed class LoopbackMemoryTests
{
    private const long Size = 200L * 1024 * 1024;
    private const long MaxManagedGrowth = 64L * 1024 * 1024;

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_200_MB_upload_is_streamed_without_buffering_the_body_in_memory(bool seekable)
    {
        using var server = LoopbackServer.Start(
            async exchange =>
            {
                await exchange.ReadBodyAsync().ConfigureAwait(false);
                await exchange.RespondAsync(201, UploadJson).ConfigureAwait(false);
            },
            keepBodyBytes: 0);
        using var client = Client(server, o => o.TransferTimeout = TimeSpan.FromMinutes(5));
        var token = TestContext.Current.CancellationToken;

        // Riscaldamento: JIT, connessione e buffer del gestore esistono gia' quando si prende la base.
        using (var warmUp = new MemoryStream(Bytes(1000)))
        {
            await Within(client.Documents.UploadAsync(new UploadDocumentRequest(warmUp, "riscaldamento.bin"), token));
        }

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var baseManaged = GC.GetTotalMemory(forceFullCollection: true);
        var baseWorkingSet = WorkingSet();

        using var source = new GeneratedStream(Size, seekable);
        UploadResult result;
        using (var sampler = new MemorySampler())
        {
            var upload = client.Documents.UploadAsync(new UploadDocumentRequest(source, "grande.bin"), token);
            if (await Task.WhenAny(upload, Task.Delay(TimeSpan.FromMinutes(3), token)) != upload)
            {
                throw new TimeoutException("L'upload da 200 MB non e' finito entro 3 minuti.");
            }

            result = await upload;
            sampler.Stop();

            var managedGrowth = sampler.PeakManaged - baseManaged;
            var workingSetGrowth = sampler.PeakWorkingSet - baseWorkingSet;
            TestContext.Current.TestOutputHelper?.WriteLine(string.Format(
                CultureInfo.InvariantCulture,
                "MEMORIA seekable={0}: gestita base {1:F1} MB, picco {2:F1} MB (+{3:F1} MB); working set base {4:F1} MB, picco {5:F1} MB (+{6:F1} MB); campioni {7}",
                seekable,
                Mb(baseManaged),
                Mb(sampler.PeakManaged),
                Mb(managedGrowth),
                Mb(baseWorkingSet),
                Mb(sampler.PeakWorkingSet),
                Mb(workingSetGrowth),
                sampler.Samples));
            Assert.True(sampler.Samples >= 5, "Troppo pochi campioni per una misura del picco.");
            Assert.True(managedGrowth < MaxManagedGrowth, "Il picco di memoria gestita e' cresciuto di " + Mb(managedGrowth).ToString("F1", CultureInfo.InvariantCulture) + " MB: il corpo e' stato bufferizzato.");
        }

        Assert.NotNull(result.Document);
        var request = server.Requests[server.Requests.Count - 1];
        Assert.True(request.BodyComplete);
        Assert.Equal(Size, source.Consumed);
        Assert.InRange(request.BodyLength, Size, Size + 4096);
        Assert.Null(request.Body);
        if (seekable)
        {
            Assert.Equal(request.BodyLength, request.ContentLength);
        }
        else
        {
            Assert.True(request.IsChunked);
        }
    }

    private static double Mb(long bytes) => bytes / (1024.0 * 1024.0);

    private static long WorkingSet()
    {
        using var process = Process.GetCurrentProcess();
        return process.WorkingSet64;
    }

    /// <summary>Campiona memoria gestita (senza raccolta) e working set su un thread a parte, tenendo il massimo.</summary>
    private sealed class MemorySampler : IDisposable
    {
        private readonly Thread _thread;
        private volatile bool _stop;
        private long _peakManaged;
        private long _peakWorkingSet;
        private int _samples;

        internal MemorySampler()
        {
            _thread = new Thread(Run) { IsBackground = true, Name = "campionatore di memoria" };
            _thread.Start();
        }

        internal long PeakManaged => Interlocked.Read(ref _peakManaged);

        internal long PeakWorkingSet => Interlocked.Read(ref _peakWorkingSet);

        internal int Samples => Volatile.Read(ref _samples);

        internal void Stop()
        {
            _stop = true;
            _thread.Join(TimeSpan.FromSeconds(5));
        }

        public void Dispose() => Stop();

        private void Run()
        {
            using var process = Process.GetCurrentProcess();
            while (!_stop)
            {
                var managed = GC.GetTotalMemory(forceFullCollection: false);
                process.Refresh();
                var workingSet = process.WorkingSet64;
                if (managed > _peakManaged)
                {
                    Interlocked.Exchange(ref _peakManaged, managed);
                }

                if (workingSet > _peakWorkingSet)
                {
                    Interlocked.Exchange(ref _peakWorkingSet, workingSet);
                }

                Interlocked.Increment(ref _samples);
                Thread.Sleep(5); // cadenza del campionamento, non un'attesa di un evento
            }
        }
    }
}
