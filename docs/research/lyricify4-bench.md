# Lyricify 4 滚动手感实测

> **状态：无法实测（环境受限）**
> 对应 UI_SPEC §6.2 待实测清单第 1 条。Lyricify 4（4.3.44.0）可安装可启动，但本次运行环境存在三个硬性障碍，无法进入"播放有歌词歌曲 → 观察滚动"的可测状态。按任务边界如实标注，不臆造数值。以下记录全部尝试、证据链与本机/官方文档可考事实，供真实环境人工实测时直接复用。

## 环境

- Lyricify 版本：4.3.44.0（安装于 `C:\Users\wei'son\AppData\Local\Lyricify 4`，程序文件时间戳 2026-04-22）
- 测试歌曲：无（无法建立任何可用的播放音源，见下）
- 实测时间：2026-08-12 17:34–18:10（GMT+8）
- 执行环境：HanaAgent 沙箱（受限令牌 + 隔离虚拟桌面 + 本地 shell 外网 TLS 不可达）

### 三个硬障碍（均有实证）

| # | 障碍 | 证据 |
|---|---|---|
| 1 | **无法在真实桌面显示 Lyricify 主窗口** | 沙箱内进程可启动（`force_update` 关闭后稳定存活 >150s 无退出），但主窗口从不布置：`EnumWindows` 只见 rect=0 的隐藏窗口；`ShowWindow`/`SetWindowPos` 无效（WPF 窗口未走显示逻辑）；`Shell_TrayWnd` 不存在 → 运行在无 Explorer/托盘的虚拟桌面，Lyricify 走"驻留托盘"路径。官方文档确认**没有"显示主窗口"的全局热键**（仅有桌面歌词 `Alt+Shift+W`、灵动词岛 `Alt+Shift+D`、任务栏歌词 `Alt+Shift+T`、重置窗口 `Ctrl+Shift+R`），托盘不可达 = 主窗口不可达 |
| 2 | **音源不可用** | 官方明确"Lyricify 4 是歌词软件，不是播放器，必须 Spotify 播放才有歌词"。本机 Spotify 桌面端 1.2.92.147 在沙箱内直接崩溃（事件日志 `Application Error`，异常码 0x80000003，模块 Spotify.exe，CEF 初始化阶段）；且 `%APPDATA%\Spotify\Users\` 无任何登录凭据文件（仅缓存类 bnk/prefs），即使不崩溃也无法播放 |
| 3 | **本地 shell 外网 TLS 全断** | `Invoke-WebRequest` 到 baidu/github/api.lyricify.com 全部报"基础连接已经关闭: 接收时发生错误"；TCP 层连通（baidu:80/443、8.8.8.8:53 均 True）；系统代理 127.0.0.1:7890 TCP 可达但 HTTPS 隧道失败 → Lyricify 的网络初始化（更新检查/歌词服务器）挂起，这与"启动后 ~1–2 分钟自动退出"（`update.force_update` 关闭前）直接相关 |

此外：提权通道（真实桌面截屏/启动）与按键注入在当前会话审批通道不可用，无法借道真实桌面完成观察。

## 滚动定位

- **无法实测。** UI_SPEC §2.1 初值（当前行稳定在视口垂直 40% 处，偏上非居中）**维持不变**，无实测数据可校准。
- 可考旁证（无直接数值）：官方文档无"当前行定位比例"的描述；TASK-002 调研草稿（`ui-spec-draft.md`）同样标注 [待实测]。
- 补充事实：本机配置文件 `window_main` 组 `lyrics_font_size: 32.0`、`translation_font_size: 20.0`、`line_spacing: 0.0`、`lyrics_align: 0` —— 均为首次运行默认值（见下方配置事实）。

## 行切换缓动

- **无法实测。** UI_SPEC §2.1 初值（ease-out cubic，300ms）**维持不变**。
- 本机配置可考的**动画开关类**事实（无具体缓动曲线/时长数值）：
  - `is_playing_line_zooming_animation_enabled: true`（当前行缩放动画开）
  - `line_switching_apple_ios_style_16_4: true`（行切换走 Apple iOS 16.4 风格路径）
  - `line_switching_opacity_speed_apple_ios_style_16_4: false`
  - `highlight_line_zooming_preset: 0`、`playing_zoom_override: NaN`、`unplaying_opacity_override: NaN`（缩放/透明度无覆盖值，走内置预设）
  - `desktop_lyrics.enable_animation: true`（桌面歌词动画总开关）

## 切歌行为

- **无法实测。** UI_SPEC §2.1 规格（新曲目第一帧直接定位、无滚动动画）**维持不变**。
- 官方 FAQ（docs.lyricify.app/lyricify-4/faq/song-switch-lag/）可考：切歌后"歌曲信息不更新"的延迟主因是网络侧（Media Session 连接、Lyricify 智能引擎、429 限流、歌词标记），即切歌延迟来自**歌词获取链路**而非渲染动画——与"切歌首帧直接定位"的规格方向一致，不冲突。

## Seek 行为

- **无法实测。** UI_SPEC §2.1 规格（seek 跳行瞬切、无缓动；连续快进滚动合并策略初值"直接跳至目标行"）**维持不变**。
- 无任何本机或文档可考数据。

## 当前行 vs 上下文视觉差异

- **无法实测。** UI_SPEC §2.1 初值（当前行约 1.15 倍字号高亮；透明度随距离单调衰减）**维持不变**。
- 本机配置可考的**衰减/层级机制**：`current_line_preplay_opacity_preset: 0`（当前行"进入播放前"的透明度预设存在独立配置项，说明透明度是分档/曲线驱动，非简单两态）；`line_switching_opacity_speed_apple_ios_style_16_4: false`（透明度变化速度与行切换动画解耦，可独立开关）。

### 本机配置的滚动相关完整事实清单（Settings.json 全默认值，2026-08-12 首次运行生成）

> ⚠️ 注意：真实 `%APPDATA%` 无 Lyricify 配置目录，本机 **4.3.44.0 从未成功运行过**（仅有安装文件）。以下为沙箱内首次运行生成的**出厂默认值**，不代表用户自定义状态。

```json
{
  "window_main": {
    "lyrics_font_size": 32.0, "translation_font_size": 20.0,
    "line_spacing": 0.0, "lyrics_align": 0, "lyrics_side_margin": 0.0,
    "is_playing_line_zooming_animation_enabled": true,
    "lyrics_mobile_mode": { "is_playing_line_zooming_animation_enabled": false }
  },
  "lyrics": {
    "highlight_line_zooming_preset": 0,
    "current_line_preplay_opacity_preset": 0,
    "line_switching_apple_ios_style_16_4": true,
    "line_switching_opacity_speed_apple_ios_style_16_4": false,
    "playing_zoom_override": "NaN", "unplaying_opacity_override": "NaN",
    "karaoke_style_enable_animation": true, "zooming_restore": true,
    "show_fps_overlay": false
  },
  "desktop_lyrics": { "enable_animation": true, "is_karaoke": true }
}
```

## 结论（一句话总结，用于 M3 参数设定）

**无法实测**——本运行环境（无外网 TLS、无真实桌面/托盘、Spotify 崩溃且无登录凭据）使 Lyricify 4 无法进入歌词滚动状态；UI_SPEC §2.1 全部初值（40% 定位 / ease-out cubic 300ms / 1.15x 高亮 / 透明度单调衰减 / 切歌直定位 / seek 瞬切）**维持不变**，待真实桌面环境人工对标后微调。

---

## 附录：真实环境人工实测指引（环境恢复后按此执行）

**前置条件**：① 真实用户桌面；② 外网可达；③ Spotify 桌面端已登录并可播放；④ Lyricify 4 首次启动后主窗口可见（官方：主窗口仅正常启动或托盘图标唤出）。

**操作步骤**：

1. Spotify 播放任意有歌词的歌曲（建议中文流行曲，歌词行多、翻译行覆盖全）。
2. 启动 Lyricify 4，主窗口显示歌词后等歌词稳定（避免歌词加载/429 抖动）。
3. 逐项测量：
   - **滚动定位**：截屏（如 Win+Shift+S）测量当前行文字垂直中心距视口顶的像素 ÷ 视口高，得百分比；多首歌重复取均值。
   - **行切换缓动**：60fps 录屏（OBS/手机）捕捉一次行切换，逐帧记录目标行 y 位移，拟合曲线类型与时长；备选：手机 240fps 慢动作估时长下限。注意 iOS 16.4 风格开关（`line_switching_apple_ios_style_16_4`）影响行为。
   - **切歌行为**：Spotify 下一首，观察 Lyricify 首帧（直跳 vs 动画）；注意切歌延迟可能来自歌词网络获取（FAQ 可考），需与渲染动画区分。
   - **seek 行为**：拖动 Spotify 进度条跨多行，观察歌词响应（瞬切 vs 动画）；连续快速拖动多次，观察是否合并/去抖。
   - **视觉差异**：截屏对比当前行与上下行的字号像素高（放大倍数）、文字颜色/透明度（取色），记录可见行数（上下各几行）。
4. 对照 UI_SPEC §2.1 初值微调，并把实测值回填本文件。

**可一并完成**：UI_SPEC §6.2 第 2–7 条（无歌词/加载中状态、来源行、点行纠时反馈、桌面歌词拖动/锁定/缩放、穿透边界）。
