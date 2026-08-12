using YTM.Lyrics.Core.Cache;
using YTM.Lyrics.Core.Cleaning;

namespace YTM.Lyrics.Core.Tests.Cache;

/// <summary>
/// CacheKey.Compute(CleanedTrack, double) 转发重载单测（TASK-203 补齐 TDD §3.5 预留口子）：
/// 主艺人取 ArtistCandidates[0]，与三参版一致；无艺人候选按空串。
/// </summary>
public class CacheKeyCleanedTrackTests
{
    [Fact]
    public void 转发重载_主艺人取候选首位_与三参版同键()
    {
        var track = new CleanedTrack("Hello World", new[] { "Primary Artist", "Feat Artist" });
        Assert.Equal(CacheKey.Compute("Hello World", "Primary Artist", 234.0), CacheKey.Compute(track, 234.0));
    }

    [Fact]
    public void 转发重载_无艺人候选_按空串()
    {
        var track = new CleanedTrack("Instrumental Piece", Array.Empty<string>());
        Assert.Equal(CacheKey.Compute("Instrumental Piece", "", 234.0), CacheKey.Compute(track, 234.0));
    }

    [Fact]
    public void 转发重载_与三参版语义一致_时长归并同桶()
    {
        var a = new CleanedTrack("Song", new[] { "Artist" });
        var b = new CleanedTrack("Song", new[] { "Artist" });
        Assert.Equal(CacheKey.Compute(a, 233.9), CacheKey.Compute(b, 234.1)); // 差 0.2s 同桶
    }

    [Fact]
    public void 转发重载_null入参_抛异常()
    {
        Assert.Throws<ArgumentNullException>(() => CacheKey.Compute(null!, 100));
    }
}
