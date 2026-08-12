using Lyricify.Lyrics.Models;
using YTM.Lyrics.Core.Engine;
using YTM.Lyrics.Core.External.Parsing;

namespace YTM.Lyrics.Core.Tests.External;

/// <summary>
/// 解析防腐层入口单测：显式传类型（坑 1 规避）、未知类型空文档、Source 透传。
/// </summary>
public class LyricsDocumentParserTests
{
    [Fact]
    public void 显式传Qrc类型_正常解析()
    {
        var doc = LyricsDocumentParser.Parse("[0,100]嗨(0,100)", LyricsRawTypes.Qrc, LyricsSourceKind.QQMusic);
        Assert.Single(doc.Lines);
    }

    [Fact]
    public void 显式传Lrc类型_正常解析()
    {
        var doc = LyricsDocumentParser.Parse("[00:01.00]词", LyricsRawTypes.Lrc, LyricsSourceKind.Netease);
        Assert.Single(doc.Lines);
    }

    [Fact]
    public void 未知类型_返回空文档不抛异常()
    {
        var doc = LyricsDocumentParser.Parse("whatever", LyricsRawTypes.Unknown, LyricsSourceKind.Kugou);
        Assert.Empty(doc.Lines);
    }

    [Fact]
    public void 扩展点类型_YrcKrcTtml_v1返回空文档()
    {
        Assert.Empty(LyricsDocumentParser.Parse("yrc", LyricsRawTypes.Yrc, LyricsSourceKind.Netease).Lines);
        Assert.Empty(LyricsDocumentParser.Parse("krc", LyricsRawTypes.Krc, LyricsSourceKind.Kugou).Lines);
        Assert.Empty(LyricsDocumentParser.Parse("ttml", LyricsRawTypes.Ttml, LyricsSourceKind.AppleMusic).Lines);
    }

    [Fact]
    public void Source_透传到文档()
    {
        var doc = LyricsDocumentParser.Parse("[0,100]嗨(0,100)", LyricsRawTypes.Qrc, LyricsSourceKind.Musixmatch);
        Assert.Equal(LyricsSourceKind.Musixmatch, doc.Source);
    }
}
