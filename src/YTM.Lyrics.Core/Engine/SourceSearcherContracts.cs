using YTM.Lyrics.Core.Cleaning;

namespace YTM.Lyrics.Core.Engine;

/// <summary>
/// 歌词原文格式（防腐层坑 #1：Helper 的 TypeHelper.GetLyricsTypes 是占位实现，解析必须显式传类型）。
/// 源适配器知道自己取回的是什么格式，随原文一并声明；TASK-204 Parsing 层据此映射到
/// Helper 的 LyricsRawTypes / 各 Parser（QrcParser / LrcParser / KrcParser / TtmlParser / MusixmatchParser）。
/// </summary>
public enum LyricsRawFormat
{
    /// <summary>QQ 音乐 QRC（解密后文本；含 `文字(mm:ss.xx)` 音节式样）。</summary>
    Qrc,

    /// <summary>标准 LRC（QQ 解密失败时的降级文本 / 网易云 Lrc 字段）。</summary>
    Lrc,

    /// <summary>YRC 逐字歌词（网易云 Yrc 字段，word 时间为相对行首偏移）。</summary>
    Yrc,

    /// <summary>酷狗 KRC（解密后文本）。</summary>
    Krc,

    /// <summary>Apple Music TTML（Api.GetLyrics 归一化后的 Ttml 文本）。</summary>
    Ttml,

    /// <summary>Musixmatch 原始 JSON（GetFullLyricsRaw 返回，喂 MusixmatchParser）。</summary>
    MusixmatchJson,

    /// <summary>未知（失败结果占位）。</summary>
    Unknown,
}

/// <summary>
/// 单条搜索结果（两段式第一段）。字段固定：源内 ID、歌名、艺人、专辑、时长（秒）。
/// 评分由引擎完成（<see cref="ISourceResultScorer"/>），适配器不预判匹配度。
/// <paramref name="SourceId"/> 对引擎不透明（不解析含义），由对应适配器自行编码/解码
/// （如酷狗用 Hash，网易云用 songId，Musixmatch 用 trackId）。
/// </summary>
public sealed record SourceSearchResult(
    LyricsSourceKind Source,
    string SourceId,
    string Title,
    IReadOnlyList<string> Artists,
    string? Album,
    double? DurationSec);

/// <summary>
/// 搜索结果调用结果（防腐层坑 #3：异常必须转换为带源标识的结果对象，禁止静默表现为"无结果"）。
/// Success=false 时 ErrorMessage 携带源标识与原因；引擎据此顺延下一源，不中断整条链路。
/// </summary>
public sealed record SourceSearchOutcome(
    bool Success,
    IReadOnlyList<SourceSearchResult>? Results,
    string? ErrorMessage);

/// <summary>
/// 歌词原文获取结果（两段式第二段）。<see cref="Format"/> 随原文显式声明（坑 #1），
/// 失败时 Format=Unknown 且 ErrorMessage 带源标识（坑 #3）。
/// </summary>
public sealed record SourceLyricsResult(
    bool Success,
    LyricsRawFormat Format,
    string? RawText,
    string? ErrorMessage);

/// <summary>
/// 五源适配器统一契约（TDD §3.6 / ADR-9：QQ音乐 → 酷狗 → Apple Music → 网易云 → Musixmatch，汽水已移出）。
///
/// 两段式：<see cref="SearchAsync"/> 拿搜索结果（源内 ID + 元数据）→ 引擎打分选定 → 取词。
///
/// 实现约束（防腐层四坑 1/3/4）：
/// 1. 解析必须显式传歌词类型 → 取词结果携带 <see cref="LyricsRawFormat"/>；
/// 3. 同源调用串行化 + 异常转换为带源标识的结果对象，禁止静默失败；
/// 4. 只允许调用 Helper 的 Async 版本（同步方法内部用 .Result 阻塞，UI 线程有死锁风险）。
/// 本接口是 Engine 与 External 的唯一边界，接口内不允许出现 Helper 类型。
/// </summary>
public interface ISourceSearcher
{
    LyricsSourceKind Source { get; }

    /// <summary>按清洗后曲目搜索。失败不抛异常（取消除外），以 <see cref="SourceSearchOutcome"/> 返回。</summary>
    Task<SourceSearchOutcome> SearchAsync(CleanedTrack track, double durationSec, CancellationToken ct = default);

    /// <summary>按搜索结果的 SourceId 获取歌词原文。失败不抛异常（取消除外）。</summary>
    Task<SourceLyricsResult> GetLyricsAsync(string sourceId, CancellationToken ct = default);
}
