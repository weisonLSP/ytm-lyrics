using LiteDB;
using YTM.Lyrics.Core.Cache;
using YTM.Lyrics.Core.Engine;

namespace YTM.Lyrics.Core.Tests.Cache;

/// <summary>
/// LiteDbLyricsCache 单测：存取往返、键稳定性、负缓存 TTL（注入时钟）、绑定覆盖优先级、
/// 绑定条目不受负缓存影响、重开库持久化、并发读写。
/// 每个测试方法独立临时目录 + 独立时钟（xunit 每方法新建本类实例）。
/// </summary>
public class LiteDbLyricsCacheTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 12, 0, 0, 0, TimeSpan.Zero);

    private readonly string _dir;
    private readonly string _dbPath;
    private DateTimeOffset _now;
    private LiteDbLyricsCache? _cache;

    public LiteDbLyricsCacheTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "ytm-cache-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _dbPath = Path.Combine(_dir, "lyrics.db");
        _now = T0;
    }

    /// <summary>新建缓存实例，时钟指向可变的 _now（TTL 测试推进时钟）。</summary>
    private LiteDbLyricsCache NewCache() => _cache = new LiteDbLyricsCache(_dbPath, () => _now);

    public void Dispose()
    {
        _cache?.Dispose();
        try
        {
            Directory.Delete(_dir, true);
        }
        catch
        {
            // 文件锁释放竞态：临时目录清理失败不影响测试结论
        }
    }

    private static CachedLyrics Entry(string key, string text, LyricsSourceKind source = LyricsSourceKind.QQMusic,
        bool userBound = false, double offsetSec = 0, string? translationText = null)
        => new(key, text, translationText, source, userBound, offsetSec, DateTimeOffset.MinValue);

    [Fact]
    public void 默认路径_指向AppData的YTM_Lyrics目录()
    {
        var p = LiteDbLyricsCache.DefaultDatabasePath;
        Assert.Equal("lyrics.db", Path.GetFileName(p));
        Assert.Contains("YTM.Lyrics", p);
        Assert.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), p);
    }

    [Fact]
    public async Task 存取往返_字段完整还原()
    {
        var cache = NewCache();
        var key = CacheKey.Compute("Song Title", "Artist", 234.0);

        Assert.Null(await cache.TryGetAsync(key)); // 未命中

        await cache.StoreAsync(Entry(key, "[00:00.00]lyric line", LyricsSourceKind.Netease, offsetSec: 1.5));

        var hit = await cache.TryGetAsync(key);
        Assert.NotNull(hit);
        Assert.Equal(key, hit.CacheKey);
        Assert.Equal("[00:00.00]lyric line", hit.RawText);
        Assert.Equal(LyricsSourceKind.Netease, hit.Source);
        Assert.False(hit.UserBound);
        Assert.Equal(1.5, hit.OffsetSec);
        Assert.Equal(_now, hit.StoredAt); // 存储层以时钟覆盖写入时间
    }

    [Fact]
    public async Task 覆盖写_同键后写生效()
    {
        var cache = NewCache();
        var key = CacheKey.Compute("T", "A", 100);

        await cache.StoreAsync(Entry(key, "v1"));
        await cache.StoreAsync(Entry(key, "v2", LyricsSourceKind.Kugou));

        var hit = await cache.TryGetAsync(key);
        Assert.Equal("v2", hit!.RawText);
        Assert.Equal(LyricsSourceKind.Kugou, hit.Source);
    }

    [Fact]
    public async Task 负缓存TTL_24小时内命中_超时失效()
    {
        var cache = NewCache();
        var key = CacheKey.Compute("T", "A", 100);

        Assert.False(await cache.IsNegativeCachedAsync(key)); // 未写过

        await cache.StoreNegativeAsync(key);
        Assert.True(await cache.IsNegativeCachedAsync(key)); // T0

        _now = T0.AddHours(23);
        Assert.True(await cache.IsNegativeCachedAsync(key)); // 23h 内命中

        _now = T0.AddHours(24);
        Assert.False(await cache.IsNegativeCachedAsync(key)); // 24h 整失效

        _now = T0.AddHours(25);
        Assert.False(await cache.IsNegativeCachedAsync(key)); // 超时失效
    }

    [Fact]
    public async Task 负缓存_同键不同键互不影响()
    {
        var cache = NewCache();
        var k1 = CacheKey.Compute("T1", "A", 100);
        var k2 = CacheKey.Compute("T2", "A", 100);

        await cache.StoreNegativeAsync(k1);
        Assert.True(await cache.IsNegativeCachedAsync(k1));
        Assert.False(await cache.IsNegativeCachedAsync(k2));
    }

    [Fact]
    public async Task 绑定覆盖优先级_自动结果不覆盖绑定()
    {
        var cache = NewCache();
        var key = CacheKey.Compute("T", "A", 100);

        await cache.StoreAsync(Entry(key, "auto"));
        Assert.Equal("auto", (await cache.TryGetAsync(key))!.RawText);

        await cache.BindUserChoiceAsync(key, Entry(key, "user-bound", LyricsSourceKind.Musixmatch));
        var hit = await cache.TryGetAsync(key);
        Assert.Equal("user-bound", hit!.RawText);
        Assert.True(hit.UserBound);
        Assert.Equal(LyricsSourceKind.Musixmatch, hit.Source);

        // 绑定之后再来自动结果：不得覆盖（用户绑定永久优先）
        await cache.StoreAsync(Entry(key, "auto-later"));
        hit = await cache.TryGetAsync(key);
        Assert.NotNull(hit);
        Assert.Equal("user-bound", hit.RawText);
        Assert.True(hit.UserBound);
    }

    [Fact]
    public async Task 负缓存对绑定条目无效()
    {
        var cache = NewCache();
        var key = CacheKey.Compute("T", "A", 100);

        // 先负缓存后绑定：绑定清除负缓存
        await cache.StoreNegativeAsync(key);
        Assert.True(await cache.IsNegativeCachedAsync(key));
        await cache.BindUserChoiceAsync(key, Entry(key, "bound"));
        Assert.False(await cache.IsNegativeCachedAsync(key));

        // 绑定后再写负缓存：不影响绑定条目
        await cache.StoreNegativeAsync(key);
        Assert.Equal("bound", (await cache.TryGetAsync(key))!.RawText);
        Assert.True((await cache.TryGetAsync(key))!.UserBound);
        Assert.False(await cache.IsNegativeCachedAsync(key));
    }

    [Fact]
    public async Task 正文写入_清除同键旧负缓存()
    {
        var cache = NewCache();
        var key = CacheKey.Compute("T", "A", 100);

        await cache.StoreNegativeAsync(key);
        Assert.True(await cache.IsNegativeCachedAsync(key));

        await cache.StoreAsync(Entry(key, "found"));
        Assert.False(await cache.IsNegativeCachedAsync(key)); // 找到歌词，负缓存作废
    }

    // ---------- F9 UnbindAsync ----------

    [Fact]
    public async Task UnbindAsync_删除绑定正文_下次自动搜索生效()
    {
        var cache = NewCache();
        var key = CacheKey.Compute("T", "A", 100);

        await cache.BindUserChoiceAsync(key, Entry(key, "bound", LyricsSourceKind.Kugou));
        Assert.NotNull(await cache.TryGetAsync(key));
        Assert.True((await cache.TryGetAsync(key))!.UserBound);

        await cache.UnbindAsync(key);

        Assert.Null(await cache.TryGetAsync(key)); // 正文（含绑定）删除 → TryGetAsync 返回 null（任务卡验收 ①）
        Assert.False(await cache.IsNegativeCachedAsync(key)); // 负缓存也删除 → 下次播放重新走自动搜索（任务卡验收 ②）
    }

    [Fact]
    public async Task UnbindAsync_删除自动正文与负缓存()
    {
        var cache = NewCache();
        var key = CacheKey.Compute("T", "A", 100);

        await cache.StoreAsync(Entry(key, "auto"));
        await cache.StoreNegativeAsync(key);
        Assert.NotNull(await cache.TryGetAsync(key));
        Assert.True(await cache.IsNegativeCachedAsync(key));

        await cache.UnbindAsync(key);

        Assert.Null(await cache.TryGetAsync(key));
        Assert.False(await cache.IsNegativeCachedAsync(key));
    }

    [Fact]
    public async Task UnbindAsync_键不存在_不抛异常()
    {
        var cache = NewCache();

        await cache.UnbindAsync("no-such-key"); // 防御性 try/catch：删除不存在键不炸
        Assert.Null(await cache.TryGetAsync("no-such-key"));
    }

    [Fact]
    public async Task UnbindAsync_不影响其他键()
    {
        var cache = NewCache();
        var k1 = CacheKey.Compute("T1", "A", 100);
        var k2 = CacheKey.Compute("T2", "A", 100);

        await cache.StoreAsync(Entry(k1, "keep"));
        await cache.BindUserChoiceAsync(k2, Entry(k2, "keep2"));

        await cache.UnbindAsync(k1);

        Assert.Null(await cache.TryGetAsync(k1));
        Assert.Equal("keep2", (await cache.TryGetAsync(k2))!.RawText); // 其他键不受影响
        Assert.True((await cache.TryGetAsync(k2))!.UserBound);
    }

    [Fact]
    public async Task 重开库_正文与绑定持久化()
    {
        var key = CacheKey.Compute("T", "A", 100);
        var autoKey = CacheKey.Compute("T2", "A", 100);
        var negKey = CacheKey.Compute("T3", "A", 100);

        var c1 = new LiteDbLyricsCache(_dbPath, () => _now);
        await c1.StoreAsync(Entry(autoKey, "auto"));
        await c1.BindUserChoiceAsync(key, Entry(key, "bound"));
        await c1.StoreNegativeAsync(negKey);
        c1.Dispose();

        var c2 = NewCache(); // 同路径重开（时钟相同，负缓存未超时）
        Assert.Equal("bound", (await c2.TryGetAsync(key))!.RawText);
        Assert.True((await c2.TryGetAsync(key))!.UserBound);
        Assert.Equal("auto", (await c2.TryGetAsync(autoKey))!.RawText);
        Assert.True(await c2.IsNegativeCachedAsync(negKey));
    }

    // ---------------- TASK-603：F11 歌词偏移（UpdateOffsetAsync） ----------------

    [Fact]
    public async Task UpdateOffsetAsync_更新自动条目偏移_其余字段不动()
    {
        var cache = NewCache();
        var key = CacheKey.Compute("Song", "Artist", 234.0);
        await cache.StoreAsync(Entry(key, "auto-raw", offsetSec: 1.5));

        await cache.UpdateOffsetAsync(key, 8.0);

        var hit = await cache.TryGetAsync(key);
        Assert.NotNull(hit);
        Assert.Equal(8.0, hit!.OffsetSec);
        Assert.False(hit.UserBound);   // UserBound 不动
        Assert.Equal("auto-raw", hit.RawText); // 正文不动
    }

    [Fact]
    public async Task UpdateOffsetAsync_更新绑定条目偏移_UserBound保持true()
    {
        var cache = NewCache();
        var key = CacheKey.Compute("Song", "Artist", 234.0);
        await cache.BindUserChoiceAsync(key, Entry(key, "bound-raw", userBound: true, offsetSec: 0));

        await cache.UpdateOffsetAsync(key, -5.0);

        var hit = await cache.TryGetAsync(key);
        Assert.NotNull(hit);
        Assert.Equal(-5.0, hit!.OffsetSec);
        Assert.True(hit.UserBound);    // 绑定条目的 UserBound 语义保持（永久有效）
        Assert.Equal("bound-raw", hit.RawText);
    }

    [Fact]
    public async Task UpdateOffsetAsync_同键无条目_noop不抛()
    {
        var cache = NewCache();
        var key = CacheKey.Compute("Ghost", "Artist", 100);

        await cache.UpdateOffsetAsync(key, 5.0);

        Assert.Null(await cache.TryGetAsync(key)); // 未创建条目
    }

    [Fact]
    public async Task 并发读写_不炸且终态一致()
    {
        var cache = NewCache();
        const int Writers = 4;
        const int Readers = 4;
        const int Iterations = 40;

        var tasks = new List<Task>();
        for (var w = 0; w < Writers; w++)
        {
            var wi = w;
            tasks.Add(Task.Run(async () =>
            {
                for (var i = 0; i < Iterations; i++)
                {
                    // 两个共享键多写者竞争 + 各自独有键
                    await cache.StoreAsync(Entry($"shared-{wi % 2}", $"w{wi}-i{i}"));
                    if (i % 7 == 0)
                    {
                        await cache.StoreNegativeAsync($"neg-{wi}-{i}");
                    }
                    if (i % 13 == 0)
                    {
                        await cache.BindUserChoiceAsync($"bound-{wi}", Entry($"bound-{wi}", $"b{wi}-i{i}"));
                    }
                }
            }));
        }

        for (var r = 0; r < Readers; r++)
        {
            var ri = r;
            tasks.Add(Task.Run(async () =>
            {
                for (var i = 0; i < Iterations; i++)
                {
                    _ = await cache.TryGetAsync($"shared-{ri % 2}");
                    _ = await cache.TryGetAsync($"bound-{ri % Writers}");
                    _ = await cache.IsNegativeCachedAsync($"neg-{ri}-{i}");
                }
            }));
        }

        await Task.WhenAll(tasks); // 不抛异常即通过

        // 终态一致：共享键存在且为某写者最后写入的合法值；绑定键 UserBound=true
        var shared = await cache.TryGetAsync("shared-0");
        Assert.NotNull(shared);
        Assert.StartsWith("w", shared.RawText);
        var bound = await cache.TryGetAsync("bound-0");
        Assert.NotNull(bound);
        Assert.True(bound.UserBound);
        Assert.StartsWith("b", bound.RawText);
    }

    // ---------- TASK-504：TranslationText 随缓存往返 ----------

    [Fact]
    public async Task TranslationText_存储往返一致()
    {
        var cache = NewCache();
        var key = CacheKey.Compute("T", "A", 100);
        const string translation = "[00:01.00]译文\r\n";

        await cache.StoreAsync(Entry(key, "raw", translationText: translation));

        var hit = await cache.TryGetAsync(key);
        Assert.Equal("raw", hit!.RawText);
        // LiteDB BsonMapper.TrimWhitespace 默认 true（5.0.21 文档：serialize 时 .Trim() 字符串），
        // 尾随 \r\n 会被吃掉；译文内容不丢（LrcParser 按行解析不受影响）——断言内容一致而非字节一致
        Assert.NotNull(hit.TranslationText);
        Assert.Equal("[00:01.00]译文", hit.TranslationText!.TrimEnd());
        Assert.Contains("译文", hit.TranslationText);
    }

    [Fact]
    public async Task TranslationText_绑定条目同样往返()
    {
        var cache = NewCache();
        var key = CacheKey.Compute("T", "A", 100);
        const string translation = "[00:02.00]绑定译文\r\n";

        await cache.BindUserChoiceAsync(key, Entry(key, "bound-raw", translationText: translation));

        var hit = await cache.TryGetAsync(key);
        Assert.NotNull(hit!.TranslationText);
        Assert.Equal("[00:02.00]绑定译文", hit.TranslationText!.TrimEnd());
        Assert.True(hit.UserBound);
    }

    [Fact]
    public async Task TranslationText_旧文档缺失字段_读为null不炸()
    {
        // TASK-504：LiteDB 对旧文档缺失字段自动返回 null，无需迁移。
        // 模拟旧 schema 文档（无 TranslationText 字段）直接落库后读取。
        var key = CacheKey.Compute("T", "A", 100);
        using (var raw = new LiteDatabase(new ConnectionString { Filename = _dbPath }))
        {
            raw.GetCollection(LiteDbLyricsCache.LyricsCollection).Insert(new BsonDocument
            {
                ["_id"] = key,
                ["RawText"] = "old-raw",
                ["Source"] = (int)LyricsSourceKind.QQMusic,
                ["UserBound"] = false,
                ["OffsetSec"] = 0.0,
                ["StoredAtTicks"] = DateTimeOffset.UtcNow.UtcTicks,
            });
        }

        var cache = NewCache();
        var hit = await cache.TryGetAsync(key);
        Assert.NotNull(hit);
        Assert.Equal("old-raw", hit!.RawText);
        Assert.Null(hit.TranslationText); // 旧文档缺失字段 → null，不抛异常
    }
}
