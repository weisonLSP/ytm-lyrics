using System.Text.RegularExpressions;
using Lyricify.Lyrics.Parsers;
using YTM.Lyrics.Core.Engine;
using YTM.Lyrics.Core.Timeline;

namespace YTM.Lyrics.Core.External.Parsing;

/// <summary>
/// QRC → LyricDocument 单向转换（防腐层坑 2 吸收点）。
/// Helper 的 <see cref="QrcParser"/> 丢弃行级时间标签（<c>ParseLyricsLine</c> 只剥离第一个 ']'
/// 之前的内容），产出行的 StartTime 仅为首音节相对值——因此**本转换器不从 Helper 取行时间**，
/// 而是从 QRC 原文自行提取行标签重建 <see cref="LyricLine.Start"/>（不允许为 null），
/// 再叠加/核对音节时间还原绝对逐字轴。
/// </summary>
public static class QrcLyricsConverter
{
    /// <summary>行标签形态 1（真实 QRC / QrcGenerator 输出）：[startMs,durationMs]。</summary>
    private static readonly Regex LineTagMsRegex =
        new(@"^\[(\d+),(\d+)\]", RegexOptions.Compiled);

    /// <summary>行标签形态 2（传统 QRC）：[mm:ss.xx]，xx 为百分秒（两位），也容忍三位毫秒。</summary>
    private static readonly Regex LineTagClockRegex =
        new(@"^\[(\d{1,3}):(\d{1,2})(?:\.(\d{1,3}))?\]", RegexOptions.Compiled);

    /// <summary>
    /// 将 QRC 原文转换为统一时间轴模型。
    /// </summary>
    /// <param name="qrcText">已解密的 QRC 纯文本（解密不在本层职责，上游负责）。</param>
    /// <param name="source">歌词来源，透传到 LyricDocument。</param>
    public static LyricDocument Convert(string qrcText, LyricsSourceKind source)
    {
        ArgumentNullException.ThrowIfNull(qrcText);

        var lines = new List<LyricLine>();
        foreach (var rawLine in qrcText.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0) continue;

            var tag = TryParseLineTag(line);
            if (tag is null) continue; // 属性行（[ti:]/[ar:]/[offset:] 等）与无标签行：跳过

            var (startMs, durationMs) = tag.Value;
            var start = TimeSpan.FromMilliseconds(startMs);

            // 音节由 Helper 解析（相对行首语义由其模型承载，我们只取原始相对值）
            var syllableLine = QrcParser.ParseLyricsLine(line);
            if (syllableLine is { Syllables.Count: > 0 })
            {
                lines.Add(ConvertLyricLine(syllableLine, startMs, durationMs));
            }
            else
            {
                // 有行标签但无音节、文本非空 → 翻译行，并入前一个歌词行（防腐层坑 2 的翻译承载）
                var text = GetTextAfterTag(line);
                if (string.IsNullOrWhiteSpace(text)) continue;
                if (lines.Count > 0 && lines[^1].Translation is null)
                {
                    lines[^1] = lines[^1] with { Translation = text };
                }
                else
                {
                    lines.Add(new LyricLine(start,
                        durationMs.HasValue ? TimeSpan.FromMilliseconds(startMs + durationMs.Value) : null,
                        text, null, null));
                }
            }
        }

        return new LyricDocument { Lines = lines, Source = source };
    }

    private static LyricLine ConvertLyricLine(
        Lyricify.Lyrics.Models.SyllableLineInfo syllableLine, int startMs, int? durationMs)
    {
        var syllables = new List<LyricSyllable>(syllableLine.Syllables.Count);

        // 逐字绝对时间判别：
        // - 形态 2（[mm:ss.xx]，无 duration）：音节为相对行首偏移（api-map 坑 6），一律叠加行标签。
        // - 形态 1（[startMs,durationMs]）：真实 QRC 音节为绝对毫秒（与行标签 start 相等，Demo 样本证实）；
        //   若手写样本以相对值给出（首音节 ≠ 行标签 start），按相对叠加处理。两者在首音节 == 行标签 start
        //   时结果相同，判别无歧义。
        var syllablesAreAbsolute = durationMs.HasValue && syllableLine.Syllables[0].StartTime == startMs;

        foreach (var s in syllableLine.Syllables)
        {
            var absStartMs = syllablesAreAbsolute ? s.StartTime : startMs + s.StartTime;
            var duration = Math.Max(0, s.EndTime - s.StartTime);
            syllables.Add(new LyricSyllable(
                TimeSpan.FromMilliseconds(absStartMs),
                TimeSpan.FromMilliseconds(duration),
                s.Text));
        }

        // End：形态 1 以行标签 duration 为准（行级权威值）；形态 2 用末音节绝对结束时刻
        var end = durationMs.HasValue
            ? TimeSpan.FromMilliseconds(startMs + durationMs.Value)
            : TimeSpan.FromMilliseconds(syllablesAreAbsolute
                ? syllableLine.Syllables[^1].EndTime
                : startMs + syllableLine.Syllables[^1].EndTime);

        return new LyricLine(
            TimeSpan.FromMilliseconds(startMs), end, syllableLine.Text, null, syllables);
    }

    /// <summary>
    /// 提取 QRC 行首时间标签。形态 1 返回 (startMs, durationMs)，形态 2 返回 (startMs, null)。
    /// 属性行（[ti:…] 等）与无标签行返回 null。
    /// </summary>
    internal static (int StartMs, int? DurationMs)? TryParseLineTag(string line)
    {
        var ms = LineTagMsRegex.Match(line);
        if (ms.Success
            && int.TryParse(ms.Groups[1].Value, out var start)
            && int.TryParse(ms.Groups[2].Value, out var duration))
        {
            return (start, duration);
        }

        var clock = LineTagClockRegex.Match(line);
        if (clock.Success
            && int.TryParse(clock.Groups[1].Value, out var minutes)
            && int.TryParse(clock.Groups[2].Value, out var seconds))
        {
            var fracGroup = clock.Groups[3];
            var fracMs = 0;
            if (fracGroup.Success && int.TryParse(fracGroup.Value, out var frac))
            {
                // 与 Helper LrcParser 相同的百分秒规则：xx 两位 → *10^1，xxx 三位 → *10^0
                fracMs = frac * (int)Math.Pow(10, 3 - fracGroup.Value.Length);
            }
            return (minutes * 60_000 + seconds * 1_000 + fracMs, null);
        }

        return null;
    }

    /// <summary>取第一个 ']' 之后的文本（与 QrcParser.ParseLyricsLine 的剥离规则一致）。</summary>
    private static string GetTextAfterTag(string line)
    {
        var idx = line.IndexOf(']');
        return idx >= 0 ? line[(idx + 1)..].Trim() : line.Trim();
    }
}
