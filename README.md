# YTM.Lyrics

YouTube Music 桌面歌词应用。C# / .NET 8 + WPF，歌词供应链复用 [Lyricify.Lyrics.Helper](https://github.com/WXRIW/Lyricify-Lyrics-Helper)（Apache-2.0）。

**形态**：油猴探针（浏览器内采集播放状态）+ 本地桌面应用（多源歌词搜索 / 缓存 / 滚动显示）。

```
YTM 页面 → 油猴探针（零类名依赖，只认 HTML5 媒体元素 + MediaSession）
        → WebSocket (127.0.0.1:17865) → Core 状态机（快照+本地外推）
        → 标题清洗 → 五源搜索引擎（QQ/酷狗/Apple/网易云/Musixmatch，打分+fallback）
        → LiteDB 缓存 → 时间轴 → WPF 渲染
```

## 使用（自用走查版）

1. **桌面端**：`dist/YTM.Lyrics.exe` 双击（依赖本机 .NET 8 Desktop Runtime，ADR-12）
2. **探针**：`probe/ytm-lyrics-probe.user.js` 拖入 Chrome（需 Tampermonkey）安装
3. 打开 music.youtube.com 放歌，30 秒内自动连接（刷新页面立即连）

日志：`%AppData%\YTM.Lyrics\logs\app.log`（实时落盘）；缓存：`%AppData%\YTM.Lyrics\lyrics.db`。

## 构建

```powershell
# 沙箱/本机统一姿势（ADR-10/11）：
$env:DOTNET_CLI_HOME="$PWD\.nuget\dotnet-home"
dotnet msbuild YTM.Lyrics.sln -target:Restore,Build -property:MSBuildEnableWorkloadResolver=false -maxcpucount:1
dotnet test src/YTM.Lyrics.Core.Tests/YTM.Lyrics.Core.Tests.csproj   # 全绿基线：260

# 发布（framework-dependent 单文件，ADR-12）：
dotnet publish src/YTM.Lyrics.Desktop -c Release --self-contained false -p:PublishSingleFile=true -p:MSBuildEnableWorkloadResolver=false -maxcpucount:1
```

**正常环境（无沙箱）**：去掉两个 MSBuild 参数与 DOTNET_CLI_HOME 即可；删除 `nuget.config` 恢复默认 NuGet 源。

## 项目结构

- `probe/`：油猴探针（唯一浏览器内代码，红线：不依赖任何 YTM CSS 类名/DOM 层级）
- `src/YTM.Lyrics.Core`：net8.0 类库，零 UI 依赖（Playback / Cleaning / Engine / Timeline / Cache / External）
- `src/YTM.Lyrics.Desktop`：WPF，只做窗口/渲染，禁止业务编排
- `src/YTM.Lyrics.Core.Tests`：xunit（260 用例基线）
- `external/Lyricify.Lyrics-Helper`：歌词供应链源码（不纳入主仓库版本控制）
- `tools/`：e2e 取证与诊断工具（Node/Python/PowerShell 编排、样本、接口诊断脚本）

依赖铁律（单向，反向禁止）：`Desktop → Core → Helper`。Helper 只许经 `Core/External/` 防腐层访问。

## 第三方依赖：Lyricify.Lyrics.Helper（源码固化）

- 固化 commit：**`c14bba9`**（master）。领先 NuGet v0.2.0 共 3 个提交（含 QRC 解密修复），**不用 NuGet 0.2.0**（ADR-4）
- 非 submodule 的原因与本机沙箱限制见 docs/HANDOFF.md「环境坑位手册」
- 升级方式：`cd external/Lyricify-Lyrics-Helper && git fetch && git checkout <新commit>`，全量构建+测试验证后更新本记录
- **已知上游情报**：QQ 音乐 `musicu.fcg` 搜索模块已失效（2026-08），本项目适配器已切老接口（详见 QqMusicSourceSearcher 注释）

## 文档地图

| 文档 | 内容 |
|---|---|
| **docs/HANDOFF.md** | 会话交接包（新窗口启动协议、坑位手册、backlog） |
| docs/PRD.md | 产品需求（v1.0 已冻结） |
| docs/TDD.md | 技术设计 + 12 条 ADR（架构最高约束） |
| docs/UI_SPEC.md | UI 规格（M3 桌面歌词窗口的依据） |
| docs/WORKFLOW.md | 协作流水线章程（含成本纪律与提权十律） |
| docs/DEVELOPMENT_PLAN.md | 里程碑与任务卡计划 |
| docs/research/ | Helper API 速查表、UI 调研草稿 |

## 状态

- **M1 ✅ M2 ✅**（2026-08-12 真机走查通过）：链路 + 引擎可用，五源搜索/缓存/翻译行/滚动显示正常
- **M3 ✅**（2026-08-12 沙箱自测通过）：桌面歌词窗口 + 主窗口居中 + default_view，滚动参数已按 Lyricify 4 实测校准（见 docs/research/lyricify4-bench.md），待真机走查
- **M4 ✅**（2026-08-12 沙箱自测通过，真机一周无重启待用户走查）：F9 手动校正 + 设置页 + 稳定性（RuleHotReloadTests 偶发失败修复、drift 校正诊断）+ 单文件发布。dist/YTM.Lyrics.exe 为含全部 M1–M4 改动的 framework-dependent 单文件产物（TASK-403）
- **真机反馈 ⑨⑩⑪ ✅**（2026-08-14 真机走查通过）：桌面歌词 duration 延迟搜索（⑩）+ 手动绑定联动刷新（⑪）+ 探针罗马音降级（⑨）。dist/YTM.Lyrics.exe 已重新发布（含 ⑩⑪）；探针需 Tampermonkey 更新到 0.1.1（含 ⑨）
