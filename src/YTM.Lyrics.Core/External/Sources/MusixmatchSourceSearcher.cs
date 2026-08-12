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
            return new SourceLyricsResult(false, LyricsRawFormat.MusixmatchJson, null, $"[{Source}] 歌词无返回");
        }

        return new SourceLyricsResult(true, LyricsRawFormat.MusixmatchJson, raw, null);
    }

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
