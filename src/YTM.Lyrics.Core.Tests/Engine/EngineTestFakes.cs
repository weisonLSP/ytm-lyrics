using YTM.Lyrics.Core.Cache;
using YTM.Lyrics.Core.Cleaning;
using YTM.Lyrics.Core.Engine;
using YTM.Lyrics.Core.Timeline;

namespace YTM.Lyrics.Core.Tests.Engine;

/// <summary>内存版 ILyricsCache（引擎单测用，负缓存 TTL 语义由 TASK-202 LiteDB 落地负责）。</summary>
internal sealed class FakeCache : ILyricsCache
{
    private readonly Dictionary<string, CachedLyrics> _lyrics = new();
    private readonly HashSet<string> _negative = new();

    public bool StoreNegativeCalled { get; private set; }

    public List<CachedLyrics> Stored { get; } = new();

    public void Seed(CachedLyrics entry) => _lyrics[entry.CacheKey] = entry;

    public void SeedNegative(string cacheKey) => _negative.Add(cacheKey);

    public Task<CachedLyrics?> TryGetAsync(string cacheKey)
        => Task.FromResult(_lyrics.TryGetValue(cacheKey, out var entry) ? entry : null);

    public Task<bool> IsNegativeCachedAsync(string cacheKey)
        => Task.FromResult(_negative.Contains(cacheKey));

    public Task StoreAsync(CachedLyrics entry)
    {
        // 对齐 LiteDB 语义：自动条目不覆盖用户绑定条目
        if (!(entry.UserBound || _lyrics.TryGetValue(entry.CacheKey, out var existing) && existing.UserBound))
        {
            _lyrics[entry.CacheKey] = entry;
        }

        Stored.Add(entry);
        _negative.Remove(entry.CacheKey);
        return Task.CompletedTask;
    }

    public Task StoreNegativeAsync(string cacheKey)
    {
        _negative.Add(cacheKey);
        StoreNegativeCalled = true;
        return Task.CompletedTask;
    }

    public Task BindUserChoiceAsync(string cacheKey, CachedLyrics entry)
    {
        _lyrics[cacheKey] = entry;
        _negative.Remove(cacheKey);
        return Task.CompletedTask;
    }

    public Task UnbindAsync(string cacheKey)
    {
        _lyrics.Remove(cacheKey);
        _negative.Remove(cacheKey);
        return Task.CompletedTask;
    }
}

/// <summary>
/// 可控 ISourceSearcher 假实现（TASK-203 卡片：单测全部 mock ISourceSearcher）。
/// 记录每次调用（search/fetch）的时刻与取词 ID，节流与 fallback 测试用。
/// </summary>
internal sealed class FakeSourceSearcher : ISourceSearcher
{
    private readonly Func<SourceSearchOutcome> _search;
    private readonly Func<string, SourceLyricsResult> _fetch;

    public FakeSourceSearcher(
        LyricsSourceKind source,
        Func<SourceSearchOutcome>? search = null,
        Func<string, SourceLyricsResult>? fetch = null)
    {
        Source = source;
        _search = search ?? (() => new SourceSearchOutcome(true, new List<SourceSearchResult>(), null));
        _fetch = fetch ?? (_ => new SourceLyricsResult(true, LyricsRawFormat.Lrc, "raw", null));
    }

    public LyricsSourceKind Source { get; }

    /// <summary>(调用类型, 时刻)，按发生顺序记录。</summary>
    public List<(string Kind, DateTimeOffset Time)> Calls { get; } = new();

    /// <summary>被请求取词的 SourceId 列表。</summary>
    public List<string> FetchedIds { get; } = new();

    public int SearchCount => Calls.Count(c => c.Kind == "search");

    public Task<SourceSearchOutcome> SearchAsync(CleanedTrack track, double durationSec, CancellationToken ct = default)
    {
        Calls.Add(("search", DateTimeOffset.UtcNow));
        return Task.FromResult(_search());
    }

    public Task<SourceLyricsResult> GetLyricsAsync(string sourceId, CancellationToken ct = default)
    {
        Calls.Add(("fetch", DateTimeOffset.UtcNow));
        FetchedIds.Add(sourceId);
        return Task.FromResult(_fetch(sourceId));
    }
}

/// <summary>按候选 Title 映射固定分数的打分器（阈值边界测试用）。</summary>
internal sealed class MapScorer : ISourceResultScorer
{
    private readonly Func<SourceSearchResult, SourceMatchScore> _map;

    public MapScorer(Func<SourceSearchResult, SourceMatchScore> map) => _map = map;

    public SourceMatchScore Score(CleanedTrack track, double durationSec, SourceSearchResult candidate) => _map(candidate);
}

/// <summary>
/// 解析缝 stub：把原文作为单行文本塞进 LyricDocument，并记录每次调用参数
/// （引擎测试验证「原文→文档」的传递与 formatHint 语义，真实解析行为由 TASK-204 单测覆盖）。
/// TASK-205F：<see cref="EmptyWhen"/> 按原文命中时返回空文档（模拟解析器不支持该格式/无歌词行）。
/// </summary>
internal sealed class StubLyricsDocumentParser : ILyricsDocumentParser
{
    /// <summary>原文命中谓词 → 返回 Lines=0 空文档（引擎防线测试用）。</summary>
    public Func<string, bool>? EmptyWhen { get; set; }

    public List<(string RawText, LyricsSourceKind Source, LyricsRawFormat? FormatHint)> Calls { get; } = new();

    public LyricDocument Parse(string rawText, LyricsSourceKind source, LyricsRawFormat? formatHint)
    {
        Calls.Add((rawText, source, formatHint));
        if (EmptyWhen?.Invoke(rawText) == true)
        {
            return new LyricDocument { Source = source, Lines = Array.Empty<LyricLine>() };
        }
        return new LyricDocument
        {
            Source = source,
            Lines = new[] { new LyricLine(TimeSpan.Zero, null, rawText, null, null) },
        };
    }
}

/// <summary>引擎单测公共工厂：默认 0 节流间隔（除节流测试外不引入真实等待）。</summary>
internal static class EngineTestFactory
{
    public static SourceSearchResult Candidate(string title, string id = "id", double? durationSec = null)
        => new(LyricsSourceKind.QQMusic, id, title, new[] { "Artist" }, null, durationSec);

    public static SourceSearchOutcome Ok(params SourceSearchResult[] results)
        => new(true, results.ToList(), null);

    public static SourceSearchOutcome Fail(string message = "boom")
        => new(false, null, message);

    public static SourceSearchResult High(string id = "high-id") => Candidate("high", id, 100);

    public static SourceSearchResult Low7(string id = "low7-id") => Candidate("low7", id, 100);

    public static SourceSearchResult Low11(string id = "low11-id") => Candidate("low11", id, 100);

    public static SourceSearchResult TooWeak(string id = "weak-id") => Candidate("five", id, 100);

    public static SourceSearchResult TitleGateFail(string id = "t3-id") => Candidate("t3", id, 100);

    /// <summary>默认打分表：high=12(High)、low7=7(Low)、low11=11(Low)、five=5(无效)、t3=歌名硬门槛不过、其余 0。</summary>
    public static readonly ISourceResultScorer DefaultScorer = new MapScorer(c => c.Title switch
    {
        "high" => new SourceMatchScore(5, 5, 2),   // 12 ≥ High(12)
        "low11" => new SourceMatchScore(5, 4, 2),  // 11 ∈ [6,12) → Low
        "low7" => new SourceMatchScore(5, 2, 0),   // 7  ∈ [6,12) → Low
        "five" => new SourceMatchScore(5, 0, 0),   // 5  < Low(6) → 无效
        "t3" => new SourceMatchScore(3, 6, 6),     // 歌名 3 < 硬门槛(4)，总分 15 也无效
        _ => new SourceMatchScore(0, 0, 0),
    });

    public static LyricsEngine Create(
        FakeCache cache,
        IReadOnlyDictionary<LyricsSourceKind, ISourceSearcher> searchers,
        ISourceResultScorer? scorer = null,
        StubLyricsDocumentParser? parser = null,
        TimeSpan? minSourceInterval = null)
        => new(cache, searchers, scorer ?? DefaultScorer, parser ?? new StubLyricsDocumentParser(),
            minSourceInterval: minSourceInterval ?? TimeSpan.Zero);
}
