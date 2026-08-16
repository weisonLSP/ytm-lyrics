# TASK-603：F11 歌词偏移 ±5s（随缓存持久化）

- 目标：歌词偏移微调。主窗口歌词面板放 `-5s` / `+5s` 按钮，桌面歌词右键菜单加偏移项，两入口操纵同一偏移状态并实时联动；偏移随缓存持久化，重播同曲自动恢复。
- 输入：PRD.md F11（P1）；TDD §3.5 `CachedLyrics.OffsetSec`（已预留字段+持久化+单测）；TASK-602 的 `LyricsSession`；`Engine/EngineContracts.cs`、`Cache/ILyricsCache.cs`
- 产出：`LyricsResult` + `ILyricsCache` + `LyricsEngine` + `LyricsSession` 契约/实现变更 + 两窗口渲染定位叠加 + UI 入口；`dotnet test` 全绿
- 边界：偏移语义冻结（见下）；步进固定 5s；不改 CacheKey 计算；偏移不影响缓存命中判定；负缓存/无歌词时不显示偏移入口
- 上报：完成后自压缩摘要（架构师抽查 offset 语义实现）

## 偏移语义（冻结，实现反了即 bug）

用户裁决（2026-08-16）：**歌词比人声慢 → 点 `+5s`（歌词提前出现）；歌词跑快 → 点 `-5s`（歌词延后）**。

技术映射（写死进代码注释与单测）：
- `OffsetSec` 为正 = 歌词提前显示；为负 = 歌词延后。
- 渲染定位：`effectivePosition = position + OffsetSec`，再用 `TimelineNavigator.FindCurrentLineIndex(doc, effectivePosition)`。
- 点 `+5s` → `OffsetSec += 5`；点 `-5s` → `OffsetSec -= 5`。
- 验证样例：某句原 Start=18s，人声 13s 就该唱（歌词慢 5s）。点 `+5s` 后 OffsetSec=5，position=13 时 effectivePosition=18 → 命中该句 = 提前 5s 显示。✓

## Core 契约变更（三处，均需同步更新单测）

1. **`LyricsResult` 加 `OffsetSec` 字段**（double）：缓存命中时透传 `cached.OffsetSec`；新取词（TryFetchAndReturnAsync）、绑定（BindLyricsAsync）传 0；负缓存/无结果传 0。所有构造 `LyricsResult` 的调用点（引擎 + 测试）补齐该参数。
2. **`ILyricsCache` 加 `UpdateOffsetAsync(string cacheKey, double offsetSec)`**：更新同键正文缓存条目（自动或绑定）的 OffsetSec；同键无条目则 no-op。LiteDB 实现走既有 update 路径，语义同 StoreAsync 的绑定优先约束（不动 UserBound）。
3. **`LyricsSession` 加 `AdjustOffset(double deltaSec)` + 渲染状态带 offset**：`LyricsRenderState` 加 `OffsetSec` 字段；`AdjustOffset` 内部 `offset += delta` → `cache.UpdateOffsetAsync(cacheKey, offset)`（cacheKey 由 session 持有的当前 CleanedTrack+duration 经 `CacheKey.Compute` 计算）→ 触发 `LyricsChanged` 携带新 offset。前提：当前 Document 非空（无词不调）。

## 渲染与 UI

- 两窗口定位代码（MainWindow.UpdateLyricsPosition / DesktopLyricsWindow 对应定位）统一改为 `position + OffsetSec`，offset 从 LyricsRenderState 取（窗口本地缓存一份当前值）。
- 主窗口：来源栏旁加两个按钮 `-5s` / `+5s`（点击调 `session.AdjustOffset(∓5)`），无歌词时禁用/隐藏。
- 桌面歌词：右键菜单（现有空菜单，UI_SPEC §5.2 标注 [待实测]）加「歌词提前 5s / 歌词延后 5s」两项，调同一 `session.AdjustOffset`。两入口状态天然联动（同一 session 状态）。

## 验收标准

1. 单测：`LyricsResult.OffsetSec` 透传（缓存命中带值、新取词为 0）；`UpdateOffsetAsync` 存取往返（含绑定条目）；定位偏移逻辑（+5 提前 / -5 延后样例）；`AdjustOffset` 累加语义。基线 267 + 新增全绿。
2. 真机：播放一首歌词慢的歌 → 主窗口点 `+5s` → 歌词提前高亮 → 停止重播 → 偏移保持（缓存持久化）；点 `-5s` 反向生效。
3. 桌面歌词右键菜单偏移与主窗口按钮联动（调 A 处 B 处同步）。
4. 无歌词（负缓存）时不出现偏移入口，点击不报错。
