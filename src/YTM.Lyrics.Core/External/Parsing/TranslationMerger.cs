using System.Diagnostics;
using System.Text.Json;
using Lyricify.Lyrics.Helpers;
using Lyricify.Lyrics.Models;
using YTM.Lyrics.Core.Engine;
using YTM.Lyrics.Core.Timeline;

namespace YTM.Lyrics.Core.External.Parsing;

/// <summary>
/// 翻译合并器（TASK-504）：把源适配器取回的 <see cref="SourceLyricsResult.TranslationText"/>
/// 按 source 分派合并进主歌词 <see cref="LyricDocument"/> 每行的 <see cref="LyricLine.Translation"/>。
///
/// 承载约定（源决定格式，见 TASK-504 约定表）：
/// - Netease / QQMusic：翻译原文为**带时间戳的 LRC**（`[mm:ss.xx]译文`）→ 时间戳对齐；
/// - Musixmatch：适配器序列化的 JSON（`[{"matchedLine":"原文行","translation":"译文"}]`）→ 行文本对齐；
/// - 酷狗 KRC：翻译已由 <see cref="KrcLyricsConverter"/> 内嵌 `[language:]` 段处理，TranslationText 恒 null。
///
/// 纪律：
/// - 解析必须显式传 <see cref="LyricsRawTypes"/>（防腐层坑 #1）；
/// - 合并失败（如 Tlyric 解析异常）**静默降级为主歌词无翻译**，不因翻译失败让整首歌词失败。
/// </summary>
public static class TranslationMerger
{
    /// <summary>主歌词行数上限防护：翻译行多于主词行/极端数据不越界。</summary>
    private const int MaxLines = 100_000;

    /// <summary>
    /// 合并翻译到主歌词文档。返回新文档（原文档不变）；无翻译/合并失败时返回原文档。
    /// </summary>
    public static LyricDocument Merge(LyricDocument document, string translationText, LyricsSourceKind source)
    {
        if (document.Lines.Count == 0 || string.IsNullOrWhiteSpace(translationText))
        {
            return document;
        }

        try
        {
            var merged = source switch
            {
                LyricsSourceKind.Netease or LyricsSourceKind.QQMusic => MergeByTimestamp(document, translationText),
                LyricsSourceKind.Musixmatch => MergeByMatchedLine(document, translationText),
                _ => null, // 其余源无翻译承载（KRC 内嵌处理）
            };

            return merged ?? document;
        }
        catch (Exception ex)
        {
            // 静默降级：翻译合并失败不影响主歌词（TASK-504 边界）
            Trace.WriteLine($"[Parsing] TranslationMerger 合并失败，降级为主歌词无翻译: source={source} {ex.GetType().Name}: {ex.Message}");
            return document;
        }
    }

    /// <summary>
    /// LRC 翻译（Netease Tlyric / QQ Trans）按时间戳对齐：
    /// 翻译行 StartTime == 主词行 Start 时填入 Translation（覆盖 null 或已有值）；
    /// 时间戳对不上的翻译行丢弃（宁可少翻译，不可错位）。
    ///
    /// TASK-504 返工裁决（raphael 2026-08-13）：双方时间戳**量化到 10ms 格点（向下取整 ms-ms%10）**
    /// 后精确匹配。QQ 主词 QRC 行标签为绝对毫秒（如 1547ms），翻译 Trans 为 LRC 百分秒（如 1540ms），
    /// 精确匹配差 1-7ms 不命中；量化后 1547→1540 与翻译 1540 命中。网易云主词 LRC 与 Tlyric 同为
    /// 百分秒（10ms 整倍数）量化不变。量化后仍为格点内精确匹配（非模糊容差）：主词 1547 只配
    /// [1540,1550) 格点内的翻译行，不会错位到 1550 的翻译。
    /// </summary>
    private static LyricDocument MergeByTimestamp(LyricDocument document, string translationText)
    {
        // 坑 #1：显式传 LyricsRawTypes.Lrc，禁止依赖 TypeHelper 占位探测
        var data = ParseHelper.ParseLyrics(translationText, LyricsRawTypes.Lrc);
        var transLines = data?.Lines;
        if (transLines is not { Count: > 0 })
        {
            return document;
        }

        // 时间戳 → 译文（同时间戳多翻译行取首条；无时间戳行丢弃；量化到 10ms 格点）
        var byStart = new Dictionary<int, string>();
        foreach (var transLine in transLines)
        {
            if (transLine.StartTime is not int startMs || byStart.Count >= MaxLines)
            {
                continue;
            }

            var text = transLine.Text?.Trim();
            if (string.IsNullOrEmpty(text))
            {
                continue;
            }

            byStart.TryAdd(startMs - startMs % 10, text);
        }

        if (byStart.Count == 0)
        {
            return document;
        }

        var lines = new List<LyricLine>(document.Lines.Count);
        foreach (var line in document.Lines)
        {
            var quantized = (int)line.Start.TotalMilliseconds - (int)line.Start.TotalMilliseconds % 10;
            if (byStart.TryGetValue(quantized, out var translation)
                && !string.IsNullOrEmpty(translation))
            {
                lines.Add(line with { Translation = translation }); // 覆盖 null 或已有值
            }
            else
            {
                lines.Add(line);
            }
        }

        return new LyricDocument { Lines = lines, Source = document.Source };
    }

    /// <summary>
    /// Musixmatch 翻译按原文行文本精确匹配：
    /// 适配器序列化 JSON `[{"matchedLine":"原文行","translation":"译文"}]`，遍历主歌词行
    /// 按 <see cref="LyricLine.Text"/>（Trim 后）精确匹配 matchedLine，命中则填 Translation。
    /// </summary>
    private static LyricDocument MergeByMatchedLine(LyricDocument document, string translationJson)
    {
        List<MatchedLineEntry>? entries;
        try
        {
            entries = JsonSerializer.Deserialize<List<MatchedLineEntry>>(translationJson);
        }
        catch (JsonException)
        {
            return document; // 中间格式损坏：降级为主歌词无翻译
        }

        if (entries is not { Count: > 0 })
        {
            return document;
        }

        // 原文行（Trim 后）→ 译文；重复条目取首条
        var byLine = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            if (string.IsNullOrWhiteSpace(entry.matchedLine) || string.IsNullOrWhiteSpace(entry.translation))
            {
                continue;
            }

            byLine.TryAdd(entry.matchedLine.Trim(), entry.translation.Trim());
        }

        if (byLine.Count == 0)
        {
            return document;
        }

        var lines = new List<LyricLine>(document.Lines.Count);
        foreach (var line in document.Lines)
        {
            if (byLine.TryGetValue(line.Text.Trim(), out var translation))
            {
                lines.Add(line with { Translation = translation }); // 覆盖 null 或已有值
            }
            else
            {
                lines.Add(line);
            }
        }

        return new LyricDocument { Lines = lines, Source = document.Source };
    }

    /// <summary>Musixmatch 中间格式条目（字段名与适配器序列化 JSON 一致，稳定契约）。</summary>
    private sealed record MatchedLineEntry(string? matchedLine, string? translation);
}
