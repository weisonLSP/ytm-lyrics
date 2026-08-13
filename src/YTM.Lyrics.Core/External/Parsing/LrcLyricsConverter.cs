using System.Text.RegularExpressions;
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
    /// credits 元数据行过滤（TASK-504 反馈 ⑤）：网易云无版权歌的 Lrc 只含
    /// 「作词/作曲/编曲/制作人/混音/母带/录音/和声/吉他/贝斯/鼓」等元数据行，非歌词正文。
    /// 匹配 `^((作词|作曲|编曲|制作人|混音|母带|录音|和声|吉他|贝斯|鼓)[:：]?\s*)`：
    /// 半角/全角冒号可选；末尾 `\s*` 容错真实数据「作词 : 山崎まさよし」——关键词与冒号间有空格
    /// （2026-08-13 直连网易云取证：One More Time One More Chance/夜曲/圣诞快乐劳伦斯先生 的 Lrc
    /// 均为 `作词 : xxx` 形态，任务卡原正则 `[:：]?` 无法命中，不修则反馈 ⑤ 无效）。
    /// 无时间戳或带时间戳的行都过滤；过滤后全空返回空文档（引擎按失败处理，不落缓存、顺延下一源）。
    /// </summary>
    private static readonly Regex CreditsLineRegex = new(
        @"^((作词|作曲|编曲|制作人|混音|母带|录音|和声|吉他|贝斯|鼓)[:：]?\s*)",
        RegexOptions.Compiled);

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
            if (CreditsLineRegex.IsMatch(text)) continue; // credits 元数据行：非歌词正文，过滤（TASK-504）

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
