using System.Net.Http;
using System.Text.Json;
using Lyricify.Lyrics.Providers.Web.QQMusic;
using YTM.Lyrics.Core.Cleaning;
using YTM.Lyrics.Core.Engine;

namespace YTM.Lyrics.Core.External.Sources;

/// <summary>
/// QQ 音乐源适配器（ADR-9 首选源）。
///
/// TASK-205F 搜索接口修复（真机"搜索无数据"根因）：Helper 的 <c>QQMusic.Api.Search</c> 走
/// <c>musicu.fcg</c> 的 <c>music.search.SearchCgiService.DoSearchForQQMusicDesktop</c> 模块，
/// 该模块 2026-08-12 实测已模块级失效（Python/OpenSSL 真实请求取证：所有 query 返回
/// <c>req_1.code=2001</c> / <c>ein=20</c> / <c>sum=0</c> / <c>song.list=[]</c>，加 comm 块仅让
/// code 归零、列表仍空）。适配器改用**稳定可用的老接口**（同为 QQ 官方，无需登录态）：
///   GET https://c.y.qq.com/soso/fcgi-bin/client_search_cp?w=&lt;keyword&gt;&amp;t=0&amp;p=1&amp;n=20&amp;format=json
///   实测「晴天 周杰伦」返回 20 条（songid/songmid/songname/singer[]/interval/albumname 齐全）。
///
/// - 取词：QQMusic.Api.GetLyricsAsync(id)（lyric_download.fcg version=15，实测仍可用，Api 内部完成
///   QRC 解密，解密失败（FormatException）时降级按 LRC 文本返回）→ 显式判定原文格式（坑 #1）；
/// - SourceId = client_search_cp 的 songid（与取词接口 musicid 同键，实测 GET/POST 均通）；
///   老歌 songid 缺失/0 时回退 songmid；
/// - 解析用 System.Text.Json（BCL 内置，响应为纯 JSON，不引入 Helper 失效路径）；
/// - 自持 HttpClient + 基类信号量串行化：同源请求互斥，不与 Helper 全局 static HttpClient 混用（坑 #4）。
/// </summary>
public sealed class QqMusicSourceSearcher : SourceSearcherBase
{
    /// <summary>QQ 老搜索接口（GET，format=json）。t=0 单曲、p=1 首页、n=20 条。</summary>
    internal const string SearchUrlTemplate =
        "https://c.y.qq.com/soso/fcgi-bin/client_search_cp?w={0}&t=0&p=1&n=20&format=json&cr=1&aggr=1";

    private static readonly HttpClient _http = CreateHttpClient();
    private readonly Api _api = new();

    public override LyricsSourceKind Source => LyricsSourceKind.QQMusic;

    protected override async Task<SourceSearchOutcome> SearchCoreAsync(CleanedTrack track, double durationSec, CancellationToken ct)
    {
        var keyword = BuildKeyword(track);
        var url = BuildSearchUrl(keyword);

        string json;
        try
        {
            using var resp = await _http.GetAsync(url, ct).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new SourceSearchOutcome(false, null, $"[{Source}] 搜索请求失败: {DescribeHttpError(ex, Source)}");
        }

        var outcome = ParseSearchResponse(json, Source);
        if (!outcome.Success && string.IsNullOrEmpty(outcome.ErrorMessage))
        {
            return outcome with { ErrorMessage = $"[{Source}] 搜索无数据" };
        }

        return outcome;
    }

    /// <summary>构造 client_search_cp 搜索 URL（internal 供单测断言）。</summary>
    internal static string BuildSearchUrl(string keyword)
        => string.Format(System.Globalization.CultureInfo.InvariantCulture,
            SearchUrlTemplate, Uri.EscapeDataString(keyword));

    /// <summary>
    /// 解析 client_search_cp 响应（internal 供单测按真实样本 mock 验证）。
    /// 结构：data.song.list[]，每项 { songid:int, songmid, songname, singer:[{name}], albumname, interval:int 秒 }。
    /// </summary>
    internal static SourceSearchOutcome ParseSearchResponse(string json, LyricsSourceKind source)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new SourceSearchOutcome(false, null, null);
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            // 老接口无顶层错误码字段（非 musicu.fcg）；code!=0 视为失败
            if (root.TryGetProperty("code", out var code) && code.GetInt32() != 0)
            {
                return new SourceSearchOutcome(false, null, $"code={code.GetInt32()}");
            }

            if (!root.TryGetProperty("data", out var data)
                || !data.TryGetProperty("song", out var song)
                || !song.TryGetProperty("list", out var list)
                || list.ValueKind != JsonValueKind.Array)
            {
                return new SourceSearchOutcome(false, null, null);
            }

            var results = new List<SourceSearchResult>();
            foreach (var item in list.EnumerateArray())
            {
                var songId = ReadInt64String(item, "songid");
                var songMid = ReadString(item, "songmid");
                if (string.IsNullOrEmpty(songId) && string.IsNullOrEmpty(songMid))
                {
                    continue; // 无有效 ID 的条目不可取词，跳过
                }

                var sourceId = !string.IsNullOrEmpty(songId) ? songId! : songMid!;
                results.Add(new SourceSearchResult(
                    source,
                    sourceId,
                    ReadString(item, "songname") ?? string.Empty,
                    ReadSingerNames(item),
                    ReadString(item, "albumname"),
                    ReadInt32AsDouble(item, "interval")));
            }

            // 无有效条目 → 失败（Success=false 时 Results 为 null，契约对齐）
            return results.Count > 0
                ? new SourceSearchOutcome(true, results, null)
                : new SourceSearchOutcome(false, null, null);
        }
        catch (JsonException ex)
        {
            return new SourceSearchOutcome(false, null, $"响应解析失败: {ex.Message}");
        }
    }

    protected override async Task<SourceLyricsResult> GetLyricsCoreAsync(string sourceId, CancellationToken ct)
    {
        var response = await _api.GetLyricsAsync(sourceId).ConfigureAwait(false);
        if (response is null || string.IsNullOrWhiteSpace(response.Lyrics))
        {
            return new SourceLyricsResult(false, LyricsRawFormat.Unknown, null, $"[{Source}] 歌词为空或接口无返回");
        }

        var text = response.Lyrics;
        return new SourceLyricsResult(true, SniffQqFormat(text), text, null);
    }

    /// <summary>
    /// 显式判定 QQ 原文格式（坑 #1：TypeHelper 是占位实现，禁止依赖自动探测）。
    /// QRC 行样式含 `文字(start,duration)` 音节标记（毫秒）；LRC 为 `[mm:ss.xx]文字`（QQ 解密失败降级路径）。
    /// 启发式：任一行含 "(" + 数字 + "," + 数字 → QRC，否则 LRC。
    /// 与解析缝的 <see cref="LyricsDocumentParserBridge.SniffQrcOrLrc"/> 同规则（本方法返回内部格式枚举）。
    /// </summary>
    private static LyricsRawFormat SniffQqFormat(string text)
    {
        foreach (var line in text.Split('\n'))
        {
            var paren = line.IndexOf('(');
            if (paren > 0 && paren + 1 < line.Length && char.IsAsciiDigit(line[paren + 1]))
            {
                var comma = line.IndexOf(',', paren);
                if (comma > paren && comma + 1 < line.Length && char.IsAsciiDigit(line[comma + 1]))
                {
                    return LyricsRawFormat.Qrc;
                }
            }
        }

        return LyricsRawFormat.Lrc;
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient(new HttpClientHandler { AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate })
        {
            Timeout = TimeSpan.FromSeconds(15),
        };
        // 与 Helper 同款 UA/Referer（老接口对 UA 敏感度低，保持一致减少风控差异）
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", Lyricify.Lyrics.Providers.Web.BaseApi.UserAgent);
        client.DefaultRequestHeaders.TryAddWithoutValidation("Referer", "https://c.y.qq.com/");
        return client;
    }

    private static string? ReadString(JsonElement item, string name)
        => item.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String
            ? el.GetString()
            : null;

    /// <summary>songid 为 int（部分老歌可能超 int32），读为十进制字符串；缺失/0 → null。</summary>
    private static string? ReadInt64String(JsonElement item, string name)
    {
        if (!item.TryGetProperty(name, out var el)) return null;
        if (el.ValueKind == JsonValueKind.Number && el.TryGetInt64(out var v) && v != 0)
        {
            return v.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        if (el.ValueKind == JsonValueKind.String && el.GetString() is { Length: > 0 } s && s != "0")
        {
            return s;
        }
        return null;
    }

    private static double? ReadInt32AsDouble(JsonElement item, string name)
        => item.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.Number
            && el.TryGetInt32(out var v) && v > 0
            ? v
            : null;

    private static IReadOnlyList<string> ReadSingerNames(JsonElement item)
    {
        if (!item.TryGetProperty("singer", out var singer) || singer.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<string>();
        }

        var names = new List<string>();
        foreach (var s in singer.EnumerateArray())
        {
            if (s.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String)
            {
                var n = name.GetString();
                if (!string.IsNullOrWhiteSpace(n)) names.Add(n!);
            }
        }

        return names;
    }
}
