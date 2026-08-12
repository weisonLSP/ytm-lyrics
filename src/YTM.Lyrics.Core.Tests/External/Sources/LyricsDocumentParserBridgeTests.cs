using YTM.Lyrics.Core.Engine;
using YTM.Lyrics.Core.External.Sources;
using YTM.Lyrics.Core.Timeline;

namespace YTM.Lyrics.Core.Tests.External.Sources;

/// <summary>
/// 解析缝桥接单测（真实 TASK-204 Parsing 层 + 真实 Helper 解析器，纯内存无网络）：
/// 格式提示映射（坑 #1 显式传类型）、缓存回放路径的按源推导/嗅探（QQ 的 Qrc/Lrc 二义、网易云 Yrc/Lrc 二义）、
/// v1 未支持的格式（Krc/Yrc/Ttml/Musixmatch）经 Parsing 层默认落空文档（TASK-204 扩展点）。
/// </summary>
public class LyricsDocumentParserBridgeTests
{
    private readonly LyricsDocumentParserBridge _bridge = new();

    // 真实形态 QRC（TASK-204 QrcLyricsConverterTests 同款样本）：行标签 [mm:ss.xx]，音节 (start,duration) 毫秒
    private const string QrcText =
        "[ti:Test Song]\r\n" +
        "[00:01.00]你(0,500)好(500,500)\r\n" +
        "[00:02.50]世(0,300)界(300,700)\r\n";

    private const string LrcText =
        "[00:01.00]Hello world\r\n" +
        "[00:02.50]Second line\r\n";

    // 真实形态 YRC：行标签 + <start,duration> 词级标记
    private const string YrcText =
        "[00:01.00]<0,500>你<500,500>好\r\n" +
        "[00:02.50]<0,300>世<300,700>界\r\n";

    private static void AssertNoLines(LyricDocument doc) => Assert.Empty(doc.Lines);

    // ---------- 新取词路径：格式提示直传（坑 #1） ----------

    [Fact]
    public void 提示Qrc_按QRC解析_产出行()
    {
        var doc = _bridge.Parse(QrcText, LyricsSourceKind.QQMusic, LyricsRawFormat.Qrc);
        Assert.Equal(2, doc.Lines.Count);
        Assert.Equal(TimeSpan.FromMilliseconds(1000), doc.Lines[0].Start);
    }

    [Fact]
    public void 提示Lrc_按LRC解析_产出行()
    {
        var doc = _bridge.Parse(LrcText, LyricsSourceKind.QQMusic, LyricsRawFormat.Lrc);
        Assert.Equal(2, doc.Lines.Count);
        Assert.Equal("Hello world", doc.Lines[0].Text);
    }

    [Fact]
    public void 提示Krc_映射Krc类型_v1解析器落空文档()
    {
        var doc = _bridge.Parse("krc-raw-text", LyricsSourceKind.Kugou, LyricsRawFormat.Krc);
        AssertNoLines(doc); // TASK-204 v1 扩展点：Krc 未实现
    }

    [Fact]
    public void 提示Ttml_映射Ttml类型_v1解析器落空文档()
    {
        var doc = _bridge.Parse("<ttml/>", LyricsSourceKind.AppleMusic, LyricsRawFormat.Ttml);
        AssertNoLines(doc);
    }

    [Fact]
    public void 提示MusixmatchJson_映射Musixmatch类型_v1解析器落空文档()
    {
        var doc = _bridge.Parse("{\"body\":{}}", LyricsSourceKind.Musixmatch, LyricsRawFormat.MusixmatchJson);
        AssertNoLines(doc);
    }

    // ---------- 缓存回放路径：无提示，按源推导/嗅探 ----------

    [Fact]
    public void 回放_QQ_QRC文本_嗅探为QRC_产出行()
    {
        var doc = _bridge.Parse(QrcText, LyricsSourceKind.QQMusic, null);
        Assert.Equal(2, doc.Lines.Count);
    }

    [Fact]
    public void 回放_QQ_LRC文本_嗅探为LRC_产出行()
    {
        var doc = _bridge.Parse(LrcText, LyricsSourceKind.QQMusic, null);
        Assert.Equal(2, doc.Lines.Count);
        Assert.Equal("Hello world", doc.Lines[0].Text);
    }

    [Fact]
    public void 回放_网易云_YRC文本_嗅探为Yrc_v1落空文档()
    {
        var doc = _bridge.Parse(YrcText, LyricsSourceKind.Netease, null);
        AssertNoLines(doc); // Yrc 在 TASK-204 v1 为扩展点；关键是不能把 YRC 当 LRC 误解析出乱行
    }

    [Fact]
    public void 回放_网易云_LRC文本_嗅探为LRC_产出行()
    {
        var doc = _bridge.Parse(LrcText, LyricsSourceKind.Netease, null);
        Assert.Equal(2, doc.Lines.Count);
    }

    [Fact]
    public void 回放_酷狗_按源推导Krc_v1落空文档()
    {
        var doc = _bridge.Parse("krc", LyricsSourceKind.Kugou, null);
        AssertNoLines(doc);
    }

    [Fact]
    public void 回放_Apple_按源推导Ttml_v1落空文档()
    {
        var doc = _bridge.Parse("<ttml/>", LyricsSourceKind.AppleMusic, null);
        AssertNoLines(doc);
    }
}
