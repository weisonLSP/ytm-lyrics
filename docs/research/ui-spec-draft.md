> **归档（2026-08-16）**：本草稿已并入 docs/UI_SPEC.md（正式规格），仅作查考，不再更新。

# Lyricify 4 UI 规格调研草稿（TASK-002）

> 状态：草稿，供架构师修订。仅记录文档可考事实；文档未写的视觉/行为细节一律标 [待实测]，不臆造。
> 信息源：① 新文档站 docs.lyricify.app/lyricify-4（优先）② 仓库旧版指南 docs/Lyricify 4/README.md（已标注不再维护，与新站冲突时以新站为准）③ 仓库 docs/Lyricify 4/settings.json 样例（2023-01 提交，较旧）④ docs/Lyricify 4/Lyrics.md（歌词格式规范，供 F5/F10 参考，非 UI）。
> 重要结论：官方文档（新旧两站）均无"桌面歌词 / 灵动词岛 / Apple Music 歌词"的独立行为规格章节，只散见于设置项、快捷键表和 FAQ。因此本稿第 2/3/6 章大量条目为 [待实测]。

---

## 1. 主窗口布局规格

依据：新站《快速开始·主界面与播放》《按键及功能》；旧版 README 同名章节；settings.json 样例 `window_main` 段。

### 1.1 区域划分（默认横向窗口，从下到上/从左到右）

| 区域 | 位置 | 内容 | 来源 |
|---|---|---|---|
| 主菜单 | 左上角 `···` 按钮 | 功能入口 + 基础设置（含"高级"子菜单：热键管理器、本地文件管理器、自定义配置管理器、登录 Spotify 网页版） | 新站 |
| 导航栏 | 左侧 | 主页、搜索、音乐库、已点赞的歌曲、快捷访问（固定歌单/专辑/艺人） | 新站 |
| 用户组件 | 右上角 | 用户信息、设置、关于、登出入口；Media Session 连接状态（开启增强时） | 新站 |
| 歌曲信息区 | 左下角 | 专辑图片、歌名、艺人 | 新站 |
| 播放控制区 | 下方中间 | 播放/暂停、上一首/下一首、进度、循环、随机（需 Premium 或 Media Session） | 新站 |
| 功能控制区 | 右下方 | 歌词（麦克风按钮）、Apple Music 歌词、Spotify Connect、音量、全屏（右击→移动端 UI 全屏） | 新站 |
| 内容区 | 导航栏右侧主体 | 有播放时默认显示"歌词"界面；无播放时显示"Lyricify 主页" | 新站 |

### 1.2 主窗口行为（settings.json `window_main.window`）

- 位置/大小持久化：`position(left/top)`、`size(width/height)`，`is_restore_window=true`（启动恢复位置）
- 显示器配置：`display_config`，与当前显示器配置不一致时自动重置窗口位置
- `topmost=false`（窗口置顶，可配置）、`maximized`、`opacity=1.0`（主窗口内无效）
- `zooming=1.0`：窗口缩放比例（等比缩放，含字体）
- `is_close_to_hide=false`：点击关闭按钮是隐藏窗口而非退出
- 导航栏宽度 `navigation_width=220.0`
- 竖屏样式：窗口缩小为竖屏模式时自动切换为类 Spotify 移动端布局（下方出现"悬浮曲目框"显示当前歌曲信息）；切换阈值 [待实测]

---

## 2. 歌词面板行为（主窗口"歌词"界面）

依据：新站 getting-started / lyrics-and-track-management；旧版 README；settings.json `window_main` 段。

### 2.1 可考行为

- 主窗口默认内容：有播放曲目时显示"歌词"界面；无播放显示主页；可经功能控制区麦克风按钮开关歌词界面
- 整首歌词滚动视图；**点击任意一行歌词 → 手动纠正时间轴**（时间轴校准手段，FAQ 明确）
- 歌词来源显示：**滑动到歌词最下方显示当前歌词来源（"歌词提供者"）**，点击/右击弹菜单：标记为正确歌词、管理本曲目
- 无歌词时的状态：文档仅覆盖"无播放→显示主页"；"有播放但无歌词"呈现 [待实测]
- 加载中的状态 [待实测]

### 2.2 渲染配置（settings.json `window_main`）

| 键 | 默认 | 说明 |
|---|---|---|
| `lyrics_font_size` | 32.0 | 歌词字号 |
| `translation_font_size` | 20.0 | 翻译字号 |
| `line_spacing` | 0.0 | 行间距 |
| `lyrics_align` | — | 歌词对齐方式 |
| `lyrics_side_margin` | 0.0 | 歌词左右边距 |
| `is_karaoke` | true | 逐字模式（QRC 驱动） |
| `is_playing_line_zooming_animation_enabled` | true | 当前行缩放动画 |
| `is_enable_multi_line`（lyrics 段） | true | 允许多行高亮 |
| `translation`（lyrics 段） | true | 启用翻译 |
| `romaji_option`（lyrics 段） | — | 罗马音 |
| `is_show_explicit` / `show_explicit` | — | Explicit 标记显示 |
| `blurred_cover` | true | 模糊封面背景 |
| `lyrics_mobile_mode` | — | 竖屏模式独立配置（lyrics_font_size 24.0 / translation_font_size 15.0 / 播放行缩放动画关） |

### 2.3 文档未写 → [待实测]

- 滚动逻辑：当前行定位位置（居中 / 1/3 处 / 其他）、切换行的滚动缓动曲线与时长、连续快进时的滚动合并策略
- 当前行与上下行的视觉层级：字号差、透明度、亮度、颜色（文档只确认有"播放行缩放动画"和"多行高亮"概念，无数值）
- 翻译行样式（缩进、字号关系、颜色层级）
- 来源"歌词提供者"的样式与展示位置细节
- 点击歌词行纠正时间轴时的交互反馈（是否提示、如何提示）

---

## 3. 桌面歌词窗口行为

依据：settings.json `desktop_lyrics` 段（行为默认值的唯一权威来源）；FAQ（字号/OBS/消失问题）；全局快捷键表。

### 3.1 窗口特性（settings.json）

| 键 | 默认 | 说明 |
|---|---|---|
| `window`（position/size/display_config/topmost/maximized/opacity） | — | 与主窗口同构：位置/大小持久化、显示器配置重置、置顶、透明度 |
| `is_opened` | false | 是否开启 |
| `is_locked` | false | 锁定（防误操作） |
| `is_duo_line` | true | 显示双行（当前行 + 下一行） |
| `enable_animation` | true | 换行动画 |
| `is_karaoke` | true | 逐字模式 |
| `auto_hide` | false | 暂停时自动隐藏 |
| `fullscreen_hide` | true | 有全屏窗口在前台时自动隐藏 |
| `border_thickness` | 100.0 | 边框宽度（默认倍数的百分比） |
| `lyrics_align` / `font_family` | — | 对齐 / 字体 |
| 颜色组 | — | `duo_line_style` / `single_line_style` / `karaoke_style`，每组含 first_fill（默认 MainColor）、first_border（Black）、second_fill（White/MainColor）、second_border（Black） |

### 3.2 行为（FAQ 可考）

- **字号 = 窗口纵向尺寸**：纵向调整桌面歌词窗口大小即调整字号（无独立字号设置项）
- 无边框 / 透明背景：文档未直接描述，但 `enable_obs_capture`（默认 false，需手动改 settings.json 开启）与透明度配置隐含窗口是自绘透明层；OBS 捕获黑屏时"锁定桌面歌词"会影响捕获
- 全屏隐藏：`fullscreen_hide=true` 时，全屏窗口（如动态壁纸）在前台会导致桌面歌词/灵动词岛消失——这是"异常消失"FAQ 的官方解释
- 全局快捷键开关：桌面歌词默认 `Alt+Shift+W`

### 3.3 文档未写 → [待实测]

- 无边框/透明/置顶的实现细节：真透明（穿透桌面）还是半透明、置顶层级（Topmost vs NoActivate）
- **鼠标点击穿透**：settings.json 无穿透开关（只有 is_locked 锁定）；Lyricify 4 是否有穿透能力存疑 [待实测]（PRD F7 的"穿透可切"需实测对标）
- 拖动换位方式：按住哪里拖（窗口内任意处？）、锁定后是否禁拖
- 缩放交互：拖边框？还是任意边缘？横向缩放是否影响字号
- 右键菜单内容
- 双行/单行的行高与间距基准、当前行与上下行视觉差异的具体数值
- 窗口默认位置/默认大小
- 无歌词时窗口的隐藏/极简状态（PRD F7 要求，文档未述）[待实测]

---

## 4. 设置项清单

来源：settings.json 样例（键 + 默认值 + 注释）、新站设置页、FAQ 提及的隐藏项。**v1 适用列**对照 PRD §3（v1 范围 = 主窗口 + 桌面歌词，无灵动词岛 / AM 歌词 / 任务栏歌词 / 全屏 / 分享）。

### 4.1 通用 / 外观

| 设置项 | 默认 | v1 适用 | 备注 |
|---|---|---|---|
| language（显示语言） | — | 否 | 我们单语言 |
| is_light_theme（深浅主题） | false | 是 | v1 至少支持深色，浅色可后置 |
| prefer_simplified_chinese | true | 否 | Spotify 内容相关 |
| is_show_relinking | false | 否 | 专辑图来源相关 |
| 自定义配色主题（xaml 加载） | — | 否 | 架构上留扩展点即可 |
| 字体家族（font_family，各界面独立，含字体回退） | — | 是 | 主窗口 + 桌面歌词各一个 |
| 预设配置（性能/质量档） | 默认 | 否 | 欢迎窗口一次性设置 |

### 4.2 歌词（lyrics 段）

| 设置项 | 默认 | v1 适用 | 备注 |
|---|---|---|---|
| translation（显示翻译） | true | 是 | 对应 PRD F12（P1） |
| is_show_explicit | true | 否 | — |
| romaji_option | — | 是 | 对应 PRD F12（P1） |
| use_lyricify_server | true | 否 | 我们无云端歌词库 |
| is_qq_first（源优先级） | true | 是 | 对应 PRD F4 源优先级排序（设置页暴露） |
| enable_netease_yrc | false | 是 | 逐字能力，对应 F10（P1） |
| use_available_lyrics | true | 否 | 云端策略 |
| is_enable_multi_line（多行高亮） | true | 否 | 主窗口歌词面板可考虑（P1） |

### 4.3 主窗口（window_main）

| 设置项 | 默认 | v1 适用 | 备注 |
|---|---|---|---|
| topmost / maximized / zooming / is_close_to_hide / is_restore_window | 见 §1.2 | 是 | 窗口行为 |
| navigation_width | 220 | 是 | 导航栏宽度 |
| blurred_cover | true | 是（可选） | 封面背景模糊 |
| is_karaoke（逐字） | true | 是 | 对应 F10（P1） |
| lyrics_font_size / translation_font_size / line_spacing / lyrics_align / lyrics_side_margin | 32/20/0/-/0 | 是 | 歌词面板排版 |
| is_playing_line_zooming_animation_enabled | true | 是 | 当前行缩放动画 |
| lyrics_mobile_mode（竖屏独立配置） | — | 否 | 竖屏样式 v1 可不做 |
| is_time_rest_mode | false | 否 | 进度条显示剩余时长 |

### 4.4 桌面歌词（desktop_lyrics）

| 设置项 | 默认 | v1 适用 | 备注 |
|---|---|---|---|
| topmost / opacity / position / size / display_config | 见 §3.1 | 是 | 窗口行为 |
| is_locked | false | 是 | 锁定 |
| is_duo_line | true | 是 | 双行/单行 |
| enable_animation（换行动画） | true | 是 | — |
| is_karaoke | true | 是 | F10（P1） |
| auto_hide（暂停隐藏） | false | 是 | 对应 PRD F7 空歌词隐藏需求（语义不同，见下） |
| fullscreen_hide | true | 是 | 全屏自动隐藏 |
| border_thickness | 100 | 否 | 边框特效 |
| lyrics_align / font_family | — | 是 | — |
| duo_line_style / single_line_style / karaoke_style（填充/描边色） | MainColor/White/Black | 是 | 歌词颜色样式（v1 可只做一组预设） |
| enable_obs_capture | false | 否 | 隐藏项，手动改文件 |

> 注：PRD F7 的"歌词为空时窗口自动隐藏或显示极简状态（可配置）"在 Lyricify 中对应的是 `auto_hide`（暂停时隐藏）语义，空歌词状态 Lyricify 无对应项，需自行设计 [待实测]。

### 4.5 不适用 v1（记录，不做）

灵动词岛（dynamic_lyrics_island 段：byland_mode、duo_line、auto_hide、fullscreen_hide）、Apple Music 歌词（apple_music_lyrics 段：blur_lyrics、blur_level、pulling、pulling_delay、karaoke 动画组、duet_view、动态封面等 17 项）、Lyricify 全屏、任务栏歌词、分享歌词（share_lyrics）、本地文件管理器、内置播放。

### 4.6 高级 / 隐藏项（FAQ 提及，settings.json 样例未含，属更新版本）

| 设置项 | v1 适用 | 备注 |
|---|---|---|
| 启用 Media Session 增强（enable_media_session） | 是 | 我们走 WebSocket 探针，语义不同；对应 PRD 设置页"端口"项 |
| 启用时间轴修正 / 时间轴纠正 Plus | 是 | 对应 F2 快照+外推的时间轴校正（PRD 已有概念） |
| 设备延迟（device_latency，ms） | 是 | 对应播放状态外推 |
| 代理（proxy：enabled/host/port/username/password）、不在 Lyricify 服务器请求中使用代理 | 否 | — |
| force_software_render | 否 | 渲染层 |
| 更新来源 / 自动更新策略 | 否 | — |

---

## 5. 交互细节

### 5.1 快捷键

**全局快捷键**（新站表格，默认值）：

| 功能 | 默认快捷键 | 默认开启 |
|---|---|---|
| 桌面歌词 | Alt + Shift + W | 是 |
| 灵动词岛 | Alt + Shift + D | 是 |
| 任务栏歌词 | Alt + Shift + T | 是 |
| 重置窗口 | Ctrl + Shift + R | 是 |
| 播放 / 暂停 | 无 | 否 |
| 上一首 | 无 | 否 |
| 下一首 | 无 | 否 |

- 自定义入口：主菜单 → 高级 → 热键管理器；按 BackSpace 清空禁用；设置完成点"应用"
- 非全局快捷键：`Shift+Alt+Q` 标记当前歌词为正确（仅主窗口有焦点时）；`Alt+R` 重载 Apple Music 歌词界面

**v1 适用**：桌面歌词开关、重置窗口两项做全局热键即可；播放控制类快捷键 v1 不需要（我们不做播放控制）。建议 v1 快捷键固定，不做自定义热键管理器。

### 5.2 点击 / 右键行为（主窗口）

| 对象 | 左键 | 右键 |
|---|---|---|
| 专辑图片 | 打开歌曲信息（含音频特性） | 菜单（含打开曲目管理） |
| 歌名 | 菜单（跳转、打开曲目管理） | 同左 |
| 艺人 | 跳转艺人主页 | — |
| 歌词行 | 纠正时间轴 | — |
| 歌词来源（歌词最下方"歌词提供者"） | 菜单（标记为正确歌词、管理本曲目） | 同左 |
| 播放/暂停等控制 | 控制播放（需 Premium/MS） | — |
| 全屏按钮 | Lyricify 全屏 | 移动端 UI 全屏 |
| 歌单/专辑（导航栏快捷访问候选） | — | 固定到快捷访问 |

### 5.3 桌面歌词交互 [待实测]

- 拖动、锁定、缩放、右键菜单、双击行为：文档无描述，见 §3.3

### 5.4 F9 相关交互（曲目管理对照）

PRD F9（手动搜索校正）在 Lyricify 中的对标物是"曲目管理"：

- 打开入口（三处）：左下角歌名菜单、歌词最下方"歌词提供者"菜单、AM 歌词 `···` 按钮
- 搜索反馈：搜索按钮变红=未找到，变绿=找到；歌词信息确认后"在主窗口中应用"预览，再"保存（上传）"
- Offset 微调：手动输入整数毫秒 → 应用 → 通过点击歌词验证（对应 PRD F11）
- 纯音乐标记
- v1 可借鉴：候选列表的"来源 + 时长 + 歌词预览"呈现方式（PRD F9 已定义），搜索按钮红/绿反馈、应用后预览再确认的流程可作交互参照

---

## 6. 视觉规格与待实测清单

### 6.1 文档可考的数值/描述

- 布局数值：导航栏宽 220；主窗口歌词字号 32 / 翻译 20 / 行距 0 / 边距 0；竖屏歌词 24 / 翻译 15
- 窗口行为数值：zooming 1.0；桌面歌词边框宽度默认 100%；透明度默认 1.0
- 颜色：桌面歌词三组样式默认值（双行 first=MainColor/Black 描边，second=White/Black 描边；单行 second=MainColor）；深浅主题两套 xaml（资源文件 DarkTheme.xaml / LightTheme.xaml 含注释）
- 动效概念（无数值）：播放行缩放动画、换行动画、卡拉 OK 逐字填充/上浮动画（上浮默认 2.0 单位，过渡宽度默认 25）、模糊歌词（blur_level 0.6）、动态专辑封面、拖动效果（pulling_delay 2.5）
- 字体：各界面独立 font_family，支持字体回退（Font Fallback）；加粗通过字体名追加 " Bold" 实现
- 特效分级：预设配置三档（默认 / 更好的性能 / 更好的质量）控制特效开关

### 6.2 待实测清单（文档查不到，需安装 Lyricify 4 实测确认）

1. 主窗口歌词滚动：当前行位置比例、切换缓动曲线与时长、快速连跳时的滚动合并
2. 当前行 vs 上下行的字号/透明度/亮度差异具体值（含播放行缩放动画的幅度）
3. "有播放但无歌词"与"歌词加载中"两种状态的 UI
4. 歌词"歌词提供者"来源行的位置/样式细节
5. 点击歌词行纠正时间轴的交互反馈
6. 桌面歌词：拖动区域、锁定后的行为、缩放方式、默认位置/尺寸
7. 桌面歌词是否有鼠标穿透（设置无对应项，仅 is_locked）
8. 桌面歌词右键菜单项
9. 桌面歌词单/双行的行高、间距、当前行与次行的视觉差异
10. 桌面歌词换行动画的具体形态（上滑？淡入淡出？）
11. 桌面歌词无歌词时的行为（隐藏 / 保留窗口 / 显示占位）
12. 翻译行在主窗口与桌面歌词中的呈现样式
13. 灵动词岛的默认形态与动画（如 v1 后置，可只记录不动手）
14. 竖屏样式切换阈值（窗口宽高比）
15. 主窗口默认尺寸与最小尺寸

### 6.3 给架构师的修订建议（仅标注，不新增功能）

- 第 3.3 / 6.2 的 [待实测] 项决定 F7 的验收标准，建议 M2 阶段并行实测（可装 Lyricify 4 截图比对）
- 穿透能力若实测确认 Lyricify 4 无对应项，v1 的"穿透可切"按 PRD 自定实现（WPF WS_EX_TRANSPARENT），本稿不越界设计
