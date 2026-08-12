namespace YTM.Lyrics.Core.Cleaning;

/// <summary>
/// 外置清洗规则表（JSON 可序列化，规则文件支持 // 注释与尾逗号）。
/// 改动规则无需重编译：TitleCleaner 热加载后原子替换编译快照。
/// </summary>
public sealed class CleaningRuleTable
{
    /// <summary>规则表版本号（仅记录，不参与匹配）。</summary>
    public int Version { get; set; } = 1;

    /// <summary>括号对列表，如 () 【】 []。要求 open/close 均为单字符且 close 互不重复。</summary>
    public List<BracketRule> Brackets { get; set; } = new();

    /// <summary>括号内白名单关键词：命中时把括号内容当作艺人信息提取进 ArtistCandidates（feat./with 等）。</summary>
    public List<string> FeatKeywords { get; set; } = new();

    /// <summary>噪声词：命中白名单的括号内容若包含这些词（如 lyrics/video），放弃提取仅整体剥离。</summary>
    public List<string> FeatNoiseTokens { get; set; } = new();

    /// <summary>
    /// 后缀噪声（Official Music Video / Lyric Video / 4K / HD / Live / Cover / Remix / Topic 等）。
    /// 仅匹配串尾，允许前置空格或连字符分隔，要求词首边界（避免误伤 Alive/Recover 这类词）。
    /// </summary>
    public List<string> Suffixes { get; set; } = new();

    /// <summary>前缀噪声（默认空，机制保留）。</summary>
    public List<string> Prefixes { get; set; } = new();

    /// <summary>装饰符号（★♪※❤ 等，逐字符剥离）；emoji 区间由引擎内置。</summary>
    public List<string> Decorations { get; set; } = new();

    /// <summary>
    /// 分隔符：推测"艺人 - 歌名"结构。
    /// 半角连字符需空格邻接（防拆 A-B 品牌名）；长破折号/全角破折号允许紧邻；斜杠仅空格邻接或左右为 CJK 时生效。
    /// </summary>
    public List<string> Separators { get; set; } = new();

    /// <summary>提取出的 feat. 艺人内部再拆分记号（& / and / 、）。</summary>
    public List<string> ArtistSplitTokens { get; set; } = new();

    /// <summary>后缀/前缀剥离最大轮数（防死循环护栏）。</summary>
    public int MaxAffixPasses { get; set; } = 5;

    /// <summary>括号剥离最大轮数（防死循环护栏）。</summary>
    public int MaxBracketPasses { get; set; } = 64;

    public void Validate()
    {
        if (Brackets.Count == 0)
            throw new InvalidDataException("Cleaning 规则表：必须至少配置一对括号");
        var closes = new HashSet<char>();
        foreach (var b in Brackets)
        {
            if (string.IsNullOrEmpty(b.Open) || string.IsNullOrEmpty(b.Close))
                throw new InvalidDataException("Cleaning 规则表：括号对 Open/Close 不能为空");
            if (b.Open.Length != 1 || b.Close.Length != 1)
                throw new InvalidDataException("Cleaning 规则表：括号对必须为单字符");
            if (!closes.Add(b.Close[0]))
                throw new InvalidDataException($"Cleaning 规则表：闭括号 '{b.Close}' 重复配置");
        }
        if (Separators.Count == 0)
            throw new InvalidDataException("Cleaning 规则表：至少配置一个分隔符");
        if (MaxAffixPasses < 1) MaxAffixPasses = 1;
        if (MaxBracketPasses < 1) MaxBracketPasses = 1;
    }
}

/// <summary>括号对定义（单字符）。</summary>
public sealed class BracketRule
{
    public string Open { get; set; } = "";
    public string Close { get; set; } = "";
}
