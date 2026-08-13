using System.Text;
using YTM.Lyrics.Core.Cache;
using YTM.Lyrics.Core.Cleaning;
using YTM.Lyrics.Core.Engine;
using YTM.Lyrics.Core.External.Sources;
using YTM.Lyrics.Core.Playback;

// TASK-505 取证种子：直连真实 QQ 取一首有翻译的歌（Lemon 米津玄師，TASK-504 取证同款），
// 以「Test Song / Test Artist / 10s」的缓存键写入 LiteDB（含 TranslationText），
// 供 App --cache-db 缓存命中路径渲染带翻译副行的桌面歌词（App 侧零改动）。
// 用法：dotnet run --project tools/t505_seed -- <cacheDbPath> <sampleQrcPath> <sampleTransPath>
Console.OutputEncoding = Encoding.UTF8;

if (args.Length < 3)
{
    Console.WriteLine("usage: t505_seed <cacheDbPath> <sampleQrcPath> <sampleTransPath>");
    return;
}

var dbPath = Path.GetFullPath(args[0]);
var qrcOut = args[1];
var transOut = args[2];

// 1) 真实取词：QQ Lemon（与 t504 取证同款，QRC 主词 + Trans 翻译 LRC）
var searcher = new QqMusicSourceSearcher();
var search = await searcher.SearchAsync(new CleanedTrack("Lemon", new[] { "米津玄师" }), 0);
if (!search.Success || search.Results is not { Count: > 0 })
{
    Console.WriteLine($"搜索失败: {search.ErrorMessage}");
    return;
}

var lyrics = await searcher.GetLyricsAsync(search.Results[0].SourceId);
if (!lyrics.Success || string.IsNullOrWhiteSpace(lyrics.RawText))
{
    Console.WriteLine($"取词失败: {lyrics.ErrorMessage}");
    return;
}

Console.WriteLine($"[seed] source={lyrics.Format} rawLen={lyrics.RawText.Length} transLen={lyrics.TranslationText?.Length}");
File.WriteAllText(qrcOut, lyrics.RawText, new UTF8Encoding(false));
if (!string.IsNullOrWhiteSpace(lyrics.TranslationText))
{
    File.WriteAllText(transOut, lyrics.TranslationText, new UTF8Encoding(false));
}

// 2) 计算缓存键（与 App 侧一致：track A = Test Song / Test Artist / 10s，见 tools/t205_push.js）
var track = new TrackInfo("Test Song", "Test Artist", 10.0);
var cleaned = TitleCleaner.FromEmbeddedRules().Clean(track);
var key = CacheKey.Compute(cleaned, track.DurationSec);
Console.WriteLine($"[seed] cacheKey={key}");

// 3) 写入 LiteDB（含 TranslationText，供缓存命中后翻译随文档恢复）
if (File.Exists(dbPath)) File.Delete(dbPath);
using (var cache = new LiteDbLyricsCache(dbPath))
{
    await cache.StoreAsync(new CachedLyrics(
        key,
        lyrics.RawText,
        lyrics.TranslationText,
        LyricsSourceKind.QQMusic, // Lemon 来自 QQ 源
        UserBound: false,
        OffsetSec: 0,
        StoredAt: DateTimeOffset.UtcNow));
}

// 4) 自证：读回并解析，确认翻译行非空
using (var verify = new LiteDbLyricsCache(dbPath))
{
    var hit = await verify.TryGetAsync(key);
    if (hit is null) { Console.WriteLine("[seed] 验证失败：缓存未命中"); return; }
    Console.WriteLine($"[seed] 验证：TranslationText 非空={!string.IsNullOrWhiteSpace(hit.TranslationText)}");
    var parser = new YTM.Lyrics.Core.External.Sources.LyricsDocumentParserBridge();
    var doc = parser.Parse(hit.RawText, hit.Source, null, hit.TranslationText);
    var withTrans = doc.Lines.Count(l => !string.IsNullOrEmpty(l.Translation));
    Console.WriteLine($"[seed] 验证：总行={doc.Lines.Count} 含翻译行={withTrans}");
}

Console.WriteLine($"[seed] done: db={dbPath}");
