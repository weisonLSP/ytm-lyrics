# TASK-505：桌面歌词窗口翻译副行渲染（依赖 TASK-504）

- **目标**：桌面歌词窗口在渲染主词行（`LyricLine.Text`）的同时，渲染翻译副行（`LyricLine.Translation`），样式对齐主窗口的翻译行（小字、暗色、主词下方）。受 `show_translation` 设置项控制。

- **前置依赖**：TASK-504 完成且验收通过（`LyricLine.Translation` 已有真实数据）。若 504 未完成，本卡无法验收。

- **输入**：必读
  - `docs/HANDOFF.md`（反馈 ③）
  - `docs/UI_SPEC.md` §3.4（桌面歌词渲染）、§2.1（翻译行样式）
  - `src/YTM.Lyrics.Desktop/DesktopLyricsWindow.xaml`（当前 Canvas + 2 TextBlock）
  - `src/YTM.Lyrics.Desktop/DesktopLyricsWindow.xaml.cs`（`SetCurrentLine`/`SetTextsInstant`/`LayoutLines`/`ApplySettings`）
  - `src/YTM.Lyrics.Desktop/MainWindow.xaml.cs`（翻译行渲染参考：`TranslationFontSizeFor`、`show_translation` 判断，~433 行）

- **产出**：

  ### Part A：XAML 加翻译行元素

  | 文件 | 改动 |
  |---|---|
  | `DesktopLyricsWindow.xaml` | 在 `LyricsCanvas` 内、现有 `CurrentLineText`/`NextLineText` 之外，新增两个翻译行 TextBlock：`CurrentTranslationText`、`NextTranslationText`（命名自定但语义对应），初始 `Visibility=Collapsed`，`TextAlignment=Center`，`TextWrapping=Wrap` |

  ### Part B：渲染逻辑

  | 文件 | 改动 |
  |---|---|
  | `DesktopLyricsWindow.xaml.cs` | ① `SetCurrentLine`：取 `doc.Lines[idx].Translation` 与下一行 `doc.Lines[idx+1].Translation`，连同主词文本一起传给 `SetTextsInstant`；② `SetTextsInstant`：填充翻译文本，空则 `Collapsed`，非空则 `Visible` + 应用翻译样式（字号 = `BaseFontSize * desktop_font_scale * 0.625`，至少 12；颜色用暗色 `#8A8A98`，`FontWeight=Normal`）；③ `LayoutLines`：主词行下方紧凑排翻译行（主词 bottom + 小间距），整块垂直居中时把「主词+翻译」作为一组计算高度；④ `show_translation=false` 时翻译 TextBlock 一律 `Collapsed`（不渲染）；⑤ `ApplySettings` 里 `show_translation` 变更即时生效（有歌词时强制重渲染，参考现有 `_currentLine = -1` 触发法）；⑥ `ShowPlaceholder`/`ClearLyrics` 里清空两个翻译 TextBlock |

  ### Part C：取证

  用 `--desktop-shot`（已有机制，见 `OnLoaded` 的 `_desktopShots`）截一张带翻译副行的桌面歌词窗口 PNG 作为自测证据。

- **验收标准**：
  1. `dotnet build` 全绿
  2. 一首「有翻译」的歌（QQ/网易云/Musixmatch 任一，翻译来自 TASK-504）在桌面歌词窗口：当前行下方显示翻译副行，字号明显小于主词，颜色暗于主词
  3. `show_translation` 关掉后翻译副行消失（即时生效）
  4. 无翻译的歌不出现空白占位（翻译 TextBlock 保持 Collapsed，不影响布局）
  5. 单行/双行模式切换时翻译行跟随主词行正确显示/隐藏

- **边界**：
  - **只动** `DesktopLyricsWindow.xaml` + `.xaml.cs`，不碰主窗口、不碰 Core、不碰设置页
  - 翻译副行只渲染「当前行翻译」（双行模式下可加「下一行翻译」，但不得破坏现有单行/双行布局语义）
  - 不实现翻译行动画、不做任何渐变/美化，v1 硬切与主词行一致
  - 不新增设置项（`show_translation` 已存在，复用即可）

- **上报**：产出压缩摘要 + 带翻译副行的桌面歌词截图（`--desktop-shot` 取证）+ `dotnet build`/`dotnet test` 通过记录。
