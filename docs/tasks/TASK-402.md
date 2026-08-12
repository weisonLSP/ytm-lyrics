# TASK-402：设置页完整

- **目标**：新增设置窗口，覆盖 UI_SPEC §4 全部 14 个设置项。改动保存后即时生效（除 `ws_port` 需重启监听），不重启应用。

- **输入**：必读文件清单
  - `docs/PRD.md` §5 F8（主窗口设置页）、§4 S1–S3
  - `docs/UI_SPEC.md` §4（v1 设置项清单最终版，14 项完整规格）、§5.1（快捷键）
  - `src/YTM.Lyrics.Desktop/SettingsManager.cs`（当前实现：Load/Save，无事件通知）
  - `src/YTM.Lyrics.Desktop/App.xaml.cs`（启动流程、BuildEngine、_settings/_desktopWindow/_provider 字段）
  - `src/YTM.Lyrics.Desktop/DesktopLyricsWindow.xaml.cs`（当前桌面歌词窗口，了解其引用 _settings 的方式）
  - `src/YTM.Lyrics.Desktop/MainWindow.xaml`（了解如何嵌入设置入口——可加按钮或菜单项）

- **产出**：

  ### Part A：SettingsManager 增强（变更通知）

  | 文件 | 改动 |
  |---|---|
  | `src/YTM.Lyrics.Desktop/SettingsManager.cs` | 新增 `event Action<AppSettings>? SettingsSaved`：Save 方法成功落盘后触发。参数为落盘后的设置对象（非原始引用，防御性拷贝由调用方负责）。Save 失败不触发。 |

  ### Part B：SettingsWindow 新增

  新建文件：
  | 文件 | 说明 |
  |---|---|
  | `src/YTM.Lyrics.Desktop/SettingsWindow.xaml` | 设置窗口 UI：TabControl 分三页（服务、歌词与源、形态与桌面歌词）。每页布局：键名列 + 控件列 + 说明列。15 项（14 设置项 + 1 个"保存"按钮触发批量保存）。具体布局见下文 §UI 布局要求 |
  | `src/YTM.Lyrics.Desktop/SettingsWindow.xaml.cs` | 加载时从传入的 AppSettings 填充控件；保存按钮 → 校验 → 构建新 AppSettings → SettingsManager.Save → 关闭窗口。构造参数：`(AppSettings current, SettingsManager manager)`。窗口 Style 对齐 MainWindow（深色背景 #1B1D29，非模态 `Show()`） |

  ### Part C：App 接线（即时生效）

  | 文件 | 改动 |
  |---|---|
  | `src/YTM.Lyrics.Desktop/App.xaml.cs` | ① 订阅 `_settingsManager.SettingsSaved` 事件 → `OnSettingsSaved` 处理器：根据变更项分发到各组件（见 §即时生效分发表）。② 新增 `RebuildEngine(IReadOnlyList<LyricsSourceKind> newPriority)` 方法：用新源优先级重建 LyricsEngine，替换 _desktopWindow 和 MainWindow 中的 engine 引用（需为 MainWindow 和 DesktopLyricsWindow 暴露 engine setter，或通过方法注入）。③ 新增 `RestartWebSocketListener(int newPort)`：dispose 旧 _provider，新建 WebSocketProbeProvider，重新 StartAsync。④ 注册全局快捷键 Ctrl+Shift+R（重置窗口位置：桌面歌词窗口居中，清除 settings 中位置/尺寸字段后保存）。⑤ 入口：MainWindow 加设置按钮（见 Part D）→ 点击打开 SettingsWindow |
  | `src/YTM.Lyrics.Desktop/MainWindow.xaml.cs` | 暴露 `UpdateSettings(AppSettings settings)` 方法（public）：主窗口字号变更时重渲染（需刷新 LyricsPanel 字号并 RecomputeLineHeights）。暴露 `ReplaceEngine(ILyricsEngine engine)` 方法：源优先级变更后替换引擎引用 |
  | `src/YTM.Lyrics.Desktop/DesktopLyricsWindow.xaml.cs` | 暴露 `ApplySettings(AppSettings settings)` 方法（public，已有类似逻辑则扩展现有）：穿透/锁定/置顶/透明度/字号缩放/双行/无歌词行为/换行动画 → 即时应用到窗口。暴露 `ReplaceEngine(ILyricsEngine engine)` |
  | `src/YTM.Lyrics.Desktop/MainWindow.xaml` | 在底部 SourceBarText 所在行（Grid.Row=7）新增设置按钮（⚙ 图标或"设置"文字，放在解除绑定按钮同行左侧或独立一行）。点击打开 SettingsWindow |

  **即时生效分发表**（OnSettingsSaved 处理逻辑）：

  | 设置项 | 生效方式 |
  |---|---|
  | `ws_port` | 调用 RestartWebSocketListener（dispose 旧 provider + 重建 + StartAsync）。唯一非即时项——重建期间可能丢几个心跳帧，可接受 |
  | `lyric_source_priority` | RebuildEngine → MainWindow.ReplaceEngine + DesktopLyricsWindow.ReplaceEngine |
  | `show_translation` | MainWindow.UpdateSettings + DesktopLyricsWindow.ApplySettings（翻译行显隐需要重建歌词面板或刷新渲染） |
  | `cleaner_rules_path` | 调用 `_cleaner.LoadRules(path)` 热加载（TitleCleaner 需新增此方法，见 Part D 下方） |
  | `default_view` | 即时切换：`main` → 隐藏桌面歌词、恢复主窗口；`desktop` → 显示桌面歌词、最小化主窗口 |
  | `main_lyrics_font_size` | MainWindow.UpdateSettings → 重建歌词面板字号并重渲染 |
  | `desktop_click_through` | DesktopLyricsWindow.ApplySettings → 设置 WS_EX_TRANSPARENT |
  | `desktop_locked` | DesktopLyricsWindow.ApplySettings → 启用/禁用拖动+缩放 |
  | `desktop_topmost` | DesktopLyricsWindow.ApplySettings → 设置 Topmost |
  | `desktop_opacity` | DesktopLyricsWindow.ApplySettings → 设置 Opacity |
  | `desktop_font_scale` | DesktopLyricsWindow.ApplySettings → 重建歌词渲染字号 |
  | `desktop_show_two_lines` | DesktopLyricsWindow.ApplySettings → 切换单/双行渲染 |
  | `desktop_when_no_lyrics` | DesktopLyricsWindow.ApplySettings → 无歌词行为切换 |
  | `desktop_line_animation` | 当前已硬切（UI_SPEC §3.4），此项为占位，暂不实现动画 |

  ### Part D：清洁器热加载 + 主窗口设置入口

  | 文件 | 改动 |
  |---|---|
  | `src/YTM.Lyrics.Core/Cleaning/` | TitleCleaner 新增 `public void LoadRules(string? path)` 方法：path 为空或 null → 回退内置默认规则；否则从文件反序列化 JSON 规则表。需保证线程安全（纯函数 cleaner 的 Clean 调用可能在任意线程），用锁或不可变替换 |
  | `src/YTM.Lyrics.Core/Cleaning/ITitleCleaner.cs`（如存在） | 同步更新接口 |
  | `src/YTM.Lyrics.Desktop/MainWindow.xaml` | Grid.Row=7 新增设置入口按钮 |

- **验收标准**（对应 PRD F8 与 UI_SPEC §4）：
  1. 从主窗口打开设置窗口 → 显示全部 14 个设置项，当前值正确
  2. 修改任意设置项（如桌面歌词透明度改为 0.5）→ 保存 → 桌面歌词窗口即时变半透明
  3. 修改 `lyric_source_priority` 顺序 → 保存 → 下一首新歌按新顺序搜索（日志可见 `SourcePriority` 变更）
  4. 修改 `ws_port` → 保存 → 探针重连成功（日志可见 `probe listener up on ws://127.0.0.1:新端口`）
  5. 关闭设置窗口后重新打开 → 显示上次保存的值（持久化正确）
  6. `cleaner_rules_path` 指向有效规则文件 → 保存后下首歌用新规则清洗

- **边界**：
  - 不允许修改 Core 层 Engine/Cache/Timeline/Playback 模块（只读 Cleaning）
  - 设置窗口不做"应用"按钮（只做"保存"即存即生效），保持简单
  - 不做设置项级联校验（如端口冲突检测），信任用户输入
  - 不做设置项搜索/过滤/导入导出
  - ForensicLyricsEngine 需补齐 RebuildEngine 场景（取证模式不重建，保持假源）
  - 翻译行显隐变更后主窗口需重建歌词面板（清空+重新 RenderLyrics），桌面歌词同理
  - SettingsWindow 用非模态 `Show()`，允许同时操作主窗口
  - 构建注意事项同 TASK-401：DOTNET_CLI_HOME + MSBuildEnableWorkloadResolver=false + maxcpucount:1

- **上报**：完成后将产出交给压缩器。自测证据：设置窗口截图（三页）+ 修改任意 3 项后日志体现即时生效 + 修改 ws_port 后探针重连日志。
