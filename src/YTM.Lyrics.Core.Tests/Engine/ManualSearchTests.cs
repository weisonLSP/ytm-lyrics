using YTM.Lyrics.Core.Cache;
using YTM.Lyrics.Core.Cleaning;
using YTM.Lyrics.Core.Engine;

namespace YTM.Lyrics.Core.Tests.Engine;

/// <summary>
/// F9 手动搜索校正与绑定覆盖单测（TDD §3.3 扩增 / PRD S2、F9；TASK-401 卡片）。
/// 覆盖：SearchCandidatesAsync 分组候选（多源/歌名硬门槛过滤/单源失败顺延/不取词不写缓存）、
/// BindLyricsAsync 绑定写入（UserBound=true + 重播命中）、绑定失败语义（取词失败/解析空）、
/// 绑定后自动 StoreAsync 不覆盖绑定条目（回归，PRD F6 绑定永久优先）。
/// </summary>
public class ManualSearchTests
{
    private const double Dur = 100;

    private static CleanedTrack Track(string title = "Song", string artist = "Artist")
        => new(title, new[] { artist });

    private static Dictionary<LyricsSourceKind, ISourceSearcher> Searchers(params (LyricsSourceKind Source, FakeSourceSearcher Searcher)[] items)
        => items.ToDictionary(x => x.Source, x => (ISourceSearcher)x.Searcher);

    /// <summary>手动搜索打分：按候选 Title 给分（keyword="Song" 时：Song/Songs 歌名达标，Wrong 错词被硬门槛过滤）。</summary>
    private static readonly ISourceResultScorer KeywordScorer = new MapScorer(c => c.Title switch
    {
        "Song" => new SourceMatchScore(7, 5, 0),   // 歌名 Perfect + 艺人分（无时长信息 → 时长 0）
        "Songs" => new SourceMatchScore(7, 5, 0),
        "Wrong" => new SourceMatchScore(0, 0, 0),  // 歌名 NoMatch → 过不了 TitleHardGate(4)
        _ => new SourceMatchScore(0, 0, 0),
    });

    // ---------- SearchCandidatesAsync ----------

    [Fact]
    public async Task 手动搜索_返回分组候选_过滤歌名硬门槛_不取词不写缓存()
    {
        var cache = new FakeCache();
        var a = new FakeSourceSearcher(
            LyricsSourceKind.QQMusic,
            search: () => EngineTestFactory.Ok(
                EngineTestFactory.Candidate("Song", "qq-1", 240),
                EngineTestFactory.Candidate("Song", "qq-2", 241),
                EngineTestFactory.Candidate("Wrong", "qq-3", 240)));
        var b = new FakeSourceSearcher(LyricsSourceKind.Kugou, search: () => EngineTestFactory.Fail()); // 单源失败 → 顺延
        var c = new FakeSourceSearcher(
            LyricsSourceKind.AppleMusic,
            search: () => EngineTestFactory.Ok(EngineTestFactory.Candidate("Song", "am-1", 238)));
        var engine = EngineTestFactory.Create(
            cache,
            Searchers((LyricsSourceKind.QQMusic, a), (LyricsSourceKind.Kugou, b), (LyricsSourceKind.AppleMusic, c)),
            scorer: KeywordScorer);

        var groups = await engine.SearchCandidatesAsync("Song");

        // 分组：QQMusic（2 个有效候选，Wrong 被硬门槛过滤）+ AppleMusic（1）；Kugou 失败顺延
        Assert.Equal(2, groups.Count);
        var qq = Assert.Single(groups, g => g.Source == LyricsSourceKind.QQMusic);
        Assert.Equal(2, qq.Candidates.Count);
        Assert.Equal(new[] { "qq-1", "qq-2" }, qq.Candidates.Select(x => x.Result.SourceId).ToArray());
        Assert.All(qq.Candidates, x => Assert.True(x.Score.TitleScore >= 4)); // 全过歌名硬门槛
        Assert.All(qq.Candidates, x => Assert.Equal(0, x.Score.DurationScore)); // 手动搜索无时长信息

        var am = Assert.Single(groups, g => g.Source == LyricsSourceKind.AppleMusic);
        Assert.Single(am.Candidates);

        Assert.Empty(a.FetchedIds);   // 只搜索，不取词（两段式第一段）
        Assert.Empty(c.FetchedIds);
        Assert.Empty(cache.Stored);   // 不写缓存
        Assert.False(cache.StoreNegativeCalled);
    }

    [Fact]
    public async Task 手动搜索_全部无有效候选_返回空列表()
    {
        var cache = new FakeCache();
        var a = new FakeSourceSearcher(
            LyricsSourceKind.QQMusic,
            search: () => EngineTestFactory.Ok(
                EngineTestFactory.Candidate("Wrong", "qq-1", 240), // 全部过不了歌名硬门槛
                EngineTestFactory.Candidate("Wrong", "qq-2", 240)));
        var engine = EngineTestFactory.Create(cache, Searchers((LyricsSourceKind.QQMusic, a)), scorer: KeywordScorer);

        var groups = await engine.SearchCandidatesAsync("Song");

        Assert.Empty(groups); // 无有效候选 → 不产出任何分组
        Assert.Empty(a.FetchedIds);
        Assert.Empty(cache.Stored);
    }

    // ---------- BindLyricsAsync ----------

    [Fact]
    public async Task 绑定_取词写缓存_UserBound为true_重播直接命中()
    {
        var cache = new FakeCache();
        var a = new FakeSourceSearcher(
            LyricsSourceKind.QQMusic,
            search: () => EngineTestFactory.Ok(),
            fetch: _ => new SourceLyricsResult(true, LyricsRawFormat.Qrc, "bound-raw", null));
        var engine = EngineTestFactory.Create(cache, Searchers((LyricsSourceKind.QQMusic, a)));

        var selected = new SourceSearchResult(LyricsSourceKind.QQMusic, "qq-id", "Song", new[] { "Artist" }, null, 240);
        var result = await engine.BindLyricsAsync(Track(), Dur, selected);

        Assert.True(result.UserBound);
        Assert.Equal(MatchConfidence.High, result.Confidence);
        Assert.Equal(LyricsSourceKind.QQMusic, result.Source);
        Assert.Equal("bound-raw", result.Document!.Lines[0].Text);
        Assert.Equal(new[] { "qq-id" }, a.FetchedIds); // 绑定取词确实发生

        var key = CacheKey.Compute(Track(), Dur);
        var hit = await cache.TryGetAsync(key);
        Assert.NotNull(hit);
        Assert.True(hit.UserBound); // 缓存条目 UserBound=true
        Assert.Equal("bound-raw", hit.RawText);

        // 验收 4：绑定后重播同一曲 → 直接命中绑定，不再发起任何搜索（日志可见 userBound=True）
        var searchCallsBefore = a.SearchCount;
        var replay = await engine.GetLyricsAsync(Track(), Dur);
        Assert.True(replay.UserBound);
        Assert.True(replay.FromCache);
        Assert.Equal(searchCallsBefore, a.SearchCount); // 重播零搜索
    }

    [Fact]
    public async Task 绑定_取词失败_返回None_不写缓存()
    {
        var cache = new FakeCache();
        var a = new FakeSourceSearcher(
            LyricsSourceKind.QQMusic,
            search: () => EngineTestFactory.Ok(),
            fetch: _ => new SourceLyricsResult(false, LyricsRawFormat.Unknown, null, "[QQMusic] 取词失败"));
        var engine = EngineTestFactory.Create(cache, Searchers((LyricsSourceKind.QQMusic, a)));

        var selected = new SourceSearchResult(LyricsSourceKind.QQMusic, "qq-id", "Song", new[] { "Artist" }, null, 240);
        var result = await engine.BindLyricsAsync(Track(), Dur, selected);

        Assert.Equal(MatchConfidence.None, result.Confidence);
        Assert.False(result.UserBound);
        Assert.Null(result.Document);
        Assert.Null(await cache.TryGetAsync(CacheKey.Compute(Track(), Dur))); // 失败不写绑定缓存
    }

    [Fact]
    public async Task 绑定_解析空文档_返回None_不写缓存()
    {
        var cache = new FakeCache();
        var parser = new StubLyricsDocumentParser { EmptyWhen = raw => raw == "krc-empty" };
        var a = new FakeSourceSearcher(
            LyricsSourceKind.QQMusic,
            search: () => EngineTestFactory.Ok(),
            fetch: _ => new SourceLyricsResult(true, LyricsRawFormat.Krc, "krc-empty", null));
        var engine = EngineTestFactory.Create(cache, Searchers((LyricsSourceKind.QQMusic, a)), parser: parser);

        var selected = new SourceSearchResult(LyricsSourceKind.QQMusic, "qq-id", "Song", new[] { "Artist" }, null, 240);
        var result = await engine.BindLyricsAsync(Track(), Dur, selected);

        Assert.Equal(MatchConfidence.None, result.Confidence); // TASK-205F 防线：空文档不落绑定缓存
        Assert.Null(result.Document);
        Assert.Null(await cache.TryGetAsync(CacheKey.Compute(Track(), Dur)));
    }

    [Fact]
    public async Task 绑定_源未注册_返回None()
    {
        var cache = new FakeCache();
        var engine = EngineTestFactory.Create(cache, Searchers()); // 无任何源

        var selected = new SourceSearchResult(LyricsSourceKind.Musixmatch, "mx-id", "Song", new[] { "Artist" }, null, 240);
        var result = await engine.BindLyricsAsync(Track(), Dur, selected);

        Assert.Equal(MatchConfidence.None, result.Confidence);
        Assert.Null(await cache.TryGetAsync(CacheKey.Compute(Track(), Dur)));
    }

    // ---------- 回归：绑定条目优先（PRD F6） ----------

    [Fact]
    public async Task 绑定后_自动StoreAsync不覆盖绑定条目()
    {
        // 语义回归（FakeCache 与 LiteDB 对齐，TASK-401 卡片 ⑤）：绑定条目存在时自动结果不得覆盖
        var cache = new FakeCache();
        var a = new FakeSourceSearcher(
            LyricsSourceKind.QQMusic,
            search: () => EngineTestFactory.Ok(),
            fetch: _ => new SourceLyricsResult(true, LyricsRawFormat.Qrc, "bound-raw", null));
        var engine = EngineTestFactory.Create(cache, Searchers((LyricsSourceKind.QQMusic, a)));

        var selected = new SourceSearchResult(LyricsSourceKind.QQMusic, "qq-id", "Song", new[] { "Artist" }, null, 240);
        await engine.BindLyricsAsync(Track(), Dur, selected);

        // 模拟绑定后另一条自动搜索链路试图写同键（如未来自动结果晚到）：不得覆盖
        await cache.StoreAsync(new CachedLyrics(
            CacheKey.Compute(Track(), Dur), "auto-later", LyricsSourceKind.Netease, UserBound: false, 0, DateTimeOffset.UtcNow));

        var hit = await cache.TryGetAsync(CacheKey.Compute(Track(), Dur));
        Assert.Equal("bound-raw", hit!.RawText);
        Assert.True(hit.UserBound); // 绑定永久优先（PRD F6）
    }
}
