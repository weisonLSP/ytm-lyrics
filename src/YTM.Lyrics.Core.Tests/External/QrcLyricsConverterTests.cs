using Lyricify.Lyrics.Generators;
using Lyricify.Lyrics.Models;
using YTM.Lyrics.Core.Engine;
using YTM.Lyrics.Core.External.Parsing;
using YTM.Lyrics.Core.Timeline;

namespace YTM.Lyrics.Core.Tests.External;

/// <summary>
/// QRC 适配器单测：行级时间重建（对照原文行标签，防腐层坑 2 验收）、
/// 音节绝对轴叠加、翻译行映射、空文档/单行文档。
/// </summary>
public class QrcLyricsConverterTests
{
    private const LyricsSourceKind Source = LyricsSourceKind.QQMusic;

    private static LyricDocument Convert(string qrc) => QrcLyricsConverter.Convert(qrc, Source);

    [Fact]
    public void 传统QRC_行标签毫秒重建_逐行对照原文()
    {
        // 手写传统 QRC：行标签 [mm:ss.xx]（百分秒），音节相对行首
        const string qrc =
            "[ti:Test Song]\r\n" +
            "[ar:Some Artist]\r\n" +
            "[offset:0]\r\n" +
            "[00:01.00]你(0,500)好(500,500)\r\n" +
            "[00:02.50]世(0,300)界(300,700)\r\n";

        var doc = Convert(qrc);

        Assert.Equal(2, doc.Lines.Count);

        // 对照原文行标签 [00:01.00] → 1000ms
        Assert.Equal(TimeSpan.FromMilliseconds(1000), doc.Lines[0].Start);
        Assert.Equal(TimeSpan.FromMilliseconds(2000), doc.Lines[0].End);   // 末音节相对 1000ms 叠加行 1000ms
        Assert.Equal("你好", doc.Lines[0].Text);
        Assert.Null(doc.Lines[0].Translation);
        Assert.Equal(new LyricSyllable(TimeSpan.FromMilliseconds(1000), TimeSpan.FromMilliseconds(500), "你"),
            doc.Lines[0].Syllables![0]);
        Assert.Equal(new LyricSyllable(TimeSpan.FromMilliseconds(1500), TimeSpan.FromMilliseconds(500), "好"),
            doc.Lines[0].Syllables[1]);

        // 对照原文行标签 [00:02.50] → 2500ms
        Assert.Equal(TimeSpan.FromMilliseconds(2500), doc.Lines[1].Start);
        Assert.Equal(TimeSpan.FromMilliseconds(3500), doc.Lines[1].End);
        Assert.Equal("世界", doc.Lines[1].Text);
        Assert.Equal(new LyricSyllable(TimeSpan.FromMilliseconds(2500), TimeSpan.FromMilliseconds(300), "世"),
            doc.Lines[1].Syllables![0]);
        Assert.Equal(new LyricSyllable(TimeSpan.FromMilliseconds(2800), TimeSpan.FromMilliseconds(700), "界"),
            doc.Lines[1].Syllables[1]);

        // 属性行 [ti:]/[ar:]/[offset:] 不得进入行列表
        Assert.All(doc.Lines, l => Assert.DoesNotContain(":", l.Text));
    }

    [Fact]
    public void QrcGenerator反向构造_绝对音节不叠加_逐行对照行标签()
    {
        // 用 Helper 生态（QrcGenerator）反向构造真实 QRC：行标签 [startMs,durationMs]，音节为绝对毫秒
        var data = new LyricsData
        {
            Lines = new List<ILineInfo>
            {
                new SyllableLineInfo(new List<ISyllableInfo>
                {
                    new SyllableInfo("你", 0, 500),
                    new SyllableInfo("好", 500, 1000),
                }),
                new SyllableLineInfo(new List<ISyllableInfo>
                {
                    new SyllableInfo("世", 2000, 2300),
                    new SyllableInfo("界", 2300, 3000),
                }),
            },
        };
        var qrc = QrcGenerator.Generate(data);
        Assert.Equal("[0,1000]你(0,500)好(500,500)\r\n[2000,1000]世(2000,300)界(2300,700)\r\n", qrc);

        var doc = Convert(qrc);

        Assert.Equal(2, doc.Lines.Count);
        // 对照生成文本行标签 [0,1000]
        Assert.Equal(TimeSpan.Zero, doc.Lines[0].Start);
        Assert.Equal(TimeSpan.FromMilliseconds(1000), doc.Lines[0].End);
        Assert.Equal(new LyricSyllable(TimeSpan.Zero, TimeSpan.FromMilliseconds(500), "你"), doc.Lines[0].Syllables![0]);
        Assert.Equal(new LyricSyllable(TimeSpan.FromMilliseconds(500), TimeSpan.FromMilliseconds(500), "好"), doc.Lines[0].Syllables[1]);
        // 对照生成文本行标签 [2000,1000]
        Assert.Equal(TimeSpan.FromMilliseconds(2000), doc.Lines[1].Start);
        Assert.Equal(TimeSpan.FromMilliseconds(3000), doc.Lines[1].End);
        Assert.Equal(new LyricSyllable(TimeSpan.FromMilliseconds(2000), TimeSpan.FromMilliseconds(300), "世"), doc.Lines[1].Syllables![0]);
        Assert.Equal(new LyricSyllable(TimeSpan.FromMilliseconds(2300), TimeSpan.FromMilliseconds(700), "界"), doc.Lines[1].Syllables[1]);
    }

    [Fact]
    public void 新QRC相对音节_首音节不等于行标签_按相对叠加()
    {
        // 手写形态 1 但音节为相对行首值：首音节 0 ≠ 行标签 start 5000 → 必须叠加
        const string qrc = "[5000,2000]你(0,500)好(500,500)";

        var doc = Convert(qrc);

        var line = Assert.Single(doc.Lines);
        Assert.Equal(TimeSpan.FromMilliseconds(5000), line.Start);
        Assert.Equal(TimeSpan.FromMilliseconds(7000), line.End);
        Assert.Equal(new LyricSyllable(TimeSpan.FromMilliseconds(5000), TimeSpan.FromMilliseconds(500), "你"), line.Syllables![0]);
        Assert.Equal(new LyricSyllable(TimeSpan.FromMilliseconds(5500), TimeSpan.FromMilliseconds(500), "好"), line.Syllables[1]);
    }

    [Fact]
    public void 翻译行_无音节有行标签_并入前一歌词行()
    {
        const string qrc =
            "[0,2000]你(0,500)好(500,500)\r\n" +
            "[3000,0]La la la\r\n";

        var doc = Convert(qrc);

        var line = Assert.Single(doc.Lines);
        Assert.Equal("你好", line.Text);
        Assert.Equal("La la la", line.Translation);
        Assert.Equal(2, line.Syllables!.Count);
    }

    [Fact]
    public void 翻译行_文档开头无前一行_保留为独立行()
    {
        const string qrc =
            "[0,1000]Intro music\r\n" +
            "[2000,1000]你(0,500)好(500,500)\r\n";

        var doc = Convert(qrc);

        Assert.Equal(2, doc.Lines.Count);
        Assert.Equal("Intro music", doc.Lines[0].Text);
        Assert.Null(doc.Lines[0].Translation);
        Assert.Null(doc.Lines[0].Syllables);
        Assert.Equal(TimeSpan.Zero, doc.Lines[0].Start);
        Assert.Equal("你好", doc.Lines[1].Text);
    }

    [Fact]
    public void 翻译行_前一歌词行已有翻译_保留为独立行()
    {
        const string qrc =
            "[0,2000]你(0,500)好(500,500)\r\n" +
            "[3000,0]First translation\r\n" +
            "[4000,0]Second translation\r\n";

        var doc = Convert(qrc);

        Assert.Equal(2, doc.Lines.Count);
        Assert.Equal("First translation", doc.Lines[0].Translation);
        Assert.Equal("Second translation", doc.Lines[1].Text);
        Assert.Null(doc.Lines[1].Translation);
    }

    [Fact]
    public void 空文档_空字符串与属性行均不产出歌词行()
    {
        Assert.Empty(Convert("").Lines);
        Assert.Empty(Convert("  \r\n\t\r\n").Lines);
        Assert.Empty(Convert("[ti:No Lyrics]\r\n[ar:Artist]\r\n[offset:0]").Lines);
    }

    [Fact]
    public void 单行文档_正常转换()
    {
        const string qrc = "[1000,500]嗨(0,500)";

        var doc = Convert(qrc);

        var line = Assert.Single(doc.Lines);
        Assert.Equal(TimeSpan.FromMilliseconds(1000), line.Start);
        Assert.Equal(TimeSpan.FromMilliseconds(1500), line.End);
        Assert.Equal(new LyricSyllable(TimeSpan.FromMilliseconds(1000), TimeSpan.FromMilliseconds(500), "嗨"), line.Syllables![0]);
    }

    [Fact]
    public void Source_透传到文档()
    {
        Assert.Equal(Source, Convert("[0,100]a(0,100)").Source);
    }
}
