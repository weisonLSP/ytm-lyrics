using Lyricify.Lyrics.Models;
using YTM.Lyrics.Core.Engine;
using YTM.Lyrics.Core.Timeline;

namespace YTM.Lyrics.Core.External.Sources;

/// <summary>
/// <see cref="ILyricsDocumentParser"/> 落地：原文 → <see cref="LyricDocument"/>。
/// 桥接 TASK-204 Parsing 层（<see cref="Parsing.LyricsDocumentParser"/>），完成两件事：
///
/// 1. 格式映射：Core 内部 <see cref="LyricsRawFormat"/> → Helper <see cref="LyricsRawTypes"/>；
/// 2. 缓存回放推导（坑 #1 吸收）：CachedLyrics 未存格式，formatHint 为 null 时按源推导 +
///    内容嗅探（QQ 的 QRC/LRC 降级二义、网易云的 Yrc/Lrc 二义），禁止依赖 TypeHelper 占位探测。
///
/// 放置说明：本类属 Parsing 桥接，因 TASK-203 边界只能动 External/Sources/，临时落此目录；
/// k3 可在合入时决定是否迁往 External/Parsing/。
/// </summary>
public sealed class LyricsDocumentParserBridge : ILyricsDocumentParser
{
    public LyricDocument Parse(string rawText, LyricsSourceKind source, LyricsRawFormat? formatHint, string? translationText = null)
    {
        ArgumentNullException.ThrowIfNull(rawText);
        var rawType = formatHint is LyricsRawFormat format
            ? MapFormat(format)
            : DeriveFormatBySource(rawText, source);
        var document = Parsing.LyricsDocumentParser.Parse(rawText, rawType, source);

        // TASK-504：主歌词解析后合并翻译（按 source 分派；翻译为空/合并失败静默降级为无翻译）
        if (!string.IsNullOrWhiteSpace(translationText))
        {
            document = Parsing.TranslationMerger.Merge(document, translationText, source);
        }

        return document;
    }

    private static LyricsRawTypes MapFormat(LyricsRawFormat format) => format switch
    {
        LyricsRawFormat.Qrc => LyricsRawTypes.Qrc,
        LyricsRawFormat.Lrc => LyricsRawTypes.Lrc,
        LyricsRawFormat.Yrc => LyricsRawTypes.Yrc,
        LyricsRawFormat.Krc => LyricsRawTypes.Krc,
        LyricsRawFormat.Ttml => LyricsRawTypes.Ttml,
        LyricsRawFormat.MusixmatchJson => LyricsRawTypes.Musixmatch,
        _ => LyricsRawTypes.Unknown,
    };

    /// <summary>缓存回放路径（无格式提示）：按源 + 内容嗅探推导原文类型。</summary>
    private static LyricsRawTypes DeriveFormatBySource(string rawText, LyricsSourceKind source) => source switch
    {
        LyricsSourceKind.QQMusic => SniffQrcOrLrc(rawText),
        LyricsSourceKind.Netease => SniffYrcOrLrc(rawText),
        LyricsSourceKind.Kugou => LyricsRawTypes.Krc,
        LyricsSourceKind.AppleMusic => LyricsRawTypes.Ttml,
        LyricsSourceKind.Musixmatch => LyricsRawTypes.Musixmatch,
        _ => LyricsRawTypes.Unknown,
    };

    /// <summary>
    /// QRC/LRC 嗅探：QRC 行样式含 `文字(start,duration)` 音节标记（毫秒，如 `你(0,500)好(500,500)`）；
    /// LRC 为 `[mm:ss.xx]文字`（QQ 解密失败降级路径）。任一行含 "(" + 数字 + "," + 数字 → QRC。
    /// </summary>
    internal static LyricsRawTypes SniffQrcOrLrc(string text)
    {
        foreach (var line in text.Split('\n'))
        {
            var paren = line.IndexOf('(');
            if (paren > 0 && paren + 1 < line.Length && char.IsAsciiDigit(line[paren + 1]))
            {
                var comma = line.IndexOf(',', paren);
                if (comma > paren && comma + 1 < line.Length && char.IsAsciiDigit(line[comma + 1]))
                {
                    return LyricsRawTypes.Qrc;
                }
            }
        }

        return LyricsRawTypes.Lrc;
    }

    /// <summary>YRC 行内嵌 `<start,duration>` 词级标记；LRC 无尖括号数字。含 "<" + 数字 → YRC。</summary>
    internal static LyricsRawTypes SniffYrcOrLrc(string text)
    {
        foreach (var line in text.Split('\n'))
        {
            var lt = line.IndexOf('<');
            if (lt >= 0 && lt + 1 < line.Length && char.IsAsciiDigit(line[lt + 1]))
            {
                return LyricsRawTypes.Yrc;
            }
        }

        return LyricsRawTypes.Lrc;
    }
}
