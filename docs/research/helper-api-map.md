# Lyricify-Lyrics-Helper API 速查表

> TASK-001 产出 · 2026-08-12 · 调研对象为 `external/Lyricify-Lyrics-Helper`（clone 于 2026-08-12）
> 本表所有类名/签名均来自仓库真实代码，可追溯；未能完全确认处标注 [待验证]
> 路径均相对仓库根 `external/Lyricify-Lyrics-Helper/`，代码根 `Lyricify.Lyrics.Helper/`（命名空间 `Lyricify.Lyrics`）

## 0. 仓库与版本事实

| 项 | 值 |
|---|---|
| 仓库 | https://github.com/WXRIW/Lyricify-Lyrics-Helper |
| HEAD | `c14bba9` (master) "[Add] Apple native duet view alignment" |
| Tags | v0.1.3 / v0.1.4 / v0.2.0 |
| master 领先 v0.2.0 | **3 个提交**：QRC 解密修复（983709b）、Apple TTML 中文转换修复（86119d8）、Apple duet alignment（c14bba9） |
| NuGet 官方包 | **存在**：`Lyricify.Lyrics.Helper`，最新 **0.2.0**，作者 WXRIW / XY Wang，Apache-2.0，下载 ~1170，无已知漏洞（nuget.org API 2026-08-12 查询） |
| 同名第三方包 | `Lyricify.Lyrics.Helper.Jayfunc`（fork，0.1.4，勿用） |
| 许可证 | Apache-2.0（csproj `PackageLicenseExpression`） |

## 1. 目标框架

- **netstandard2.1**（`Lyricify.Lyrics.Helper.csproj`），LangVersion 10.0，Nullable enable，ImplicitUsings enable
- **net8.0-windows 可直接引用**：net8.0 实现 netstandard2.1，无需兼容层
- 依赖（全部托管 NuGet 包）：
  - `Newtonsoft.Json` 13.0.3
  - `SharpZipLib` 1.4.2（用于 QRC/KRC 解压、网易云 eapi 响应等）
  - `CHTCHSConv` 1.0.0（简繁转换，Apple/TTML 中文歌词用）
- 无任何 native / P/Invoke 依赖（`AllowUnsafeBlocks=true` 但代码未用 unsafe 做外部调用）

## 2. 搜索（六源 + 备用源）

### 2.1 统一入口与类型

| API | 签名 | 位置 |
|---|---|---|
| 静态搜索入口 | `SearchHelper.Search(ITrackMetadata, Searchers.Searchers) → Task<ISearchResult?>` | `Helpers/SearchHelper.cs` |
| 同上（带阈值） | `SearchHelper.Search(ITrackMetadata, ISearcher, CompareHelper.MatchType minimumMatch) → Task<ISearchResult?>` | 同上 |
| 单例取源 | `SearcherHelper.QQMusicSearcher` 等 8 个静态属性（懒加载单例） | `Searchers/SearcherHelper.cs` |
| 搜索接口 | `ISearcher`：`SearchForResult(ITrackMetadata)` / `SearchForResult(ITrackMetadata, MatchType)` / `SearchForResults(ITrackMetadata)` / `SearchForResults(ITrackMetadata, bool fullSearch)` / `SearchForResults(string)` | `Searchers/ISearcher.cs` |
| 结果模型 | `ISearchResult`：`Searcher` / `Title` / `Artists: string[]` / `Artist` / `Album` / `AlbumArtists` / `DurationMs: int?` / `MatchType: CompareHelper.MatchType?` | `Searchers/ISearchResult.cs` |

- `Searchers.Searchers` 枚举成员：QQMusic / Netease / Kugou / Musixmatch / SodaMusic / AppleMusic / Spotify / LRCLIB（另有 `Searchers.cs` 与 `SearchersHelper.cs` 两个文件，前者为枚举 [待验证：具体定义位置]）
- `Searcher`（抽象基类）自动完成：多级关键词降级搜索（标题+艺人+专辑 → 标题+艺人 → 仅标题）、`CompareHelper.CompareTrack` 打分、按 MatchType 排序、`minimumMatch` 过滤。**调用方只需给 `TrackMetadata` / `TrackMultiArtistMetadata`**
- 元数据输入：`TrackMetadata`（Title/Artist/Album/AlbumArtist/DurationMs/Isrc/Language）或 `TrackMultiArtistMetadata`（Artists/AlbumArtists 列表版，`Models/TrackMetadata.cs`）

### 2.2 六源逐项

| 源 | Searcher 类 | 底层搜索调用 | 歌词获取 | 鉴权 / Cookie |
|---|---|---|---|---|
| QQ 音乐 | `QQMusicSearcher`（`Searchers/QQMusicSearcher.cs`） | `QQMusic.Api.Search(string, SearchTypeEnum.SONG_ID)` → `MusicFcgReq1`（`Providers/Web/QQMusic/Api.cs`） | `QQMusic.Api.GetLyricsAsync(string id) → Task<QqLyricsResponse?>`（lyric_download.fcg，`version=15`）；`QqLyricsResponse` 含 `Lyric` / `Trans` 字段 + `Decode()`（Base64 解码）（`Providers/Web/QQMusic/Response.cs`）；另有 `Decrypter/Qrc/Helper.cs` 提供 `GetLyricsByMidAsync(mid)` / `GetLyricsAsync(id)` | 无登录态，无需自带 Cookie |
| 网易云 | `NeteaseSearcher`（`Searchers/NeteaseSearcher.cs`） | `Netease.Api.Search / SearchNew`（双接口自动切换；Code==-460 时切换，`-460` 即限流） | `Netease.Api.GetLyric(string songId) / GetLyricNew(string songId) → Task<LyricResult?>`；`LyricResult` 字段：`Lrc/Klyric/Tlyric/Romalrc/Yrc/Ytlrc/Yromalrc`（各为 `Lyrics{Version, Lyric}`）、`Nolyric/Uncollected` 标志（`Providers/Web/Netease/Response.cs`） | 内置硬编码 Cookie（见坑 #4）；eapi 接口用 `EapiHelper`（AES-ECB + MD5 摘要，纯托管，`Providers/Web/Netease/EapiHelper.cs`）；无登录态，高频易限流 |
| 酷狗 | `KugouSearcher`（`Searchers/KugouSearcher.cs`） | `Kugou.Api.GetSearchSong(string keywords) → SearchSongResponse?`；另有 `GetSearchLyrics(string? keywords=null, int? duration=null, string? hash=null)`（`Providers/Web/Kugou/Api.cs`） | `Decrypter/Krc/Helper.cs`：`GetLyrics(string id, string accessKey) → string?`（同步用 `.Result`！）/ `GetLyricsAsync(string id, string accessKey)` → 解密后的 KRC 纯文本 | 需 `accessKey`（来自搜索响应），无需登录 Cookie |
| 汽水音乐 | `SodaMusicSearcher`（`Searchers/SodaMusicSearcher.cs`） | `SodaMusic.Api.Search(string searchString)`（`Providers/Web/SodaMusic/Api.cs`） | 歌词获取 API [待验证]（未在本仓库见到 GetLyrics 公开方法） | 无需 Cookie [待验证：接口稳定性] |
| Apple Music | `AppleMusicSearcher`（`Searchers/AppleMusicSearcher.cs`） | `AppleMusic.Api.Search(string keyword, int limit = 20)` | `AppleMusic.Api.GetLyrics(string songId) → Task<LyricResponse?>`（内部归一化到 TTML，`Providers/Web/AppleMusic/Api.cs`） | **自动抓取 access token**（从 music.apple.com 前端 JS 解析 JWT，`RefreshAccessTokenAsync` → `FindIndexScriptUrls`）；可选注入 `SetMediaUserToken(string)` / `SetAccessToken(string?)` 提升稳定性 |
| Musixmatch | `MusixmatchSearcher`（`Searchers/MusixmatchSearcher.cs`） | 内部走 `Musixmatch.Api.GetTrack(track, artist, duration?)` 链 | `Musixmatch.Api.GetFullLyricsRaw(string trackId) → Task<string?>`（原始 JSON，喂 `MusixmatchParser`）；另有 `GetFullLyrics(track, artist, duration?)`、`GetTranslations(string trackId, string language="zh")`（`Providers/Web/MusixMatch/Api.cs`） | 自动 `GetToken()`（`GetUserToken()` 可读取已有 token）；依赖 Musixmatch 网页端 token 机制 |

备用源（非任务六源，仓库内存在）：`LRCLIBSearcher`（LRC Lib，API 开放、无需鉴权）、`SpotifySearcher`（README 标注"暂不支持"，需 Spotify 客户端 token）。

### 2.3 典型调用（源自 `Lyricify.Lyrics.Demo/Program.cs` SearchDemo）

```csharp
var search = SearchHelper.Search(new TrackMultiArtistMetadata
{
    Album = "RUNAWAY",
    AlbumArtists = new() { "OneRepublic" },
    Artists = new() { "OneRepublic" },
    DurationMs = 143264,
    Title = "RUNAWAY",
}, Searchers.Searchers.Netease, Searchers.Helpers.CompareHelper.MatchType.Medium).Result;
```

## 3. 解密

| 格式 | 入口 | 算法 | 依赖 | 纯托管？ |
|---|---|---|---|---|
| QRC | `Decrypter.Qrc.Decrypter.DecryptLyrics(string encryptedLyrics) → string?`（入参为十六进制字符串）（`Decrypter/Qrc/Decrypter.cs`） | 自实现 **TripleDES**（`Decrypter/Qrc/DESHelper.cs` 手写密钥编排，key = ASCII `!@#)(*$%123ZXC!@!@#)(NHL`）+ SharpZipLib Inflater 解压 + 去 BOM | SharpZipLib | ✅ 纯托管 |
| KRC | `Decrypter.Krc.Decrypter.DecryptLyrics(string encryptedLyrics) → string?`（入参 Base64，内部跳过前 4 字节头）（`Decrypter/Krc/Decrypter.cs`） | XOR（16 字节固定 key `0x40 0x47 ... 0x69`）+ SharpZipLib 解压 + 去首字符 | SharpZipLib | ✅ 纯托管 |

- 包装层：`Decrypter/Qrc/Helper.cs`（`GetLyricsByMid`/`GetLyricsByMidAsync`/`GetLyrics`/`GetLyricsAsync`，同步版本用 `.Result` 阻塞）、`Decrypter/Krc/Helper.cs`（`GetLyrics(id, accessKey)` 同步 / `GetLyricsAsync`）
- 注意：`DecryptLyrics` 返回**解密后纯文本**，仍需 `QrcParser`/`KrcParser` 解析成模型

## 4. 解析

### 4.1 入口

```csharp
// 推荐：显式指定类型
ParseHelper.ParseLyrics(string lyrics, LyricsRawTypes lyricsRawType) → LyricsData?
// 不推荐：单参版本依赖 TypeHelper.GetLyricsTypes —— 当前为占位实现恒返回 Unknown（见坑 #1），实际永远返回 null
```

各 Parser 均为 `public static class`，`Parse(string) → LyricsData`（除非标注）：

| 格式 | Parser | 签名 |
|---|---|---|
| LRC | `LrcParser` | `Parse(ReadOnlySpan<char> input) → LyricsData` |
| QRC（已解密） | `QrcParser` | `Parse(string lyrics)`；另有 `ParseLyrics(List<string> lines, int? offset)` / `ParseLyricsLine(string)` |
| YRC | `YrcParser` | `Parse(string input)`；另有 `ParseOnlyLyrics(ReadOnlySpan<char>)` |
| KRC（已解密） | `KrcParser` | `Parse(string krc)`；`ParseLyrics(string krc) → List<ILineInfo>`；`KrcTranslationParser`（翻译解析） |
| TTML | `TtmlParser` | `Parse(string ttml, bool useEmbeddedSimplifiedChineseLyrics = true) → LyricsData` |
| Spotify JSON | `SpotifyParser` | `Parse(string rawJson) → LyricsData?` |
| Musixmatch JSON | `MusixmatchParser` | `Parse(string rawJson, bool ignoreSyllable) → LyricsData?` |

全部位于 `Parsers/` 目录。另有类型检测 `TypeHelper.GetLyricsTypes(string) → LyricsRawTypes`（占位，见坑 #1）、`TypeHelper.IsLyricsType(string, LyricsTypes)`（仅支持 LyricifyLines / Lrc）。

### 4.2 统一歌词模型（`Models/`）

```
LyricsData
├─ File: FileInfo?              // Type(LyricsTypes), SyncTypes, AdditionalInfo(Attributes: [ar:]/[ti:]等)
├─ Lines: List<ILineInfo>?      // ← 核心
├─ Writers: List<string>?
└─ TrackMetadata: ITrackMetadata?
```

- **行接口** `ILineInfo`：`Text` / `StartTime: int?` / `EndTime: int?` / `Duration` / `StartTimeWithSubLine` / `FullText` / `LyricsAlignment` / `SubLine: ILineInfo?`（对唱副行）
- **行实现层次**：
  - `LineInfo`（可变普通行）
  - `SyllableLineInfo`（逐字行）：`Syllables: List<ISyllableInfo>`，`Text/StartTime/EndTime` 由音节惰性聚合；`IsSyllable` 判断
  - `FullLineInfo : LineInfo, IFullLineInfo`（带翻译/发音）
  - `FullSyllableLineInfo : SyllableLineInfo, IFullLineInfo`（逐字 + 翻译/发音）
- **逐字时间轴**：`ISyllableInfo { Text, StartTime: int, EndTime: int }`（int 毫秒），实现 `SyllableInfo` / `FullSyllableInfo`（后者可嵌套 `SubItems`）
- **时间单位**：全部为 **int 毫秒**
- **⚠ 逐字时间语义因源而异**（关键，见坑 #6）：
  - KRC：`[start,ms]` 标签为**绝对毫秒**，行级与音节级一致
  - QRC：`文字(start,duration)` 中 start 为**相对行首偏移**；且 `QrcParser` **不解析行级 `[mm:ss.xx]` 主标签**（`ParseLyricsLine` 将其剥掉），产出行的 `StartTime` = 首音节相对值 → 行级时间轴需自行处理
  - YRC：word 时间由 `timeSpanBuilder` 从行起始累加，同样为相对行首（`Parsers/YrcParser.cs`）
  - TTML：`begin/end` 为绝对时间

## 5. 翻译行 / 罗马音承载

- **接口** `IFullLineInfo`（`Models/ILineInfo.cs`）：
  - `Translations: Dictionary<string, string>`（key 约定：`"zh"` = 中文翻译，`ChineseTranslation` 便捷属性读写 `"zh"`）
  - `Pronunciation: string?`（罗马音/注音）
- **各源承载情况**：
  - KRC：`KrcParser` 检测到翻译段后把行替换为 `FullSyllableLineInfo(syllableLine, chineseTranslation: t)` → `Translations["zh"]` ✅ 原生承载
  - QRC：解析器**未见 Translations 处理**，翻译行按独立时间行解析为普通行 [待验证：Lyricify 主程序对 QRC 翻译的处理方式]
  - 网易云：`LyricResult.Tlyric`（翻译）/ `Romalrc`（罗马音）/ `Ytlrc`（逐字翻译）为**独立字段**，需自行与 `Lrc`/`Yrc` 行合并，库内无合并封装 [待验证]
  - Apple TTML：中文歌词经 `useEmbeddedSimplifiedChineseLyrics` 参数选择（简/繁），逐字由 TTML 音节承载，翻译承载方式 [待验证]
  - Musixmatch：`GetTranslations(trackId, "zh")` 独立接口返回，解析器对翻译的处理 [待验证]

## 6. 匹配（CompareHelper 等）

| API | 签名 | 位置 |
|---|---|---|
| 打分主入口 | `CompareHelper.CompareTrack(ITrackMetadata track, ISearchResult searchResult) → MatchType` | `Searchers/Helpers/CompareHelper.cs` |
| 多艺人重载 | `CompareHelper.CompareTrack(TrackMultiArtistMetadata, ISearchResult) → MatchType` | 同上 |
| 子评分 | `CompareName` / `CompareArtist` / `CompareDuration`（partial class，分文件 `Searchers/Helpers/MatchHelpers/NameMatch.cs`、`ArtistMatch.cs`、`DurationMatch.cs`，含 `GetMatchScore()` 扩展） | 同上目录 |
| 排序比较器 | `MatchTypeComparer : IComparer<CompareHelper.MatchType>` | 同上 |

- `MatchType` 枚举：`Perfect=100, VeryHigh=99, High=95, PrettyHigh=90, Medium=70, Low=30, VeryLow=10, NoMatch=-1`
- 计分逻辑：标题 1.0 + 艺人 1.0 + 专辑 0.4 + 专辑艺人 0.2 + 时长 1.0（缺失项按比例拉伸），>21 Perfect / >19 VeryHigh / >17 High / >15 PrettyHigh / >11 Medium / >8 Low / >3 VeryLow / 其余 NoMatch
- `Searcher.SearchForResult(track, minimumMatch)` 会自动拒绝低于阈值的首个结果 → **`MatchType.Medium` 是自用场景的合理门槛**（Demo 即用 Medium）
- 另有 `Searchers/Helpers/ArtistHelper.cs`（中英文艺人名对照，提升匹配率）

## 7. 已知坑（按风险排序）

1. **`TypeHelper.GetLyricsTypes` 是占位实现**（`Helpers/TypeHelper.cs`：`return LyricsRawTypes.Unknown;` 恒不识别）→ `ParseHelper.ParseLyrics(string)` 单参版本永远返回 null；**必须显式传 `LyricsRawTypes`**。各 Parser 类型已知时直接调 Parser 亦可。
2. **QRC 解析丢弃行级时间标签**：`QrcParser.ParseLyricsLine` 剥离 `[mm:ss.xx]` 主标签，只留音节相对时间 → 逐字/逐行渲染需要另行解析 QRC 行标签或放弃 QRC 绝对时间轴（QQ 音乐源的关键风险）。
3. **master 领先 NuGet 0.2.0 三个提交，含 QRC 解密修复**（`983709b [Fix] QRC decryption issue in some cases`）→ 若用 NuGet 0.2.0，QRC 解密有已知 bug；建议源码引用 master 或等待新包。
4. **全局共享静态 HttpClient**：`BaseApi.HttpClient` 为 static，`SetRequestHeaders()` 每次请求 `Clear()` 全部 DefaultRequestHeaders → **并发多源搜索会互相覆盖请求头**（UA/Cookie/Referer 串源）；对自用单请求场景影响小，但必须串行化或用独立引用。
5. **内置网易云硬编码 Cookie**（`BaseApi.Cookie = "os=pc;osver=...;channel=netease"`，模拟桌面客户端）+ eapi 逆向接口 → 无登录态、**高频限流（Code=-460）**，NeteaseSearcher 靠 Search/SearchNew 双接口兜底；eapi 依赖网易云逆向，随时可能失效。
6. **逐字时间语义不统一**（QRC/YRC 相对行首、KRC/TTML 绝对），`SyllableLineInfo.StartTime` 对不同源含义不同 → 逐字卡拉 OK 渲染前必须按源归一化时间轴。
7. **Searcher 静默吞异常**：各 `SearchForResults` 内 `try/catch` 后返回 `null` → 网络失败、接口变动**无任何日志/错误信息**，上层无法区分"无结果"与"源挂了"。
8. **`.Result` 同步阻塞**：`Decrypter/Qrc/Helper.cs`、`Decrypter/Krc/Helper.cs` 的同步方法用 `.Result` 阻塞等待 → 在 WPF UI 线程（有 SynchronizationContext）调用有死锁风险，应只用 Async 版本。
9. **Apple Music 自动 token 抓取依赖前端 JS**（解析 music.apple.com 脚本里的 JWT，含评分启发式）→ 随 Apple 前端改版易碎；`SetMediaUserToken` 注入可缓解，但增加了部署复杂度。
10. **SodaMusic / Musixmatch 接口无官方保证**：汽水音乐为字节内部生态接口（公开性/稳定性存疑），Musixmatch 依赖网页 token 机制 → 建议按"实验源"降级处理。

## 附录：关键文件索引（相对仓库根 `Lyricify.Lyrics.Helper/`）

| 关注点 | 文件 |
|---|---|
| 项目配置 | `Lyricify.Lyrics.Helper.csproj` |
| 解析入口 | `Helpers/ParseHelper.cs`、`Helpers/TypeHelper.cs` |
| 各解析器 | `Parsers/LrcParser.cs`、`Parsers/QrcParser.cs`、`Parsers/YrcParser.cs`、`Parsers/KrcParser.cs`、`Parsers/TtmlParser.cs`、`Parsers/SpotifyParser.cs`、`Parsers/MusixmatchParser.cs` |
| 模型 | `Models/LyricsData.cs`、`Models/LineInfo.cs`、`Models/SyllableInfo.cs`、`Models/ILineInfo.cs`、`Models/ISyllableInfo.cs`、`Models/TrackMetadata.cs`、`Models/LyricsTypes.cs`、`Models/SyncTypes.cs`、`Models/FileInfo.cs`、`Models/AdditionalFileInfo.cs` |
| 搜索 | `Searchers/ISearcher.cs`、`Searchers/Searcher.cs`、`Searchers/SearcherHelper.cs`、`Searchers/ISearchResult.cs`、`Helpers/SearchHelper.cs`、`Searchers/*Searcher.cs` |
| 匹配 | `Searchers/Helpers/CompareHelper.cs`、`Searchers/Helpers/MatchHelpers/*`、`Searchers/Helpers/ArtistHelper.cs` |
| 解密 | `Decrypter/Qrc/Decrypter.cs`、`Decrypter/Qrc/DESHelper.cs`、`Decrypter/Qrc/Helper.cs`、`Decrypter/Krc/Decrypter.cs`、`Decrypter/Krc/Helper.cs` |
| 网络层 | `Providers/Web/BaseApi.cs`、`Providers/Web/Providers.cs`、`Providers/Web/{QQMusic,Netease,Kugou,SodaMusic,AppleMusic,MusixMatch,LRCLIB,Spotify}/Api.cs`、`Providers/Web/Netease/EapiHelper.cs` |
| 用法示例 | `Lyricify.Lyrics.Demo/Program.cs`（ParsersDemo / SearchDemo / LRCLIBDemo） |
