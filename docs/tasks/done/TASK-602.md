# TASK-602：ADR-13 LyricsSession 下沉 Core（纯重构，行为不变）

- 目标：把两个窗口各自重复的会话编排（播放订阅、TrackSearchDeferrer 延迟搜索、歌词加载、手动绑定/解绑、重载）抽成 Core 层共享 `LyricsSession`，MainWindow / DesktopLyricsWindow / App 只做窗口、渲染与设置分发。行为完全不变。
- 输入：TDD.md ADR-13 + §5 现状偏差（已裁决）；`src/YTM.Lyrics.Core/Playback/TrackSearchDeferrer.cs`；`src/YTM.Lyrics.Desktop/App.xaml.cs`（编排中枢）、`MainWindow.xaml.cs`、`DesktopLyricsWindow.xaml.cs`；`Engine/EngineContracts.cs`
- 产出：`src/YTM.Lyrics.Core/Session/LyricsSession.cs`（新）+ 三个 Desktop code-behind 改造；`dotnet test` 全绿（267）
- 边界：禁止引入 MVVM 框架与 DI 容器（ADR-13）；不改 LyricsResult / ILyricsEngine / ILyricsCache 等既有 Core 契约签名；不顺手改清洗/搜索/渲染逻辑；取证参数（--shot / --desktop-shot / --settings-shot / --mock-engine / --f9-auto / --preload-* / --cache-db / --log-dir / --quit-after）全部保留可用
- 上报：完成后自压缩摘要（里程碑级重构，架构师抽查）

## 背景（架构师诊断，2026-08-16）

- 现状偏差（TDD §5）：MainWindow 929 行 / DesktopLyricsWindow 766 行 / App 680 行 code-behind，两窗口各自实现一套"同款"编排（代码注释里明确写着"与 MainWindow 同款"），已造成 ⑩⑪ 两次行为漂移（桌面窗口漏延迟搜索、手动绑定不联动）。
- ADR-13 已裁决：抽共享会话层持有「播放订阅、TrackSearchDeferrer、搜索/绑定/解绑/重载、设置应用」，两个窗口只订阅渲染。

## 契约草图（LyricsSession，最终以代码为准）

```csharp
namespace YTM.Lyrics.Core.Session;

/// 渲染状态快照：窗口订阅 LyricsChanged 后拿它渲染歌词面板/来源栏/状态。
public sealed record LyricsRenderState(
    LyricDocument? Document,
    LyricsSourceKind? Source,
    MatchConfidence Confidence,
    bool FromCache,
    bool UserBound);

public sealed class LyricsSession
{
    // 构造注入：IPlaybackStateMachine + ITitleCleaner + ILyricsEngine + ILyricsCache
    // 持有：TrackSearchDeferrer + 代际序号 + CancellationTokenSource（延迟搜索/切歌竞态从窗口迁入）
    // 事件：
    //   LyricsChanged(LyricsRenderState) —— 歌词加载完成/失败/无词（窗口只消费此事件渲染）
    //   TrackChanged —— 曲目切换（透传，窗口需清空旧渲染）
    // 方法：
    //   void StartLoad(TrackInfo) —— OnTrackChanged 内部走 deferrer：Immediate→加载 / Wait→起延迟轮询
    //   Task ReloadAsync() —— 重载当前曲目（手动绑定后联动刷新用，⑪ 场景）
    //   Task<IReadOnlyList<SourceCandidateGroup>> SearchCandidatesAsync(string) —— F9 候选
    //   Task<LyricsResult> BindLyricsAsync(SourceSearchResult) —— F9 绑定（内部 Clean + 取词）
    //   Task UnbindAsync() —— F9 解绑后重载
    //   void ReplacePlayback(IPlaybackStateMachine) —— ws_port 重建后换绑
    //   void ReplaceEngine(ILyricsEngine) —— 源优先级重建后换绑
}
```

要点：
- 延迟搜索轮询 timer 放哪？LyricsSession 是 Core（零 UI 依赖），不能用 DispatcherTimer。用 `System.Threading.Timer` 或 `Task.Delay` 循环实现 250ms 轮询，到 Ready 或切歌取消。这是本卡唯一的实现难点，执行者自选最稳方案。
- 窗口仍需直接订阅 machine 拿 position 做 60fps 定位（渲染层职责），LyricsSession 不管 60fps 定位。
- App 构造 LyricsSession（注入 machine/cleaner/engine/cache），两窗口构造时注入 session + machine；App 的 ManualLyricsChanged → session.ReloadAsync；设置分发（ws_port / priority）→ session.ReplacePlayback / ReplaceEngine。

## 验收标准

1. `dotnet test` 全绿（基线 267，Core 单测无破坏；若新增 LyricsSession 单测则 267 + 新增）
2. MainWindow / DesktopLyricsWindow code-behind 不再包含：TrackSearchDeferrer 实例、GetLyricsAsync / BindLyricsAsync / SearchCandidatesAsync 直接调用、延迟搜索 DispatcherTimer 编排（只保留渲染与窗口职责）
3. ⑩ 场景不回归：duration=0 首帧延迟搜索在两窗口都生效（架构师抽查 deferrer 已迁入 session 且单份）
4. ⑪ 场景不回归：主窗口手动绑定后桌面歌词刷新（session.ReloadAsync 联动）
5. 取证能力不破坏：`--mock-engine=manual --f9-auto <kw>` 跑通 F9 自动绑定序列；`--shot` / `--desktop-shot` 仍能产出截图
