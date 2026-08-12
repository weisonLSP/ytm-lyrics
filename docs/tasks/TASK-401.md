# TASK-401：F9 手动搜索校正与绑定覆盖

- **目标**：用户可在主窗口手动输入关键词搜索歌词，从候选列表选择后绑定到当前曲目；绑定后重播该曲直接命中绑定结果，不再发起自动搜索。支持解除绑定回退到自动搜索。

- **输入**：必读文件清单
  - `docs/PRD.md` §4 S2、§5 F9
  - `docs/TDD.md` §3.3（ILyricsEngine）、§3.5（ILyricsCache）
  - `src/YTM.Lyrics.Core/Engine/EngineContracts.cs`（ILyricsEngine 接口 + LyricsResult）
  - `src/YTM.Lyrics.Core/Engine/SourceSearcherContracts.cs`（SourceSearchResult、LyricsRawFormat）
  - `src/YTM.Lyrics.Core/Engine/LyricsEngine.cs`（当前实现，参考搜索流程）
  - `src/YTM.Lyrics.Core/Cache/ILyricsCache.cs`（当前接口）
  - `src/YTM.Lyrics.Core/Cache/LiteDbLyricsCache.cs`（当前实现，BindUserChoiceAsync 已可用）
  - `src/YTM.Lyrics.Core/Cache/CacheKey.cs`（Compute 方法）
  - `src/YTM.Lyrics.Core/Cleaning/`（ITitleCleaner / CleanedTrack）
  - `src/YTM.Lyrics.Desktop/MainWindow.xaml`（当前 UI 布局）
  - `src/YTM.Lyrics.Desktop/MainWindow.xaml.cs`（当前逻辑，ApplyLyricsResult / ShowLyricsState）
  - `src/YTM.Lyrics.Desktop/App.xaml.cs`（BuildEngine 参考 DI 模式）

- **产出**：三部分，按顺序完成

  ### Part A：Core 层接口扩增

  | 文件 | 改动 |
  |---|---|
  | `src/YTM.Lyrics.Core/Cache/ILyricsCache.cs` | 新增 `Task UnbindAsync(string cacheKey)` ——删除同键正文缓存条目（不区分 UserBound），使下次自动搜索生效 |
  | `src/YTM.Lyrics.Core/Cache/LiteDbLyricsCache.cs` | 实现 UnbindAsync：删除 lyrics 集合中该键文档 + negative 集合中该键文档（两个 try/catch，LiteDB Delete 不存在也不抛异常但防御性写）；信号量内执行 |
  | `src/YTM.Lyrics.Core/Engine/EngineContracts.cs` | ILyricsEngine 新增两个方法：<br>① `Task<IReadOnlyList<SourceCandidateGroup>> SearchCandidatesAsync(string keyword, CancellationToken ct = default)` ——用户输入原始关键词（不经过 Clean 自动流程），按 SourcePriority 逐源搜索，每源返回候选列表+匹配分；不取词、不写缓存。返回候选中不含 UserBound 语义（那是绑定后的存储层属性）。<br>② `Task<LyricsResult> BindLyricsAsync(CleanedTrack track, double durationSec, SourceSearchResult selected, CancellationToken ct = default)` ——对选定候选取词→解析→写绑定缓存→返回结果 |
  | `src/YTM.Lyrics.Core/Engine/EngineContracts.cs` | 新增记录类型 `SourceCandidateGroup(LyricsSourceKind Source, IReadOnlyList<ScoredCandidate> Candidates)` 和 `ScoredCandidate(SourceSearchResult Result, SourceMatchScore Score)` ——候选列表展示用（来源+候选+匹配分） |
  | `src/YTM.Lyrics.Core/Engine/LyricsEngine.cs` | 实现 SearchCandidatesAsync：逐源调用 searcher.SearchAsync，对每个候选调用 scorer.Score 算分，过滤掉 titleScore < TitleHardGate 的无效候选（复用 MatchScoreTable）。单源异常/无结果顺延（同 GetLyricsAsync 语义）。**不取词、不写缓存**。返回所有源的分组候选。<br>BindLyricsAsync：取词（同 TryFetchAndReturnAsync 的取词部分）→解析→调用 cache.BindUserChoiceAsync 写绑定缓存→返回 LyricsResult(UserBound=true) |
  | `src/YTM.Lyrics.Core.Tests/` | 新增单测：<br>① UnbindAsync 后 TryGetAsync 返回 null<br>② UnbindAsync 后 IsNegativeCachedAsync 返回 false<br>③ SearchCandidatesAsync 返回分组候选（用 mock searcher）<br>④ BindLyricsAsync 写入后 TryGetAsync 命中且 UserBound=true<br>⑤ 绑定后自动 StoreAsync 不覆盖绑定条目（已有语义，补回归测试即可） |

  ### Part B：Core 层新增手动搜索服务（可选，如果直接扩 ILyricsEngine 则不需要）→ 已决定直接扩 ILyricsEngine

  ### Part C：Desktop 层 F9 UI

  | 文件 | 改动 |
  |---|---|
  | `src/YTM.Lyrics.Desktop/MainWindow.xaml` | 在主窗口底部区域新增手动搜索面板：<br>① 搜索栏（TextBox + Button"搜索"），默认隐藏，当自动搜索结果为 Low/None 时显示<br>② 搜索结果列表区域（ScrollViewer 内 StackPanel），每项显示：来源标签、标题、艺人、时长、匹配分（简短），点击选中<br>③ "使用此歌词"按钮（选中后启用）+ "取消"按钮（隐藏搜索面板）<br>④ 已有绑定结果时显示"解除绑定"按钮（SourceBarText 旁或搜索面板内） |
  | `src/YTM.Lyrics.Desktop/MainWindow.xaml.cs` | ① 注入 ILyricsEngine 引用（已有）和 ILyricsCache 引用（当前 MainWindow 构造未注入 cache，需新增）<br>② 搜索按钮 → 调用 engine.SearchCandidatesAsync → 渲染候选列表<br>③ 选定候选 → 调用 engine.BindLyricsAsync → 更新歌词显示 + SourceBarText 标记"用户绑定"<br>④ 解除绑定 → cache.UnbindAsync → 触发重新自动搜索（调用 StartLoad）<br>⑤ ApplyLyricsResult 中识别 UserBound=true → 在 SourceBarText 追加"🔗 用户绑定"标识 |
  | `src/YTM.Lyrics.Desktop/App.xaml.cs` | MainWindow 构造函数传参新增 ILyricsCache（BuildEngine 同期注入） |

- **验收标准**（对应 PRD §4 S2）：
  1. 播放一首搜索无结果或错误结果的歌曲 → 主窗口出现"手动搜索"入口
  2. 输入正确歌名搜索 → 候选列表显示各源结果（来源、时长、匹配分）
  3. 选择正确候选后绑定 → 歌词立即显示，底部来源条标注"用户绑定"
  4. 停止后重新播放同一首歌 → 直接命中绑定歌词，不再自动搜索（日志可见 `userBound=True`）
  5. 点击"解除绑定" → 下次播放该曲重新走自动搜索流程

- **边界**：
  - 不允许修改 LyricsEngine.GetLyricsAsync 的主流程（只扩接口，不改已有逻辑）
  - 不允许修改 SourcePriorityDefaults（源优先级不变）
  - 不允许改动 DesktopLyricsWindow（F9 仅主窗口）
  - 不允许修改 Core 层 Cleaning 模块
  - UnbindAsync 只删缓存条目，不触发 UI 刷新（UI 刷新由调用方负责）
  - 搜索面板不要做得太花哨：最小可用即可。交付即停，不顺手做动画/美化/键盘导航

- **上报**：Part A + Part C 全部完成后，将产出目录（Core 变更 + Desktop 变更 + Tests）交给压缩器产出摘要。自测证据必须包含：单元测试通过截图 + 手动搜索→绑定→重播命中 的日志链完整截图。
