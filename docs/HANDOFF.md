# HANDOFF：会话交接包

> 本文件只保留「当前真相 + 开口事项」。完成超过 7 天的内容移入 docs/history/（规则见 WORKFLOW §9）。
> 最后更新：2026-08-16（TASK-601 进 v1.0.0；ADR-13/F11 落地 TASK-602/603，已真机确认）

## 30 秒速览

YTM.Lyrics：YouTube Music 桌面歌词应用。C# .NET 8 + WPF 桌面端 + 油猴探针，歌词供应链复用 Lyricify.Lyrics.Helper（源码固化 external/，commit c14bba9）。

**现状**：M1–M4 全部完成，用户真机重度实测中/日/英覆盖率约 90%。dist/YTM.Lyrics.exe 为 framework-dependent 单文件（ADR-12），含 M1–M4 全部改动 + 真机反馈 ⑩⑪；探针 @0.1.1（含⑨，需 Tampermonkey 侧更新）。单测基线 289 全绿。

**方向（2026-08-16 裁决）**：进入日用打磨期。打 v1.0.0 tag → 按 BUGS.md 建议顺序烧零 backlog；F11 歌词偏移与 LyricsSession 重构（TDD ADR-13）绑定实施。不做移动端，不做 UI 大改；公开发布（GitHub 公网推广）是独立决策，未拍板。

## 开口事项

### 活 backlog（BUGS.md，建议处理顺序）

1. ~~「」括号过滤~~ ✅ 已修+已真机确认（TASK-601，2026-08-16：角括弧抢救规则，267 单测全绿），随 v1.0.0 发布进 dist
2. ~~F11 歌词偏移 ±5s（连同 ADR-13 LyricsSession 下沉 Core）~~ ✅ 已落地+已真机确认（TASK-602 ADR-13 + TASK-603 F11，289 单测全绿），随发布进 dist
3. 绑定失败原因透明化（Apple Music 根因已诊断：2026-08 起歌词 API 需 privileged token / media-user-token，TASK-604 落地中：移除默认源 + 失败原因逐源透传）
4. UX 零碎：歌名可复制 / 清缓存残留输入 / 清缓存等待过长
5. 本地数据/离线听歌：待用户拍板是否立项（YTM 本身离线能力存疑）

### 技术债与候选

- ⑧ 特摄/翻唱曲绑定全失败：罗马音→日文搜索映射（P2）+ 主流中文源覆盖不足 + 手动搜索只有标题维无法区分同名曲
- 网易云「纯音乐，请欣赏」占位行过滤（无歌词判定语义，与 credits 过滤分开处理）
- P1：F10 逐字卡拉 OK（数据层已保留 Syllables）；P2：播放控制反向注入（协议已预留）、机翻补全；备选：SMTC 探针

### 真机走查（M4 验收门，进行中）

- **连续使用一周无重启**（2026-08-12 起算，用户侧观察）
- 13 项走查清单见 docs/history/2026-08.md「M4 完成后真机走查清单」
- 待确认观感项：托盘白色图标在浅色任务栏偏淡；桌面歌词默认 600x120 下 4 行字号偏小；悬停磨砂 #80000000 深浅/阴影浓淡

## 新会话启动

AGENTS.md（仓库根，自动加载）已含铁律与文档路由 → 再读本文件 + BUGS.md + 当日任务卡。深文档（TDD/PRD/UI_SPEC/research）按需，勿全读。
冷启动预算：HANDOFF + BUGS ≈ 6KB 以内。

## 环境坑位手册（活坑）

**任何环境都生效**：

- 用户名含单引号 `wei'son` → 只许 framework-dependent 单文件发布（ADR-12），禁止 self-contained
- PowerShell 5.1 把无 BOM UTF-8 脚本当 GBK 读 → 编排脚本用 Python 或脚本带 BOM；控制台读中文先设 `[Console]::OutputEncoding = [System.Text.Encoding]::UTF8`
- nuget.config（不提交）指向工作区相对路径 `.nuget/local-feed`；正常环境删除即恢复 nuget.org
- 提权规则见 WORKFLOW §8；沙盒用完提醒用户开回（戈塔什大人 2026-08-12 约定，全文见 docs/history/2026-08.md）

**沙箱 ON 才生效**（当前 OFF，2026-08-12 起）：构建加参 / DOTNET_CLI_HOME / Schannel 断网 / headless 起不来 / 进程随会话回收 / APPDATA 重定向 / gh 凭据读取。完整清单与解法见 docs/history/2026-08.md「沙箱期坑位清单」。

## 用户环境档案

Chrome 151 + Tampermonkey 5.5.0（Profile 1/3，无 Default）；代理 127.0.0.1:7890（Clash 系）；.NET SDK 8.0.424 + Desktop Runtime 8.0.30；Lyricify 4 已装但从未跑通过（无 Spotify 凭据，对标靠用户截图）。

## 关键情报（勿再调研）

- QQ 音乐 musicu.fcg 搜索已失效（2026-08），适配器已切老接口 `c.y.qq.com/soso/fcgi-bin/client_search_cp`（见 QqMusicSourceSearcher 注释）
- Helper 四坑已在防腐层吸收（TDD §3.6）；Helper KRC 解析保留行级时间，音节为绝对毫秒；LiteDB DateTime 一律 UTC ticks 存储
- Lyricify 4 滚动对标结论：当前行居中 50%、行切换硬切无动画（docs/research/lyricify4-bench.md）
- 主窗口滚动 bug 真根因：行高未计入 Margin(0,8,0,8)（每行 16px 累积），非 UpdateLayout 层级
- YTM mediaSession 对日语歌标题给罗马音 → 探针 0.1.1 已做 CJK 原文降级（artist 仍取 mediaSession）
- 真实 KRC/QRC/QQ 搜索响应样本已存档 tools/ 与 Tests
