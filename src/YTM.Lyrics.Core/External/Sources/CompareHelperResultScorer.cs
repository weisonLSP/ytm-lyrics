using Lyricify.Lyrics.Searchers.Helpers;
using YTM.Lyrics.Core.Cleaning;
using YTM.Lyrics.Core.Engine;

namespace YTM.Lyrics.Core.External.Sources;

/// <summary>
/// CompareHelper 打分器（TDD §3.3「用 Helper CompareHelper 能力按 api-map.md 定稿分值表」）。
/// Core 内唯一调用 CompareHelper 的位置；阈值判定不在此处（见 <see cref="MatchScoreTable"/>）。
///
/// 分值表定稿：
/// - 歌名/艺人维：CompareName / CompareArtist 的匹配等级 → 0..7 量表（Perfect=7 … NoMatch=0）；
/// - 时长维：先过 ±3s 容差门（frozen，超容差记 0 分；与 CacheKey 桶宽一致），
///   容差内按 CompareDuration 分级（Δ=0→7 / &lt;0.3s→6 / &lt;0.7s→5 / &lt;1.5s→4 / &lt;3s→2）；
/// - 时长缺失（null/0）按 0 分（CompareDuration 返回 null 的语义）。
///
/// ⚠ Helper 上游 bug（master c14bba9 实测）：`GetMatchScore(this X)` 非空重载为自递归
/// `return GetMatchScore(matchType);`（栈溢出）。必须**禁止用 `?.GetMatchScore()`**（空接会绑定
/// 到非空重载）；一律在可空值上用普通 `.GetMatchScore()` 绑定 nullable 重载（null→0），
/// 与 Helper 自身 CompareTrack 的调用方式一致。
/// </summary>
public sealed class CompareHelperResultScorer : ISourceResultScorer
{
    public SourceMatchScore Score(CleanedTrack track, double durationSec, SourceSearchResult candidate)
    {
        ArgumentNullException.ThrowIfNull(track);
        ArgumentNullException.ThrowIfNull(candidate);

        // 注意：可空值上直接用 .GetMatchScore()（nullable 重载，null→0）；禁止 ?.GetMatchScore()（Helper bug，见类注释）
        var title = CompareHelper.CompareName(track.Title, candidate.Title).GetMatchScore();
        var artist = CompareHelper.CompareArtist(track.ArtistCandidates, candidate.Artists).GetMatchScore();
        var duration = ScoreDuration(durationSec, candidate.DurationSec);
        return new SourceMatchScore(title, artist, duration);
    }

    private static int ScoreDuration(double trackDurationSec, double? candidateDurationSec)
    {
        if (candidateDurationSec is not double candidate)
        {
            return 0; // 时长缺失：无法比对，按 0 分
        }

        if (Math.Abs(candidate - trackDurationSec) > MatchScoreTable.DurationToleranceSec)
        {
            return 0; // frozen ±3s 容差门：超容差时长维记 0（CompareDuration 的 <3.5s Low 档比 frozen 门更宽，以 frozen 为准）
        }

        return CompareHelper.CompareDuration(
            (int)Math.Round(trackDurationSec * 1000),
            (int)Math.Round(candidate * 1000)).GetMatchScore();
    }
}
