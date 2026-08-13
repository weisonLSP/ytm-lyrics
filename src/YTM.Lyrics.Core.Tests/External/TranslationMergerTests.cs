using YTM.Lyrics.Core.Engine;
using YTM.Lyrics.Core.External.Parsing;
using YTM.Lyrics.Core.Timeline;

namespace YTM.Lyrics.Core.Tests.External;

/// <summary>
/// TASK-504 TranslationMerger 单测：
/// ① LRC 翻译按时间戳对齐（命中/错位丢弃）② MatchedLine 文本对齐（命中/无匹配）
/// ③ 翻译为空/null 不合并 ④ 翻译行多于/少于主词行不越界 ⑤ 覆盖已有 Translation 值
/// ⑥ 中间格式损坏静默降级（不抛异常）。
/// </summary>
public class TranslationMergerTests
{
    private static LyricDocument Doc(params LyricLine[] lines)
        => new() { Lines = lines, Source = LyricsSourceKind.Netease };

    private static LyricLine Line(int startMs, string text, string? translation = null)
        => new(TimeSpan.FromMilliseconds(startMs), null, text, translation, null);

    // ---- ① LRC 时间戳对齐 ----

    [Fact]
    public void Lrc翻译_时间戳命中_填入Translation()
    {
        var doc = Doc(Line(1000, "第一句"), Line(2500, "第二句"));
        const string lrc = "[00:01.00]First line\r\n[00:02.50]Second line\r\n";

        var merged = TranslationMerger.Merge(doc, lrc, LyricsSourceKind.Netease);

        Assert.Equal("First line", merged.Lines[0].Translation);
        Assert.Equal("Second line", merged.Lines[1].Translation);
    }

    [Fact]
    public void Lrc翻译_时间戳错位_丢弃不填()
    {
        // 翻译行时间戳（2000ms）与主词行（1000/2500）都不同 → 全部不命中
        var doc = Doc(Line(1000, "第一句"), Line(2500, "第二句"));
        const string lrc = "[00:02.00]错位的翻译\r\n";

        var merged = TranslationMerger.Merge(doc, lrc, LyricsSourceKind.Netease);

        Assert.Null(merged.Lines[0].Translation);
        Assert.Null(merged.Lines[1].Translation);
        Assert.Equal(2, merged.Lines.Count);
    }

    [Fact]
    public void Lrc翻译_主词非10ms整倍数_量化到格点后命中()
    {
        // TASK-504 返工裁决：QQ 主词 QRC 绝对毫秒（1547ms）vs 翻译 LRC 百分秒（1540ms）
        // 量化 1547→1540 后命中；这是格点内精确匹配，不是模糊容差
        var doc = Doc(Line(1547, "夢ならば"), Line(6882, "未だに"));
        const string lrc = "[00:01.54]如果只是一场梦\r\n[00:06.88]你依旧出现在我梦里\r\n";

        var merged = TranslationMerger.Merge(doc, lrc, LyricsSourceKind.Netease);

        Assert.Equal("如果只是一场梦", merged.Lines[0].Translation); // 1547→1540 命中
        Assert.Equal("你依旧出现在我梦里", merged.Lines[1].Translation); // 6882→6880 命中
    }

    [Fact]
    public void Lrc翻译_量化不越格_差一个格点不命中()
    {
        // 主词 1547 量化 1540，翻译 1550 量化 1550 → 不同格点不命中（防止量化退化为模糊匹配错位）
        var doc = Doc(Line(1547, "主词行"));
        const string lrc = "[00:01.55]下一格的翻译\r\n";

        var merged = TranslationMerger.Merge(doc, lrc, LyricsSourceKind.Netease);

        Assert.Null(merged.Lines[0].Translation);
    }

    [Fact]
    public void Lrc翻译_部分命中_部分丢弃()
    {
        var doc = Doc(Line(1000, "第一句"), Line(2500, "第二句"), Line(4000, "第三句"));
        const string lrc = "[00:01.00]译文一\r\n[00:02.50]译文二\r\n[00:09.99]对不上的译文\r\n";

        var merged = TranslationMerger.Merge(doc, lrc, LyricsSourceKind.Netease);

        Assert.Equal("译文一", merged.Lines[0].Translation);
        Assert.Equal("译文二", merged.Lines[1].Translation);
        Assert.Null(merged.Lines[2].Translation); // 4000ms 主词行：翻译无该时间戳
    }

    [Fact]
    public void Lrc翻译_元数据行与无时间戳行_不参与对齐()
    {
        // 翻译 LRC 含 [ti:]/[ar:]/[by:] 元数据 + 无时间戳纯文本 → 解析后只剩有效时间戳行
        var doc = Doc(Line(1000, "第一句"));
        const string lrc =
            "[ti:Title]\r\n[ar:Artist]\r\n[by:someone]\r\n" +
            "这是无时间戳的纯文本\r\n" +
            "[00:01.00]译文\r\n";

        var merged = TranslationMerger.Merge(doc, lrc, LyricsSourceKind.Netease);

        Assert.Equal("译文", merged.Lines[0].Translation);
    }

    [Fact]
    public void Lrc翻译_覆盖已有Translation值()
    {
        // 主歌词已带内嵌翻译（双行 LRC 归并产物），外部翻译按时间戳覆盖（TASK-504：覆盖 null 或已有值）
        var doc = Doc(Line(1000, "第一句", "旧译文"));
        const string lrc = "[00:01.00]新译文\r\n";

        var merged = TranslationMerger.Merge(doc, lrc, LyricsSourceKind.Netease);

        Assert.Equal("新译文", merged.Lines[0].Translation);
    }

    // ---- ② MatchedLine 文本对齐 ----

    [Fact]
    public void MatchedLine_命中_填入Translation()
    {
        var doc = Doc(Line(1000, "Line one"), Line(2000, "Line two"));
        const string json = """[{"matchedLine":"Line one","translation":"译文一"},{"matchedLine":"Line two","translation":"译文二"}]""";

        var merged = TranslationMerger.Merge(doc, json, LyricsSourceKind.Musixmatch);

        Assert.Equal("译文一", merged.Lines[0].Translation);
        Assert.Equal("译文二", merged.Lines[1].Translation);
    }

    [Fact]
    public void MatchedLine_Trim后匹配_首尾空白不敏感()
    {
        var doc = Doc(Line(1000, "  Line one  "), Line(2000, "Line two"));
        const string json = """[{"matchedLine":"Line one","translation":"译文"}]""";

        var merged = TranslationMerger.Merge(doc, json, LyricsSourceKind.Musixmatch);

        Assert.Equal("译文", merged.Lines[0].Translation);   // 主词行 Trim 后匹配
        Assert.Null(merged.Lines[1].Translation);            // 未命中行不填
    }

    [Fact]
    public void MatchedLine_无匹配_全部不填()
    {
        var doc = Doc(Line(1000, "Line one"));
        const string json = """[{"matchedLine":"完全不存在的行","translation":"译文"}]""";

        var merged = TranslationMerger.Merge(doc, json, LyricsSourceKind.Musixmatch);

        Assert.Null(merged.Lines[0].Translation);
    }

    [Fact]
    public void MatchedLine_空原文行或空译文条目_跳过()
    {
        var doc = Doc(Line(1000, "Line one"));
        const string json = """[{"matchedLine":"","translation":"无原文"},{"matchedLine":"Line one","translation":""},{"matchedLine":"Line one","translation":"有效译文"}]""";

        var merged = TranslationMerger.Merge(doc, json, LyricsSourceKind.Musixmatch);

        Assert.Equal("有效译文", merged.Lines[0].Translation);
    }

    // ---- ③ 翻译为空/null 不合并 ----

    [Fact]
    public void 翻译为null或空白_不合并_原文档返回()
    {
        var doc = Doc(Line(1000, "第一句"));

        Assert.Same(doc, TranslationMerger.Merge(doc, null!, LyricsSourceKind.Netease));
        Assert.Same(doc, TranslationMerger.Merge(doc, "", LyricsSourceKind.Netease));
        Assert.Same(doc, TranslationMerger.Merge(doc, "   ", LyricsSourceKind.Netease));
    }

    // ---- ④ 数量不越界 ----

    [Fact]
    public void 翻译行多于主词行_只填命中行_不越界()
    {
        var doc = Doc(Line(1000, "第一句"));
        const string lrc =
            "[00:01.00]命中译文\r\n[00:02.00]多余译文1\r\n[00:03.00]多余译文2\r\n[00:04.00]多余译文3\r\n";

        var merged = TranslationMerger.Merge(doc, lrc, LyricsSourceKind.Netease);

        Assert.Single(merged.Lines);
        Assert.Equal("命中译文", merged.Lines[0].Translation);
    }

    [Fact]
    public void 翻译行少于主词行_未命中行保持null()
    {
        var doc = Doc(Line(1000, "第一句"), Line(2000, "第二句"), Line(3000, "第三句"));
        const string lrc = "[00:01.00]译文\r\n";

        var merged = TranslationMerger.Merge(doc, lrc, LyricsSourceKind.Netease);

        Assert.Equal(3, merged.Lines.Count);
        Assert.Equal("译文", merged.Lines[0].Translation);
        Assert.Null(merged.Lines[1].Translation);
        Assert.Null(merged.Lines[2].Translation);
    }

    [Fact]
    public void 主歌词为空文档_不合并()
    {
        var empty = new LyricDocument { Lines = Array.Empty<LyricLine>(), Source = LyricsSourceKind.Netease };

        Assert.Same(empty, TranslationMerger.Merge(empty, "[00:01.00]译文", LyricsSourceKind.Netease));
    }

    // ---- ⑤⑥ 降级与分发 ----

    [Fact]
    public void Musixmatch中间格式损坏_静默降级_不抛异常()
    {
        var doc = Doc(Line(1000, "Line one"));

        var merged = TranslationMerger.Merge(doc, "{not valid json", LyricsSourceKind.Musixmatch);

        Assert.Same(doc, merged); // 降级为主歌词无翻译
    }

    [Fact]
    public void 酷狗KRC_不合并_原样返回()
    {
        // KRC 翻译已内嵌 [language:] 段，TranslationText 恒 null；防御性覆盖：非空也不走本合并器
        var doc = Doc(Line(1000, "第一句"));

        Assert.Same(doc, TranslationMerger.Merge(doc, "[00:01.00]译文", LyricsSourceKind.Kugou));
    }

    [Fact]
    public void 翻译LRC解析异常_静默降级()
    {
        var doc = Doc(Line(1000, "第一句"));

        // 不可解析的翻译原文（纯乱码）→ LrcParser 返回空/无有效行 → 不合并不抛
        var merged = TranslationMerger.Merge(doc, "}}}   \\u0000 乱码 [[[", LyricsSourceKind.Netease);

        var line = Assert.Single(merged.Lines);
        Assert.Null(line.Translation);
    }
}
