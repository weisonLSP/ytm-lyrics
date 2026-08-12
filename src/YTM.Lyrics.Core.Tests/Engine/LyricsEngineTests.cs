using YTM.Lyrics.Core.Cache;
using YTM.Lyrics.Core.Cleaning;
using YTM.Lyrics.Core.Engine;

namespace YTM.Lyrics.Core.Tests.Engine;

/// <summary>
/// F4 多源搜索引擎单测（TDD §3.3 / PRD F4；TASK-203 卡片：单测全部 mock ISourceSearcher）。
/// 覆盖：绑定短路、缓存命中、负缓存、源 fallback 顺序、打分阈值边界、单源异常、
/// 节流间隔（同源 500ms / 异源独立）、取词失败降级、全失败写负缓存。
/// </summary>
public class LyricsEngineTests
{
    private const double Dur = 100;

    private static CleanedTrack Track(string title = "Song", string artist = "Artist")
        => new(title, new[] { artist });

    private static Dictionary<LyricsSourceKind, ISourceSearcher> Searchers(params (LyricsSourceKind Source, FakeSourceSearcher Searcher)[] items)
        => items.ToDictionary(x => x.Source, x => (ISourceSearcher)x.Searcher);

    // ---------- 缓存路径 ----------

    [Fact]
    public async Task 用户绑定条目_短路_不发起任何搜索()
    {
        var cache = new FakeCache();
        var a = new FakeSourceSearcher(LyricsSourceKind.QQMusic, search: () => EngineTestFactory.Ok(EngineTestFactory.High()));
        var parser = new StubLyricsDocumentParser();
        var engine = EngineTestFactory.Create(cache, Searchers((LyricsSourceKind.QQMusic, a)), parser: parser);

        cache.Seed(new CachedLyrics(CacheKey.Compute(Track(), Dur), "bound-raw", LyricsSourceKind.QQMusic, UserBound: true, 0, DateTimeOffset.UtcNow));

        var result = await engine.GetLyricsAsync(Track(), Dur);

        Assert.True(result.UserBound);
        Assert.True(result.FromCache);
        Assert.NotNull(result.Document);
        Assert.Equal("bound-raw", result.Document!.Lines[0].Text);
        Assert.Equal(LyricsSourceKind.QQMusic, result.Source);
        Assert.Equal(0, a.SearchCount); // 绑定条目优先级高于一切自动搜索（PRD S2）
        var call = Assert.Single(parser.Calls);
        Assert.Null(call.FormatHint); // 缓存未存格式 → 解析缝按源推导
    }

    [Fact]
    public async Task 自动缓存命中_直接返回_不发起搜索()
    {
        var cache = new FakeCache();
        var a = new FakeSourceSearcher(LyricsSourceKind.QQMusic, search: () => EngineTestFactory.Ok(EngineTestFactory.High()));
        var engine = EngineTestFactory.Create(cache, Searchers((LyricsSourceKind.QQMusic, a)));

        cache.Seed(new CachedLyrics(CacheKey.Compute(Track(), Dur), "cached-raw", LyricsSourceKind.Kugou, UserBound: false, 0, DateTimeOffset.UtcNow));

        var result = await engine.GetLyricsAsync(Track(), Dur);

        Assert.True(result.FromCache);
        Assert.False(result.UserBound);
        Assert.NotNull(result.Document);
        Assert.Equal("cached-raw", result.Document!.Lines[0].Text);
        Assert.Equal(LyricsSourceKind.Kugou, result.Source);
        Assert.Equal(0, a.SearchCount);
    }

    [Fact]
    public async Task 负缓存命中_返回None_不再自动搜索()
    {
        var cache = new FakeCache();
        var a = new FakeSourceSearcher(LyricsSourceKind.QQMusic, search: () => EngineTestFactory.Ok(EngineTestFactory.High()));
        var engine = EngineTestFactory.Create(cache, Searchers((LyricsSourceKind.QQMusic, a)));

        cache.SeedNegative(CacheKey.Compute(Track(), Dur));

        var result = await engine.GetLyricsAsync(Track(), Dur);

        Assert.Equal(MatchConfidence.None, result.Confidence);
        Assert.Null(result.Source);
        Assert.True(result.FromCache);
        Assert.Equal(0, a.SearchCount); // PRD S3：24h 内只搜一次
    }

    // ---------- fallback 顺序 ----------

    [Fact]
    public async Task 首源High_达标即返回_后续源不搜索()
    {
        var cache = new FakeCache();
        var a = new FakeSourceSearcher(LyricsSourceKind.QQMusic, search: () => EngineTestFactory.Ok(EngineTestFactory.High("qq-id")));
        var b = new FakeSourceSearcher(LyricsSourceKind.Kugou, search: () => EngineTestFactory.Ok(EngineTestFactory.High("kg-id")));
        var c = new FakeSourceSearcher(LyricsSourceKind.AppleMusic, search: () => EngineTestFactory.Ok(EngineTestFactory.High("am-id")));
        var engine = EngineTestFactory.Create(cache, Searchers((LyricsSourceKind.QQMusic, a), (LyricsSourceKind.Kugou, b), (LyricsSourceKind.AppleMusic, c)));

        var result = await engine.GetLyricsAsync(Track(), Dur);

        Assert.Equal(MatchConfidence.High, result.Confidence);
        Assert.Equal(LyricsSourceKind.QQMusic, result.Source);
        Assert.Equal(1, a.SearchCount);
        Assert.Equal(0, b.SearchCount); // PRD F4：达标即返回，不浪费后续源请求
        Assert.Equal(0, c.SearchCount);
        Assert.Equal(new[] { "qq-id" }, a.FetchedIds);
    }

    [Fact]
    public async Task 首源Low_顺延到次源High_命中次源()
    {
        var cache = new FakeCache();
        var a = new FakeSourceSearcher(LyricsSourceKind.QQMusic, search: () => EngineTestFactory.Ok(EngineTestFactory.Low7()));
        var b = new FakeSourceSearcher(LyricsSourceKind.Kugou, search: () => EngineTestFactory.Ok(EngineTestFactory.High("kg-id")));
        var c = new FakeSourceSearcher(LyricsSourceKind.AppleMusic, search: () => EngineTestFactory.Ok(EngineTestFactory.High()));
        var engine = EngineTestFactory.Create(cache, Searchers((LyricsSourceKind.QQMusic, a), (LyricsSourceKind.Kugou, b), (LyricsSourceKind.AppleMusic, c)));

        var result = await engine.GetLyricsAsync(Track(), Dur);

        Assert.Equal(MatchConfidence.High, result.Confidence);
        Assert.Equal(LyricsSourceKind.Kugou, result.Source);
        Assert.Equal(1, a.SearchCount);
        Assert.Equal(1, b.SearchCount);
        Assert.Equal(0, c.SearchCount); // b 已 High → 短路
        Assert.Equal(new[] { "kg-id" }, b.FetchedIds);
    }

    [Fact]
    public async Task 源顺序按优先级_低优先级源兜底()
    {
        var cache = new FakeCache();
        var a = new FakeSourceSearcher(LyricsSourceKind.QQMusic, search: () => EngineTestFactory.Fail());
        var b = new FakeSourceSearcher(LyricsSourceKind.Kugou, search: () => EngineTestFactory.Ok());
        var c = new FakeSourceSearcher(LyricsSourceKind.AppleMusic, search: () => EngineTestFactory.Ok(EngineTestFactory.High("am-id")));
        var engine = EngineTestFactory.Create(cache, Searchers((LyricsSourceKind.QQMusic, a), (LyricsSourceKind.Kugou, b), (LyricsSourceKind.AppleMusic, c)));

        var result = await engine.GetLyricsAsync(Track(), Dur);

        Assert.Equal(LyricsSourceKind.AppleMusic, result.Source);
        Assert.Equal(MatchConfidence.High, result.Confidence);
        Assert.Equal(1, a.SearchCount);
        Assert.Equal(1, b.SearchCount);
        Assert.Equal(1, c.SearchCount);
    }

    // ---------- 打分阈值边界 ----------

    [Fact]
    public async Task 总分12_刚好达High阈值_返回High()
    {
        var cache = new FakeCache();
        var a = new FakeSourceSearcher(LyricsSourceKind.QQMusic, search: () => EngineTestFactory.Ok(EngineTestFactory.High()));
        var engine = EngineTestFactory.Create(cache, Searchers((LyricsSourceKind.QQMusic, a)));

        var result = await engine.GetLyricsAsync(Track(), Dur);

        Assert.Equal(MatchConfidence.High, result.Confidence); // 12 ≥ HighThreshold(12)：边界达标
        Assert.Equal(LyricsSourceKind.QQMusic, result.Source);
    }

    [Fact]
    public async Task 总分11_低于High阈值_返回Low()
    {
        var cache = new FakeCache();
        var a = new FakeSourceSearcher(LyricsSourceKind.QQMusic, search: () => EngineTestFactory.Ok(EngineTestFactory.Low11()));
        var engine = EngineTestFactory.Create(cache, Searchers((LyricsSourceKind.QQMusic, a)));

        var result = await engine.GetLyricsAsync(Track(), Dur);

        Assert.Equal(MatchConfidence.Low, result.Confidence); // 11 < HighThreshold(12)：降级 Low，UI 提示确认
        Assert.Equal(LyricsSourceKind.QQMusic, result.Source);
    }

    [Fact]
    public async Task 总分7_为有效Low候选_返回Low()
    {
        var cache = new FakeCache();
        var a = new FakeSourceSearcher(LyricsSourceKind.QQMusic, search: () => EngineTestFactory.Ok(EngineTestFactory.Low7()));
        var engine = EngineTestFactory.Create(cache, Searchers((LyricsSourceKind.QQMusic, a)));

        var result = await engine.GetLyricsAsync(Track(), Dur);

        Assert.Equal(MatchConfidence.Low, result.Confidence); // 7 ≥ LowThreshold(6)：边界有效
    }

    [Fact]
    public async Task 总分5_低于Low阈值_候选无效_写负缓存()
    {
        var cache = new FakeCache();
        var a = new FakeSourceSearcher(LyricsSourceKind.QQMusic, search: () => EngineTestFactory.Ok(EngineTestFactory.TooWeak()));
        var engine = EngineTestFactory.Create(cache, Searchers((LyricsSourceKind.QQMusic, a)));

        var result = await engine.GetLyricsAsync(Track(), Dur);

        Assert.Equal(MatchConfidence.None, result.Confidence);
        Assert.True(cache.StoreNegativeCalled); // 无有效候选 → 全失败语义
    }

    [Fact]
    public async Task 歌名低于硬门槛_总分再高也无效()
    {
        var cache = new FakeCache();
        var a = new FakeSourceSearcher(LyricsSourceKind.QQMusic, search: () => EngineTestFactory.Ok(EngineTestFactory.TitleGateFail()));
        var engine = EngineTestFactory.Create(cache, Searchers((LyricsSourceKind.QQMusic, a)));

        var result = await engine.GetLyricsAsync(Track(), Dur);

        Assert.Equal(MatchConfidence.None, result.Confidence); // 歌名 3 < 硬门槛 4 → 防错词（PRD R1）
        Assert.True(cache.StoreNegativeCalled);
    }

    [Fact]
    public async Task 全部Low_按分数取最佳_返回Low()
    {
        var cache = new FakeCache();
        var a = new FakeSourceSearcher(LyricsSourceKind.QQMusic, search: () => EngineTestFactory.Ok(EngineTestFactory.Low7("qq-id")));
        var b = new FakeSourceSearcher(LyricsSourceKind.Kugou, search: () => EngineTestFactory.Ok(EngineTestFactory.Low11("kg-id")));
        var engine = EngineTestFactory.Create(cache, Searchers((LyricsSourceKind.QQMusic, a), (LyricsSourceKind.Kugou, b)));

        var result = await engine.GetLyricsAsync(Track(), Dur);

        Assert.Equal(MatchConfidence.Low, result.Confidence);
        Assert.Equal(LyricsSourceKind.Kugou, result.Source);   // 11 分 > 7 分 → 取最佳 Low
        Assert.Equal(new[] { "kg-id" }, b.FetchedIds);
        Assert.Empty(a.FetchedIds);                            // 低分候选不取词
    }

    // ---------- 异常与降级 ----------

    [Fact]
    public async Task 单源异常_不影响后续源()
    {
        var cache = new FakeCache();
        var a = new FakeSourceSearcher(LyricsSourceKind.QQMusic, search: () => EngineTestFactory.Fail());
        var b = new FakeSourceSearcher(LyricsSourceKind.Kugou, search: () => EngineTestFactory.Ok(EngineTestFactory.High("kg-id")));
        var engine = EngineTestFactory.Create(cache, Searchers((LyricsSourceKind.QQMusic, a), (LyricsSourceKind.Kugou, b)));

        var result = await engine.GetLyricsAsync(Track(), Dur);

        Assert.Equal(LyricsSourceKind.Kugou, result.Source);
        Assert.Equal(MatchConfidence.High, result.Confidence);
        Assert.False(cache.StoreNegativeCalled);
    }

    [Fact]
    public async Task 搜索命中High_但取词失败_顺延下一源()
    {
        var cache = new FakeCache();
        var a = new FakeSourceSearcher(
            LyricsSourceKind.QQMusic,
            search: () => EngineTestFactory.Ok(EngineTestFactory.High("qq-id")),
            fetch: _ => new SourceLyricsResult(false, LyricsRawFormat.Unknown, null, "[QQMusic] 取词失败"));
        var b = new FakeSourceSearcher(
            LyricsSourceKind.Kugou,
            search: () => EngineTestFactory.Ok(EngineTestFactory.High("kg-id")));
        var engine = EngineTestFactory.Create(cache, Searchers((LyricsSourceKind.QQMusic, a), (LyricsSourceKind.Kugou, b)));

        var result = await engine.GetLyricsAsync(Track(), Dur);

        Assert.Equal(LyricsSourceKind.Kugou, result.Source);   // QQ 取词失败 → 不写负缓存，继续搜酷狗
        Assert.Equal(MatchConfidence.High, result.Confidence);
        Assert.Equal(new[] { "qq-id" }, a.FetchedIds);         // QQ 确实尝试过取词
        Assert.Equal(1, b.SearchCount);
        Assert.False(cache.StoreNegativeCalled);
    }

    [Fact]
    public async Task 取词器抛出异常_等同取词失败_顺延下一源()
    {
        var cache = new FakeCache();
        var a = new FakeSourceSearcher(
            LyricsSourceKind.QQMusic,
            search: () => EngineTestFactory.Ok(EngineTestFactory.High("qq-id")),
            fetch: _ => throw new InvalidOperationException("网络异常"));
        var b = new FakeSourceSearcher(LyricsSourceKind.Kugou, search: () => EngineTestFactory.Ok(EngineTestFactory.High("kg-id")));
        var engine = EngineTestFactory.Create(cache, Searchers((LyricsSourceKind.QQMusic, a), (LyricsSourceKind.Kugou, b)));

        var result = await engine.GetLyricsAsync(Track(), Dur);

        Assert.Equal(LyricsSourceKind.Kugou, result.Source);
        Assert.Equal(MatchConfidence.High, result.Confidence);
    }

    [Fact]
    public async Task 全部失败_写负缓存_返回None()
    {
        var cache = new FakeCache();
        var a = new FakeSourceSearcher(LyricsSourceKind.QQMusic, search: () => EngineTestFactory.Fail());
        var b = new FakeSourceSearcher(LyricsSourceKind.Kugou, search: () => EngineTestFactory.Ok());
        var c = new FakeSourceSearcher(LyricsSourceKind.AppleMusic, search: () => EngineTestFactory.Ok(EngineTestFactory.TooWeak()));
        var engine = EngineTestFactory.Create(cache, Searchers((LyricsSourceKind.QQMusic, a), (LyricsSourceKind.Kugou, b), (LyricsSourceKind.AppleMusic, c)));

        var result = await engine.GetLyricsAsync(Track(), Dur);

        Assert.Equal(MatchConfidence.None, result.Confidence);
        Assert.Null(result.Document);
        Assert.False(result.FromCache);
        Assert.True(cache.StoreNegativeCalled); // PRD S3：24h 内不再自动搜索
        Assert.Empty(cache.Stored);
    }

    // ---------- 缓存写入 ----------

    [Fact]
    public async Task 搜索命中_写正文缓存_解析缝收到原文与格式提示()
    {
        var cache = new FakeCache();
        var parser = new StubLyricsDocumentParser();
        var a = new FakeSourceSearcher(
            LyricsSourceKind.Kugou,
            search: () => EngineTestFactory.Ok(EngineTestFactory.High("kg-id")),
            fetch: _ => new SourceLyricsResult(true, LyricsRawFormat.Krc, "krc-text", null));
        var engine = EngineTestFactory.Create(cache, Searchers((LyricsSourceKind.Kugou, a)), parser: parser);

        var result = await engine.GetLyricsAsync(Track(), Dur);

        Assert.NotNull(result.Document);
        Assert.Equal("krc-text", result.Document!.Lines[0].Text);
        var stored = Assert.Single(cache.Stored);
        Assert.Equal(CacheKey.Compute(Track(), Dur), stored.CacheKey);
        Assert.Equal(LyricsSourceKind.Kugou, stored.Source);
        Assert.False(stored.UserBound);
        Assert.Equal("krc-text", stored.RawText);
        Assert.Equal(LyricsSourceKind.Kugou, result.Source);
        Assert.False(result.FromCache);
        Assert.False(cache.StoreNegativeCalled);
        var call = Assert.Single(parser.Calls);
        Assert.Equal("krc-text", call.RawText);
        Assert.Equal(LyricsRawFormat.Krc, call.FormatHint); // 新取词路径：适配器显式声明格式（坑 #1）
    }

    [Fact]
    public async Task 解析空文档_候选视为失败_顺延下一源()
    {
        // TASK-205F 真机根因 4：KRC 原文落入 default 空文档 → 引擎不得把 lines=0 当 High 返回。
        // mock：QQ 取回 KRC（解析空），酷狗取回 LRC（解析非空）→ 顺延命中酷狗。
        var cache = new FakeCache();
        var parser = new StubLyricsDocumentParser { EmptyWhen = raw => raw == "krc-empty" };
        var a = new FakeSourceSearcher(
            LyricsSourceKind.QQMusic,
            search: () => EngineTestFactory.Ok(EngineTestFactory.High("qq-id")),
            fetch: _ => new SourceLyricsResult(true, LyricsRawFormat.Krc, "krc-empty", null));
        var b = new FakeSourceSearcher(
            LyricsSourceKind.Kugou,
            search: () => EngineTestFactory.Ok(EngineTestFactory.High("kg-id")),
            fetch: _ => new SourceLyricsResult(true, LyricsRawFormat.Lrc, "lrc-ok", null));
        var engine = EngineTestFactory.Create(cache, Searchers((LyricsSourceKind.QQMusic, a), (LyricsSourceKind.Kugou, b)), parser: parser);

        var result = await engine.GetLyricsAsync(Track(), Dur);

        Assert.Equal(LyricsSourceKind.Kugou, result.Source);   // QQ 空文档 → 失败顺延
        Assert.Equal(MatchConfidence.High, result.Confidence);
        Assert.Equal(new[] { "qq-id" }, a.FetchedIds);         // QQ 确实尝试过取词
        Assert.Equal(new[] { "kg-id" }, b.FetchedIds);
        Assert.False(cache.StoreNegativeCalled);               // 非全灭：不写负缓存
        var stored = Assert.Single(cache.Stored);              // 只缓存解析成功的原文
        Assert.Equal("lrc-ok", stored.RawText);
    }

    [Fact]
    public async Task 全部候选解析空_全灭_写负缓存_不写正文缓存()
    {
        var cache = new FakeCache();
        var parser = new StubLyricsDocumentParser { EmptyWhen = _ => true }; // 所有原文都解析空
        var a = new FakeSourceSearcher(
            LyricsSourceKind.QQMusic,
            search: () => EngineTestFactory.Ok(EngineTestFactory.High("qq-id")),
            fetch: _ => new SourceLyricsResult(true, LyricsRawFormat.Krc, "krc-empty", null));
        var b = new FakeSourceSearcher(
            LyricsSourceKind.Kugou,
            search: () => EngineTestFactory.Ok(EngineTestFactory.High("kg-id")),
            fetch: _ => new SourceLyricsResult(true, LyricsRawFormat.Lrc, "lrc-empty", null));
        var engine = EngineTestFactory.Create(cache, Searchers((LyricsSourceKind.QQMusic, a), (LyricsSourceKind.Kugou, b)), parser: parser);

        var result = await engine.GetLyricsAsync(Track(), Dur);

        Assert.Equal(MatchConfidence.None, result.Confidence); // 全灭语义
        Assert.Null(result.Document);
        Assert.True(cache.StoreNegativeCalled);                // PRD S3：24h 内不再自动搜索
        Assert.Empty(cache.Stored);                            // 空文档原文不写正文缓存（防缓存回放空文档）
    }

    [Fact]
    public async Task 缓存命中但解析空文档_视为未命中_继续搜索()
    {
        var cache = new FakeCache();
        var parser = new StubLyricsDocumentParser { EmptyWhen = raw => raw == "stale-empty" };
        var a = new FakeSourceSearcher(
            LyricsSourceKind.QQMusic,
            search: () => EngineTestFactory.Ok(EngineTestFactory.High("qq-id")),
            fetch: _ => new SourceLyricsResult(true, LyricsRawFormat.Lrc, "fresh-ok", null));
        var engine = EngineTestFactory.Create(cache, Searchers((LyricsSourceKind.QQMusic, a)), parser: parser);

        // 缓存里是旧版本解析器落盘的 KRC（现在解析空）→ 不应短路返回空文档
        cache.Seed(new CachedLyrics(CacheKey.Compute(Track(), Dur), "stale-empty", LyricsSourceKind.Kugou, UserBound: false, 0, DateTimeOffset.UtcNow));

        var result = await engine.GetLyricsAsync(Track(), Dur);

        Assert.Equal(LyricsSourceKind.QQMusic, result.Source);  // 缓存视为未命中 → 继续搜索
        Assert.False(result.FromCache);
        Assert.Equal(MatchConfidence.High, result.Confidence);
        Assert.Equal(1, a.SearchCount);
        Assert.Equal("fresh-ok", result.Document!.Lines[0].Text);
    }

    // ---------- 节流 ----------

    [Fact]
    public async Task 节流_同源连续请求_间隔不小于500ms()
    {
        var cache = new FakeCache();
        var a = new FakeSourceSearcher(
            LyricsSourceKind.QQMusic,
            search: () => EngineTestFactory.Ok(EngineTestFactory.High()),
            fetch: _ => new SourceLyricsResult(false, LyricsRawFormat.Unknown, null, "取词失败（本测试只验证节流间隔）"));
        // 真实 500ms 节流：第一首 search→fetch 各占一个节流槽；第二首（不同键）再触发同源 search
        var engine = new LyricsEngine(
            cache,
            Searchers((LyricsSourceKind.QQMusic, a)),
            EngineTestFactory.DefaultScorer,
            new StubLyricsDocumentParser(),
            minSourceInterval: LyricsEngine.DefaultMinSourceInterval);

        await engine.GetLyricsAsync(Track("One"), Dur);
        await engine.GetLyricsAsync(Track("Two"), Dur);

        Assert.True(a.Calls.Count >= 2, "同源至少发生两次调用");
        // 容差说明（TASK-205H 实测）：Windows threadpool timer 按系统 tick（~15.6ms）向下取整到期时间，
        // Task.Delay(500-δ) 可提前最多 ~15.6ms 触发，理论下限 ≈ 484.4ms；断言 480 留足取整余量，
        // 仍严格验证 500ms 节流语义（490 的 10ms 余量在并行跑套件时会偶发误报）。
        for (var i = 1; i < a.Calls.Count; i++)
        {
            var gap = a.Calls[i].Time - a.Calls[i - 1].Time;
            Assert.True(gap >= TimeSpan.FromMilliseconds(480),
                $"同源第 {i} 次调用间隔 {gap.TotalMilliseconds:F0}ms < 500ms（容差内）");
        }
    }

    [Fact]
    public async Task 节流_不同源互不阻塞()
    {
        var cache = new FakeCache();
        var a = new FakeSourceSearcher(LyricsSourceKind.QQMusic, search: () => EngineTestFactory.Ok(EngineTestFactory.Low7()));
        var b = new FakeSourceSearcher(LyricsSourceKind.Kugou, search: () => EngineTestFactory.Ok(EngineTestFactory.High("kg-id")));
        var engine = new LyricsEngine(
            cache,
            Searchers((LyricsSourceKind.QQMusic, a), (LyricsSourceKind.Kugou, b)),
            EngineTestFactory.DefaultScorer,
            new StubLyricsDocumentParser(),
            minSourceInterval: LyricsEngine.DefaultMinSourceInterval);

        await engine.GetLyricsAsync(Track(), Dur);

        // A 先搜（Low）→ B 紧接搜索：B 的搜索不应因 A 的计时器等待
        var aSearch = a.Calls[0].Time;
        var bSearch = b.Calls[0].Time;
        Assert.True(bSearch - aSearch < TimeSpan.FromMilliseconds(300),
            $"异源 B 的搜索被 A 的计时器拖慢（{(bSearch - aSearch).TotalMilliseconds:F0}ms）");
    }
}
