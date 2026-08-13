using System.Diagnostics;
using LiteDB;
using YTM.Lyrics.Core.Engine;

namespace YTM.Lyrics.Core.Cache;

/// <summary>
/// LiteDB 落地（ADR-5 冻结：纯托管嵌入式、单文件零依赖）。
///
/// 集合设计（任务卡约定）：
/// - lyrics   ：正文缓存，_id = CacheKey（唯一）。StoreAsync 自动条目 / BindUserChoiceAsync 绑定条目。
/// - negative ：负缓存，_id = CacheKey，带 StoredAt；TTL 24h 判定在读取侧（不依赖存储层过期机制）。
/// - settings ：预留（未来设置项），现仅写 schema_version 标记。
///
/// 优先级语义（PRD F6「用户绑定高于一切自动结果，永久有效」）：
/// - 同键永远只有一条正文：绑定覆盖自动；自动条目不覆盖已存在的绑定条目。
/// - 绑定/写入正文成功即清除同键负缓存；同键存在绑定条目时 IsNegativeCachedAsync 恒 false（负缓存对绑定无效）。
///
/// 线程安全：所有 DB 操作经内部信号量串行化（LiteDB 单连接 + 单写者模型），并发读写安全。
/// 使用约束：同一库文件单进程只能开一个 LiteDatabase 实例（LiteDB 文件锁），本类持有一个连接，
/// 生命周期与进程一致即可；默认路径 %AppData%\YTM.Lyrics\lyrics.db，测试注入临时目录。
/// </summary>
public sealed class LiteDbLyricsCache : ILyricsCache, IDisposable
{
    public const string LyricsCollection = "lyrics";
    public const string NegativeCollection = "negative";
    public const string SettingsCollection = "settings";

    /// <summary>负缓存 TTL（任务卡：24h，判定在读取侧）。</summary>
    public static readonly TimeSpan NegativeTtl = TimeSpan.FromHours(24);

    /// <summary>默认库文件路径：%AppData%\YTM.Lyrics\lyrics.db（可注入覆盖，测试用临时目录）。</summary>
    public static string DefaultDatabasePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "YTM.Lyrics",
        "lyrics.db");

    private readonly LiteDatabase _db;
    private readonly Func<DateTimeOffset> _clock;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ILiteCollection<LyricsDoc> _lyrics;
    private readonly ILiteCollection<NegativeDoc> _negative;
    private bool _disposed;

    /// <param name="databasePath">库文件路径（目录不存在会自动创建）。</param>
    /// <param name="clock">时钟注入（测试用），默认 UTC 当前时间；负缓存 TTL 判定依赖它。</param>
    public LiteDbLyricsCache(string databasePath, Func<DateTimeOffset>? clock = null)
    {
        var fullPath = Path.GetFullPath(databasePath);
        var dir = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _db = new LiteDatabase(new ConnectionString { Filename = fullPath });
        _lyrics = _db.GetCollection<LyricsDoc>(LyricsCollection);
        _negative = _db.GetCollection<NegativeDoc>(NegativeCollection);

        // settings 预留：未来设置项（默认源优先级/端口/偏移等）落此集合。
        // 现仅写入 schema_version 标记（幂等），为将来迁移提供版本判断。
        var settings = _db.GetCollection<BsonDocument>(SettingsCollection);
        if (settings.FindById("schema_version") is null)
        {
            settings.Insert(new BsonDocument { ["_id"] = "schema_version", ["value"] = 1 });
        }
    }

    /// <summary>正文缓存读取。绑定条目存在时永远优先返回（绑定覆盖自动、自动不覆盖绑定，故同键至多一条）。</summary>
    public Task<CachedLyrics?> TryGetAsync(string cacheKey) => ExecuteAsync(() =>
    {
        var doc = _lyrics.FindById(cacheKey);
        return doc is null ? null : ToEntry(doc);
    });

    /// <summary>负缓存判定：24h TTL，超时视同不存在；同键存在绑定条目时恒 false（负缓存对绑定无效）。</summary>
    public Task<bool> IsNegativeCachedAsync(string cacheKey) => ExecuteAsync(() =>
    {
        if (_lyrics.FindById(cacheKey) is { UserBound: true })
        {
            return false;
        }

        var neg = _negative.FindById(cacheKey);
        return neg is not null && _clock().UtcDateTime.Ticks - neg.StoredAtTicks < NegativeTtl.Ticks;
    });

    /// <summary>写入自动搜索结果。同键已存在用户绑定条目时忽略（绑定优先级高于一切自动结果，PRD F6）。</summary>
    public Task StoreAsync(CachedLyrics entry) => ExecuteAsync(() =>
    {
        if (_lyrics.FindById(entry.CacheKey) is { UserBound: true })
        {
            return; // 自动结果不得覆盖用户绑定
        }

        _lyrics.Upsert(ToDoc(entry, entry.CacheKey, userBound: false));
        _negative.Delete(entry.CacheKey); // 已找到歌词 → 作废旧负缓存
    });

    /// <summary>写入负缓存（记录当前时间，TTL 判定在读取侧）。</summary>
    public Task StoreNegativeAsync(string cacheKey) => ExecuteAsync(() =>
    {
        _negative.Upsert(new NegativeDoc { Id = cacheKey, StoredAtTicks = _clock().UtcDateTime.Ticks });
    });

    /// <summary>
    /// 写入用户绑定条目（F9）：永久有效，覆盖同键一切自动条目，并清除同键负缓存。
    /// 键以参数为准（覆盖 entry.CacheKey），UserBound 强制为 true，StoredAt 由存储层时钟覆盖。
    /// </summary>
    public Task BindUserChoiceAsync(string cacheKey, CachedLyrics entry) => ExecuteAsync(() =>
    {
        _lyrics.Upsert(ToDoc(entry, cacheKey, userBound: true));
        _negative.Delete(cacheKey);
    });

    /// <summary>
    /// F9 解除绑定：删除 lyrics 集合中该键文档（不区分 UserBound，绑定/自动一并删除）+ negative 集合中该键文档，
    /// 使下次自动搜索重新生效。LiteDB Delete 对不存在的键也不抛异常，但按任务卡要求防御性 try/catch
    /// （单集合操作失败不影响另一集合，异常只记录不向上传播——删除是尽力而为，失败后下次搜索仍会走到搜索链路）。
    /// </summary>
    public Task UnbindAsync(string cacheKey) => ExecuteAsync(() =>
    {
        try
        {
            _lyrics.Delete(cacheKey);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[Cache] UnbindAsync lyrics delete failed: key={cacheKey} {ex.GetType().Name}: {ex.Message}");
        }

        try
        {
            _negative.Delete(cacheKey);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[Cache] UnbindAsync negative delete failed: key={cacheKey} {ex.GetType().Name}: {ex.Message}");
        }
    });

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _db.Dispose();
        _gate.Dispose();
    }

    // ---- 内部 ----

    private async Task ExecuteAsync(Action action)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            action();
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<T> ExecuteAsync<T>(Func<T> func)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            return func();
        }
        finally
        {
            _gate.Release();
        }
    }

    private LyricsDoc ToDoc(CachedLyrics entry, string key, bool userBound) => new()
    {
        Id = key,
        RawText = entry.RawText,
        TranslationText = entry.TranslationText,
        Source = (int)entry.Source,
        UserBound = userBound,
        OffsetSec = entry.OffsetSec,
        StoredAtTicks = _clock().UtcDateTime.Ticks, // 存储层以时钟为准；UTC ticks 规避 LiteDB DateTime Kind 换算
    };

    private static CachedLyrics ToEntry(LyricsDoc doc) => new(
        doc.Id,
        doc.RawText,
        doc.TranslationText,
        (LyricsSourceKind)doc.Source,
        doc.UserBound,
        doc.OffsetSec,
        new DateTimeOffset(doc.StoredAtTicks, TimeSpan.Zero));

    private sealed class LyricsDoc
    {
        [BsonId]
        public string Id { get; set; } = "";

        public string RawText { get; set; } = "";

        /// <summary>TASK-504：翻译原文，随缓存持久化；LiteDB 对旧文档缺失字段自动返回 null，无需迁移。</summary>
        public string? TranslationText { get; set; }

        public int Source { get; set; }

        public bool UserBound { get; set; }

        public double OffsetSec { get; set; }

        public long StoredAtTicks { get; set; }
    }

    private sealed class NegativeDoc
    {
        [BsonId]
        public string Id { get; set; } = "";

        public long StoredAtTicks { get; set; }
    }
}
