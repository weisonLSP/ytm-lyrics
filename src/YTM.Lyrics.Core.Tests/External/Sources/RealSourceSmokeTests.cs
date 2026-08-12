using YTM.Lyrics.Core.Cleaning;
using YTM.Lyrics.Core.Engine;
using YTM.Lyrics.Core.External.Sources;

namespace YTM.Lyrics.Core.Tests.External.Sources;

/// <summary>
/// 真实源冒烟入口（TASK-203 卡片：单测全 mock 源；真实源冒烟并入 M2 用户走查，但必须留冒烟入口）。
///
/// 沙箱约束：Schannel TLS 不可用（ADR-11），Helper 的 HttpClient 在沙箱内出不了网，
/// 因此本类默认全部直接通过（不产生网络调用）。
///
/// 可出网环境运行（M2 走查）：
///   set YTM_LYRICS_SMOKE=1
///   dotnet test --filter "FullyQualifiedName~RealSourceSmoke" --no-restore
///
/// 断言为结构级（请求成功 + 有结果/有原文），不校验歌词内容正确性——内容正确性由 M2 S1 场景人工走查。
/// </summary>
public class RealSourceSmokeTests
{
    private const string EnvFlag = "YTM_LYRICS_SMOKE";

    private static bool SmokeEnabled => Environment.GetEnvironmentVariable(EnvFlag) == "1";

    private static readonly CleanedTrack KnownEnglishTrack = new("Shape of You", new[] { "Ed Sheeran" });

    [Fact]
    public async Task 冒烟_五源按ADR9顺序搜索_热门英文歌()
    {
        if (!SmokeEnabled) return;

        ISourceSearcher[] searchers =
        {
            new QqMusicSourceSearcher(),
            new KugouSourceSearcher(),
            new AppleMusicSourceSearcher(),
            new NeteaseSourceSearcher(),
            new MusixmatchSourceSearcher(),
        };

        foreach (var searcher in searchers)
        {
            var outcome = await searcher.SearchAsync(KnownEnglishTrack, 233.0);
            Assert.True(outcome.Success, $"[{searcher.Source}] 搜索应成功: {outcome.ErrorMessage}");
            Assert.NotNull(outcome.Results);
            Assert.NotEmpty(outcome.Results!);
        }
    }

    [Fact]
    public async Task 冒烟_各源取词_首个候选能取回原文()
    {
        if (!SmokeEnabled) return;

        ISourceSearcher[] searchers =
        {
            new QqMusicSourceSearcher(),
            new KugouSourceSearcher(),
            new AppleMusicSourceSearcher(),
            new NeteaseSourceSearcher(),
            new MusixmatchSourceSearcher(),
        };

        foreach (var searcher in searchers)
        {
            var search = await searcher.SearchAsync(KnownEnglishTrack, 233.0);
            if (!search.Success || search.Results is null || search.Results.Count == 0)
            {
                continue; // 搜索已单独冒烟，这里聚焦"取词链路能通"
            }

            var first = search.Results[0];
            var lyrics = await searcher.GetLyricsAsync(first.SourceId);
            Assert.True(lyrics.Success, $"[{searcher.Source}] 取词应成功: {lyrics.ErrorMessage}");
            Assert.False(string.IsNullOrWhiteSpace(lyrics.RawText), $"[{searcher.Source}] 原文不应为空");
            Assert.NotEqual(LyricsRawFormat.Unknown, lyrics.Format);
        }
    }
}
