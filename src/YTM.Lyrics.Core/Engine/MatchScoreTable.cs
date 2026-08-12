namespace YTM.Lyrics.Core.Engine;

/// <summary>
/// 三维打分分值表（TDD §3.3「按 helper-api-map.md 定稿，k3 抽查」；High/Low 阈值集中一处，可配）。
///
/// 维度分使用 Helper CompareHelper.GetMatchScore() 量表（0..7）：
///   Perfect=7 / VeryHigh=6 / High=5 / Medium=4 / Low=2 / NoMatch=0
/// 总分 = 歌名 + 艺人 + 时长（0..21）；时长维先过 ±3s 容差门（frozen，超容差记 0 分），
/// 容差内按 CompareHelper.CompareDuration 分级（&lt;0.3s→6 / &lt;0.7s→5 / &lt;1.5s→4 / &lt;3s→2）。
///
/// 阈值设计依据（分值表定稿说明）：
/// - High=12：双 High 带时长（5+5+2=12）或歌名 Perfect+艺人 High（7+5+0=12，常见无时长结果）即达标；
///   歌名 Perfect+艺人 Medium+无时长（7+4+0=11）不达标 → 顺延，防仅歌名强但艺人存疑的错配。
/// - Low=6：歌名 Perfect 其余全缺（7+0+0=7）值得展示并提示确认；歌名 High 其余全缺（5+0+0=5）太弱不展示。
/// - TitleHardGate=4（Medium）：歌名分低于 Medium 的候选直接无效（PRD R1 错词根因防护，歌名是主键）。
/// </summary>
public sealed class MatchScoreTable
{
    /// <summary>时长容差（frozen：±3s，与 CacheKey 桶宽一致）。</summary>
    public const double DurationToleranceSec = 3.0;

    /// <summary>歌名硬门槛：低于此值候选直接无效。</summary>
    public int TitleHardGate { get; init; } = 4;

    /// <summary>High 阈值：总分 ≥ 此值 → MatchConfidence.High（达标即返回，不再顺延）。</summary>
    public int HighThreshold { get; init; } = 12;

    /// <summary>Low 阈值：过歌名硬门槛且总分 ≥ 此值 → 候选有效，置信 Low。</summary>
    public int LowThreshold { get; init; } = 6;

    /// <summary>默认分值表（引擎未注入时使用）。</summary>
    public static MatchScoreTable Default { get; } = new();
}
