using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using YTM.Lyrics.Core.Cleaning;
using YTM.Lyrics.Core.Engine;
using YTM.Lyrics.Core.Playback;
using YTM.Lyrics.Core.Timeline;

namespace YTM.Lyrics.Desktop;

/// <summary>
/// 桌面歌词窗口（TASK-301 壳 + TASK-302 歌词渲染，F7）：
/// - 透明无边框 + 置顶（可配置）；默认横条 600x120，屏幕底部居中；位置/尺寸持久化
/// - 拖动：MouseLeftButtonDown → DragMove（锁定时跳过）；缩放：四边+四角 Thumb（锁定时忽略），最小 200x60
/// - 两个正交开关（UI_SPEC §3.2）：点击穿透（WS_EX_TRANSPARENT，SourceInitialized 后应用）与
///   锁定位置互相独立；穿透开启时窗口不接收任何鼠标事件 → 逃生口 = 全局热键 Alt+Shift+W（App 注册）
///   或主窗口；右键菜单（窗口可交互时可用）可关穿透/锁/置顶、重置位置、隐藏
///
/// TASK-302 歌词渲染（UI_SPEC §3.4）：
/// - 数据：TrackChanged → ITitleCleaner.Clean → ILyricsEngine.GetLyricsAsync（全异步，切歌竞态
///   _loadGen + _loadCts 双守卫，与 MainWindow 同模式）；DispatcherTimer 50ms（~20fps，桌面歌词只
///   1~2 行开销极低）驱动 GetInterpolatedPosition() + TimelineNavigator.FindCurrentLineIndex
/// - 样式：基础字号 = 28 × desktop_font_scale（§3.1 字号与窗口尺寸解耦）；当前行 1.15x 高亮放大
///   纯白 #F2F2F8 SemiBold（§2.1 初值）；下一行基础字号暗色 #8A8A98，Opacity 0.55 静态渐隐（§2.1
///   透明度随与当前行距离单调衰减）
/// - 双行模式（desktop_show_two_lines）：当前行在上 + 下一行在下紧凑排列，整块垂直居中；
///   单行模式：当前行垂直居中。模式切换即时生效（下次行变化按新模式渲染，切换本身不触发动画）
/// - 换行动画：v1 硬切（Lyricify 4 实测校准，desktop_line_animation 项保留供后续扩展）；
///   切歌首帧直接定位无动画（§2.1）
/// - 无歌词三态（§3.3）：_doc==null 且加载中 → "加载中…"；加载完成无词 → desktop_when_no_lyrics
///   hide（隐藏）/ minimal（"暂无歌词"）；无播放 → 品牌占位
/// - 隐藏逻辑：desktop_when_no_lyrics=hide 时无歌词 Hide()；=minimal（默认）时显示极简占位。
/// - 关闭语义：桌面歌词窗口无"关闭"，Alt+F4/关闭 → 保存几何 + 隐藏（应用由主窗口退出）
/// 线程纪律：状态机事件在接收线程触发（锁外），本窗口处理器一律回 UI 线程。
/// </summary>
public partial class DesktopLyricsWindow : Window
{
    private const double DefaultWidth = 600;          // 横条形态（UI_SPEC §3.1 待实测初值）
    private const double DefaultHeight = 120;
    private const double MinShellWidth = 200;         // 任务边界：最小尺寸 200x60
    private const double MinShellHeight = 60;
    private const double DefaultBottomMargin = 96;    // 启动位置：屏幕底部居中，距工作区底 96px

    // ---- TASK-302 渲染参数（UI_SPEC §2.1/§3.1 初值，M3 验收肉眼微调）----
    private const double BaseFontSize = 28;           // 基础字号：比主窗口 32 小一号（§3.1）
    private const double CurrentLineScale = 1.15;     // 当前行放大倍数（§2.1）
    private const double TranslationFontScale = 0.625; // 翻译副行字号系数（对齐主窗口 TranslationFontSizeFor）
    private const double TranslationFontMin = 12;     // 翻译副行字号下限（保可读）
    private static readonly Brush BrushActive = new SolidColorBrush(Color.FromRgb(0x98, 0xD3, 0x3C)); // #98d33c 当前行（亮绿）
    private static readonly Brush BrushDim = new SolidColorBrush(Color.FromRgb(0xFF, 0xFF, 0xFF));    // #FFFFFF 等待歌词/翻译（白）

    /// <summary>歌词文字阴影（纯透明背景上提升可读性）。</summary>
    private static readonly System.Windows.Media.Effects.DropShadowEffect LyricsShadow = new()
    {
        BlurRadius = 4,
        ShadowDepth = 1,
        Direction = 270,
        Opacity = 0.85,
        Color = Color.FromRgb(0x00, 0x00, 0x00),
    };

    /// <summary>悬停时的微弱暗色磨砂背景（#80000000，黑色 50% 半透明，极简、无边框）。</summary>
    private static readonly Brush HoverBackground = new SolidColorBrush(Color.FromArgb(0x80, 0x00, 0x00, 0x00));

    /// <summary>默认近乎透明背景（alpha=1：layered window 的 alpha=0 区域会鼠标穿透到桌面，无法拖动）。</summary>
    private static readonly Brush IdleBackground = new SolidColorBrush(Color.FromArgb(0x01, 0x15, 0x17, 0x20));

    private IPlaybackStateMachine _machine;
    private ILyricsEngine _engine;
    private readonly ITitleCleaner _cleaner;
    private readonly SettingsManager _settingsManager;
    private readonly DispatcherTimer _ticker;
    private readonly IReadOnlyList<(string Path, int DelayMs)> _desktopShots; // 取证：--desktop-shot 截图
    private bool _sourceReady; // SourceInitialized 后才有有效 HWND，穿透样式才能应用

    /// <summary>设置引用（共享 App 实例）；Opacity 双向绑定到 desktop_opacity（UI_SPEC §4.4）。
    /// TASK-402：ApplySettings 整体替换引用（新 AppSettings 对象），故 setter 非只读。</summary>
    public AppSettings Settings { get; private set; }

    // ---- TASK-302 歌词状态（仅 UI 线程访问）----
    private LyricDocument? _doc;
    private int _currentLine = -1;
    private int _loadGen;                        // 代际序号：切歌竞态守卫（与 CTS 双保险）
    private CancellationTokenSource? _loadCts;

    // ⑩：duration=0 延迟搜索（探针首帧时长无效时等有效时长/3s 超时再搜，与 MainWindow 同款决策器）
    private readonly TrackSearchDeferrer _deferrer = new();
    private DispatcherTimer? _deferTimer;

    public DesktopLyricsWindow(
        IPlaybackStateMachine machine,
        AppSettings settings,
        SettingsManager settingsManager,
        ILyricsEngine engine,
        ITitleCleaner cleaner,
        IReadOnlyList<(string Path, int DelayMs)> desktopShots)
    {
        InitializeComponent();

        // 纯透明背景上文字加阴影提升可读性（当前行/下一行/翻译/占位统一）
        CurrentLineText.Effect = LyricsShadow;
        NextLineText.Effect = LyricsShadow;
        CurrentTranslationText.Effect = LyricsShadow;
        NextTranslationText.Effect = LyricsShadow;
        PlaceholderText.Effect = LyricsShadow;

        _machine = machine;
        Settings = settings;
        _settingsManager = settingsManager;
        _engine = engine;
        _cleaner = cleaner;
        _desktopShots = desktopShots;
        DataContext = this; // 启用 XAML 中 Opacity 绑定

        RestoreGeometry();                      // 恢复上次位置/尺寸（无记录 → 底部居中默认）
        Topmost = Settings.desktop_topmost;

        _ticker = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        _ticker.Tick += (_, _) => Refresh();

        _machine.TrackChanged += OnTrackChanged;
        SourceInitialized += OnSourceInitialized;
        Closing += OnClosing;
        Closed += OnClosed;
        ContextMenuOpening += OnContextMenuOpening;
        Loaded += OnLoaded;
        IsVisibleChanged += OnIsVisibleChanged;

        RefreshMenuChecks();
        Trace.WriteLine($"[DesktopLyricsWindow] ctor: size={Width:F0}x{Height:F0} pos=({Left:F0},{Top:F0}) " +
                        $"locked={Settings.desktop_locked} click_through={Settings.desktop_click_through}");
    }

    // ---------------- 位置/尺寸持久化（UI_SPEC §3.1 / 任务边界） ----------------

    /// <summary>启动恢复几何；记录缺失或已完全移出工作区（显示器变更）→ 默认底部居中 600x120。</summary>
    private void RestoreGeometry()
    {
        var wa = SystemParameters.WorkArea;
        var s = Settings;

        var restorable = s.is_restore_window
            && s.desktop_size_width is { } w && w >= MinShellWidth
            && s.desktop_size_height is { } h && h >= MinShellHeight
            && s.desktop_position_left is { } l && s.desktop_position_top is { } t
            && l + w > wa.Left && l < wa.Right   // 与工作区水平有交集
            && t + h > wa.Top && t < wa.Bottom;  // 与工作区垂直有交集（防跑丢）
        if (restorable)
        {
            Left = s.desktop_position_left!.Value;
            Top = s.desktop_position_top!.Value;
            Width = s.desktop_size_width!.Value;
            Height = s.desktop_size_height!.Value;
            return;
        }

        Width = DefaultWidth;
        Height = DefaultHeight;
        Left = wa.Left + (wa.Width - DefaultWidth) / 2;
        Top = wa.Bottom - DefaultHeight - DefaultBottomMargin;
    }

    private void SaveGeometry()
    {
        Settings.desktop_position_left = Left;
        Settings.desktop_position_top = Top;
        Settings.desktop_size_width = Width;
        Settings.desktop_size_height = Height;
        _settingsManager.Save(Settings);
    }

    // ---------------- 点击穿透（WS_EX_TRANSPARENT，UI_SPEC §3.2） ----------------

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _sourceReady = true;
        ApplyClickThrough();
    }

    /// <summary>添加/移除 WS_EX_TRANSPARENT：开启后窗口不拦截任何鼠标事件（正交于锁定，不互斥）。</summary>
    private void ApplyClickThrough()
    {
        if (!_sourceReady) return;
        var hwnd = new WindowInteropHelper(this).Handle;
        var ex = NativeMethods.GetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();
        ex = Settings.desktop_click_through
            ? ex | NativeMethods.WS_EX_TRANSPARENT
            : ex & ~NativeMethods.WS_EX_TRANSPARENT;
        NativeMethods.SetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE, new IntPtr(ex));
        Trace.WriteLine($"[DesktopLyricsWindow] WS_EX_TRANSPARENT={(Settings.desktop_click_through ? "on" : "off")}");
    }

    // ---------------- 拖动 / 缩放（PRD F7） ----------------

    /// <summary>鼠标悬停：显示半透明深色底（可拖动/缩放的视觉边界与命中区域）。</summary>
    private void OnShellMouseEnter(object sender, MouseEventArgs e)
    {
        Shell.Background = HoverBackground;
    }

    /// <summary>鼠标离开：恢复近乎透明（保持 alpha=1 接收鼠标，视觉透明）。</summary>
    private void OnShellMouseLeave(object sender, MouseEventArgs e)
    {
        Shell.Background = IdleBackground;
    }

    private void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (Settings.desktop_click_through) return; // 穿透开启时本不该收到事件，防御
        if (Settings.desktop_locked) return;        // 锁定：不执行 DragMove
        if (e.OriginalSource is Thumb) return;      // 缩放手柄按下不触发拖动（Thumb 已标记 handled，防御）
        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
            // 偶发非左键按下态（如重入），忽略
        }
    }

    // ---------------- TASK-402：设置即时生效（ApplySettings / ReplaceEngine / ReplacePlayback） ----------------

    /// <summary>
    /// 设置即时生效入口（TASK-402）：整体替换设置引用，并即时应用桌面歌词全部设置项：
    /// 置顶 / 透明度 / 点击穿透 / 锁定 / 字号缩放 / 双行 / 无歌词行为。
    /// desktop_line_animation 为占位项（v1 换行硬切，UI_SPEC §3.4 实测校准），不实现动画。
    /// TASK-505 翻译副行（show_translation）：开关变更经下方 _currentLine = -1 强制重渲染即时生效——
    /// SetCurrentLine 重新取 Translation 并走 SetTranslation（关 → 一律 Collapsed，开 → 按行内容显隐）。
    /// </summary>
    public void ApplySettings(AppSettings settings)
    {
        Settings = settings;
        Topmost = settings.desktop_topmost;
        Opacity = settings.desktop_opacity;   // XAML 绑定在源无 INPC 时不会随属性替换刷新，显式设置
        ApplyClickThrough();                  // WS_EX_TRANSPARENT（_sourceReady 后才有效，未初始化则等 SourceInitialized）

        // 渲染参数（字号缩放 / 双行 / 行数）：有歌词时立即按新参数重渲染
        if (_doc is { Lines.Count: > 0 })
        {
            _currentLine = -1; // 强制重渲染（SetCurrentLine 对同 idx 早退）
            SetCurrentLine(TimelineNavigator.FindCurrentLineIndex(_doc, _machine.GetInterpolatedPosition()));
        }

        // 无歌词行为：当前正处"暂无歌词"占位（有播放、无词）时按新策略重执行
        if (_doc is null && PlaceholderText.Visibility == Visibility.Visible
            && _machine.Current is not null && PlaceholderText.Text == "暂无歌词")
        {
            ApplyNoLyricsPolicy();
        }

        RefreshMenuChecks();
        Trace.WriteLine($"[DesktopLyricsWindow] ApplySettings: topmost={settings.desktop_topmost} opacity={settings.desktop_opacity} " +
                        $"clickThrough={settings.desktop_click_through} locked={settings.desktop_locked} " +
                        $"fontScale={settings.desktop_font_scale} twoLines={settings.desktop_show_two_lines} " +
                        $"noLyrics={settings.desktop_when_no_lyrics} lineAnim={settings.desktop_line_animation}");
    }

    /// <summary>替换引擎引用（TASK-402 RebuildEngine 注入）：立即用新引擎重新加载当前曲目（即时生效）。</summary>
    public void ReplaceEngine(ILyricsEngine engine)
    {
        _engine = engine;
        if (_machine.Current is { } cur && !string.IsNullOrWhiteSpace(cur.Track.RawTitle))
        {
            StartLoad(cur.Track);
        }
        Trace.WriteLine($"[DesktopLyricsWindow] engine replaced: priority=[{string.Join(", ", engine.SourcePriority)}]");
    }

    /// <summary>
    /// ⑪：主窗口手动绑定/解除绑定（或清除缓存重搜）后，桌面歌词需重新加载当前曲目。
    /// 两窗口独立订阅同一状态机，但手动绑定不触发 TrackChanged，故需 App 中转调用本方法。
    /// </summary>
    public void ReloadCurrentTrack()
    {
        if (_machine.Current is { } cur && !string.IsNullOrWhiteSpace(cur.Track.RawTitle))
        {
            StartLoad(cur.Track);
        }
        else
        {
            Trace.WriteLine("[DesktopLyricsWindow] ReloadCurrentTrack skipped: no current track");
        }
    }

    /// <summary>
    /// 替换播放链路（TASK-402 RestartWebSocketListener：ws_port 变更后 machine 重建）：
    /// 退订旧 machine、绑定新 machine，清空歌词回到等待态（探针重连推帧后自然恢复）。
    /// 不调用 SetNoPlaybackState（避免把用户手动隐藏的窗口重新弹回）。
    /// </summary>
    public void ReplacePlayback(IPlaybackStateMachine machine)
    {
        _machine.TrackChanged -= OnTrackChanged;
        _machine = machine;
        _machine.TrackChanged += OnTrackChanged;
        _doc = null;
        _currentLine = -1;
        _loadCts?.Cancel();
        _loadCts = null;
        _deferTimer?.Stop();
        _deferTimer = null;
        _deferrer.Reset();
        ShowPlaceholder("等待探针数据…");
        Trace.WriteLine("[DesktopLyricsWindow] playback replaced (machine rebuilt)");
    }

    /// <summary>四边 + 四角缩放；锁定位置时忽略（UI_SPEC §3.2：锁定只约束拖动/缩放）。</summary>
    private void OnResizeDrag(object sender, DragDeltaEventArgs e)
    {
        if (Settings.desktop_locked) return;

        var name = ((Thumb)sender).Name;
        var l = Left;
        var t = Top;
        var w = Width;
        var h = Height;

        // 水平：左边/左角拖右边界改变 Left+Width；右边/右角只改 Width
        if (name is "ResizeLeft" or "ResizeTopLeft" or "ResizeBottomLeft")
        {
            var nw = w - e.HorizontalChange;
            if (nw >= MinShellWidth) { l += e.HorizontalChange; w = nw; }
        }
        else if (name is "ResizeRight" or "ResizeTopRight" or "ResizeBottomRight")
        {
            w = Math.Max(MinShellWidth, w + e.HorizontalChange);
        }

        // 垂直：同理
        if (name is "ResizeTop" or "ResizeTopLeft" or "ResizeTopRight")
        {
            var nh = h - e.VerticalChange;
            if (nh >= MinShellHeight) { t += e.VerticalChange; h = nh; }
        }
        else if (name is "ResizeBottom" or "ResizeBottomLeft" or "ResizeBottomRight")
        {
            h = Math.Max(MinShellHeight, h + e.VerticalChange);
        }

        Left = l;
        Top = t;
        Width = w;
        Height = h;
    }

    // ---------------- 歌词加载（TASK-302：TrackChanged → 清洗 → 引擎，全异步 + 切歌竞态守卫） ----------------

    private void OnTrackChanged(object? sender, EventArgs e)
    {
        // 状态机在接收线程触发（锁外），操作 WPF 控件必须先回 UI 线程
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => OnTrackChanged(sender, e));
            return;
        }

        // ⑩：新曲目到达先作废旧等待（决策器状态 + 轮询 timer），防止旧曲目延迟 timer 误触发加载
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
            SetNoPlaybackState();
            return;
        }

        // ⑩：duration ≤ 0（探针首帧时长无效）→ 延迟搜索：等首个 duration>0 快照或 3s 超时兜底（缓存键不再漂移）
        var decision = _deferrer.OnTrackChanged(current.Track, DateTimeOffset.UtcNow);
        if (decision == TrackSearchDecision.Wait)
        {
            StartDeferredTimer();
            return;
        }

        StartLoad(current.Track);
    }

    /// <summary>
    /// ⑩：延迟搜索轮询（250ms）：duration>0 快照到达或 3s 超时 → Ready → 用当前快照曲目启动加载。
    /// 仅 UI 线程调用；Ready 后立即停 timer（单发）。与 MainWindow.StartDeferredTimer 同款。
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

    /// <summary>启动一次歌词加载（代际守卫 + CTS），由 OnTrackChanged 与 Loaded 补加载共用。</summary>
    private void StartLoad(TrackInfo track)
    {
        var gen = ++_loadGen;
        _loadCts = new CancellationTokenSource();
        var ct = _loadCts.Token;

        ShowPlaceholder("加载中…");
        Trace.WriteLine($"[DesktopLyricsWindow] StartLoad: title='{track.RawTitle}' gen={gen}");
        _ = LoadLyricsAsync(track, ct, gen); // fire-and-forget，内部全捕获
    }

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
            Trace.WriteLine($"[DesktopLyricsWindow] lyrics load failed: {ex.GetType().Name}: {ex.Message}");
            await Dispatcher.BeginInvoke(() =>
            {
                if (gen != _loadGen) return;
                ShowPlaceholder("歌词加载失败");
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
        Trace.WriteLine($"[DesktopLyricsWindow] ApplyLyricsResult: source={result.Source?.ToString() ?? "null"} " +
                        $"confidence={result.Confidence} fromCache={result.FromCache} " +
                        $"lines={result.Document?.Lines.Count ?? 0}");
        var doc = result.Document;

        // 加载完成但无词（空文档 / 负缓存 None）→ _doc 判空后走 hide/minimal 分支（§3.3）
        if (doc is null || doc.Lines.Count == 0 || result.Confidence == MatchConfidence.None)
        {
            _doc = null;
            ClearLyrics();
            ApplyNoLyricsPolicy();
            Trace.WriteLine("[DesktopLyricsWindow] lyrics: none (no lyrics / negative cache)");
            return;
        }

        _doc = doc;
        _currentLine = -1; // 强制重定位（切歌首帧直接定位，无动画，UI_SPEC §2.1）
        ShowLyricsPanel();
        Refresh();
    }

    // ---------------- 渲染（TASK-302：当前行定位 + 样式 + 双行 + 换行动画） ----------------

    /// <summary>定时器驱动：当前行跟随外推进度推进（暂停/离线时位置冻结，行保持）。</summary>
    private void Refresh()
    {
        if (_doc is null || _doc.Lines.Count == 0) return;
        var pos = _machine.GetInterpolatedPosition();
        var idx = TimelineNavigator.FindCurrentLineIndex(_doc, pos);
        SetCurrentLine(idx);
    }

    private void SetCurrentLine(int idx)
    {
        if (idx == _currentLine) return;
        _currentLine = idx;

        var doc = _doc;
        if (doc is null || doc.Lines.Count == 0) return;

        // 双行模式：下一行 = 当前行的下一行；单行模式：只显示当前行
        var showNext = Settings.desktop_show_two_lines;
        var curText = idx >= 0 && idx < doc.Lines.Count ? doc.Lines[idx].Text : "";
        var nextText = showNext && idx + 1 >= 0 && idx + 1 < doc.Lines.Count
            ? doc.Lines[idx + 1].Text
            : "";

        // TASK-505 翻译副行：随主词行取 Translation；show_translation=false 时一律不取（不渲染）
        var showTrans = Settings.show_translation;
        var curTranslation = showTrans && idx >= 0 && idx < doc.Lines.Count
            ? doc.Lines[idx].Translation
            : null;
        var nextTranslation = showTrans && showNext && idx + 1 >= 0 && idx + 1 < doc.Lines.Count
            ? doc.Lines[idx + 1].Translation
            : null;

        // v1 硬切（Lyricify 4 实测校准）：直接替换文本 + 样式，无过渡动画
        Trace.WriteLine($"[DesktopLyricsWindow] line {_currentLine} text='{curText}'");
        SetTextsInstant(curText, nextText, curTranslation, nextTranslation);
    }

    /// <summary>瞬切：直接替换文本 + 重布局（v1 硬切，Lyricify 4 实测校准）。</summary>
    private void SetTextsInstant(string curText, string nextText, string? curTranslation, string? nextTranslation)
    {
        CurrentLineText.Text = curText;
        ApplyActiveStyle(CurrentLineText);
        NextLineText.Text = nextText;
        ApplyDimStyle(NextLineText);
        NextLineText.Visibility = string.IsNullOrEmpty(nextText) ? Visibility.Collapsed : Visibility.Visible;

        // TASK-505：翻译副行（空翻译/开关关闭 → Collapsed，不占位不破坏布局）
        SetTranslation(CurrentTranslationText, curTranslation);
        SetTranslation(NextTranslationText, nextTranslation);
    }

    /// <summary>
    /// 翻译副行样式（TASK-505，对齐主窗口翻译行）：
    /// 字号 = BaseFontSize * desktop_font_scale * 0.625（下限 12），暗色 #8A8A98，FontWeight=Normal。
    /// 空/空白翻译保持 Collapsed（无空白占位）。
    /// </summary>
    private void SetTranslation(TextBlock tb, string? translation)
    {
        var has = !string.IsNullOrWhiteSpace(translation);
        tb.Text = translation ?? "";
        tb.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
        if (!has) return;
        tb.FontSize = Math.Max(TranslationFontMin, BaseFontSize * Settings.desktop_font_scale * TranslationFontScale);
        tb.Foreground = BrushDim;
        tb.FontWeight = FontWeights.Normal;
        Trace.WriteLine($"[DesktopLyricsWindow] SetTranslation {tb.Name}: has={has} text='{tb.Text}' vis={tb.Visibility}");
    }

    /// <summary>当前行样式：1.15x 放大 + 纯白 #F2F2F8 + SemiBold（UI_SPEC §2.1 初值）。</summary>
    private void ApplyActiveStyle(TextBlock tb)
    {
        tb.FontSize = BaseFontSize * Settings.desktop_font_scale * CurrentLineScale;
        tb.Foreground = BrushActive;
        tb.FontWeight = FontWeights.SemiBold;
    }

    /// <summary>下一行样式：基础字号 + 暗色 #8A8A98（透明度 0.55 在 XAML 静态设定）。</summary>
    private void ApplyDimStyle(TextBlock tb)
    {
        tb.FontSize = BaseFontSize * Settings.desktop_font_scale;
        tb.Foreground = BrushDim;
        tb.FontWeight = FontWeights.Normal;
    }

    // ---------------- 显示状态（占位 ↔ 歌词层互斥，UI_SPEC §3.3） ----------------

    /// <summary>显示占位（品牌 / 加载中 / 暂无歌词 / 失败），隐藏歌词层并清空行内容。</summary>
    private void ShowPlaceholder(string text)
    {
        _doc = null; // 占位即无歌词数据（调用点负责语义区分）
        _currentLine = -1;
        PlaceholderText.Text = text;
        PlaceholderText.Visibility = Visibility.Visible;
        LyricsViewbox.Visibility = Visibility.Collapsed;
        CurrentLineText.Text = "";
        NextLineText.Text = "";
        NextLineText.Visibility = Visibility.Collapsed;
        ClearTranslations();
    }

    private void ShowLyricsPanel()
    {
        PlaceholderText.Visibility = Visibility.Collapsed;
        LyricsViewbox.Visibility = Visibility.Visible;
        // 首次有歌词自动显示：App 启动不 Show 桌面窗口（默认 main 视图），首个 TrackChanged
        // 加载成功后窗口尚在未显示态（IsLoaded=false）→ Show 一次；此后用户手动隐藏（热键/菜单）
        // 不弹回（IsLoaded=true 保持隐藏，尊重用户意图）
        if (!IsLoaded) Show();
    }

    private void ClearLyrics()
    {
        _currentLine = -1;
        CurrentLineText.Text = "";
        NextLineText.Text = "";
        NextLineText.Visibility = Visibility.Collapsed;
        ClearTranslations();
    }

    /// <summary>TASK-505：清空两个翻译副行（占位/清词同步，保持 Collapsed 不占位）。</summary>
    private void ClearTranslations()
    {
        CurrentTranslationText.Text = "";
        CurrentTranslationText.Visibility = Visibility.Collapsed;
        NextTranslationText.Text = "";
        NextTranslationText.Visibility = Visibility.Collapsed;
    }

    /// <summary>无播放：品牌占位，不触发隐藏（保持 TASK-301 行为）。</summary>
    private void SetNoPlaybackState()
    {
        ShowPlaceholder("🎵 YTM.Lyrics 桌面歌词");
        if (!IsVisible) Show();
    }

    /// <summary>加载完成且无词（_doc == null）：hide → 隐藏；minimal（默认）→ 极简占位"暂无歌词"。</summary>
    private void ApplyNoLyricsPolicy()
    {
        if (Settings.desktop_when_no_lyrics.Equals("hide", StringComparison.OrdinalIgnoreCase))
        {
            Hide();
            Trace.WriteLine("[DesktopLyricsWindow] no lyrics -> hidden (desktop_when_no_lyrics=hide)");
        }
        else
        {
            ShowPlaceholder("暂无歌词");
            if (!IsVisible) Show();
            Trace.WriteLine("[DesktopLyricsWindow] no lyrics -> minimal placeholder");
        }
    }

    // ---------------- 右键菜单（逃生口 / 开关，UI_SPEC §3.2、§5.2） ----------------

    private void OnContextMenuOpening(object sender, ContextMenuEventArgs e) => RefreshMenuChecks();

    private void RefreshMenuChecks()
    {
        MiClickThrough.IsChecked = Settings.desktop_click_through;
        MiLocked.IsChecked = Settings.desktop_locked;
        MiTopmost.IsChecked = Settings.desktop_topmost;
    }

    private void OnToggleClickThrough(object sender, RoutedEventArgs e)
    {
        Settings.desktop_click_through = !Settings.desktop_click_through;
        ApplyClickThrough();
        MiClickThrough.IsChecked = Settings.desktop_click_through;
    }

    private void OnToggleLocked(object sender, RoutedEventArgs e)
    {
        Settings.desktop_locked = !Settings.desktop_locked;
        MiLocked.IsChecked = Settings.desktop_locked;
    }

    private void OnToggleTopmost(object sender, RoutedEventArgs e)
    {
        Settings.desktop_topmost = !Settings.desktop_topmost;
        Topmost = Settings.desktop_topmost;
        MiTopmost.IsChecked = Settings.desktop_topmost;
    }

    private void OnResetPosition(object sender, RoutedEventArgs e)
    {
        var wa = SystemParameters.WorkArea;
        Width = DefaultWidth;
        Height = DefaultHeight;
        Left = wa.Left + (wa.Width - DefaultWidth) / 2;
        Top = wa.Bottom - DefaultHeight - DefaultBottomMargin;
        SaveGeometry();
    }

    private void OnHideWindow(object sender, RoutedEventArgs e) => Hide();

    // ---------------- 生命周期 ----------------

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Refresh();
        _ticker.Start(); // 与 IsVisibleChanged 双保险（Start 幂等）

        // 取证辅助：--desktop-shot "path;delayMs" 到点把桌面歌词窗口内容导出 PNG
        //（不参与业务，仅自测截图；timer 从窗口首次 Show（Loaded）起算）
        foreach (var (path, delayMs) in _desktopShots)
        {
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(delayMs) };
            timer.Tick += (_, _) => { timer.Stop(); SaveSnapshot(path); };
            timer.Start();
        }

        // 防错过 TrackChanged（启动时探针已连上且曲目已变，事件在窗口构造前已触发）：
        // 从未加载过且当前有曲目 → 补一次加载
        if (_loadGen == 0 && _machine.Current is { } cur && !string.IsNullOrWhiteSpace(cur.Track.RawTitle))
        {
            StartLoad(cur.Track);
        }
    }

    /// <summary>窗口隐藏时停 ticker（省空转）；显示时恢复。50ms 空转开销本就极低，此为顺带优化。</summary>
    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (IsVisible) _ticker.Start();
        else _ticker.Stop();
    }

    /// <summary>把当前窗口内容离屏渲染为 PNG（不受窗口遮挡影响，供沙箱取证；同 MainWindow.SaveSnapshot）。</summary>
    private void SaveSnapshot(string path)
    {
        try
        {
            var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap(
                (int)ActualWidth, (int)ActualHeight, 96, 96, PixelFormats.Pbgra32);
            bmp.Render(this);
            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
            using var fs = System.IO.File.Create(path);
            encoder.Save(fs);
            Trace.WriteLine($"[DesktopLyricsWindow] snapshot saved: {path}");
            Trace.Flush(); // 截图后立即落盘取证日志
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[DesktopLyricsWindow] snapshot failed: {ex.Message}");
        }
    }

    /// <summary>桌面歌词窗口无"关闭"语义：Alt+F4/关闭 → 保存几何 + 隐藏（应用本体由主窗口/OnExit 退出）。</summary>
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        SaveGeometry();
        e.Cancel = true;
        Hide();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        SaveGeometry(); // 应用退出等路径的兜底（正常路径 OnClosing 已保存；重复保存幂等）
        _ticker.Stop();
        _deferTimer?.Stop();
        _deferTimer = null;
        _loadCts?.Cancel();
        _loadCts?.Dispose();
        _loadCts = null;
        _machine.TrackChanged -= OnTrackChanged;
        SourceInitialized -= OnSourceInitialized;
        Closing -= OnClosing;
        Closed -= OnClosed;
        ContextMenuOpening -= OnContextMenuOpening;
        Loaded -= OnLoaded;
        IsVisibleChanged -= OnIsVisibleChanged;
    }
}
