using Lyricify.Lyrics.Decrypter.Qrc;
using Lyricify.Lyrics.Providers.Web.Musixmatch;
using Lyricify.Lyrics.Providers.Web.Netease;
using YTM.Lyrics.Core.Engine;
using YTM.Lyrics.Core.External.Sources;

namespace YTM.Lyrics.Core.Tests.External.Sources;

/// <summary>
/// TASK-504 三源适配器翻译接入单测（internal 纯函数 mock 验证，无网络）：
/// QQ 取 Trans、网易云取 Tlyric、Musixmatch 序列化翻译 → TranslationText 正确填入。
/// </summary>
public class SourceTranslationTests
{
    // ---------- QQ：Trans → TranslationText ----------

    [Fact]
    public void QQ_响应含Trans_填入TranslationText()
    {
        var resp = new QqLyricsResponse { Lyrics = "[00:01.00]原词\r\n", Trans = "[00:01.00]译文\r\n" };

        var result = QqMusicSourceSearcher.MapLyricsResponse(resp, LyricsSourceKind.QQMusic);

        Assert.True(result.Success);
        Assert.Equal("[00:01.00]译文\r\n", result.TranslationText);
        Assert.Equal(LyricsRawFormat.Lrc, result.Format); // 无音节标记 → LRC
    }

    [Fact]
    public void QQ_无Trans_TranslationText为null()
    {
        var resp = new QqLyricsResponse { Lyrics = "[00:01.00]原词\r\n", Trans = "" };

        var result = QqMusicSourceSearcher.MapLyricsResponse(resp, LyricsSourceKind.QQMusic);

        Assert.True(result.Success);
        Assert.Null(result.TranslationText);
    }

    [Fact]
    public void QQ_响应为null或歌词空_失败()
    {
        var fail = QqMusicSourceSearcher.MapLyricsResponse(null, LyricsSourceKind.QQMusic);
        Assert.False(fail.Success);
        Assert.Contains("QQMusic", fail.ErrorMessage);

        var empty = QqMusicSourceSearcher.MapLyricsResponse(
            new QqLyricsResponse { Lyrics = "", Trans = "" }, LyricsSourceKind.QQMusic);
        Assert.False(empty.Success);
    }

    // ---------- 网易云：Tlyric → TranslationText ----------

    [Fact]
    public void 网易云_含Tlyric_填入TranslationText()
    {
        var result = new LyricResult
        {
            Lrc = new Lyricify.Lyrics.Providers.Web.Netease.Lyrics { Lyric = "[00:01.00]原词\r\n" },
            Tlyric = new Lyricify.Lyrics.Providers.Web.Netease.Lyrics { Lyric = "[00:01.00]译文\r\n" },
        };

        var mapped = NeteaseSourceSearcher.MapLyricResult(result, LyricsSourceKind.Netease);

        Assert.True(mapped.Success);
        Assert.Equal("[00:01.00]译文\r\n", mapped.TranslationText);
        Assert.Equal(LyricsRawFormat.Lrc, mapped.Format);
    }

    [Fact]
    public void 网易云_主歌词Yrc优先_翻译仍取Tlyric()
    {
        var result = new LyricResult
        {
            Yrc = new Lyricify.Lyrics.Providers.Web.Netease.Lyrics { Lyric = "[00:01.00]<0,500>原<500,500>词\r\n" },
            Lrc = new Lyricify.Lyrics.Providers.Web.Netease.Lyrics { Lyric = "[00:01.00]原词\r\n" },
            Tlyric = new Lyricify.Lyrics.Providers.Web.Netease.Lyrics { Lyric = "[00:01.00]译文\r\n" },
        };

        var mapped = NeteaseSourceSearcher.MapLyricResult(result, LyricsSourceKind.Netease);

        Assert.True(mapped.Success);
        Assert.Equal(LyricsRawFormat.Yrc, mapped.Format);      // Yrc 优先
        Assert.Equal("[00:01.00]<0,500>原<500,500>词\r\n", mapped.RawText);
        Assert.Equal("[00:01.00]译文\r\n", mapped.TranslationText); // Tlyric 独立承载
    }

    [Fact]
    public void 网易云_无Tlyric_TranslationText为null()
    {
        var result = new LyricResult
        {
            Lrc = new Lyricify.Lyrics.Providers.Web.Netease.Lyrics { Lyric = "[00:01.00]原词\r\n" },
        };

        var mapped = NeteaseSourceSearcher.MapLyricResult(result, LyricsSourceKind.Netease);

        Assert.True(mapped.Success);
        Assert.Null(mapped.TranslationText);
    }

    [Fact]
    public void 网易云_Nolyric_失败()
    {
        var mapped = NeteaseSourceSearcher.MapLyricResult(
            new LyricResult { Nolyric = true }, LyricsSourceKind.Netease);

        Assert.False(mapped.Success);
        Assert.Contains("Nolyric", mapped.ErrorMessage);
    }

    // ---------- Musixmatch：翻译条目 → JSON 中间格式 ----------

    private static GetTranslationsResponse.TranslationsListItem Item(string? matchedLine, string? description, string? subtitleLine = null)
        => new()
        {
            Translation = new GetTranslationsResponse.Translation
            {
                MatchedLine = matchedLine,
                SubtitleMatchedLine = subtitleLine,
                Description = description,
            },
        };

    [Fact]
    public void Musixmatch_翻译条目_序列化为matchedLine_translation_JSON()
    {
        var items = new[]
        {
            Item("夢ならばどれほどよかったでしょう", "若这是一场梦该有多好"),
            Item("未だにあなたのことを夢にみる", "至今仍能与你梦中相遇"),
        };

        var json = MusixmatchSourceSearcher.SerializeTranslations(items);

        Assert.Equal(
            "[{\"matchedLine\":\"夢ならばどれほどよかったでしょう\",\"translation\":\"若这是一场梦该有多好\"},{\"matchedLine\":\"未だにあなたのことを夢にみる\",\"translation\":\"至今仍能与你梦中相遇\"}]",
            json);
    }

    [Fact]
    public void Musixmatch_matchedLine缺失_回退subtitle_matched_line()
    {
        var items = new[] { Item(null, "译文", subtitleLine: "回退行") };

        var json = MusixmatchSourceSearcher.SerializeTranslations(items);

        Assert.Contains("\"matchedLine\":\"回退行\"", json);
    }

    [Fact]
    public void Musixmatch_无译文或空列表_返回null()
    {
        Assert.Null(MusixmatchSourceSearcher.SerializeTranslations(null));
        Assert.Null(MusixmatchSourceSearcher.SerializeTranslations(Array.Empty<GetTranslationsResponse.TranslationsListItem>()));

        // 条目无原文行或无译文 → 全部丢弃 → null
        var allInvalid = new[] { Item("", "译文"), Item("有行", ""), Item(null, "译文") };
        Assert.Null(MusixmatchSourceSearcher.SerializeTranslations(allInvalid));
    }

    [Fact]
    public void Musixmatch_混合有效与无效条目_只序列化有效项()
    {
        var items = new[]
        {
            Item("", "无原文行"),          // 无效
            Item("有效行", ""),            // 无效
            Item("Line one", "译文一"),    // 有效
            Item("Line two", "译文二"),    // 有效
        };

        var json = MusixmatchSourceSearcher.SerializeTranslations(items);

        Assert.DoesNotContain("无原文行", json);
        Assert.Contains("\"matchedLine\":\"Line one\"", json);
        Assert.Contains("\"matchedLine\":\"Line two\"", json);
    }
}
