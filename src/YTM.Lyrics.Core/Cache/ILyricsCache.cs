namespace YTM.Lyrics.Core.Cache;

/// <summary>
/// 歌词缓存读写契约（TDD §3.5 冻结）。
/// 语义约定（Engine §3.3 查询顺序：缓存命中 → 负缓存检查 → 逐源搜索 → 写缓存）：
/// - <see cref="TryGetAsync"/>：正文缓存命中；绑定条目永远优先（自动条目不覆盖绑定条目）。
/// - <see cref="IsNegativeCachedAsync"/>：负缓存 24h TTL，判定在读取侧（存储层不主动过期）。
/// - <see cref="StoreAsync"/>：写入自动结果；若同键已存在用户绑定条目则忽略（绑定优先，永久有效）。
/// - <see cref="BindUserChoiceAsync"/>：写入用户绑定条目，覆盖同键一切自动条目，并清除负缓存。
/// - 负缓存对绑定条目无效：同键存在绑定条目时 <see cref="IsNegativeCachedAsync"/> 恒返回 false。
/// - <see cref="UnbindAsync"/>：删除同键正文（含绑定）与负缓存条目，使下次自动搜索重新生效（F9 解除绑定）。
///   只删缓存条目，不触发 UI 刷新（刷新由调用方负责）。
/// </summary>
public interface ILyricsCache
{
    Task<CachedLyrics?> TryGetAsync(string cacheKey);

    Task<bool> IsNegativeCachedAsync(string cacheKey); // 24h TTL

    Task StoreAsync(CachedLyrics entry);

    Task StoreNegativeAsync(string cacheKey);

    Task BindUserChoiceAsync(string cacheKey, CachedLyrics entry); // 永久

    /// <summary>F9 解除绑定：删除同键正文缓存条目（不区分 UserBound）+ 负缓存条目，下次自动搜索生效。</summary>
    Task UnbindAsync(string cacheKey);
}
