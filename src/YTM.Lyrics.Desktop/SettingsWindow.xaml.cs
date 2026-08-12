using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace YTM.Lyrics.Desktop;

/// <summary>
/// 设置窗口（TASK-402 Part B，UI_SPEC §4 全部 14 项）：
/// - TabControl 三页：服务（ws_port）/ 歌词与源（源优先级排序、翻译开关、清洗规则路径）/ 形态与桌面歌词（default_view、主窗口字号、桌面歌词 8 项）
/// - 每项 = 键名 + 控件 + 说明。源优先级用 ListBox + 上移/下移按钮
/// - 保存按钮：数值范围校验 → 构建新 AppSettings（几何字段原样保留）→ SettingsManager.Save → 关闭。
///   只做"保存"即存即生效，不做"应用"；不做设置项级联校验（边界）
/// - 非模态 Show()，允许同时操作主窗口；Style 对齐 MainWindow（深色 #1B1D29 / 浅色前景 / FontSize 13-14）
///
/// 取证（TASK-402 自测，不参与业务）：
/// - --settings-shot "path;delayMs"：到点把窗口切到第 N 个 Tab 并导出 PNG（第 1 个 shot 截页 1，依此类推）
/// - --settings-auto-edit：最后一个 shot 之后自动修改 3 项（ws_port / 透明度 / 主窗口字号 + 源优先级上移）并触发保存，
///   用于沙箱验证"保存 → 即时生效"链路（App 日志体现）
/// </summary>
public partial class SettingsWindow : Window
{
    /// <summary>源优先级列表项：键（settings 存的值）+ 展示名。</summary>
    private sealed record SourcePriorityEntry(string Key, string DisplayName);

    private readonly AppSettings _current;
    private readonly SettingsManager _manager;
    private readonly List<SourcePriorityEntry> _sourceEntries = new();
    private readonly IReadOnlyList<(string Path, int DelayMs)>? _shots;
    private readonly bool _autoEdit;
    private readonly string? _cleanerAutoPath; // TASK-402 取证：auto-edit 时写入 cleaner_rules_path（验证热加载分发路径）

    public SettingsWindow(
        AppSettings current,
        SettingsManager manager,
        IReadOnlyList<(string Path, int DelayMs)>? shots = null,
        bool autoEdit = false,
        string? cleanerAutoPath = null)
    {
        InitializeComponent();
        _current = current;
        _manager = manager;
        _shots = shots;
        _autoEdit = autoEdit;
        _cleanerAutoPath = cleanerAutoPath;

        LoadFromSettings(current);
        Loaded += OnLoaded;
    }

    /// <summary>把当前设置填充到各控件（键名即 settings 字段名，UI_SPEC §4）。</summary>
    private void LoadFromSettings(AppSettings s)
    {
        PortBox.Text = s.ws_port.ToString(CultureInfo.InvariantCulture);

        foreach (var key in s.lyric_source_priority)
        {
            _sourceEntries.Add(new SourcePriorityEntry(key, $"{SourceDisplayName(key)} ({key})"));
        }
        RefreshSourceList();
        MoveUpButton.IsEnabled = MoveDownButton.IsEnabled = false; // 未选中时禁用

        TranslationCheck.IsChecked = s.show_translation;
        CleanerPathBox.Text = s.cleaner_rules_path;

        DefaultViewBox.SelectedIndex = s.default_view.Equals("desktop", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        FontSizeBox.Text = s.main_lyrics_font_size.ToString("0.##", CultureInfo.InvariantCulture);

        ClickThroughCheck.IsChecked = s.desktop_click_through;
        LockedCheck.IsChecked = s.desktop_locked;
        TopmostCheck.IsChecked = s.desktop_topmost;
        OpacityBox.Text = s.desktop_opacity.ToString("0.##", CultureInfo.InvariantCulture);
        FontScaleBox.Text = s.desktop_font_scale.ToString("0.##", CultureInfo.InvariantCulture);
        TwoLinesCheck.IsChecked = s.desktop_show_two_lines;
        NoLyricsBox.SelectedIndex = s.desktop_when_no_lyrics.Equals("hide", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        LineAnimCheck.IsChecked = s.desktop_line_animation;
    }

    private void RefreshSourceList()
    {
        SourcePriorityList.ItemsSource = null;
        SourcePriorityList.ItemsSource = _sourceEntries;
        SourcePriorityList.SelectedIndex = -1;
    }

    private void OnSourceSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var idx = SourcePriorityList.SelectedIndex;
        MoveUpButton.IsEnabled = idx > 0;
        MoveDownButton.IsEnabled = idx >= 0 && idx < _sourceEntries.Count - 1;
    }

    private void OnMoveUpClicked(object sender, RoutedEventArgs e) => MoveSource(-1);

    private void OnMoveDownClicked(object sender, RoutedEventArgs e) => MoveSource(1);

    private void MoveSource(int delta)
    {
        var idx = SourcePriorityList.SelectedIndex;
        var target = idx + delta;
        if (idx < 0 || target < 0 || target >= _sourceEntries.Count) return;
        (_sourceEntries[target], _sourceEntries[idx]) = (_sourceEntries[idx], _sourceEntries[target]);
        RefreshSourceList();
        SourcePriorityList.SelectedIndex = target;
    }

    // ---------------- 保存：校验 → 构建新 AppSettings → Save（触发 SettingsSaved 分发） ----------------

    private void OnSaveClicked(object sender, RoutedEventArgs e)
    {
        if (!TryBuild(out var updated, out var error))
        {
            Trace.WriteLine($"[SettingsWindow] validation failed: {error}");
            MessageBox.Show(this, error, "设置无效", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        Trace.WriteLine($"[SettingsWindow] saving: port={updated.ws_port} priority=[{string.Join(",", updated.lyric_source_priority)}] " +
                        $"showTranslation={updated.show_translation} font={updated.main_lyrics_font_size} " +
                        $"opacity={updated.desktop_opacity} scale={updated.desktop_font_scale}");
        _manager.Save(updated); // 成功落盘 → SettingsSaved → App 分发即时生效
        Close();
    }

    /// <summary>数值范围校验 + 构建新 AppSettings。几何字段（TASK-301）原样保留，防保存丢失窗口位置。</summary>
    private bool TryBuild(out AppSettings updated, out string error)
    {
        error = string.Empty;
        updated = null!;

        if (!int.TryParse(PortBox.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var port)
            || port is < 1024 or > 65535)
        {
            error = "WebSocket 端口必须是 1024–65535 之间的整数";
            return false;
        }

        if (_sourceEntries.Count == 0)
        {
            error = "源优先级至少保留一项";
            return false;
        }

        if (!double.TryParse(FontSizeBox.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var fontSize)
            || fontSize is < 16 or > 72)
        {
            error = "主窗口歌词字号必须是 16–72 之间的数字";
            return false;
        }

        if (!double.TryParse(OpacityBox.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var opacity)
            || opacity is < 0.1 or > 1.0)
        {
            error = "桌面歌词透明度必须是 0.1–1.0 之间的数字";
            return false;
        }

        if (!double.TryParse(FontScaleBox.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var fontScale)
            || fontScale is < 0.5 or > 2.0)
        {
            error = "桌面歌词字号缩放系数必须是 0.5–2.0 之间的数字";
            return false;
        }

        updated = new AppSettings
        {
            ws_port = port,
            lyric_source_priority = _sourceEntries.Select(e => e.Key).ToArray(),
            show_translation = TranslationCheck.IsChecked == true,
            cleaner_rules_path = CleanerPathBox.Text.Trim(),
            default_view = DefaultViewBox.SelectedIndex == 1 ? "desktop" : "main",
            main_lyrics_font_size = fontSize,
            desktop_click_through = ClickThroughCheck.IsChecked == true,
            desktop_locked = LockedCheck.IsChecked == true,
            desktop_topmost = TopmostCheck.IsChecked == true,
            desktop_opacity = opacity,
            desktop_font_scale = fontScale,
            desktop_show_two_lines = TwoLinesCheck.IsChecked == true,
            desktop_when_no_lyrics = NoLyricsBox.SelectedIndex == 1 ? "hide" : "minimal",
            desktop_line_animation = LineAnimCheck.IsChecked == true,
            // TASK-301 窗口几何：设置窗口不碰这些字段，原样保留（DefaultIgnoreCondition=WhenWritingNull 下若不拷贝会清空）
            is_restore_window = _current.is_restore_window,
            desktop_position_left = _current.desktop_position_left,
            desktop_position_top = _current.desktop_position_top,
            desktop_size_width = _current.desktop_size_width,
            desktop_size_height = _current.desktop_size_height,
        };
        return true;
    }

    // ---------------- 取证：三页截图 + 自动编辑保存（不参与业务） ----------------

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // WPF 坑：TabControl 非选中 Tab 内的 ComboBox 在窗口 Loaded 前设置 SelectedIndex，
        // SelectionBox 模板可能未绑定刷新（截图可见选中项文字空白）→ Loaded 后强制重设（-1 → 原值触发模板更新）
        var dv = DefaultViewBox.SelectedIndex;
        DefaultViewBox.SelectedIndex = -1;
        DefaultViewBox.SelectedIndex = dv;
        var nl = NoLyricsBox.SelectedIndex;
        NoLyricsBox.SelectedIndex = -1;
        NoLyricsBox.SelectedIndex = nl;
        Trace.WriteLine($"[SettingsWindow] loaded: defaultView={DefaultViewBox.SelectedIndex} " +
                        $"item='{((ComboBoxItem?)DefaultViewBox.SelectedItem)?.Content}' " +
                        $"noLyrics={NoLyricsBox.SelectedIndex} item='{((ComboBoxItem?)NoLyricsBox.SelectedItem)?.Content}'");

        if (_shots is not { Count: > 0 }) return;

        var tabIndex = 0;
        foreach (var (path, delayMs) in _shots)
        {
            var tab = tabIndex++;
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(delayMs) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                Tabs.SelectedIndex = tab % Tabs.Items.Count; // 第 1 个 shot 截页 1，依此类推
                Tabs.UpdateLayout();
                SaveSnapshot(path);
            };
            timer.Start();
        }

        if (_autoEdit)
        {
            // 最后一个 shot 之后触发（取证脚本约定 shots 末个 delay 后 1.5s）
            var lastDelay = _shots.Last().DelayMs;
            var saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(lastDelay + 1500) };
            saveTimer.Tick += (_, _) =>
            {
                saveTimer.Stop();
                RunForensicAutoEdit();
            };
            saveTimer.Start();
        }
    }

    /// <summary>取证自动编辑：改 3 项（覆盖服务 / 桌面歌词 / 主窗口三条分发路径）+ 源优先级上移，然后走真实保存按钮。</summary>
    private void RunForensicAutoEdit()
    {
        PortBox.Text = "17977";      // ws_port：RestartWebSocketListener 路径
        OpacityBox.Text = "0.5";     // desktop_opacity：DesktopLyricsWindow.ApplySettings 路径
        FontSizeBox.Text = "40";     // main_lyrics_font_size：MainWindow.UpdateSettings 路径
        if (_cleanerAutoPath is not null)
        {
            CleanerPathBox.Text = _cleanerAutoPath; // cleaner_rules_path：TitleCleaner.LoadRules 热加载路径
            Trace.WriteLine($"[SettingsWindow] forensic auto-edit: cleaner path='{_cleanerAutoPath}'");
        }
        if (_sourceEntries.Count >= 2)
        {
            SourcePriorityList.SelectedIndex = 1; // 源优先级：第 2 项上移 → RebuildEngine 路径
            MoveSource(-1);
        }
        Trace.WriteLine("[SettingsWindow] forensic auto-edit applied, clicking save");
        OnSaveClicked(this, new RoutedEventArgs());
    }

    /// <summary>把当前窗口内容离屏渲染为 PNG（不受窗口遮挡影响，供沙箱取证；同 MainWindow.SaveSnapshot）。</summary>
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
            Trace.WriteLine($"[SettingsWindow] snapshot saved: {path}");
            Trace.Flush(); // 截图后立即落盘取证日志
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[SettingsWindow] snapshot failed: {ex.Message}");
        }
    }

    private static string SourceDisplayName(string key) => key.ToLowerInvariant() switch
    {
        "qq" => "QQ音乐",
        "netease" => "网易云音乐",
        "kugou" => "酷狗音乐",
        "soda" => "汽水音乐",
        "applemusic" => "Apple Music",
        "musixmatch" => "Musixmatch",
        _ => key,
    };
}
