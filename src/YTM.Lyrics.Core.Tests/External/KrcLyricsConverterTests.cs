using System.Text;
using Lyricify.Lyrics.Parsers;
using YTM.Lyrics.Core.Engine;
using YTM.Lyrics.Core.External.Parsing;
using YTM.Lyrics.Core.Timeline;

namespace YTM.Lyrics.Core.Tests.External;

/// <summary>
/// KRC → LyricDocument 转换单测（TASK-205F 补齐 TASK-204 扩展点）。
/// 样本按 Helper KrcGenerator 输出格式（行标签 [startMs,durationMs] + `<偏移,时长,0>词`）手写，
/// 并经 Helper KrcParser 反向验证（与 LyricsData 版解析结果逐行对照）。
/// </summary>
public class KrcLyricsConverterTests
{
    private const LyricsSourceKind Source = LyricsSourceKind.Kugou;

    /// <summary>构造 [language:] 翻译段（KugouTranslation JSON → Base64）。</summary>
    private static string TranslationTag(params string[] lines)
    {
        var items = lines.Select(l => $"[\"{l}\"]");
        var json = $"{{\"content\":[{{\"language\":0,\"type\":1,\"lyricContent\":[{string.Join(",", items)}]}}],\"version\":1}}";
        return $"[language:{Convert.ToBase64String(Encoding.UTF8.GetBytes(json))}]";
    }

    [Fact]
    public void 行级时间_音节绝对毫秒_翻译合并_逐行对照()
    {
        // 酷狗 KRC 真实形态：行标签绝对毫秒，音节=行标签+词偏移（Helper 保留行级时间，api-map §4.2）
        var krc = string.Join("\n",
            "[ar:周杰伦]",
            "[ti:晴天]",
            "[offset:0]",
            TranslationTag("小黄花", "从前的从前"),
            "[3700,2400]<0,800,0>晴<800,400,0>天<1200,600,0>周<1800,500,0>杰<2300,400,0>伦",
            "[6100,2400]<0,700,0>故事<700,600,0>的<1300,400,0>小黄花",
            "[8500,2000]<0,1000,0>从前<1000,1000,0>的从前");

        var doc = KrcLyricsConverter.Convert(krc, Source);

        Assert.Equal(3, doc.Lines.Count);
        Assert.Equal(Source, doc.Source);

        // 行 1：行级时间以行标签为权威（Start=3700, End=6100）
        var l1 = doc.Lines[0];
        Assert.Equal("晴天周杰伦", l1.Text);
        Assert.Equal(TimeSpan.FromMilliseconds(3700), l1.Start);
        Assert.Equal(TimeSpan.FromMilliseconds(6100), l1.End); // 3700 + 2400
        Assert.Equal("小黄花", l1.Translation);                // 翻译并入（按行序）
        Assert.Equal(5, l1.Syllables!.Count);
        Assert.Equal(new[] { "晴", "天", "周", "杰", "伦" }, l1.Syllables.Select(s => s.Text).ToArray());
        Assert.Equal(TimeSpan.FromMilliseconds(3700), l1.Syllables[0].Start); // 绝对毫秒
        Assert.Equal(TimeSpan.FromMilliseconds(4500), l1.Syllables[1].Start);
        Assert.Equal(TimeSpan.FromMilliseconds(800), l1.Syllables[0].Duration);
        Assert.Equal(TimeSpan.FromMilliseconds(6000), l1.Syllables[4].Start);

        // 行 2：连续时间轴
        var l2 = doc.Lines[1];
        Assert.Equal("故事的小黄花", l2.Text);
        Assert.Equal(TimeSpan.FromMilliseconds(6100), l2.Start);
        Assert.Equal(TimeSpan.FromMilliseconds(8500), l2.End);
        Assert.Equal("从前的从前", l2.Translation);
        Assert.Equal(TimeSpan.FromMilliseconds(6100), l2.Syllables![0].Start);

        // 行 3
        var l3 = doc.Lines[2];
        Assert.Equal("从前的从前", l3.Text);
        Assert.Equal(TimeSpan.FromMilliseconds(8500), l3.Start);
        Assert.Equal(TimeSpan.FromMilliseconds(10500), l3.End);
        Assert.Equal(TimeSpan.FromMilliseconds(8500), l3.Syllables![0].Start);
        Assert.Equal(TimeSpan.FromMilliseconds(9500), l3.Syllables[1].Start);
    }

    [Fact]
    public void 无翻译段_Translation为空()
    {
        var krc = string.Join("\n",
            "[ar:周杰伦]",
            "[ti:晴天]",
            "[3700,2400]<0,800,0>晴<800,400,0>天<1200,600,0>周<1800,500,0>杰<2300,400,0>伦");

        var doc = KrcLyricsConverter.Convert(krc, Source);

        var line = Assert.Single(doc.Lines);
        Assert.Null(line.Translation);
    }

    [Fact]
    public void 翻译占位_双斜杠_不承载()
    {
        // "//" 占位表示该行无翻译（KrcParser.Parse 合并规则同源）
        var krc = string.Join("\n",
            "[language:" + Convert.ToBase64String(Encoding.UTF8.GetBytes(
                "{\"content\":[{\"language\":0,\"type\":1,\"lyricContent\":[[\"//\"]]}],\"version\":1}")) + "]",
            "[3700,2400]<0,800,0>晴<800,400,0>天");

        var doc = KrcLyricsConverter.Convert(krc, Source);

        Assert.Null(Assert.Single(doc.Lines).Translation);
    }

    [Fact]
    public void offset属性_整体平移行时间()
    {
        var krc = string.Join("\n",
            "[offset:500]",
            "[3700,2400]<0,800,0>晴<800,400,0>天");

        var doc = KrcLyricsConverter.Convert(krc, Source);

        var line = Assert.Single(doc.Lines);
        Assert.Equal(TimeSpan.FromMilliseconds(4200), line.Start);  // 3700 + 500
        Assert.Equal(TimeSpan.FromMilliseconds(6600), line.End);    // 4200 + 2400
        Assert.Equal(TimeSpan.FromMilliseconds(4200), line.Syllables![0].Start);
    }

    [Fact]
    public void 首音节偏移非零_行Start用行标签_音节用Helper绝对毫秒()
    {
        // 变体样本：首音节偏移 100ms（≠0）→ 行开始（行标签权威）与首音节开始（Helper 绝对毫秒）不同，
        // 前导空白语义：词确实从 3800ms 才唱，行从 3700ms 开始
        var krc = "[3700,2400]<100,800,0>晴<900,400,0>天";

        var doc = KrcLyricsConverter.Convert(krc, Source);

        var line = Assert.Single(doc.Lines);
        Assert.Equal(TimeSpan.FromMilliseconds(3700), line.Start);      // 行标签权威
        Assert.Equal(TimeSpan.FromMilliseconds(3800), line.Syllables![0].Start); // Helper 绝对：3700 + 100
        Assert.Equal(TimeSpan.FromMilliseconds(4600), line.Syllables[1].Start);  // 3700 + 900
    }

    [Fact]
    public void 属性行_过滤不产出歌词行()
    {
        var krc = string.Join("\n",
            "[ar:周杰伦]",
            "[ti:晴天]",
            "[hash:abc]",
            "[language:eyJjb250ZW50IjpbXSwidmVyc2lvbiI6MX0=]");

        var doc = KrcLyricsConverter.Convert(krc, Source);

        Assert.Empty(doc.Lines);
    }

    [Fact]
    public void 与HelperKrcParser解析结果_行文本与行数一致()
    {
        // 对照 Helper KrcParser.Parse（LyricsData 路径）：行数、行文本、音节文本序列一致
        var krc = string.Join("\n",
            "[ar:周杰伦]",
            "[ti:晴天]",
            "[offset:0]",
            "[3700,2400]<0,800,0>晴<800,400,0>天<1200,600,0>周<1800,500,0>杰<2300,400,0>伦",
            "[6100,2400]<0,700,0>故事<700,600,0>的<1300,400,0>小黄花");

        var ours = KrcLyricsConverter.Convert(krc, Source);
        var helper = KrcParser.Parse(krc);

        Assert.Equal(helper.Lines!.Count, ours.Lines.Count);
        for (var i = 0; i < ours.Lines.Count; i++)
        {
            Assert.Equal(helper.Lines![i].Text, ours.Lines[i].Text);
            var helperSyl = ((Lyricify.Lyrics.Models.SyllableLineInfo)helper.Lines![i]).Syllables;
            Assert.Equal(helperSyl.Select(s => s.Text).ToArray(),
                ours.Lines[i].Syllables!.Select(s => s.Text).ToArray());
            // Helper 音节绝对毫秒（lineStart + 词偏移）与我们的绝对毫秒一致
            Assert.Equal(helperSyl.Select(s => s.StartTime).ToArray(),
                ours.Lines[i].Syllables!.Select(s => (int)s.Start.TotalMilliseconds).ToArray());
        }
    }

    [Fact]
    public void 空文本_返回空文档()
    {
        var doc = KrcLyricsConverter.Convert("", Source);
        Assert.Empty(doc.Lines);

        var doc2 = KrcLyricsConverter.Convert("   \n  ", Source);
        Assert.Empty(doc2.Lines);
    }

    /// <summary>
    /// 真实 KRC 冒烟（TASK-205F）：tools/t205f/krc_fetch_real.py 抓取的酷狗真实样本
    /// （晴天/周杰伦，解密后 8404 字节，与 M2 真机日志 rawLen=8404 一致）。
    /// 默认跳过；设置 KRC_SAMPLE_PATH 指向样本文件后启用（沙箱网络不可用时不依赖真实网络）。
    /// 断言：不抛异常、行数 &gt; 0、全部行 Start 有效、与 Helper KrcParser 行文本一致。
    /// </summary>
    [Fact]
    public void 冒烟_真实酷狗KRC样本_解析非空且与Helper一致()
    {
        var samplePath = Environment.GetEnvironmentVariable("KRC_SAMPLE_PATH");
        if (string.IsNullOrEmpty(samplePath) || !File.Exists(samplePath)) return;

        var krc = File.ReadAllText(samplePath, Encoding.UTF8);
        var doc = KrcLyricsConverter.Convert(krc, Source);

        Assert.NotEmpty(doc.Lines);
        Assert.All(doc.Lines, l => Assert.True(l.Start >= TimeSpan.Zero));
        Assert.All(doc.Lines, l => Assert.False(string.IsNullOrWhiteSpace(l.Text)));

        // 与 Helper LyricsData 路径对照（真实数据上的交叉验证）
        var helper = KrcParser.Parse(krc);
        Assert.Equal(helper.Lines!.Count, doc.Lines.Count);
        for (var i = 0; i < doc.Lines.Count; i++)
        {
            Assert.Equal(helper.Lines![i].Text, doc.Lines[i].Text);
        }
    }
}
