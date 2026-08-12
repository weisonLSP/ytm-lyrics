using System.Text.RegularExpressions;

namespace YTM.Lyrics.Core.Cleaning;

/// <summary>
/// 规则表编译快照：不可变，供 TitleCleaner 以 volatile 引用原子替换（热加载）。
/// 正则均在加载/热加载时一次性编译；Clean 只读快照，无共享可变状态。
/// </summary>
internal sealed class CompiledRules
{
    public required IReadOnlyDictionary<char, char> OpenForClose { get; init; }
    public required IReadOnlySet<char> BracketCloseSet { get; init; }

    /// <summary>括号内白名单匹配：^(?:feat\.?|featuring|ft\.?|with)(?:\s|$)|^(?:参与|合作|…)（拉丁词要求空格/串尾边界）。</summary>
    public required Regex FeatKeywordRegex { get; init; }

    /// <summary>白名单命中后的噪声词表（小写）。</summary>
    public required string[] FeatNoiseTokens { get; init; }

    /// <summary>后缀正则（最长优先）：(?:\s*[-–—―－]\s*|\s+|^)suffix\s*$，IgnoreCase。</summary>
    public required Regex[] SuffixRegexes { get; init; }

    /// <summary>前缀正则：^\s*prefix(?:\s*[-–—―－]\s*|\s+)?，IgnoreCase。</summary>
    public required Regex[] PrefixRegexes { get; init; }

    /// <summary>装饰符号 + emoji 区间（含变体选择符/ZWJ）。</summary>
    public required Regex DecorationRegex { get; init; }

    /// <summary>分隔符合并正则（匹配全部出现位置，用于切段）。</summary>
    public required Regex SeparatorRegex { get; init; }

    /// <summary>feat. 艺人内部拆分（& / and / 、）。</summary>
    public required Regex ArtistSplitRegex { get; init; }

    public required int MaxAffixPasses { get; init; }
    public required int MaxBracketPasses { get; init; }

    public static CompiledRules Compile(CleaningRuleTable t)
    {
        var openForClose = new Dictionary<char, char>();
        var closeSet = new HashSet<char>();
        foreach (var b in t.Brackets)
        {
            openForClose[b.Close[0]] = b.Open[0];
            closeSet.Add(b.Close[0]);
        }

        // feat 关键词：拉丁尾字符要求空格/串尾边界（防 "without" 命中 "with"）；CJK 关键词不附加约束（"参与王菲"）
        var kws = t.FeatKeywords
            .Where(k => k.Length > 0)
            .OrderByDescending(k => k.Length)
            .Select(k => char.IsAscii(k[^1]) ? $"{Regex.Escape(k)}(?:\\s|$)" : Regex.Escape(k));
        var featRx = new Regex($"^(?:{string.Join("|", kws)})", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        // 后缀：最长优先 + 词首边界（空格/连字符/串首），防误伤 "Alive"/"Recover" 等
        var suffixes = t.Suffixes
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .OrderByDescending(s => s.Length)
            .Select(s => new Regex($@"(?:\s*[-–—―－]\s*|\s+|^){Regex.Escape(s.Trim())}\s*$",
                RegexOptions.Compiled | RegexOptions.IgnoreCase))
            .ToArray();

        var prefixes = t.Prefixes
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .OrderByDescending(s => s.Length)
            .Select(s => new Regex($@"^\s*{Regex.Escape(s.Trim())}(?:\s*[-–—―－]\s*|\s+)?",
                RegexOptions.Compiled | RegexOptions.IgnoreCase))
            .ToArray();

        // 装饰符：emoji（增补平面 U+1F000–U+1FFFF 的代理对）+ 常用符号区间 + 可配符号表
        var emojiRanges = @"[\uD83C-\uD83F][\uDC00-\uDFFF]";
        var symbolRanges = @"[\u2600-\u27BF\u2B00-\u2BFF\u2190-\u21FF\u2300-\u23FF\u25A0-\u25FF\uFE0F\u200D\uFE0E-\uFE0F]";
        var decoParts = new List<string> { emojiRanges, symbolRanges };
        decoParts.AddRange(t.Decorations.Where(d => d.Length > 0).Select(Regex.Escape));
        var decoRx = new Regex(string.Join("|", decoParts), RegexOptions.Compiled);

        // 分隔符：半角连字符需空格邻接；长/全角破折号允许紧邻；斜杠需空格邻接或左右为 CJK（"周杰伦/晴天" 拆、"AC/DC" 不拆）
        var sepParts = new List<string>();
        foreach (var tok in t.Separators)
        {
            if (string.IsNullOrWhiteSpace(tok)) continue;
            if (tok.Contains('—') || tok.Contains('―') || tok.Contains('－'))
                sepParts.Add(@"\s*[—―－]\s*");
            else if (tok.Contains('-') || tok.Contains('–'))
                sepParts.Add(@"\s*[-–]\s+|\s+[-–]\s*");
            else if (tok.Contains('/') || tok.Contains('／'))
                sepParts.Add(@"\s+/|\s*/\s+|(?<=[\u4E00-\u9FFF])/"); // 零宽 lookbehind，不消费 CJK 字符
            else
                sepParts.Add($@"\s*{Regex.Escape(tok)}\s*");
        }
        var sepRx = new Regex(string.Join("|", sepParts.Distinct()), RegexOptions.Compiled);

        var splitParts = t.ArtistSplitTokens.Where(s => s.Length > 0).Select(Regex.Escape);
        var splitRx = new Regex(string.Join("|", splitParts), RegexOptions.Compiled | RegexOptions.IgnoreCase);

        return new CompiledRules
        {
            OpenForClose = openForClose,
            BracketCloseSet = closeSet,
            FeatKeywordRegex = featRx,
            FeatNoiseTokens = t.FeatNoiseTokens.Where(x => x.Length > 0).ToArray(),
            SuffixRegexes = suffixes,
            PrefixRegexes = prefixes,
            DecorationRegex = decoRx,
            SeparatorRegex = sepRx,
            ArtistSplitRegex = splitRx,
            MaxAffixPasses = t.MaxAffixPasses,
            MaxBracketPasses = t.MaxBracketPasses,
        };
    }
}
