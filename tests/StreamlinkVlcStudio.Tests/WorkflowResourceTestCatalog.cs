internal static class WorkflowResourceTestCatalog
{
    internal static IReadOnlyList<(string Name, Func<Task> Run)> All { get; } =
    [
        ("workflow resources: Enter shares an active automatic search and still permits refresh", ShareSearchAsync),
        ("workflow resources: changed queries cancel shared search without accepting stale results", ReplaceSearchAsync),
        ("workflow resources: changing quality starts a new search instead of sharing old probes", SearchQualityAsync),
        ("workflow resources: viewer counts preserve search rows and commands while reordering", PreserveSearchRowsAsync),
        ("workflow resources: playable live search results appear before optional metadata", EarlySearchResultsAsync),
        ("workflow resources: desktop stream search fills missing live titles and categories from the provider", SearchTitlesAndCategoriesAsync),
        ("workflow resources: late metadata cannot overwrite a replacement search", StaleSearchMetadataAsync),
        ("workflow resources: each viewer count appears without waiting for slower channels", ProgressiveSearchCountsAsync),
        ("workflow resources: channel artwork and viewer counts load independently", IndependentSearchDetailsAsync),
        ("workflow resources: replacing search cancels artwork and viewer counts together", CancelSearchDetailsAsync),
        ("workflow resources: Recent reuses fresh live and offline results without rebuilding cards", ReuseRecentAsync),
        ("workflow resources: Recent checks new channels without refreshing fresh channels", NewRecentAsync),
        ("workflow resources: Recent retries unavailable metadata on the next visit", RetryRecentAsync),
        ("workflow resources: metadata freshness expires and deletion removes cached freshness", RecentFreshnessAsync),
        ("workflow resources: Recent checks at most four distinct channels concurrently", ConcurrentRecentAsync),
        ("workflow resources: shutdown cancels Recent requests before admitting queued channels", CancelRecentAsync)
    ];

    private static Task ShareSearchAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        var release = new TaskCompletionSource<StreamSearchResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken observedToken = default;
        var search = new FakeStreamSearchService(SearchResult("streamer"))
        {
            ResponderAsync = (_, token) =>
            {
                observedToken = token;
                return release.Task.WaitAsync(token);
            }
        };
        await using var viewModel = Create(search: search);
        viewModel.NewStreamText = "streamer";
        Assert.Equal(1, search.CallCount);
        var originalToken = observedToken;
        var enter = viewModel.AddAndPlayCommand.ExecuteAsync();
        try
        {
            Assert.Equal(1, search.CallCount);
            Assert.Equal(false, originalToken.IsCancellationRequested);
            Assert.Equal(false, enter.IsCompleted);
        }
        finally { release.TrySetResult(SearchResult("streamer")); }
        await enter;
        Assert.Equal("streamer", viewModel.StreamSearchResults.Single().Channel);
        await viewModel.AddAndPlayCommand.ExecuteAsync();
        Assert.Equal(2, search.CallCount);
    });

    private static Task ReplaceSearchAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        var oldRelease = new TaskCompletionSource<StreamSearchResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var newRelease = new TaskCompletionSource<StreamSearchResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken oldToken = default;
        var search = new FakeStreamSearchService(SearchResult("unused"))
        {
            ResponderAsync = (request, token) =>
            {
                if (request.Query == "old")
                {
                    oldToken = token;
                    // Deliberately ignore cancellation to exercise generation checks.
                    return oldRelease.Task;
                }
                return newRelease.Task.WaitAsync(token);
            }
        };
        await using var viewModel = Create(search: search);
        viewModel.NewStreamText = "old";
        var enter = viewModel.AddAndPlayCommand.ExecuteAsync();
        try
        {
            viewModel.NewStreamText = "new";
            Assert.True(oldToken.IsCancellationRequested);
            oldRelease.SetResult(SearchResult("old"));
            await enter;
            Assert.Equal(0, viewModel.StreamSearchResults.Count);
            var nextEnter = viewModel.AddAndPlayCommand.ExecuteAsync();
            Assert.Equal(2, search.CallCount);
            newRelease.SetResult(SearchResult("new"));
            await nextEnter;
            Assert.Equal("new", viewModel.StreamSearchResults.Single().Channel);
        }
        finally
        {
            oldRelease.TrySetResult(SearchResult("old"));
            newRelease.TrySetResult(SearchResult("new"));
        }
    });

    private static Task SearchQualityAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        var release = new TaskCompletionSource<StreamSearchResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var tokens = new List<CancellationToken>();
        var search = new FakeStreamSearchService(SearchResult("streamer"))
        {
            ResponderAsync = (_, token) =>
            {
                tokens.Add(token);
                return release.Task.WaitAsync(token);
            }
        };
        await using var viewModel = Create(search: search);
        viewModel.NewStreamText = "streamer";
        viewModel.SelectedQuality = "480p";
        var enter = viewModel.AddAndPlayCommand.ExecuteAsync();
        try
        {
            Assert.Equal(2, search.CallCount);
            Assert.Equal("480p", search.Requests[1].Quality);
            Assert.True(tokens[0].IsCancellationRequested);
        }
        finally { release.TrySetResult(SearchResult("streamer")); }
        await enter;
        Assert.Equal(1, viewModel.StreamSearchResults.Count);
    });

    private static Task PreserveSearchRowsAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var counts = new FakeViewerCountService
        {
            ResponderAsync = async (target, token) =>
            {
                await release.Task.WaitAsync(token);
                return new ViewerCountResult(ViewerCountState.Available, target.Channel == "second" ? 200 : 10, "updated");
            }
        };
        await using var viewModel = Create(search: new FakeStreamSearchService(SearchResult("first", "second")), counts: counts);
        viewModel.NewStreamText = "streamer";
        var original = viewModel.StreamSearchResults.ToArray();
        var actions = new List<NotifyCollectionChangedAction>();
        var properties = new List<string?>();
        viewModel.StreamSearchResults.CollectionChanged += (_, e) => actions.Add(e.Action);
        original[1].PropertyChanged += (_, e) => properties.Add(e.PropertyName);
        release.SetResult();
        await TestWait.UntilAsync(() => viewModel.StreamSearchResults[0].ViewerCount == 200, TimeSpan.FromSeconds(2));
        Assert.True(ReferenceEquals(original[1], viewModel.StreamSearchResults[0]));
        Assert.True(ReferenceEquals(original[0].OpenCommand, viewModel.StreamSearchResults[1].OpenCommand));
        Assert.True(actions.All(action => action == NotifyCollectionChangedAction.Move));
        Assert.True(properties.Contains(nameof(StreamSearchResultViewModel.ViewerCount)));
        Assert.True(properties.Contains(nameof(StreamSearchResultViewModel.HasViewerCount)));
        Assert.True(properties.Contains(nameof(StreamSearchResultViewModel.ViewerCountText)));
        Assert.Equal("200 viewers", original[1].ViewerCountText);
    });

    private static Task EarlySearchResultsAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        var release = new TaskCompletionSource<StreamMetadataResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var viewModel = Create(settings: new AppSettings { StreamlinkPath = "streamlink.exe" },
            metadata: new SearchMetadataService((_, token) => release.Task.WaitAsync(token)));
        viewModel.NewStreamText = "streamer";
        try
        {
            Assert.Equal(2, viewModel.StreamSearchResults.Count);
            Assert.Equal(false, viewModel.IsStreamSearchRunning);
            var rows = viewModel.StreamSearchResults.ToArray();
            Assert.True(rows.All(row => row.OpenCommand.CanExecute(null)));
            var changes = new List<NotifyCollectionChangedAction>();
            viewModel.StreamSearchResults.CollectionChanged += (_, args) => changes.Add(args.Action);
            release.SetResult(new(StreamMetadataState.Available, "https://example.com/thumbnail.jpg",
                "Streamer display", "live", "Music", "https://example.com/avatar.jpg"));
            await TestWait.UntilAsync(() => rows.All(row => row.HasThumbnail), TimeSpan.FromSeconds(2));
            Assert.SequenceEqual(rows, viewModel.StreamSearchResults);
            Assert.Equal(0, changes.Count);
            Assert.True(rows.All(row => row.DisplayName == "Streamer display" && row.CategoryName == "Music" &&
                row.Target.ProfileImageUrl == "https://example.com/avatar.jpg"));
        }
        finally { release.TrySetResult(Metadata()); }
    });

    private static Task SearchTitlesAndCategoriesAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        var kickDetails = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var requests = new ConcurrentBag<string>();
        using var client = new HttpClient(new AsyncHttpMessageHandler(async (request, token) =>
        {
            var uri = request.RequestUri!;
            requests.Add(uri.AbsolutePath);
            Assert.True(request.Headers.Authorization is null);
            string json;
            if (uri.Host == "gql.twitch.tv")
            {
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
                var variables = body.RootElement.GetProperty("variables");
                if (variables.TryGetProperty("login", out var login))
                {
                    Assert.Equal("creator", login.GetString());
                    json = """
                        {"data":{"user":{"login":"creator","stream":{"title":"Pasted Twitch URL","game":{"name":"Music"}}}}}
                        """;
                }
                else
                {
                    Assert.Equal("creator", variables.GetProperty("query").GetString());
                    json = """
                        {"data":{"searchFor":{"channels":{"edges":[{"item":{"login":"creator","stream":{"title":"Twitch broadcast","game":{"name":"Art"}}}}]}}}}
                        """;
                }
            }
            else if (uri.AbsolutePath == "/api/search")
            {
                json = """
                    {"channels":[{"slug":"creator","isLive":true,"recentCategories":[{"name":"Previous category"}]},
                                 {"slug":"creatoroffline","isLive":false}]}
                    """;
            }
            else
            {
                Assert.Equal("/api/v2/channels/creator", uri.AbsolutePath);
                await kickDetails.Task.WaitAsync(token);
                json = """
                    {"slug":"creator","livestream":{"is_live":true,"session_title":"Kick broadcast","categories":[{"name":"Just Chatting"}]}}
                    """;
            }
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
        }));
        var logger = new MemoryLogger();
        var search = new StreamSearchService(logger, new FakeStreamlinkService(), client);
        var metadata = new StreamMetadataService(logger, client);
        await using var viewModel = Create(new AppSettings { StreamlinkPath = "streamlink.exe" }, search, metadata);
        viewModel.NewStreamText = "creator";
        try
        {
            await TestWait.UntilAsync(() => requests.Contains("/api/v2/channels/creator"), TimeSpan.FromSeconds(2));
            var twitch = viewModel.StreamSearchResults.Single(row => row.Platform == PlatformKind.Twitch);
            var kick = viewModel.StreamSearchResults.Single(row => row.Platform == PlatformKind.Kick && row.IsLive);
            var offline = viewModel.StreamSearchResults.Single(row => row.IsOffline);
            Assert.Equal("Twitch broadcast", twitch.Title);
            Assert.Equal("Art", twitch.CategoryName);
            Assert.True(kick.OpenCommand.CanExecute(null));
            Assert.Equal(false, kick.HasTitle || kick.HasCategory);
            Assert.Equal(false, offline.HasTitle || offline.HasCategory);

            kickDetails.SetResult();
            await TestWait.UntilAsync(() => kick.HasTitle && kick.HasCategory, TimeSpan.FromSeconds(2));
            Assert.Equal("Kick broadcast", kick.Title);
            Assert.Equal("Just Chatting", kick.CategoryName);
            Assert.Equal(kick.CategoryName, kick.Target.CategoryName);
            Assert.True(ReferenceEquals(kick, viewModel.StreamSearchResults.Single(row => row.Platform == PlatformKind.Kick && row.IsLive)));
            Assert.SequenceEqual(new[] { "/api/search", "/api/v2/channels/creator", "/gql" }, requests.Order(StringComparer.Ordinal));

            viewModel.NewStreamText = "https://www.twitch.tv/creator";
            await TestWait.UntilAsync(() => viewModel.StreamSearchResults.SingleOrDefault()?.Title == "Pasted Twitch URL", TimeSpan.FromSeconds(2));
            Assert.Equal("Music", viewModel.StreamSearchResults.Single().CategoryName);
        }
        finally
        {
            kickDetails.TrySetResult();
        }
    });

    private static Task StaleSearchMetadataAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        var old = new TaskCompletionSource<StreamMetadataResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var viewModel = Create(settings: new AppSettings { StreamlinkPath = "streamlink.exe" },
            metadata: new SearchMetadataService((target, _) => target.Channel == "old"
                ? old.Task : Task.FromResult(new StreamMetadataResult(StreamMetadataState.Available,
                    "https://example.com/new.jpg", "New channel", "live"))));
        viewModel.NewStreamText = "old";
        var pending = viewModel.AddAndPlayCommand.ExecuteAsync();
        try
        {
            Assert.Equal(2, viewModel.StreamSearchResults.Count);
            viewModel.NewStreamText = "new";
            var rows = viewModel.StreamSearchResults.ToArray();
            Assert.True(rows.All(row => row.DisplayName == "New channel"));
            old.SetResult(new(StreamMetadataState.Available, "https://example.com/old.jpg", "Old channel", "live"));
            await pending;
            Assert.SequenceEqual(rows, viewModel.StreamSearchResults);
            Assert.True(rows.All(row => row.Channel == "new" && row.DisplayName == "New channel"));
        }
        finally { old.TrySetResult(Metadata()); }
    });

    private static Task ProgressiveSearchCountsAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        var slow = new TaskCompletionSource<ViewerCountResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var fast = new TaskCompletionSource<ViewerCountResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var counts = new FakeViewerCountService
        {
            ResponderAsync = (target, token) => (target.Channel == "first" ? slow.Task : fast.Task).WaitAsync(token)
        };
        await using var viewModel = Create(search: new FakeStreamSearchService(SearchResult("first", "second")), counts: counts);
        viewModel.NewStreamText = "streamer";
        var rows = viewModel.StreamSearchResults.ToArray();
        try
        {
            fast.SetResult(new(ViewerCountState.Available, 200, "updated"));
            await TestWait.UntilAsync(() => viewModel.StreamSearchResults[0].ViewerCount == 200, TimeSpan.FromSeconds(2));
            Assert.Equal(false, slow.Task.IsCompleted);
            Assert.True(ReferenceEquals(rows[1], viewModel.StreamSearchResults[0]));
            Assert.True(ReferenceEquals(rows[0].OpenCommand, viewModel.StreamSearchResults[1].OpenCommand));
            slow.SetResult(new(ViewerCountState.Available, 10, "updated"));
            await TestWait.UntilAsync(() => rows[0].ViewerCount == 10, TimeSpan.FromSeconds(2));
        }
        finally
        {
            slow.TrySetResult(new(ViewerCountState.Unavailable, null, "unavailable"));
            fast.TrySetResult(new(ViewerCountState.Unavailable, null, "unavailable"));
        }
    });

    private static Task IndependentSearchDetailsAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        var slow = new TaskCompletionSource<StreamMetadataResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var fast = new TaskCompletionSource<StreamMetadataResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var viewers = new TaskCompletionSource<ViewerCountResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var counts = new FakeViewerCountService { ResponderAsync = (_, token) => viewers.Task.WaitAsync(token) };
        await using var viewModel = Create(settings: new AppSettings { StreamlinkPath = "streamlink.exe" },
            metadata: new SearchMetadataService((target, token) =>
                (target.Platform == PlatformKind.Twitch ? slow.Task : fast.Task).WaitAsync(token)), counts: counts);
        viewModel.NewStreamText = "streamer";
        var rows = viewModel.StreamSearchResults.ToArray();
        var changes = new List<NotifyCollectionChangedAction>();
        viewModel.StreamSearchResults.CollectionChanged += (_, args) => changes.Add(args.Action);
        try
        {
            Assert.Equal(2, counts.CallCount);
            fast.SetResult(new(StreamMetadataState.Available, "https://example.com/kick.jpg", "Kick channel", "live"));
            await TestWait.UntilAsync(() => rows.Single(row => row.Platform == PlatformKind.Kick).HasThumbnail, TimeSpan.FromSeconds(2));
            Assert.Equal(false, slow.Task.IsCompleted);
            Assert.Equal(false, viewers.Task.IsCompleted);
            Assert.Equal(false, rows.Single(row => row.Platform == PlatformKind.Twitch).HasThumbnail);
            Assert.Equal(0, changes.Count);
            viewers.SetResult(new(ViewerCountState.Available, 123, "updated"));
            await TestWait.UntilAsync(() => rows.All(row => row.ViewerCount == 123), TimeSpan.FromSeconds(2));
            Assert.Equal(false, slow.Task.IsCompleted);
            slow.SetResult(new(StreamMetadataState.Available, "https://example.com/twitch.jpg", "Twitch channel", "live"));
            await TestWait.UntilAsync(() => rows.All(row => row.HasThumbnail), TimeSpan.FromSeconds(2));
            Assert.SequenceEqual(rows, viewModel.StreamSearchResults);
            Assert.True(rows.All(row => row.OpenCommand.CanExecute(null) && row.ViewerCount == 123));
        }
        finally
        {
            slow.TrySetResult(Metadata());
            fast.TrySetResult(Metadata());
            viewers.TrySetResult(new(ViewerCountState.Unavailable, null, "unavailable"));
        }
    });

    private static Task CancelSearchDetailsAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        var tokens = new List<CancellationToken>();
        var metadata = new TaskCompletionSource<StreamMetadataResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var viewers = new TaskCompletionSource<ViewerCountResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var viewModel = Create(settings: new AppSettings { StreamlinkPath = "streamlink.exe" },
            metadata: new SearchMetadataService((_, token) => { tokens.Add(token); return metadata.Task; }),
            counts: new FakeViewerCountService { ResponderAsync = (_, token) => { tokens.Add(token); return viewers.Task; } });
        viewModel.NewStreamText = "old";
        var pending = viewModel.AddAndPlayCommand.ExecuteAsync();
        try
        {
            Assert.Equal(4, tokens.Count);
            viewModel.NewStreamText = "";
            Assert.True(tokens.All(token => token.IsCancellationRequested));
            metadata.SetResult(new(StreamMetadataState.Available, "https://example.com/old.jpg", "Old channel", "live"));
            viewers.SetResult(new(ViewerCountState.Available, 123, "updated"));
            await pending;
            Assert.Equal(0, viewModel.StreamSearchResults.Count);
            Assert.Equal(false, viewModel.IsStreamSearchRunning);
        }
        finally
        {
            metadata.TrySetResult(Metadata());
            viewers.TrySetResult(new(ViewerCountState.Unavailable, null, "unavailable"));
        }
    });

    private sealed class SearchMetadataService(Func<StreamTarget, CancellationToken, Task<StreamMetadataResult>> respond)
        : IStreamMetadataService
    {
        public Task<StreamMetadataResult> GetLiveStreamMetadataAsync(StreamTarget target, AppSettings settings,
            CancellationToken cancellationToken = default) => respond(target, cancellationToken);
    }

    private static Task ReuseRecentAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        var metadata = new FakeStreamMetadataService(Metadata(), new(StreamMetadataState.Offline, "", "", "offline"));
        await using var viewModel = Create(settings: RecentSettings(2), metadata: metadata);
        viewModel.ShowRecentHomePageCommand.Execute(null);
        var cards = viewModel.RecentStreams.ToArray();
        var changes = 0;
        viewModel.RecentStreams.CollectionChanged += (_, _) => changes++;
        for (var i = 0; i < 5; i++)
        {
            viewModel.ShowFollowedHomePageCommand.Execute(null);
            viewModel.ShowRecentHomePageCommand.Execute(null);
        }
        Assert.Equal(2, metadata.CallCount);
        Assert.Equal(0, changes);
        Assert.SequenceEqual(cards, viewModel.RecentStreams);
        Assert.SequenceEqual(new[] { "Live", "Offline" }, cards.Select(card => card.LiveStatusText));
    });

    private static Task NewRecentAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        var settings = RecentSettings(1);
        var metadata = new FakeStreamMetadataService(Metadata());
        await using var viewModel = Create(settings: settings, metadata: metadata);
        viewModel.ShowRecentHomePageCommand.Execute(null);
        settings.RecentStreams.Add(Recent("newchannel"));
        viewModel.ShowRecentHomePageCommand.Execute(null);
        Assert.SequenceEqual(new[] { "channel0", "newchannel" }, metadata.Requests.Select(target => target.Channel));
        Assert.Equal(2, viewModel.RecentStreams.Count);
    });

    private static Task RetryRecentAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        var metadata = new FakeStreamMetadataService(new(StreamMetadataState.Unavailable, "", "", "try again"), Metadata());
        await using var viewModel = Create(settings: RecentSettings(1), metadata: metadata);
        viewModel.ShowRecentHomePageCommand.Execute(null);
        Assert.Equal("Unknown", viewModel.RecentStreams.Single().LiveStatusText);
        viewModel.ShowRecentHomePageCommand.Execute(null);
        Assert.Equal(2, metadata.CallCount);
        Assert.Equal("Live", viewModel.RecentStreams.Single().LiveStatusText);
    });

    private static Task RecentFreshnessAsync()
    {
        var controller = new RecentStreamController();
        var now = DateTimeOffset.UtcNow;
        var interval = TimeSpan.FromMinutes(5);
        controller.RecordMetadataRefresh("Twitch:streamer", now, succeeded: true);
        Assert.True(controller.IsMetadataFresh("twitch:STREAMER", now + interval - TimeSpan.FromTicks(1), interval));
        Assert.Equal(false, controller.IsMetadataFresh("Twitch:streamer", now + interval, interval));
        Assert.Equal(false, controller.IsMetadataFresh("Twitch:streamer", now - TimeSpan.FromTicks(1), interval));
        Assert.Equal(false, controller.IsMetadataFresh("Twitch:streamer", now, TimeSpan.Zero));
        controller.RecordMetadataRefresh("Twitch:streamer", now, succeeded: false);
        Assert.Equal(false, controller.IsMetadataFresh("Twitch:streamer", now, interval));
        controller.RecordMetadataRefresh("Twitch:streamer", now, succeeded: true);
        controller.RemoveLiveStatus("Twitch:streamer");
        Assert.Equal(false, controller.IsMetadataFresh("Twitch:streamer", now, interval));
        return Task.CompletedTask;
    }

    private static Task ConcurrentRecentAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        var settings = RecentSettings(9);
        settings.RecentStreams.Add(Recent("CHANNEL0"));
        var metadata = new BlockingMetadataService();
        await using var viewModel = Create(settings: settings, metadata: metadata);
        try
        {
            viewModel.ShowRecentHomePageCommand.Execute(null);
            Assert.Equal(4, metadata.Requests.Count);
            viewModel.ShowRecentHomePageCommand.Execute(null);
            Assert.Equal(4, metadata.Requests.Count);
            metadata.Release.TrySetResult();
            await TestWait.UntilAsync(() => viewModel.RecentStreams.All(card => card.LiveStatusText == "Live"), TimeSpan.FromSeconds(2));
            Assert.Equal(9, metadata.Requests.Count);
            Assert.Equal(4, metadata.MaxConcurrentCalls);
        }
        finally { metadata.Release.TrySetResult(); }
    });

    private static Task CancelRecentAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        var metadata = new BlockingMetadataService();
        await using var viewModel = Create(settings: RecentSettings(9), metadata: metadata);
        viewModel.ShowRecentHomePageCommand.Execute(null);
        Assert.Equal(4, metadata.Requests.Count);
        await viewModel.DisposeAsync();
        Assert.Equal(4, metadata.Requests.Count);
        Assert.Equal(0, metadata.ActiveCalls);
        Assert.True(metadata.Tokens.All(token => token.IsCancellationRequested));
    });

    private static MainViewModel Create(AppSettings? settings = null, IStreamSearchService? search = null,
        IStreamMetadataService? metadata = null, IViewerCountService? counts = null)
    {
        settings ??= new AppSettings();
        return TestViewModels.CreateMain(settings, new FakeSettingsService(settings), new FakeStreamlinkService(),
            new FakePlaybackEngineFactory(), new FakeChatClientFactory(), new MemoryLogger(), action => action(),
            streamSearchService: search, streamMetadataService: metadata, viewerCountService: counts,
            streamSearchDebounceInterval: TimeSpan.Zero, recentThumbnailRefreshInterval: TimeSpan.FromHours(1));
    }

    private static StreamSearchResult SearchResult(params string[] channels) => new(StreamSearchResultStatus.Available,
        channels.Select(channel => new StreamSearchChannel(PlatformKind.Twitch, channel, channel,
            $"https://www.twitch.tv/{channel}", "", "", "", StreamSearchChannelState.Live,
            StreamSearchSourceStatus.Available, "live", true)).ToArray(), "found");

    private static AppSettings RecentSettings(int count) => new()
    {
        RecentStreams = Enumerable.Range(0, count).Select(index => Recent($"channel{index}")).ToList()
    };

    private static RecentStreamSettings Recent(string channel) => new()
    {
        Platform = PlatformKind.Twitch,
        Channel = channel,
        Url = $"https://www.twitch.tv/{channel}"
    };

    private static StreamMetadataResult Metadata() => new(StreamMetadataState.Available, "", "", "live");

    private sealed class BlockingMetadataService : IStreamMetadataService
    {
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<StreamTarget> Requests { get; } = [];
        public List<CancellationToken> Tokens { get; } = [];
        public int ActiveCalls { get; private set; }
        public int MaxConcurrentCalls { get; private set; }

        public async Task<StreamMetadataResult> GetLiveStreamMetadataAsync(StreamTarget target, AppSettings settings,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(target);
            Tokens.Add(cancellationToken);
            MaxConcurrentCalls = Math.Max(MaxConcurrentCalls, ++ActiveCalls);
            try
            {
                await Release.Task.WaitAsync(cancellationToken);
                return Metadata();
            }
            finally { ActiveCalls--; }
        }
    }
}
