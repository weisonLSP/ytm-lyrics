using Lyricify.Lyrics.Providers.Web.Netease;
using YTM.Lyrics.Core.Cleaning;
using YTM.Lyrics.Core.Engine;

namespace YTM.Lyrics.Core.External.Sources;

/// <summary>
/// 网易云源适配器（ADR-9 第四顺位；硬编码 Cookie + 高频限流，api-map 坑 #5）。
/// - 搜索双接口兜底（对齐 Helper NeteaseSearcher 行为）：先 Search，Code==-460（限流）或异常时
///   切 SearchNew（eapi，可在受限网络使用）；SearchNew 已把 EapiSong 归一化为 Song，结果结构一致；
/// - 取词：Netease.Api.GetLyric(songId) → LyricResult，主歌词优先级 Yrc.Lyric → Lrc.Lyric
///   （Yrc 逐字优先，与 F5 逐字保留方向一致）；Nolyric/Uncollected 视为无词；
/// - SourceId = 搜索结果 Song.Id。
/// </summary>
public sealed class NeteaseSourceSearcher : SourceSearcherBase
{
    private readonly Api _api = new();

    public override LyricsSourceKind Source => LyricsSourceKind.Netease;

    protected override async Task<SourceSearchOutcome> SearchCoreAsync(CleanedTrack track, double durationSec, CancellationToken ct)
    {
        var (result, error) = await TrySearchAsync(_api, BuildKeyword(track)).ConfigureAwait(false);
        var songs = result?.Result?.Songs;
        if (songs is null || songs.Length == 0)
        {
            // TASK-205H：失败真因（异常/限流）进 ErrorMessage，不再笼统报"无数据"
            return new SourceSearchOutcome(false, null, $"[{Source}] 搜索无数据（{error ?? "接口无返回"}）");
        }

        var list = new List<SourceSearchResult>(songs.Length);
        foreach (var song in songs)
        {
            list.Add(new SourceSearchResult(
                Source,
                song.Id ?? string.Empty,
                song.Name ?? string.Empty,
                song.Artists?.Select(a => a.Name).ToArray() ?? Array.Empty<string>(),
                song.Album?.Name,
                song.Duration > 0 ? song.Duration / 1000.0 : null)); // Duration 单位 ms
        }

        return new SourceSearchOutcome(true, list, null);
    }

    protected override async Task<SourceLyricsResult> GetLyricsCoreAsync(string sourceId, CancellationToken ct)
    {
        var result = await _api.GetLyric(sourceId).ConfigureAwait(false);
        if (result is null || result.Nolyric || result.Uncollected)
        {
            return new SourceLyricsResult(false, LyricsRawFormat.Unknown, null, $"[{Source}] 无歌词（Nolyric/Uncollected/无返回）");
        }

        // 主歌词：Yrc（逐字）优先，缺省回退 Lrc。Tlyric（翻译）/Romalrc（罗马音）为独立字段，
        // 合并规则属 Parsing 层（TASK-204），本层只交主歌词原文（见上报决策点 D3）。
        var hasYrc = !string.IsNullOrWhiteSpace(result.Yrc?.Lyric);
        var text = hasYrc ? result.Yrc!.Lyric : result.Lrc?.Lyric;
        if (string.IsNullOrWhiteSpace(text))
        {
            return new SourceLyricsResult(false, LyricsRawFormat.Unknown, null, $"[{Source}] 歌词字段为空");
        }

        return new SourceLyricsResult(true, hasYrc ? LyricsRawFormat.Yrc : LyricsRawFormat.Lrc, text, null);
    }

    /// <summary>
    /// 搜索双接口兜底：先 Search，Code==-460（限流）或异常时切 SearchNew（eapi）。
    /// 行为与 Helper NeteaseSearcher 对齐；TASK-205H：两段失败详情都收集进返回值，
    /// 双接口全灭时调用方 ErrorMessage 可见每段真因（HTTP 状态码/异常类型/目标域名）。
    /// </summary>
    private static async Task<(SearchResult? Result, string? Error)> TrySearchAsync(Api api, string keyword)
    {
        var errors = new List<string>();
        try
        {
            var r = await api.Search(keyword, Api.SearchTypeEnum.SONG_ID).ConfigureAwait(false);
            if (r?.Code == -460)
            {
                errors.Add("Search Code=-460 限流"); // 触发切 SearchNew
            }
            else
            {
                return (r, null);
            }
        }
        catch (Exception ex)
        {
            errors.Add($"Search: {DescribeHttpError(ex, LyricsSourceKind.Netease)}");
        }

        try
        {
            return (await api.SearchNew(keyword).ConfigureAwait(false), null);
        }
        catch (Exception ex)
        {
            errors.Add($"SearchNew: {DescribeHttpError(ex, LyricsSourceKind.Netease)}");
            return (null, string.Join("; ", errors));
        }
    }
}
