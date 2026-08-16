using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace YTM.Lyrics.Desktop;

/// <summary>
/// v1 设置模型（对齐 UI_SPEC §4，键名即 JSON 字段名）。
/// 除 ws_port 外所有项改动即时生效（TASK-402 验收门）；热加载不在本卡范围（TASK-402）。
/// TASK-301 扩展：窗口位置/尺寸持久化（§3.1）+ is_restore_window 恢复开关（§1.2 语义）。
/// 位置/尺寸用可空 double：从未落盘时为 null，避免与 (0,0) 混淆。
/// </summary>
public sealed class AppSettings
{
    // §4.1 服务
    public int ws_port { get; set; } = 17865;

    // §4.2 歌词与清洗
    public string[] lyric_source_priority { get; set; } =
        { "qq", "kugou", "netease", "musixmatch" }; // TASK-604 A：Apple Music 2026-08 移除（歌词 API 需特权 token），代码/枚举保留作扩展点
    public bool show_translation { get; set; } = true;
    public string cleaner_rules_path { get; set; } = "";

    // §4.3 形态与主窗口
    public string default_view { get; set; } = "main"; // "main" / "desktop"
    public double main_lyrics_font_size { get; set; } = 32;

    // §4.4 桌面歌词
    public bool desktop_click_through { get; set; } = false;
    public bool desktop_locked { get; set; } = false;
    public bool desktop_topmost { get; set; } = true;
    public double desktop_opacity { get; set; } = 1.0;
    public double desktop_font_scale { get; set; } = 1.0;
    public bool desktop_show_two_lines { get; set; } = true;
    public string desktop_when_no_lyrics { get; set; } = "minimal"; // "hide" / "minimal"
    public bool desktop_line_animation { get; set; } = true;

    // TASK-301 扩展：窗口几何持久化
    public bool is_restore_window { get; set; } = true;
    public double? desktop_position_left { get; set; }
    public double? desktop_position_top { get; set; }
    public double? desktop_size_width { get; set; }
    public double? desktop_size_height { get; set; }
}

/// <summary>
/// settings.json 读写（%AppData%\YTM.Lyrics\settings.json，对齐 UI_SPEC §4）。
/// v1 最小实现：启动 Load + 退出/窗口关闭 Save；热加载留 TASK-402。
/// 取证覆盖：--settings-path=&lt;path&gt;（沙箱写不了 %AppData%，与 --cache-db/--log-dir 同级，不参与业务）。
/// 全部 IO 失败只 Trace 不抛出：设置文件不可用不能拖垮应用。
/// </summary>
public sealed class SettingsManager
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// Save 成功落盘后触发（TASK-402 Part A）。参数为落盘后的设置对象（与传入对象同一引用）。
    /// Save 失败（IO/序列化异常）不触发。事件在调用 Save 的线程同步触发（UI 线程常态）。
    /// </summary>
    public event Action<AppSettings>? SettingsSaved;

    private readonly string _path;

    public SettingsManager(string? pathOverride = null)
    {
        _path = pathOverride ?? DefaultSettingsPath;
    }

    public static string DefaultSettingsPath => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "YTM.Lyrics", "settings.json");

    public string FilePath => _path;

    /// <summary>读取设置；文件缺失/损坏时回退默认值并尝试落盘默认文件（失败仅 Trace）。</summary>
    public AppSettings Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                var json = File.ReadAllText(_path);
                var settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
                if (settings is not null)
                {
                    Trace.WriteLine($"[SettingsManager] loaded: {_path}");
                    return settings;
                }
                Trace.WriteLine($"[SettingsManager] deserialize returned null, fallback to defaults: {_path}");
            }
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[SettingsManager] load failed ({ex.GetType().Name}: {ex.Message}), fallback to defaults");
        }

        var fresh = new AppSettings();
        Save(fresh); // 首次运行：落盘默认值（UI_SPEC §4 各项默认值）
        return fresh;
    }

    public void Save(AppSettings settings)
    {
        try
        {
            var dir = System.IO.Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(_path, JsonSerializer.Serialize(settings, JsonOptions));
            Trace.WriteLine($"[SettingsManager] saved: {_path}");
            SettingsSaved?.Invoke(settings); // 落盘成功才通知（TASK-402：App 据此分发即时生效）
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[SettingsManager] save failed ({ex.GetType().Name}: {ex.Message}): {_path}");
        }
    }
}
