using Filemaster.Application;
using Filemaster.Domain;

namespace Filemaster.UnitTests.Application;

/// <summary>
/// Lo stesso insieme di prove per <c>EnumerateAsync</c> dei documenti e dei contatti (le due estensioni condividono il ciclo di
/// paginazione): ogni classe concreta fornisce solo l'adattatore verso la sua porta finta, e i test li eredita.
/// </summary>
public abstract class EnumerationTests<TItem, TQuery>
    where TQuery : class
{
    /// <summary>Le pagine finte e il punto d'ingresso dell'estensione sotto prova.</summary>
    public abstract class Harness
    {
        /// <summary>Le pagine finte (le stesse che la porta finta restituisce da <c>ListAsync</c>).</summary>
        public abstract PagedScript<TItem> Pages { get; }

        /// <summary>I filtri che la porta finta ha ricevuto, uno per <c>ListAsync</c>.</summary>
        public abstract IReadOnlyList<TQuery?> Queries { get; }

        /// <summary>Il nome del primo parametro dell'estensione (<c>store</c> o <c>directory</c>).</summary>
        public abstract string StoreParameterName { get; }

        /// <summary>Chiama <c>EnumerateAsync</c> sulla porta finta.</summary>
        public abstract IAsyncEnumerable<TItem> Enumerate(TQuery? query = null, int? pageSize = null, CancellationToken cancellationToken = default);

        /// <summary>Chiama <c>EnumerateAsync</c> su una porta null.</summary>
        public abstract IAsyncEnumerable<TItem> EnumerateOnNull();
    }

    protected abstract Harness CreateHarness();

    protected abstract TItem CreateItem(int n);

    protected abstract TQuery ValidQuery();

    protected abstract TQuery InvalidQuery();

    // Il nome della proprieta' che Validate() mette in ParamName per la query non valida.
    protected abstract string InvalidQueryParamName { get; }

    // Sempre la stessa istanza per lo stesso numero: i Document contengono un JsonElement, che si confronta per identita'.
    private readonly Dictionary<int, TItem> _items = new();

    private TItem Item(int n)
    {
        if (!_items.TryGetValue(n, out var item))
        {
            item = CreateItem(n);
            _items[n] = item;
        }

        return item;
    }

    private static async Task<List<TItem>> ToListAsync(IAsyncEnumerable<TItem> source)
    {
        var all = new List<TItem>();
        await foreach (var item in source)
        {
            all.Add(item);
        }

        return all;
    }

    private TItem[] Items(params int[] numbers) => numbers.Select(Item).ToArray();

    // --- scorrimento --------------------------------------------------------------------------------------------

    [Fact]
    public async Task Several_pages_are_read_in_the_server_order_with_each_cursor_and_the_page_size()
    {
        var harness = CreateHarness();
        harness.Pages
            .Add(null, Items(1, 2), "cursore-1")
            .Add("cursore-1", Items(3, 4), "cursore-2")
            .Add("cursore-2", Items(5), null);

        var all = await ToListAsync(harness.Enumerate(pageSize: 2));

        Assert.Equal(Items(1, 2, 3, 4, 5), all);
        Assert.Equal(
            new[] { new PageRequest(null, 2), new PageRequest("cursore-1", 2), new PageRequest("cursore-2", 2) },
            harness.Pages.Requests);
    }

    [Fact]
    public async Task After_the_last_page_no_further_request_is_made()
    {
        var harness = CreateHarness();
        harness.Pages.MaxCalls = 3;
        harness.Pages
            .Add(null, Items(1), "c1")
            .Add("c1", Items(2), "c2")
            .Add("c2", Items(3), null);

        var all = await ToListAsync(harness.Enumerate());

        Assert.Equal(Items(1, 2, 3), all);
        Assert.Equal(3, harness.Pages.Requests.Count);
    }

    [Fact]
    public async Task A_single_page_costs_a_single_request()
    {
        var harness = CreateHarness();
        harness.Pages.MaxCalls = 1;
        harness.Pages.Add(null, Items(1, 2, 3), null);

        var all = await ToListAsync(harness.Enumerate());

        Assert.Equal(Items(1, 2, 3), all);
        Assert.Single(harness.Pages.Requests);
    }

    [Fact]
    public async Task An_empty_first_page_gives_an_empty_sequence_after_one_request()
    {
        var harness = CreateHarness();
        harness.Pages.MaxCalls = 1;
        harness.Pages.Add(null, Array.Empty<TItem>(), null);

        var all = await ToListAsync(harness.Enumerate());

        Assert.Empty(all);
        Assert.Single(harness.Pages.Requests);
    }

    [Fact]
    public async Task An_empty_page_in_the_middle_does_not_stop_the_enumeration()
    {
        var harness = CreateHarness();
        harness.Pages
            .Add(null, Items(1), "c1")
            .Add("c1", Array.Empty<TItem>(), "c2")
            .Add("c2", Items(2), null);

        var all = await ToListAsync(harness.Enumerate());

        Assert.Equal(Items(1, 2), all);
        Assert.Equal(3, harness.Pages.Requests.Count);
    }

    [Fact]
    public async Task Without_a_page_size_every_request_leaves_the_limit_to_the_server_default()
    {
        var harness = CreateHarness();
        harness.Pages.Add(null, Items(1), "c1").Add("c1", Items(2), null);

        await ToListAsync(harness.Enumerate());

        Assert.All(harness.Pages.Requests, request => Assert.Null(request!.Limit));
        Assert.Equal(new[] { null, "c1" }, harness.Pages.Requests.Select(request => request!.Cursor));
    }

    [Fact]
    public async Task The_query_reaches_every_page_unchanged()
    {
        var harness = CreateHarness();
        var query = ValidQuery();
        harness.Pages.Add(null, Items(1), "c1").Add("c1", Items(2), "c2").Add("c2", Items(3), null);

        await ToListAsync(harness.Enumerate(query));

        Assert.Equal(3, harness.Queries.Count);
        Assert.All(harness.Queries, received => Assert.Same(query, received));
    }

    [Fact]
    public async Task Without_a_query_the_port_receives_null_on_every_page()
    {
        var harness = CreateHarness();
        harness.Pages.Add(null, Items(1), "c1").Add("c1", Items(2), null);

        await ToListAsync(harness.Enumerate());

        Assert.Equal(2, harness.Queries.Count);
        Assert.All(harness.Queries, received => Assert.Null(received));
    }

    [Fact]
    public async Task The_sequence_is_lazy_and_makes_no_request_until_the_first_element_is_asked_for()
    {
        var harness = CreateHarness();
        harness.Pages.Add(null, Items(1), null);

        var sequence = harness.Enumerate();
        await using var enumerator = sequence.GetAsyncEnumerator();

        Assert.Empty(harness.Pages.Requests);
        Assert.True(await enumerator.MoveNextAsync());
        Assert.Single(harness.Pages.Requests);
    }

    [Fact]
    public async Task The_same_sequence_can_be_enumerated_twice_and_each_time_starts_from_the_first_page()
    {
        var harness = CreateHarness();
        harness.Pages.Add(null, Items(1), "c1").Add("c1", Items(2), null);
        var sequence = harness.Enumerate();

        var first = await ToListAsync(sequence);
        var second = await ToListAsync(sequence);

        Assert.Equal(Items(1, 2), first);
        Assert.Equal(first, second);
        Assert.Equal(4, harness.Pages.Requests.Count);
        Assert.Null(harness.Pages.Requests[2]!.Cursor);
    }

    // --- argomenti controllati subito ---------------------------------------------------------------------------

    [Fact]
    public void An_invalid_query_throws_at_the_call_and_not_at_the_first_iteration()
    {
        var harness = CreateHarness();

        var exception = Assert.Throws<ArgumentException>(() => harness.Enumerate(InvalidQuery()));

        Assert.Equal(InvalidQueryParamName, exception.ParamName);
        Assert.Empty(harness.Pages.Requests);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(201)]
    [InlineData(int.MinValue)]
    [InlineData(int.MaxValue)]
    public void A_page_size_out_of_range_throws_at_the_call_and_not_at_the_first_iteration(int pageSize)
    {
        var harness = CreateHarness();

        var exception = Assert.Throws<ArgumentOutOfRangeException>(() => harness.Enumerate(null, pageSize));

        Assert.Equal("pageSize", exception.ParamName);
        Assert.Empty(harness.Pages.Requests);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(50)]
    [InlineData(200)]
    public async Task A_page_size_in_range_is_accepted_and_sent_as_the_limit(int pageSize)
    {
        var harness = CreateHarness();
        harness.Pages.Add(null, Items(1), null);

        var all = await ToListAsync(harness.Enumerate(null, pageSize));

        Assert.Equal(Items(1), all);
        Assert.Equal(pageSize, harness.Pages.Requests[0]!.Limit);
    }

    [Fact]
    public void A_null_port_throws_ArgumentNullException_at_the_call()
    {
        var harness = CreateHarness();

        var exception = Assert.Throws<ArgumentNullException>(() => harness.EnumerateOnNull());

        Assert.Equal(harness.StoreParameterName, exception.ParamName);
    }

    // --- un server che non avanza -------------------------------------------------------------------------------

    [Fact]
    public async Task The_cursor_just_used_coming_back_stops_the_enumeration_with_UnexpectedResponseException()
    {
        var harness = CreateHarness();
        harness.Pages.MaxCalls = 2;
        harness.Pages.Add(null, Items(1), "A").Add("A", Items(2), "A");
        var seen = new List<TItem>();

        var exception = await Assert.ThrowsAsync<UnexpectedResponseException>(async () =>
        {
            await foreach (var item in harness.Enumerate())
            {
                seen.Add(item);
            }
        });

        // Gli elementi delle due pagine arrivano e la terza richiesta, che sarebbe uguale alla seconda, non parte.
        Assert.Equal(Items(1, 2), seen);
        Assert.Equal(2, harness.Pages.Requests.Count);
        Assert.Equal(200, exception.StatusCode);
    }

    [Fact]
    public async Task A_cursor_seen_earlier_in_a_cycle_stops_the_enumeration_with_UnexpectedResponseException()
    {
        var harness = CreateHarness();
        harness.Pages.MaxCalls = 3;
        harness.Pages.Add(null, Items(1), "A").Add("A", Items(2), "B").Add("B", Items(3), "A");
        var seen = new List<TItem>();

        var exception = await Assert.ThrowsAsync<UnexpectedResponseException>(async () =>
        {
            await foreach (var item in harness.Enumerate())
            {
                seen.Add(item);
            }
        });

        Assert.Equal(Items(1, 2, 3), seen);
        Assert.Equal(3, harness.Pages.Requests.Count);
        Assert.Equal(200, exception.StatusCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public async Task A_blank_next_cursor_is_an_UnexpectedResponseException_not_an_ArgumentException(string cursor)
    {
        var harness = CreateHarness();
        harness.Pages.MaxCalls = 1;
        harness.Pages.Add(null, Items(1), cursor);
        var seen = new List<TItem>();

        var exception = await Assert.ThrowsAsync<UnexpectedResponseException>(async () =>
        {
            await foreach (var item in harness.Enumerate())
            {
                seen.Add(item);
            }
        });

        Assert.Equal(Items(1), seen);
        Assert.Equal(200, exception.StatusCode);
    }

    [Fact]
    public async Task Cursors_are_compared_exactly_so_a_cursor_that_differs_only_in_case_is_a_new_one()
    {
        // I cursori sono opachi (base64): "a" e "A" sono due cursori diversi, non una ripetizione.
        var harness = CreateHarness();
        harness.Pages.Add(null, Items(1), "a").Add("a", Items(2), "A").Add("A", Items(3), null);

        var all = await ToListAsync(harness.Enumerate());

        Assert.Equal(Items(1, 2, 3), all);
    }

    // --- errori e annullamento ----------------------------------------------------------------------------------

    [Fact]
    public async Task An_error_on_the_second_page_is_propagated_after_the_elements_of_the_first()
    {
        var harness = CreateHarness();
        harness.Pages.MaxCalls = 2;
        harness.Pages.Add(null, Items(1, 2), "c1").Fail("c1", new InvalidOperationException("rete"));
        var seen = new List<TItem>();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var item in harness.Enumerate())
            {
                seen.Add(item);
            }
        });

        Assert.Equal("rete", exception.Message);
        Assert.Equal(Items(1, 2), seen);
        Assert.Equal(2, harness.Pages.Requests.Count);
    }

    [Fact]
    public async Task An_error_on_the_first_page_is_propagated_and_nothing_else_is_requested()
    {
        var harness = CreateHarness();
        harness.Pages.MaxCalls = 1;
        harness.Pages.Fail(null, new InvalidOperationException("subito"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => ToListAsync(harness.Enumerate()));

        Assert.Single(harness.Pages.Requests);
    }

    [Fact]
    public async Task A_token_cancelled_before_the_first_element_throws_and_no_request_is_made_even_if_the_port_ignores_the_token()
    {
        var harness = CreateHarness();
        harness.Pages.Add(null, Items(1), null);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var item in harness.Enumerate(null, null, cancellation.Token))
            {
                Assert.Fail("nessun elemento atteso: " + item);
            }
        });

        Assert.Empty(harness.Pages.Requests);
    }

    [Fact]
    public async Task A_token_cancelled_between_two_pages_stops_before_the_next_request_even_if_the_port_ignores_the_token()
    {
        var harness = CreateHarness();
        harness.Pages.MaxCalls = 1;
        harness.Pages.Add(null, Items(1, 2), "c1").Add("c1", Items(3), null);
        using var cancellation = new CancellationTokenSource();
        var seen = new List<TItem>();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var item in harness.Enumerate(null, null, cancellation.Token))
            {
                seen.Add(item);
                if (seen.Count == 2)
                {
                    cancellation.Cancel();
                }
            }
        });

        Assert.Equal(Items(1, 2), seen);
        Assert.Single(harness.Pages.Requests);
    }

    [Fact]
    public async Task A_token_cancelled_during_a_request_throws_OperationCanceledException_after_the_earlier_elements()
    {
        var harness = CreateHarness();
        using var cancellation = new CancellationTokenSource();
        harness.Pages.Add(null, Items(1), "c1").Add("c1", Items(2), null);
        harness.Pages.OnCall = (call, token) =>
        {
            if (call == 2)
            {
                cancellation.Cancel();
            }

            token.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        };
        var seen = new List<TItem>();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var item in harness.Enumerate(null, null, cancellation.Token))
            {
                seen.Add(item);
            }
        });

        Assert.Equal(Items(1), seen);
        Assert.Equal(2, harness.Pages.Requests.Count);
    }

    [Fact]
    public async Task The_caller_token_reaches_every_request()
    {
        var harness = CreateHarness();
        harness.Pages.Add(null, Items(1), "c1").Add("c1", Items(2), "c2").Add("c2", Items(3), null);
        using var cancellation = new CancellationTokenSource();

        await ToListAsync(harness.Enumerate(null, null, cancellation.Token));

        Assert.Equal(3, harness.Pages.Tokens.Count);
        Assert.All(harness.Pages.Tokens, token => Assert.Equal(cancellation.Token, token));
    }

    [Fact]
    public async Task A_token_given_with_WithCancellation_cancels_the_enumeration_and_reaches_the_port()
    {
        var harness = CreateHarness();
        harness.Pages.MaxCalls = 1;
        harness.Pages.Add(null, Items(1, 2), "c1").Add("c1", Items(3), null);
        using var cancellation = new CancellationTokenSource();
        var seen = new List<TItem>();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var item in harness.Enumerate().WithCancellation(cancellation.Token))
            {
                seen.Add(item);
                if (seen.Count == 2)
                {
                    cancellation.Cancel();
                }
            }
        });

        Assert.Equal(Items(1, 2), seen);
        Assert.Single(harness.Pages.Requests);
        Assert.Equal(cancellation.Token, harness.Pages.Tokens[0]);
    }

    [Fact]
    public async Task Disposing_the_enumerator_early_makes_no_further_request()
    {
        var harness = CreateHarness();
        harness.Pages.MaxCalls = 1;
        harness.Pages.Add(null, Items(1, 2), "c1").Add("c1", Items(3), null);

        await foreach (var item in harness.Enumerate())
        {
            Assert.Equal(Item(1), item);
            break;
        }

        Assert.Single(harness.Pages.Requests);
    }

    [Fact]
    public async Task Disposing_the_enumerator_by_hand_ends_the_sequence_and_a_later_MoveNextAsync_makes_no_request()
    {
        var harness = CreateHarness();
        harness.Pages.MaxCalls = 1;
        harness.Pages.Add(null, Items(1, 2), "c1").Add("c1", Items(3), null);
        var enumerator = harness.Enumerate().GetAsyncEnumerator();
        Assert.True(await enumerator.MoveNextAsync());

        await enumerator.DisposeAsync();

        Assert.False(await enumerator.MoveNextAsync());
        Assert.Single(harness.Pages.Requests);
    }
}
