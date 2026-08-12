using YTM.Lyrics.Core.Playback;

namespace YTM.Lyrics.Core.Tests.Playback;

/// <summary>PlaybackStateMachine 单测：外推、暂停冻结、漂移校正阈值、TrackChanged 三字段。</summary>
public class PlaybackStateMachineTests
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 12, 0, 0, 0, TimeSpan.Zero);

    private static TrackInfo Track(string title = "T", string artist = "A", double duration = 234.5)
        => new(title, artist, duration);

    private static PlaybackSnapshot Snap(TrackInfo track, double pos, bool playing, long seq, DateTimeOffset at)
        => new(track, pos, playing, seq, at, at);

    [Fact]
    public void 外推_playing时位置随时间累加()
    {
        var now = T0;
        var fake = new FakePlaybackProvider();
        var sm = new PlaybackStateMachine(fake, () => now);

        fake.Push(Snap(Track(), 10.0, true, 1, now));

        now = T0.AddSeconds(2);
        Assert.Equal(12.0, sm.GetInterpolatedPosition().TotalSeconds, 3);

        now = T0.AddSeconds(2.5);
        Assert.Equal(12.5, sm.GetInterpolatedPosition().TotalSeconds, 3);
    }

    [Fact]
    public void 外推_首帧前返回零()
    {
        var fake = new FakePlaybackProvider();
        var sm = new PlaybackStateMachine(fake, () => T0);

        Assert.Equal(TimeSpan.Zero, sm.GetInterpolatedPosition());
        Assert.Null(sm.Current);
    }

    [Fact]
    public void 暂停_位置冻结不随流逝累加()
    {
        var now = T0;
        var fake = new FakePlaybackProvider();
        var sm = new PlaybackStateMachine(fake, () => now);

        fake.Push(Snap(Track(), 10.0, true, 1, now));
        now = T0.AddSeconds(3);
        fake.Push(Snap(Track(), 13.0, false, 2, now)); // 暂停帧

        now = T0.AddSeconds(8); // 暂停 5s 后
        Assert.Equal(13.0, sm.GetInterpolatedPosition().TotalSeconds, 3);

        now = T0.AddSeconds(30);
        Assert.Equal(13.0, sm.GetInterpolatedPosition().TotalSeconds, 3);
    }

    [Fact]
    public void 暂停后恢复_外推从恢复帧重新开始()
    {
        var now = T0;
        var fake = new FakePlaybackProvider();
        var sm = new PlaybackStateMachine(fake, () => now);

        fake.Push(Snap(Track(), 10.0, true, 1, now));   // 播放
        now = T0.AddSeconds(3);
        fake.Push(Snap(Track(), 13.0, false, 2, now));  // 暂停
        now = T0.AddSeconds(8);
        fake.Push(Snap(Track(), 13.2, true, 3, now));   // 恢复

        now = T0.AddSeconds(10); // 恢复 2s 后
        Assert.Equal(15.2, sm.GetInterpolatedPosition().TotalSeconds, 3);
    }

    [Fact]
    public void 漂移校正_偏差079秒在阈值内保持外推连续()
    {
        var now = T0;
        var fake = new FakePlaybackProvider();
        var sm = new PlaybackStateMachine(fake, () => now);

        fake.Push(Snap(Track(), 10.0, true, 1, now));
        now = T0.AddSeconds(1); // 外推值 11.0
        // 快照值 = 11.0 - 0.79，偏差 0.79 ≤ 0.8：不接受快照值，位置连续
        fake.Push(Snap(Track(), 10.21, true, 2, now));

        Assert.Equal(11.0, sm.GetInterpolatedPosition().TotalSeconds, 3);
    }

    [Fact]
    public void 漂移校正_偏差081秒超过阈值接受快照值()
    {
        var now = T0;
        var fake = new FakePlaybackProvider();
        var sm = new PlaybackStateMachine(fake, () => now);

        fake.Push(Snap(Track(), 10.0, true, 1, now));
        now = T0.AddSeconds(1); // 外推值 11.0
        // 快照值 = 11.0 - 0.81，偏差 0.81 > 0.8：接受快照值（seek 校正）
        fake.Push(Snap(Track(), 10.19, true, 2, now));

        Assert.Equal(10.19, sm.GetInterpolatedPosition().TotalSeconds, 3);
    }

    [Fact]
    public void 漂移校正_大跳变后位置跟随新基准()
    {
        var now = T0;
        var fake = new FakePlaybackProvider();
        var sm = new PlaybackStateMachine(fake, () => now);

        fake.Push(Snap(Track(), 10.0, true, 1, now));
        now = T0.AddSeconds(2);
        fake.Push(Snap(Track(), 60.0, true, 2, now)); // seek 到 60s

        now = T0.AddSeconds(3);
        Assert.Equal(61.0, sm.GetInterpolatedPosition().TotalSeconds, 3);
    }

    [Fact]
    public void 断线_位置冻结于最后快照_重连后校正恢复()
    {
        var now = T0;
        var fake = new FakePlaybackProvider();
        var sm = new PlaybackStateMachine(fake, () => now);

        fake.Push(Snap(Track(), 10.0, true, 1, now));
        now = T0.AddSeconds(2);
        fake.Lose(); // 探针失联
        now = T0.AddSeconds(7); // 失联 5s
        Assert.Equal(10.0, sm.GetInterpolatedPosition().TotalSeconds, 3); // 冻结

        fake.Push(Snap(Track(), 17.0, true, 2, now)); // 重连后恢复（漂移 7s → 校正）
        now = T0.AddSeconds(8);
        Assert.Equal(18.0, sm.GetInterpolatedPosition().TotalSeconds, 3);
    }

    [Fact]
    public void TrackChanged_三字段任一变化均触发()
    {
        var fake = new FakePlaybackProvider();
        var sm = new PlaybackStateMachine(fake, () => T0);
        var trackChanged = 0;
        sm.TrackChanged += (_, _) => trackChanged++;

        fake.Push(Snap(Track(), 0, true, 1, T0)); // 首帧
        Assert.Equal(1, trackChanged);

        fake.Push(Snap(Track(), 5, true, 2, T0)); // 全同：不触发
        fake.Push(Snap(Track(), 6, true, 3, T0));
        Assert.Equal(1, trackChanged);

        fake.Push(Snap(Track(title: "新标题"), 7, true, 4, T0)); // 标题变
        Assert.Equal(2, trackChanged);

        fake.Push(Snap(Track(artist: "新艺人"), 8, true, 5, T0)); // 艺人变
        Assert.Equal(3, trackChanged);

        fake.Push(Snap(Track(duration: 300.0), 9, true, 6, T0)); // 时长变
        Assert.Equal(4, trackChanged);
    }

    [Fact]
    public void TrackChanged_首帧与播放状态切换触发正确事件()
    {
        var fake = new FakePlaybackProvider();
        var sm = new PlaybackStateMachine(fake, () => T0);
        var trackChanged = 0;
        var playChanged = 0;
        sm.TrackChanged += (_, _) => trackChanged++;
        sm.PlayStateChanged += (_, _) => playChanged++;

        fake.Push(Snap(Track(), 0, true, 1, T0)); // 首帧：两个事件都触发
        Assert.Equal(1, trackChanged);
        Assert.Equal(1, playChanged);

        fake.Push(Snap(Track(), 3, false, 2, T0)); // 暂停：仅 PlayStateChanged
        Assert.Equal(1, trackChanged);
        Assert.Equal(2, playChanged);

        fake.Push(Snap(Track(title: "切歌"), 0, false, 3, T0)); // 切歌且保持暂停：仅 TrackChanged
        Assert.Equal(2, trackChanged);
        Assert.Equal(2, playChanged);
    }

    // ---------- TASK-403 Part B：drift 校正过频诊断 ----------
    // 机制推导：偏差 = |外推值 − 快照值| = |Δ_new − Δ_base|，Δ = ArrivedAt − SentAt（传输延迟，
    // 含探针/桌面时钟差常数，两帧相减抵消）。±50ms 抖动 → 相邻帧偏差 ≤ 100ms << 0.8s 阈值，
    // 稳态播放理论上不可能因传输抖动误触发校正。以下测试用固定种子注入抖动序列验证该论断。

    [Fact]
    public void drift诊断_稳态播放正负50ms传输抖动_不误触发校正()
    {
        var rng = new Random(403); // 固定种子：抖动序列可复现
        const double heartbeatSec = 2.0;   // 探针稳态心跳（probe: HEARTBEAT_MS = 2000）
        const double jitterMs = 50.0;      // 传输延迟抖动 ±50ms
        const int frames = 60;             // 120s 稳态播放

        var now = T0;
        var fake = new FakePlaybackProvider();
        var sm = new PlaybackStateMachine(fake, () => now);

        var corrections = 0;   // |Push 前外推 − 快照值| > 0.8s 的次数（状态机将接受快照重置基准）
        var maxJumpOffset = 0.0; // 相邻帧显示位置步进相对 2s 心跳的最大偏移
        var posAtSent = 10.0 + (frames - 1) * heartbeatSec; // 期望最终真实位置

        for (var i = 0; i < frames; i++)
        {
            var sentAt = T0.AddSeconds(i * heartbeatSec);
            var pos = 10.0 + i * heartbeatSec; // 真实播放 1x：position 精确等于 sentAt 时刻的播放位置
            var jitter = (rng.NextDouble() * 2 - 1) * jitterMs; // 传输延迟 U(−50, +50)ms
            var arrivedAt = sentAt.AddMilliseconds(jitter);
            now = arrivedAt; // 桌面端时钟与帧到达时刻对齐

            var extrapolated = sm.GetInterpolatedPosition().TotalSeconds; // Push 前：基于旧基准的外推
            fake.Push(new PlaybackSnapshot(Track(), pos, true, i + 1, sentAt, arrivedAt));

            var display = sm.GetInterpolatedPosition().TotalSeconds;
            if (i > 0)
            {
                // 连续帧显示步进应≈2s；偏差只可能来自传输延迟差（≤100ms），超出 150ms 即位置跳变
                maxJumpOffset = Math.Max(maxJumpOffset, Math.Abs(display - (10.0 + (i - 1) * heartbeatSec) - heartbeatSec));
            }

            // 首帧不参与 drift 判定（状态机首帧直接置基准）；i>0 时 |外推−快照|>0.8 才可能触发校正
            if (i > 0 && Math.Abs(extrapolated - pos) > 0.8)
            {
                corrections++;
                Console.WriteLine($"DRIFT-TRIGGER frame={i} extrapolated={extrapolated:F4} pos={pos:F4} dev={Math.Abs(extrapolated - pos):F4} jitter={jitter:F3}");
            }
        }

        // 诊断结论 ①：稳态 ±50ms 抖动下校正触发次数必须为 0（若 > 0 则「过频」属实，需记录证据）
        Assert.Equal(0, corrections);
        // 诊断结论 ②：位置连续性——每帧步进与 2s 心跳的偏移 ≤ 150ms（传输延迟差上界 100ms + 余量）
        Assert.True(maxJumpOffset <= 0.15, $"maxJumpOffset={maxJumpOffset:F3}s");
        // 诊断结论 ③：全程无累积漂移，最终显示位置与真实位置偏差 ≤ 150ms
        var finalDisplay = sm.GetInterpolatedPosition().TotalSeconds;
        Assert.True(Math.Abs(finalDisplay - posAtSent) <= 0.15, $"final offset={Math.Abs(finalDisplay - posAtSent):F3}s");
    }

    [Fact]
    public void drift诊断_抖动场景下真seek仍能触发校正_不误杀()
    {
        var rng = new Random(403);
        const double heartbeatSec = 2.0;
        const double jitterMs = 50.0;
        const int frames = 10; // 20s 稳态播放

        var now = T0;
        var fake = new FakePlaybackProvider();
        var sm = new PlaybackStateMachine(fake, () => now);

        for (var i = 0; i < frames; i++)
        {
            var sentAt = T0.AddSeconds(i * heartbeatSec);
            var jitter = (rng.NextDouble() * 2 - 1) * jitterMs;
            now = sentAt.AddMilliseconds(jitter);
            fake.Push(new PlaybackSnapshot(Track(), 10.0 + i * heartbeatSec, true, i + 1, sentAt, now));
        }

        // 注入真实 seek：position 跳 +5s（20s 播放处从 30s 跳到 35s）
        var seekSentAt = T0.AddSeconds(frames * heartbeatSec);
        var seekJitter = (rng.NextDouble() * 2 - 1) * jitterMs;
        now = seekSentAt.AddMilliseconds(seekJitter);
        var extrapolatedBeforeSeek = sm.GetInterpolatedPosition().TotalSeconds;
        fake.Push(new PlaybackSnapshot(Track(), 35.0, true, frames + 1, seekSentAt, now));

        // 抖动不影响真校正：偏差 ≈ 5s > 0.8s，快照被接受，位置跳到 35s 并继续外推
        Assert.True(Math.Abs(extrapolatedBeforeSeek - 35.0) > 0.8, "seek 偏差应超阈值");
        now = now.AddSeconds(1);
        Assert.Equal(36.0, sm.GetInterpolatedPosition().TotalSeconds, 3);
    }

    [Fact]
    public void drift诊断_时钟差常数加抖动_常数被抵消不触发()
    {
        // 探针时钟与桌面端时钟不同步是常态（浏览器时钟可能快/慢），表现为 ArrivedAt − SentAt 带常数 C。
        // 偏差 = |Δ_i − Δ_k|，两帧相减时 C 抵消；本测试注入 C=+700ms 常数 + ±50ms 抖动验证。
        var rng = new Random(403);
        const double heartbeatSec = 2.0;
        const double clockOffsetMs = 700.0; // 探针时钟比桌面快 700ms（模拟 transport 负值场景）
        const double jitterMs = 50.0;
        const int frames = 30;

        var now = T0;
        var fake = new FakePlaybackProvider();
        var sm = new PlaybackStateMachine(fake, () => now);

        var corrections = 0;
        for (var i = 0; i < frames; i++)
        {
            var sentAt = T0.AddSeconds(i * heartbeatSec).AddMilliseconds(clockOffsetMs); // 探针时钟
            var pos = 10.0 + i * heartbeatSec;
            var jitter = (rng.NextDouble() * 2 - 1) * jitterMs;
            var arrivedAt = sentAt.AddMilliseconds(jitter - clockOffsetMs); // 桌面时钟 = 探针时钟 − C + 抖动
            now = arrivedAt;

            var extrapolated = sm.GetInterpolatedPosition().TotalSeconds;
            fake.Push(new PlaybackSnapshot(Track(), pos, true, i + 1, sentAt, arrivedAt));

            if (i > 0 && Math.Abs(extrapolated - pos) > 0.8)
            {
                corrections++;
            }
        }

        Assert.Equal(0, corrections); // 时钟差常数被两帧相减抵消，不触发
    }

    [Fact]
    public void drift诊断_抖动边界扫描_定位临界触发幅度()
    {
        // 回答「阈值是否太敏感」：确定性地构造最坏情况序列（相邻帧延迟差 = 2×jitterMs），
        // 精确验证「偏差 = 相邻帧传输延迟差」这一机制，并定位触发边界。
        // 理论：偏差 > 0.8s ⟺ 2×jitterMs > 800ms ⟺ jitterMs > 400ms。
        // 用确定性交替序列（+m, −m, +m, −m…）保证每对相邻帧延迟差都达到 2×jitterMs，
        // 不依赖随机抽样（随机序列在有限帧内可能抽不中极端组合，见首版诊断教训）。
        const double heartbeatSec = 2.0;
        const int frames = 200;
        var criticalJitterMs = -1.0;

        foreach (var jitterMs in new[] { 100.0, 300.0, 399.0, 400.0, 401.0, 500.0 })
        {
            var now = T0;
            var fake = new FakePlaybackProvider();
            var sm = new PlaybackStateMachine(fake, () => now);

            var triggered = false;
            var maxDev = 0.0;
            for (var i = 0; i < frames; i++)
            {
                var sentAt = T0.AddSeconds(i * heartbeatSec);
                var pos = 10.0 + i * heartbeatSec;
                var jitter = (i % 2 == 0) ? jitterMs : -jitterMs; // 相邻帧延迟差 = 2×jitterMs
                var arrivedAt = sentAt.AddMilliseconds(jitter);
                now = arrivedAt;

                var extrapolated = sm.GetInterpolatedPosition().TotalSeconds;
                fake.Push(new PlaybackSnapshot(Track(), pos, true, i + 1, sentAt, arrivedAt));

                if (i > 0)
                {
                    var dev = Math.Abs(extrapolated - pos);
                    maxDev = Math.Max(maxDev, dev);
                    if (dev > 0.8)
                    {
                        triggered = true;
                        break;
                    }
                }
            }

            Console.WriteLine($"DRIFT-SCAN jitter={jitterMs}ms maxDev={maxDev:F3}s triggered={triggered}");
            if (triggered && criticalJitterMs < 0)
            {
                criticalJitterMs = jitterMs;
            }
        }

        // 实测：100→0.200s / 300→0.600s / 399→0.798s / 400→0.800s / 401→0.802s / 500→1.000s，
        // 精确验证「偏差 = 2×jitterMs（相邻帧延迟差）」。触发临界落在 400ms：
        // 400ms 档偏差 0.8s 因浮点表示为 0.80000000000000004 而触发（严格 > 0.8），
        // 与理论边界（2×jitter > 0.8 ⟺ jitter > 400ms）一致。
        Assert.True(criticalJitterMs is >= 400 and <= 401,
            $"临界抖动幅度 = {criticalJitterMs}ms，期望落在 400~401ms（理论临界 400ms）");
    }
}
