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

    // ---- TASK-504 反馈 ⑤：credits 元数据行过滤 ----

    [Theory]
    [InlineData("[00:00.00]作词:方文山", true)]
    [InlineData("[00:00.00]作词：方文山", true)]
    [InlineData("[00:00.00]作曲:周杰伦", true)]
    [InlineData("[00:00.00]编曲:林迈可", true)]
    [InlineData("[00:00.00]制作人:周杰伦", true)]
    [InlineData("[00:00.00]混音:某某", true)]
    [InlineData("[00:00.00]作词", true)] // 无冒号无内容
    [InlineData("[00:00.00]作词 : 方文山", true)] // 取证格式：关键词与冒号间有空格（网易云真实数据）
    [InlineData("[00:00.00]作词 :方文山", true)]
    [InlineData("[00:00.00]作词: 方文山", true)]
    [InlineData("[00:00.00]夜曲 - 周杰伦", false)] // 非 credits 行：不得误杀
    [InlineData("[00:00.00]一群嗜血的蚂蚁", false)]
    [InlineData("[00:00.00]歌词里有作词两个字", false)] // 不在行首：不得误杀
    public void Credits行_带时间戳_按形态过滤(string lrc, bool shouldFilter)
    {
        var doc = Convert(lrc);
        Assert.Equal(shouldFilter, doc.Lines.Count == 0);
    }

    [Fact]
    public void Credits行_无时间戳_不产出歌词行()
    {
        // 无时间戳纯文本行 LrcParser 本就不产出；带时间戳 credits 行由过滤兜底（上一用例覆盖）
        var doc = Convert("作词 : Montagem\r\n作曲 : Sazablue\r\n编曲 : 夏蔓蔓");
        Assert.Empty(doc.Lines);
    }

    [Fact]
    public void 过滤后全空_返回空文档()
    {
        // 真实取证形态（Merry Christmas Mr. Lawrence，网易云无版权歌）：只有 credits 行 + 无正文
        const string lrc =
            "[00:00.00] 编曲 : 坂本龍一\r\n" +
            "[00:01.00] 制作人 : 坂本龍一/Katsunori Oshimo/Shinichi Tanaka\r\n" +
            "[00:02.00] 作曲 : 坂本龍一\r\n";

        Assert.Empty(Convert(lrc).Lines);
    }

    [Fact]
    public void credits与正文混排_过滤credits_保留正文()
    {
        // 真实取证形态（夜曲）：3 行 credits（同时间戳）+ 正文行
        const string lrc =
            "[00:00.000] 作词 : 方文山\r\n" +
            "[00:00.000] 作曲 : 周杰伦\r\n" +
            "[00:00.000] 编曲 : 林迈可\r\n" +
            "[00:24.960]一群嗜血的蚂蚁 被腐肉所吸引\r\n";

        var doc = Convert(lrc);

        var line = Assert.Single(doc.Lines);
        Assert.Equal("一群嗜血的蚂蚁 被腐肉所吸引", line.Text);
    }
}
