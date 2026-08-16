using System.Diagnostics;
using YTM.Lyrics.Core.Cache;
using YTM.Lyrics.Core.Cleaning;
using YTM.Lyrics.Core.Engine;
using YTM.Lyrics.Core.Playback;
using YTM.Lyrics.Core.Session;
using YTM.Lyrics.Core.Tests.Engine;
using YTM.Lyrics.Core.Timeline;

namespace YTM.Lyrics.Core.Tests.Session;

/// <summary>
/// LyricsSession 会话编排层单测（TASK-602，ADR-13 落地）。
/// 覆盖：TrackChanged 立即加载、duration=0 延迟搜索（250ms 轮询）、切歌竞态代际守卫、
/// ReloadAsync、UnbindAsync、ReplaceEngine、ReplacePlayback、加载异常广播、F9 绑定转发。
/// </summary>
public class LyricsSessionTests
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 16, 8, 0, 0, TimeSpan.Zero);

    private static TrackInfo Track(double durationSec = 269, string title = "晴天", string artist = "周杰伦")
        => new(title, artist, durationSec);

    private static PlaybackSnapshot Snap(TrackInfo track, double pos = 0, bool playing = true, long seq = 1)
        => new(track, pos, playing, seq, T0, T0);

    private static async Task WaitFor(Func<bool> condition, int timeoutMs = 3000)
    {
        var sw = Stopwatch.StartNew();
        while (!condition() && sw.ElapsedMilliseconds < timeoutMs)
        {
            await Task.Delay(10);
        }
        Assert.True(condition(), $"condition not met within {timeoutMs}ms");
    }

    private static async Task WaitForAsync(Func<Task<bool>> condition, int timeoutMs = 3000)
    {
        var sw = Stopwatch.StartNew();
        while (!await condition() && sw.ElapsedMilliseconds < timeoutMs)
        {
            await Task.Delay(10);
        }
        Assert.True(await condition(), $"condition not met within {timeoutMs}ms");
    }

    private static LyricsSession CreateSession(
        FakeMachine machine, FakeEngine engine, out FakeCache cache, out FakeCleaner cleaner)
    {
        cache = new FakeCache();
        cleaner = new FakeCleaner();
        return new LyricsSession(machine, cleaner, engine, cache);
    }

    [Fact]
    public async Task TrackChanged_有效时长_立即加载并透传事件()
    {
        var machine = new FakeMachine { Current = Snap(Track()) };
        var engine = new FakeEngine();
        var session = CreateSession(machine, engine, out _, out _);
        LyricsRenderState? received = null;
        var trackEvents = 0;
        session.LyricsChanged += (_, s) => received = s;
        session.TrackChanged += (_, _) => trackEvents++;

        machine.RaiseTrackChanged();

        Assert.Equal(1, trackEvents);                 // TrackChanged 透传（同步）
        await WaitFor(() => engine.GetCalls == 1);    // duration>0 → 立即加载（加载链路异步）
        await WaitFor(() => received is not null);
        Assert.Equal(MatchConfidence.High, received!.Confidence);
        Assert.Equal(LyricsSourceKind.QQMusic, received.Source);
        session.Dispose();
    }

    [Fact]
    public async Task TrackChanged_时长为零_延迟轮询等有效时长后加载()
    {
        var machine = new FakeMachine { Current = Snap(Track(durationSec: 0)) };
        var engine = new FakeEngine();
        var session = CreateSession(machine, engine, out _, out _);
        LyricsRenderState? received = null;
        session.LyricsChanged += (_, s) => received = s;

        machine.RaiseTrackChanged();

        Assert.Equal(0, engine.GetCalls);             // 时长无效：不立即搜索
        await Task.Delay(400);                        // 跨过至少一个 250ms 轮询周期
        Assert.Equal(0, engine.GetCalls);             // 时长仍无效：继续等

        machine.Current = Snap(Track(durationSec: 269)); // 首个有效时长快照到达
        await WaitFor(() => engine.GetCalls == 1, 2000); // 下一轮询 tick → Ready → 加载
        await WaitFor(() => received is not null);
        Assert.Equal(MatchConfidence.High, received!.Confidence);
        session.Dispose();
    }

    [Fact]
    public async Task 切歌竞态_旧代结果被丢弃()
    {
        var machine = new FakeMachine { Current = Snap(Track(durationSec: 269, title: "旧歌")) };
        var tcs1 = new TaskCompletionSource<LyricsResult>();
        var tcs2 = new TaskCompletionSource<LyricsResult>();
        var calls = 0;
        var engine = new FakeEngine(_ => calls++ == 0 ? tcs1.Task : tcs2.Task); // 首次调用挂 tcs1，之后挂 tcs2
        var session = CreateSession(machine, engine, out _, out _);
        var received = new List<LyricsRenderState>();
        session.LyricsChanged += (_, s) => received.Add(s);

        machine.RaiseTrackChanged();                              // gen1：挂起 tcs1
        await WaitFor(() => engine.GetCalls == 1);
        machine.Current = Snap(Track(durationSec: 200, title: "新歌"));
        machine.RaiseTrackChanged();                              // gen2：挂起 tcs2，取消 gen1
        await WaitFor(() => engine.GetCalls == 2);

        // 确定性顺序（避免依赖旧代 continuation 的调度时窗）：gen2 结果先完成 → 广播；
        // gen1 结果后完成 → 必须被丢弃（received 不新增、不被旧代覆盖）
        var gen2Result = FakeEngine.Ok with
        {
            Document = new LyricDocument
            {
                Source = LyricsSourceKind.QQMusic,
                Lines = new[] { new LyricLine(TimeSpan.Zero, null, "gen2-line", null, null) },
            },
        };
        tcs2.SetResult(gen2Result);
        await WaitFor(() => received.Count >= 1);
        Assert.Equal("gen2-line", received[0].Document!.Lines[0].Text);

        tcs1.SetResult(FakeEngine.Ok);                            // gen1 结果完成 → 应被丢弃
        await Task.Delay(300);
        Assert.Single(received);                                  // 无新增广播（gen1 被代际守卫丢弃）
        Assert.Equal("gen2-line", received[0].Document!.Lines[0].Text); // 旧代未覆盖新代
        session.Dispose();
    }

    [Fact]
    public async Task ReloadAsync_重载当前曲目并广播()
    {
        var machine = new FakeMachine { Current = Snap(Track()) };
        var engine = new FakeEngine();
        var session = CreateSession(machine, engine, out _, out _);
        LyricsRenderState? received = null;
        session.LyricsChanged += (_, s) => received = s;

        await session.ReloadAsync();

        await WaitFor(() => engine.GetCalls == 1);
        await WaitFor(() => received is not null);
        session.Dispose();
    }

    [Fact]
    public async Task ReloadAsync_无当前曲目_跳过不加载()
    {
        var machine = new FakeMachine { Current = null };
        var engine = new FakeEngine();
        var session = CreateSession(machine, engine, out _, out _);

        await session.ReloadAsync();

        Assert.Equal(0, engine.GetCalls);
        session.Dispose();
    }

    [Fact]
    public async Task UnbindAsync_删除正文与负缓存条目()
    {
        var machine = new FakeMachine { Current = Snap(Track(durationSec: 269, title: "晴天", artist: "周杰伦")) };
        var engine = new FakeEngine();
        var session = CreateSession(machine, engine, out var cache, out _);
        var key = CacheKey.Compute(new CleanedTrack("晴天", new[] { "周杰伦" }), 269);
        cache.Seed(new CachedLyrics(key, "raw", null, LyricsSourceKind.QQMusic, false, 0, T0));
        cache.SeedNegative(key);

        var ok = await session.UnbindAsync();

        Assert.True(ok);
        Assert.Null(await cache.TryGetAsync(key));
        Assert.False(await cache.IsNegativeCachedAsync(key));
        session.Dispose();
    }

    [Fact]
    public async Task UnbindAsync_缓存异常_返回false()
    {
        var machine = new FakeMachine { Current = Snap(Track()) };
        var broken = new LyricsSession(machine, new FakeCleaner(), new FakeEngine(), new ThrowingCache());
        try
        {
            Assert.False(await broken.UnbindAsync());
        }
        finally
        {
            broken.Dispose();
        }
    }

    [Fact]
    public async Task ReplaceEngine_换引擎并立即重载当前曲目()
    {
        var machine = new FakeMachine { Current = Snap(Track()) };
        var oldEngine = new FakeEngine();
        var session = CreateSession(machine, oldEngine, out _, out _);
        var newEngine = new FakeEngine();
        LyricsRenderState? received = null;
        session.LyricsChanged += (_, s) => received = s;

        session.ReplaceEngine(newEngine);

        Assert.Equal(0, oldEngine.GetCalls);
        await WaitFor(() => newEngine.GetCalls == 1); // 新引擎立即重载（加载链路异步）
        await WaitFor(() => received is not null);
        session.Dispose();
    }

    [Fact]
    public async Task ReplacePlayback_换绑后仅新machine驱动加载()
    {
        var machine1 = new FakeMachine { Current = Snap(Track()) };
        var engine = new FakeEngine();
        var session = CreateSession(machine1, engine, out _, out _);
        var machine2 = new FakeMachine { Current = Snap(Track(durationSec: 200, title: "新机曲目")) };

        session.ReplacePlayback(machine2);

        machine1.RaiseTrackChanged(); // 旧 machine 已退订：不触发
        Assert.Equal(0, engine.GetCalls);

        machine2.RaiseTrackChanged(); // 新 machine 驱动加载
        await WaitFor(() => engine.GetCalls == 1);
        session.Dispose();
    }

    [Fact]
    public async Task 加载异常_广播Error非空()
    {
        var machine = new FakeMachine { Current = Snap(Track()) };
        var engine = new FakeEngine(_ => throw new InvalidOperationException("boom"));
        var session = CreateSession(machine, engine, out _, out _);
        LyricsRenderState? received = null;
        session.LyricsChanged += (_, s) => received = s;

        machine.RaiseTrackChanged();

        await WaitFor(() => received is not null);
        Assert.NotNull(received!.Error);
        Assert.Equal(MatchConfidence.None, received.Confidence);
        Assert.Null(received.Document);
        session.Dispose();
    }

    [Fact]
    public async Task BindLyricsAsync_清洗并转发引擎绑定()
    {
        var machine = new FakeMachine { Current = Snap(Track()) };
        var engine = new FakeEngine();
        var session = CreateSession(machine, engine, out _, out var cleaner);
        var selected = new SourceSearchResult(LyricsSourceKind.QQMusic, "song-1", "晴天", new[] { "周杰伦" }, null, 269);

        var result = await session.BindLyricsAsync(selected);

        Assert.Equal(1, engine.BindCalls);
        Assert.Equal(1, cleaner.CleanCalls);
        Assert.Equal(MatchConfidence.High, result.Confidence);
        session.Dispose();
    }

    [Fact]
    public void Dispose_后退订不再响应TrackChanged()
    {
        var machine = new FakeMachine { Current = Snap(Track()) };
        var engine = new FakeEngine();
        var session = CreateSession(machine, engine, out _, out _);

        session.Dispose();
        machine.RaiseTrackChanged();

        Assert.Equal(0, engine.GetCalls);
    }

    // ---------------- TASK-603：F11 歌词偏移（±5s，随缓存持久化） ----------------

    /// <summary>
    /// 冻结样例：某句原 Start=18s，人声 13s 就该唱（歌词慢 5s）。点 +5s 后 OffsetSec=5，
    /// position=13 时 effectivePosition=18 → 命中该句 = 提前 5s 显示；-5s 反向（延后）。
    /// 渲染公式：effectivePosition = position + OffsetSec，再经 TimelineNavigator 二分定位。
    /// </summary>
    [Fact]
    public void 偏移定位_加5秒提前命中_减5秒延后命中()
    {
        var doc = new LyricDocument
        {
            Source = LyricsSourceKind.QQMusic,
            Lines = new[]
            {
                new LyricLine(TimeSpan.FromSeconds(10), null, "A", null, null),
                new LyricLine(TimeSpan.FromSeconds(18), null, "B", null, null),
                new LyricLine(TimeSpan.FromSeconds(25), null, "C", null, null),
            },
        };

        Assert.Equal(1, TimelineNavigator.FindCurrentLineIndex(doc, TimeSpan.FromSeconds(13 + 5))); // +5s：position=13 → effective=18 → B 提前显示
        Assert.Equal(1, TimelineNavigator.FindCurrentLineIndex(doc, TimeSpan.FromSeconds(23 - 5))); // -5s：position=23 → effective=18 → B 延后显示
        Assert.Equal(0, TimelineNavigator.FindCurrentLineIndex(doc, TimeSpan.FromSeconds(13)));       // 无偏移对照组：position=13 → 仍 A
    }

    [Fact]
    public async Task AdjustOffset_累加并广播与持久化()
    {
        var machine = new FakeMachine { Current = Snap(Track()) };
        var engine = new FakeEngine();
        var session = CreateSession(machine, engine, out var cache, out _);
        // F11 真实场景：当前曲目歌词已在正文缓存（重播命中/新取词落盘后），偏移持久化目标为该条目。
        // FakeEngine 不走缓存，故手动 seed（UpdateOffsetAsync 对无条目 no-op，测不到持久化）。
        var key = CacheKey.Compute(new CleanedTrack("晴天", new[] { "周杰伦" }), 269);
        cache.Seed(new CachedLyrics(key, "raw", null, LyricsSourceKind.QQMusic, false, 0, T0));
        var received = new List<LyricsRenderState>();
        session.LyricsChanged += (_, s) => received.Add(s);

        machine.RaiseTrackChanged(); // 加载成功（新取词 OffsetSec=0）
        await WaitFor(() => received.Count >= 1);

        session.AdjustOffset(+5); // 歌词提前 5s
        session.AdjustOffset(-3); // 再延后 3s → 净 +2

        Assert.Equal(3, received.Count);          // 初始 + 两次 AdjustOffset 广播（同步）
        Assert.Equal(5, received[1].OffsetSec);   // 累加语义：+5
        Assert.Equal(2, received[2].OffsetSec);   // 累加语义：+5-3

        // 持久化：缓存条目 OffsetSec 已更新（PersistOffsetAsync fire-and-forget，轮询等落盘）
        await WaitForAsync(async () => (await cache.TryGetAsync(key))?.OffsetSec == 2);
        session.Dispose();
    }

    [Fact]
    public async Task 加载后渲染状态_透传引擎偏移()
    {
        // 缓存命中语义：引擎返回 OffsetSec=3（既有偏移）→ session 广播带 3，窗口据此定位
        var machine = new FakeMachine { Current = Snap(Track()) };
        var engine = new FakeEngine(_ => Task.FromResult(FakeEngine.Ok with { OffsetSec = 3 }));
        var session = CreateSession(machine, engine, out _, out _);
        LyricsRenderState? received = null;
        session.LyricsChanged += (_, s) => received = s;

        machine.RaiseTrackChanged();
        await WaitFor(() => received is not null);

        Assert.Equal(3, received!.OffsetSec);
        session.Dispose();
    }

    [Fact]
    public async Task AdjustOffset_无歌词_静默不广播不写缓存()
    {
        // 负缓存/无词：引擎返回 None（Document null）→ AdjustOffset 静默 no-op（UI 也不显示入口）
        var machine = new FakeMachine { Current = Snap(Track()) };
        var engine = new FakeEngine(_ => Task.FromResult(
            new LyricsResult(null, null, MatchConfidence.None, false, false, 0)));
        var session = CreateSession(machine, engine, out var cache, out _);
        var broadcasts = 0;
        session.LyricsChanged += (_, _) => broadcasts++;

        machine.RaiseTrackChanged();
        await WaitFor(() => broadcasts >= 1); // None 广播（无词）

        session.AdjustOffset(+5);
        await Task.Delay(100);
        Assert.Equal(1, broadcasts); // 无词：不广播
        Assert.Empty(cache.Stored);  // 无词不写正文缓存 → UpdateOffsetAsync no-op（无条目）
        session.Dispose();
    }

    [Fact]
    public async Task 切歌后偏移重置_新曲目从零开始()
    {
        var machine = new FakeMachine { Current = Snap(Track(durationSec: 269, title: "旧歌")) };
        var engine = new FakeEngine();
        var session = CreateSession(machine, engine, out _, out _);
        var received = new List<LyricsRenderState>();
        session.LyricsChanged += (_, s) => received.Add(s);

        machine.RaiseTrackChanged();
        await WaitFor(() => received.Count >= 1);
        session.AdjustOffset(+5);
        Assert.Equal(5, received[^1].OffsetSec);

        // 切歌：偏移态清空（重播同曲才经缓存恢复）
        machine.Current = Snap(Track(durationSec: 200, title: "新歌"));
        machine.RaiseTrackChanged();
        await WaitFor(() => received.Count >= 3); // 旧(0) + 偏移(5) + 新歌加载(0)
        Assert.Equal(0, received[^1].OffsetSec);
        session.Dispose();
    }

    // ---------------- TASK-604 B：失败原因透传 ----------------

    [Fact]
    public async Task 加载全失败_渲染状态透传失败明细()
    {
        // 自动搜索全失败 → session 广播的 LyricsRenderState 带 Failures（窗口状态条显示逐源原因汇总）
        var machine = new FakeMachine { Current = Snap(Track()) };
        var engine = new FakeEngine(_ => Task.FromResult(new LyricsResult(null, null, MatchConfidence.None, false, false, 0,
            new[] { new SourceFailureDetail(LyricsSourceKind.QQMusic, SourceFailureStage.Search, "无数据") })));
        var session = CreateSession(machine, engine, out _, out _);
        LyricsRenderState? received = null;
        session.LyricsChanged += (_, s) => received = s;

        machine.RaiseTrackChanged();
        await WaitFor(() => received is not null);

        Assert.Equal(MatchConfidence.None, received!.Confidence);
        var failure = Assert.Single(received.Failures!);
        Assert.Equal(LyricsSourceKind.QQMusic, failure.Source);
        Assert.Equal(SourceFailureStage.Search, failure.Stage);
        Assert.Equal("无数据", failure.Reason);
        session.Dispose();
    }
}

/// <summary>可手动驱动的 IPlaybackStateMachine（position 渲染职责不在 session，测试只驱动 TrackChanged）。</summary>
internal sealed class FakeMachine : IPlaybackStateMachine
{
    public PlaybackSnapshot? Current { get; set; }
    public TimeSpan GetInterpolatedPosition() => TimeSpan.Zero;
    public event EventHandler? TrackChanged;
    public event EventHandler? PlayStateChanged;

    public void RaiseTrackChanged() => TrackChanged?.Invoke(this, EventArgs.Empty);
}

/// <summary>纯函数 stub：原样返回，记录调用次数。</summary>
internal sealed class FakeCleaner : ITitleCleaner
{
    public int CleanCalls { get; private set; }

    public CleanedTrack Clean(TrackInfo raw)
    {
        CleanCalls++;
        return new CleanedTrack(raw.RawTitle, new[] { raw.RawArtist });
    }

    public void LoadRules(string? path) { }
}

/// <summary>可控 ILyricsEngine：记录调用，可注入结果/异常/延迟完成（竞态测试用）。</summary>
internal sealed class FakeEngine : ILyricsEngine
{
    private readonly Func<CancellationToken, Task<LyricsResult>> _get;

    public FakeEngine(Func<CancellationToken, Task<LyricsResult>>? get = null)
    {
        _get = get ?? (_ => Task.FromResult(Ok));
    }

    public static LyricsResult Ok => new(
        new LyricDocument
        {
            Source = LyricsSourceKind.QQMusic,
            Lines = new[] { new LyricLine(TimeSpan.Zero, null, "line", null, null) },
        },
        LyricsSourceKind.QQMusic, MatchConfidence.High, false, false, 0);

    public int GetCalls { get; private set; }
    public int BindCalls { get; private set; }
    public List<string> SearchedKeywords { get; } = new();

    public Task<LyricsResult> GetLyricsAsync(CleanedTrack track, double durationSec, CancellationToken ct = default)
    {
        GetCalls++;
        return _get(ct);
    }

    public Task<ManualSearchOutcome> SearchCandidatesAsync(string keyword, CancellationToken ct = default)
    {
        SearchedKeywords.Add(keyword);
        return Task.FromResult(new ManualSearchOutcome(
            Array.Empty<SourceCandidateGroup>(), Array.Empty<SourceFailureDetail>()));
    }

    public Task<LyricsResult> BindLyricsAsync(CleanedTrack track, double durationSec, SourceSearchResult selected, CancellationToken ct = default)
    {
        BindCalls++;
        return Task.FromResult(Ok);
    }

    public IReadOnlyList<LyricsSourceKind> SourcePriority { get; } = SourcePriorityDefaults.Adr9;
}

/// <summary>UnbindAsync 抛异常的缓存（UnbindAsync 失败路径测试）；UpdateOffsetAsync 正常 no-op。</summary>
internal sealed class ThrowingCache : ILyricsCache
{
    public Task<CachedLyrics?> TryGetAsync(string cacheKey) => Task.FromResult<CachedLyrics?>(null);
    public Task<bool> IsNegativeCachedAsync(string cacheKey) => Task.FromResult(false);
    public Task StoreAsync(CachedLyrics entry) => Task.CompletedTask;
    public Task StoreNegativeAsync(string cacheKey) => Task.CompletedTask;
    public Task BindUserChoiceAsync(string cacheKey, CachedLyrics entry) => Task.CompletedTask;
    public Task UnbindAsync(string cacheKey) => throw new InvalidOperationException("boom");
    public Task UpdateOffsetAsync(string cacheKey, double offsetSec) => Task.CompletedTask;
}
