using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using YTM.Lyrics.Core.Cache;
using YTM.Lyrics.Core.Cleaning;
using YTM.Lyrics.Core.Engine;
using YTM.Lyrics.Core.External.Sources;
using YTM.Lyrics.Core.Playback;
using YTM.Lyrics.Core.Playback.Providers;

namespace YTM.Lyrics.Desktop;

/// <summary>
/// App 生命周期（TASK-104 探针链路 + TASK-205 M2 引擎接线）：
/// 启动 → 构造 Cache(LiteDB %AppData%) + TitleCleaner + LyricsEngine（五源真实适配器，
/// 沙箱外用户环境才真搜）→ WebSocketProbeProvider + PlaybackStateMachine → StartAsync → 主窗口。
/// 退出 → Dispose provider 与 cache（LiteDB 文件锁：单进程单连接）。
///
/// 取证参数（TASK-205 沙箱自测，全部不参与业务）：
/// - --shot "path;delayMs" / --quit-after=ms ：M1 沿用
/// - --cache-db=&lt;path&gt;：覆盖缓存库路径（沙箱写不了 %AppData%，取证落工作区）
/// - --log-dir=&lt;path&gt;：覆盖日志目录（TASK-205H；沙箱写不了 %AppData%\YTM.Lyrics\logs，取证落工作区；
///   生产默认固定 %AppData%\YTM.Lyrics\logs\app.log）
/// - --track-title / --track-artist / --track-duration：预置曲目元数据（直灌缓存键计算用）
/// - --preload-cache=&lt;qrc|lrc&gt;;&lt;样本文件&gt;：把样本原文按预置键写入正文缓存（缓存命中路线）
/// - --preload-negative：把预置键写入负缓存（暂无歌词路线，PRD S3）
/// - --mock-engine=low --mock-sample=&lt;QRC样本&gt;：注入返回低置信结果的取证引擎
/// </summary>
public partial class App : Application
{
    private WebSocketProbeProvider? _provider;
    private PlaybackStateMachine? _machine;
    private LiteDbLyricsCache? _cache;
    private TitleCleaner? _cleaner;

    // ---- TASK-301：设置持久化 + 桌面歌词窗口 + 全局热键（UI_SPEC §5.1 v1 固定）----
    // TASK-402：热键扩展为两个——Alt+Shift+W 桌面歌词开关 + Ctrl+Shift+R 重置窗口位置
    private const int HotKeyIdToggleDesktop = 0x0301;
    private const int HotKeyIdResetPosition = 0x0302;
    private AppSettings _settings = new();
    private SettingsManager? _settingsManager;
    private DesktopLyricsWindow? _desktopWindow;
    private MainWindow? _mainWindow;
    private HwndSource? _hotKeySource;   // 保持引用防 GC（热键消息泵挂在主窗口 HWND 上）
    private bool _hotKeyRegisteredW;
    private bool _hotKeyRegisteredR;
    private TrayIcon? _trayIcon;        // TASK-501：系统托盘（Shell_NotifyIcon，消息泵复用 _hotKeySource）

    // ---- TASK-402：取证参数（--settings-shot / --settings-auto-edit）与引擎重建上下文 ----
    private IReadOnlyList<(string Path, int DelayMs)> _settingsShots = Array.Empty<(string, int)>();
    private bool _settingsAutoEdit;
    private ForensicEngineMode _forensicMode;   // 取证引擎模式：RebuildEngine 跳过判定（保持假源）
    private string? _forensicSamplePath;

    private enum ForensicEngineMode { None, Manual, MockEngine }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 日志系统（TASK-205H）：固定写 %AppData%\YTM.Lyrics\logs\app.log（真实用户目录，
        // 打包后 BaseDirectory 可能只读/难找）；Directory.CreateDirectory 防御；
        // Trace.AutoFlush 常开——缓冲不落盘已两次坑取证，观测日志只信已落盘内容。
        // 取证覆盖：--log-dir=&lt;path&gt;（沙箱写不了 %AppData%，与 --cache-db 同级，不参与业务）。
        var logDir = ParseArgValue(e.Args, "--log-dir")
            ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "YTM.Lyrics", "logs");
        var logPath = Path.Combine(logDir, "app.log");
        try
        {
            Directory.CreateDirectory(logDir);
            // TASK-205F：显式指定 UTF-8（无 BOM）——TextWriterTraceListener 默认编码受环境/框架
            // 版本影响（真机日志 GBK 乱码，中文曲名/艺人名难读，增加诊断成本）；
            // 显式 UTF8 后无论查看端用什么解码器都能正确读出中文。
            Trace.Listeners.Add(new TextWriterTraceListener(
                new StreamWriter(logPath, append: true, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
                {
                    AutoFlush = true,
                },
                "applog"));
            Trace.AutoFlush = true; // 每行立即落盘，运行中即可读内容
        }
        catch (Exception logEx)
        {
            // 日志不可用不拖垮应用（默认 Debug 监听器仍在），如实记录失败原因
            Trace.WriteLine($"[App] log init failed: {logEx.GetType().Name}: {logEx.Message}");
        }

        var shots = ParseShots(e.Args, "--shot");
        var desktopShots = ParseShots(e.Args, "--desktop-shot"); // 取证：桌面歌词窗口截图（TASK-302 自测）
        _settingsShots = ParseShots(e.Args, "--settings-shot"); // TASK-402 取证：设置窗口三页截图
        _settingsAutoEdit = HasArg(e.Args, "--settings-auto-edit"); // TASK-402 取证：自动改 3 项并保存

        // ---- TASK-205 接线：缓存（LiteDB，路径可取证覆盖）----
        var cacheDb = ParseArgValue(e.Args, "--cache-db") ?? LiteDbLyricsCache.DefaultDatabasePath;
        _cache = new LiteDbLyricsCache(cacheDb);
        Trace.WriteLine($"[App] cache db: {cacheDb}");

        // 环境摘要（TASK-205H）：exe 版本 / OS / 缓存路径 / 日志路径，每次启动首行定位现场
        Trace.WriteLine($"[App] env summary: exe={Assembly.GetExecutingAssembly().GetName().Version} " +
                        $"os={Environment.OSVersion} cache={cacheDb} log={logPath}");

        // 取证辅助：直灌正文/负缓存（键由预置曲目元数据计算，推帧脚本用同元数据命中）
        PreloadCacheIfRequested(e.Args, _cache);

        _cleaner = TitleCleaner.FromEmbeddedRules();
        var wsPort = ParseArgValue(e.Args, "--ws-port") is { } portText && int.TryParse(portText, out var wp) ? wp : WebSocketProbeProvider.DefaultPort;
        _provider = new WebSocketProbeProvider(wsPort);
        _machine = new PlaybackStateMachine(_provider);

        _provider.StartAsync().ContinueWith(t =>
        {
            if (t.IsFaulted)
                Trace.WriteLine($"[App] StartAsync failed: {t.Exception?.GetBaseException().Message}");
            else
                Trace.WriteLine($"[App] probe listener up on ws://127.0.0.1:{_provider.BoundPort}");
        }, TaskScheduler.Default);

        // 取证辅助：--quit-after=<ms> 到点自动 Shutdown（验证 OnExit → Dispose 路径，免外部关窗依赖）
        var quitAfter = ParseQuitAfter(e.Args);
        if (quitAfter > 0)
        {
            var quitTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(quitAfter)
            };
            quitTimer.Tick += (_, _) =>
            {
                quitTimer.Stop();
                Trace.WriteLine("[App] auto-quit timer fired, shutting down");
                Shutdown();
            };
            quitTimer.Start();
        }

        var engine = BuildEngine(e.Args);

        // ---- TASK-301/402：设置加载提前（MainWindow 构造需要 settings 作初始字号/翻译开关）----
        // 取证覆盖：--settings-path=&lt;path&gt;（沙箱写不了 %AppData%，与 --cache-db/--log-dir 同级，不参与业务）
        _settingsManager = new SettingsManager(ParseArgValue(e.Args, "--settings-path"));
        _settings = _settingsManager.Load();
        // TASK-402：Load 之后订阅（首启落盘默认值时窗口尚未创建，不触发分发）；Save 成功 → OnSettingsSaved 分发即时生效
        _settingsManager.SettingsSaved += OnSettingsSaved;

        var window = new MainWindow(_machine, _provider, _cleaner, engine, _cache!, shots,
            ParseArgValue(e.Args, "--f9-auto"), _settings);
        _mainWindow = window;
        MainWindow = window;

        // ---- TASK-301：桌面歌词窗口壳（共享 _machine；不 Show，默认显示主窗口，PRD 决策4）----
        // TASK-302：传入 engine + cleaner 供歌词加载（TrackChanged → Clean → GetLyricsAsync）
        _desktopWindow = new DesktopLyricsWindow(_machine, _settings, _settingsManager, engine, _cleaner, desktopShots);

        // ---- TASK-301：全局热键 Alt+Shift+W（窗口显示/隐藏切换）----
        // RegisterHotKey 需要 HWND + 消息泵：主窗口 SourceInitialized 之后注册（任务卡要求）
        window.SourceInitialized += OnMainWindowSourceInitialized;

        window.Show();

        // ---- TASK-303：启动默认形态（UI_SPEC §5.1 项 5 `default_view`，PRD F7）----
        // main：仅主窗口（默认；桌面歌词窗口已创建但不 Show）
        // desktop：显示桌面歌词窗口，主窗口最小化到任务栏。
        // v1 无托盘图标，不能用 Hide()——MainWindow 是 Application.Current.MainWindow，
        // Hide 后主窗口引用消失会导致应用退出；最小化保留窗口实例。
        if (_settings.default_view.Equals("desktop", StringComparison.OrdinalIgnoreCase))
        {
            _desktopWindow.Show();
            _mainWindow.WindowState = WindowState.Minimized;
            Trace.WriteLine("[App] default_view=desktop: desktop lyrics shown, main window minimized");
        }
        else
        {
            Trace.WriteLine($"[App] default_view={_settings.default_view}: main window only");
        }

        // TASK-402 取证：--open-settings 启动后自动打开设置窗口（沙箱无 UI 自动化，等效点击主窗口设置按钮）
        if (HasArg(e.Args, "--open-settings"))
        {
            OpenSettingsWindow(ParseArgValue(e.Args, "--settings-cleaner-path"));
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // TASK-402：先退订 SettingsSaved，防 OnExit 兜底 Save 触发分发到已关闭窗口（MainWindow 已 Closed）
        if (_settingsManager is not null)
            _settingsManager.SettingsSaved -= OnSettingsSaved;

        // TASK-301：反注册全局热键（Alt+Shift+W / Ctrl+Shift+R）+ 兜底保存设置（桌面窗口几何已由其自身在 Closing/Closed 保存）
        if (_hotKeySource is not null)
        {
            if (_hotKeyRegisteredW) NativeMethods.UnregisterHotKey(_hotKeySource.Handle, HotKeyIdToggleDesktop);
            if (_hotKeyRegisteredR) NativeMethods.UnregisterHotKey(_hotKeySource.Handle, HotKeyIdResetPosition);
        }
        if (_desktopWindow is not null && _settingsManager is not null)
        {
            _settings.desktop_position_left = _desktopWindow.Left;
            _settings.desktop_position_top = _desktopWindow.Top;
            _settings.desktop_size_width = _desktopWindow.Width;
            _settings.desktop_size_height = _desktopWindow.Height;
            _settingsManager.Save(_settings);
        }

        Trace.WriteLine("[App] OnExit: disposing probe provider / cleaner / cache");
        // TASK-501：移除托盘图标（NIM_DELETE），防止进程退出后残留
        _trayIcon?.Dispose(); // Dispose → Remove（NIM_DELETE + DestroyIcon + 日志）
        _provider?.Dispose();
        _provider = null;
        _cleaner?.Dispose();
        _cleaner = null;
        _cache?.Dispose();
        _cache = null;
        _machine = null;
        Trace.Flush(); // 确保取证日志落盘
        base.OnExit(e);
    }

    /// <summary>
    /// 构造歌词引擎。生产路径：五源真实适配器（ADR-9）+ CompareHelper 打分 + 解析缝 + LiteDB 缓存。
    /// 取证路径：
    /// - --mock-engine=low|high：ForensicLyricsEngine 注入（固定置信度，沙箱内唯一拿不到 Low/None 的通道）；
    /// - --mock-engine=manual：真实 LyricsEngine + ForensicSourceSearcher 假源注入
    ///   （TASK-401 F9 自测：手动搜索→绑定→重播命中全走真实 Core 代码路径，仅"网络"是假的）。
    /// TASK-402：记录取证模式供 RebuildEngine 判定（mock 引擎跳过重建保持假源）。
    /// </summary>
    private ILyricsEngine BuildEngine(string[] args)
    {
        var mock = ParseArgValue(args, "--mock-engine");
        if (mock is not null)
        {
            var samplePath = ParseArgValue(args, "--mock-sample")
                ?? throw new ArgumentException("--mock-engine 需要 --mock-sample=<QRC样本路径>");
            var sample = File.ReadAllText(samplePath);
            _forensicSamplePath = samplePath;

            if (mock.Equals("manual", StringComparison.OrdinalIgnoreCase))
            {
                _forensicMode = ForensicEngineMode.Manual;
                var fakeSearchers = new Dictionary<LyricsSourceKind, ISourceSearcher>
                {
                    [LyricsSourceKind.QQMusic] = new ForensicSourceSearcher(sample),
                };
                Trace.WriteLine($"[App] forensic manual engine: real LyricsEngine + fake QQMusic source, sample={samplePath}");
                return new LyricsEngine(
                    _cache!,
                    fakeSearchers,
                    new CompareHelperResultScorer(),
                    new LyricsDocumentParserBridge());
            }

            _forensicMode = ForensicEngineMode.MockEngine;
            Trace.WriteLine($"[App] forensic mock engine: confidence={mock}, sample={samplePath}");
            return new ForensicLyricsEngine(
                mock.Equals("low", StringComparison.OrdinalIgnoreCase)
                    ? MatchConfidence.Low
                    : MatchConfidence.High,
                LyricsSourceKind.QQMusic,
                new LyricsDocumentParserBridge(),
                sample);
        }

        _forensicMode = ForensicEngineMode.None;
        return new LyricsEngine(
            _cache!,
            BuildRealSearchers(),
            new CompareHelperResultScorer(),
            new LyricsDocumentParserBridge());
    }

    /// <summary>生产路径五源适配器表（ADR-9 顺序不相关，优先级由 LyricsEngine 构造参数决定）。</summary>
    private static IReadOnlyDictionary<LyricsSourceKind, ISourceSearcher> BuildRealSearchers()
        => new Dictionary<LyricsSourceKind, ISourceSearcher>
        {
            [LyricsSourceKind.QQMusic] = new QqMusicSourceSearcher(),
            [LyricsSourceKind.Kugou] = new KugouSourceSearcher(),
            [LyricsSourceKind.AppleMusic] = new AppleMusicSourceSearcher(),
            [LyricsSourceKind.Netease] = new NeteaseSourceSearcher(),
            [LyricsSourceKind.Musixmatch] = new MusixmatchSourceSearcher(),
        };

    /// <summary>取证直灌：--preload-cache / --preload-negative，键由 --track-* 元数据清洗后计算。</summary>
    private void PreloadCacheIfRequested(string[] args, ILyricsCache cache)
    {
        var title = ParseArgValue(args, "--track-title");
        var artist = ParseArgValue(args, "--track-artist");
        var duration = ParseArgValue(args, "--track-duration");
        if (title is null) return;

        var track = new TrackInfo(title, artist ?? string.Empty,
            double.TryParse(duration, out var d) ? d : 0);
        var cleaned = TitleCleaner.FromEmbeddedRules().Clean(track);
        var key = CacheKey.Compute(cleaned, track.DurationSec);

        var preload = ParseArgValue(args, "--preload-cache");
        if (preload is not null)
        {
            var sep = preload.IndexOf(';');
            if (sep > 0)
            {
                var fmt = preload[..sep];
                var path = preload[(sep + 1)..];
                var raw = File.ReadAllText(path);
                var source = fmt.ToLowerInvariant() switch
                {
                    "qrc" => LyricsSourceKind.QQMusic,
                    "lrc" => LyricsSourceKind.Netease,
                    _ => throw new ArgumentException($"未知缓存格式: {fmt}"),
                };
                cache.StoreAsync(new CachedLyrics(
                    key, raw, source, UserBound: false, OffsetSec: 0, StoredAt: DateTimeOffset.UtcNow))
                    .GetAwaiter().GetResult();
                Trace.WriteLine($"[App] preloaded lyrics cache: key={key} fmt={fmt} source={source}");
            }
        }

        if (HasArg(args, "--preload-negative"))
        {
            cache.StoreNegativeAsync(key).GetAwaiter().GetResult();
            Trace.WriteLine($"[App] preloaded negative cache: key={key}");
        }
    }

    // ---------------- TASK-301/402：全局热键（Alt+Shift+W 桌面歌词开关 + Ctrl+Shift+R 重置窗口位置） ----------------

    /// <summary>主窗口 SourceInitialized 后注册热键（需有效 HWND；范围全局，不要求应用在前台，UI_SPEC §5.1）。</summary>
    private void OnMainWindowSourceInitialized(object? sender, EventArgs e)
    {
        if (sender is not Window w) return;
        var handle = new WindowInteropHelper(w).Handle;
        if (handle == IntPtr.Zero) return;

        _hotKeySource = HwndSource.FromHwnd(handle);
        if (_hotKeySource is null) return;
        _hotKeySource.AddHook(HotKeyWndProc);

        if (!_hotKeyRegisteredW)
        {
            _hotKeyRegisteredW = NativeMethods.RegisterHotKey(
                _hotKeySource.Handle, HotKeyIdToggleDesktop,
                NativeMethods.MOD_ALT | NativeMethods.MOD_SHIFT, NativeMethods.VK_W);
            Trace.WriteLine(_hotKeyRegisteredW
                ? "[App] global hotkey Alt+Shift+W registered"
                : $"[App] global hotkey Alt+Shift+W registration FAILED (lastErr={Marshal.GetLastWin32Error()})");
        }

        if (!_hotKeyRegisteredR)
        {
            _hotKeyRegisteredR = NativeMethods.RegisterHotKey(
                _hotKeySource.Handle, HotKeyIdResetPosition,
                NativeMethods.MOD_CONTROL | NativeMethods.MOD_SHIFT, NativeMethods.VK_R);
            Trace.WriteLine(_hotKeyRegisteredR
                ? "[App] global hotkey Ctrl+Shift+R registered"
                : $"[App] global hotkey Ctrl+Shift+R registration FAILED (lastErr={Marshal.GetLastWin32Error()})");
        }

        // ---- TASK-501：系统托盘常驻入口（NIM_ADD；回调消息走同一 HwndSource 消息泵，不另起循环）----
        _trayIcon = new TrayIcon();
        _trayIcon.ShowMainWindowRequested += ShowMainWindowFromTray;
        _trayIcon.ToggleDesktopRequested += ToggleDesktopWindow;
        _trayIcon.ExitRequested += () => Shutdown();
        _trayIcon.Register(_hotKeySource.Handle);
    }

    private IntPtr HotKeyWndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_HOTKEY)
        {
            if (wParam.ToInt32() == HotKeyIdToggleDesktop)
            {
                ToggleDesktopWindow();
                handled = true;
            }
            else if (wParam.ToInt32() == HotKeyIdResetPosition)
            {
                ResetDesktopWindowPosition();
                handled = true;
            }
        }
        else if (msg == TrayIcon.CallbackMessage && _trayIcon is not null)
        {
            // TASK-501：托盘回调（双击/右键菜单）分发到 TrayIcon，复用同一消息泵
            handled = _trayIcon.HandleMessage(msg, wParam, lParam);
        }
        return IntPtr.Zero;
    }

    /// <summary>TASK-501：托盘"显示主窗口"/双击 → 还原 + 激活主窗口（desktop 形态下从最小化恢复）。</summary>
    private void ShowMainWindowFromTray()
    {
        if (_mainWindow is null) return;
        _mainWindow.WindowState = WindowState.Normal;
        _mainWindow.Show();
        _mainWindow.Activate();
        Trace.WriteLine($"[App] tray: show main window (state={_mainWindow.WindowState})");
    }

    /// <summary>Alt+Shift+W：桌面歌词窗口显示/隐藏切换（全局触发）。</summary>
    private void ToggleDesktopWindow()
    {
        if (_desktopWindow is null) return;
        if (_desktopWindow.IsVisible) _desktopWindow.Hide();
        else _desktopWindow.Show();
        Trace.WriteLine($"[App] hotkey toggle desktop window: {(_desktopWindow.IsVisible ? "shown" : "hidden")}");
    }

    /// <summary>Ctrl+Shift+R（UI_SPEC §5.1）：桌面歌词窗口位置重置到屏幕居中，清除位置/尺寸持久化字段后保存。</summary>
    private void ResetDesktopWindowPosition()
    {
        if (_desktopWindow is null || _settingsManager is null) return;
        const double resetWidth = 600;
        const double resetHeight = 120;
        var wa = SystemParameters.WorkArea;
        _desktopWindow.Width = resetWidth;
        _desktopWindow.Height = resetHeight;
        _desktopWindow.Left = wa.Left + (wa.Width - resetWidth) / 2;
        _desktopWindow.Top = wa.Top + (wa.Height - resetHeight) / 2; // 任务卡：重置到屏幕居中
        _settings.desktop_position_left = null;
        _settings.desktop_position_top = null;
        _settings.desktop_size_width = null;
        _settings.desktop_size_height = null;
        _settingsManager.Save(_settings);
        Trace.WriteLine($"[App] hotkey Ctrl+Shift+R: desktop window reset to center ({_desktopWindow.Left:F0},{_desktopWindow.Top:F0}), " +
                        "position/size fields cleared and saved");
    }

    // ---------------- TASK-402：设置即时生效（SettingsSaved 订阅 → 按变更项分发） ----------------

    /// <summary>
    /// SettingsManager.Save 成功后的分发入口。按变更项分发到各组件（即时生效分发表，TASK-402 §Part C）：
    /// ws_port → 重启监听；lyric_source_priority → RebuildEngine；show_translation / main_lyrics_font_size → 主窗口重渲染；
    /// cleaner_rules_path → 清洗器热加载；default_view → 形态切换；桌面歌词 8 项 → ApplySettings。
    /// </summary>
    private void OnSettingsSaved(AppSettings settings)
    {
        // Save 在 SettingsWindow 按钮（UI 线程）或 OnExit（UI 线程）触发；防御性回 UI 线程
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => ApplySettingsChanges(settings));
            return;
        }
        ApplySettingsChanges(settings);
    }

    private void ApplySettingsChanges(AppSettings newSettings)
    {
        var old = _settings;
        _settings = newSettings;

        // 1. ws_port：唯一非即时项——dispose 旧 provider + 重建 + StartAsync（重建期间可能丢几个心跳帧，可接受）
        if (old.ws_port != newSettings.ws_port)
        {
            RestartWebSocketListener(newSettings.ws_port);
        }

        // 2. lyric_source_priority：用新优先级重建 LyricsEngine → 注入两个窗口
        if (!old.lyric_source_priority.SequenceEqual(newSettings.lyric_source_priority))
        {
            RebuildEngine(ParsePriority(newSettings.lyric_source_priority));
        }

        // 3. show_translation：主窗口重建歌词面板 + 桌面歌词 ApplySettings（翻译行显隐）
        if (old.show_translation != newSettings.show_translation)
        {
            _mainWindow?.UpdateSettings(newSettings);
            _desktopWindow?.ApplySettings(newSettings);
        }

        // 4. cleaner_rules_path：清洗器热加载（空 → 内置默认规则）
        if (!string.Equals(old.cleaner_rules_path, newSettings.cleaner_rules_path, StringComparison.Ordinal))
        {
            try
            {
                _cleaner?.LoadRules(string.IsNullOrWhiteSpace(newSettings.cleaner_rules_path)
                    ? null
                    : newSettings.cleaner_rules_path);
                Trace.WriteLine($"[App] cleaner rules hot-loaded: path='{newSettings.cleaner_rules_path}'");
            }
            catch (Exception ex)
            {
                // 解析失败保留旧规则不崩溃（TitleCleaner 既有语义）
                Trace.WriteLine($"[App] cleaner rules load failed, keep old rules: {ex.GetType().Name}: {ex.Message}");
            }
        }

        // 5. default_view：即时切换主窗口/桌面歌词显示状态
        if (!string.Equals(old.default_view, newSettings.default_view, StringComparison.OrdinalIgnoreCase))
        {
            ApplyDefaultView(newSettings.default_view);
        }

        // 6. main_lyrics_font_size：主窗口重建歌词面板字号并重渲染
        if (Math.Abs(old.main_lyrics_font_size - newSettings.main_lyrics_font_size) > 0.001)
        {
            _mainWindow?.UpdateSettings(newSettings);
        }

        // 7. 桌面歌词 8 项：任一变化 → ApplySettings 整组应用
        if (old.desktop_click_through != newSettings.desktop_click_through
            || old.desktop_locked != newSettings.desktop_locked
            || old.desktop_topmost != newSettings.desktop_topmost
            || Math.Abs(old.desktop_opacity - newSettings.desktop_opacity) > 0.001
            || Math.Abs(old.desktop_font_scale - newSettings.desktop_font_scale) > 0.001
            || old.desktop_show_two_lines != newSettings.desktop_show_two_lines
            || !string.Equals(old.desktop_when_no_lyrics, newSettings.desktop_when_no_lyrics, StringComparison.OrdinalIgnoreCase)
            || old.desktop_line_animation != newSettings.desktop_line_animation)
        {
            _desktopWindow?.ApplySettings(newSettings);
        }

        Trace.WriteLine($"[App] settings applied: port={newSettings.ws_port} view={newSettings.default_view} " +
                        $"priority=[{string.Join(",", newSettings.lyric_source_priority)}] " +
                        $"showTranslation={newSettings.show_translation} cleaner='{newSettings.cleaner_rules_path}' " +
                        $"font={newSettings.main_lyrics_font_size} opacity={newSettings.desktop_opacity} " +
                        $"scale={newSettings.desktop_font_scale} twoLines={newSettings.desktop_show_two_lines} " +
                        $"noLyrics={newSettings.desktop_when_no_lyrics} lineAnim={newSettings.desktop_line_animation}");
    }

    /// <summary>ws_port 变更：dispose 旧 provider → 重建 WebSocketProbeProvider + PlaybackStateMachine → 注入两个窗口 → StartAsync。</summary>
    private void RestartWebSocketListener(int newPort)
    {
        Trace.WriteLine($"[App] RestartWebSocketListener: port {(_provider?.BoundPort.ToString() ?? "none")} -> {newPort}");
        _provider?.Dispose();
        _provider = new WebSocketProbeProvider(newPort);
        // machine 在构造时绑定 provider 事件且无换绑通道（Core Playback 只读），必须重建并重新注入窗口
        _machine = new PlaybackStateMachine(_provider);
        _mainWindow?.ReplacePlayback(_machine, _provider);
        _desktopWindow?.ReplacePlayback(_machine);

        _provider.StartAsync().ContinueWith(t =>
        {
            if (t.IsFaulted)
                Trace.WriteLine($"[App] RestartWebSocketListener StartAsync failed: {t.Exception?.GetBaseException().Message}");
            else
                Trace.WriteLine($"[App] probe listener up on ws://127.0.0.1:{_provider.BoundPort}");
        }, TaskScheduler.Default);
    }

    /// <summary>
    /// 用新源优先级重建 LyricsEngine 并注入两个窗口。取证边界（TASK-402）：
    /// --mock-engine=low|high 的 ForensicLyricsEngine 保持假源，跳过重建；
    /// --mock-engine=manual 走真实 LyricsEngine + 假源字典，可重建（搜索顺序随新优先级）。
    /// </summary>
    private void RebuildEngine(IReadOnlyList<LyricsSourceKind> newPriority)
    {
        if (_forensicMode == ForensicEngineMode.MockEngine)
        {
            Trace.WriteLine("[App] forensic mock engine active: engine rebuild skipped (keep fake source)");
            return;
        }

        IReadOnlyDictionary<LyricsSourceKind, ISourceSearcher> searchers = _forensicMode == ForensicEngineMode.Manual
            ? new Dictionary<LyricsSourceKind, ISourceSearcher>
            {
                // 与 BuildEngine 同款：构造需要样本内容（非路径），重建时重新读文件
                [LyricsSourceKind.QQMusic] = new ForensicSourceSearcher(File.ReadAllText(_forensicSamplePath!)),
            }
            : BuildRealSearchers();

        var engine = new LyricsEngine(
            _cache!,
            searchers,
            new CompareHelperResultScorer(),
            new LyricsDocumentParserBridge(),
            newPriority);
        Trace.WriteLine($"[App] engine rebuilt: priority=[{string.Join(", ", newPriority)}]");
        _mainWindow?.ReplaceEngine(engine);
        _desktopWindow?.ReplaceEngine(engine);
    }

    /// <summary>default_view 即时切换：main → 隐藏桌面歌词、恢复主窗口；desktop → 显示桌面歌词、最小化主窗口。</summary>
    private void ApplyDefaultView(string view)
    {
        if (_mainWindow is null || _desktopWindow is null) return;
        if (view.Equals("desktop", StringComparison.OrdinalIgnoreCase))
        {
            _desktopWindow.Show();
            _mainWindow.WindowState = WindowState.Minimized; // v1 无托盘：用最小化保留主窗口实例（启动分支同款）
            Trace.WriteLine("[App] default_view=desktop: desktop lyrics shown, main window minimized");
        }
        else
        {
            _desktopWindow.Hide();
            _mainWindow.WindowState = WindowState.Normal;
            _mainWindow.Show();
            Trace.WriteLine("[App] default_view=main: main window restored, desktop lyrics hidden");
        }
    }

    /// <summary>主窗口设置按钮入口（TASK-402 Part C）：非模态打开设置窗口（允许同时操作主窗口）。
    /// 取证：cleanerAutoPath 透传给 auto-edit（--settings-cleaner-path，验证 cleaner_rules_path 热加载分发）。</summary>
    public void OpenSettingsWindow(string? cleanerAutoPath = null)
    {
        if (_settingsManager is null) return;
        var win = new SettingsWindow(_settings, _settingsManager, _settingsShots, _settingsAutoEdit, cleanerAutoPath);
        win.Show();
        Trace.WriteLine("[App] settings window opened (non-modal)");
    }

    /// <summary>设置字符串键（UI_SPEC §4.2）→ LyricsSourceKind 列表；无效键忽略并记录。</summary>
    private static IReadOnlyList<LyricsSourceKind> ParsePriority(IEnumerable<string> keys)
    {
        var list = new List<LyricsSourceKind>();
        foreach (var key in keys)
        {
            if (TryMapSource(key, out var kind)) list.Add(kind);
            else Trace.WriteLine($"[App] unknown lyric source key ignored: '{key}'");
        }
        return list;
    }

    private static bool TryMapSource(string key, out LyricsSourceKind kind)
    {
        switch (key.Trim().ToLowerInvariant())
        {
            case "qq": kind = LyricsSourceKind.QQMusic; return true;
            case "netease": kind = LyricsSourceKind.Netease; return true;
            case "kugou": kind = LyricsSourceKind.Kugou; return true;
            case "soda": kind = LyricsSourceKind.Soda; return true;
            case "applemusic": kind = LyricsSourceKind.AppleMusic; return true;
            case "musixmatch": kind = LyricsSourceKind.Musixmatch; return true;
            default: kind = default; return false;
        }
    }

    // ---------------- 取证参数解析（不参与业务） ----------------

    private static int ParseQuitAfter(string[] args)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i].Equals("--quit-after", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(args[i + 1], out var ms) && ms > 0)
                return ms;
        }
        return 0;
    }

    private static List<(string Path, int DelayMs)> ParseShots(string[] args, string name)
    {
        var shots = new List<(string, int)>();
        for (var i = 0; i < args.Length; i++)
        {
            if (!args[i].Equals(name, StringComparison.OrdinalIgnoreCase) || i + 1 >= args.Length)
                continue;
            var spec = args[++i];
            var sep = spec.IndexOf(';');
            if (sep <= 0) continue;
            var path = spec[..sep];
            var delay = int.TryParse(spec[(sep + 1)..], out var ms) ? ms : 0;
            shots.Add((path, Math.Max(0, delay)));
        }
        return shots;
    }

    private static string? ParseArgValue(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        }
        return null;
    }

    private static bool HasArg(string[] args, string name)
        => args.Any(a => a.Equals(name, StringComparison.OrdinalIgnoreCase));
}
