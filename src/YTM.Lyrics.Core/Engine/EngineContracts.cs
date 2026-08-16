using YTM.Lyrics.Core.Cleaning;
using YTM.Lyrics.Core.Timeline;

namespace YTM.Lyrics.Core.Engine;

/// <summary>引擎置信度（TDD §3.3 冻结）。High=阈值达标直接返回；Low=无达标源时返回最佳候选并标 Low（UI 提示确认）；None=无结果。</summary>
public enum MatchConfidence { High, Low, None }

/// <summary>
/// 源失败阶段（TASK-604 B：失败原因透明化）。
/// Search=搜索阶段失败（无数据/无匹配候选/搜索异常）；Fetch=取词阶段失败；
/// Parse=解析空文档；NoLyrics=接口无歌词返回。
/// </summary>
public enum SourceFailureStage { Search, Fetch, Parse, NoLyrics }

/// <summary>单个源的具体失败原因（TASK-604 B；UI 据此展示「{源} {原因}」）。</summary>
/// <param name="Source">失败的源。</param>
/// <param name="Stage">失败阶段。</param>
/// <param name="Reason">具体原因（引擎侧描述，如"无数据"/"解析空文档"/error message；可能为 null）。</param>
public sealed record SourceFailureDetail(LyricsSourceKind Source, SourceFailureStage Stage, string? Reason);

/// <summary>
/// 引擎结果（TDD §3.3 冻结）。
/// </summary>
/// <param name="Document">统一歌词模型（解析后）。None/解析失败时可能为空文档。</param>
/// <param name="Source">命中来源；None 时为 null。</param>
/// <param name="Confidence">命中置信度。</param>
/// <param name="FromCache">是否来自缓存（正文命中或负缓存短路）。</param>
/// <param name="UserBound">缓存条目是否用户手动绑定（F9）。</param>
/// <param name="OffsetSec">F11 歌词偏移：正=歌词提前显示，负=延后；缓存命中透传缓存值，新取词/绑定/无结果为 0。</param>
/// <param name="Failures">TASK-604 B：失败明细（逐源收集）。正常命中路径为 null；全失败（Confidence=None）时带各源原因。</param>
public sealed record LyricsResult(
    LyricDocument? Document,
    LyricsSourceKind? Source,
    MatchConfidence Confidence,
    bool FromCache,
    bool UserBound,
    double OffsetSec,
    IReadOnlyList<SourceFailureDetail>? Failures = null);

/// <summary>
/// F9 手动搜索结果（TASK-604 B：失败源透传）。候选组 + 失败源明细，UI 渲染候选列表并在底部展示失败源。
/// </summary>
/// <param name="Groups">各源有效候选分组（已过歌名硬门槛）。</param>
/// <param name="Failures">失败的源（搜索无数据/异常/无匹配候选），正常无失败时为空列表。</param>
public sealed record ManualSearchOutcome(
    IReadOnlyList<SourceCandidateGroup> Groups,
    IReadOnlyList<SourceFailureDetail> Failures);

/// <summary>
/// F4 多源搜索引擎契约（TDD §3.3 冻结）。
/// 内部顺序：缓存命中(含用户绑定) → 负缓存检查 → 按优先级逐源搜索+打分 → 写缓存/负缓存。
/// </summary>
public interface ILyricsEngine
{
    /// <summary>获取歌词。按 <see cref="SourcePriority"/> 逐源搜索，High 达标即返回，Low 顺延保留最佳。</summary>
    Task<LyricsResult> GetLyricsAsync(CleanedTrack track, double durationSec, CancellationToken ct = default);

    /// <summary>
    /// F9 手动搜索：用户输入原始关键词（不经过 Clean 自动流程），按 <see cref="SourcePriority"/> 逐源搜索，
    /// 每源返回候选列表 + 匹配分。不取词、不写缓存；返回候选中不含 UserBound 语义（那是绑定后的存储层属性）。
    /// 单源异常/无结果顺延下一源（同 GetLyricsAsync 语义），失败源记入 <see cref="ManualSearchOutcome.Failures"/>（TASK-604 B）；
    /// 无效候选按 <see cref="MatchScoreTable.TitleHardGate"/> 过滤。
    /// </summary>
    Task<ManualSearchOutcome> SearchCandidatesAsync(string keyword, CancellationToken ct = default);

    /// <summary>
    /// F9 绑定：对选定候选取词 → 解析 → 写绑定缓存（UserBound=true）→ 返回结果。
    /// 失败（取词失败/原文空/解析空）返回 Confidence=None 的结果（不写缓存），
    /// <see cref="LyricsResult.Failures"/> 带单个失败明细（TASK-604 B：UI 显示「{源} {原因}」），调用方据此提示。
    /// </summary>
    Task<LyricsResult> BindLyricsAsync(CleanedTrack track, double durationSec, SourceSearchResult selected, CancellationToken ct = default);

    /// <summary>源优先级（默认 ADR-9，构造可注入覆盖）。</summary>
    IReadOnlyList<LyricsSourceKind> SourcePriority { get; }
}

/// <summary>
/// 单条候选打分结果（F9 手动搜索候选展示用）。
/// </summary>
/// <param name="Result">源搜索结果（含 SourceId，供绑定取词）。</param>
/// <param name="Score">三维匹配分（引擎侧用 <see cref="MatchScoreTable"/> 打分）。</param>
public sealed record ScoredCandidate(SourceSearchResult Result, SourceMatchScore Score);

/// <summary>
/// 单源候选分组（F9 手动搜索：按 <see cref="SourcePriority"/> 逐源分组展示，来源标签由 UI 呈现）。
/// </summary>
/// <param name="Source">候选来源。</param>
/// <param name="Candidates">该源有效候选（已过歌名硬门槛），按源返回顺序。</param>
public sealed record SourceCandidateGroup(LyricsSourceKind Source, IReadOnlyList<ScoredCandidate> Candidates);

/// <summary>
/// 原文 → 统一时间轴模型（<see cref="Timeline.LyricDocument"/>）解析缝。
/// 由 External 层实现（映射到 Helper 的 LyricsRawTypes / 各 Parser，吸收防腐层坑 #1），
/// 本接口只使用 Core 内部类型，Engine 不接触 Helper 类型。
/// </summary>
public interface ILyricsDocumentParser
{
    /// <summary>
    /// 解析歌词原文。两条调用路径：
    /// - 新取词（适配器显式声明了 <see cref="LyricsRawFormat"/>）：formatHint 有值，按提示解析；
    /// - 缓存回放（CachedLyrics 未存格式）：formatHint 为 null，由实现按源推导/嗅探（坑 #1 吸收）。
    /// <paramref name="translationText"/> 为翻译原文（TASK-504 承载约定：QQ/网易云为 LRC，
    /// Musixmatch 为适配器序列化 JSON），非空白时实现层调 <see cref="External.Parsing.TranslationMerger"/>
    /// 按源分派合并到每行 <see cref="Timeline.LyricLine.Translation"/>；合并失败静默降级为主歌词无翻译。
    /// </summary>
    LyricDocument Parse(string rawText, LyricsSourceKind source, LyricsRawFormat? formatHint, string? translationText = null);
}

/// <summary>
/// 单源候选三维匹配分（CompareHelper 0..7 量表：Perfect=7 VeryHigh=6 High=5 Medium=4 Low=2 NoMatch=0）。
/// 阈值判定（歌名硬门槛 / High / Low）在引擎侧，用 <see cref="MatchScoreTable"/>，此处只承载原始分数。
/// </summary>
public sealed record SourceMatchScore(int TitleScore, int ArtistScore, int DurationScore)
{
    /// <summary>总分 0..21 = 歌名 + 艺人 + 时长。</summary>
    public int TotalScore => TitleScore + ArtistScore + DurationScore;
}

/// <summary>
/// 打分器：清洗后曲目 × 源候选 → 三维分。唯一允许触碰 Helper CompareHelper 的位置
/// （External/Sources 内实现，TDD §3.3「用 Helper CompareHelper 能力」）。
/// </summary>
public interface ISourceResultScorer
{
    SourceMatchScore Score(CleanedTrack track, double durationSec, SourceSearchResult candidate);
}

/// <summary>ADR-9 默认源优先级（TASK-604 A：Apple Music 2026-08 移除，歌词 API 需特权 token；代码保留作扩展点，可显式配置启用）：QQ音乐 → 酷狗 → 网易云 → Musixmatch；汽水已移出歌词源列表。</summary>
public static class SourcePriorityDefaults
{
    public static readonly IReadOnlyList<LyricsSourceKind> Adr9 =
        new[]
        {
            LyricsSourceKind.QQMusic,
            LyricsSourceKind.Kugou,
            LyricsSourceKind.Netease,
            LyricsSourceKind.Musixmatch,
        };
}
