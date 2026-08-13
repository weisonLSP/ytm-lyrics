using Lyricify.Lyrics.Providers.Web.AppleMusic;
using YTM.Lyrics.Core.Cleaning;
using YTM.Lyrics.Core.Engine;

namespace YTM.Lyrics.Core.External.Sources;

/// <summary>
/// Apple Music 源适配器（ADR-9 第三顺位）。
/// - 搜索：AppleMusic.Api.Search(keyword)（amp-api.music.apple.com，Api 自动抓取 access token，
///   可选 SetMediaUserToken/SetAccessToken 注入——v1 不注入，走自动抓取，坑 #9 已接受）；
/// - 取词：AppleMusic.Api.GetLyrics(songId) → LyricResponse（内部归一化为 TTML，取 .Ttml）；
/// - SourceId = 搜索结果 SongData.Id。自持 Api 实例以保留 access token / storefront 生命周期。
/// </summary>
public sealed class AppleMusicSourceSearcher : SourceSearcherBase
{
    private readonly Api _api = new();

    public override LyricsSourceKind Source => LyricsSourceKind.AppleMusic;

    protected override async Task<SourceSearchOutcome> SearchCoreAsync(CleanedTrack track, double durationSec, CancellationToken ct)
    {
        var result = await _api.Search(BuildKeyword(track)).ConfigureAwait(false);
        var songs = result?.Results?.Songs?.Data;
        if (songs is null || songs.Length == 0)
        {
            return new SourceSearchOutcome(false, null, $"[{Source}] 搜索无数据");
        }

        var list = new List<SourceSearchResult>(songs.Length);
        foreach (var song in songs)
        {
            var attributes = song.Attributes;
            list.Add(new SourceSearchResult(
                Source,
                song.Id ?? string.Empty,
                attributes?.Name ?? string.Empty,
                SplitArtists(attributes?.ArtistName),
                attributes?.AlbumName,
                attributes is { DurationInMillis: > 0 } a ? a.DurationInMillis / 1000.0 : null));
        }

        return new SourceSearchOutcome(true, list, null);
    }

    protected override async Task<SourceLyricsResult> GetLyricsCoreAsync(string sourceId, CancellationToken ct)
    {
        var response = await _api.GetLyrics(sourceId).ConfigureAwait(false);
        var ttml = response?.Ttml;
        if (string.IsNullOrEmpty(ttml))
        {
            return new SourceLyricsResult(false, LyricsRawFormat.Ttml, null, null, $"[{Source}] 歌词无返回");
        }

        return new SourceLyricsResult(true, LyricsRawFormat.Ttml, ttml, null, null);
    }

    /// <summary>艺人名拆分（对齐 Helper AppleMusicSearchResult 语义：", " 与末位 " & "）。</summary>
    private static string[] SplitArtists(string? artistName)
    {
        if (string.IsNullOrWhiteSpace(artistName))
        {
            return Array.Empty<string>();
        }

        var parts = artistName
            .Split(new[] { ", " }, StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Trim())
            .ToList();

        if (parts.Count == 0)
        {
            return Array.Empty<string>();
        }

        var last = parts[^1];
        var ampIndex = last.LastIndexOf(" & ", StringComparison.Ordinal);
        if (ampIndex >= 0)
        {
            parts.RemoveAt(parts.Count - 1);
            var left = last[..ampIndex].Trim();
            var right = last[(ampIndex + 3)..].Trim();
            if (!string.IsNullOrEmpty(left))
            {
                parts.Add(left);
            }

            if (!string.IsNullOrEmpty(right))
            {
                parts.Add(right);
            }
        }

        return parts.ToArray();
    }
}
