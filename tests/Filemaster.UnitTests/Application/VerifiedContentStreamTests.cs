using System.Text;
using Filemaster.Application;
using Filemaster.Domain;

// I test girano anche su net48, che non ha gli overload con Memory/Span degli stream ne' AsSpan: si esercitano di proposito le forme
// con array, che sono quelle dell'asset netstandard2.0, e si spengono gli avvisi che spingono verso API assenti li' (lezione 23).
#pragma warning disable CA1835, CA1845

namespace Filemaster.UnitTests.Application;

/// <summary>
/// <see cref="VerifiedContentStream"/>: lo SHA-256 si calcola mentre si legge e il verdetto c'e' solo all'EOF; hash o lunghezza
/// sbagliati lanciano <see cref="ContentIntegrityException"/> a quel punto e a ogni lettura dopo; smaltire in anticipo non
/// da' alcun verdetto. Gli hash attesi sono costanti calcolate nel terminale (i comandi sono nei commenti), non dal codice sotto test.
/// </summary>
public sealed class VerifiedContentStreamTests
{
    // printf '' | shasum -a 256
    private const string EmptyHash = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";

    // printf 'abc' | shasum -a 256   (stesso valore con: printf 'abc' | openssl dgst -sha256)
    private const string AbcHash = "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad";

    // printf 'ab' | shasum -a 256
    private const string AbHash = "fb8e20fc2e4c3f248c60c39bd652f3c1347298bb977b8b4d5903b85055620603";

    // printf 'abcd' | shasum -a 256
    private const string AbcdHash = "88d4266fd4e6338d13b845fcf289579d209c897823b9217da3e161936f031589";

    // 13 byte: "perch", C3 A9 (e acuta in UTF-8), spazio, 00, FF, FE, 80 (UTF-8 non valido), "A".
    // echo 7065726368c3a92000fffe8041 | xxd -r -p | shasum -a 256   (stesso valore con openssl dgst -sha256)
    private const string NonAsciiHash = "037fcd8dc3268b26315194638b5878e50612a7a6a69ac035bfa5a7b07716d587";

    // 1 MiB: il byte i vale i modulo 251 (periodo primo: nessun pezzo di lettura coincide con il periodo).
    // python3 -c "import sys; sys.stdout.buffer.write(bytes(i % 251 for i in range(1048576)))" | shasum -a 256
    private const string PatternHash = "631b84027d6b9e52b539c4e8373622d23032dfadc64d60af87339c9037e4f769";

    private const int PatternLength = 1048576;

    private static readonly byte[] Abc = Encoding.ASCII.GetBytes("abc");
    private static readonly byte[] NonAscii = { 0x70, 0x65, 0x72, 0x63, 0x68, 0xC3, 0xA9, 0x20, 0x00, 0xFF, 0xFE, 0x80, 0x41 };

    private static byte[] Pattern()
    {
        var data = new byte[PatternLength];
        for (var i = 0; i < data.Length; i++)
        {
            data[i] = (byte)(i % 251);
        }

        return data;
    }

    private static (byte[] Data, string Hash) Case(string name) =>
        name switch
        {
            "empty" => (Array.Empty<byte>(), EmptyHash),
            "abc" => (Abc, AbcHash),
            "non-ascii" => (NonAscii, NonAsciiHash),
            "1MiB" => (Pattern(), PatternHash),
            _ => throw new ArgumentOutOfRangeException(nameof(name)),
        };

    // Legge fino al primo 0 a pezzi di "chunk" byte; restituisce i byte arrivati e quante letture ha fatto (l'ultima e' lo 0).
    private static byte[] ReadAll(Stream stream, int chunk)
    {
        using var all = new MemoryStream();
        var buffer = new byte[chunk];
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            all.Write(buffer, 0, read);
        }

        return all.ToArray();
    }

    private static async Task<byte[]> ReadAllAsync(Stream stream, int chunk)
    {
        using var all = new MemoryStream();
        var buffer = new byte[chunk];
        int read;
        while ((read = await stream.ReadAsync(buffer, 0, buffer.Length, CancellationToken.None)) > 0)
        {
            all.Write(buffer, 0, read);
        }

        return all.ToArray();
    }

    // Un hash con l'ultima cifra cambiata: stessa lunghezza, valore diverso.
    private static string WrongHash(string hash)
    {
        var digits = hash.ToCharArray();
        digits[digits.Length - 1] = digits[digits.Length - 1] == '0' ? '1' : '0';
        return new string(digits);
    }

    // --- contenuto valido ---------------------------------------------------------------------------------------

    [Theory]
    [InlineData("empty")]
    [InlineData("abc")]
    [InlineData("non-ascii")]
    [InlineData("1MiB")]
    public void Valid_content_read_with_Read_arrives_whole_and_the_end_is_clean(string name)
    {
        var (data, hash) = Case(name);
        using var stream = new VerifiedContentStream(new MemoryStream(data), hash);

        var received = ReadAll(stream, 4093);

        Assert.Equal(data, received);
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("abc")]
    [InlineData("non-ascii")]
    [InlineData("1MiB")]
    public async Task Valid_content_read_with_ReadAsync_arrives_whole_and_the_end_is_clean(string name)
    {
        var (data, hash) = Case(name);
        using var stream = new VerifiedContentStream(new MemoryStream(data), hash);

        var received = await ReadAllAsync(stream, 4093);

        Assert.Equal(data, received);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("non-ascii")]
    public void Valid_content_read_one_byte_at_a_time_arrives_whole_and_the_end_is_clean(string name)
    {
        var (data, hash) = Case(name);
        using var stream = new VerifiedContentStream(new MemoryStream(data), hash);

        var received = ReadAll(stream, 1);

        Assert.Equal(data, received);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("non-ascii")]
    public async Task Valid_content_read_one_byte_at_a_time_with_ReadAsync_arrives_whole_and_the_end_is_clean(string name)
    {
        var (data, hash) = Case(name);
        using var stream = new VerifiedContentStream(new MemoryStream(data), hash);

        var received = await ReadAllAsync(stream, 1);

        Assert.Equal(data, received);
    }

    [Fact]
    public void Valid_content_read_with_ReadByte_arrives_whole_and_the_end_is_clean()
    {
        using var stream = new VerifiedContentStream(new MemoryStream(NonAscii), NonAsciiHash);
        var received = new List<byte>();

        int next;
        while ((next = stream.ReadByte()) >= 0)
        {
            received.Add((byte)next);
        }

        Assert.Equal(NonAscii, received.ToArray());
    }

    [Theory]
    [InlineData(81920)]
    [InlineData(4093)]
    [InlineData(1)]
    public async Task Valid_content_copied_with_CopyToAsync_arrives_whole(int bufferSize)
    {
        var data = bufferSize == 1 ? NonAscii : Pattern();
        var hash = bufferSize == 1 ? NonAsciiHash : PatternHash;
        using var stream = new VerifiedContentStream(new MemoryStream(data), hash);
        using var sink = new MemoryStream();

        await stream.CopyToAsync(sink, bufferSize);

        Assert.Equal(data, sink.ToArray());
    }

    [Fact]
    public void Valid_content_copied_with_CopyTo_arrives_whole()
    {
        using var stream = new VerifiedContentStream(new MemoryStream(Pattern()), PatternHash);
        using var sink = new MemoryStream();

        stream.CopyTo(sink, 4093);

        Assert.Equal(Pattern(), sink.ToArray());
    }

    [Fact]
    public void Valid_content_delivered_by_an_inner_stream_in_small_chunks_is_hashed_across_all_the_chunks()
    {
        // Se l'hash coprisse solo il primo pezzo, un contenuto di piu' pezzi risulterebbe sempre alterato (o, peggio, mai).
        using var stream = new VerifiedContentStream(new ScriptedStream(Pattern(), maxChunk: 997), PatternHash, PatternLength);

        var received = ReadAll(stream, 4093);

        Assert.Equal(Pattern(), received);
    }

#if NET
    [Theory]
    [InlineData("abc")]
    [InlineData("1MiB")]
    public void Valid_content_read_with_a_Span_arrives_whole_and_the_end_is_clean(string name)
    {
        var (data, hash) = Case(name);
        using var stream = new VerifiedContentStream(new MemoryStream(data), hash);
        using var all = new MemoryStream();
        var buffer = new byte[4093];

        int read;
        while ((read = stream.Read(new Span<byte>(buffer))) > 0)
        {
            all.Write(buffer, 0, read);
        }

        Assert.Equal(data, all.ToArray());
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("1MiB")]
    public async Task Valid_content_read_with_a_Memory_arrives_whole_and_the_end_is_clean(string name)
    {
        var (data, hash) = Case(name);
        using var stream = new VerifiedContentStream(new MemoryStream(data), hash);
        using var all = new MemoryStream();
        var buffer = new byte[4093];

        int read;
        while ((read = await stream.ReadAsync(new Memory<byte>(buffer))) > 0)
        {
            all.Write(buffer, 0, read);
        }

        Assert.Equal(data, all.ToArray());
    }

    [Fact]
    public void A_Span_read_of_wrong_content_hashes_the_bytes_and_throws_at_the_end()
    {
        using var stream = new VerifiedContentStream(new MemoryStream(Abc), WrongHash(AbcHash));
        var buffer = new byte[16];

        Assert.Equal(3, stream.Read(new Span<byte>(buffer)));
        var exception = Assert.Throws<ContentIntegrityException>(() => stream.Read(new Span<byte>(buffer)));
        Assert.False(exception.IsTruncated);
    }

    [Fact]
    public async Task A_Memory_read_of_wrong_content_hashes_the_bytes_and_throws_at_the_end()
    {
        using var stream = new VerifiedContentStream(new MemoryStream(Abc), WrongHash(AbcHash));
        var buffer = new byte[16];

        Assert.Equal(3, await stream.ReadAsync(new Memory<byte>(buffer)));
        var exception = await Assert.ThrowsAsync<ContentIntegrityException>(async () => Assert.Equal(0, await stream.ReadAsync(new Memory<byte>(buffer))));
        Assert.False(exception.IsTruncated);
    }
#endif

    [Fact]
    public void Expected_hash_in_upper_case_is_accepted()
    {
        using var stream = new VerifiedContentStream(new MemoryStream(NonAscii), NonAsciiHash.ToUpperInvariant());

        Assert.Equal(NonAscii, ReadAll(stream, 5));
    }

    [Fact]
    public void Expected_hash_in_mixed_case_is_accepted()
    {
        var mixed = new StringBuilder();
        for (var i = 0; i < AbcHash.Length; i++)
        {
            mixed.Append(i % 2 == 0 ? char.ToUpperInvariant(AbcHash[i]) : AbcHash[i]);
        }

        using var stream = new VerifiedContentStream(new MemoryStream(Abc), mixed.ToString());

        Assert.Equal(Abc, ReadAll(stream, 5));
    }

    [Fact]
    public void Empty_content_with_the_hash_of_the_empty_string_is_valid_from_the_first_read()
    {
        using var stream = new VerifiedContentStream(new MemoryStream(), EmptyHash, expectedLength: 0);

        Assert.Equal(0, stream.Read(new byte[8], 0, 8));
        Assert.Equal(0, stream.Read(new byte[8], 0, 8));
    }

    [Fact]
    public void Empty_content_with_a_different_hash_throws_at_the_first_read_that_reaches_the_end()
    {
        using var stream = new VerifiedContentStream(new MemoryStream(), AbcHash);

        var exception = Assert.Throws<ContentIntegrityException>(() => stream.Read(new byte[8], 0, 8));

        Assert.False(exception.IsTruncated);
        Assert.Equal(0L, exception.ActualLength);
    }

    // --- hash sbagliato -----------------------------------------------------------------------------------------

    [Theory]
    [InlineData("abc")]
    [InlineData("non-ascii")]
    [InlineData("1MiB")]
    public void Wrong_hash_delivers_every_byte_first_and_throws_only_at_the_end(string name)
    {
        var (data, hash) = Case(name);
        using var stream = new VerifiedContentStream(new MemoryStream(data), WrongHash(hash));
        using var all = new MemoryStream();
        var buffer = new byte[4093];

        // Ogni lettura prima dell'EOF restituisce i suoi byte senza eccezioni: il verdetto non c'e' ancora.
        int read;
        while (all.Length < data.Length && (read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            all.Write(buffer, 0, read);
        }

        Assert.Equal(data, all.ToArray());
        var exception = Assert.Throws<ContentIntegrityException>(() => stream.Read(buffer, 0, buffer.Length));
        Assert.False(exception.IsTruncated);
        Assert.Equal(200, exception.StatusCode);
        Assert.Null(exception.ExpectedLength);
        Assert.Equal((long)data.Length, exception.ActualLength);
    }

    [Fact]
    public async Task Wrong_hash_with_ReadAsync_delivers_every_byte_first_and_throws_only_at_the_end()
    {
        using var stream = new VerifiedContentStream(new MemoryStream(NonAscii), WrongHash(NonAsciiHash));
        var buffer = new byte[64];

        Assert.Equal(NonAscii.Length, await stream.ReadAsync(buffer, 0, buffer.Length));
        var exception = await Assert.ThrowsAsync<ContentIntegrityException>(() => stream.ReadAsync(buffer, 0, buffer.Length));

        Assert.False(exception.IsTruncated);
        Assert.Equal(200, exception.StatusCode);
    }

    [Fact]
    public async Task Wrong_hash_with_CopyToAsync_copies_every_byte_and_then_throws()
    {
        using var stream = new VerifiedContentStream(new MemoryStream(Pattern()), WrongHash(PatternHash));
        using var sink = new MemoryStream();

        await Assert.ThrowsAsync<ContentIntegrityException>(() => stream.CopyToAsync(sink, 4093));

        Assert.Equal(PatternLength, sink.Length);
    }

    [Fact]
    public void Wrong_hash_on_a_single_changed_byte_is_detected()
    {
        // Lo stesso contenuto con un solo byte diverso nel mezzo del megabyte, verificato con l'hash dell'originale.
        var tampered = Pattern();
        tampered[PatternLength / 2] ^= 0x01;
        using var stream = new VerifiedContentStream(new MemoryStream(tampered), PatternHash);

        Assert.Throws<ContentIntegrityException>(() => ReadAll(stream, 4093));
    }

    [Fact]
    public async Task Every_read_after_a_negative_verdict_throws_again_for_every_kind_of_read()
    {
        using var stream = new VerifiedContentStream(new MemoryStream(Abc), WrongHash(AbcHash), expectedLength: 3);
        var buffer = new byte[16];
        Assert.Equal(3, stream.Read(buffer, 0, buffer.Length));
        var first = Assert.Throws<ContentIntegrityException>(() => stream.Read(buffer, 0, buffer.Length));

        // Chi ha inghiottito la prima eccezione non deve mai vedere un EOF pulito, in nessuna forma di lettura.
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var again = Assert.Throws<ContentIntegrityException>(() => stream.Read(buffer, 0, buffer.Length));
            Assert.False(again.IsTruncated);
            Assert.Equal(first.ActualLength, again.ActualLength);
            Assert.Equal(first.ExpectedLength, again.ExpectedLength);
            Assert.Equal(first.StatusCode, again.StatusCode);
        }

        Assert.Throws<ContentIntegrityException>(() => stream.Read(buffer, 0, 0));
        Assert.Throws<ContentIntegrityException>(() => stream.ReadByte());
        Assert.Throws<ContentIntegrityException>(() => stream.CopyTo(Stream.Null));
        await Assert.ThrowsAsync<ContentIntegrityException>(() => stream.ReadAsync(buffer, 0, buffer.Length));
        await Assert.ThrowsAsync<ContentIntegrityException>(() => stream.CopyToAsync(Stream.Null));
    }

    [Fact]
    public async Task Every_read_after_a_negative_verdict_throws_again_when_the_verdict_came_from_ReadAsync()
    {
        using var stream = new VerifiedContentStream(new MemoryStream(Abc), WrongHash(AbcHash));
        var buffer = new byte[16];
        Assert.Equal(3, await stream.ReadAsync(buffer, 0, buffer.Length));
        await Assert.ThrowsAsync<ContentIntegrityException>(() => stream.ReadAsync(buffer, 0, buffer.Length));

        await Assert.ThrowsAsync<ContentIntegrityException>(() => stream.ReadAsync(buffer, 0, buffer.Length));
        Assert.Throws<ContentIntegrityException>(() => stream.Read(buffer, 0, buffer.Length));
    }

    [Fact]
    public void The_repeated_exception_keeps_the_first_one_as_its_cause()
    {
        using var stream = new VerifiedContentStream(new MemoryStream(Abc), WrongHash(AbcHash));
        var buffer = new byte[16];
        Assert.Equal(3, stream.Read(buffer, 0, buffer.Length));
        var first = Assert.Throws<ContentIntegrityException>(() => stream.Read(buffer, 0, buffer.Length));

        var again = Assert.Throws<ContentIntegrityException>(() => stream.Read(buffer, 0, buffer.Length));

        Assert.Same(first, again.InnerException);
        Assert.Equal(first.Message, again.Message);
    }

    // --- lunghezza attesa ---------------------------------------------------------------------------------------

    [Fact]
    public void Content_with_the_right_hash_and_the_right_length_is_valid()
    {
        using var stream = new VerifiedContentStream(new MemoryStream(NonAscii), NonAsciiHash, NonAscii.Length);

        Assert.Equal(NonAscii, ReadAll(stream, 4));
    }

    [Fact]
    public void Content_shorter_than_the_expected_length_throws_with_IsTruncated_true()
    {
        // Mancano byte (e quindi anche l'hash non corrisponde): l'hash atteso e' quello dell'intero contenuto "abc".
        using var stream = new VerifiedContentStream(new MemoryStream(Encoding.ASCII.GetBytes("ab")), AbcHash, expectedLength: 3);
        var buffer = new byte[16];
        Assert.Equal(2, stream.Read(buffer, 0, buffer.Length));

        var exception = Assert.Throws<ContentIntegrityException>(() => stream.Read(buffer, 0, buffer.Length));

        Assert.True(exception.IsTruncated);
        Assert.Equal(200, exception.StatusCode);
        Assert.Equal(3L, exception.ExpectedLength);
        Assert.Equal(2L, exception.ActualLength);
    }

    [Fact]
    public void Shorter_content_is_a_verdict_even_when_the_hash_happens_to_match()
    {
        // La lunghezza dichiarata e' una condizione a parte: 3 byte con l'hash giusto ma 5 attesi sono comunque un errore.
        using var stream = new VerifiedContentStream(new MemoryStream(Abc), AbcHash, expectedLength: 5);

        var exception = Assert.Throws<ContentIntegrityException>(() => ReadAll(stream, 16));

        Assert.True(exception.IsTruncated);
        Assert.Equal(5L, exception.ExpectedLength);
        Assert.Equal(3L, exception.ActualLength);
    }

    [Fact]
    public void Content_longer_than_the_expected_length_throws_with_IsTruncated_false()
    {
        using var stream = new VerifiedContentStream(new MemoryStream(Encoding.ASCII.GetBytes("abcd")), AbcHash, expectedLength: 3);
        var buffer = new byte[16];
        Assert.Equal(4, stream.Read(buffer, 0, buffer.Length));

        var exception = Assert.Throws<ContentIntegrityException>(() => stream.Read(buffer, 0, buffer.Length));

        Assert.False(exception.IsTruncated);
        Assert.Equal(3L, exception.ExpectedLength);
        Assert.Equal(4L, exception.ActualLength);
    }

    [Fact]
    public void Longer_content_is_a_verdict_even_when_the_hash_happens_to_match()
    {
        using var stream = new VerifiedContentStream(new MemoryStream(Encoding.ASCII.GetBytes("abcd")), AbcdHash, expectedLength: 3);

        var exception = Assert.Throws<ContentIntegrityException>(() => ReadAll(stream, 16));

        Assert.False(exception.IsTruncated);
        Assert.Equal(3L, exception.ExpectedLength);
        Assert.Equal(4L, exception.ActualLength);
        Assert.Contains("4", exception.Message);
        Assert.Contains("3", exception.Message);
    }

    [Fact]
    public void A_hash_mismatch_with_the_right_length_is_not_a_truncation()
    {
        using var stream = new VerifiedContentStream(new MemoryStream(Abc), WrongHash(AbcHash), expectedLength: 3);

        var exception = Assert.Throws<ContentIntegrityException>(() => ReadAll(stream, 16));

        Assert.False(exception.IsTruncated);
        Assert.Equal(3L, exception.ExpectedLength);
        Assert.Equal(3L, exception.ActualLength);
    }

    // --- l'EOF e il verdetto ------------------------------------------------------------------------------------

    [Fact]
    public void A_read_of_zero_bytes_returns_zero_and_never_concludes_anything()
    {
        var inner = new ScriptedStream(Abc);
        using var stream = new VerifiedContentStream(inner, WrongHash(AbcHash));
        var buffer = new byte[16];

        // All'inizio, a meta' e dopo l'ultimo byte (ma prima della lettura che vede l'EOF): niente verdetto.
        Assert.Equal(0, stream.Read(buffer, 0, 0));
        Assert.Equal(0, inner.ReadCalls);
        Assert.Equal(2, stream.Read(buffer, 0, 2));
        Assert.Equal(0, stream.Read(buffer, 0, 0));
        Assert.Equal(1, stream.Read(buffer, 0, 2));
        Assert.Equal(0, stream.Read(buffer, 0, 0));
        Assert.Equal(2, inner.ReadCalls);

        // Solo la lettura con count maggiore di zero che trova l'EOF da' il verdetto.
        Assert.Throws<ContentIntegrityException>(() => stream.Read(buffer, 0, 1));
    }

    [Fact]
    public async Task An_asynchronous_read_of_zero_bytes_returns_zero_and_never_concludes_anything()
    {
        var inner = new ScriptedStream(Abc);
        using var stream = new VerifiedContentStream(inner, WrongHash(AbcHash));
        var buffer = new byte[16];

        Assert.Equal(0, await stream.ReadAsync(buffer, 0, 0));
        Assert.Equal(3, await stream.ReadAsync(buffer, 0, 16));
        Assert.Equal(0, await stream.ReadAsync(buffer, 0, 0));
        Assert.Equal(1, inner.ReadCalls);
        await Assert.ThrowsAsync<ContentIntegrityException>(() => stream.ReadAsync(buffer, 0, 1));
    }

    [Fact]
    public async Task After_a_positive_verdict_every_read_returns_zero_without_touching_the_inner_stream_again()
    {
        var inner = new ScriptedStream(Abc);
        using var stream = new VerifiedContentStream(inner, AbcHash, expectedLength: 3);
        var buffer = new byte[16];
        Assert.Equal(3, stream.Read(buffer, 0, buffer.Length));
        Assert.Equal(0, stream.Read(buffer, 0, buffer.Length)); // il verdetto
        var callsAtVerdict = inner.ReadCalls;

        // Un verdetto positivo non si ripete e non si rovescia (l'hash non e' piu' quello del vuoto o di altro).
        for (var attempt = 0; attempt < 3; attempt++)
        {
            Assert.Equal(0, stream.Read(buffer, 0, buffer.Length));
            Assert.Equal(0, await stream.ReadAsync(buffer, 0, buffer.Length));
            Assert.Equal(-1, stream.ReadByte());
        }

        Assert.Equal(callsAtVerdict, inner.ReadCalls);
    }

    // --- smaltimento --------------------------------------------------------------------------------------------

    [Fact]
    public void Disposing_early_gives_no_verdict_and_no_exception_even_with_a_wrong_hash()
    {
        var inner = new ScriptedStream(NonAscii);
        var stream = new VerifiedContentStream(inner, WrongHash(NonAsciiHash), expectedLength: 999);
        var buffer = new byte[4];
        Assert.Equal(4, stream.Read(buffer, 0, buffer.Length));

        stream.Dispose(); // niente verdetto: chi non legge fino in fondo non ha garanzie, ma non riceve neanche un errore

        Assert.Equal(1, inner.DisposeCount);
    }

    [Fact]
    public void Disposing_before_any_read_gives_no_exception()
    {
        var inner = new ScriptedStream(NonAscii);
        var stream = new VerifiedContentStream(inner, WrongHash(NonAsciiHash));

        stream.Dispose();

        Assert.Equal(1, inner.DisposeCount);
    }

    [Fact]
    public void Disposing_closes_the_inner_stream_by_default_and_leaves_it_open_with_leaveOpen()
    {
        var closed = new ScriptedStream(Abc);
        var open = new ScriptedStream(Abc);

        new VerifiedContentStream(closed, AbcHash).Dispose();
        new VerifiedContentStream(open, AbcHash, leaveOpen: true).Dispose();

        Assert.Equal(1, closed.DisposeCount);
        Assert.Equal(0, open.DisposeCount);
        Assert.True(open.CanRead);
    }

    [Fact]
    public void Disposing_after_a_negative_verdict_does_not_throw()
    {
        var inner = new ScriptedStream(Abc);
        var stream = new VerifiedContentStream(inner, WrongHash(AbcHash));
        Assert.Throws<ContentIntegrityException>(() => ReadAll(stream, 16));

        stream.Dispose();

        Assert.Equal(1, inner.DisposeCount);
    }

    [Fact]
    public void Disposing_twice_disposes_the_inner_stream_once()
    {
        var inner = new ScriptedStream(Abc);
        var stream = new VerifiedContentStream(inner, AbcHash);

        stream.Dispose();
        stream.Dispose();
        stream.Close();

        Assert.Equal(1, inner.DisposeCount);
    }

    [Fact]
    public async Task Reading_after_Dispose_throws_ObjectDisposedException_and_CanRead_is_false()
    {
        var stream = new VerifiedContentStream(new ScriptedStream(Abc), AbcHash);
        Assert.True(stream.CanRead);
        stream.Dispose();

        Assert.False(stream.CanRead);
        Assert.Throws<ObjectDisposedException>(() => stream.Read(new byte[4], 0, 4));
        Assert.Throws<ObjectDisposedException>(() => stream.Read(new byte[4], 0, 0));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => stream.ReadAsync(new byte[4], 0, 4));
    }

    [Fact]
    public void Disposing_with_leaveOpen_still_stops_further_reads_without_touching_the_inner_stream()
    {
        var inner = new ScriptedStream(Abc);
        var stream = new VerifiedContentStream(inner, AbcHash, leaveOpen: true);
        stream.Dispose();

        Assert.Throws<ObjectDisposedException>(() => stream.Read(new byte[4], 0, 4));

        Assert.Equal(0, inner.ReadCalls);
    }

    // --- costruttore --------------------------------------------------------------------------------------------

    [Fact]
    public void Constructor_with_a_null_inner_stream_throws_ArgumentNullException()
    {
        var exception = Assert.Throws<ArgumentNullException>(() => new VerifiedContentStream(null!, AbcHash));

        Assert.Equal("inner", exception.ParamName);
    }

    [Fact]
    public void Constructor_with_a_null_hash_throws_ArgumentNullException()
    {
        var exception = Assert.Throws<ArgumentNullException>(() => new VerifiedContentStream(new MemoryStream(), null!));

        Assert.Equal("expectedSha256", exception.ParamName);
    }

    [Fact]
    public void Constructor_with_an_inner_stream_that_cannot_be_read_throws_ArgumentException()
    {
        var closed = new MemoryStream();
        closed.Dispose();

        var exception = Assert.Throws<ArgumentException>(() => new VerifiedContentStream(closed, EmptyHash));

        Assert.Equal("inner", exception.ParamName);
    }

    [Fact]
    public void Constructor_with_a_negative_expected_length_throws_ArgumentOutOfRangeException()
    {
        var exception = Assert.Throws<ArgumentOutOfRangeException>(() => new VerifiedContentStream(new MemoryStream(), EmptyHash, expectedLength: -1));

        Assert.Equal("expectedLength", exception.ParamName);
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b85")] // 63 cifre
    [InlineData("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b8550")] // 65 cifre
    [InlineData("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b85g")] // 64 caratteri, l'ultimo non esadecimale
    [InlineData("g3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855")] // il primo non esadecimale
    [InlineData("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b85 ")] // spazio finale
    [InlineData(" e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b785b855")] // spazio iniziale
    [InlineData("0xe3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b8")] // prefisso 0x
    [InlineData("sha256:e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855")]
    public void Constructor_with_a_malformed_hash_throws_ArgumentException_for_expectedSha256(string hash)
    {
        var exception = Assert.Throws<ArgumentException>(() => new VerifiedContentStream(new MemoryStream(), hash));

        Assert.Equal("expectedSha256", exception.ParamName);
    }

    [Fact]
    public void Constructor_with_a_hash_of_non_ASCII_digits_throws_ArgumentException()
    {
        // La cifra "uno" a larghezza piena (codice 0xFF11) e' una cifra Unicode ma non e' esadecimale ASCII: char.IsDigit la accetterebbe.
        var digits = EmptyHash.ToCharArray();
        digits[0] = (char)0xFF11;
        var hash = new string(digits);
        Assert.Equal(64, hash.Length);

        var exception = Assert.Throws<ArgumentException>(() => new VerifiedContentStream(new MemoryStream(), hash));

        Assert.Equal("expectedSha256", exception.ParamName);
    }

    [Fact]
    public void Constructor_with_a_malformed_hash_does_not_touch_or_dispose_the_inner_stream()
    {
        var inner = new ScriptedStream(Abc);

        Assert.Throws<ArgumentException>(() => new VerifiedContentStream(inner, "not-a-hash"));

        Assert.Equal(0, inner.DisposeCount);
        Assert.Equal(0, inner.ReadCalls);
    }

    // --- forma dello stream -------------------------------------------------------------------------------------

    [Fact]
    public void The_stream_is_readable_forward_only_and_not_writable()
    {
        using var stream = new VerifiedContentStream(new MemoryStream(Abc), AbcHash);

        Assert.True(stream.CanRead);
        Assert.False(stream.CanSeek);
        Assert.False(stream.CanWrite);
        Assert.False(stream.CanTimeout);
    }

    [Fact]
    public void Length_Position_Seek_SetLength_and_Write_are_not_supported()
    {
        using var stream = new VerifiedContentStream(new MemoryStream(Abc), AbcHash);

        Assert.Throws<NotSupportedException>(() => { _ = stream.Length; });
        Assert.Throws<NotSupportedException>(() => { _ = stream.Position; });
        Assert.Throws<NotSupportedException>(() => { stream.Position = 0; });
        Assert.Throws<NotSupportedException>(() => stream.Seek(0, SeekOrigin.Begin));
        Assert.Throws<NotSupportedException>(() => stream.SetLength(0));
        Assert.Throws<NotSupportedException>(() => stream.Write(new byte[1], 0, 1));
        Assert.Throws<NotSupportedException>(() => stream.WriteByte(1));
    }

    [Fact]
    public async Task WriteAsync_is_not_supported()
    {
        using var stream = new VerifiedContentStream(new MemoryStream(Abc), AbcHash);

        await Assert.ThrowsAsync<NotSupportedException>(() => stream.WriteAsync(new byte[1], 0, 1));
    }

    [Fact]
    public async Task Flush_and_FlushAsync_do_nothing_and_do_not_disturb_the_reading()
    {
        using var stream = new VerifiedContentStream(new MemoryStream(Abc), AbcHash);

        stream.Flush();
        await stream.FlushAsync();
        stream.Flush();

        Assert.Equal(Abc, ReadAll(stream, 2));
    }

    [Fact]
    public void Read_with_invalid_arguments_throws_the_usual_exceptions()
    {
        using var stream = new VerifiedContentStream(new MemoryStream(Abc), AbcHash);

        Assert.Equal("buffer", Assert.Throws<ArgumentNullException>(() => stream.Read(null!, 0, 1)).ParamName);
        Assert.Equal("offset", Assert.Throws<ArgumentOutOfRangeException>(() => stream.Read(new byte[4], -1, 1)).ParamName);
        Assert.Equal("count", Assert.Throws<ArgumentOutOfRangeException>(() => stream.Read(new byte[4], 0, -1)).ParamName);
        Assert.Throws<ArgumentException>(() => stream.Read(new byte[4], 3, 2));
        Assert.Throws<ArgumentException>(() => stream.Read(new byte[4], 0, 5));
    }

    [Fact]
    public async Task ReadAsync_with_invalid_arguments_throws_the_usual_exceptions()
    {
        using var stream = new VerifiedContentStream(new MemoryStream(Abc), AbcHash);

        var nullBuffer = await Assert.ThrowsAsync<ArgumentNullException>(() => stream.ReadAsync(null!, 0, 1));
        Assert.Equal("buffer", nullBuffer.ParamName);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => stream.ReadAsync(new byte[4], -1, 1));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => stream.ReadAsync(new byte[4], 0, -1));
        await Assert.ThrowsAsync<ArgumentException>(() => stream.ReadAsync(new byte[4], 3, 2));
    }

    [Fact]
    public void Read_into_the_middle_of_a_buffer_writes_only_where_asked()
    {
        using var stream = new VerifiedContentStream(new MemoryStream(Abc), AbcHash);
        var buffer = new byte[] { 9, 9, 9, 9, 9 };

        var read = stream.Read(buffer, 1, 3);

        Assert.Equal(3, read);
        Assert.Equal(new byte[] { 9, 97, 98, 99, 9 }, buffer);
        Assert.Equal(0, stream.Read(buffer, 1, 3));
    }

    // --- stream interno che fallisce ----------------------------------------------------------------------------

    [Fact]
    public void An_exception_from_the_inner_stream_is_not_a_verdict_and_the_reading_can_go_on()
    {
        var inner = new ScriptedStream(NonAscii, maxChunk: 5);
        inner.FailAtRead[1] = new IOException("rete caduta per un attimo");
        using var stream = new VerifiedContentStream(inner, NonAsciiHash, NonAscii.Length);
        var buffer = new byte[5];
        var received = new List<byte>();

        var first = stream.Read(buffer, 0, buffer.Length);
        received.AddRange(buffer.Take(first));
        Assert.Throws<IOException>(() => stream.Read(buffer, 0, buffer.Length));
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            received.AddRange(buffer.Take(read));
        }

        // La lettura che ha lanciato non ha consegnato byte: l'hash e il conteggio sono rimasti coerenti e il verdetto e' positivo.
        Assert.Equal(NonAscii, received.ToArray());
    }

    [Fact]
    public void An_exception_from_the_inner_stream_followed_by_a_premature_end_ends_with_a_negative_verdict_from_the_wrapper()
    {
        // L'interno fallisce e poi dichiara la fine con soli 5 byte su 13: l'hash non puo' corrispondere.
        var inner = new ScriptedStream(NonAscii.Take(5).ToArray(), maxChunk: 5);
        inner.FailAtRead[1] = new IOException("rete caduta");
        using var stream = new VerifiedContentStream(inner, NonAsciiHash, NonAscii.Length);
        var buffer = new byte[5];
        Assert.Equal(5, stream.Read(buffer, 0, buffer.Length));
        Assert.Throws<IOException>(() => stream.Read(buffer, 0, buffer.Length));

        var exception = Assert.Throws<ContentIntegrityException>(() => stream.Read(buffer, 0, buffer.Length));

        Assert.True(exception.IsTruncated);
        Assert.Equal(13L, exception.ExpectedLength);
        Assert.Equal(5L, exception.ActualLength);
    }

    [Fact]
    public void A_ContentIntegrityException_from_the_inner_stream_passes_through_unchanged()
    {
        // E' il troncamento del trasporto, rilevato dallo stream dell'Infrastructure: qui non si rimappa e non si avvolge.
        var transport = new ContentIntegrityException(isTruncated: true, statusCode: 200, expectedLength: 590, actualLength: 100);
        var inner = new ScriptedStream(Abc);
        inner.FailAtRead[0] = transport;
        using var stream = new VerifiedContentStream(inner, AbcHash, expectedLength: 3);

        var thrown = Assert.Throws<ContentIntegrityException>(() => stream.Read(new byte[8], 0, 8));

        Assert.Same(transport, thrown);
        Assert.True(thrown.IsTruncated);
    }

    [Fact]
    public async Task A_ContentIntegrityException_from_the_inner_stream_passes_through_unchanged_with_ReadAsync()
    {
        var transport = new ContentIntegrityException(isTruncated: true, statusCode: 200, expectedLength: 590, actualLength: 100);
        var inner = new ScriptedStream(Abc);
        inner.FailAtRead[0] = transport;
        using var stream = new VerifiedContentStream(inner, AbcHash);

        var thrown = await Assert.ThrowsAsync<ContentIntegrityException>(() => stream.ReadAsync(new byte[8], 0, 8));

        Assert.Same(transport, thrown);
    }

    [Fact]
    public void An_inner_stream_returning_an_impossible_count_is_rejected_instead_of_corrupting_the_hash()
    {
        var negative = new ScriptedStream(Abc) { ReadResult = _ => -1 };
        var tooMany = new ScriptedStream(Abc) { ReadResult = count => count + 1 };
        using var first = new VerifiedContentStream(negative, AbcHash);
        using var second = new VerifiedContentStream(tooMany, AbcHash);

        Assert.Throws<InvalidOperationException>(() => first.Read(new byte[4], 0, 4));
        Assert.Throws<InvalidOperationException>(() => second.Read(new byte[4], 0, 4));
    }

    [Fact]
    public async Task A_cancelled_read_is_not_a_verdict_and_the_stream_stays_usable()
    {
        var inner = new ScriptedStream(Abc);
        using var stream = new VerifiedContentStream(inner, AbcHash, expectedLength: 3);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var buffer = new byte[16];

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stream.ReadAsync(buffer, 0, buffer.Length, cancellation.Token));
        var received = await ReadAllAsync(stream, 16);

        Assert.Equal(Abc, received);
        Assert.Equal(cancellation.Token, inner.Tokens[0]);
    }

    [Fact]
    public async Task The_cancellation_token_reaches_the_inner_stream_on_every_asynchronous_read()
    {
        var inner = new ScriptedStream(NonAscii, maxChunk: 4);
        using var stream = new VerifiedContentStream(inner, NonAsciiHash);
        using var cancellation = new CancellationTokenSource();
        var buffer = new byte[4];

        while (await stream.ReadAsync(buffer, 0, buffer.Length, cancellation.Token) > 0)
        {
        }

        Assert.NotEmpty(inner.Tokens);
        Assert.All(inner.Tokens, token => Assert.Equal(cancellation.Token, token));
    }
}
