using YTM.Lyrics.Core.Cleaning;
using YTM.Lyrics.Core.Engine;
using YTM.Lyrics.Core.External.Sources;

namespace YTM.Lyrics.Core.Tests.External.Sources;

/// <summary>
/// CompareHelper 打分器单测（真实 Helper CompareHelper，纯内存计算，无网络）：
/// 锁定 TDD §3.3 分值表定稿 —— 歌名/艺人/时长三维 0..7 量表、时长 ±3s 容差门、
/// 时长缺失/无关候选记 0。阈值判定在引擎侧（LyricsEngineTests）。
/// </summary>
public class CompareHelperResultScorerTests
{
    private readonly CompareHelperResultScorer _scorer = new();

    private static CleanedTrack Track(string title, params string[] artists) => new(title, artists);

    private static SourceSearchResult Candidate(string title, string[]? artists = null, double? durationSec = null, string id = "id")
        => new(LyricsSourceKind.QQMusic, id, title, artists ?? new[] { "Artist" }, null, durationSec);

    [Fact]
    public void 三维完全一致_全Perfect_21分()
    {
        var score = _scorer.Score(
            Track("Shape of You", "Ed Sheeran"),
            233.0,
            Candidate("Shape of You", new[] { "Ed Sheeran" }, 233.0));

        Assert.Equal(7, score.TitleScore);
        Assert.Equal(7, score.ArtistScore);
        Assert.Equal(7, score.DurationScore);
        Assert.Equal(21, score.TotalScore);
    }

    [Fact]
    public void 歌名大小写_归一后Perfect()
    {
        var score = _scorer.Score(Track("Love Story", "Taylor Swift"), 100, Candidate("LOVE STORY", new[] { "Taylor Swift" }, 100));
        Assert.Equal(7, score.TitleScore); // CompareName 归一（ToSC/ToLower/Trim）后相等
    }

    [Fact]
    public void 歌名带feat后缀_接近而非Perfect_VeryHigh()
    {
        var score = _scorer.Score(Track("Perfect", "Ed Sheeran"), 100, Candidate("Perfect (feat. Beyoncé)", new[] { "Ed Sheeran" }));
        Assert.Equal(6, score.TitleScore); // SpecialCompare(feat) → VeryHigh
    }

    [Fact]
    public void 艺人部分匹配_单艺人vs双艺人_High()
    {
        var score = _scorer.Score(Track("Perfect", "Ed Sheeran"), 100, Candidate("Perfect", new[] { "Ed Sheeran", "Beyoncé" }));
        Assert.Equal(5, score.ArtistScore); // count=1, list1=1, list2=2 → High
    }

    [Fact]
    public void 艺人完全无关_0分()
    {
        var score = _scorer.Score(Track("A", "甲"), 100, Candidate("A", new[] { "Zzz" }));
        Assert.Equal(0, score.ArtistScore);
    }

    [Fact]
    public void 时长完全一致_Perfect()
    {
        var score = _scorer.Score(Track("T", "A"), 233.0, Candidate("T", new[] { "A" }, 233.0));
        Assert.Equal(7, score.DurationScore);
    }

    [Fact]
    public void 时长差2点9秒_容差内_按CompareDuration分级_Low2分()
    {
        var score = _scorer.Score(Track("T", "A"), 233.0, Candidate("T", new[] { "A" }, 230.1));
        Assert.Equal(2, score.DurationScore); // |Δ|=2.9s ≤ 3s，CompareDuration <3500ms → Low(2)
    }

    [Fact]
    public void 时长差3点1秒_超容差门_0分()
    {
        var score = _scorer.Score(Track("T", "A"), 233.0, Candidate("T", new[] { "A" }, 236.1));
        Assert.Equal(0, score.DurationScore); // |Δ|=3.1s > frozen ±3s 门 → 0（CompareDuration 的 <3.5s 档更宽，以 frozen 门为准）
    }

    [Fact]
    public void 时长缺失_0分()
    {
        var score = _scorer.Score(Track("T", "A"), 233.0, Candidate("T", new[] { "A" }, null));
        Assert.Equal(0, score.DurationScore);
    }

    [Fact]
    public void 完全无关候选_全0分()
    {
        var score = _scorer.Score(Track("Shape of You", "Ed Sheeran"), 233.0, Candidate("Random", new[] { "Someone" }, 300.0));
        Assert.Equal(0, score.TitleScore);
        Assert.Equal(0, score.ArtistScore);
        Assert.Equal(0, score.DurationScore);
    }
}
