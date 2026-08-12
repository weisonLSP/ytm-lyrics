using YTM.Lyrics.Core.Playback;

namespace YTM.Lyrics.Core.Tests.Playback;

/// <summary>
/// duration=0 延迟搜索决策器单测（TASK-205F 杂项 4）。
/// 覆盖：有效时长立即搜、无效时长进入等待、等待中无效快照继续等、
/// 首个有效时长快照放行、超时兜底放行、曲目不一致不提前放行、Reset 清态。
/// </summary>
public class TrackSearchDeferrerTests
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 12, 8, 0, 0, TimeSpan.Zero);

    private static TrackInfo Track(double durationSec, string title = "晴天", string artist = "周杰伦")
        => new(title, artist, durationSec);

    [Fact]
    public void duration有效_立即搜索()
    {
        var gate = new TrackSearchDeferrer();

        var decision = gate.OnTrackChanged(Track(269), T0);

        Assert.Equal(TrackSearchDecision.Immediate, decision);
    }

    [Fact]
    public void duration为零_进入等待()
    {
        var gate = new TrackSearchDeferrer();

        var decision = gate.OnTrackChanged(Track(0), T0);

        Assert.Equal(TrackSearchDecision.Wait, decision);
    }

    [Fact]
    public void duration为负_进入等待()
    {
        var gate = new TrackSearchDeferrer();

        var decision = gate.OnTrackChanged(Track(-1), T0);

        Assert.Equal(TrackSearchDecision.Wait, decision);
    }

    [Fact]
    public void 等待中快照时长仍无效_继续等待()
    {
        var gate = new TrackSearchDeferrer();
        gate.OnTrackChanged(Track(0), T0);

        var decision = gate.OnSnapshot(Track(0), T0 + TimeSpan.FromSeconds(1));

        Assert.Equal(TrackSearchDecision.Wait, decision);
    }

    [Fact]
    public void 等待中首个有效时长快照_放行()
    {
        var gate = new TrackSearchDeferrer();
        gate.OnTrackChanged(Track(0), T0);

        // 无效快照 → Wait；有效快照（同曲目）→ Ready
        Assert.Equal(TrackSearchDecision.Wait,
            gate.OnSnapshot(Track(0), T0 + TimeSpan.FromMilliseconds(500)));
        Assert.Equal(TrackSearchDecision.Ready,
            gate.OnSnapshot(Track(269), T0 + TimeSpan.FromMilliseconds(500)));
    }

    [Fact]
    public void 有效时长快照但曲目不同_不提前放行()
    {
        var gate = new TrackSearchDeferrer();
        gate.OnTrackChanged(Track(0, title: "晴天", artist: "周杰伦"), T0);

        // 另一首歌（换艺人）时长有效 → 不是本曲目的首个有效快照，继续等
        var decision = gate.OnSnapshot(Track(180, title: "晴天", artist: "林俊杰"), T0 + TimeSpan.FromSeconds(1));

        Assert.Equal(TrackSearchDecision.Wait, decision);
    }

    [Fact]
    public void 超时兜底_照常放行()
    {
        var gate = new TrackSearchDeferrer(timeout: TimeSpan.FromSeconds(3));
        gate.OnTrackChanged(Track(0), T0);

        // 3s 整（边界）→ Ready；即使快照时长仍无效
        var decision = gate.OnSnapshot(Track(0), T0 + TimeSpan.FromSeconds(3));

        Assert.Equal(TrackSearchDecision.Ready, decision);
    }

    [Fact]
    public void 超时前一刻_仍等待()
    {
        var gate = new TrackSearchDeferrer(timeout: TimeSpan.FromSeconds(3));
        gate.OnTrackChanged(Track(0), T0);

        var decision = gate.OnSnapshot(Track(0), T0 + TimeSpan.FromSeconds(2.999));

        Assert.Equal(TrackSearchDecision.Wait, decision);
    }

    [Fact]
    public void Ready后等待态已清_再次快照为Immediate()
    {
        var gate = new TrackSearchDeferrer(timeout: TimeSpan.FromSeconds(3));
        gate.OnTrackChanged(Track(0), T0);
        gate.OnSnapshot(Track(269), T0 + TimeSpan.FromSeconds(1)); // Ready（清态）

        var decision = gate.OnSnapshot(Track(269), T0 + TimeSpan.FromSeconds(1.1));

        Assert.Equal(TrackSearchDecision.Immediate, decision); // 未在等待 → 防御性 Immediate
    }

    [Fact]
    public void Reset后_恢复Immediate()
    {
        var gate = new TrackSearchDeferrer();
        gate.OnTrackChanged(Track(0), T0);

        gate.Reset();

        // 重置后 OnSnapshot（非等待态）→ Immediate；新曲目有效时长 → Immediate
        Assert.Equal(TrackSearchDecision.Immediate, gate.OnSnapshot(Track(269), T0 + TimeSpan.FromSeconds(2)));
        Assert.Equal(TrackSearchDecision.Immediate, gate.OnTrackChanged(Track(180), T0 + TimeSpan.FromSeconds(3)));
    }

    [Fact]
    public void 新曲目到达_覆盖旧等待态()
    {
        var gate = new TrackSearchDeferrer();
        gate.OnTrackChanged(Track(0, title: "旧歌"), T0);          // 旧歌等待中
        gate.OnTrackChanged(Track(0, title: "新歌"), T0 + TimeSpan.FromSeconds(1)); // 切歌，新歌也无效

        // 旧歌的有效时长快照不再放行（等待态已被新歌覆盖）
        var decision = gate.OnSnapshot(Track(269, title: "旧歌"), T0 + TimeSpan.FromSeconds(1.5));

        Assert.Equal(TrackSearchDecision.Wait, decision);
    }
}
