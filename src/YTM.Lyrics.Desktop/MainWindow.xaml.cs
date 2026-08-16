using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using YTM.Lyrics.Core.Cleaning;
using YTM.Lyrics.Core.Engine;
using YTM.Lyrics.Core.Playback;
using YTM.Lyrics.Core.Session;
using YTM.Lyrics.Core.Timeline;

namespace YTM.Lyrics.Desktop;

/// <summary>
/// 主窗口（TASK-104 最简版 + TASK-205 歌词面板，M2 收官；TASK-602 按 ADR-13 收敛为纯渲染）。
///
/// 会话编排（播放订阅、TrackSearchDeferrer 延迟搜索、歌词加载、绑定/解绑/重载）已下沉到
/// Core 层 <see cref="LyricsSession"/>（TASK-602）：本窗口只订阅渲染——
/// - LyricsSession.TrackChanged → 清空旧渲染 + 加载中/无播放占位；
/// - LyricsSession.LyricsChanged → 渲染歌词面板/来源栏/状态条（只读 Core 计算出的结果）；
/// - 仍直接订阅 IPlaybackStateMachine 拿 position 做定位（GetInterpolatedPosition + TimelineNavigator
///   二分，DispatcherTimer 200ms 一档；当前行 1.15 倍字号高亮，UI_SPEC §2.1 初值）；
/// - F9 手动搜索/绑定/解绑只调 session 方法并渲染候选列表，业务编排不进窗口。
///
/// 线程纪律（TDD §5）：session 事件在后台线程触发，本窗口一律 marshal 回 UI 线程再渲染；
/// 渲染只读 Core 计算出的当前行索引，Desktop 不重算业务逻辑。
/// </summary>
public partial class MainWindow : Window
{
    // UI_SPEC §2.3 初值：main_lyrics_font_size=32 / main_translation_font_size=20；当前行 1.15x（§2.1）
    // TASK-402：字号改为实例字段，由 settings.main_lyrics_font_size 驱动（保存后即时生效）
    private const double DefaultBaseFontSize = 32;
    private const double CurrentLineScale = 1.15;

    private AppSettings _settings;
    private double _baseFontSize;

    private IPlaybackStateMachine _machine;
    private IPlaybackProvider _provider;
    private readonly ITitleCleaner _cleaner; // 仅 F9 搜索框预填用（纯函数，UI 准备；歌词链路清洗在 session 内）
    private readonly LyricsSession _session;
    private readonly IReadOnlyList<(string Path, int DelayMs)> _shots;
    private readonly DispatcherTimer _ticker;
    private readonly Brush _brushPlaying = new SolidColorBrush(Color.FromRgb(0x8F, 0xD6, 0xB6));
    private readonly Brush _brushPaused  = new SolidColorBrush(Color.FromRgb(0xE8, 0xB9, 0x6B));
    private readonly Brush _brushLost    = new SolidColorBrush(Color.FromRgb(0xE0, 0x6C, 0x75));
    private readonly Brush _brushNeutral = new SolidColorBrush(Color.FromRgb(0x9A, 0x9A, 0xA6));
    private readonly Brush _brushNormal  = new SolidColorBrush(Color.FromRgb(0xC9, 0xC9, 0xD6));
    private readonly Brush _brushActive  = new SolidColorBrush(Color.FromRgb(0xF2, 0xF2, 0xF8));
    private readonly Brush _brushDim     = new SolidColorBrush(Color.FromRgb(0x8A, 0x8A, 0x98));
    private readonly Brush _brushWarn    = new SolidColorBrush(Color.FromRgb(0xE8, 0xB9, 0x6B));
    private readonly Brush _brushItem    = new SolidColorBrush(Color.FromRgb(0x1E, 0x21, 0x30)); // F9 候选列表项底色
    private readonly Brush _brushSelected = new SolidColorBrush(Color.FromRgb(0x2A, 0x3B, 0x55)); // F9 候选选中底色

    private bool _providerLost;
    private string? _f9AutoKeyword; // TASK-401 取证：--f9-auto 关键词（OnLoaded 时启动自动序列）

    /// <summary>
    /// ⑪：主窗口手动绑定/解除绑定/清除缓存重搜成功后触发，App 订阅后中转刷新桌面歌词窗口。
    /// 不含 TrackChanged 自动搜索（桌面歌词窗口自己订阅状态机，已同步，避免重复加载）。
    /// </summary>
    public event EventHandler? ManualLyricsChanged;

    // ---- 歌词状态（仅 UI 线程访问） ----
    private LyricDocument? _doc;
    private int _currentLine = -1;
    private double _offsetSec; // TASK-603 F11：当前曲目偏移（正=歌词提前显示，负=延后；随 LyricsChanged 更新）
    private readonly List<FrameworkElement> _lineElements = new();
    private readonly List<double> _lineHeights = new();

    // ---- TASK-401 F9 手动搜索状态（仅 UI 线程访问） ----
    private readonly List<ScoredCandidate> _flatCandidates = new(); // 展平候选（渲染顺序）
    private int _selectedIndex = -1;                                // 当前选中候选索引

    public MainWindow(
        IPlaybackStateMachine machine,
        IPlaybackProvider provider,
        ITitleCleaner cleaner,
        LyricsSession session,
        IReadOnlyList<(string Path, int DelayMs)> shots,
        string? f9AutoKeyword = null,
        AppSettings? settings = null)
    {
        InitializeComponent();
        _machine = machine;
        _provider = provider;
        _cleaner = cleaner;
        _session = session;
        _shots = shots;
        _f9AutoKeyword = f9AutoKeyword;
        _settings = settings ?? new AppSettings(); // TASK-402：App 传入已加载设置（show_translation / 字号）
        _baseFontSize = _settings.main_lyrics_font_size;

        _provider.SnapshotReceived += OnSnapshotReceived;
        _provider.ProviderLost += OnProviderLost;
        _machine.PlayStateChanged += OnPlayStateChanged;
        _session.TrackChanged += OnSessionTrackChanged;
        _session.LyricsChanged += OnSessionLyricsChanged;

        _ticker = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _ticker.Tick += (_, _) => Refresh();

        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Refresh();
        _ticker.Start();

        // 取证辅助：--shot "path;delayMs" 到点把窗口内容导出 PNG（不参与业务，仅自测截图）
        foreach (var (path, delayMs) in _shots)
        {
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(delayMs) };
            timer.Tick += (_, _) => { timer.Stop(); SaveSnapshot(path); };
            timer.Start();
        }

        // 取证辅助：--f9-auto="keyword" 自动执行 F9 手动搜索→绑定→解除绑定序列（TASK-401 自测，不参与业务）。
        // 时间线（与 t401_push.js 推帧节奏对齐）：
        //   t≈2.5s 自动搜索（此时负缓存 None 已落地、搜索面板已出现）
        //   t≈5.0s 自动选中首个候选并绑定（写绑定缓存，来源条标"用户绑定"）
        //   t≈13s 自动解除绑定（触发重新自动搜索，验收 5）
        if (_f9AutoKeyword is { Length: > 0 })
        {
            StartF9Forensic(_f9AutoKeyword);
        }
    }

    /// <summary>F9 取证序列：搜索 → 绑定 → 解除绑定（定时器驱动，等效 UI 操作）。</summary>
    private void StartF9Forensic(string keyword)
    {
        var searchTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(2500) };
        searchTimer.Tick += (_, _) =>
        {
            searchTimer.Stop();
            SearchKeywordBox.Text = keyword;
            Trace.WriteLine($"[MainWindow] f9-forensic: auto search keyword='{keyword}'");
            SearchAndRenderCandidates();
        };
        searchTimer.Start();

        var bindTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(5000) };
        bindTimer.Tick += (_, _) =>
        {
            bindTimer.Stop();
            if (_flatCandidates.Count > 0)
            {
                SelectCandidate(0);
                Trace.WriteLine("[MainWindow] f9-forensic: auto select candidate 0");
                OnBindClicked(this, new RoutedEventArgs());
            }
            else
            {
                Trace.WriteLine("[MainWindow] f9-forensic: no candidates at bind time, skipped");
            }
        };
        bindTimer.Start();

        var unbindTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(13000) };
        unbindTimer.Tick += (_, _) =>
        {
            unbindTimer.Stop();
            Trace.WriteLine("[MainWindow] f9-forensic: auto unbind");
            OnUnbindClicked(this, new RoutedEventArgs());
        };
        unbindTimer.Start();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _ticker.Stop();
        _session.TrackChanged -= OnSessionTrackChanged;
        _session.LyricsChanged -= OnSessionLyricsChanged;
        _provider.SnapshotReceived -= OnSnapshotReceived;
        _provider.ProviderLost -= OnProviderLost;
        _machine.PlayStateChanged -= OnPlayStateChanged;
    }

    private void OnSnapshotReceived(object? sender, PlaybackSnapshot snapshot)
    {
        _providerLost = false; // 有新帧说明连接活着（探针重连后自动恢复）
        DispatchRefresh();
    }

    private void OnProviderLost(object? sender, EventArgs e)
    {
        _providerLost = true;
        DispatchRefresh();
    }



    /// <summary>
    /// 曲目切换（session 透传，后台线程触发）：清空旧渲染并进入加载中/无播放态。
    /// 仅渲染职责——延迟搜索决策在 session 内（duration=0 时 250ms 轮询等有效时长），窗口不参与。
    /// </summary>
    private void OnSessionTrackChanged(object? sender, EventArgs e)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => OnSessionTrackChanged(sender, e));
            return;
        }

        var current = _machine.Current;
        if (current is null)
        {
            ShowLyricsState("暂无播放", isWarn: false);
            return;
        }

        // 清空旧歌词（切歌竞态结果由 session 代际守卫丢弃，窗口只按最新 LyricsChanged 渲染）
        _doc = null;
        _currentLine = -1;
        ClearLyricsPanel();
        UpdateOffsetButtonsVisibility(); // F11：加载中无歌词，隐藏偏移入口
        ShowLyricsState("加载中…", isWarn: false);
    }

    private void OnPlayStateChanged(object? sender, EventArgs e) => DispatchRefresh();

    private void DispatchRefresh()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(Refresh);
            return;
        }
        Refresh();
    }

    private void Refresh()
    {
        var current = _machine.Current;
        var lost = _providerLost;

        if (current is null)
        {
            TitleText.Text = "—";
            ArtistText.Text = "—";
            ProgressText.Text = "--:-- / --:--";
            StateText.Text = lost ? "探针离线" : "等待数据";
            StateText.Foreground = _brushNeutral;
            SetConnDot(lost ? _brushLost : _brushNeutral);
            ConnText.Text = lost ? "探针离线" : "等待探针连接…";
            return;
        }

        TitleText.Text = current.Track.RawTitle;
        ArtistText.Text = current.Track.RawArtist;

        var pos = _machine.GetInterpolatedPosition();
        ProgressText.Text =
            $"{Format(pos)} / {Format(TimeSpan.FromSeconds(current.Track.DurationSec))}";

        if (lost)
        {
            StateText.Text = "探针离线（位置冻结）";
            StateText.Foreground = _brushLost;
            SetConnDot(_brushLost);
            ConnText.Text = "探针离线";
        }
        else
        {
            StateText.Text = current.Playing ? "播放中" : "已暂停";
            StateText.Foreground = current.Playing ? _brushPlaying : _brushPaused;
            SetConnDot(_brushPlaying);
            ConnText.Text = "探针已连接";
        }

        // TASK-205：当前行跟随外推进度推进（暂停/离线时位置冻结，行保持）
        UpdateLyricsPosition();
    }

    // ---------------- 歌词渲染状态（LyricsSession 结果 → 面板/来源栏/状态） ----------------

    /// <summary>歌词结果到达（session 后台线程触发）：marshal 回 UI 线程后渲染。</summary>
    private void OnSessionLyricsChanged(object? sender, LyricsRenderState state)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => OnSessionLyricsChanged(sender, state));
            return;
        }
        ApplyLyricsState(state);
    }

    /// <summary>把渲染快照应用到面板（结果来自 session 自动搜索或 F9 绑定，渲染逻辑同一套）。</summary>
    private void ApplyLyricsState(LyricsRenderState state)
    {
        _offsetSec = state.OffsetSec; // F11：渲染定位用当前曲目偏移（缓存命中/AdjustOffset 都会带新值）

        if (state.Error is not null)
        {
            _doc = null;
            ClearLyricsPanel();
            UpdateOffsetButtonsVisibility();
            ShowLyricsState("歌词加载失败", isWarn: true);
            SourceBarText.Text = "";
            UnbindButton.Visibility = Visibility.Collapsed;
            return;
        }

        // TASK-205H：每次结果应用先落一行摘要（Source/Confidence/FromCache/UserBound/行数），
        // 与引擎侧 [Engine] 全链路日志衔接成完整观测链
        Trace.WriteLine($"[MainWindow] ApplyLyricsState: source={state.Source?.ToString() ?? "null"} " +
                        $"confidence={state.Confidence} fromCache={state.FromCache} userBound={state.UserBound} " +
                        $"lines={state.Document?.Lines.Count ?? 0}");
        _doc = state.Document;
        var doc = _doc;

        if (doc is null || doc.Lines.Count == 0 || state.Confidence == MatchConfidence.None)
        {
            _doc = null;
            ClearLyricsPanel();
            UpdateOffsetButtonsVisibility();
            // TASK-604 B：自动搜索全失败 → 状态条显示逐源原因汇总（如「QQ音乐:无数据 · 酷狗:无匹配候选」）；
            // Failures 为空（负缓存/正常无词）→ 回退原「暂无歌词」提示
            if (state.Failures is { Count: > 0 })
            {
                var reason = string.Join(" · ", state.Failures.Select(f => $"{SourceName(f.Source)}:{f.Reason ?? FailureStageText(f.Stage)}"));
                ShowLyricsState(reason, isWarn: true);
                Trace.WriteLine($"[MainWindow] lyrics: none with failures: {reason}");
            }
            else
            {
                ShowLyricsState("暂无歌词", isWarn: false);
                Trace.WriteLine("[MainWindow] lyrics: none (no lyrics / negative cache)");
            }
            SourceBarText.Text = "";
            UnbindButton.Visibility = Visibility.Collapsed;
            // F9：None（无结果/负缓存）→ 提供手动搜索入口
            ShowManualSearchPanel(show: true);
            return;
        }

        RenderLyrics(doc);
        _currentLine = -1; // 强制重定位（切歌首帧直接定位，无滚动动画，UI_SPEC §2.1）
        UpdateLyricsPosition();
        ShowSourceBar(doc, state);
        UpdateOffsetButtonsVisibility(); // F11：有歌词 → 显示 ±5s 入口

        // F9 面板联动（TASK-401）：
        // - 绑定结果（UserBound=true）→ 显示"解除绑定"按钮，隐藏搜索面板；
        // - Low 自动结果 → 显示搜索面板（用户可手动校正，PRD S2）；
        // - High 自动结果 → 全部隐藏。
        UnbindButton.Visibility = state.UserBound ? Visibility.Visible : Visibility.Collapsed;
        ShowManualSearchPanel(show: !state.UserBound && state.Confidence == MatchConfidence.Low);
    }

    /// <summary>F9 绑定结果渲染入口：LyricsResult → 渲染快照（绑定成功经 ⑪ 事件链联动重载，不重复渲染）。</summary>
    private void ApplyBindResult(LyricsResult result)
        => ApplyLyricsState(new LyricsRenderState(
            result.Document, result.Source, result.Confidence, result.FromCache, result.UserBound, result.OffsetSec,
            result.Failures));

    /// <summary>F11：无歌词（无词/失败/加载中）时隐藏偏移按钮。</summary>
    private void UpdateOffsetButtonsVisibility()
        => OffsetButtonPanel.Visibility = _doc is { Lines.Count: > 0 } ? Visibility.Visible : Visibility.Collapsed;

    // ---------------- 歌词渲染 ----------------

    private void RenderLyrics(LyricDocument doc)
    {
        LyricsPanel.Children.Clear();
        _lineElements.Clear();
        _lineHeights.Clear();

        foreach (var line in doc.Lines)
        {
            var container = new StackPanel { Margin = new Thickness(0, 8, 0, 8) };
            var main = new TextBlock
            {
                Text = line.Text,
                FontSize = _baseFontSize,
                Foreground = _brushNormal,
                TextWrapping = TextWrapping.Wrap,
            };
            container.Children.Add(main);

            // 翻译行默认显示（PRD 决策3 / F12）：主词下副行小字；TASK-402：受 show_translation 开关控制
            if (_settings.show_translation && !string.IsNullOrWhiteSpace(line.Translation))
            {
                container.Children.Add(new TextBlock
                {
                    Text = line.Translation,
                    FontSize = TranslationFontSizeFor(_baseFontSize),
                    Foreground = _brushDim,
                    Margin = new Thickness(0, 2, 0, 0),
                    TextWrapping = TextWrapping.Wrap,
                });
            }

            LyricsPanel.Children.Add(container);
            _lineElements.Add(container);
        }

        // TASK-FIX-SCROLL：让 ScrollViewer 自身完成 measure+arrange 后再读行高——
        // 仅对子 StackPanel 调 UpdateLayout() 不会向上刷新父 ScrollViewer 的
        // ViewportHeight/ScrollableHeight，新建行元素也可能尚未 arrange（ActualHeight=0）。
        LyricsScroller.UpdateLayout();
        RecomputeLineHeights();
        LyricsStatusText.Text = "";
        LyricsStatusText.Foreground = _brushNeutral;
    }

    private void ClearLyricsPanel()
    {
        LyricsPanel.Children.Clear();
        _lineElements.Clear();
        _lineHeights.Clear();
    }

    private void RecomputeLineHeights()
    {
        // TASK-FIX-SCROLL：行容器 Margin(0,8,0,8) 计入占位高——ScrollViewer 的内容坐标
        //（extent/vOff）按 LyricsPanel 内每个子元素占位（ActualHeight + Margin）累计，
        // 若行高只读 ActualHeight，ScrollToLine 的 top 会每行少 16px，offset 系统性偏小，
        // 当前行被压在视口下方（远离 40% 定位线）。
        _lineHeights.Clear();
        foreach (var el in _lineElements)
        {
            _lineHeights.Add(el.ActualHeight + el.Margin.Top + el.Margin.Bottom);
        }
    }

    /// <summary>ShowLyricsState：歌词状态条文案（加载中 / 暂无歌词 / 低置信提示 / 失败）。</summary>
    private void ShowLyricsState(string text, bool isWarn)
    {
        LyricsStatusText.Text = text;
        LyricsStatusText.Foreground = isWarn ? _brushWarn : _brushNeutral;
    }

    private void ShowSourceBar(LyricDocument doc, LyricsRenderState state)
    {
        var source = SourceName(doc.Source);
        var from = state.FromCache ? "缓存命中" : "网络获取";
        var conf = state.Confidence == MatchConfidence.Low ? "低置信" : "高置信";
        var bound = state.UserBound ? " · 🔗 用户绑定" : ""; // F9：绑定结果标识（任务卡 ⑤）

        if (state.Confidence == MatchConfidence.Low)
        {
            // TDD §3.3：Low 时 UI 明确提示"低置信结果"
            SourceBarText.Text = $"来源：{source} · {from} · {conf}{bound} — ⚠ 低置信结果，请确认歌词是否匹配";
            SourceBarText.Foreground = _brushWarn;
            ShowLyricsState("⚠ 低置信结果，请确认歌词是否匹配", isWarn: true);
        }
        else
        {
            SourceBarText.Text = $"来源：{source} · {from} · {conf}{bound}";
            SourceBarText.Foreground = _brushDim;
        }
    }

    // ---------------- F9 手动搜索、绑定、解除绑定（TASK-401） ----------------

    /// <summary>显示/隐藏手动搜索面板（F9）。隐藏时清空候选列表与选中态（保留输入关键词）。</summary>
    private void ShowManualSearchPanel(bool show)
    {
        ManualSearchPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (show)
        {
            // TASK-502⑦：预填当前曲目清洗后歌名（仅搜索框为空时；用户已有输入不覆盖、不清空）
            if (string.IsNullOrWhiteSpace(SearchKeywordBox.Text) && _machine.Current is { } cur)
            {
                try
                {
                    var cleanedTitle = _cleaner.Clean(cur.Track).Title; // 纯函数同步调用（与 OnBindClicked 同用法）
                    if (!string.IsNullOrWhiteSpace(cleanedTitle))
                    {
                        SearchKeywordBox.Text = cleanedTitle;
                        Trace.WriteLine($"[MainWindow] manual search prefill: '{cleanedTitle}'");
                    }
                }
                catch (Exception ex)
                {
                    // 清洗失败不阻塞面板显示（保留空搜索框）
                    Trace.WriteLine($"[MainWindow] manual search prefill failed: {ex.GetType().Name}: {ex.Message}");
                }
            }
        }
        else
        {
            ClearCandidates();
        }
    }

    private void HideManualSearchPanel() => ShowManualSearchPanel(show: false);

    private void ClearCandidates()
    {
        CandidateListPanel.Children.Clear();
        _flatCandidates.Clear();
        _selectedIndex = -1;
        BindButton.IsEnabled = false;
    }

    private void OnSearchClicked(object sender, RoutedEventArgs e) => SearchAndRenderCandidates();

    private void OnSearchKeywordKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            SearchAndRenderCandidates();
        }
    }

    private async void SearchAndRenderCandidates()
    {
        var keyword = SearchKeywordBox.Text.Trim();
        if (keyword.Length == 0)
        {
            return;
        }

        try
        {
            SearchButton.IsEnabled = false;
            Trace.WriteLine($"[MainWindow] manual search: keyword='{keyword}'");
            var outcome = await _session.SearchCandidatesAsync(keyword);
            var groups = outcome.Groups;
            var total = groups.Sum(g => g.Candidates.Count);
            Trace.WriteLine($"[MainWindow] manual search done: groups={groups.Count} total={total} failures={outcome.Failures.Count}");
            RenderCandidates(groups, outcome.Failures); // TASK-604 B：候选列表底部追加失败源一行
            if (total == 0)
            {
                ShowLyricsState("手动搜索无候选，换个关键词试试", isWarn: true);
            }
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[MainWindow] manual search failed: {ex.GetType().Name}: {ex.Message}");
            ShowLyricsState("手动搜索失败", isWarn: true);
        }
        finally
        {
            SearchButton.IsEnabled = true;
        }
    }

    /// <summary>把分组候选渲染为可点击列表（来源标签、标题、艺人、时长、匹配分）+ 失败源汇总行（TASK-604 B）。</summary>
    private void RenderCandidates(IReadOnlyList<SourceCandidateGroup> groups, IReadOnlyList<SourceFailureDetail> failures)
    {
        ClearCandidates();

        foreach (var group in groups)
        {
            foreach (var cand in group.Candidates)
            {
                var index = _flatCandidates.Count;
                _flatCandidates.Add(cand);

                var item = new Border
                {
                    Background = _brushItem,
                    CornerRadius = new CornerRadius(4),
                    Padding = new Thickness(8, 6, 8, 6),
                    Margin = new Thickness(0, 2, 0, 2),
                    Cursor = Cursors.Hand,
                    Tag = index,
                    Child = new TextBlock
                    {
                        Text = BuildCandidateText(group.Source, cand),
                        FontSize = 13,
                        Foreground = _brushNormal,
                        TextWrapping = TextWrapping.Wrap,
                    },
                };
                item.MouseLeftButtonUp += OnCandidateClicked;
                CandidateListPanel.Children.Add(item);
            }
        }

        if (_flatCandidates.Count == 0)
        {
            CandidateListPanel.Children.Add(new TextBlock
            {
                Text = "无匹配候选",
                FontSize = 13,
                Foreground = _brushDim,
                Margin = new Thickness(4, 6, 4, 6),
            });
        }

        // TASK-604 B：失败源一行（小字灰显），如「Apple Music: 无歌词 · 网易云: 无数据」；
        // 全部失败时显示具体原因而非空列表
        if (failures.Count > 0)
        {
            var text = string.Join(" · ", failures.Select(f => $"{SourceName(f.Source)}: {f.Reason ?? FailureStageText(f.Stage)}"));
            CandidateListPanel.Children.Add(new TextBlock
            {
                Text = text,
                FontSize = 12,
                Foreground = _brushDim,
                Margin = new Thickness(4, 6, 4, 6),
                TextWrapping = TextWrapping.Wrap,
            });
        }
    }

    /// <summary>TASK-604 B：失败阶段兜底文案（Reason 为 null 时使用）。</summary>
    private static string FailureStageText(SourceFailureStage stage) => stage switch
    {
        SourceFailureStage.Search => "搜索失败",
        SourceFailureStage.Fetch => "取词失败",
        SourceFailureStage.Parse => "解析失败",
        SourceFailureStage.NoLyrics => "无歌词",
        _ => "失败",
    };

    private static string BuildCandidateText(LyricsSourceKind source, ScoredCandidate cand)
    {
        var r = cand.Result;
        var artists = r.Artists.Count > 0 ? string.Join("/", r.Artists) : "未知艺人";
        var duration = r.DurationSec is { } d ? Format(TimeSpan.FromSeconds(d)) : "--:--";
        return $"[{SourceName(source)}] {r.Title} - {artists} · {duration} · 匹配 {cand.Score.TotalScore} 分";
    }

    private void OnCandidateClicked(object sender, MouseButtonEventArgs e)
    {
        if (sender is Border { Tag: int index })
        {
            SelectCandidate(index);
        }
    }

    private void SelectCandidate(int index)
    {
        _selectedIndex = index;
        for (var i = 0; i < CandidateListPanel.Children.Count; i++)
        {
            if (CandidateListPanel.Children[i] is Border b)
            {
                b.Background = i == index ? _brushSelected : _brushItem;
            }
        }
        BindButton.IsEnabled = index >= 0; // 选中后启用"使用此歌词"
    }

    private async void OnBindClicked(object sender, RoutedEventArgs e)
    {
        if (_selectedIndex < 0 || _selectedIndex >= _flatCandidates.Count)
        {
            return;
        }

        var current = _machine.Current;
        if (current is null)
        {
            return;
        }

        var selected = _flatCandidates[_selectedIndex].Result;
        try
        {
            Trace.WriteLine($"[MainWindow] bind lyrics: source={selected.Source} id={selected.SourceId} title='{selected.Title}'");
            var result = await _session.BindLyricsAsync(selected);
            if (result.Confidence == MatchConfidence.None)
            {
                Trace.WriteLine($"[MainWindow] bind failed: confidence=None (fetch or parse failed)");
                // TASK-604 B：显示具体原因（如「绑定失败：QQ音乐 取词失败」）；Failures 为空回退旧文案
                var reason = result.Failures is { Count: > 0 }
                    ? $"{SourceName(result.Failures[0].Source)} {result.Failures[0].Reason ?? FailureStageText(result.Failures[0].Stage)}"
                    : "取词或解析未成功";
                ShowLyricsState($"绑定失败：{reason}", isWarn: true);
                return;
            }

            ApplyBindResult(result); // 渲染绑定结果：UserBound=true → 显示"解除绑定"、隐藏搜索面板
            ManualLyricsChanged?.Invoke(this, EventArgs.Empty); // ⑪：App → session.ReloadAsync → 桌面歌词窗口刷新
            Trace.WriteLine($"[MainWindow] bind success: source={result.Source} userBound={result.UserBound}");
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[MainWindow] bind failed: {ex.GetType().Name}: {ex.Message}");
            ShowLyricsState("绑定失败", isWarn: true);
        }
    }

    /// <summary>解除绑定（F9）：删缓存条目后重新触发自动搜索（PRD F9「解除绑定回落到自动搜索」）。</summary>
    private async void OnUnbindClicked(object sender, RoutedEventArgs e)
    {
        // "解除绑定"场景专属：隐藏解除绑定按钮（清除缓存重搜按钮不涉及此控件）
        UnbindButton.Visibility = Visibility.Collapsed;
        await ClearCacheAndReload();
    }

    /// <summary>TASK-503：「清除缓存重搜」入口（搜索面板内按钮）。清当前曲目缓存后重新自动搜索。</summary>
    private async void OnClearCacheClicked(object sender, RoutedEventArgs e)
    {
        await ClearCacheAndReload();
    }

    /// <summary>TASK-503：常驻「搜索歌词」入口（Low/None 被动弹出之外的主动打开通道；预填逻辑见 ShowManualSearchPanel）。</summary>
    private void OnSearchLyricsClicked(object sender, RoutedEventArgs e)
    {
        ShowManualSearchPanel(show: true);
        Trace.WriteLine("[MainWindow] manual search panel opened via toolbar button");
    }

    /// <summary>
    /// 清当前曲目缓存（正文/负缓存/用户绑定条目，UnbindAsync 语义不变）+ 触发重新自动搜索。
    /// OnUnbindClicked（F9 解除绑定）与 OnClearCacheClicked（清除缓存重搜）共用，避免重复实现。
    /// 重载经 ⑪ 事件链：session.ReloadAsync → 两窗口统一刷新（会话编排已下沉 Core，窗口不再自行 StartLoad）。
    /// UnbindButton 的显隐属"解除绑定"场景，由调用方单独处理，不在此方法内。
    /// </summary>
    private async Task ClearCacheAndReload()
    {
        if (_machine.Current is null)
        {
            return;
        }

        try
        {
            if (!await _session.UnbindAsync())
            {
                Trace.WriteLine("[MainWindow] clear cache & reload failed: unbind failed");
                ShowLyricsState("清除缓存重搜失败", isWarn: true);
                return;
            }

            HideManualSearchPanel();
            ManualLyricsChanged?.Invoke(this, EventArgs.Empty); // ⑪：解除绑定/清缓存重搜 → App → session.ReloadAsync
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[MainWindow] clear cache & reload failed: {ex.GetType().Name}: {ex.Message}");
            ShowLyricsState("清除缓存重搜失败", isWarn: true);
        }
    }

    private void OnCloseSearchClicked(object sender, RoutedEventArgs e) => HideManualSearchPanel();

    private static string SourceName(LyricsSourceKind source) => source switch
    {
        LyricsSourceKind.QQMusic => "QQ音乐",
        LyricsSourceKind.Kugou => "酷狗音乐",
        LyricsSourceKind.AppleMusic => "Apple Music",
        LyricsSourceKind.Netease => "网易云音乐",
        LyricsSourceKind.Musixmatch => "Musixmatch",
        _ => source.ToString(),
    };

    // ---------------- 当前行定位（TimelineNavigator + 外推位置） ----------------

    private void UpdateLyricsPosition()
    {
        if (_doc is null || _doc.Lines.Count == 0) return;
        // F11：定位叠加偏移（+5s=歌词提前显示），effectivePosition = position + OffsetSec
        var pos = _machine.GetInterpolatedPosition() + TimeSpan.FromSeconds(_offsetSec);
        var idx = TimelineNavigator.FindCurrentLineIndex(_doc, pos);
        SetCurrentLine(idx);
    }

    private void SetCurrentLine(int idx)
    {
        if (idx == _currentLine) return;

        if (_currentLine >= 0 && _currentLine < _lineElements.Count)
        {
            ApplyLineStyle(_currentLine, active: false);
        }
        _currentLine = idx;

        if (idx >= 0 && idx < _lineElements.Count)
        {
            ApplyLineStyle(idx, active: true);
            // TASK-FIX-SCROLL：字号变化影响行高 → 让父 ScrollViewer 同步完成布局
            //（子级 UpdateLayout 不刷新 ScrollViewer 的视口/滚动范围，滚动 offset 会按旧值计算）
            LyricsScroller.UpdateLayout();
            RecomputeLineHeights();
            ScrollToLine(idx);
        }
    }

    /// <summary>当前行强调：1.15 倍字号 + 加亮（UI_SPEC §2.1 初值；本卡不做缓动，M3 才做）。</summary>
    private void ApplyLineStyle(int idx, bool active)
    {
        if (idx < 0 || idx >= _lineElements.Count) return;
        if (_lineElements[idx] is not StackPanel sp || sp.Children.Count == 0) return;

        var main = (TextBlock)sp.Children[0];
        main.FontSize = active ? _baseFontSize * CurrentLineScale : _baseFontSize;
        main.Foreground = active ? _brushActive : _brushNormal;
        main.FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal;
    }

    /// <summary>滚动：当前行稳定在视口垂直 50% 居中处（UI_SPEC §2.1，Lyricify 4 实测校准，瞬切无缓动）。</summary>
    private void ScrollToLine(int idx)
    {
        if (idx < 0 || idx >= _lineHeights.Count) return;
        double top = 0;
        for (var i = 0; i < idx; i++) top += _lineHeights[i];
        top += _lineHeights[idx] * 0.5;

        var viewport = LyricsScroller.ViewportHeight;
        var offset = top - viewport * 0.5;
        LyricsScroller.ScrollToVerticalOffset(Math.Clamp(offset, 0, LyricsScroller.ScrollableHeight));
    }

    private void SetConnDot(Brush brush) => ConnDot.Fill = brush;

    // ---------------- TASK-402：设置即时生效（UpdateSettings / ReplaceEngine / ReplacePlayback） ----------------

    /// <summary>翻译副行字号：与主字号联动（32→20 与规格初值一致），下限 12 保可读。</summary>
    private static double TranslationFontSizeFor(double baseFontSize) => Math.Max(12, baseFontSize * 0.625);

    /// <summary>
    /// 设置即时生效入口（TASK-402）：更新设置引用 + 主窗口字号；
    /// 有歌词时重建歌词面板（翻译行显隐 / 字号变化都要重渲染并重算行高）。
    /// </summary>
    public void UpdateSettings(AppSettings settings)
    {
        _settings = settings;
        _baseFontSize = settings.main_lyrics_font_size;
        if (_doc is { Lines.Count: > 0 })
        {
            var doc = _doc;
            RenderLyrics(doc);
            _currentLine = -1; // 强制重定位（重建后从当前进度重新定位当前行）
            UpdateLyricsPosition();
        }
        Trace.WriteLine($"[MainWindow] UpdateSettings: font={_baseFontSize} showTranslation={settings.show_translation}");
    }

    /// <summary>
    /// 替换播放链路（TASK-402 RestartWebSocketListener：ws_port 变更后 provider/machine 重建）：
    /// 退订旧链路事件、绑定新链路、清空歌词回到等待态（探针重连推帧后自然恢复）。
    /// 编排侧换绑（取消在途/延迟搜索）由 App 调 session.ReplacePlayback 完成，本方法只管渲染与事件重订。
    /// </summary>
    public void ReplacePlayback(IPlaybackStateMachine machine, IPlaybackProvider provider)
    {
        _provider.SnapshotReceived -= OnSnapshotReceived;
        _provider.ProviderLost -= OnProviderLost;
        _machine.PlayStateChanged -= OnPlayStateChanged;

        _machine = machine;
        _provider = provider;

        _provider.SnapshotReceived += OnSnapshotReceived;
        _provider.ProviderLost += OnProviderLost;
        _machine.PlayStateChanged += OnPlayStateChanged;

        _doc = null;
        _currentLine = -1;
        ClearLyricsPanel();
        ShowLyricsState("等待数据", isWarn: false);
        Refresh();
        Trace.WriteLine("[MainWindow] playback replaced (provider/machine rebuilt)");
    }

    // ---------------- TASK-603：F11 歌词偏移（±5s，同一 session 状态与桌面歌词菜单联动） ----------------

    /// <summary>歌词延后 5s：OffsetSec -5，渲染定位 effectivePosition = position - 5。</summary>
    private void OnOffsetMinusClicked(object sender, RoutedEventArgs e)
    {
        _session.AdjustOffset(-5);
        Trace.WriteLine("[MainWindow] offset -5s requested");
    }

    /// <summary>歌词提前 5s：OffsetSec +5，渲染定位 effectivePosition = position + 5。</summary>
    private void OnOffsetPlusClicked(object sender, RoutedEventArgs e)
    {
        _session.AdjustOffset(+5);
        Trace.WriteLine("[MainWindow] offset +5s requested");
    }

    /// <summary>主窗口设置按钮（Grid.Row=7）：打开非模态设置窗口（TASK-402 Part C）。</summary>
    private void OnOpenSettingsClicked(object sender, RoutedEventArgs e)
    {
        if (Application.Current is App app)
        {
            app.OpenSettingsWindow();
        }
    }

    private static string Format(TimeSpan t)
    {
        if (t < TimeSpan.Zero) t = TimeSpan.Zero;
        return $"{(int)t.TotalMinutes:00}:{t.Seconds:00}";
    }

    /// <summary>把当前窗口内容离屏渲染为 PNG（不受窗口遮挡影响，供沙箱取证）。</summary>
    private void SaveSnapshot(string path)
    {
        try
        {
            var bmp = new RenderTargetBitmap(
                (int)ActualWidth, (int)ActualHeight, 96, 96, PixelFormats.Pbgra32);
            bmp.Render(this);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bmp));
            using var fs = File.Create(path);
            encoder.Save(fs);
            Trace.WriteLine($"[MainWindow] snapshot saved: {path}");
            Trace.Flush(); // 截图后立即落盘取证日志
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[MainWindow] snapshot failed: {ex.Message}");
        }
    }
}
