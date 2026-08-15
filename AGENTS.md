# AGENTS.md — YTM.Lyrics 会话引导（自动加载，保持 ≤2KB）

YouTube Music 桌面歌词：油猴探针（probe/）→ WebSocket 127.0.0.1:17865 → Core 状态机 → 标题清洗 → 五源搜索引擎（QQ/酷狗/Apple/网易云/Musixmatch）→ LiteDB 缓存 → WPF 渲染。C# / .NET 8，单测基线 260。

## 铁律（违反即返工）

1. 依赖单向：Desktop → Core → Helper；Helper 只许经 `Core/External/` 防腐层访问
2. 探针红线：不依赖任何 YTM CSS 类名/DOM 层级，只认 HTML5 媒体元素 + MediaSession
3. Desktop 只做窗口/渲染，业务编排在 Core（现状偏差与整改见 TDD ADR-13）
4. 发布只许 framework-dependent 单文件（ADR-12：本机用户名含单引号，self-contained 会崩）
5. 构建测试：`dotnet build` / `dotnet test`（沙箱开启时的加参姿势见 docs/history/2026-08.md「沙箱期坑位清单」）

## 先读什么（按需，勿全读）

- 接手会话：`docs/HANDOFF.md`（当前真相 + 开口事项）+ `BUGS.md`（活 backlog）+ 当日任务卡
- 改架构/契约前：`docs/TDD.md`（13 条 ADR + 模块契约）
- 改 UI 前：`docs/UI_SPEC.md`
- 查需求边界：`docs/PRD.md`（v1.0 冻结）
- 协作规则（任务卡/调试预算/提权十律/文档卫生）：`docs/WORKFLOW.md`
- Helper API 速查与调研报告：`docs/research/`（按需单篇读）
- 已验收任务卡：`docs/tasks/done/`；历史增量日志：`docs/history/`

## 当前方向（2026-08-16）

M1–M4 完成，v1.0.0 待发。下一阶段（详见 HANDOFF「开口事项」）：BUGS.md 烧零——「」括号过滤 → F11 歌词偏移（连同 ADR-13 LyricsSession 重构）→ 绑定失败原因透明化 → UX 零碎。不做移动端，不做 UI 大改。
