using YTM.Lyrics.Core.Engine;
using YTM.Lyrics.Core.External.Parsing;
using YTM.Lyrics.Core.Timeline;

namespace YTM.Lyrics.Core.Tests.External;

/// <summary>
/// LRC 适配器单测：行解析（含 [offset:] 由 Helper 应用）、翻译行归并、空文档。
/// </summary>
public class LrcLyricsConverterTests
{
    private const LyricsSourceKind Source = LyricsSourceKind.Netease;

    private static LyricDocument Convert(string lrc) => LrcLyricsConverter.Convert(lrc, Source);

    [Fact]
    public void 基础解析_行时间与文本正确()
    {
        const string lrc =
            "[ti:Title]\r\n" +
            "[ar:Artist]\r\n" +
            "[00:01.00]第一句\r\n" +
            "[00:02.50]第二句\r\n" +
            "[00:04.00]第三句\r\n";

        var doc = Convert(lrc);

        Assert.Equal(3, doc.Lines.Count);
        Assert.Equal(TimeSpan.FromMilliseconds(1000), doc.Lines[0].Start);
        Assert.Equal("第一句", doc.Lines[0].Text);
        Assert.Equal(TimeSpan.FromMilliseconds(2500), doc.Lines[1].Start);
        Assert.Equal("第二句", doc.Lines[1].Text);
        Assert.Equal(TimeSpan.FromMilliseconds(4000), doc.Lines[2].Start);
        Assert.Equal("第三句", doc.Lines[2].Text);
        // LRC 无行级结束时间
        Assert.All(doc.Lines, l => Assert.Null(l.End));
        // LRC 无逐字时间轴
        Assert.All(doc.Lines, l => Assert.Null(l.Syllables));
    }

    [Fact]
    public void 翻译行_时间戳相同的下一行归并为Translation()
    {
        const string lrc =
            "[00:01.00]第一句\r\n" +
            "[00:01.00]First line\r\n" +
            "[00:02.50]第二句\r\n";

        var doc = Convert(lrc);

        Assert.Equal(2, doc.Lines.Count);
        Assert.Equal("第一句", doc.Lines[0].Text);
        Assert.Equal("First line", doc.Lines[0].Translation);
        Assert.Equal("第二句", doc.Lines[1].Text);
        Assert.Null(doc.Lines[1].Translation);
    }

    [Fact]
    public void 翻译行_时间戳不同_不归并保留独立行()
    {
        const string lrc =
            "[00:01.00]第一句\r\n" +
            "[00:02.00]另一句\r\n";

        var doc = Convert(lrc);

        Assert.Equal(2, doc.Lines.Count);
        Assert.Null(doc.Lines[0].Translation);
        Assert.Null(doc.Lines[1].Translation);
    }

    [Fact]
    public void 同时间戳三行_只归并第一行为翻译_第三行保留()
    {
        const string lrc =
            "[00:01.00]原词\r\n" +
            "[00:01.00]译文\r\n" +
            "[00:01.00]罗马音\r\n";

        var doc = Convert(lrc);

        Assert.Equal(2, doc.Lines.Count);
        Assert.Equal("原词", doc.Lines[0].Text);
        Assert.Equal("译文", doc.Lines[0].Translation);
        Assert.Equal("罗马音", doc.Lines[1].Text);
    }

    [Fact]
    public void Offset_由Helper应用_转换器不二次处理()
    {
        // [offset:500] → Helper 将全部行时间戳前移 500ms：1000-500=500ms
        const string lrc =
            "[offset:500]\r\n" +
            "[00:01.00]第一句\r\n";

        var doc = Convert(lrc);

        var line = Assert.Single(doc.Lines);
        Assert.Equal(TimeSpan.FromMilliseconds(500), line.Start);
    }

    [Fact]
    public void 空文档_空字符串与无时间戳纯文本均不产出歌词行()
    {
        Assert.Empty(Convert("").Lines);
        Assert.Empty(Convert("只是纯文本没有时间戳").Lines);
        Assert.Empty(Convert("[ti:No Lyrics]\r\n[ar:Artist]").Lines);
    }

    [Fact]
    public void 空文本时间戳行_跳过()
    {
        // "[00:01.00]" 后无文本：LrcParser 产出空文本行，转换器应跳过
        const string lrc = "[00:01.00]\r\n[00:02.00]有词\r\n";
        var doc = Convert(lrc);
        Assert.Equal(TimeSpan.FromMilliseconds(2000), Assert.Single(doc.Lines).Start);
    }

    [Fact]
    public void Source_透传到文档()
    {
        Assert.Equal(Source, Convert("[00:01.00]词").Source);
    }
}
