using System.Diagnostics;
using YTM.Lyrics.Core.Cache;
using YTM.Lyrics.Core.Cleaning;
using YTM.Lyrics.Core.Timeline;

namespace YTM.Lyrics.Core.Engine;

/// <summary>
/// F4 多源搜索引擎（TDD §3.3 冻结流程）：
///
///   GetLyricsAsync 内部顺序：
///   1. 正文缓存命中（含用户绑定）→ 直接返回（不再搜索）；
///   2. 负缓存检查（24h TTL，判定在读取侧）→ 返回 None，不再自动搜索（PRD S3）；
///   3. 按 <see cref="SourcePriority"/> 逐源搜索 + 打分：
///      - High 达标即返回（PRD F4「首选源匹配分达标即返回」），后续源不再搜索；
///      - Low 顺延，保留每源最佳候选；
///      - 单源异常（搜索或取词）→ 记为带源标识结果，不影响后续源；
///   4. 无 High → 全部 Low 候选按分数降序尝试取词，成功返回标 Low（UI 提示确认）；
///   5. 全部失败 → 写负缓存（24h 内不再搜），返回 None。
///
/// 节流：同源请求最小间隔 500ms，每源独立计时器（PRD F4「同源请求串行，单源最小请求间隔 500ms」），
/// 搜索与取词同源共用一个计时器。
///
/// 本类不直接触碰 Helper 类型：原文→统一模型（LyricDocument）经 <see cref="ILyricsDocumentParser"/>
/// 解析缝完成（External 实现，对接 TASK-204 Parsing 层；坑 #1 显式传类型由适配器声明的
/// <see cref="LyricsRawFormat"/> 承载，缓存回放路径由解析缝按源推导）。
/// </summary>
public sealed class LyricsEngine : ILyricsEngine
{
    /// <summary>同源最小请求间隔（PRD F4：500ms）。</summary>
    public static readonly TimeSpan DefaultMinSourceInterval = TimeSpan.FromMilliseconds(500);

    private readonly ILyricsCache _cache;
    private readonly IReadOnlyDictionary<LyricsSourceKind, ISourceSearcher> _searchers;
    private readonly ISourceResultScorer _scorer;
    private readonly ILyricsDocumentParser _parser;
    private readonly MatchScoreTable _thresholds;
    private readonly IReadOnlyDictionary<LyricsSourceKind, int> _priorityIndex;
    private readonly SourceThrottle _throttle;
    private readonly Func<DateTimeOffset> _clock;

    public IReadOnlyList<LyricsSourceKind> SourcePriority { get; }

    /// <param name="cache">缓存（正文/负缓存/用户绑定）。</param>
    /// <param name="searchers">源适配器表（未启用的源不放入即可，配置项未来落 settings）。</param>
    /// <param name="scorer">三维打分器（External/Sources 用 CompareHelper 实现）。</param>
    /// <param name="parser">原文→统一时间轴模型解析缝（External 实现，对接 TASK-204 Parsing 层）。</param>
    /// <param name="sourcePriority">源优先级，默认 ADR-9。</param>
    /// <param name="thresholds">分值表，默认 <see cref="MatchScoreTable.Default"/>。</param>
    /// <param name="minSourceInterval">同源最小请求间隔，默认 500ms。</param>
    /// <param name="clock">时钟注入（节流判定用，测试可注入）。</param>
    public LyricsEngine(
        ILyricsCache cache,
        IReadOnlyDictionary<LyricsSourceKind, ISourceSearcher> searchers,
        ISourceResultScorer scorer,
        ILyricsDocumentParser parser,
        IReadOnlyList<LyricsSourceKind>? sourcePriority = null,
        MatchScoreTable? thresholds = null,
        TimeSpan? minSourceInterval = null,
        Func<DateTimeOffset>? clock = null)
    {
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _searchers = searchers ?? throw new ArgumentNullException(nameof(searchers));
        _scorer = scorer ?? throw new ArgumentNullException(nameof(scorer));
        _parser = parser ?? throw new ArgumentNullException(nameof(parser));
        SourcePriority = sourcePriority ?? SourcePriorityDefaults.Adr9;
        _thresholds = thresholds ?? MatchScoreTable.Default;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _throttle = new SourceThrottle(minSourceInterval ?? DefaultMinSourceInterval, _clock);
        _priorityIndex = SourcePriority
            .Select((source, index) => (source, index))
            .ToDictionary(x => x.source, x => x.index);
    }

    public async Task<LyricsResult> GetLyricsAsync(CleanedTrack track, double durationSec, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(track);
        var cacheKey = CacheKey.Compute(track, durationSec);
        Trace.WriteLine($"[Engine] GetLyricsAsync: track='{track.Title}' artists=[{string.Join(", ", track.ArtistCandidates)}] duration={durationSec:F1}s key={cacheKey}");

        // 1. 正文缓存命中（含用户绑定条目）：绑定优先、直接返回，不发起任何搜索。
        //    TASK-205F 防线：缓存原文解析后 Lines==0（解析器不支持该格式/缓存损坏）不当作结果返回——
        //    空文档上 UI 是"暂无歌词"且缓存永不更新，等同失效；记日志并降级为缓存未命中继续搜索。
        var cached = await _cache.TryGetAsync(cacheKey).ConfigureAwait(false);
        if (cached is not null)
        {
            Trace.WriteLine($"[Engine] cache hit: key={cacheKey} source={cached.Source} userBound={cached.UserBound} rawLen={cached.RawText.Length}");
            var document = _parser.Parse(cached.RawText, cached.Source, formatHint: null); // 缓存未存格式 → 解析缝按源推导/嗅探
            if (document.Lines.Count == 0)
            {
                Trace.WriteLine($"[Engine] cache hit but parsed empty document, treated as miss: key={cacheKey} source={cached.Source} rawLen={cached.RawText.Length}");
            }
            else
            {
                return new LyricsResult(
                    document,
                    cached.Source,
                    MatchConfidence.High, // 缓存回放不再重新判定（见上报决策点 D2）
                    FromCache: true,
                    UserBound: cached.UserBound);
            }
        }

        // 2. 负缓存（24h）：不重复自动搜索（PRD S3）。
        if (await _cache.IsNegativeCachedAsync(cacheKey).ConfigureAwait(false))
        {
            Trace.WriteLine($"[Engine] negative cache hit: key={cacheKey} → None（24h TTL 内不再自动搜索）");
            return new LyricsResult(null, null, MatchConfidence.None, FromCache: true, UserBound: false);
        }

        // 3. 逐源搜索 + 打分 + fallback。
        var lowCandidates = new List<SourceCandidate>(); // 每源最佳 Low 候选（源优先级序）

        foreach (var source in SourcePriority)
        {
            ct.ThrowIfCancellationRequested();
            if (!_searchers.TryGetValue(source, out var searcher))
            {
                Trace.WriteLine($"[Engine] source not registered, skipped: {source}");
                continue; // 未启用的源
            }

            Trace.WriteLine($"[Engine] search start: source={source}");
            var outcome = await ThrottledSearchAsync(searcher, track, durationSec, ct).ConfigureAwait(false);
            if (!outcome.Success || outcome.Results is null || outcome.Results.Count == 0)
            {
                Trace.WriteLine($"[Engine] search end: source={source} success=false candidates=0 error={outcome.ErrorMessage ?? "no data"}");
                continue; // 单源异常/无结果 → 顺延（异常已在 outcome 中带源标识，不静默）
            }
            Trace.WriteLine($"[Engine] search end: source={source} success=true candidates={outcome.Results.Count}");

            var best = SelectBestCandidate(outcome.Results, track, durationSec, source);
            if (best is null)
            {
                Trace.WriteLine($"[Engine] no valid candidate: source={source}");
                continue; // 无有效候选（未过歌名硬门槛或总分 < Low）
            }

            Trace.WriteLine($"[Engine] best candidate: source={source} id={Truncate(best.Result.SourceId)} title='{best.Result.Title}' " +
                            $"score(T/A/D)={best.Score.TitleScore}/{best.Score.ArtistScore}/{best.Score.DurationScore} total={best.Score.TotalScore} " +
                            $"highGate={_thresholds.HighThreshold} lowGate={_thresholds.LowThreshold}");

            if (best.Score.TotalScore >= _thresholds.HighThreshold)
            {
                // High 达标即返回；取词失败则降级继续顺延下一源（不写负缓存——搜索命中过，源可能只是瞬时故障）。
                var hit = await TryFetchAndReturnAsync(best, cacheKey, MatchConfidence.High, ct).ConfigureAwait(false);
                if (hit is not null)
                {
                    Trace.WriteLine($"[Engine] result returned: confidence=High source={best.Source} fromCache=false");
                    return hit;
                }

                Trace.WriteLine($"[Engine] high fetch failed, fallthrough: source={best.Source}");
                continue;
            }

            lowCandidates.Add(best);
        }

        // 4. 无 High：Low 候选按（分数降序，源优先级升序）尝试取词。
        foreach (var candidate in lowCandidates
                     .OrderByDescending(c => c.Score.TotalScore)
                     .ThenBy(c => _priorityIndex.GetValueOrDefault(c.Source, int.MaxValue)))
        {
            Trace.WriteLine($"[Engine] low fetch start: source={candidate.Source} total={candidate.Score.TotalScore}");
            var hit = await TryFetchAndReturnAsync(candidate, cacheKey, MatchConfidence.Low, ct).ConfigureAwait(false);
            if (hit is not null)
            {
                Trace.WriteLine($"[Engine] result returned: confidence=Low source={candidate.Source} fromCache=false");
                return hit;
            }

            Trace.WriteLine($"[Engine] low fetch failed: source={candidate.Source}");
        }

        // 5. 全部失败 → 负缓存（PRD S3：同一首歌 24 小时内只自动搜索一次）。
        Trace.WriteLine($"[Engine] all sources exhausted, write negative cache: key={cacheKey}");
        await _cache.StoreNegativeAsync(cacheKey).ConfigureAwait(false);
        Trace.WriteLine($"[Engine] negative cache written: key={cacheKey}");
        return new LyricsResult(null, null, MatchConfidence.None, FromCache: false, UserBound: false);
    }

    /// <summary>
    /// F9 手动搜索：用户输入原始关键词（不经过 Clean 自动流程），按源优先级逐源搜索，
    /// 每源返回候选 + 匹配分（ScoredCandidate）。不取词、不写缓存、不产生 UserBound 语义。
    ///
    /// 打分上下文：关键词直接作为 CleanedTrack.Title，无艺人、无时长信息（durationSec=0），
    /// 故艺人/时长维度天然为 0 分，排序权重落在歌名维（用户主动搜索，主键仍是歌名）。
    /// 无效候选复用 <see cref="MatchScoreTable.TitleHardGate"/> 过滤（防错词，PRD R1）。
    /// 单源异常/无结果顺延（同 <see cref="GetLyricsAsync"/> 语义）。
    /// </summary>
    public async Task<IReadOnlyList<SourceCandidateGroup>> SearchCandidatesAsync(string keyword, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(keyword);
        var track = new CleanedTrack(keyword, Array.Empty<string>());
        const double durationSec = 0; // 手动搜索无时长信息：时长维记 0 分
        Trace.WriteLine($"[Engine] SearchCandidatesAsync: keyword='{keyword}'");

        var groups = new List<SourceCandidateGroup>();
        foreach (var source in SourcePriority)
        {
            ct.ThrowIfCancellationRequested();
            if (!_searchers.TryGetValue(source, out var searcher))
            {
                Trace.WriteLine($"[Engine] SearchCandidates source not registered, skipped: {source}");
                continue; // 未启用的源
            }

            var outcome = await ThrottledSearchAsync(searcher, track, durationSec, ct).ConfigureAwait(false);
            if (!outcome.Success || outcome.Results is null || outcome.Results.Count == 0)
            {
                Trace.WriteLine($"[Engine] SearchCandidates end: source={source} success=false candidates=0 error={outcome.ErrorMessage ?? "no data"}");
                continue; // 单源异常/无结果 → 顺延
            }

            var scored = new List<ScoredCandidate>();
            foreach (var result in outcome.Results)
            {
                var score = _scorer.Score(track, durationSec, result);
                if (score.TitleScore < _thresholds.TitleHardGate)
                {
                    Trace.WriteLine($"[Engine] SearchCandidates filtered (titleGate): source={source} id={Truncate(result.SourceId)} " +
                                    $"title='{result.Title}' titleScore={score.TitleScore} gate={_thresholds.TitleHardGate}");
                    continue; // 复用 MatchScoreTable：歌名硬门槛过滤无效候选（PRD R1 错词防护）
                }

                scored.Add(new ScoredCandidate(result, score));
            }

            if (scored.Count > 0)
            {
                groups.Add(new SourceCandidateGroup(source, scored));
                Trace.WriteLine($"[Engine] SearchCandidates group: source={source} candidates={scored.Count}");
            }
        }

        Trace.WriteLine($"[Engine] SearchCandidates done: keyword='{keyword}' groups={groups.Count} total={groups.Sum(g => g.Candidates.Count)}");
        return groups;
    }

    /// <summary>
    /// F9 绑定：对选定候选取词（同源节流）→ 解析 → 写绑定缓存（UserBound=true，永久，PRD F6）→ 返回结果。
    /// 失败语义：取词失败/原文空/解析空 → 返回 Confidence=None（不写缓存），调用方提示；不抛异常（除取消）。
    /// 绑定成功后重播该曲：GetLyricsAsync 缓存命中直接返回 UserBound=true（PRD S2 验收 4）。
    /// </summary>
    public async Task<LyricsResult> BindLyricsAsync(CleanedTrack track, double durationSec, SourceSearchResult selected, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(track);
        ArgumentNullException.ThrowIfNull(selected);
        var cacheKey = CacheKey.Compute(track, durationSec);
        Trace.WriteLine($"[Engine] BindLyricsAsync: track='{track.Title}' duration={durationSec:F1}s key={cacheKey} " +
                        $"source={selected.Source} id={Truncate(selected.SourceId)} title='{selected.Title}'");

        if (!_searchers.TryGetValue(selected.Source, out var searcher))
        {
            Trace.WriteLine($"[Engine] bind failed: source not registered: {selected.Source}");
            return new LyricsResult(null, null, MatchConfidence.None, FromCache: false, UserBound: false);
        }

        SourceLyricsResult lyrics;
        try
        {
            await _throttle.WaitAsync(selected.Source, ct).ConfigureAwait(false);
            lyrics = await searcher.GetLyricsAsync(selected.SourceId, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw; // 取消不当作绑定失败
        }
        catch (Exception ex)
        {
            lyrics = new SourceLyricsResult(false, LyricsRawFormat.Unknown, null,
                $"[{selected.Source}] {ex.GetType().Name}: {ex.Message}");
        }

        if (!lyrics.Success || string.IsNullOrEmpty(lyrics.RawText))
        {
            Trace.WriteLine($"[Engine] bind fetch failed: source={selected.Source} error={lyrics.ErrorMessage ?? "empty raw text"}");
            return new LyricsResult(null, null, MatchConfidence.None, FromCache: false, UserBound: false);
        }

        Trace.WriteLine($"[Engine] bind fetch ok: source={selected.Source} format={lyrics.Format} rawLen={lyrics.RawText.Length}");

        // TASK-205F 防线同款：解析空文档的原文不落绑定缓存（否则重播回放空文档，绑定形同虚设）
        var document = _parser.Parse(lyrics.RawText, selected.Source, lyrics.Format);
        if (document.Lines.Count == 0)
        {
            Trace.WriteLine($"[Engine] bind parsed empty document, failed: source={selected.Source} " +
                            $"format={lyrics.Format} rawLen={lyrics.RawText.Length}");
            return new LyricsResult(null, null, MatchConfidence.None, FromCache: false, UserBound: false);
        }

        Trace.WriteLine($"[Engine] bind store cache key={cacheKey} source={selected.Source} rawLen={lyrics.RawText.Length}");
        await _cache.BindUserChoiceAsync(cacheKey, new CachedLyrics(
            cacheKey,
            lyrics.RawText,
            selected.Source,
            UserBound: true,
            OffsetSec: 0,
            StoredAt: _clock().UtcDateTime)).ConfigureAwait(false);

        return new LyricsResult(
            document,
            selected.Source,
            MatchConfidence.High,
            FromCache: false,
            UserBound: true);
    }

    // ---- 内部 ----

    private async Task<SourceSearchOutcome> ThrottledSearchAsync(
        ISourceSearcher searcher, CleanedTrack track, double durationSec, CancellationToken ct)
    {
        await _throttle.WaitAsync(searcher.Source, ct).ConfigureAwait(false);
        try
        {
            return await searcher.SearchAsync(track, durationSec, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw; // 取消不当作源失败
        }
        catch (Exception ex)
        {
            return new SourceSearchOutcome(false, null, $"[{searcher.Source}] {ex.GetType().Name}: {ex.Message}");
        }
    }

    private SourceCandidate? SelectBestCandidate(
        IReadOnlyList<SourceSearchResult> results, CleanedTrack track, double durationSec, LyricsSourceKind source)
    {
        SourceCandidate? best = null;
        foreach (var result in results)
        {
            var score = _scorer.Score(track, durationSec, result);

            // 有效候选 = 过歌名硬门槛（防错词，PRD R1）且总分 ≥ Low。
            var valid = score.TitleScore >= _thresholds.TitleHardGate && score.TotalScore >= _thresholds.LowThreshold;
            Trace.WriteLine($"[Engine] candidate: source={source} id={Truncate(result.SourceId)} title='{result.Title}' " +
                            $"score(T/A/D)={score.TitleScore}/{score.ArtistScore}/{score.DurationScore} total={score.TotalScore} " +
                            $"valid={valid} (titleGate={_thresholds.TitleHardGate} lowGate={_thresholds.LowThreshold})");
            if (!valid)
            {
                continue;
            }

            if (best is null || score.TotalScore > best.Score.TotalScore)
            {
                best = new SourceCandidate(source, result, score);
            }
        }

        return best;
    }

    /// <summary>取词（同源节流）→ 成功则写缓存并返回结果；失败返回 null，调用方顺延。异常同样转失败。</summary>
    private async Task<LyricsResult?> TryFetchAndReturnAsync(
        SourceCandidate candidate, string cacheKey, MatchConfidence confidence, CancellationToken ct)
    {
        var searcher = _searchers[candidate.Source];
        SourceLyricsResult lyrics;
        try
        {
            await _throttle.WaitAsync(candidate.Source, ct).ConfigureAwait(false);
            lyrics = await searcher.GetLyricsAsync(candidate.Result.SourceId, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            lyrics = new SourceLyricsResult(false, LyricsRawFormat.Unknown, null,
                $"[{candidate.Source}] {ex.GetType().Name}: {ex.Message}");
        }

        if (!lyrics.Success || string.IsNullOrEmpty(lyrics.RawText))
        {
            Trace.WriteLine($"[Engine] fetch failed: source={candidate.Source} error={lyrics.ErrorMessage ?? "empty raw text"}");
            return null;
        }

        Trace.WriteLine($"[Engine] fetch ok: source={candidate.Source} format={lyrics.Format} rawLen={lyrics.RawText.Length}");

        // TASK-205F 防线（真机根因 4）：候选原文经解析缝产出 Lines==0 的空文档时，
        // 该候选按失败处理——不写缓存（否则缓存命中回放又得空文档，永久占用该键）、记录日志并返回 null，
        // 由调用方顺延下一候选/下一源；全部候选解析为空 → 全灭 → 调用方写负缓存（PRD S3）。
        var document = _parser.Parse(lyrics.RawText, candidate.Source, lyrics.Format);
        if (document.Lines.Count == 0)
        {
            Trace.WriteLine($"[Engine] parsed empty document, candidate failed: source={candidate.Source} " +
                            $"format={lyrics.Format} rawLen={lyrics.RawText.Length}（解析器不支持该格式/原文无歌词行）");
            return null;
        }

        Trace.WriteLine($"[Engine] store cache key={cacheKey} source={candidate.Source} rawLen={lyrics.RawText.Length}");
        await _cache.StoreAsync(new CachedLyrics(
            cacheKey,
            lyrics.RawText,
            candidate.Source,
            UserBound: false,
            OffsetSec: 0,
            StoredAt: _clock().UtcDateTime)).ConfigureAwait(false);

        return new LyricsResult(
            document,
            candidate.Source,
            confidence,
            FromCache: false,
            UserBound: false);
    }

    private sealed record SourceCandidate(LyricsSourceKind Source, SourceSearchResult Result, SourceMatchScore Score);

    /// <summary>日志辅助：SourceId 截断（酷狗 hash / Musixmatch trackId 过长）。</summary>
    private static string Truncate(string value, int max = 32)
        => string.IsNullOrEmpty(value) ? value : (value.Length <= max ? value : value[..max] + "…");

    /// <summary>
    /// 每源独立计时器：同源请求（搜索/取词）最小间隔 <see cref="LyricsEngine.DefaultMinSourceInterval"/>。
    /// 槽位预留制：请求时按「上一槽位 + 间隔」预留本次槽位，延迟到槽位时刻执行——
    /// 实际调用开始时刻的间隔恒 ≥ 间隔（若在延迟前记录时刻，被延迟调用的记录值会和上一调用
    /// 相同，下一个调用会误判无需等待，实测 1ms 间隔；本设计同时保证并发调用串行化）。
    /// Dictionary + lock 保证并发安全。
    /// </summary>
    private sealed class SourceThrottle
    {
        private readonly TimeSpan _minInterval;
        private readonly Func<DateTimeOffset> _clock;
        private readonly Dictionary<LyricsSourceKind, DateTimeOffset> _lastSlot = new();
        private readonly object _gate = new();

        public SourceThrottle(TimeSpan minInterval, Func<DateTimeOffset> clock)
        {
            _minInterval = minInterval;
            _clock = clock;
        }

        public async Task WaitAsync(LyricsSourceKind source, CancellationToken ct)
        {
            Task? gate = null;
            lock (_gate)
            {
                var now = _clock();
                if (_lastSlot.TryGetValue(source, out var last))
                {
                    var next = last + _minInterval;
                    if (now < next)
                    {
                        gate = Task.Delay(next - now, ct); // 预留槽位：本次占用 next 时刻
                        _lastSlot[source] = next;
                    }
                    else
                    {
                        _lastSlot[source] = now;
                    }
                }
                else
                {
                    _lastSlot[source] = now;
                }
            }

            if (gate is not null)
            {
                await gate.ConfigureAwait(false);
            }
        }
    }
}
