using Lyricify.Lyrics.Helpers;
using Lyricify.Lyrics.Models;
using YTM.Lyrics.Core.Engine;
using YTM.Lyrics.Core.Timeline;

namespace YTM.Lyrics.Core.External.Parsing;

/// <summary>
/// LRC → LyricDocument 单向转换。
/// LRC 行级时间由 Helper 的 <see cref="Lyricify.Lyrics.Parsers.LrcParser"/> 完整保留
/// （LineInfo.StartTime 为绝对毫秒，[offset:] 已由 Helper 应用），本层只做模型映射与翻译行归并。
/// 调用解析必须显式传 <see cref="LyricsRawTypes.Lrc"/>（防腐层坑 1：单参 ParseLyrics 恒 null）。
/// </summary>
public static class LrcLyricsConverter
{
    /// <summary>
    /// 将 LRC 原文转换为统一时间轴模型。
    /// </summary>
    public static LyricDocument Convert(string lrcText, LyricsSourceKind source)
    {
        ArgumentNullException.ThrowIfNull(lrcText);

        var data = ParseHelper.ParseLyrics(lrcText, LyricsRawTypes.Lrc);
        if (data?.Lines is not { Count: > 0 } helperLines)
        {
            return new LyricDocument { Lines = Array.Empty<LyricLine>(), Source = source };
        }

        var lines = new List<LyricLine>(helperLines.Count);
        for (var i = 0; i < helperLines.Count; i++)
        {
            var helperLine = helperLines[i];
            if (helperLine.StartTime is not int startMs) continue; // 无时间戳行：跳过
            var text = helperLine.Text.Trim();
            if (text.Length == 0) continue;

            // 翻译行归并：行业通用形态为「下一行与上一行时间戳完全相同、文本为译文」
            //（QQ 音乐双行 LRC / 网易云 Lrc+Tlyric 合并）。同时间戳的第三行（如罗马音）保留为独立行，不丢数据。
            string? translation = null;
            if (i + 1 < helperLines.Count
                && helperLines[i + 1].StartTime == startMs)
            {
                var t = helperLines[i + 1].Text.Trim();
                if (t.Length > 0)
                {
                    translation = t;
                    i++; // 消费翻译行
                }
            }

            lines.Add(new LyricLine(
                TimeSpan.FromMilliseconds(startMs),
                helperLine.EndTime is int endMs ? TimeSpan.FromMilliseconds(endMs) : null,
                text, translation, null));
        }

        return new LyricDocument { Lines = lines, Source = source };
    }
}
