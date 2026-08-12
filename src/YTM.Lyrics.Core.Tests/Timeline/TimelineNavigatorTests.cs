using YTM.Lyrics.Core.Engine;
using YTM.Lyrics.Core.Timeline;

namespace YTM.Lyrics.Core.Tests.Timeline;

/// <summary>
/// TimelineNavigator 二分定位单测：首行前 / 末行后 / 行间空隙 / 接缝命中 / 空文档 / 单行文档。
/// </summary>
public class TimelineNavigatorTests
{
    private static LyricLine Line(double startSec) => new(
        TimeSpan.FromSeconds(startSec), null, "歌词", null, null);

    // 三行文档：0s / 2s / 5s
    private static LyricDocument Doc3() => new()
    {
        Lines = new[] { Line(0), Line(2), Line(5) },
        Source = LyricsSourceKind.QQMusic,
    };

    [Fact]
    public void 空文档_恒返回负一()
    {
        var doc = new LyricDocument { Lines = Array.Empty<LyricLine>(), Source = LyricsSourceKind.QQMusic };
        Assert.Equal(-1, TimelineNavigator.FindCurrentLineIndex(doc, TimeSpan.Zero));
        Assert.Equal(-1, TimelineNavigator.FindCurrentLineIndex(doc, TimeSpan.FromHours(1)));
    }

    [Fact]
    public void 单行文档_首行前负一_命中后恒为0()
    {
        var doc = new LyricDocument { Lines = new[] { Line(3) }, Source = LyricsSourceKind.QQMusic };
        Assert.Equal(-1, TimelineNavigator.FindCurrentLineIndex(doc, TimeSpan.FromSeconds(2.999)));
        Assert.Equal(0, TimelineNavigator.FindCurrentLineIndex(doc, TimeSpan.FromSeconds(3)));
        Assert.Equal(0, TimelineNavigator.FindCurrentLineIndex(doc, TimeSpan.FromSeconds(999)));
    }

    [Fact]
    public void 首行前_返回负一()
    {
        var doc = Doc3();
        Assert.Equal(-1, TimelineNavigator.FindCurrentLineIndex(doc, TimeSpan.FromSeconds(-1)));
        Assert.Equal(-1, TimelineNavigator.FindCurrentLineIndex(doc, TimeSpan.FromMilliseconds(-1)));
        Assert.Equal(-1, TimelineNavigator.FindCurrentLineIndex(doc, TimeSpan.FromTicks(-1))); // 任意负位置均在首行 start=0s 之前
    }

    [Fact]
    public void 恰好命中各行_返回对应索引()
    {
        var doc = Doc3();
        Assert.Equal(0, TimelineNavigator.FindCurrentLineIndex(doc, TimeSpan.FromSeconds(0)));
        Assert.Equal(1, TimelineNavigator.FindCurrentLineIndex(doc, TimeSpan.FromSeconds(2)));
        Assert.Equal(2, TimelineNavigator.FindCurrentLineIndex(doc, TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void 行间空隙_保持上一行()
    {
        var doc = Doc3();
        Assert.Equal(0, TimelineNavigator.FindCurrentLineIndex(doc, TimeSpan.FromSeconds(1.5)));
        Assert.Equal(1, TimelineNavigator.FindCurrentLineIndex(doc, TimeSpan.FromSeconds(3.99)));
    }

    [Fact]
    public void 行间空隙_上一行有End_仍保持上一行()
    {
        // 首行 End=1.5s，空隙 1.5s~2s，position=1.8s 应保持第 0 行
        var doc = new LyricDocument
        {
            Lines = new[]
            {
                new LyricLine(TimeSpan.Zero, TimeSpan.FromSeconds(1.5), "a", null, null),
                new LyricLine(TimeSpan.FromSeconds(2), null, "b", null, null),
            },
            Source = LyricsSourceKind.QQMusic,
        };
        Assert.Equal(0, TimelineNavigator.FindCurrentLineIndex(doc, TimeSpan.FromSeconds(1.8)));
    }

    [Fact]
    public void 接缝_上行End与下行Start重合_取后一行()
    {
        // 上行 End=2s 与下行 Start=2s 重合，position=2s 应命中第 1 行
        var doc = new LyricDocument
        {
            Lines = new[]
            {
                new LyricLine(TimeSpan.Zero, TimeSpan.FromSeconds(2), "a", null, null),
                new LyricLine(TimeSpan.FromSeconds(2), null, "b", null, null),
            },
            Source = LyricsSourceKind.QQMusic,
        };
        Assert.Equal(1, TimelineNavigator.FindCurrentLineIndex(doc, TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public void 末行后_停留末行()
    {
        var doc = Doc3();
        Assert.Equal(2, TimelineNavigator.FindCurrentLineIndex(doc, TimeSpan.FromSeconds(5.0001)));
        Assert.Equal(2, TimelineNavigator.FindCurrentLineIndex(doc, TimeSpan.FromHours(24)));
    }
}
