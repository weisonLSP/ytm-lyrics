using System.Text.RegularExpressions;
using Lyricify.Lyrics.Parsers;
using YTM.Lyrics.Core.Engine;
using YTM.Lyrics.Core.Timeline;

namespace YTM.Lyrics.Core.External.Parsing;

/// <summary>
/// KRC → LyricDocument 单向转换（TASK-204 扩展点补齐，TASK-205F 真机根因 4 修复）。
///
/// 行级时间确认（api-map §4.2 + KrcParser 源码实证）：Helper 的 <see cref="KrcParser"/>
/// **保留 KRC 行级时间**——<c>ParseLyricsLine</c> 从行首 <c>[startMs,durationMs]</c> 标签解析
/// lineStart，音节 <c>StartTime = lineStart + 词偏移</c>（绝对毫秒）。与 QRC 的关键差异：
/// QRC 的 Helper 解析产出相对行首偏移（需重建行时间），KRC 的 Helper 解析产出**绝对毫秒**——
/// 本转换器直接信任 Helper 音节绝对时间，不做叠加；仅两处防御性对齐：
/// 1. 行 Start 以行标签 startMs 为权威（首词偏移非 0 时行开始与首音节开始不同，行标签才是行名义起点）；
/// 2. [offset:N]（毫秒）属性整体平移（KrcParser.Parse 的 LyricsData 路径应用 offset，逐行路径
///    不应用——此处补齐，避免两路径行为不一致）。
///
/// 翻译承载（api-map §5）：Helper <see cref="KrcTranslationParser"/> 解析 <c>[language:]</c>
/// Base64 JSON 得按行序翻译列表；行序与歌词行一一对应（与 KrcParser.Parse 合并规则一致，
/// 占位 "//" 表示该行无翻译）。本转换器把非空译文并入 <see cref="LyricLine.Translation"/>。
///
/// 属性行过滤与 Helper 的 <c>GetSplitedKrcWithoutInfoLine</c> 同规则（<c>[ar:] [ti:] [offset:]</c>
/// 等不以数字开头 → 丢弃）；[offset:N]（毫秒）单独解析并整体平移。
/// </summary>
public static class KrcLyricsConverter
{
    /// <summary>KRC 行标签：[startMs,durationMs]，绝对毫秒。</summary>
    private static readonly Regex LineTagRegex =
        new(@"^\[(\d+),(\d+)\]", RegexOptions.Compiled);

    /// <summary>属性行 [offset:N]（毫秒）。</summary>
    private static readonly Regex OffsetTagRegex =
        new(@"^\[offset:(-?\d+)\]$", RegexOptions.Compiled);

    /// <summary>
    /// 将已解密 KRC 纯文本转换为统一时间轴模型。
    /// </summary>
    /// <param name="krcText">已解密 KRC（解密不在本层职责，上游负责）。</param>
    /// <param name="source">歌词来源，透传到 LyricDocument。</param>
    public static LyricDocument Convert(string krcText, LyricsSourceKind source)
    {
        ArgumentNullException.ThrowIfNull(krcText);

        var translation = KrcTranslationParser.CheckKrcTranslation(krcText)
            ? KrcTranslationParser.GetTranslationFromKrc(krcText)
            : null;

        var lines = new List<LyricLine>();
        var offsetMs = 0;
        var lineIndex = 0; // 与翻译列表对齐的行序号（只统计歌词行）

        foreach (var rawLine in krcText.Split('\n'))
        {
            // 只去 \r（Split 残留），保留行内空格——KRC 空格也是音节数据（如 `Re So So` 间的空格），
            // 整行 Trim 会破坏行尾空格音节（真实样本实测：Helper 保留、Trim 后丢失，文本不一致）
            var line = rawLine.Trim('\r');
            if (line.Length == 0) continue;

            // 属性行：头部 [offset:N] 单独处理（整体平移）；其余属性行（[ar:]/[ti:]/[language:] 等）丢弃
            if (!IsLyricLine(line))
            {
                if (offsetMs == 0 && TryParseOffset(line, out var offset))
                {
                    offsetMs = offset;
                }
                continue;
            }

            var tag = TryParseLineTag(line);
            if (tag is null) continue; // 理论不可达（IsLyricLine 已保证数字开头），防御

            var (startMs, durationMs) = tag.Value;

            var syllableLine = KrcParser.ParseLyricsLine(line);
            if (syllableLine is not { Syllables.Count: > 0 })
            {
                continue; // 无音节歌词行：跳过（KRC 行必有音节，防御）
            }

            lines.Add(ConvertLyricLine(syllableLine, startMs, offsetMs, durationMs, translation, lineIndex));
            lineIndex++;
        }

        return new LyricDocument { Lines = lines, Source = source };
    }

    /// <summary>
    /// 歌词行判定（与 Helper <c>GetSplitedKrcWithoutInfoLine</c> 同规则）：
    /// 以 '[' 开头、长度 ≥ 5、第二个字符为数字（[ar:] 的 'a' / [offset:] 的 'o' 被滤除）。
    /// </summary>
    internal static bool IsLyricLine(string line)
        => line.Length >= 5 && line[0] == '[' && char.IsAsciiDigit(line[1]);

    /// <summary>解析 KRC 行首 [startMs,durationMs] 标签。</summary>
    internal static (int StartMs, int DurationMs)? TryParseLineTag(string line)
    {
        var m = LineTagRegex.Match(line);
        if (m.Success
            && int.TryParse(m.Groups[1].Value, out var start)
            && int.TryParse(m.Groups[2].Value, out var duration))
        {
            return (start, duration);
        }

        return null;
    }

    private static bool TryParseOffset(string line, out int offsetMs)
    {
        offsetMs = 0;
        var m = OffsetTagRegex.Match(line);
        return m.Success && int.TryParse(m.Groups[1].Value, out offsetMs);
    }

    private static LyricLine ConvertLyricLine(
        Lyricify.Lyrics.Models.SyllableLineInfo syllableLine,
        int startMs, int offsetMs, int durationMs,
        IReadOnlyList<string>? translation, int lineIndex)
    {
        // Helper 音节为绝对毫秒（lineStart + 词偏移，KRC 语义，api-map §4.2），直接采用 + offset 平移；
        // 行 Start 以行标签 startMs 为权威（首词偏移非 0 时行开始与首音节开始不同）
        var syllables = new List<LyricSyllable>(syllableLine.Syllables.Count);
        foreach (var s in syllableLine.Syllables)
        {
            var duration = Math.Max(0, s.EndTime - s.StartTime);
            syllables.Add(new LyricSyllable(
                TimeSpan.FromMilliseconds(s.StartTime + offsetMs),
                TimeSpan.FromMilliseconds(duration),
                s.Text));
        }

        // End：行标签 duration 为行级权威值（KRC [start,duration] 语义）
        var end = TimeSpan.FromMilliseconds(startMs + durationMs + offsetMs);

        // 翻译按行序对应（KrcParser.Parse 合并规则）；"//" 占位与空译文不承载
        string? trans = null;
        if (translation is not null && lineIndex < translation.Count)
        {
            var t = translation[lineIndex];
            if (!string.IsNullOrWhiteSpace(t) && t != "//")
            {
                trans = t;
            }
        }

        return new LyricLine(
            TimeSpan.FromMilliseconds(startMs + offsetMs), end, syllableLine.Text, trans, syllables);
    }
}
