using YTM.Lyrics.Core.Engine;

namespace YTM.Lyrics.Core.Cache;

/// <summary>
/// 缓存条目（TDD §3.5 冻结契约 + TASK-504 扩展 TranslationText）。
/// 正文缓存存原始歌词文本（不二次解析的源格式，PRD F6），来源、绑定标记、offset、命中时间随行。
/// </summary>
/// <param name="CacheKey">缓存键，见 <see cref="CacheKey.Compute"/>。</param>
/// <param name="RawText">原始歌词文本（源格式原文，读取侧另行解析）。</param>
/// <param name="TranslationText">翻译原文（TASK-504：随缓存往返，缓存命中后翻译仍恢复；
/// 格式同 <see cref="Engine.SourceLyricsResult.TranslationText"/> 承载约定；无翻译为 null）。</param>
/// <param name="Source">歌词来源。</param>
/// <param name="UserBound">是否用户手动绑定（F9）。绑定条目优先级高于一切自动结果，永久有效。</param>
/// <param name="OffsetSec">歌词偏移微调（±offset，F11 预留，随缓存持久化）。</param>
/// <param name="StoredAt">写入时间（命中时间），由存储层以时钟为准覆盖写入。</param>
public sealed record CachedLyrics(
    string CacheKey,
    string RawText,
    string? TranslationText,
    LyricsSourceKind Source,
    bool UserBound,
    double OffsetSec,
    DateTimeOffset StoredAt);
