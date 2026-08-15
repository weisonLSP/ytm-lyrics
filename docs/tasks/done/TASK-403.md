# TASK-403：稳定性排查修复 + 单文件发布

- **目标**：排查并修复 M4 稳定性问题（RuleHotReloadTests 偶发失败、drift 校正过频），验证断线重连与负缓存，最后发布 framework-dependent 单文件 exe 到 dist/。

- **输入**：必读文件清单
  - `docs/PRD.md` §4 S3/S4、§7 M4
  - `docs/HANDOFF.md`「已知问题与 Backlog」表（drift 校正过频、RuleHotReloadTests 偶发失败）
  - `src/YTM.Lyrics.Core.Tests/Cleaning/RuleHotReloadTests.cs`（偶发失败测试）
  - `src/YTM.Lyrics.Core/Playback/PlaybackStateMachine.cs`（drift 校正逻辑）
  - `src/YTM.Lyrics.Core/Playback/Providers/WebSocketProbeProvider.cs`（断线重连实现）
  - `src/YTM.Lyrics.Core/Cache/LiteDbLyricsCache.cs`（负缓存实现）
  - `src/YTM.Lyrics.Core.Tests/Playback/PlaybackStateMachineTests.cs`（现有 drift 测试）
  - `src/YTM.Lyrics.Core.Tests/Playback/WebSocketProbeProviderTests.cs`（现有断线重连测试）
  - `README.md`（构建/发布命令、状态表）

- **产出**：四部分

  ### Part A：RuleHotReloadTests 偶发失败修复

  | 文件 | 改动 |
  |---|---|
  | `src/YTM.Lyrics.Core.Tests/Cleaning/RuleHotReloadTests.cs` | 诊断偶发失败根因（FileSystemWatcher 300ms 防抖 + 100ms 轮询 + 10s 超时，慢机器/CI 下可能超时）。修复方向（择一或组合）：<br>① `WaitUntilAsync` 超时从 10s 提到 30s；<br>② 轮询间隔从 100ms 降到 50ms 加快探测；<br>③ 对热加载断言加「重试直到超时」而非单次断言。修复后连续跑 5 次 `dotnet test --filter RuleHotReloadTests` 全绿才算通过。 |

  ### Part B：drift 校正过频诊断

  | 文件 | 改动 |
  |---|---|
  | `src/YTM.Lyrics.Core/Playback/PlaybackStateMachine.cs` | 先诊断后修。分析「drift 校正过频」根因：稳态播放时探针 2s 心跳，外推值 = base.Position + (now − base.ArrivedAt)，新快照 position 与外推值偏差若因传输延迟抖动反复越过 0.8s 阈值，会频繁重置基准导致位置跳变。<br>**诊断产出**（写入 `docs/research/drift-analysis.md`）：<br>① 用 FakePlaybackProvider 注入带传输延迟抖动（ArrivedAt 随机 ±50ms）的快照序列，观测 drift 校正触发频率与位置跳变幅度；<br>② 判断「过频」是真实 bug（阈值太敏感）还是观测噪声（日志刷屏但位置无感）；<br>③ 若确需修，方案候选：漂移校正加「持续 N 帧超阈值才校正」的去抖（滞回），或阈值按传输延迟自适应。**只做诊断 + 单测，不改生产逻辑**（改阈值需真机校准，沙箱数据不足以支撑）。<br>补单测：注入抖动快照序列验证现有 drift 逻辑在 ±50ms 抖动下不会误触发校正（若误触发，记录证据，归入诊断报告，不修）。 |

  ### Part C：断线重连 + 负缓存验证（沙箱可测）

  | 文件 | 改动 |
  |---|---|
  | `src/YTM.Lyrics.Core.Tests/Playback/WebSocketProbeProviderTests.cs` | 补充/确认断线重连测试覆盖：探针断连 → ProviderLost 触发 → 重连 → 新连接 seq 从新序列开始（连接级 maxSeq）。已有测试则核对覆盖，缺则补 |
  | `src/YTM.Lyrics.Core.Tests/Cache/LiteDbLyricsCacheTests.cs` | 补充/确认负缓存测试覆盖：StoreNegativeAsync 后 IsNegativeCachedAsync 返回 true；24h 后（注入时钟）返回 false；绑定条目存在时恒 false。已有则核对，缺则补 |

  ### Part D：单文件发布

  | 文件 | 改动 |
  |---|---|
  | `dist/` | 重新 publish 最新代码（含 TASK-401/402/403 全部改动）为 framework-dependent 单文件。命令：`dotnet publish src/YTM.Lyrics.Desktop -c Release --self-contained false -p:PublishSingleFile=true -p:MSBuildEnableWorkloadResolver=false -maxcpucount:1`。产物覆盖 dist/YTM.Lyrics.exe（+pdb）。注意 PublishSingleFile 首次 publish 可能 MSB4018 瞬态失败，原样重跑即过 |
  | `README.md` | 更新「状态」表：M1/M2 ✅、M3 ✅、M4 ✅（M4 标注「沙箱自测通过，真机一周无重启待用户走查」）；单测基线数从 185 更新为实际数（TASK-401 后 196，TASK-403 可能增加） |

- **验收标准**：
  1. RuleHotReloadTests 连续 5 次全绿
  2. drift 诊断报告产出（`docs/research/drift-analysis.md`），含抖动快照模拟证据与「是否真 bug」的明确结论
  3. 断线重连 + 负缓存测试覆盖确认（缺口补齐）
  4. 全量 `dotnet test` 全绿（无偶发失败）
  5. dist/YTM.Lyrics.exe 为最新发布产物（含 TASK-402 设置页，启动后日志可见 settings 相关行）

- **边界**：
  - drift 校正**只诊断不修生产逻辑**（阈值改动需真机校准，超出沙箱能力）
  - 不新增功能，不重构无关代码
  - 不改协议（TDD §4）、不改引擎主流程
  - publish 产物只覆盖 dist/，不动其他目录
  - 构建注意事项同前：DOTNET_CLI_HOME + MSBuildEnableWorkloadResolver=false + maxcpucount:1

- **上报**：完成后将产出交给压缩器。自测证据：RuleHotReloadTests 5 连绿记录 + drift 诊断报告 + 全量测试绿 + dist 产物新时间戳 + 发布产物启动日志（含 settings 行）。
