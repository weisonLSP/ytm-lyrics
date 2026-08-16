# TDD：YTM.Lyrics 技术设计文档

| 字段 | 内容 |
|---|---|
| 版本 | v1.0（2026-08-16：待回填项全部关闭，新增 ADR-13） |
| 作者 | k3（架构师） |
| 阅读对象 | 执行 agent（kathrik）：本文件是编码时的最高约束，与 PRD 冲突时以 PRD 为准并立即上报 |

> 契约的代码形态以 `src/YTM.Lyrics.Core/**` 各 Contracts 文件为准；本文件记录设计意图与约束。v0.5 之后的契约演进见 §3.7。

---

## 1. 架构总览

```
浏览器（YTM 页面）
  └─ probe/ytm-lyrics-probe.user.js   油猴探针，唯一浏览器内代码
        │ WebSocket (ws://127.0.0.1:17865)
        ▼
YTM.Lyrics.Desktop (WPF, net8.0-windows)
  ├─ 引用 Core，只做：窗口、渲染、动画、设置界面
  └─ 禁止包含：搜索逻辑、解析逻辑、缓存逻辑、协议解析
        │
YTM.Lyrics.Core (net8.0 类库，零 UI 依赖)
  ├─ Playback/      状态机、位置外推、IPlaybackProvider
  ├─ Playback/Providers/  WebSocketProbeProvider
  ├─ Cleaning/      标题清洗器（纯函数）
  ├─ Engine/        多源搜索、打分、fallback、节流
  ├─ Timeline/      统一歌词模型、当前行定位
  ├─ Cache/         LiteDB 读写、负缓存、用户绑定
  └─ External/      Lyrics Helper 防腐层（唯一允许引用 Helper 的目录）
        │
Lyricify.Lyrics.Helper   external/Lyricify-Lyrics-Helper 源码项目引用（master @ c14bba9，submodule 固化）
# 不用 NuGet 0.2.0：master 领先 3 个提交，其中含 QRC 解密 bug 修复，NuGet 版带 bug
```

**依赖铁律**：依赖方向单向（Desktop → Core → Helper），反向禁止。Core 内任何文件出现 `System.Windows` / `System.Windows.Controls` 引用即为架构违规，CI 检查项。

## 2. ADR（架构决策记录）

| # | 决策 | 结论 | 理由 | 状态 |
|---|---|---|---|---|
| ADR-1 | 桌面端技术栈 | C# / .NET 8 + WPF | 复用 .NET Standard 的 Helper；透明置顶穿透窗口原生支持 | 冻结 |
| ADR-2 | 探针载体 | 油猴脚本 | YTM 全形态皆为 Chrome 页面；维护者已有经验；失效爆炸半径最小 | 冻结 |
| ADR-3 | 探针通信 | WebSocket loopback | 浏览器沙箱内最自然的双向通道；支持未来多探针与反向控制 | 冻结 |
| ADR-4 | 歌词供应链 | Lyricify.Lyrics.Helper | Apache 2.0；六源+解密+解析为实战代码 | 冻结：**源码引用 master @ c14bba9**，不用 NuGet 0.2.0（带 QRC 解密 bug） |

| ADR-5 | 缓存存储 | LiteDB | 纯托管嵌入式，单文件零依赖 | 冻结 |
| ADR-6 | 进度同步 | 快照 + 本地外推 + 漂移校正 | 探针 2s 心跳即可支撑逐字级滚动；探针因此可做薄 | 冻结 |
| ADR-7 | 跨平台预留 | Core 零 UI + IPlaybackProvider | Android (MAUI) 未来只写 UI 与新探针实现 | 冻结 |
| ADR-8 | Helper 隔离 | External/ 防腐层，内部模型不暴露 Helper 类型 | Helper 升级/fork 时影响面限于一层 | 冻结 |
| ADR-9 | 默认源优先级 | QQ音乐 → 酷狗 → 网易云 → Musixmatch（Apple Music 2026-08 移除：歌词 API 需特权 token）；**汽水移出歌词源列表** | 汽水只有搜索 API 无歌词获取接口；网易云硬编码 Cookie + 限流(-460) 降为低优先级；Apple Music 代码/枚举保留作扩展点，未来有登录态可经 SetMediaUserToken 接回（TASK-604）；可配置，不违反 PRD | 冻结 | 汽水只有搜索 API 无歌词获取接口；网易云硬编码 Cookie + 限流(-460) 降为低优先级；可配置，不违反 PRD | 冻结 |
| ADR-10 | 沙箱构建参数 | 构建/测试统一携带 `-property:MSBuildEnableWorkloadResolver=false -maxcpucount:1`（推荐经 `.nuget/` 响应文件）；**前置：`DOTNET_CLI_HOME` 指向工作区内可写目录（如 `.nuget/dotnet-home`）**（TASK-102 补录） | 本机 SDK 8.0.424 被裁剪缺 workload locator（MSB4276）；受限 token 阻断 MSBuild worker 节点 spawn（0 错误静默退出 1）；CLI 首启 sentinel 写入用户目录被拒。均为官方机制/环境变量重定向，仅沙箱内需要 | 冻结（TASK-101/102 实测） |
| ADR-11 | NuGet 离线源 | `.nuget/local-feed/` + 仓库根 nuget.config（不提交）；Python/OpenSSL 拉包（`.nuget/fetch_packages.py`） | 受限 token 下 Schannel TLS 不可用（SEC_E_NO_CREDENTIALS），Python ssl 走 OpenSSL 可出网。正常环境删除 nuget.config 即恢复默认源。NU1603 近似匹配警告已接受（不影响产物） | 冻结（TASK-101 实测） |
| ADR-12 | 发布形态 | **framework-dependent 单文件**（依赖本机 .NET 8 Desktop Runtime），不用 self-contained | self-contained 自解压 native dll 到 `%TEMP%`，本机用户名含单引号（`wei'son`）导致解压路径解析失败，WPF 启动 DllNotFoundException（2026-08-12 实测，事件日志实证）。沙箱验证通过但用户环境崩溃，验证环境差异警示。framework-dependent 单文件无自解压机制，绕开此坑 | 冻结 |
| ADR-13 | 桌面编排下沉 Core（LyricsSession） | 抽共享会话层持有：播放订阅、TrackSearchDeferrer、搜索/绑定/解绑/重载、设置应用；两个窗口只订阅渲染。禁止引入 MVVM 框架与 DI 容器 | 两窗口各自编排已产生 ⑩⑪ 两次「行为漂移」bug（桌面窗口漏延迟搜索、手动绑定不联动）；MainWindow 929 行 / DesktopLyricsWindow 766 行 / App 680 行 code-behind 违反 §5「只做窗口/渲染」 | **已落地（TASK-602，2026-08-16）** |

## 3. Core 模块契约（设计意图冻结，代码形态以 Contracts 文件为准）

### 3.1 Playback

```csharp
namespace YTM.Lyrics.Core.Playback;

public sealed record TrackInfo(string RawTitle, string RawArtist, double DurationSec);

public sealed record PlaybackSnapshot(
    TrackInfo Track, double PositionSec, bool Playing,
    long Seq, DateTimeOffset SentAt, DateTimeOffset ArrivedAt);

public interface IPlaybackProvider
{
    event EventHandler<PlaybackSnapshot>? SnapshotReceived;
    event EventHandler? ProviderLost;          // 心跳超时 10s 触发
    Task StartAsync(CancellationToken ct = default);
}

// 状态机：对 UI 暴露"当前应显示什么"
public interface IPlaybackStateMachine
{
    PlaybackSnapshot? Current { get; }
    TimeSpan GetInterpolatedPosition();        // ADR-6 外推实现
    event EventHandler? TrackChanged;          // 曲目标识变化（驱动歌词加载）
    event EventHandler? PlayStateChanged;
}
```

外推规则（ADR-6，TASK-102 实现后补全）：
- 播放中：`position = 基准.Position + (now − 基准.ArrivedAt)`；暂停/断线：冻结于最新快照位置
- 基准锚定三分支：首帧直接锚定；**播放状态切换（暂停/恢复）强制重置基准**（不重置则恢复后用旧基准外推错乱）；同状态播放中 |外推值 − 快照值| > 0.8s 时接受快照值（seek 校正）
- 漂移 ≤ 0.8s 时保持旧基准，保证位置连续不抖动

### 3.2 Cleaning（纯函数，单测必须 100% 分支覆盖规则表）

```csharp
namespace YTM.Lyrics.Core.Cleaning;

public sealed record CleanedTrack(string Title, IReadOnlyList<string> ArtistCandidates);

public interface ITitleCleaner
{
    CleanedTrack Clean(TrackInfo raw);   // 规则表外置 JSON，热加载
}
```

### 3.3 Engine

```csharp
namespace YTM.Lyrics.Core.Engine;

public enum LyricsSourceKind { QQMusic, Netease, Kugou, Soda, AppleMusic, Musixmatch }
public enum MatchConfidence { High, Low, None }

public sealed record LyricsResult(
    LyricDocument? Document, LyricsSourceKind? Source,
    MatchConfidence Confidence, bool FromCache, bool UserBound);

public interface ILyricsEngine
{
    // 内部顺序：缓存命中(含用户绑定) → 负缓存检查 → 按优先级逐源搜索+打分 → 写缓存
    Task<LyricsResult> GetLyricsAsync(CleanedTrack track, double durationSec, CancellationToken ct = default);
    IReadOnlyList<LyricsSourceKind> SourcePriority { get; }  // 可配置
}
```

打分规则：歌名 + 艺人 + 时长三维，时长容差 ±3s；阈值达到为 High，否则顺延；所有源 Low 时返回最佳候选并标 Low（UI 提示确认）；全失败写负缓存（24h）。分值表按 `docs/research/helper-api-map.md` 中 CompareHelper 的真实能力在 TASK-203 定稿，k3 抽查。

### 3.4 Timeline

```csharp
namespace YTM.Lyrics.Core.Timeline;

public sealed record LyricSyllable(TimeSpan Start, TimeSpan Duration, string Text);
public sealed record LyricLine(
    TimeSpan Start, TimeSpan? End, string Text,
    string? Translation,                          // 翻译行，默认显示（PRD 决策3）
    IReadOnlyList<LyricSyllable>? Syllables);     // 逐字时间轴，v1 数据层必须保留，渲染 P1

public sealed class LyricDocument
{
    public required IReadOnlyList<LyricLine> Lines { get; init; }
    public LyricsSourceKind Source { get; init; }
}

public static class TimelineNavigator
{
    public static int FindCurrentLineIndex(LyricDocument doc, TimeSpan position); // 二分查找
}
```

### 3.5 Cache

```csharp
namespace YTM.Lyrics.Core.Cache;

public sealed record CachedLyrics(
    string CacheKey, string RawText, LyricsSourceKind Source,
    bool UserBound, double OffsetSec, DateTimeOffset StoredAt);

public interface ILyricsCache
{
    Task<CachedLyrics?> TryGetAsync(string cacheKey);
    Task<bool> IsNegativeCachedAsync(string cacheKey);   // 24h TTL
    Task StoreAsync(CachedLyrics entry);
    Task StoreNegativeAsync(string cacheKey);
    Task BindUserChoiceAsync(string cacheKey, CachedLyrics entry);  // 永久
}

public static class CacheKey
{
    public static string Compute(CleanedTrack track, double durationSec);
    // sha1(Title.ToLowerInvariant + "|" + 主艺人.ToLowerInvariant + "|" + durationBucket)，bucket 按 ±3s 归并
}
```

### 3.6 External（防腐层）

- Core 内唯一直接 `using Lyricify.Lyrics.Helper` 的目录
- 职责：Helper 类型 → §3.4 内部模型的单向转换；Helper 搜索 API → §3.3 的源适配器
- 五个源各一个适配器类（汽水已移出，ADR-9），实现内部接口 `ISourceSearcher`；签名按 `docs/research/helper-api-map.md` 在 TASK-203 定稿，k3 抽查

**防腐层必须吸收的 Helper 已知坑（TASK-001 调研结论）：**
1. `TypeHelper.GetLyricsTypes` 是占位实现（恒返回 Unknown），`ParseLyrics(string)` 单参版永远返回 null → 防腐层调用解析时**必须显式传歌词类型**，禁止依赖自动探测
2. QRC 解析器丢弃 `[mm:ss.xx]` 行级时间标签，只产出相对行首的音节时间 → 防腐层在转换 QRC 时**负责重建行级 Start**（从原始 QRC 文本提取行标签，再叠加音节相对时间），`LyricLine.Start` 不允许为 null
3. Helper 使用全局静态 HttpClient 且 Searcher 静默吞异常 → 防腐层对同一源的调用**串行化**（与节流策略一致），捕获所有异常并转换为带源标识的结果对象，禁止静默失败向上传播为“无结果”
4. Helper 部分同步方法用 `.Result` 阻塞 → Core/Desktop 只允许调用 Async 版本，代码审查红线

### 3.7 契约演进记录（v0.5 之后，以代码为准）

| 时间 | 演进 | 位置 |
|---|---|---|
| M2 实战修复 | `TrackSearchDeferrer`：duration=0 首帧延迟搜索（250ms 轮询 + 3s 超时兜底），主窗口与桌面歌词窗口共用 | Playback/TrackSearchDeferrer.cs |
| M4 | F9 手动校正：`SearchCandidatesAsync`/`BindLyricsAsync` + `ScoredCandidate`/`SourceCandidateGroup` | Engine/EngineContracts.cs |
| M4 | F9 解绑：`ILyricsCache.UnbindAsync`（删正文+负缓存条目） | Cache/ILyricsCache.cs |
| M4 | 清洗规则热加载：`TitleCleaner.LoadRules(path)`（volatile 快照 + lock 切换 watcher） | Cleaning/ |
| 2026-08-13 | 翻译链路：QQ Trans / 网易云 Tlyric / Musixmatch GetTranslations 三源接入 + TranslationMerger（10ms 量化时间戳对齐）+ LrcLyricsConverter credits 行过滤 | External/ |

## 4. WebSocket 协议 v1（冻结）

探针 → 桌面端（JSON 文本帧）：

```json
{ "type": "state", "seq": 1024,
  "track": { "title": "原始标题", "artist": "原始频道名", "duration": 234.5 },
  "position": 67.2, "playing": true, "sentAt": 1786531200000 }
```

- 事件即发（切歌/暂停/恢复/seek），稳态 2s 心跳
- `seq` 单调递增，桌面端丢弃 ≤ 已收最大 seq 的帧；**seq 语义为连接级**：每连接独立维护 maxSeq，探针重连后从新序列开始（TASK-102 裁决）
- 桌面端 → 探针：`{ "type": "hello", "version": "1" }`（建连确认）
- 错误帧：`{ "type": "error", "code": "...", "message": "..." }`（仅记录日志，不改变状态机）
- 协议演进规则：字段只增不改；破坏性变更升 `version` 并走 hello 协商

## 5. Desktop 层设计约束

- 允许：窗口管理（透明/置顶/穿透/拖动）、动画、设置绑定、把 Core 事件转成 ViewModel 通知
- 禁止：任何 `using YTM.Lyrics.Core.Engine` 以外的业务编排；直接 `using` Helper 命名空间（红线）
- ViewModel 层薄：一行歌词的显示状态（过去/当前/未来）由 Core 计算，Desktop 只读
- **现状偏差（2026-08-16 已整改，TASK-602）**：~~MainWindow 929 行 / DesktopLyricsWindow 766 行 / App 680 行，两窗口各自实现会话编排~~ → 已抽 `Core/Session/LyricsSession.cs` 下沉（持有播放订阅、TrackSearchDeferrer、加载/重载、F9 绑定/解绑、引擎与播放链路换绑），两窗口只订阅 `TrackChanged`/`LyricsChanged` 渲染，App 只做构造与设置分发。编排单份，⑩⑪ 漂移根因消除。

## 6. 测试策略

| 层 | 测试 | 标准 |
|---|---|---|
| Cleaning | 单测 | 20 条真实脏标题样本，清洗正确率 ≥ 90%（TASK-201） |
| Timeline | 单测 | 二分定位边界（首行前/末行后/行间空隙）全覆盖 |
| Engine | 单测（源 mock）+ 少量真实源冒烟 | fallback 顺序、节流、负缓存 TTL |
| Playback | 单测 | 外推、漂移校正、乱序帧丢弃、ProviderLost |
| Desktop | 手动走查 | PRD §4 S1–S4 |

## 7. 待回填清单

✅ v0.5 的 7 项待回填已全部关闭（2026-08-16）。历史明细见 git 历史或 docs/history/2026-08.md。
