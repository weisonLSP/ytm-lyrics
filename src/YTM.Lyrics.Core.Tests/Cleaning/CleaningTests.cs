using YTM.Lyrics.Core.Cleaning;
using YTM.Lyrics.Core.Playback;

namespace YTM.Lyrics.Core.Tests.Cleaning;

/// <summary>
/// F3 标题清洗器单测（TDD §6：20 条真实风格脏标题样本，清洗正确率 ≥ 90%）。
/// 样本覆盖：MV / Lyric Video / Cover / Live / feat./with 提取 / - Topic / emoji / 多重括号 / 中英文混合。
/// </summary>
public class CleaningTests
{
    private readonly ITitleCleaner _cleaner = TitleCleaner.FromEmbeddedRules();

    // 20 条样本：RawTitle / RawArtist / 期望歌名 / 期望艺人候选（有序）/ 来源模式说明
    public static IEnumerable<object[]> Samples()
    {
        yield return new object[] { "【4K】Taylor Swift - Love Story (Official Lyric Video)", "Taylor Swift - Topic", "Love Story", new[] { "Taylor Swift" }, "【4K】括号 + Official Lyric Video + artist 带 - Topic" };
        yield return new object[] { "周杰伦 - 晴天 (Official MV)", "周杰伦 - Topic", "晴天", new[] { "周杰伦" }, "中文 + Official MV" };
        yield return new object[] { "Ed Sheeran - Perfect (feat. Beyoncé)", "Ed Sheeran - Topic", "Perfect", new[] { "Ed Sheeran", "Beyoncé" }, "feat. 括号艺人提取" };
        yield return new object[] { "🎵 Alan Walker - Faded 🎵", "Alan Walker - Topic", "Faded", new[] { "Alan Walker" }, "emoji 前后包围" };
        yield return new object[] { "Lil Nas X - Industry Baby (feat. Jack Harlow) [Official Audio]", "Lil Nas X - Topic", "Industry Baby", new[] { "Lil Nas X", "Jack Harlow" }, "feat. + Official Audio 多重括号" };
        yield return new object[] { "阿冗 - 你的答案 (Cover)", "阿冗 - Topic", "你的答案", new[] { "阿冗" }, "中文 + Cover" };
        yield return new object[] { "陈奕迅 - 十年 (Live)", "陈奕迅 - Topic", "十年", new[] { "陈奕迅" }, "中文 + Live" };
        yield return new object[] { "Imagine Dragons - Believer - 4K", "Imagine Dragons - Topic", "Believer", new[] { "Imagine Dragons" }, "裸 4K 后缀 + 连字符" };
        yield return new object[] { "Coldplay - Viva La Vida (Official Music Video) HD", "Coldplay - Topic", "Viva La Vida", new[] { "Coldplay" }, "Official Music Video 括号 + 裸 HD" };
        yield return new object[] { "Dua Lipa - Levitating (feat. DaBaby) (Official Video)", "Dua Lipa - Topic", "Levitating", new[] { "Dua Lipa", "DaBaby" }, "feat. + Official Video 双括号" };
        yield return new object[] { "东京事变 - 群青日和 (Live Ver.)", "东京事变 - Topic", "群青日和", new[] { "东京事变" }, "日文 + Live Ver." };
        yield return new object[] { "💖 IU - Celebrity 💖", "IU - Topic", "Celebrity", new[] { "IU" }, "emoji 包围（emoji 区间）" };
        yield return new object[] { "周深 - 大鱼 (【大鱼海棠】主题曲)", "周深 - Topic", "大鱼", new[] { "周深" }, "嵌套括号（【】 内嵌 ()）" };
        yield return new object[] { "BLACKPINK - Pink Venom (8D Audio)", "BLACKPINK - Topic", "Pink Venom", new[] { "BLACKPINK" }, "8D Audio 括号噪声" };
        yield return new object[] { "Pharrell Williams - Happy (with Miley Cyrus)", "Pharrell Williams - Topic", "Happy", new[] { "Pharrell Williams", "Miley Cyrus" }, "with 括号艺人提取" };
        yield return new object[] { "刀郎 - 2002年的第一场雪 (字幕版)", "刀郎 - Topic", "2002年的第一场雪", new[] { "刀郎" }, "字幕版括号（数字开头的歌名）" };
        yield return new object[] { "Taylor Swift - Blank Space (with lyrics)", "Taylor Swift - Topic", "Blank Space", new[] { "Taylor Swift" }, "with lyrics 噪声守卫：不提取艺人" };
        yield return new object[] { "邓紫棋 - 光年之外 (Live Piano Version)", "邓紫棋 - Topic", "光年之外", new[] { "邓紫棋" }, "Live Piano Version 括号" };
        yield return new object[] { "The Weeknd - Blinding Lights (Official Instrumental)", "The Weeknd - Topic", "Blinding Lights", new[] { "The Weeknd" }, "Instrumental 括号" };
        yield return new object[] { "林俊杰 - 修炼爱情 (feat. 萧敬腾)", "林俊杰 - Topic", "修炼爱情", new[] { "林俊杰", "萧敬腾" }, "中文 feat. 提取" };
    }

    [Theory]
    [MemberData(nameof(Samples))]
    public void 真实脏标题样本_清洗正确(string rawTitle, string rawArtist, string expectedTitle, string[] expectedArtists, string mode)
    {
        Assert.False(string.IsNullOrWhiteSpace(mode)); // 每条样本的来源模式注释（报告用）
        var result = _cleaner.Clean(new TrackInfo(rawTitle, rawArtist, 100));
        Assert.Equal(expectedTitle, result.Title);
        Assert.Equal(expectedArtists, result.ArtistCandidates);
    }

    // ---------- 规则类型覆盖（TDD §3.2：分支覆盖规则表） ----------

    [Theory]
    [InlineData("周杰伦／晴天", "周杰伦 - Topic", "晴天")]
    [InlineData("周杰伦/晴天", "周杰伦 - Topic", "晴天")]
    [InlineData("周杰伦—晴天", "周杰伦 - Topic", "晴天")]          // 长破折号紧邻
    [InlineData("周杰伦 - 晴天", "周杰伦 - Topic", "晴天")]        // 常规空格连字符
    public void 分隔符拆分_艺人歌名结构(string title, string artist, string expected)
    {
        var r = _cleaner.Clean(new TrackInfo(title, artist, 100));
        Assert.Equal(expected, r.Title);
        Assert.Equal(new[] { "周杰伦" }, r.ArtistCandidates);
    }

    [Fact]
    public void 分隔符拆分_斜杠不拆AC_DC品牌名()
    {
        var r = _cleaner.Clean(new TrackInfo("AC/DC - Back in Black", "AC/DC - Topic", 100));
        Assert.Equal("Back in Black", r.Title);
        Assert.Equal(new[] { "AC/DC" }, r.ArtistCandidates);
    }

    [Fact]
    public void 分隔符拆分_旧格式歌名在前_艺人段匹配修正()
    {
        // 旧版 document.title 形如 "Song - Artist"：艺人段与清洗后 RawArtist 一致，反向修正
        var r = _cleaner.Clean(new TrackInfo("Perfect - Ed Sheeran", "Ed Sheeran - Topic", 100));
        Assert.Equal("Perfect", r.Title);
        Assert.Equal(new[] { "Ed Sheeran" }, r.ArtistCandidates);
    }

    [Fact]
    public void 分隔符拆分_多段_艺人段命中后其余重组为歌名()
    {
        var r = _cleaner.Clean(new TrackInfo("Ed Sheeran - Perfect - YouTube Music", "Ed Sheeran - Topic", 100));
        Assert.Equal("Perfect", r.Title);
        Assert.Equal(new[] { "Ed Sheeran" }, r.ArtistCandidates);
    }

    [Fact]
    public void 全角字符规范化()
    {
        var r = _cleaner.Clean(new TrackInfo("Ｅｄ Ｓｈｅｅｒａｎ - Ｐｅｒｆｅｃｔ （Ｏｆｆｉｃｉａｌ ＭＶ）", "Ｅｄ Ｓｈｅｅｒａｎ - Ｔｏｐｉｃ", 100));
        Assert.Equal("Perfect", r.Title);
        Assert.Equal(new[] { "Ed Sheeran" }, r.ArtistCandidates);
    }

    [Fact]
    public void 裸Remix后缀()
    {
        var r = _cleaner.Clean(new TrackInfo("Alan Walker - Faded Remix", "Alan Walker - Topic", 100));
        Assert.Equal("Faded", r.Title);
    }

    [Fact]
    public void YouTubeMusic后缀()
    {
        var r = _cleaner.Clean(new TrackInfo("Ed Sheeran - Perfect - YouTube Music", "Ed Sheeran", 100));
        Assert.Equal("Perfect", r.Title);
        Assert.Equal(new[] { "Ed Sheeran" }, r.ArtistCandidates);
    }

    [Fact]
    public void 清洗后为空_回退原始标题()
    {
        var r = _cleaner.Clean(new TrackInfo("【4K】", "X - Topic", 100));
        Assert.Equal("【4K】", r.Title); // 全剥空时回退原始，绝不返回空
    }

    [Fact]
    public void 艺人缺失_候选为空()
    {
        var r = _cleaner.Clean(new TrackInfo("Some Song", "", 100));
        Assert.Equal("Some Song", r.Title);
        Assert.Empty(r.ArtistCandidates);
    }

    [Fact]
    public void 媒体会话干净路径_无Topic不误伤()
    {
        var r = _cleaner.Clean(new TrackInfo("Pink Floyd - Comfortably Numb", "Pink Floyd", 100));
        Assert.Equal("Comfortably Numb", r.Title);
        Assert.Equal(new[] { "Pink Floyd" }, r.ArtistCandidates);
    }

    [Fact]
    public void 艺人Topic后缀_仅剥尾不影响歌名()
    {
        var r = _cleaner.Clean(new TrackInfo("Not Like Us", "Kendrick Lamar - Topic", 100));
        Assert.Equal("Not Like Us", r.Title);
        Assert.Equal(new[] { "Kendrick Lamar" }, r.ArtistCandidates);
    }

    [Fact]
    public void feat艺人_内部与号拆分()
    {
        var r = _cleaner.Clean(new TrackInfo("A - B (feat. X & Y)", "A - Topic", 100));
        Assert.Equal("B", r.Title);
        Assert.Equal(new[] { "A", "X", "Y" }, r.ArtistCandidates);
    }

    [Fact]
    public void 纯函数_同输入两次结果一致且输入不被修改()
    {
        var input = new TrackInfo("【4K】Taylor Swift - Love Story (Official Lyric Video)", "Taylor Swift - Topic", 100);
        var a = _cleaner.Clean(input);
        var b = _cleaner.Clean(input);
        // record 对 IReadOnlyList 成员按引用比较，故逐字段断言
        Assert.Equal(a.Title, b.Title);
        Assert.Equal(a.ArtistCandidates, b.ArtistCandidates);
        Assert.Equal("【4K】Taylor Swift - Love Story (Official Lyric Video)", input.RawTitle);
        Assert.Equal("Taylor Swift - Topic", input.RawArtist);
    }

    [Fact]
    public void 装饰符剥离_可配符号表()
    {
        var r = _cleaner.Clean(new TrackInfo("★ Song ♪", "Artist - Topic", 100));
        Assert.Equal("Song", r.Title);
    }

    [Fact]
    public void 自定义规则表_前缀剥离()
    {
        var table = new CleaningRuleTable();
        table.Brackets.Add(new BracketRule { Open = "(", Close = ")" });
        table.Separators.Add(" - ");
        table.Prefixes.Add("HD");
        var cleaner = TitleCleaner.FromRules(table);

        var r = cleaner.Clean(new TrackInfo("HD Song", "Artist", 100));
        Assert.Equal("Song", r.Title);
        Assert.Equal(new[] { "Artist" }, r.ArtistCandidates);
    }

    [Fact]
    public void 非法规则表_拒绝加载()
    {
        Assert.Throws<InvalidDataException>(() => CleaningRuleLoader.Parse("{ \"brackets\": [] }"));
        Assert.Throws<InvalidDataException>(() => CleaningRuleLoader.Parse("{ \"brackets\": [ { \"open\": \"(\", \"close\": \")\" } ] }")); // 缺分隔符
    }
}
