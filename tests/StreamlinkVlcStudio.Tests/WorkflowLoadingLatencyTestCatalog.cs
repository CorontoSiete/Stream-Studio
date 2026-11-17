internal static class WorkflowLoadingLatencyTestCatalog
{
    internal static IReadOnlyList<(string Name, Func<Task> Run)> All { get; } =
    [
        ("workflow loading latency: VOD access batches overlap within a four-request limit and retain video order", () => AccessBatchesAsync(false)),
        ("workflow loading latency: a failed VOD access batch preserves other batches", () => AccessBatchesAsync(true)),
        ("workflow loading latency: canceling VOD access stops queued batches and drains active requests", () => CancelAccessBatchesAsync(false)),
        ("workflow loading latency: canceled VOD access rejects queued batches when active requests finish first", () => CancelAccessBatchesAsync(true)),
        ("workflow loading latency: Kick artwork and history share a four-request limit and drain on cancellation", KickRequestBudgetAsync)
    ];

    private static async Task AccessBatchesAsync(bool failBatch)
    {
        using var fixture = new AccessFixture();
        var pending = fixture.SearchAsync();
        try
        {
            await TestWait.UntilAsync(() => fixture.Loads.Count == 4, TimeSpan.FromSeconds(2));
            Assert.Equal(4, fixture.Active);
            // Keep the first three requests pending while later batches complete in reverse order.
            // A single slow batch must not prevent the rest of the page from being checked.
            for (var count = 4; count <= 7; count++)
            {
                await TestWait.UntilAsync(() => fixture.Loads.Count == count, TimeSpan.FromSeconds(2));
                fixture.Loads.Last().Complete();
            }
            Assert.Equal(false, pending.IsCompleted);
            var loads = fixture.Loads.ToArray();
            loads[2].Complete();
            loads[1].Complete(failBatch);
            loads[0].Complete();
            var result = await pending.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal(TwitchVodSearchStatus.Available, result.Status);
            Assert.SequenceEqual(Enumerable.Range(1000, 100).Select(id => id.ToString(CultureInfo.InvariantCulture)),
                result.Videos.Select(video => video.Id));
            Assert.SequenceEqual(new[] { 15, 15, 15, 15, 15, 15, 10 }, loads.Select(load => load.Ids.Length).OrderDescending());
            var failedIds = failBatch ? loads[1].Ids.ToHashSet() : [];
            foreach (var video in result.Videos)
            {
                var expected = failedIds.Contains(video.Id) ? TwitchVodAccessKind.Unknown
                    : int.Parse(video.Id, CultureInfo.InvariantCulture) % 2 == 0
                        ? TwitchVodAccessKind.Public : TwitchVodAccessKind.SubscriberOnly;
                Assert.Equal(expected, video.AccessKind);
            }
            Assert.Equal(4, fixture.MaximumActive);
            Assert.Equal(0, fixture.Active);
        }
        finally
        {
            fixture.Cancellation.Cancel();
            try { await pending; }
            catch (OperationCanceledException) { }
        }
    }

    private static async Task CancelAccessBatchesAsync(bool finishActiveDuringCancellation)
    {
        using var fixture = new AccessFixture();
        var pending = fixture.SearchAsync();
        try
        {
            await TestWait.UntilAsync(() => fixture.Loads.Count == 4, TimeSpan.FromSeconds(2));
            if (finishActiveDuringCancellation)
            {
                // Finish active requests before the queued semaphore waiters receive
                // cancellation. Admission must check the caller's token after taking a slot.
                var drained = false;
                using var release = fixture.Cancellation.Token.Register(() =>
                {
                    foreach (var load in fixture.Loads.ToArray()) load.Complete();
                    drained = SpinWait.SpinUntil(() => pending.IsCompleted, TimeSpan.FromSeconds(3));
                });
                fixture.Cancellation.Cancel();
                Assert.True(drained);
            }
        }
        finally
        {
            fixture.Cancellation.Cancel();
            try { await pending; }
            catch (OperationCanceledException) { }
        }
        await Assert.ThrowsAsync<OperationCanceledException>(() => pending);
        Assert.Equal(4, fixture.Loads.Count);
        Assert.Equal(0, fixture.Active);
        if (!finishActiveDuringCancellation)
            Assert.True(fixture.Loads.All(load => load.Token.IsCancellationRequested));
    }

    private static async Task KickRequestBudgetAsync()
    {
        using var cancellation = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var active = 0;
        var total = 0;
        var maximum = 0;
        var gate = new object();
        using var client = new HttpClient(new AsyncHttpMessageHandler(async (_, token) =>
        {
            lock (gate)
            {
                active++;
                total++;
                maximum = Math.Max(maximum, active);
                if (active == 4) started.TrySetResult();
            }
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                throw new InvalidOperationException("A blocked request must be canceled.");
            }
            finally { lock (gate) active--; }
        }));
        var service = new FollowedStreamsService(new MemoryLogger(), client);
        var channels = Enumerable.Range(0, 12).Select(index =>
            new FollowedChannel(PlatformKind.Kick, "channel" + index, "Channel", $"https://kick.com/channel{index}")).ToArray();
        var pending = service.EnrichOfflineChannelsAsync(channels, cancellation.Token);
        try { await started.Task.WaitAsync(TimeSpan.FromSeconds(2)); }
        finally
        {
            cancellation.Cancel();
            try { await pending; }
            catch (OperationCanceledException) { }
        }
        await Assert.ThrowsAsync<OperationCanceledException>(() => pending);
        Assert.Equal(4, maximum);
        Assert.Equal(4, total);
        Assert.Equal(0, active);
    }

    private sealed class AccessLoad(string[] ids, CancellationToken token)
    {
        private readonly TaskCompletionSource<bool> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal string[] Ids { get; } = ids;
        internal CancellationToken Token { get; } = token;
        internal void Complete(bool fail = false) => completion.TrySetResult(fail);
        internal async Task<HttpResponseMessage> WaitAsync()
        {
            if (await completion.Task.WaitAsync(Token))
                return new(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("{}") };
            var data = Ids.Select((id, index) => new
            {
                Alias = "vod" + index,
                Token = new
                {
                    value = JsonSerializer.Serialize(new
                    {
                        authorization = new { forbidden = false },
                        chansub = new
                        {
                            restricted_bitrates = int.Parse(id, CultureInfo.InvariantCulture) % 2 == 0
                            ? Array.Empty<string>() : ["chunked"]
                        },
                        vod_id = id
                    })
                }
            }).ToDictionary(item => item.Alias, item => item.Token);
            return Response(JsonSerializer.Serialize(new { data }));
        }
    }

    private sealed class AccessFixture : IDisposable
    {
        private readonly HttpClient client;
        private readonly object gate = new();
        private readonly AppSettings settings = new();
        internal CancellationTokenSource Cancellation { get; } = new();
        internal ConcurrentQueue<AccessLoad> Loads { get; } = new();
        internal int Active { get; private set; }
        internal int MaximumActive { get; private set; }

        internal AccessFixture()
        {
            settings.Chat.TwitchOAuthToken = "workflow-loading-" + Guid.NewGuid().ToString("N");
            client = new HttpClient(new AsyncHttpMessageHandler(async (request, token) =>
            {
                var uri = request.RequestUri!;
                if (uri.Host == "id.twitch.tv")
                    return Response("""{"client_id":"client","user_id":"1","login":"viewer","scopes":[],"expires_in":3600}""");
                if (uri.AbsolutePath == "/helix/users")
                    return Response("""{"data":[{"id":"1","login":"streamer","display_name":"Streamer"}]}""");
                if (uri.AbsolutePath == "/helix/videos")
                    return Response(JsonSerializer.Serialize(new
                    {
                        data = Enumerable.Range(1000, 100).Select(id => new { id = id.ToString(CultureInfo.InvariantCulture), url = $"https://www.twitch.tv/videos/{id}" })
                    }));
                Assert.Equal("gql.twitch.tv", uri.Host);
                using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
                var load = new AccessLoad(document.RootElement.GetProperty("variables").EnumerateObject()
                    .Select(variable => variable.Value.GetString()!).ToArray(), token);
                lock (gate)
                {
                    Active++;
                    MaximumActive = Math.Max(MaximumActive, Active);
                }
                Loads.Enqueue(load);
                try { return await load.WaitAsync(); }
                finally { lock (gate) Active--; }
            }));
        }

        internal Task<TwitchVodSearchResult> SearchAsync() => new TwitchVodService(new MemoryLogger(), client)
            .SearchAsync(new TwitchVodSearchRequest("streamer"), settings, Cancellation.Token);

        public void Dispose()
        {
            Cancellation.Cancel();
            Cancellation.Dispose();
            client.Dispose();
        }
    }

    private static HttpResponseMessage Response(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body) };
}
