using Lyricify.Lyrics.Decrypter.Krc;
using Lyricify.Lyrics.Providers.Web.Kugou;
using YTM.Lyrics.Core.Cleaning;
using YTM.Lyrics.Core.Engine;

namespace YTM.Lyrics.Core.External.Sources;

/// <summary>
/// 酷狗源适配器（ADR-9 第二顺位）。
/// - 搜索：Kugou.Api.GetSearchSong(keyword)（mobilecdn.kugou.com，无需 Cookie）；
/// - 取词两跳：Kugou.Api.GetSearchLyrics(hash) 拿 id+accesskey → Krc.Helper.GetLyricsAsync(id, accessKey)
///   解密为 KRC 纯文本（只调 Async，坑 #8；Krc.Helper 自带独立 HttpClient）；
/// - SourceId = 搜索结果 InfoItem.Hash。
/// </summary>
public sealed class KugouSourceSearcher : SourceSearcherBase
{
    private readonly Api _api = new();

    public override LyricsSourceKind Source => LyricsSourceKind.Kugou;

    protected override async Task<SourceSearchOutcome> SearchCoreAsync(CleanedTrack track, double durationSec, CancellationToken ct)
    {
        var result = await _api.GetSearchSong(BuildKeyword(track)).ConfigureAwait(false);
        var infos = result?.Data?.Info;
        if (infos is null || infos.Count == 0)
        {
            return new SourceSearchOutcome(false, null, $"[{Source}] 搜索无数据");
        }

        var list = new List<SourceSearchResult>(infos.Count);
        foreach (var info in infos)
        {
            list.Add(new SourceSearchResult(
                Source,
                info.Hash ?? string.Empty,
                info.SongName ?? string.Empty,
                (info.SingerName ?? string.Empty).Split('、'),
                info.AlbumName,
                info.Duration)); // Duration 单位秒
        }

        // 注：不展开 Group（同歌不同版本子项，均为独立 Hash）——首屏候选已足，评分由引擎统一处理。
        return new SourceSearchOutcome(true, list, null);
    }

    protected override async Task<SourceLyricsResult> GetLyricsCoreAsync(string sourceId, CancellationToken ct)
    {
        // 第一跳：hash → 歌词索引（id + accesskey）
        var index = await _api.GetSearchLyrics(hash: sourceId).ConfigureAwait(false);
        var candidate = index?.Candidates?.FirstOrDefault(c => !string.IsNullOrEmpty(c.Id));
        if (candidate is null)
        {
            return new SourceLyricsResult(false, LyricsRawFormat.Unknown, null, null, $"[{Source}] 歌词索引无候选");
        }

        // 第二跳：KRC 解密（Async 版本，坑 #8）
        var text = await Helper.GetLyricsAsync(candidate.Id, candidate.AccessKey).ConfigureAwait(false);
        if (string.IsNullOrEmpty(text))
        {
            return new SourceLyricsResult(false, LyricsRawFormat.Krc, null, null, $"[{Source}] KRC 解密结果为空");
        }

        // KRC 翻译已内嵌 [language:] 段（KrcLyricsConverter 处理），TranslationText 恒 null（TASK-504）
        return new SourceLyricsResult(true, LyricsRawFormat.Krc, text, null, null);
    }
}
