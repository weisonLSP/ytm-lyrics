# HANDOFF：会话交接包（M4 完成态）

> 本文件是会话换岗的唯一启动包。新窗口的 raphael 读这份文件 + 指向的文档即可恢复全部上下文。
> 最后更新：2026-08-13（backlog 已修 4 条 + 新增托盘退出修复；剩卡 2 翻译链路。详见「2026-08-13 增量」）

## 30 秒速览

YTM.Lyrics：YouTube Music 桌面歌词应用。C# .NET 8 + WPF 桌面端 + 油猴探针，歌词供应链复用 Lyricify.Lyrics.Helper（源码固化 external/，commit c14bba9）。

**现状**：M1（播放链路）、M2（歌词引擎）、M3（桌面歌词窗口）、M4（F9 手动校正 + 设置页 + 稳定性 + 发布）全部完成。dist/YTM.Lyrics.exe 为 framework-dependent 单文件（ADR-12），含 M1–M4 全部改动（2026-08-12 23:35 发布）。单测基线 201 全绿。

**下一步**：修 7 条真机反馈 backlog（见「已知问题与 Backlog」与文末「明天开工指引」），然后真机走查 M3+M4 全功能（清单见文末），连续使用一周验证「无重启需求」。之后按需排期 P1 项（F10 逐字卡拉 OK、F11 歌词偏移、F12 翻译开关已有）。

## 2026-08-13 增量（当天交接必读）

### ⚠️ 环境漂移（重要，勿重踩）

HanaAgent 当前会话工作台是 `E:\Codex\OH-WorkSpace\ytm-lyrics`（注意 Codex，不是 Code），与 HANDOFF 原记的 `E:\Code\OH-WorkSpace\ytm-lyrics` 是两个目录：

- **E:\Codex（当前工作台）**：M4 最新代码（含 SettingsWindow.xaml 等 M4 文件），但缺 external/、.git/、.nuget/、dist/。
- **E:\Code（旧路径）**：有完整 external/（Helper 源码 5MB）+ .git + .nuget 离线缓存（packages + local-feed），但 src 停在 M3、git 也停在 M3。

今日已做的非破坏性补全（把两边拼齐）：
1. 复制 `E:\Code\...\external`（Helper 源码）→ E:\Codex 工作台
2. 写了 `E:\Codex\...\nuget.config` 指向 `E:\Code\...\.nuget\local-feed`（离线源，勿删、勿切回 nuget.org）
3. 构建/测试复用 `E:\Code\...\.nuget\packages`（NUGET_PACKAGES 环境变量）

沙箱下 Schannel 出不了网（HANDOFF 坑位 #3），所以 build/test 必须带这两个环境变量：
```powershell
$env:DOTNET_CLI_HOME="E:\Codex\OH-WorkSpace\ytm-lyrics\.nuget\dotnet-home"
$env:NUGET_PACKAGES="E:\Code\OH-WorkSpace\ytm-lyrics\.nuget\packages"
```
沙箱开放后可直接连 nuget.org，但沿用离线路更稳。基线 build 0 错误、test 201 全绿。

**遗留风险**：E:\Codex 无 .git，今日改动（三张卡的代码）无版本控制保护。收工前需决策：把 E:\Codex 改动同步回 E:\Code 的 git 提交，或在 E:\Codex 初始化 git。

### 今日完成（3 张 backlog 卡，均验收通过）

| 卡 | 编号 | 内容 | 反馈项 |
|---|---|---|---|
| 托盘+退出修复 | TASK-501 | ShutdownMode=OnMainWindowClose + 原生 Shell_NotifyIcon 托盘（显示主窗口/切换桌面歌词/退出），点主窗口 X 真退出 | 用户今日新提（非原 7 条） |
| UI 对比度+预填 | TASK-502 | 设置页 TabItem 深色模板 + ComboBox 深色主题 + 搜索框预填歌名 | ① ② ⑦ |
| F9 常驻入口 | TASK-503 | 常驻「搜索歌词」按钮 + 「清除缓存重搜」动作（复用 UnbindAsync，无新 Core 接口） | ⑥ |

### 新增/修改文件（今日）

- 新增：`src/YTM.Lyrics.Desktop/TrayIcon.cs`（原生托盘，纯 P/Invoke，无 WinForms 依赖，白色音符 32x32 内嵌 ICO）
- 修改：`App.xaml`（ShutdownMode）、`App.xaml.cs`（托盘接线+OnExit 移除）、`NativeMethods.cs`（托盘/菜单 P/Invoke）、`SettingsWindow.xaml`（TabItem+ComboBox 深色）、`MainWindow.xaml`+`.xaml.cs`（搜索歌词/清除缓存重搜按钮 + 预填 + ClearCacheAndReload 公共方法）
- 新增：`nuget.config`（离线源，指向 E:\Code 的 local-feed）

### 遗留（卡 2，留低谷跑）

- ③ 桌面歌词不支持翻译行、④ 翻译字段接入残缺（QQ Trans / 网易云 Tlyric / Musixmatch GetTranslations）、⑤ 网易云无版权歌元数据行过滤——三件事打包卡 2，涉及 Core 层 SourceLyricsResult 契约变更，签发前想清翻译数据随两段式 Search→GetLyrics 的流转。
- 真机走查：托盘图标白色在浅色任务栏可能偏淡（待用户确认）、托盘/热键交互、设置页下拉观感。

---

## 新会话启动协议

用户在新窗口的第一句话模板：
> 「继续 YTM.Lyrics 项目，目录 E:\Code\OH-WorkSpace\ytm-lyrics，先读 docs/HANDOFF.md」

接手后的阅读顺序：HANDOFF.md（本文件）→ docs/DEVELOPMENT_PLAN.md（任务卡进度）→ docs/TDD.md（架构铁律与 12 条 ADR，需要时查）→ docs/WORKFLOW.md（流水线章程，含 §7 成本纪律与 §8 提权规则）→ docs/PRD.md（需求冻结版，需要时查）→ docs/UI_SPEC.md（滚动参数已校准）。

## 流水线要点（详版在 WORKFLOW.md）

- raphael（你）：架构决策、任务卡签发、读压缩摘要验收、关键代码抽查。**不读原始产出全文，不亲自编码。**
- kathrik（deepseek-v4-flash）：执行。任务卡必须自包含（实例回收后上下文全失）。
- 压缩摘要按 WORKFLOW §4 模板；调试 3 轮预算；无自测证据直接打回。
- **提权十律（§8）**：执行者禁止提权，受阻按失败现场快照上报。
- subagent 实例会被系统回收（活不过约 1~2 小时），所有认知必须随时落盘，commit 即保险。

## 本会话关键纠正（勿重蹈）

1. **滚动 bug 真根因不是 UpdateLayout 层级**。raphael 初始诊断"LyricsPanel.UpdateLayout() 不向上传播"被 kathrik DIAG 实测推翻。真根因：`RecomputeLineHeights()` 未计入行容器 `Margin(0,8,0,8)`，每行少算 16px，第 12 行时累积偏差 180px≈3.2 行。修复：行高计入 margin。UpdateLayout 改 ScrollViewer 层级保留但非根因。
2. **Lyricify 4 本机从未成功运行过**。HANDOFF 旧版写"已装可用于对标"不准确——真实 %APPDATA% 无 Lyricify 配置目录，只有安装文件。沙箱内也无法启动（虚拟桌面无托盘 + 无 Spotify 登录凭据 + TLS 全断）。用户通过截图完成肉眼对标。
3. **Lyricify 4 实测校准结果**：当前行居中（50%）、行切换硬切无动画（非 ease-out 300ms）。UI_SPEC.md §2.1 已更新。

## M3 新增文件清单

| 文件 | 说明 |
|---|---|
| `src/YTM.Lyrics.Desktop/DesktopLyricsWindow.xaml` | 桌面歌词窗口：透明无边框、Canvas + 2 TextBlock（当前行/下一行）、8 Thumb 缩放、右键菜单 |
| `src/YTM.Lyrics.Desktop/DesktopLyricsWindow.xaml.cs` | 窗口壳 + 歌词渲染管线（TrackChanged→歌词加载→50ms ticker→当前行定位→硬切） |
| `src/YTM.Lyrics.Desktop/NativeMethods.cs` | P/Invoke：RegisterHotKey/UnregisterHotKey、GetWindowLong/SetWindowLong（位数分派）|
| `src/YTM.Lyrics.Desktop/SettingsManager.cs` | settings.json 读写（%AppData%\YTM.Lyrics\settings.json），AppSettings 含全部 UI_SPEC §4 默认值 |
| `docs/research/lyricify4-bench.md` | Lyricify 4 滚动手感实测报告（含人工实测指引） |
| `tools/t205_scroll_*.ps1/js` | 滚动 bug 修复取证脚本（长样本 + 像素分析） |
| `tools/t302_forensic.ps1` | 桌面歌词窗口渲染取证 |

## M3 修改文件清单

| 文件 | 改动 |
|---|---|
| `MainWindow.xaml.cs` | `RecomputeLineHeights` 计入 margin（滚动修复）；`ScrollToLine` 0.4→0.5（居中校准） |
| `App.xaml.cs` | 接入 SettingsManager + 创建 DesktopLyricsWindow + 全局热键 Alt+Shift+W + default_view 启动分支 |
| `UI_SPEC.md` | §2.1 滚动定位 40%→50%、缓动 ease-out→硬切；§3.4 换行动画标注硬切；§6.2 第 1 条标记完成 |

## M4 新增文件清单

| 文件 | 说明 |
|---|---|
| `src/YTM.Lyrics.Desktop/SettingsWindow.xaml` + `.xaml.cs` | 设置窗口：三页 TabControl（服务/歌词与源/形态与桌面歌词）、14 设置项、源优先级 ListBox+上移下移、保存即生效 |
| `src/YTM.Lyrics.Desktop/ForensicSourceSearcher.cs` | 取证假搜索源（--mock-engine=manual 用，Core 路径全真实、仅网络假） |
| `docs/research/drift-analysis.md` | drift 校正过频诊断报告（TASK-403：结论非阈值 bug，阈值不敏感，不改生产逻辑） |
| `docs/tasks/TASK-401/402/403.md` | M4 三张任务卡（F9 手动校正 / 设置页 / 稳定性+发布） |
| `src/YTM.Lyrics.Core.Tests/Engine/ManualSearchTests.cs` | F9 手动搜索/绑定/解绑单测（6 个） |
| `tools/t402_shots/`、`tools/t402b_cleaner.ps1` | TASK-402 设置页取证（截图/日志/cleaner 热加载专项验证） |

## M4 修改文件清单

| 文件 | 改动 |
|---|---|
| `Core/Cache/ILyricsCache.cs` + `LiteDbLyricsCache.cs` | 新增 `UnbindAsync`（F9 解绑：删正文+负缓存条目） |
| `Core/Engine/EngineContracts.cs` + `LyricsEngine.cs` | 新增 `SearchCandidatesAsync`/`BindLyricsAsync` + `ScoredCandidate`/`SourceCandidateGroup` 记录（F9） |
| `Core/Cleaning/TitleCleaner.cs` + `CleaningContracts.cs` | 新增 `LoadRules(path)` 热加载（volatile 快照 + lock 切换 watcher） |
| `Desktop/MainWindow.xaml` + `.xaml.cs` | F9 搜索面板（Row8）+ 解除绑定按钮 + 设置按钮；注入 ILyricsCache；`UpdateSettings`/`ReplaceEngine`/`ReplacePlayback` |
| `Desktop/App.xaml.cs` | `SettingsSaved` 订阅→14 项即时生效分发；`RebuildEngine`/`RestartWebSocketListener`/`ApplyDefaultView`；Ctrl+Shift+R 重置窗口；`OpenSettingsWindow` |
| `Desktop/DesktopLyricsWindow.xaml.cs` | `ApplySettings`/`ReplaceEngine`/`ReplacePlayback`（设置即时生效） |
| `Desktop/SettingsManager.cs` | 新增 `SettingsSaved` 事件（Save 成功触发） |
| `Tests/*` | RuleHotReloadTests 超时加固；drift 诊断 4 测试；断线重连整合链路测试；FakeCache 补 UnbindAsync |

## 环境坑位手册（每条都烧过钱，勿再踩）

> **⚠️ 沙箱状态变更（2026-08-12 晚）**：用户已关闭沙盒（Settings → Security → 沙盒 OFF），当前为**完整权限环境**。
> 下方坑位仅在「沙盒开启」时才踩到；沙盒关闭后，以下绕路**全部不再需要**，别再惯性使用：
> - 构建参数 `MSBuildEnableWorkloadResolver=false -maxcpucount:1` → 裸 `dotnet build` 即可
> - 离线 feed / Python 拉 NuGet 包 → 直接连 nuget.org restore
> - mock 源 + 样本回放验证 → **直连真实歌词源**（QQ/网易云/酷狗等），尤其翻译字段接入（卡 2）不用靠样本猜
> - `GH_CONFIG_DIR` 指真实 APPDATA → gh 直接读凭据
> - 取证 `--cache-db/--log-dir/--settings-path` 指工作区 → 沙箱才需要
> - git push 提权 → 直接 push
>
> 沙盒关闭期间 AI 权限完整（可读写/删除系统任意文件），破坏性操作仍需先确认；**用完提醒用户开回沙盒**（见「沙盒与提权约定」）。

1. **构建必须带参**：`-property:MSBuildEnableWorkloadResolver=false -maxcpucount:1`
2. **DOTNET_CLI_HOME 前置**：`$env:DOTNET_CLI_HOME="<项目>\.nuget\dotnet-home"`
3. **沙箱 Schannel 死**：.NET/curl 出不了网；Python (OpenSSL) 可出网
4. **headless Chrome/Edge 沙箱内起不来**：用 browser 工具或 Node 注入替代
5. **exec_command 拉起的进程随命令会话回收**：让用户自己双击 exe
6. **沙箱 $env:APPDATA 重定向**：取证用 --cache-db/--log-dir/--settings-path 指工作区
7. **用户名含单引号 `wei'son`**：永远用 framework-dependent 单文件发布（ADR-12）
8. **PublishSingleFile 首次 publish 可能 MSB4018 瞬态失败**：原样重跑即过
9. **PowerShell 5.1**：无 BOM UTF-8 脚本被 GBK 误读；编排脚本用 Python
10. **沙箱读不到 gh 凭据**：沙箱 APPDATA 重定向到 `C:\Users\weison\.hanako\.ephemeral\...`，gh 读不到真实登录；解：`$env:GH_CONFIG_DIR = "C:\Users\weison\AppData\Roaming\GitHub CLI"`（真实 APPDATA 下的 GitHub CLI 目录）

## 沙盒与提权约定（戈塔什大人 2026-08-12 亲定）

1. **需要提权（require_escalated）或让用户解开沙盒时，必须告知用户并说明明确原因**——为什么沙盒内做不了（如 Schannel TLS 死）、提权后具体要执行什么操作。
2. 用户可以解开沙盒：Settings → Security → 关闭「沙盒」开关（关闭后 AI 可读写/删除系统任意文件，警告属实）。
3. **任务处理完后，必须提醒用户重新打开沙盒**（保持沙盒默认 ON，临时解开、用完关回）。
4. 提权后只做完成当前任务所必需的最小操作，不做无关探索/清理/修改。
5. 提权前先尝试非提权方案（走代理、`GH_CONFIG_DIR` 指真实 APPDATA、Python/OpenSSL 出网、gh 的 Go TLS 等），确实不行再提。

## 用户环境档案

- Chrome 151 + Tampermonkey 5.5.0（Profile 1/3，无 Default）
- 代理 127.0.0.1:7890（Clash 系）
- Lyricify 4（4.3.44.0）已安装文件但从未成功运行过（无 Spotify 登录）
- .NET SDK 8.0.424 + Desktop Runtime 8.0.30

## 已知问题与 Backlog

| 项 | 状态 | 归属 |
|---|---|---|
| ~~主窗口滚动定位失效~~ | ✅ 已修（margin 未计入行高） | M3 本会话 |
| ~~Lyricify 4 滚动手感对标~~ | ✅ 已完成（居中+硬切，见 docs/research/lyricify4-bench.md） | M3 本会话 |
| ScrollToVerticalOffset 生效滞后一帧（连续推进时高亮行偏低 ~40px，40%±10% 容差内） | 已裁决不修 | M3 做缓动时自然消灭 |
| ~~drift 校正过频~~ | ✅ 已裁决不修（TASK-403 诊断：非阈值 bug，是观测噪声+真实 seek/切歌事件，0.8s 阈值对 loopback 抖动约 80 倍余量，见 docs/research/drift-analysis.md） | M4 本会话 |
| ~~RuleHotReloadTests 偶发失败~~ | ✅ 已修（TASK-403：超时 10s→30s、轮询 100ms→50ms、延时 800ms→1500ms，5 连绿） | M4 本会话 |
| 桌面歌词窗口热键/穿透需真机验证（沙箱无法模拟） | 待真机走查 | 用户 |
| 设置窗口 label 字体黑色看不清（深灰底未适配前景色） | 待修 | 真机反馈 ① |
| 设置窗口 ComboBox 下拉白底 + 选项近白色字体看不清（默认控件未跟深色主题） | 待修 | 真机反馈 ② |
| 桌面歌词窗口不支持翻译行（当前只渲染主词行，翻译行仅主窗口有） | 待排期（需对齐 PRD F12 是否扩展到桌面歌词） | 真机反馈 ③ |
| **翻译字段接入残缺**：QQ `Trans` 字段未取、网易云 `Tlyric` 丢弃、Musixmatch `GetTranslations` 未调；当前唯一出翻译路径 = 酷狗 KRC 带 `[language:]` 段。表现为「有些歌有翻译有些没」（与冷门无关） | 待排期（数据层，先捞翻译字段，再谈桌面歌词显示） | 真机反馈 ④ |
| **网易云无版权歌返回「作词/作曲」元数据行**（非歌词正文），被当歌词缓存+高置信命中，歌词面板只显示 credits 无正文。根因：网易云对部分日文/无正文歌的 Lrc 只含 credits 元数据，清洗/解析层未识别过滤 | 待排期（数据层：识别并过滤「作词/作曲/编曲」等元数据行） | 真机反馈 ⑤ |
| **F9 手动搜索入口触发太窄 + 无常驻入口（“限时促销”）**：仅在 Low/None 时被动闪现，且切歌后 ApplyLyricsResult 重跑（新歌 High）面板即消失；无主动打开按钮，用户想用都找不到。且缓存命中后改源优先级不重搜（命中直接返回） | 待排期（F9 入口应常驻/主动可开，如「设置」旁加「搜索歌词」按钮，随时手动纠正；或加「清除缓存重搜」动作） | 真机反馈 ⑥ |
| **手动搜索框未预填歌名**：面板出现时搜索框空白，需用户手敲歌名。应预填当前曲目清洗后歌名（或歌名+艺人），用户点「搜索」即用 | 待排期（UX：面板出现时自动填 SearchKeywordBox = 当前曲目歌名） | 真机反馈 ⑦ |
| YRC/TTML/MusixmatchJson 解析扩展点 | 仅 QRC/LRC/KRC | 按需 |
| 逐字卡拉 OK 渲染 | P1 | M3 后 |
| 播放控制（桌面端→探针反向注入） | P2 | 协议已预留 |
| 机翻补全 | P2 候选 | 待用户拍板 |
| SMTC 探针 | 备选 | 未排期 |

## 关键情报（勿再调研）

- QQ 音乐 musicu.fcg 已失效，适配器已切老接口 `c.y.qq.com/soso/fcgi-bin/client_search_cp`
- Helper 四坑已在防腐层吸收（TDD §3.6）
- Helper KRC 解析保留行级时间，音节为绝对毫秒
- LiteDB DateTime 一律 UTC ticks 存储
- 真实 KRC/QRC/QQ 搜索响应样本已存档 tools/ 与 Tests

## 里程碑账本

- M1 ✅（101/102/103/103R/104/104b）：探针链路 + 主窗口。真机走查通过
- M2 ✅（201/202/203/204/205/205H/205F）：清洗器 + 缓存 + 五源引擎 + 时间轴 + 主窗口歌词。真机走查通过
- M3 ✅（301/302/303 + 滚动修复 + Lyricify 4 校准）：桌面歌词窗口壳 + 渲染 + 主窗口居中 + default_view。沙箱自测通过，**待真机走查**
- M4 ✅（401/402/403）：F9 手动校正 + 设置页（14 项即时生效）+ 稳定性（RuleHotReloadTests 修复、drift 诊断）+ 单文件发布。沙箱自测通过，**待真机走查**

## M4 完成后真机走查清单（用户侧）

**M3 桌面歌词（沙箱已自测，待真机）**：
1. 启动 YTM.Lyrics.exe，打开 YTM 播放歌曲 → 主窗口歌词面板应正常显示
2. 按 Alt+Shift+W → 桌面歌词窗口应出现，横条形态、置顶、可拖动
3. 右键桌面歌词 → 测试穿透/锁定/置顶开关
4. 修改 settings.json 中 `default_view` 为 `desktop`，重启 → 启动时主窗口应最小化，桌面歌词直接显示
5. 穿透开启后窗口不拦截鼠标，用 Alt+Shift+W 关掉窗口再开恢复正常
6. 桌面歌词单行/双行切换（改 `desktop_show_two_lines`）

**M4 F9 手动校正 + 设置页（沙箱已自测，待真机）**：
7. 播放一首自动搜索无结果/错误的歌 → 主窗口出现手动搜索面板 → 输入正确歌名搜索 → 候选列表选择 → 绑定 → 歌词显示且底部标「用户绑定」
8. 停止后重播该曲 → 直接命中绑定歌词，不再自动搜索
9. 点「解除绑定」→ 下次播放重新自动搜索
10. 主窗口设置按钮 → 打开设置窗口 → 改任意项（如桌面歌词透明度 0.5）→ 保存 → 立即生效
11. 设置里改 ws_port → 保存 → 探针自动重连到新端口
12. Ctrl+Shift+R → 桌面歌词窗口重置到屏幕居中
13. **连续使用一周无重启需求**（M4 验收门）

## 明天开工指引（修 7 条真机反馈 backlog）

> 建议拆 3 张任务卡，按依赖与手感优先级排：

### 卡 1：UI 对比度 + 预填（快，半小时级）

- **①** 设置窗口 label 黑字看不清 → SettingsWindow.xaml 各 Label/TextBlock 显式 `Foreground`（浅色，对齐 MainWindow 的 `#9A9AA6`/`#EDEDF2`）
- **②** ComboBox 白底白字 → WPF 深色主题需自定义 ComboBox Style/模板（下拉白底、选项字体白）；或最小改动：设 `Foreground`+`Background`，下拉项模板仍需自定义（默认系统模板不跟窗口色）
- **⑦** 搜索框预填歌名 → `ShowManualSearchPanel(show:true)` 时把 `_cleaner.Clean(current.Track).Title` 填入 `SearchKeywordBox`（填清洗后歌名，非原始脏标题）

### 卡 2：翻译链路完整（重，硬活，2~3 小时）

- **④** 翻译字段接入（数据层，依赖顺序：先接字段再谈渲染）：
  - QQ：`QqMusicSourceSearcher.GetLyricsCoreAsync` 目前只取 `response.Lyrics`，需同时取 `response.Trans`（QQ 翻译字段），随 `SourceLyricsResult` 传递（需扩展该 record 或新增字段）
  - 网易云：`NeteaseSourceSearcher` 丢弃 `result.Tlyric`，需取回并在 Parsing 层与主歌词行合并（Tlyric 是独立字段，不是双行 LRC）
  - Musixmatch：`GetFullLyricsRaw` 之外还需调 `GetTranslations(trackId, "zh")` 独立接口
  - 涉及：`SourceLyricsResult` 契约扩展 + 三源适配器 + `LyricsDocumentParserBridge` + 翻译合并规则
- **⑤** 元数据行过滤：网易云无版权歌 Lrc 只含「作词/作曲/编曲」元数据行，被当歌词正文。在清洗/解析层识别过滤这些元数据行（`作词[:：]`/`作曲[:：]`/`编曲[:：]` 开头行），过滤后若 Lyrics 为空则视为无歌词
- **③** 桌面歌词翻译渲染（依赖 ④）：`DesktopLyricsWindow` 当前只渲染主词行，需加翻译副行渲染（对齐主窗口的翻译行样式）

### 卡 3：F9 手动纠正体验（中，1 小时）

- **⑥** F9 常驻入口：当前 `ShowManualSearchPanel` 只在 Low/None 被动触发，切歌即消失。加常驻「搜索歌词」按钮（MainWindow 底部「设置」旁），任何时候可打开；并加「清除缓存重搜」动作（UnbindAsync 或新增 PurgeAsync，清缓存后重新自动搜索）

### 顺序建议

卡 1（快，先提升今日手感）→ 卡 3（F9 入口，影响最大的体验问题）→ 卡 2（翻译硬活，精力好时做）。或按你当天的精力灵活调整。

### 提醒

- 每张卡先读 HANDOFF 本文件 + 对应任务卡 + TDD/PRD 相关条款，不要凭记忆开工
- 卡 2 涉及 Core 层契约变更（SourceLyricsResult），属「需升级裁决」级改动，签发前先想清楚翻译数据怎么随两段式（Search→GetLyrics）流转
- 修完一张卡跑一次全量 `dotnet test`，基线 201
