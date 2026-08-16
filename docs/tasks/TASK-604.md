# TASK-604：移除 Apple Music 默认源 + 绑定失败原因透明化

- 目标：把 Apple Music 从默认歌词源移除（歌词 API 已失效），同时让绑定/搜索失败时 UI 显示具体原因（哪个源、取词失败还是无歌词），替换笼统的「绑定失败:取词或解析未成功」。
- 输入：诊断结论（2026-08-16，见下）；`Engine/EngineContracts.cs`、`Engine/LyricsEngine.cs`、`Engine/SourceSearcherContracts.cs`；`Desktop/SettingsManager.cs`、`SettingsWindow.xaml.cs`；`MainWindow.xaml.cs`；`docs/TDD.md` ADR-9、`docs/UI_SPEC.md` §4.2
- 产出：A 移除默认源 + B 失败原因透传与 UI 展示；`dotnet test` 全绿（289 + 新增）
- 边界：不删 `AppleMusicSourceSearcher` 代码与 `LyricsSourceKind.AppleMusic` 枚举（留作未来接回扩展点）；不改 CacheKey 计算；`LyricsResult.Failures` 用默认参数 null（正常路径不动）
- 上报：完成后自压缩摘要（架构师抽查 Failures 透传链路）

## 背景（架构师诊断，2026-08-16）

Apple Music 源在真机全失败，根因：**Apple Music 2025 年收紧了歌词 API 权限**。网页抓的普通 access token 能搜歌、能看到 `hasLyrics=True`，但 `include[songs]=syllable-lyrics` 不再返回歌词关系，取词必得「歌词无返回」。歌词正文现在需要 privileged developer token（付费开发者账号）或 media-user-token（Apple Music 登录态），用户两者皆无。裁决：从默认源移除，代码保留作扩展点（Helper 已预留 `SetMediaUserToken`，未来有账号再接回）。

## A. 移除 Apple Music 默认源

1. `SourcePriorityDefaults.Adr9` → `[QQMusic, Kugou, Netease, Musixmatch]`（四源）。
2. `SettingsManager.lyric_source_priority` 默认值 → `{ "qq", "kugou", "netease", "musixmatch" }`。
3. 设置窗口：`LoadFromSettings` 加载时**过滤掉 `applemusic`**（老 settings.json 里残留的 Apple Music 项自动剔除，不显示）。`SourceDisplayName` 的 `"applemusic" => "Apple Music"` 映射可保留（防御性）。
4. **保留** `AppleMusicSourceSearcher.cs` 与 `LyricsSourceKind.AppleMusic`，在 `AppleMusicSourceSearcher` 类注释头补一行：`/// 2026-08 起歌词 API 需 privileged token / media-user-token，已从默认源移除；未来有 Apple Music 登录态可经 SetMediaUserToken 接回。`
5. `App.xaml.cs` 的 `BuildRealSearchers` 保留 `[LyricsSourceKind.AppleMusic] = new AppleMusicSourceSearcher()`（代码可编译、可被显式配置启用），`TryMapSource` 的 applemusic 分支保留。
6. 文档同步：TDD §2 ADR-9 结论列改为四源 + 一句「Apple Music 2026-08 移除（歌词 API 需特权 token）」；UI_SPEC §4.2 项 2 默认值改四源；HANDOFF 开口事项第 3 条标注 Apple Music 根因已诊断。

## B. 绑定失败原因透明化

### 契约（Core，EngineContracts.cs）

```csharp
public enum SourceFailureStage { Search, Fetch, Parse, NoLyrics }  // 搜索失败/取词失败/解析空/源无歌词

public sealed record SourceFailureDetail(LyricsSourceKind Source, SourceFailureStage Stage, string? Reason);

public sealed record LyricsResult(
    LyricDocument? Document, LyricsSourceKind? Source, MatchConfidence Confidence,
    bool FromCache, bool UserBound, double OffsetSec,
    IReadOnlyList<SourceFailureDetail>? Failures = null);  // 默认 null，正常路径不动
```

### 引擎收集（LyricsEngine.cs）

- `GetLyricsAsync` 逐源循环里，每个源失败时记录 `SourceFailureDetail`（搜索无数据 → Search + "无数据"；搜索异常 → Search + error；取词失败 → Fetch + error；解析空 → Parse + "解析空文档"；源无歌词 → NoLyrics + "无歌词"）。全失败返回 None 时把 `failures` 塞进 `LyricsResult.Failures`。注意：**逐源循环里记录的是"每个源的失败原因"，但成功路径（High/Low 命中）不返回 Failures（保持 null）**。
- `BindLyricsAsync` 失败路径（取词失败/原文空/解析空）返回 `LyricsResult` 时带单个 `SourceFailureDetail`（来源=选中源，stage=Fetch/Parse，reason=具体 error）。
- `SearchCandidatesAsync` 需透传失败源：**返回类型改为新 record** `ManualSearchOutcome(IReadOnlyList<SourceCandidateGroup> Groups, IReadOnlyList<SourceFailureDetail> Failures)`，失败的源（搜索无数据/异常）记入 Failures。同步改 `ILyricsEngine` 接口、`LyricsSession.SearchCandidatesAsync`、`MainWindow.SearchAndRenderCandidates` 三处调用链。

### UI（MainWindow.xaml.cs）

- 自动搜索全失败（Confidence=None）时，状态条显示具体原因汇总，格式如：`QQ音乐:无结果 · 酷狗:无结果 · 网易云:无结果 · Musixmatch:无结果`（Failures 为空时回退原提示「暂无歌词」）。
- 手动绑定失败（result.Confidence=None 且 Failures 非空）时，显示 `绑定失败：{源} {原因}`（如「绑定失败：Apple Music 无歌词」），替换「绑定失败：取词或解析未成功」。
- 手动搜索面板：候选列表底部/末尾追加失败源一行（小字灰显），如「Apple Music: 无歌词 · 网易云: 无结果」；全部失败则显示具体原因而非空列表。

## 验收标准

1. `dotnet test` 全绿（289 + 新增：Failures 收集/透传、SearchCandidatesAsync 失败源、BindLyricsAsync 失败原因）。
2. 默认源优先级为四源（无 AppleMusic），设置窗口加载老 settings 时 applemusic 被过滤。
3. 真机：手动搜索一首歌，失败的源显示具体原因；自动搜索全失败时状态条显示原因汇总；Apple Music 不再出现在源列表。
4. `AppleMusicSourceSearcher` 与 `LyricsSourceKind.AppleMusic` 仍可编译（未删除），单测中显式用 AppleMusic 的既有测试不回归。
