using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using YTM.Lyrics.Core.Playback;

namespace YTM.Lyrics.Core.Cleaning;

/// <summary>
/// F3 标题清洗器（PRD F3 / TDD §3.2）。
///
/// 清洗管线（纯函数，按规则快照执行）：
///   1. 规范化：全角 ASCII → 半角、nbsp/全角空格归一、零宽字符剥离
///   2. 剥装饰符号与 emoji
///   3. 剥括号对（含嵌套）：命中 feat./with 白名单的括号内容提取为艺人候选，其余整体剥离
///   4. 剥后缀/前缀噪声（重复直至稳定，最长优先 + 词首边界）
///   5. 分隔符拆分推测 "艺人 - 歌名"（YTM 约定 Artist - Title）；若某段与清洗后 RawArtist 一致则以该段为艺人
///      （兼容旧版 "歌名 - 艺人" 顺序）；剩余段重组为歌名
///   6. 艺人候选排序：清洗后 RawArtist（结构化元数据，最可信）→ 标题拆分艺人 → 括号内 feat./with 艺人，去重保序
///
/// 热加载：FromRulesFile 通过 FileSystemWatcher 监听规则文件，防抖 300ms 后原子替换规则快照；
/// 解析失败保留旧规则不崩溃。Clean 只读 volatile 快照，线程安全。
/// </summary>
public sealed class TitleCleaner : ITitleCleaner, IDisposable
{
    private static readonly Regex WhitespaceCollapseRegex = new(@"\s+", RegexOptions.Compiled);
    private static readonly Regex DanglingSeparatorRegex = new(@"^[\s\-–—―－/|｜]+|[\s\-–—―－/|｜]+$", RegexOptions.Compiled);

    private readonly object _gate = new();
    private volatile CompiledRules _rules;
    private FileSystemWatcher? _watcher;
    private System.Threading.Timer? _reloadTimer;
    private string? _rulesFilePath; // TASK-402：LoadRules 可切换监听目标（原 readonly）
    private bool _disposed;

    /// <summary>规则热加载成功（含首次加载）后触发；失败不触发。</summary>
    public event EventHandler? RulesReloaded;

    private TitleCleaner(CompiledRules rules, string? rulesFilePath)
    {
        _rules = rules;
        _rulesFilePath = rulesFilePath;
    }

    /// <summary>使用内嵌默认规则表（无文件监听）。</summary>
    public static TitleCleaner FromEmbeddedRules() => new(CleaningRuleLoader.LoadEmbeddedCompiled(), null);

    /// <summary>直接注入规则表（测试/编程配置用，无文件监听）。</summary>
    public static TitleCleaner FromRules(CleaningRuleTable table) => new(CompiledRules.Compile(table), null);

    /// <summary>使用外部规则文件并启动 FileSystemWatcher 热加载（规则改动无需重编译）。</summary>
    public static TitleCleaner FromRulesFile(string rulesFilePath)
    {
        if (string.IsNullOrWhiteSpace(rulesFilePath))
            throw new ArgumentNullException(nameof(rulesFilePath));
        var compiled = CleaningRuleLoader.LoadFileCompiled(rulesFilePath);
        var cleaner = new TitleCleaner(compiled, rulesFilePath);
        cleaner.StartWatching();
        return cleaner;
    }

    /// <summary>纯函数：单次调用无副作用，不修改输入；线程安全（读不可变规则快照）。</summary>
    public CleanedTrack Clean(TrackInfo raw) => CleanCore(raw, _rules);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _reloadTimer?.Dispose();
        _reloadTimer = null;
        if (_watcher is not null)
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Dispose();
            _watcher = null;
        }
        GC.SuppressFinalize(this);
    }

    // ---------- 热加载 ----------

    /// <summary>
    /// 热加载规则表（TASK-402 Part D，设置项 cleaner_rules_path 的即时生效入口）：
    /// - path 为空/null → 回退内置默认规则，停止文件监听；
    /// - 否则从文件加载并切换 FileSystemWatcher 到新文件（沿用 300ms 防抖热重载）；
    /// - 加载/解析失败抛异常，由调用方捕获并保留旧规则（与 FromRulesFile 语义一致）。
    /// 线程安全：规则快照原子替换；watcher 切换在 _gate 锁内，Clean 读 volatile 快照不受影响。
    /// </summary>
    public void LoadRules(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            lock (_gate)
            {
                StopWatching();
                _rules = CleaningRuleLoader.LoadEmbeddedCompiled();
            }
            Trace.WriteLine("[TitleCleaner] LoadRules: embedded defaults (watcher stopped)");
            RulesReloaded?.Invoke(this, EventArgs.Empty);
            return;
        }

        var full = Path.GetFullPath(path);
        var compiled = CleaningRuleLoader.LoadFileCompiled(full); // 失败抛异常 → 保留旧规则
        lock (_gate)
        {
            StopWatching();
            _rules = compiled;
            _rulesFilePath = full;
            StartWatching();
        }
        Trace.WriteLine($"[TitleCleaner] LoadRules: {full}");
        RulesReloaded?.Invoke(this, EventArgs.Empty);
    }

    private void StartWatching()
    {
        var full = Path.GetFullPath(_rulesFilePath!);
        var dir = Path.GetDirectoryName(full)!;
        var file = Path.GetFileName(full);
        _watcher = new FileSystemWatcher(dir, file)
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName | NotifyFilters.CreationTime,
            EnableRaisingEvents = true,
        };
        _watcher.Changed += OnFileChanged;
        _watcher.Created += OnFileChanged;
        _watcher.Renamed += OnFileChanged;
        _watcher.Deleted += OnFileChanged;
        _reloadTimer = new System.Threading.Timer(_ => TryReload(), null, Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>停掉文件监听与防抖 timer（LoadRules 切换规则源时调用，锁内执行）。</summary>
    private void StopWatching()
    {
        if (_watcher is not null)
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Dispose();
            _watcher = null;
        }
        _reloadTimer?.Dispose();
        _reloadTimer = null;
    }

    private void OnFileChanged(object? sender, FileSystemEventArgs e)
    {
        if (_disposed) return;
        _reloadTimer?.Change(300, Timeout.Infinite); // 防抖：连续写入只触发一次重载
    }

    private void TryReload()
    {
        try
        {
            var compiled = CleaningRuleLoader.LoadFileCompiled(_rulesFilePath!);
            _rules = compiled; // 原子替换快照
            Trace.WriteLine("[TitleCleaner] rules hot-reloaded");
            RulesReloaded?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[TitleCleaner] rules reload failed, keep old rules: {ex.Message}");
        }
    }

    // ---------- 清洗管线（纯函数） ----------

    private static CleanedTrack CleanCore(TrackInfo raw, CompiledRules rules)
    {
        var rawTitle = raw.RawTitle ?? string.Empty;
        var rawArtist = raw.RawArtist ?? string.Empty;

        var title = Normalize(rawTitle);
        var artist = Normalize(rawArtist);
        var extracted = new List<string>(); // 括号内 feat./with 提取的艺人（按出现顺序）

        title = rules.DecorationRegex.Replace(title, " ");
        title = StripBrackets(title, rules, extracted);
        title = StripAffixes(title, rules);
        title = rules.DecorationRegex.Replace(title, " ");
        title = CollapseWhitespace(title);

        // RawArtist（频道名）路径：剥 " - Topic" 等频道后缀
        var cleanArtist = rules.DecorationRegex.Replace(artist, " ");
        cleanArtist = CollapseWhitespace(StripAffixes(cleanArtist, rules));

        // 分隔符拆分：YTM 约定 Artist - Title；若某段与清洗后 RawArtist 一致则以该段为艺人（兼容旧格式 Song - Artist）
        var segments = SplitOnSeparators(title, rules);
        string? splitArtist = null;
        if (segments.Count > 1)
        {
            var idx = FindArtistSegment(segments, cleanArtist);
            if (idx >= 0)
            {
                splitArtist = segments[idx];
                segments.RemoveAt(idx);
                title = string.Join(" - ", segments);
            }
            else if (IsRomanizedTitleTail(segments))
            {
                // 官方频道「歌名 - 罗马音/英文副标题」（如「トリセツ - Torisetsu」）：
                // 首段 CJK 歌名 + 后续纯拉丁 → 首段为歌名，剥离后续转写段（艺人已在 RawArtist 频道名里）
                title = segments[0];
            }
            else
            {
                splitArtist = segments[0]; // 约定序：首段为艺人
                segments.RemoveAt(0);
                title = string.Join(" - ", segments);
            }
        }

        title = StripAffixes(title, rules); // 重组后可能残留 " - YouTube Music" 等
        title = rules.DecorationRegex.Replace(title, " ");
        title = CollapseWhitespace(title);
        title = TrimDanglingSeparators(title);

        if (string.IsNullOrWhiteSpace(title))
            title = rawTitle.Trim(); // 全剥空时回退原始标题，绝不返回空

        var candidates = new List<string>();
        AddCandidate(candidates, cleanArtist); // 结构化元数据艺人最可信，排最前
        AddCandidate(candidates, splitArtist); // 标题拆分艺人
        foreach (var name in extracted)        // 括号内 feat./with 艺人
            AddCandidate(candidates, name);

        return new CleanedTrack(title, candidates);
    }

    /// <summary>剥括号对（含嵌套）：先找最内层对；白名单命中且无噪声词 → 提取艺人，否则整体剥离。括号位置补空格防粘连。</summary>
    private static string StripBrackets(string s, CompiledRules rules, List<string> extracted)
    {
        for (var pass = 0; pass < rules.MaxBracketPasses; pass++)
        {
            var closeIdx = -1;
            for (var i = 0; i < s.Length; i++)
            {
                if (rules.BracketCloseSet.Contains(s[i])) { closeIdx = i; break; }
            }
            if (closeIdx < 0) break;

            var closeCh = s[closeIdx];
            var openCh = rules.OpenForClose[closeCh];
            var openIdx = -1;
            for (var i = closeIdx - 1; i >= 0; i--)
            {
                if (s[i] == openCh) { openIdx = i; break; }
            }

            if (openIdx < 0)
            {
                s = s.Remove(closeIdx, 1); // 孤立闭括号直接删除
                continue;
            }

            var inner = s.Substring(openIdx + 1, closeIdx - openIdx - 1);
            var trimmed = inner.Trim();
            if (trimmed.Length > 0 && rules.FeatKeywordRegex.IsMatch(trimmed))
            {
                var hasNoise = rules.FeatNoiseTokens.Any(t => inner.Contains(t, StringComparison.OrdinalIgnoreCase));
                if (!hasNoise)
                    ExtractArtists(trimmed, rules, extracted);
            }
            s = s.Remove(openIdx, closeIdx - openIdx + 1).Insert(openIdx, " ");
        }
        return s;
    }

    /// <summary>从白名单括号内容提取艺人：去掉 feat./with 关键词前缀，按 &/and/、 拆分，逐段清洗入列。</summary>
    private static void ExtractArtists(string inner, CompiledRules rules, List<string> extracted)
    {
        var m = rules.FeatKeywordRegex.Match(inner);
        if (!m.Success) return;
        var rest = inner.Substring(m.Length).Trim(' ', '\t', '-', '–', '—', ':', '：', '/', '\\', '|');
        if (rest.Length == 0) return;
        foreach (var part in rules.ArtistSplitRegex.Split(rest))
        {
            var name = CollapseWhitespace(part).Trim(' ', '&', '＆', '、', '-', '–');
            if (name.Length > 0)
                extracted.Add(name);
        }
    }

    /// <summary>剥后缀/前缀噪声：最长优先，重复直至稳定（MaxAffixPasses 护栏）。</summary>
    private static string StripAffixes(string s, CompiledRules rules)
    {
        if (string.IsNullOrEmpty(s)) return s;
        for (var pass = 0; pass < rules.MaxAffixPasses; pass++)
        {
            var before = s;
            foreach (var rx in rules.SuffixRegexes)
            {
                var m = rx.Match(s);
                if (m.Success) { s = s.Remove(m.Index, m.Length); break; }
            }
            foreach (var rx in rules.PrefixRegexes)
            {
                var m = rx.Match(s);
                if (m.Success) { s = s.Remove(m.Index, m.Length); break; }
            }
            if (s == before) break;
            s = s.Trim();
        }
        return s;
    }

    /// <summary>按分隔符全切段（保留顺序），空段丢弃。</summary>
    private static List<string> SplitOnSeparators(string s, CompiledRules rules)
    {
        var result = new List<string>();
        var last = 0;
        foreach (Match m in rules.SeparatorRegex.Matches(s))
        {
            var seg = s.Substring(last, m.Index - last).Trim();
            if (seg.Length > 0) result.Add(seg);
            last = m.Index + m.Length;
        }
        var tail = s.Substring(last).Trim();
        if (tail.Length > 0) result.Add(tail);
        return result;
    }

    /// <summary>找与清洗后 RawArtist 完全一致的段（大小写不敏感），用于修正 "Song - Artist" 旧格式。</summary>
    private static int FindArtistSegment(List<string> segments, string cleanArtist)
    {
        if (string.IsNullOrEmpty(cleanArtist)) return -1;
        for (var i = 0; i < segments.Count; i++)
        {
            if (string.Equals(segments[i], cleanArtist, StringComparison.OrdinalIgnoreCase))
                return i;
        }
        return -1;
    }

    /// <summary>
    /// 「歌名 - 罗马音/英文副标题」模式判断：首段含 CJK（假名/汉字），所有后续段均不含 CJK（纯拉丁）。
    /// 典型 YTM 官方频道标题「トリセツ - Torisetsu」——首段是日文歌名，后续是罗马音转写；
    /// 此格式不应把首段当艺人（艺人已在 RawArtist 频道名中），否则「トリセツ」被误当艺人、
    /// 「Torisetsu」被误当歌名，导致按罗马音搜索搜不到日文歌名。
    /// </summary>
    private static bool IsRomanizedTitleTail(List<string> segments)
    {
        if (!ContainsCjk(segments[0]))
        {
            return false;
        }

        for (var i = 1; i < segments.Count; i++)
        {
            if (ContainsCjk(segments[i]))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>是否含 CJK 字符：平假名/片假名 \u3040-\u30FF，或统一汉字 \u4E00-\u9FFF。</summary>
    private static bool ContainsCjk(string s)
    {
        foreach (var c in s)
        {
            if (c is >= '\u3040' and <= '\u30FF') return true;
            if (c is >= '\u4E00' and <= '\u9FFF') return true;
        }
        return false;
    }

    /// <summary>规范化：全角 ASCII（FF01–FF5E）→ 半角；nbsp/全角空格 → 空格；零宽字符剥离。</summary>
    private static string Normalize(string s)
    {
        if (string.IsNullOrEmpty(s)) return s;
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
        {
            if (c is >= '\uFF01' and <= '\uFF5E') { sb.Append((char)(c - 0xFEE0)); continue; }
            switch (c)
            {
                case '\u00A0':
                case '\u3000':
                    sb.Append(' ');
                    break;
                case '\u200B':
                case '\u200C':
                case '\u200D':
                case '\uFEFF':
                    break; // 零宽字符直接剥离
                default:
                    sb.Append(c);
                    break;
            }
        }
        return sb.ToString();
    }

    private static string CollapseWhitespace(string s)
        => WhitespaceCollapseRegex.Replace(s ?? "", " ").Trim();

    /// <summary>剥串首/串尾悬空分隔符（"- " "／" 等）。</summary>
    private static string TrimDanglingSeparators(string s)
        => DanglingSeparatorRegex.Replace(s ?? "", "").Trim();

    /// <summary>去重（大小写不敏感）并追加候选。</summary>
    private static void AddCandidate(List<string> candidates, string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        var n = CollapseWhitespace(name);
        if (n.Length == 0) return;
        if (candidates.Any(x => string.Equals(x, n, StringComparison.OrdinalIgnoreCase))) return;
        candidates.Add(n);
    }
}
