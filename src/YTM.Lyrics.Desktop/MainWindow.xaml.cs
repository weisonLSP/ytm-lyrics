using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using YTM.Lyrics.Core.Cache;
using YTM.Lyrics.Core.Cleaning;
using YTM.Lyrics.Core.Engine;
using YTM.Lyrics.Core.Playback;
using YTM.Lyrics.Core.Timeline;

namespace YTM.Lyrics.Desktop;

/// <summary>
/// 主窗口（TASK-104 最简版 + TASK-205 歌词面板，M2 收官）。
///
/// TASK-205 新增（M2 引擎接入）：
/// - TrackChanged → TitleCleaner.Clean → LyricsEngine.GetLyricsAsync（全异步，不阻塞 UI）；
/// - 整首歌词纯文本滚动列表：当前行由 GetInterpolatedPosition() + TimelineNavigator 二分驱动，
///   DispatcherTimer 200ms 一档；当前行 1.15 倍字号高亮（UI_SPEC §2.1 初值），40% 视口定位（瞬切，
///   缓动动画 M3 才做）；
/// - 翻译行默认显示（PRD 决策3 / F12）：LyricLine.Translation 非空时主词下副行小字；
/// - 底部来源条：来源 + 缓存/网络 + 置信度；Low 时明确提示"低置信结果"（TDD §3.3）；
/// - 无歌词/负缓存 → "暂无歌词"；加载中 → 加载态；
/// - 切歌竞态：新 TrackChanged 到达时取消上一首的加载（CancellationToken + 代际序号双守卫）。
///
/// 线程纪律（TDD §5）：引擎调用全部异步，ConfigureAwait(false) 回 UI 线程；Clean 为纯函数
/// 在线程池执行；渲染只读 Core 计算出的当前行索引，Desktop 不重算业务逻辑。
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
    private readonly ITitleCleaner _cleaner;
    private ILyricsEngine _engine;
    private readonly ILyricsCache _cache;
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

    // ---- 歌词状态（仅 UI 线程访问） ----
    private LyricDocument? _doc;
    private int _currentLine = -1;
    private int _loadGen;                        // 代际序号：切歌竞态守卫（与 CTS 双保险）
    private CancellationTokenSource? _loadCts;
    private readonly List<FrameworkElement> _lineElements = new();
    private readonly List<double> _lineHeights = new();

    // ---- TASK-205F：duration=0 延迟搜索（探针首帧时长无效时等有效时长/3s 超时再搜） ----
    private readonly TrackSearchDeferrer _deferrer = new();
    private DispatcherTimer? _deferTimer;

    // ---- TASK-401 F9 手动搜索状态（仅 UI 线程访问） ----
    private readonly List<ScoredCandidate> _flatCandidates = new(); // 展平候选（渲染顺序）
    private int _selectedIndex = -1;                                // 当前选中候选索引

    public MainWindow(
        IPlaybackStateMachine machine,
        IPlaybackProvider provider,
        ITitleCleaner cleaner,
        ILyricsEngine engine,
        ILyricsCache cache,
        IReadOnlyList<(string Path, int DelayMs)> shots,
        string? f9AutoKeyword = null,
        AppSettings? settings = null)
    {
        InitializeComponent();
        _machine = machine;
        _provider = provider;
        _cleaner = cleaner;
        _engine = engine;
        _cache = cache;
        _shots = shots;
        _f9AutoKeyword = f9AutoKeyword;
        _settings = settings ?? new AppSettings(); // TASK-402：App 传入已加载设置（show_translation / 字号）
        _baseFontSize = _settings.main_lyrics_font_size;

        _provider.SnapshotReceived += OnSnapshotReceived;
        _provider.ProviderLost += OnProviderLost;
        _machine.TrackChanged += OnTrackChanged;
        _machine.PlayStateChanged += OnPlayStateChanged;

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
        _deferTimer?.Stop();
        _deferTimer = null;
        _loadCts?.Cancel();
        _loadCts?.Dispose();
        _loadCts = null;
        _provider.SnapshotReceived -= OnSnapshotReceived;
        _provider.ProviderLost -= OnProviderLost;
        _machine.TrackChanged -= OnTrackChanged;
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

    private void OnTrackChanged(object? sender, EventArgs e)
    {
        // TrackChanged 由状态机在接收线程触发（锁外），本处理器操作 WPF 控件，必须先回 UI 线程
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => OnTrackChanged(sender, e));
            return;
        }

        // TASK-205F：新曲目到达先作废旧等待（决策器状态 + 轮询 timer），防止旧曲目
        // 的延迟 timer 在切歌后误触发 StartLoad
        _deferTimer?.Stop();
        _deferTimer = null;
        _deferrer.Reset();

        // 切歌竞态：取消上一首仍在途的加载（新 TrackChanged 到达 → 上一首作废）
        _loadCts?.Cancel();
        _loadCts?.Dispose();
        _loadCts = null;

        var current = _machine.Current;
        if (current is null)
        {
            ShowLyricsState("暂无播放", isWarn: false);
            return;
        }

        // duration ≤ 0（探针首帧时长无效）→ 延迟搜索：等首个 duration>0 快照（缓存键不再漂移）
        // 或 3s 超时兜底照常搜（任务卡 TASK-205F 杂项 4）
        var decision = _deferrer.OnTrackChanged(current.Track, DateTimeOffset.UtcNow);
        if (decision == TrackSearchDecision.Wait)
        {
            StartDeferredTimer();
            return;
        }

        StartLoad(current.Track);
    }

    /// <summary>
    /// 延迟搜索轮询（250ms）：duration>0 快照到达或 3s 超时 → Ready → 用当前快照曲目启动加载。
    /// 仅 UI 线程调用；Ready 后立即停 timer（单发）。
    /// </summary>
    private void StartDeferredTimer()
    {
        _deferTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _deferTimer.Tick += (_, _) =>
        {
            var current = _machine.Current;
            if (current is null)
            {
                _deferTimer!.Stop();
                _deferTimer = null;
                return;
            }

            var decision = _deferrer.OnSnapshot(current.Track, DateTimeOffset.UtcNow);
            switch (decision)
            {
                case TrackSearchDecision.Wait:
                    return; // 时长仍无效且未超时：继续等
                case TrackSearchDecision.Ready:
                    _deferTimer!.Stop();
                    _deferTimer = null;
                    StartLoad(current.Track); // 用最新（时长已有效）的快照曲目
                    return;
                default: // Immediate：等待态已被清除（如切歌竞态），停 timer 防泄漏
                    _deferTimer!.Stop();
                    _deferTimer = null;
                    return;
            }
        };
        _deferTimer.Start();
    }

    /// <summary>启动一次歌词加载（代际守卫 + CTS），由 OnTrackChanged 与延迟搜索 Ready 共用。</summary>
    private void StartLoad(TrackInfo track)
    {
        var gen = ++_loadGen;
        _loadCts = new CancellationTokenSource();
        var ct = _loadCts.Token;

        ShowLyricsState("加载中…", isWarn: false);
        _ = LoadLyricsAsync(track, ct, gen); // fire-and-forget，内部全捕获
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

    // ---------------- 歌词加载（全异步，切歌竞态守卫） ----------------

    private async Task LoadLyricsAsync(TrackInfo raw, CancellationToken ct, int gen)
    {
        LyricsResult result;
        try
        {
            var cleaned = await Task.Run(() => _cleaner.Clean(raw), ct).ConfigureAwait(false); // 纯函数，线程池执行
            result = await _engine.GetLyricsAsync(cleaned, raw.DurationSec, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return; // 已被切歌取消，静默退出
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[MainWindow] lyrics load failed: {ex.GetType().Name}: {ex.Message}");
            await Dispatcher.BeginInvoke(() =>
            {
                if (gen != _loadGen) return;
                ShowLyricsState("歌词加载失败", isWarn: true);
            });
            return;
        }

        await Dispatcher.BeginInvoke(() =>
        {
            if (gen != _loadGen) return; // 期间又切了歌，丢弃本结果
            ApplyLyricsResult(result);
        });
    }

    private void ApplyLyricsResult(LyricsResult result)
    {
        // TASK-205H：每次结果应用先落一行摘要（Source/Confidence/FromCache/UserBound/行数），
        // 与引擎侧 [Engine] 全链路日志衔接成完整观测链
        Trace.WriteLine($"[MainWindow] ApplyLyricsResult: source={result.Source?.ToString() ?? "null"} " +
                        $"confidence={result.Confidence} fromCache={result.FromCache} userBound={result.UserBound} " +
                        $"lines={result.Document?.Lines.Count ?? 0}");
        _doc = result.Document;
        var doc = _doc;

        if (doc is null || doc.Lines.Count == 0 || result.Confidence == MatchConfidence.None)
        {
            _doc = null;
            ClearLyricsPanel();
            ShowLyricsState("暂无歌词", isWarn: false);
            SourceBarText.Text = "";
            UnbindButton.Visibility = Visibility.Collapsed;
            // F9：None（无结果/负缓存）→ 提供手动搜索入口
            ShowManualSearchPanel(show: true);
            Trace.WriteLine("[MainWindow] lyrics: none (no lyrics / negative cache)");
            return;
        }

        RenderLyrics(doc);
        _currentLine = -1; // 强制重定位（切歌首帧直接定位，无滚动动画，UI_SPEC §2.1）
        UpdateLyricsPosition();
        ShowSourceBar(doc, result);

        // F9 面板联动（TASK-401）：
        // - 绑定结果（UserBound=true）→ 显示"解除绑定"按钮，隐藏搜索面板；
        // - Low 自动结果 → 显示搜索面板（用户可手动校正，PRD S2）；
        // - High 自动结果 → 全部隐藏。
        UnbindButton.Visibility = result.UserBound ? Visibility.Visible : Visibility.Collapsed;
        ShowManualSearchPanel(show: !result.UserBound && result.Confidence == MatchConfidence.Low);
    }

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

    private void ShowSourceBar(LyricDocument doc, LyricsResult result)
    {
        var source = SourceName(doc.Source);
        var from = result.FromCache ? "缓存命中" : "网络获取";
        var conf = result.Confidence == MatchConfidence.Low ? "低置信" : "高置信";
        var bound = result.UserBound ? " · 🔗 用户绑定" : ""; // F9：绑定结果标识（任务卡 ⑤）

        if (result.Confidence == MatchConfidence.Low)
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
        if (!show)
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
            var groups = await _engine.SearchCandidatesAsync(keyword);
            var total = groups.Sum(g => g.Candidates.Count);
            Trace.WriteLine($"[MainWindow] manual search done: groups={groups.Count} total={total}");
            RenderCandidates(groups);
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

    /// <summary>把分组候选渲染为可点击列表（来源标签、标题、艺人、时长、匹配分）。</summary>
    private void RenderCandidates(IReadOnlyList<SourceCandidateGroup> groups)
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
    }

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
            var cleaned = await Task.Run(() => _cleaner.Clean(current.Track)); // 纯函数，线程池执行
            Trace.WriteLine($"[MainWindow] bind lyrics: source={selected.Source} id={selected.SourceId} title='{selected.Title}'");
            var result = await _engine.BindLyricsAsync(cleaned, current.Track.DurationSec, selected);
            if (result.Confidence == MatchConfidence.None)
            {
                Trace.WriteLine("[MainWindow] bind failed: confidence=None (fetch or parse failed)");
                ShowLyricsState("绑定失败：取词或解析未成功", isWarn: true);
                return;
            }

            ApplyLyricsResult(result); // 内部联动：UserBound=true → 显示"解除绑定"、隐藏搜索面板
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
        var current = _machine.Current;
        if (current is null)
        {
            return;
        }

        try
        {
            var cleaned = await Task.Run(() => _cleaner.Clean(current.Track)); // 与 GetLyricsAsync 同键
            var key = CacheKey.Compute(cleaned, current.Track.DurationSec);
            Trace.WriteLine($"[MainWindow] unbind lyrics: key={key}");
            await _cache.UnbindAsync(key);
            Trace.WriteLine($"[MainWindow] unbind done, re-trigger auto search: key={key}");

            UnbindButton.Visibility = Visibility.Collapsed;
            HideManualSearchPanel();
            StartLoad(current.Track); // 重新自动搜索（绑定已删，缓存/负缓存均不再命中）
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[MainWindow] unbind failed: {ex.GetType().Name}: {ex.Message}");
            ShowLyricsState("解除绑定失败", isWarn: true);
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
        var pos = _machine.GetInterpolatedPosition();
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

    /// <summary>替换引擎引用（TASK-402 RebuildEngine 注入）：立即用新引擎重新加载当前曲目（即时生效）。</summary>
    public void ReplaceEngine(ILyricsEngine engine)
    {
        _engine = engine;
        if (_machine.Current is { } cur && !string.IsNullOrWhiteSpace(cur.Track.RawTitle))
        {
            StartLoad(cur.Track);
        }
        Trace.WriteLine($"[MainWindow] engine replaced: priority=[{string.Join(", ", engine.SourcePriority)}]");
    }

    /// <summary>
    /// 替换播放链路（TASK-402 RestartWebSocketListener：ws_port 变更后 provider/machine 重建）：
    /// 退订旧链路事件、绑定新链路、清空歌词回到等待态（探针重连推帧后自然恢复）。
    /// </summary>
    public void ReplacePlayback(IPlaybackStateMachine machine, IPlaybackProvider provider)
    {
        _provider.SnapshotReceived -= OnSnapshotReceived;
        _provider.ProviderLost -= OnProviderLost;
        _machine.TrackChanged -= OnTrackChanged;
        _machine.PlayStateChanged -= OnPlayStateChanged;

        _machine = machine;
        _provider = provider;

        _provider.SnapshotReceived += OnSnapshotReceived;
        _provider.ProviderLost += OnProviderLost;
        _machine.TrackChanged += OnTrackChanged;
        _machine.PlayStateChanged += OnPlayStateChanged;

        _doc = null;
        _currentLine = -1;
        _loadCts?.Cancel();
        _loadCts = null;
        ClearLyricsPanel();
        ShowLyricsState("等待数据", isWarn: false);
        Refresh();
        Trace.WriteLine("[MainWindow] playback replaced (provider/machine rebuilt)");
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
