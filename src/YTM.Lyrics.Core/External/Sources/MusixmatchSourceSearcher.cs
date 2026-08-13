using System.Text.Encodings.Web;
using System.Text.Json;
using Lyricify.Lyrics.Providers.Web.Musixmatch;
using YTM.Lyrics.Core.Cleaning;
using YTM.Lyrics.Core.Engine;

namespace YTM.Lyrics.Core.External.Sources;

/// <summary>
/// Musixmatch 源适配器（ADR-9 第五顺位；依赖网页 token 机制，api-map 坑 #10 实验源）。
/// - 搜索：Musixmatch.Api.GetTrack(track, artist, durationSec)（matcher 接口，返回单条最佳）；
/// - 取词：Musixmatch.Api.GetFullLyricsRaw(trackId) → 原始 JSON（可直接喂 MusixmatchParser）；
/// - SourceId = GetTrackResponse.Track.TrackId。自持 Api 实例以保留 user token 生命周期。
/// </summary>
public sealed class MusixmatchSourceSearcher : SourceSearcherBase
{
    private readonly Api _api = new();

    public override LyricsSourceKind Source => LyricsSourceKind.Musixmatch;

    protected override async Task<SourceSearchOutcome> SearchCoreAsync(CleanedTrack track, double durationSec, CancellationToken ct)
    {
        var primary = track.ArtistCandidates.FirstOrDefault() ?? string.Empty;
        var result = await _api
            .GetTrack(track.Title, primary, (int)Math.Round(durationSec))
            .ConfigureAwait(false);
        var matched = result?.Message?.Body?.Track;
        if (matched is null)
        {
            return new SourceSearchOutcome(false, null, $"[{Source}] GetTrack 无返回");
        }

        var list = new List<SourceSearchResult>(1)
        {
            new(
                Source,
                matched.TrackId.ToString(),
                matched.TrackName ?? string.Empty,
                SplitArtists(matched.ArtistName),
                matched.AlbumName,
                matched.TrackLength is int length && length > 0 ? length : null), // TrackLength 单位秒
        };

        return new SourceSearchOutcome(true, list, null);
    }

    protected override async Task<SourceLyricsResult> GetLyricsCoreAsync(string sourceId, CancellationToken ct)
    {
        var raw = await _api.GetFullLyricsRaw(sourceId).ConfigureAwait(false);
        if (string.IsNullOrEmpty(raw))
        {
            return new SourceLyricsResult(false, LyricsRawFormat.MusixmatchJson, null, null, $"[{Source}] 歌词无返回");
        }

        // TASK-504：翻译独立接口 GetTranslations(trackId, "zh")。
        // 取证（2026-08-13 直连真实源，Lemon trackId=147177145，translations_list=32 条）：
        // 译文字段为 translation.description（中文译文），snippet/matched_line/subtitle_matched_line
        // 三者均为原文行文本，且与 richsync 行文本（MusixmatchParser 主词行 Text）逐字一致 →
        // 合并走「原文行文本精确匹配」对齐。取词成功但翻译失败时静默降级为无翻译
        // （异常已在此吸收，不因翻译失败让整首歌词失败；Token 门禁场景实测高频触发）。
        string? translation = null;
        try
        {
            var trans = await _api.GetTranslations(sourceId, "zh").ConfigureAwait(false);
            translation = SerializeTranslations(trans?.Message?.Body?.TranslationsList);
        }
        catch (OperationCanceledException)
        {
            throw; // 取消不当作翻译失败
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"[{Source}] 翻译获取失败，降级为无翻译: {ex.GetType().Name}: {ex.Message}");
        }

        return new SourceLyricsResult(true, LyricsRawFormat.MusixmatchJson, raw, translation, null);
    }

    /// <summary>
    /// 翻译条目 → 中间格式 JSON（internal 供单测 mock 验证）。
    /// 字段名定稿（稳定契约）：matchedLine / translation；合并侧用 System.Text.Json 同规则解析。
    /// 无原文行或译文的条目丢弃；全部无效/空列表 → null（无翻译）。
    /// </summary>
    internal static string? SerializeTranslations(GetTranslationsResponse.TranslationsListItem[]? items)
    {
        if (items is not { Length: > 0 })
        {
            return null;
        }

        var pairs = new List<MatchedLineTranslation>(items.Length);
        foreach (var item in items)
        {
            var t = item.Translation;
            var line = !string.IsNullOrWhiteSpace(t.MatchedLine)
                ? t.MatchedLine
                : t.SubtitleMatchedLine;
            if (string.IsNullOrWhiteSpace(line) || string.IsNullOrWhiteSpace(t.Description))
            {
                continue; // 无原文行或无译文（如 snippets 占位）的条目丢弃
            }

            pairs.Add(new MatchedLineTranslation(line.Trim(), t.Description.Trim()));
        }

        return pairs.Count > 0 ? JsonSerializer.Serialize(pairs, JsonSerializationOptions) : null;
    }

    /// <summary>Musixmatch 翻译中间格式（适配器序列化 → TranslationMerger 按文本匹配对齐）。</summary>
    private sealed record MatchedLineTranslation(string matchedLine, string translation);

    private static readonly JsonSerializerOptions JsonSerializationOptions = new()
    {
        // 成员名已与 JSON 字段一致（matchedLine/translation）；UnsafeRelaxedJsonEscaping 让 CJK 直接输出
        //（不转义为 \uXXXX，中间格式可读、便于日志排查；存储于 LiteDB/缓存，无 HTML 注入面）
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>艺人名拆分（对齐 Helper MusixmatchSearchResult 语义：" feat. " / " & "）。</summary>
    private static string[] SplitArtists(string? artistName)
    {
        if (string.IsNullOrWhiteSpace(artistName))
        {
            return Array.Empty<string>();
        }

        return artistName.Split(new[] { " feat. ", " & " }, StringSplitOptions.RemoveEmptyEntries);
    }
}
