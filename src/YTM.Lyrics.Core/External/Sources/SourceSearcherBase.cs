using System.Net.Http;
using YTM.Lyrics.Core.Cleaning;
using YTM.Lyrics.Core.Engine;

namespace YTM.Lyrics.Core.External.Sources;

/// <summary>
/// 源适配器基类：落实防腐层坑 #3/#4 的公共部分。
///
/// - 坑 #3（Searcher 静默吞异常）：本层捕获一切异常并转换为带源标识的结果对象
///   （<see cref="SourceSearchOutcome"/> / <see cref="SourceLyricsResult"/>），禁止静默表现为"无结果"；
/// - 坑 #4（全局静态 HttpClient 并发串头）：同源调用经信号量串行化
///   （引擎侧另有 500ms 节流，双保险；Musixmatch 的 token 刷新 / Apple 的 access token 生命周期
///   都要求同源请求互斥）；
/// - 坑 #8：只调用 Helper 的 Async 版本（同步版本内部 .Result 阻塞，UI 线程死锁红线）。
///
/// 坑 #1（解析必须显式传类型）由各实现取词时随原文声明 <see cref="LyricsRawFormat"/>。
/// </summary>
public abstract class SourceSearcherBase : ISourceSearcher
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public abstract LyricsSourceKind Source { get; }

    public async Task<SourceSearchOutcome> SearchAsync(CleanedTrack track, double durationSec, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await SearchCoreAsync(track, durationSec, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw; // 取消不是源故障
        }
        catch (Exception ex)
        {
            return new SourceSearchOutcome(false, null, $"[{Source}] 搜索失败: {DescribeHttpError(ex, Source)}");
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<SourceLyricsResult> GetLyricsAsync(string sourceId, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await GetLyricsCoreAsync(sourceId, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new SourceLyricsResult(false, LyricsRawFormat.Unknown, null,
                $"[{Source}] 取词失败: {DescribeHttpError(ex, Source)}");
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>源实现：搜索。只允许调用 Helper Async 版本。</summary>
    protected abstract Task<SourceSearchOutcome> SearchCoreAsync(CleanedTrack track, double durationSec, CancellationToken ct);

    /// <summary>源实现：取词。只允许调用 Helper Async 版本。</summary>
    protected abstract Task<SourceLyricsResult> GetLyricsCoreAsync(string sourceId, CancellationToken ct);

    /// <summary>通用搜索关键词 = 歌名 + 主艺人（YTM 清洗后结构）。</summary>
    protected static string BuildKeyword(CleanedTrack track)
    {
        var primary = track.ArtistCandidates.FirstOrDefault();
        return string.IsNullOrWhiteSpace(primary) ? track.Title : $"{track.Title} {primary}";
    }

    /// <summary>
    /// HTTP 层异常详情（TASK-205H，防腐层坑 #3 落地强化）：状态码 / 异常类型 / 目标域名必须进
    /// ErrorMessage——真机"全源静默失败"时，每源失败的真原因可直接从日志读出，不再黑盒。
    /// 编译环境注意：Helper 为 netstandard2.1 引用集，其 HttpRequestException 无 RequestMessage
    /// 属性（.NET 5+ 才加），域名统一用源已知域名（每源固定 API 域名，语义一致）。
    /// </summary>
    protected static string DescribeHttpError(Exception ex, LyricsSourceKind source)
    {
        if (ex is HttpRequestException http)
        {
            var host = KnownDomain(source);
            return http.StatusCode is { } code
                ? $"HttpRequestException status={(int)code} host={host}: {http.Message}"
                : $"HttpRequestException host={host}: {http.Message}";
        }

        return $"{ex.GetType().Name}: {ex.Message}";
    }

    /// <summary>各源已知 API 域名（ErrorMessage 兜底；Helper 吞掉请求上下文时仍有目标可查）。</summary>
    private static string KnownDomain(LyricsSourceKind source) => source switch
    {
        LyricsSourceKind.QQMusic => "c.y.qq.com",
        LyricsSourceKind.Kugou => "mobilecdn.kugou.com",
        LyricsSourceKind.AppleMusic => "amp-api.music.apple.com",
        LyricsSourceKind.Netease => "music.163.com",
        LyricsSourceKind.Musixmatch => "api.musixmatch.com",
        _ => "unknown",
    };
}
