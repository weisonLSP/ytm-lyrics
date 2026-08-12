using YTM.Lyrics.Core.Playback;

namespace YTM.Lyrics.Core.Cleaning;

/// <summary>
/// 标题清洗结果：歌名单值 + 艺人候选列表（有序，最可能在前）。
/// 供 Engine（F4 搜索）与 CacheKey（F6 缓存键）消费。
/// </summary>
public sealed record CleanedTrack(string Title, IReadOnlyList<string> ArtistCandidates);

/// <summary>
/// F3 标题清洗器契约（TDD §3.2 冻结）。
/// 纯函数：单次 Clean 调用无副作用、不修改输入；规则表外置 JSON，支持热加载（规则改动无需重编译）。
/// </summary>
public interface ITitleCleaner
{
    CleanedTrack Clean(TrackInfo raw);

    /// <summary>
    /// 热加载规则表（TASK-402 Part D）：path 为空或 null → 回退内置默认规则；
    /// 否则从文件加载并（重新）监听该文件。解析失败抛异常，调用方负责保留旧规则。
    /// </summary>
    void LoadRules(string? path);
}
