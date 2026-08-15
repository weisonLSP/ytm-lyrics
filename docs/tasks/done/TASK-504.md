# TASK-504：翻译字段接入（QQ Trans / 网易云 Tlyric / Musixmatch GetTranslations）+ 网易云 credits 元数据行过滤

- **目标**：把 QQ、网易云、Musixmatch 三源的翻译字段接入歌词供应链，翻译随原文流转到 `LyricLine.Translation`（主窗口/未来桌面歌词都读它）；同时过滤网易云无版权歌返回的「作词/作曲/编曲」credits 元数据行。当前唯一出翻译的路径只有酷狗 KRC 的 `[language:]` 段（已工作，本卡不碰）。

- **输入**：必读文件清单（按顺序读）
  - `docs/HANDOFF.md`（「卡 2：翻译链路完整」一节，含 ③④⑤ 原始反馈）
  - `docs/TDD.md` §3.3（Engine）、§3.4（Timeline/LyricLine.Translation）、§3.6（防腐层四坑）
  - `src/YTM.Lyrics.Core/Engine/SourceSearcherContracts.cs`（`SourceLyricsResult` 契约，本卡要扩展）
  - `src/YTM.Lyrics.Core/Engine/EngineContracts.cs`（`ILyricsDocumentParser`，本卡要扩展）
  - `src/YTM.Lyrics.Core/Engine/LyricsEngine.cs`（Parse 调用点 ×3：GetLyricsAsync 缓存命中、TryFetchAndReturnAsync、BindLyricsAsync）
  - `src/YTM.Lyrics.Core/External/Sources/QqMusicSourceSearcher.cs`（GetLyricsCoreAsync 现只取 `response.Lyrics`）
  - `src/YTM.Lyrics.Core/External/Sources/NeteaseSourceSearcher.cs`（GetLyricsCoreAsync 现丢弃 `result.Tlyric`）
  - `src/YTM.Lyrics.Core/External/Sources/MusixmatchSourceSearcher.cs`（GetLyricsCoreAsync 现只取 `GetFullLyricsRaw`）
  - `src/YTM.Lyrics.Core/External/Sources/LyricsDocumentParserBridge.cs`（解析缝，本卡要接翻译合并）
  - `src/YTM.Lyrics.Core/External/Parsing/LrcLyricsConverter.cs`（已有「同时间戳下一行归并」，本卡要加 credits 过滤）
  - `src/YTM.Lyrics.Core/External/Parsing/LyricsDocumentParser.cs`（各格式分派）
  - `src/YTM.Lyrics.Core/Cache/ILyricsCache.cs` + `LiteDbLyricsCache.cs`（`CachedLyrics`/`LyricsDoc`，本卡要加 TranslationText）
  - Helper 源码（只读参考，勿改）：`external/.../Providers/Web/Netease/Response.cs`（LyricResult.Tlyric）、`external/.../Providers/Web/MusixMatch/Api.cs`（GetTranslations）+ `Response.cs`（GetTranslationsResponse，~555 行起）、`external/.../Providers/Web/QQMusic/Api.cs`（GetLyricsAsync 的 Trans 字段来源）

- **架构裁决（raphael 已定，执行者照做，不自行变更）**

  ### 翻译数据流转（两段式 Search → GetLyrics）

  ```
  SearchAsync → SourceSearchResult(SourceId)
    → 打分选定
    → GetLyricsAsync(SourceId) → SourceLyricsResult(RawText + TranslationText)   ← 翻译在取词阶段拿到
    → Parse(RawText + TranslationText, source, format) → LyricDocument(每行 LyricLine.Translation)
    → 缓存 CachedLyrics(RawText + TranslationText)
    → 缓存命中 → Parse(RawText + TranslationText, ...) → 翻译随缓存恢复
  ```

  ### 契约扩展（3 处，均为向后兼容的「新增字段/新增参数」）

  1. `SourceLyricsResult` 新增 `string? TranslationText`，位置在 `RawText` 之后、`ErrorMessage` 之前。翻译原文格式由源决定（见下表），失败时为 null。
  2. `CachedLyrics` 新增 `string? TranslationText`（`RawText` 之后）。`LiteDbLyricsCache.LyricsDoc` 同步加字段，`ToDoc`/`ToEntry` 透传；LiteDB 对旧文档缺失字段自动返回 null，无需迁移。
  3. `ILyricsDocumentParser.Parse` 新增可选参数 `string? translationText = null`。`LyricsDocumentParserBridge` 实现：解析主歌词拿到 document 后，若 translationText 非空白，调新增的 `TranslationMerger.Merge(document, translationText, source)` 合并翻译，再返回。

  ### 翻译承载约定（每源 TranslationText 是什么）

  | 源 | TranslationText 内容 | 格式 |
  |---|---|---|
  | QQMusic | `QqLyricsResponse.Trans` 明文（`Api.GetLyricsAsync` 已把 `ts` 字段解密为明文，无需再 Decode） | **待真实取证**：大概率 LRC（`[mm:ss.xx]译文`），小概率纯文本。取证后按「LRC → 时间戳对齐 / 纯文本 → 行序对齐」二选一实现，并在上报的决策点里写清证据 |
  | Netease | `result.Tlyric?.Lyric`（LRC 格式，带时间戳；注意主歌词走 Yrc 时翻译仍是 Tlyric 独立字段） | LRC，确定 |
  | Musixmatch | 适配器调 `_api.GetTranslations(sourceId, "zh")`，从 `GetTranslationsResponse.Message.Body.TranslationsList[].Translation` 提取「原文行 + 译文」，序列化为 JSON（见下） | 中间格式（适配器序列化） |

  ### 翻译合并策略（Parsing 层新增 `TranslationMerger`，按 source 分派）

  - **Netease / QQMusic（LRC 翻译）**：用 `Lyricify.Lyrics.Parsers.LrcParser.ParseLyrics(translationText)`（或 `ParseHelper.ParseLyrics(text, LyricsRawTypes.Lrc)`）解析出翻译行（StartTime + Text），再与主歌词 `LyricDocument.Lines` 按时间戳匹配：翻译行 `StartTime == 主词行 Start` 时，把翻译文本填入该主词行 `LyricLine.Translation`（覆盖 null 或已有值）。时间戳对不上的翻译行丢弃（宁可少翻译，不可错位）。
  - **Musixmatch（MatchedLine 对齐）**：适配器序列化的 JSON 形如 `[{"matchedLine":"原文行","translation":"译文"}]`（用 `System.Text.Json`；字段名执行者定稿但要稳定）。合并时遍历主歌词行，按 `line.Text`（Trim 后）精确匹配 `matchedLine`，命中则填 `Translation`。
  - **酷狗 KRC**：不改。KRC 翻译已由 `KrcLyricsConverter` 内嵌 `[language:]` 段处理，`TranslationText` 恒为 null。

  ### 网易云 credits 元数据行过滤（反馈 ⑤）

  在 `LrcLyricsConverter.Convert` 的循环里，对**无时间戳或带时间戳**的行文本做过滤：若 `text` 以 `作词`/`作曲`/`编曲`/`制作人`/`混音` 等 credits 关键词开头（匹配 `^((作词|作曲|编曲|制作人|混音|母带|录音|和声|吉他|贝斯|鼓)[:：]?)`，含半角/全角冒号可选），跳过该行不加入 Lines。过滤后若 Lines 为空，返回空文档（引擎已有「解析空文档」防线，会按失败处理，不落缓存、顺延下一源）。

- **产出**：按 Part 顺序完成

  ### Part A：契约扩展

  | 文件 | 改动 |
  |---|---|
  | `src/YTM.Lyrics.Core/Engine/SourceSearcherContracts.cs` | `SourceLyricsResult` 加 `string? TranslationText`；更新其 XML 注释说明各源承载 |
  | `src/YTM.Lyrics.Core/Engine/EngineContracts.cs` | `ILyricsDocumentParser.Parse` 加 `string? translationText = null` |
  | `src/YTM.Lyrics.Core/Cache/ILyricsCache.cs` | `CachedLyrics` 加 `string? TranslationText` |
  | `src/YTM.Lyrics.Core/Cache/LiteDbLyricsCache.cs` | `LyricsDoc` 加字段、`ToDoc`/`ToEntry` 透传 |

  ### Part B：三源适配器取翻译

  | 文件 | 改动 |
  |---|---|
  | `QqMusicSourceSearcher.cs` | `GetLyricsCoreAsync` 同时取 `response.Trans`，非空白则填入 `TranslationText`。**先真实取证 Trans 格式**（沙盒网络已开，直连真实源取一首有翻译的歌，看 Trans 是 LRC 还是纯文本），在代码注释 + 上报决策点里写明取证结论 |
  | `NeteaseSourceSearcher.cs` | `GetLyricsCoreAsync` 取 `result.Tlyric?.Lyric` 填入 `TranslationText`（LRC 格式） |
  | `MusixmatchSourceSearcher.cs` | `GetLyricsCoreAsync` 在取原文后追加 `_api.GetTranslations(sourceId, "zh")`，提取 `TranslationsList[].Translation` 的「原文行 + 译文」序列化为 JSON 填入 `TranslationText`。**先真实取证确认译文字段**（Description 还是 Snippet，MatchedLine 匹配的是 subtitle 行还是 richsync 行），写清取证结论 |

  ### Part C：Parsing 层翻译合并 + credits 过滤

  | 文件 | 改动 |
  |---|---|
  | `External/Parsing/TranslationMerger.cs`（新增） | 实现上述按 source 分派的合并逻辑；LRC 翻译解析用 Helper 的 LrcParser，MatchedLine 对齐用 System.Text.Json |
  | `External/Sources/LyricsDocumentParserBridge.cs` | `Parse` 接 `translationText` 参数，主歌词解析后调 `TranslationMerger.Merge` |
  | `External/Parsing/LrcLyricsConverter.cs` | 加 credits 元数据行过滤（见裁决） |
  | `src/YTM.Lyrics.Core/Engine/LyricsEngine.cs` | 三处 Parse 调用点（缓存命中、TryFetchAndReturnAsync、BindLyricsAsync）补传 `translationText`；缓存写 `StoreAsync`/`BindUserChoiceAsync` 时把 `lyrics.TranslationText` 写进 `CachedLyrics` |

  ### Part D：单测（新增 + 不破坏基线）

  | 测试 | 覆盖 |
  |---|---|
  | `TranslationMergerTests.cs` | ① LRC 翻译按时间戳对齐（命中/错位丢弃）② MatchedLine 对齐（命中/无匹配）③ 翻译为空/null 不合并 ④ 翻译行多于/少于主词行不越界 |
  | `LrcLyricsConverterTests.cs`（追加） | credits 行（作词/作曲/编曲，带冒号/不带冒号/半角全角）过滤；过滤后全空返回空文档 |
  | 各源适配器 mock 测试 | QQ 取 Trans、网易云取 Tlyric、Musixmatch 序列化翻译，`TranslationText` 正确填入 |
  | `LiteDbLyricsCacheTests` | `CachedLyrics.TranslationText` 存储往返（存/读一致，旧文档读为 null 不炸） |

- **验收标准**：
  1. `dotnet build` 全绿（0 错误）；`dotnet test` 全绿，基线 201 不倒退（新增测试后总数增加）
  2. 真机/直连取一首「有翻译」的歌（QQ 或网易云或 Musixmatch），日志/断点可见 `LyricLine.Translation` 非空，主窗口翻译行正常显示
  3. 网易云无版权歌（无正文、只有 credits 的歌）不再把 credits 行当歌词，歌词面板显示「暂无歌词」而非 credits
  4. 缓存命中后翻译仍恢复（TranslationText 随缓存往返）

- **边界**：
  - **不改** `LyricLine`/`LyricDocument`/`Timeline` 的现有字段语义（只填 `Translation`，这是既有字段）
  - **不改** `LyricsEngine.GetLyricsAsync` 主流程（缓存→负缓存→逐源搜索→打分→fallback 顺序不动）
  - **不改**酷狗 KRC 翻译链路、Apple Music TTML、汽水源（汽水已移出）
  - **不碰** Desktop 层（桌面歌词翻译渲染是 TASK-505，本卡只保证 Core 数据到位）
  - 防腐层四坑仍遵守：翻译解析显式传类型、异常转带源标识结果、只调 Helper Async 版本
  - 翻译合并失败（如 Tlyric 解析异常）时**静默降级为主歌词无翻译**，不因翻译失败让整首歌词失败

- **上报**：全部 Part 完成后，产出「压缩摘要 + 两个取证结论（QQ Trans 格式 / Musixmatch 译文字段）+ 自测证据（dotnet test 通过记录 + 至少一首真实歌翻译显示的证据）」。
