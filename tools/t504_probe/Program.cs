using System.Text;
using Lyricify.Lyrics.Providers.Web.Musixmatch;
using Lyricify.Lyrics.Providers.Web.Netease;
using Lyricify.Lyrics.Providers.Web.QQMusic;
using YTM.Lyrics.Core.Cleaning;
using YTM.Lyrics.Core.Engine;
using YTM.Lyrics.Core.External.Sources;

// =====================================================================
// TASK-504 取证探针（直连真实源，2026-08-13）
// 取证点 1：QQ QqLyricsResponse.Trans 是带时间戳 LRC 还是纯文本
// 取证点 2：Musixmatch GetTranslations 译文字段（description/snippet?）、
//           matched_line 匹配的是 subtitle 行还是 richsync 行
// =====================================================================

Console.OutputEncoding = Encoding.UTF8;

// ---------- 取证点 1：QQ Trans ----------
Console.WriteLine("================ 取证点 1：QQ Trans 格式 ================");
var qqSearcher = new QqMusicSourceSearcher();
var qqApi = new Lyricify.Lyrics.Providers.Web.QQMusic.Api();
var qqTracks = new[]
{
    new CleanedTrack("晴天", new[] { "周杰伦" }),
    new CleanedTrack("起风了", new[] { "买辣椒也用券" }),
    new CleanedTrack("Lemon", new[] { "米津玄师" }),
    new CleanedTrack("Shape of You", new[] { "Ed Sheeran" }),
    new CleanedTrack("My Heart Will Go On", new[] { "Celine Dion" }),
    new CleanedTrack("樱花樱花想见你", new[] { "高野健一" }),
};

var qqFound = false;
foreach (var track in qqTracks)
{
    var search = await qqSearcher.SearchAsync(track, 0);
    if (!search.Success || search.Results is null || search.Results.Count == 0)
    {
        Console.WriteLine($"[QQ] 搜索失败: {track.Title} {search.ErrorMessage}");
        continue;
    }

    var first = search.Results[0];
    var resp = await qqApi.GetLyricsAsync(first.SourceId);
    if (resp is null)
    {
        Console.WriteLine($"[QQ] 取词无返回: {track.Title} id={first.SourceId}");
        continue;
    }

    Console.WriteLine($"[QQ] {track.Title} → '{first.Title}' id={first.SourceId} LyricsLen={resp.Lyrics?.Length} TransLen={resp.Trans?.Length}");
    if (!string.IsNullOrWhiteSpace(resp.Trans))
    {
        qqFound = true;
        Console.WriteLine("----- Lyrics 头 6 行 -----");
        foreach (var l in resp.Lyrics!.Split('\n').Take(6)) Console.WriteLine("  |" + l);
        Console.WriteLine("----- Trans 全文（前 30 行）-----");
        var transLines = resp.Trans.Split('\n').Where(l => !string.IsNullOrWhiteSpace(l)).Take(30).ToArray();
        foreach (var l in transLines) Console.WriteLine("  |" + l);
        Console.WriteLine($"----- Trans 行数={resp.Trans.Split('\n').Count(l => !string.IsNullOrWhiteSpace(l))} 首行含'['={transLines.FirstOrDefault()?.StartsWith('[') ?? false}");
        break;
    }
}
if (!qqFound) Console.WriteLine("[QQ] 上述候选均无 Trans（无翻译），需换歌单再取证");

// ---------- 取证点 2：Musixmatch ----------
Console.WriteLine();
Console.WriteLine("================ 取证点 2：Musixmatch GetTranslations ================");
try
{
    var mxSearcher = new MusixmatchSourceSearcher();
    var mxApi = new Lyricify.Lyrics.Providers.Web.Musixmatch.Api();
    var mxTracks = new[]
    {
        new CleanedTrack("Shape of You", new[] { "Ed Sheeran" }),
        new CleanedTrack("Lemon", new[] { "Kenshi Yonezu" }),
        new CleanedTrack("My Heart Will Go On", new[] { "Celine Dion" }),
        new CleanedTrack("Yesterday", new[] { "The Beatles" }),
    };

    foreach (var track in mxTracks)
    {
        var search = await mxSearcher.SearchAsync(track, 0);
        if (!search.Success || search.Results is null || search.Results.Count == 0)
        {
            Console.WriteLine($"[MX] 搜索失败: {track.Title} {search.ErrorMessage}");
            continue;
        }

        var first = search.Results[0];
        var trackId = first.SourceId;
        Console.WriteLine($"[MX] {track.Title} → '{first.Title}' trackId={trackId}");

        var raw = await mxApi.GetFullLyricsRaw(trackId);
    Console.WriteLine($"[MX] GetFullLyricsRaw len={raw?.Length}");
    if (raw is null) continue;

    // 打印 macro 响应结构头：找 subtitle_body / richsync 字段
    var rawHead = raw.Length > 4000 ? raw[..4000] : raw;
    var subtitleIdx = raw.IndexOf("\"subtitle_body\"", StringComparison.Ordinal);
    var richIdx = raw.IndexOf("richsync", StringComparison.OrdinalIgnoreCase);
    Console.WriteLine($"[MX] 响应含 subtitle_body@{subtitleIdx} richsync@{richIdx}");

    var trans = await mxApi.GetTranslations(trackId, "zh");
    if (trans?.Message?.Body?.TranslationsList is not { Length: > 0 } list)
    {
        Console.WriteLine($"[MX] GetTranslations 无返回/空列表（无 zh 翻译）");
        continue;
    }

        Console.WriteLine($"[MX] GetTranslations 返回 {list.Length} 条翻译");
        var sample = list[0].Translation;
        Console.WriteLine("----- 首条翻译字段值 -----");
        Console.WriteLine($"  language        = {sample.Language}");
        Console.WriteLine($"  selected_lang   = {sample.SelectedLanguage}");
        Console.WriteLine($"  language_from   = {sample.LanguageFrom}");
        Console.WriteLine($"  description     = '{sample.Description}'");
        Console.WriteLine($"  snippet         = '{sample.Snippet}'");
        Console.WriteLine($"  matched_line    = '{sample.MatchedLine}'");
        Console.WriteLine($"  subtitle_line   = '{sample.SubtitleMatchedLine}'");
        Console.WriteLine($"  position        = {sample.Position}");
        Console.WriteLine($"  index           = {sample.Index}");
        Console.WriteLine($"  commontrack_id  = {sample.CommontrackId}");
        Console.WriteLine($"  key             = {sample.Key}");
        Console.WriteLine();
        Console.WriteLine("----- 前 5 条 matched_line vs description -----");
        foreach (var item in list.Take(5))
        {
            Console.WriteLine($"  M:'{item.Translation.MatchedLine}'");
            Console.WriteLine($"  D:'{item.Translation.Description}'");
            Console.WriteLine($"  S:'{item.Translation.Snippet}'");
            Console.WriteLine();
        }
        break;
    }
}
catch (Exception ex)
{
    Console.WriteLine($"[MX] 取证中断（Token 门禁/接口失败，不影响其余取证）: {ex.GetType().Name}: {ex.Message}");
}

Console.WriteLine();
Console.WriteLine("================ 参考：网易云 Tlyric + credits 行取证 ================");
var neSearcher = new NeteaseSourceSearcher();
var neApi = new Lyricify.Lyrics.Providers.Web.Netease.Api();
var neCandidates = new[]
{
    ("晴天", new[] { "周杰伦" }),
    ("First Love", new[] { "宇多田光" }),
    ("One More Time One More Chance", new[] { "山崎将义" }),
    ("Merry Christmas Mr. Lawrence", new[] { "坂本龙一" }),
    ("夜曲", new[] { "周杰伦" }),
    ("七里香", new[] { "周杰伦" }),
};

foreach (var (title, artists) in neCandidates)
{
    var ne = await neSearcher.SearchAsync(new CleanedTrack(title, artists), 0);
    if (!ne.Success || ne.Results is null || ne.Results.Count == 0)
    {
        Console.WriteLine($"[NE] 搜索失败: {title} {ne.ErrorMessage}");
        continue;
    }

    var sid = ne.Results[0].SourceId;
    Lyricify.Lyrics.Providers.Web.Netease.LyricResult? lyric = null;
    try { lyric = await neApi.GetLyric(sid); } catch (Exception ex) { Console.WriteLine($"[NE] {title} GetLyric 异常: {ex.GetType().Name}: {ex.Message}"); }
    if (lyric is null) { Console.WriteLine($"[NE] {title} 无返回"); continue; }
    Console.WriteLine($"[NE] {title} id={sid} nolyric={lyric.Nolyric} uncollected={lyric.Uncollected} " +
                      $"LrcLen={lyric.Lrc?.Lyric?.Length} YrcLen={lyric.Yrc?.Lyric?.Length} TlyricLen={lyric.Tlyric?.Lyric?.Length}");

    if (!string.IsNullOrWhiteSpace(lyric.Lrc?.Lyric))
    {
        Console.WriteLine("----- Lrc 前 10 行 -----");
        foreach (var l in lyric.Lrc!.Lyric!.Split('\n').Where(x => !string.IsNullOrWhiteSpace(x)).Take(10))
            Console.WriteLine("  |" + l);
    }
    if (!string.IsNullOrWhiteSpace(lyric.Tlyric?.Lyric))
    {
        Console.WriteLine("----- Tlyric 前 6 行 -----");
        foreach (var l in lyric.Tlyric!.Lyric!.Split('\n').Where(x => !string.IsNullOrWhiteSpace(x)).Take(6))
            Console.WriteLine("  |" + l);
    }
    Console.WriteLine();
}

Console.WriteLine();
Console.WriteLine("================ TASK-504 验收：真实歌 LyricLine.Translation 非空 ================");

// 生产路径端到端：QqMusicSourceSearcher.GetLyricsAsync → LyricsDocumentParserBridge.Parse（含 TranslationMerger）
var bridge = new YTM.Lyrics.Core.External.Sources.LyricsDocumentParserBridge();
var qq2 = new QqMusicSourceSearcher();
var qqSearch2 = await qq2.SearchAsync(new CleanedTrack("Lemon", new[] { "米津玄师" }), 0);
if (qqSearch2.Success && qqSearch2.Results is { Count: > 0 })
{
    var qqLyrics = await qq2.GetLyricsAsync(qqSearch2.Results[0].SourceId);
    Console.WriteLine($"[验收-QQ] Lemon TranslationText 非空={!string.IsNullOrWhiteSpace(qqLyrics.TranslationText)}");
    var doc = bridge.Parse(qqLyrics.RawText!, LyricsSourceKind.QQMusic, qqLyrics.Format, qqLyrics.TranslationText);
    var withTrans = doc.Lines.Count(l => !string.IsNullOrEmpty(l.Translation));
    Console.WriteLine($"[验收-QQ] 总行={doc.Lines.Count} 含翻译行={withTrans}");
    foreach (var l in doc.Lines.Take(8))
        Console.WriteLine($"  [{l.Start:mm\\:ss\\.ff}] {l.Text}  → 翻译={(l.Translation is null ? "(无)" : l.Translation)}");
}

var ne2 = new NeteaseSourceSearcher();
var neSearch2 = await ne2.SearchAsync(new CleanedTrack("First Love", new[] { "宇多田光" }), 0);
if (neSearch2.Success && neSearch2.Results is { Count: > 0 })
{
    var neLyrics = await ne2.GetLyricsAsync(neSearch2.Results[0].SourceId);
    Console.WriteLine($"[验收-NE] First Love TranslationText 非空={!string.IsNullOrWhiteSpace(neLyrics.TranslationText)}");
    var doc = bridge.Parse(neLyrics.RawText!, LyricsSourceKind.Netease, neLyrics.Format, neLyrics.TranslationText);
    var withTrans = doc.Lines.Count(l => !string.IsNullOrEmpty(l.Translation));
    Console.WriteLine($"[验收-NE] 总行={doc.Lines.Count} 含翻译行={withTrans}");
    foreach (var l in doc.Lines.Take(6))
        Console.WriteLine($"  [{l.Start:mm\\:ss\\.ff}] {l.Text}  → 翻译={(l.Translation is null ? "(无)" : l.Translation)}");
}

Console.WriteLine("==== 探针结束 ====");
