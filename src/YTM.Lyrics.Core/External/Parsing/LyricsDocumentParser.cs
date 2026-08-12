using Lyricify.Lyrics.Models;
using YTM.Lyrics.Core.Engine;
using YTM.Lyrics.Core.Timeline;

namespace YTM.Lyrics.Core.External.Parsing;

/// <summary>
/// 解析防腐层统一入口（TDD §3.6）：Helper 各解析器 → <see cref="LyricDocument"/> 单向转换。
/// </summary>
/// <remarks>
/// 防腐层纪律吸收：
/// <list type="bullet">
/// <item>坑 1：调用方必须显式传 <see cref="LyricsRawTypes"/>。Helper 的 <c>TypeHelper.GetLyricsTypes</c>
/// 是占位实现恒返回 Unknown，单参 <c>ParseHelper.ParseLyrics</c> 永远返回 null，禁止依赖自动探测。</item>
/// <item>坑 2：QRC 行级时间由 <see cref="QrcLyricsConverter"/> 从原文重建（本层不向外暴露 Helper 类型）。</item>
/// <item>坑 4：解析器均为 Helper 同步纯函数（无 Async 版本），本层无 .Result 阻塞调用。</item>
/// </list>
/// KRC 行级时间由 Helper 保留（api-map §4.2），<see cref="KrcLyricsConverter"/> 直接消费并做
/// 防御性对齐 + 翻译合并（TASK-205F 补齐 TASK-204 扩展点）。
/// TTML / Musixmatch 留扩展点：暂落入 default 返回空文档。
/// </remarks>
public static class LyricsDocumentParser
{
    /// <summary>
    /// 解析歌词原文为统一时间轴模型。未知/暂不支持的类型返回空文档（Lines 为空）。
    /// </summary>
    public static LyricDocument Parse(string rawText, LyricsRawTypes rawType, LyricsSourceKind source)
    {
        ArgumentNullException.ThrowIfNull(rawText);

        return rawType switch
        {
            LyricsRawTypes.Qrc => QrcLyricsConverter.Convert(rawText, source),
            LyricsRawTypes.Lrc => LrcLyricsConverter.Convert(rawText, source),
            LyricsRawTypes.Krc => KrcLyricsConverter.Convert(rawText, source),
            _ => new LyricDocument { Lines = Array.Empty<LyricLine>(), Source = source },
        };
    }
}
