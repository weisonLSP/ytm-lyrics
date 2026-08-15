# TASK-601：清洗器「」角括弧歌名抢救

- 目标：日文 MV 标题 `艺人「歌名」` / `「歌名」/ 艺人` 格式中，提取「」内真歌名，BUGS.md 案例清洗正确，20 条样本回归不降级
- 输入：BUGS.md 第 1 条；`src/YTM.Lyrics.Core/Cleaning/TitleCleaner.cs`；`src/YTM.Lyrics.Core/Cleaning/Rules/cleaning-rules.json`；`src/YTM.Lyrics.Core.Tests/Cleaning/CleaningTests.cs`
- 产出：TitleCleaner 抢救逻辑 + 新增单测（含 BUGS 案例）；`dotnet test` 全绿（基线 260 + 新增）
- 边界：不改 CleanedTrack 契约（Engine/CacheKey 无感）；不改规则表 JSON 结构；『』（双角括弧=作品名标注）不纳入抢救；不顺手改其他清洗行为
- 上报：完成后自压缩摘要（日常小任务，WORKFLOW §2 降级通道）

## 背景与根因（架构师诊断，2026-08-16）

BUGS 案例：`いきものがかり「さよならララ」 (オリジナルTVアニメ『さよならララ』OPテーマ) Music Video`

- 「」（U+300C/300D）不在 brackets 剥离表（() 【】 [] （）｛｝），也不在装饰符区间 → 剥括号阶段原样保留
- (...) 注释（含 『』）被整体剥离 ✓，"Music Video" 后缀被剥 ✓
- 最终 title = `いきものがかり「さよならララ」`（艺人+歌名+角括弧粘连），五源搜索无法匹配 → 表现为「搜不到/绑定错」

## 验收标准

1. BUGS 案例：Clean → Title=`さよならララ`，ArtistCandidates 首位 `いきものがかり`
2. 既有 20 条样本 + 全部规则类型测试不回归（它们不含 「」，预期零改动通过）
3. 新增覆盖：「」在串首、搬运频道（前缀≠频道名）、无艺人元数据、「」内容为噪声词不抢救、既有含 - 样本不回归
