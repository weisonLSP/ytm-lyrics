using YTM.Lyrics.Core.Engine;

namespace YTM.Lyrics.Core.Timeline;

/// <summary>
/// 逐字音节（TDD §3.4 冻结）。<see cref="Start"/> 为**绝对时间轴**上的起始时刻
/// （即叠加了所在 LyricLine 行级时间后的值），与 Helper 的 QRC 相对行首语义已由防腐层归一。
/// </summary>
public sealed record LyricSyllable(TimeSpan Start, TimeSpan Duration, string Text);

/// <summary>
/// 歌词行（TDD §3.4 冻结）。
/// </summary>
/// <param name="Start">行起始时刻，**不允许为 null**（防腐层坑 2：QRC 行标签重建在此完成）。</param>
/// <param name="End">行结束时刻；LRC 等无行级结束时间的格式为 null，导航时仅用 Start。</param>
/// <param name="Text">本行歌词文本（逐字行的音节拼接；QRC 嵌套对唱括号原样保留）。</param>
/// <param name="Translation">翻译行，默认显示（PRD 决策 3）；无翻译为 null。</param>
/// <param name="Syllables">逐字时间轴，v1 数据层必须保留，渲染 P1（F10）；逐行格式为 null。</param>
public sealed record LyricLine(
    TimeSpan Start,
    TimeSpan? End,
    string Text,
    string? Translation,
    IReadOnlyList<LyricSyllable>? Syllables);

/// <summary>
/// 统一歌词模型（TDD §3.4 冻结）。Lines 必须按 <see cref="LyricLine.Start"/> 升序排列
/// （适配器保证，TimelineNavigator 的二分依赖该前置条件）。
/// </summary>
public sealed class LyricDocument
{
    public required IReadOnlyList<LyricLine> Lines { get; init; }
    public LyricsSourceKind Source { get; init; }
}
